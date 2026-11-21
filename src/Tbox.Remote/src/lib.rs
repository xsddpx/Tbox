use std::time::Duration;

use anyhow::{bail, ensure, Context, Result};
use async_trait::async_trait;
use base64::{engine::general_purpose, Engine};
use rand::RngCore;
use reqwest::{redirect::Policy, Client, Url};
use serde::{Deserialize, Serialize};
use tokio::time::{sleep_until, timeout, timeout_at, Instant};
use uuid::Uuid;

const PACKET_LIMIT: usize = 4096;
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(5);
const LEASE_DURATION: Duration = Duration::from_secs(30);
const RENEW_INTERVAL: Duration = Duration::from_secs(10);

// The RustDesk adapter supplies the status of the negotiated stream and the peer's
// verified identity from its key exchange. No ticket is sent on an unverified stream.
#[async_trait]
pub trait SecureStream: Send {
    fn encrypted(&self) -> bool;
    async fn send_packet(&mut self, bytes: Vec<u8>) -> Result<()>;
    async fn receive_packet(&mut self, maximum: usize) -> Result<Vec<u8>>;
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Invitation {
    pub session_id: Uuid,
    pub host_id: Uuid,
    pub transport: TransportIdentity,
    ticket: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TransportIdentity {
    pub peer_id: String,
    public_key: String,
}

#[derive(Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Packet {
    Challenge { nonce: String },
    Claim(Claim),
    Granted(Lease),
}

#[derive(Serialize, Deserialize)]
struct Claim {
    ticket: String,
    challenge: String,
    signature: String,
}

#[derive(Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct Lease {
    session_id: Uuid,
    controller_id: Uuid,
    host_id: Uuid,
}

#[async_trait]
pub trait ControllerSigner: Sync {
    async fn sign_challenge(&self, host_id: Uuid, ticket: &str, challenge: &str) -> Result<String>;
}

pub async fn authorize_controller(
    stream: &mut impl SecureStream,
    verified_key: Option<&[u8]>,
    invitation: &Invitation,
    controller_id: Uuid,
    signer: &impl ControllerSigner,
) -> Result<()> {
    let key = general_purpose::STANDARD
        .decode(&invitation.transport.public_key)
        .context("Invalid remote host key")?;
    ensure!(
        stream.encrypted() && key.len() == 32 && verified_key == Some(key.as_slice()),
        "Remote host identity could not be verified"
    );
    ensure!(
        valid_secret(&invitation.ticket)
            && !controller_id.is_nil()
            && !invitation.session_id.is_nil()
            && !invitation.host_id.is_nil()
            && invitation.host_id != controller_id,
        "Invalid remote invitation"
    );

    timeout(HANDSHAKE_TIMEOUT, async {
        let Packet::Challenge { nonce } = receive(stream).await? else {
            bail!("Expected a remote host challenge")
        };
        ensure!(valid_secret(&nonce), "Invalid remote host challenge");
        let signature = signer
            .sign_challenge(invitation.host_id, &invitation.ticket, &nonce)
            .await?;
        send(
            stream,
            Packet::Claim(Claim {
                ticket: invitation.ticket.clone(),
                challenge: nonce,
                signature,
            }),
        )
        .await?;
        let Packet::Granted(lease) = receive(stream).await? else {
            bail!("Remote host rejected the invitation")
        };
        ensure!(
            lease.session_id == invitation.session_id
                && lease.controller_id == controller_id
                && lease.host_id == invitation.host_id,
            "Remote host returned a different connection"
        );
        Ok(())
    })
    .await
    .context("Remote authorization timed out")?
}

#[derive(Clone)]
pub struct HostAuthorization {
    client: Client,
    origin: Url,
    device_id: Uuid,
    credential: String,
}

impl HostAuthorization {
    pub fn new(origin: &str, device_id: Uuid, credential: String) -> Result<Self> {
        let origin = Url::parse(origin).context("Invalid remote account origin")?;
        ensure!(
            origin.scheme() == "https"
                && origin.has_host()
                && origin.path() == "/"
                && origin.query().is_none()
                && origin.fragment().is_none()
                && origin.username().is_empty()
                && origin.password().is_none(),
            "Remote account origin must be an HTTPS root address"
        );
        ensure!(
            valid_secret(&credential) && !device_id.is_nil(),
            "Invalid remote host enrollment"
        );
        let client = Client::builder()
            .redirect(Policy::none())
            .timeout(REQUEST_TIMEOUT)
            .build()
            .context("Could not initialize the remote account connection")?;
        Ok(Self {
            client,
            origin,
            device_id,
            credential,
        })
    }

    // Call only while a locally enabled host is listening and registered with rendezvous.
    pub async fn heartbeat(&self) -> Result<()> {
        let response = self
            .request("/v1/remote/host/heartbeat")?
            .send()
            .await
            .context("Could not report remote host readiness")?;
        ensure!(response.status() == 204, "Remote host readiness rejected");
        Ok(())
    }

    pub async fn authorize(&self, stream: &mut impl SecureStream) -> Result<HostLease> {
        ensure!(
            stream.encrypted(),
            "Remote authorization requires encryption"
        );
        timeout(HANDSHAKE_TIMEOUT, async {
            let claim = receive_claim(stream).await?;
            let requested = Instant::now();
            let response = self
                .request("/v1/remote/host/claim")?
                .json(&claim)
                .send()
                .await
                .context("Could not redeem the remote invitation")?;
            let lease = read_lease(response).await?;
            ensure!(
                lease.host_id == self.device_id
                    && !lease.session_id.is_nil()
                    && !lease.controller_id.is_nil()
                    && lease.controller_id != lease.host_id,
                "Remote lease does not belong to this host"
            );
            let session_id = lease.session_id;
            let controller_id = lease.controller_id;
            send(stream, Packet::Granted(lease)).await?;
            Ok(HostLease {
                host: self.clone(),
                session_id,
                controller_id,
                requested,
            })
        })
        .await
        .context("Remote authorization timed out")?
    }

    fn request(&self, path: &str) -> Result<reqwest::RequestBuilder> {
        Ok(self
            .client
            .post(self.origin.join(path)?)
            .bearer_auth(&self.credential))
    }
}

pub struct HostLease {
    host: HostAuthorization,
    session_id: Uuid,
    controller_id: Uuid,
    requested: Instant,
}

impl HostLease {
    // The host races its desktop connection against this future. A request's start
    // bounds the lease locally even when the server response arrives late.
    pub async fn maintain(self) -> Result<()> {
        let path = format!("/v1/remote/host/sessions/{}/renew", self.session_id);
        let mut requested = self.requested;
        loop {
            sleep_until(requested + RENEW_INTERVAL).await;
            let deadline = requested + LEASE_DURATION;
            let next_request = Instant::now();
            ensure!(next_request < deadline, "Remote lease expired");
            let lease = timeout_at(deadline, async {
                let response = self.host.request(&path)?.send().await?;
                read_lease(response).await
            })
            .await
            .context("Remote lease expired")??;
            ensure!(
                lease.session_id == self.session_id
                    && lease.host_id == self.host.device_id
                    && lease.controller_id == self.controller_id,
                "Remote lease changed identities"
            );
            requested = next_request;
        }
    }
}

async fn receive_claim(stream: &mut impl SecureStream) -> Result<Claim> {
    let mut random = [0_u8; 32];
    rand::rngs::OsRng.try_fill_bytes(&mut random)?;
    let nonce = general_purpose::URL_SAFE_NO_PAD.encode(random);
    send(
        stream,
        Packet::Challenge {
            nonce: nonce.clone(),
        },
    )
    .await?;
    let Packet::Claim(claim) = receive(stream).await? else {
        bail!("Expected a remote invitation")
    };
    ensure!(
        claim.challenge == nonce
            && valid_secret(&claim.ticket)
            && general_purpose::STANDARD
                .decode(&claim.signature)
                .is_ok_and(|bytes| bytes.len() == 64),
        "Invalid remote invitation proof"
    );
    Ok(claim)
}

async fn read_lease(mut response: reqwest::Response) -> Result<Lease> {
    ensure!(
        response.status() == 200,
        "Remote invitation or lease rejected"
    );
    let mut body = Vec::new();
    while let Some(bytes) = response.chunk().await? {
        ensure!(
            body.len() + bytes.len() <= PACKET_LIMIT,
            "Remote lease response is too large"
        );
        body.extend_from_slice(&bytes);
    }
    serde_json::from_slice(&body).context("Invalid remote lease response")
}

fn valid_secret(input: &str) -> bool {
    input.len() == 43
        && general_purpose::URL_SAFE_NO_PAD
            .decode(input)
            .is_ok_and(|bytes| bytes.len() == 32)
}

async fn send(stream: &mut impl SecureStream, packet: Packet) -> Result<()> {
    let bytes = serde_json::to_vec(&packet)?;
    ensure!(
        bytes.len() <= PACKET_LIMIT,
        "Remote authorization message is too large"
    );
    stream.send_packet(bytes).await
}

async fn receive(stream: &mut impl SecureStream) -> Result<Packet> {
    let bytes = stream.receive_packet(PACKET_LIMIT).await?;
    ensure!(
        bytes.len() <= PACKET_LIMIT,
        "Remote authorization message is too large"
    );
    serde_json::from_slice(&bytes).context("Invalid remote authorization message")
}

#[cfg(test)]
mod tests;
