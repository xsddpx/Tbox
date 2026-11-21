use std::{
    io::{self, BufRead, Read, Write},
    sync::{Mutex, OnceLock},
    time::{Duration, Instant},
};

use async_trait::async_trait;
use base::message_proto::{message, misc, Message, OptionMessage};
use hbb_common::{
    anyhow::{anyhow, ensure, Context, Result},
    config::{self, Config},
    tokio, Stream,
};
use serde_derive::{Deserialize, Serialize};
use tbox_remote::{ControllerSigner, HostAuthorization, HostLease, Invitation, SecureStream};
use uuid::Uuid;

static HOST: OnceLock<Host> = OnceLock::new();
static CONTROLLER: OnceLock<Controller> = OnceLock::new();
static LAST_READY: Mutex<Option<Instant>> = Mutex::new(None);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Network {
    rendezvous: String,
    rendezvous_key: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct HostBootstrap {
    network: Network,
    origin: String,
    device_id: Uuid,
    credential: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Controller {
    network: Network,
    device_id: Uuid,
    invitation: Invitation,
    #[serde(default)]
    force_relay: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct HostIdentity {
    peer_id: String,
    public_key: String,
}

struct Host {
    authorization: HostAuthorization,
    identity: HostIdentity,
}

#[derive(Deserialize)]
struct StartHost {
    ready: bool,
}

pub(crate) fn setup_name() {
    *config::APP_NAME.write().unwrap() = "TboxRemote".to_owned();
    std::env::remove_var("TBOX_REMOTE_VIEWER_PEER");
    std::env::remove_var("TBOX_REMOTE_VIEWER_RELAY");
}

pub(crate) fn start_host() -> Result<()> {
    let bootstrap: HostBootstrap = read_control()?;
    configure_network(&bootstrap.network)?;
    let host =
        HostAuthorization::new(&bootstrap.origin, bootstrap.device_id, bootstrap.credential)?;
    let (_, key) = Config::get_key_pair();
    ensure!(
        key.len() == 32,
        "Could not initialize Tbox remote host identity"
    );
    let identity = HostIdentity {
        peer_id: Config::get_id(),
        public_key: crate::encode64(key),
    };
    send_control(&identity)?;
    // The parent obtains local consent and signs the identity registration.
    let answer: StartHost = read_control()?;
    ensure!(answer.ready, "Tbox remote host registration was cancelled");
    HOST.set(Host {
        authorization: host,
        identity,
    })
    .map_err(|_| anyhow!("Tbox remote host already initialized"))?;
    watch_parent_pipe();
    crate::start_server(true, false);
    Ok(())
}

pub(crate) fn start_controller() -> Result<Vec<String>> {
    let controller: Controller = read_control()?;
    configure_network(&controller.network)?;
    let peer = &controller.invitation.transport.peer_id;
    ensure!(
        (6..=32).contains(&peer.len())
            && peer.as_bytes()[0].is_ascii_alphanumeric()
            && peer
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b"_-".contains(&byte)),
        "Invalid Tbox remote peer"
    );
    // The macOS runner does not forward the Rust startup argument vector to Dart.
    // The display target is public metadata. The ticket remains in Rust memory.
    std::env::set_var("TBOX_REMOTE_VIEWER_PEER", peer);
    if controller.force_relay {
        std::env::set_var("TBOX_REMOTE_VIEWER_RELAY", "1");
    }
    CONTROLLER
        .set(controller)
        .map_err(|_| anyhow!("Tbox controller already initialized"))?;
    Ok(Vec::new())
}

fn configure_network(network: &Network) -> Result<()> {
    ensure!(
        !network.rendezvous.is_empty()
            && network.rendezvous.len() <= 253
            && network
                .rendezvous
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b".-:".contains(&byte))
            && crate::decode64(&network.rendezvous_key).is_ok_and(|key| key.len() == 32),
        "Invalid Tbox rendezvous settings"
    );
    *config::EXE_RENDEZVOUS_SERVER.write().unwrap() = network.rendezvous.clone();
    config::OVERWRITE_SETTINGS
        .write()
        .unwrap()
        .insert("key".to_owned(), network.rendezvous_key.clone());
    Ok(())
}

pub(crate) fn registered() {
    let Ok(host) = host() else {
        return;
    };
    {
        let mut last = LAST_READY.lock().unwrap();
        let now = Instant::now();
        if last.is_some_and(|previous| now.duration_since(previous) < Duration::from_secs(8)) {
            return;
        }
        *last = Some(now);
    }
    tokio::spawn(async move {
        if let Err(error) = host.heartbeat().await {
            hbb_common::log::debug!("Tbox remote readiness failed: {error}");
        }
    });
}

pub(crate) fn host() -> Result<&'static HostAuthorization> {
    let host = HOST.get().context("Tbox remote host is disabled")?;
    let (_, key) = Config::get_key_pair();
    ensure!(
        Config::get_id() == host.identity.peer_id
            && crate::encode64(key) == host.identity.public_key,
        "Tbox remote host identity changed"
    );
    Ok(&host.authorization)
}

pub(crate) async fn authorize_host(
    host: &HostAuthorization,
    stream: &mut Stream,
) -> Result<HostLease> {
    host.authorize(&mut RustDeskStream(stream)).await
}

pub(crate) async fn authorize_controller(
    peer_id: &str,
    verified_key: Option<&[u8]>,
    stream: &mut Stream,
) -> Result<()> {
    let controller = CONTROLLER
        .get()
        .context("No Tbox remote connection was requested")?;
    ensure!(
        peer_id == controller.invitation.transport.peer_id,
        "Unexpected Tbox remote peer"
    );
    tbox_remote::authorize_controller(
        &mut RustDeskStream(stream),
        verified_key,
        &controller.invitation,
        controller.device_id,
        &StdioSigner,
    )
    .await?;
    stream.set_max_packet_length(usize::MAX);
    send_control(&serde_json::json!({ "connected": true }))?;
    watch_parent_pipe();
    Ok(())
}

fn watch_parent_pipe() {
    // When Tbox exits abruptly, the inherited input handle closes as well.
    std::thread::spawn(|| {
        let _ = io::stdin().read(&mut [0_u8; 1]);
        std::process::exit(0);
    });
}

pub(crate) fn controller_key(peer_id: &str) -> Result<&'static str> {
    let controller = CONTROLLER
        .get()
        .context("No Tbox remote connection was requested")?;
    ensure!(
        peer_id == controller.invitation.transport.peer_id,
        "Unexpected Tbox remote peer"
    );
    Ok(&controller.network.rendezvous_key)
}

pub(crate) fn allows_desktop_message(message: &Message) -> bool {
    match message.union.as_ref() {
        Some(
            message::Union::LoginRequest(_)
            | message::Union::TestDelay(_)
            | message::Union::MouseEvent(_)
            | message::Union::PointerDeviceEvent(_)
            | message::Union::KeyEvent(_),
        ) => true,
        Some(message::Union::Misc(misc)) => matches!(
            misc.union,
            Some(
                misc::Union::CloseReason(_)
                    | misc::Union::SwitchDisplay(_)
                    | misc::Union::CaptureDisplays(_)
                    | misc::Union::RefreshVideo(_)
                    | misc::Union::RefreshVideoDisplay(_)
                    | misc::Union::VideoReceived(_)
                    | misc::Union::Option(_)
                    | misc::Union::AutoAdjustFps(_)
                    | misc::Union::RequestCursorData(_)
            )
        ),
        _ => false,
    }
}

pub(crate) fn desktop_options(option: &OptionMessage) -> OptionMessage {
    OptionMessage {
        image_quality: option.image_quality,
        custom_image_quality: option.custom_image_quality,
        custom_fps: option.custom_fps,
        show_remote_cursor: option.show_remote_cursor,
        supported_decoding: option.supported_decoding.clone(),
        disable_keyboard: option.disable_keyboard,
        ..Default::default()
    }
}

struct RustDeskStream<'a>(&'a mut Stream);

#[async_trait]
impl SecureStream for RustDeskStream<'_> {
    fn encrypted(&self) -> bool {
        self.0.is_secured()
    }

    async fn send_packet(&mut self, bytes: Vec<u8>) -> Result<()> {
        self.0.send_bytes(bytes.into()).await
    }

    async fn receive_packet(&mut self, maximum: usize) -> Result<Vec<u8>> {
        self.0.set_max_packet_length(maximum);
        Ok(self
            .0
            .next()
            .await
            .context("Tbox remote peer disconnected")??
            .to_vec())
    }
}

struct StdioSigner;

#[derive(Serialize)]
struct SignChallenge {
    challenge: String,
}

#[derive(Deserialize)]
struct SignedChallenge {
    signature: String,
}

#[async_trait]
impl ControllerSigner for StdioSigner {
    async fn sign_challenge(&self, _host: Uuid, _ticket: &str, challenge: &str) -> Result<String> {
        let request = SignChallenge {
            challenge: challenge.to_owned(),
        };
        tokio::task::spawn_blocking(move || {
            send_control(&request)?;
            let answer: SignedChallenge = read_control()?;
            Ok(answer.signature)
        })
        .await?
    }
}

fn send_control(value: &impl serde::Serialize) -> Result<()> {
    let mut stdout = io::stdout().lock();
    stdout.write_all(b"TBOX_REMOTE ")?;
    serde_json::to_writer(&mut stdout, value)?;
    stdout.write_all(b"\n")?;
    stdout.flush()?;
    Ok(())
}

fn read_control<T: serde::de::DeserializeOwned>() -> Result<T> {
    const MAX_CONTROL_PACKET: u64 = 4096;
    let mut bytes = Vec::new();
    io::stdin()
        .lock()
        .take(MAX_CONTROL_PACKET + 1)
        .read_until(b'\n', &mut bytes)?;
    ensure!(
        bytes.len() <= MAX_CONTROL_PACKET as usize && bytes.last() == Some(&b'\n'),
        "Invalid Tbox remote startup message"
    );
    serde_json::from_slice(&bytes).context("Invalid Tbox remote startup message")
}
