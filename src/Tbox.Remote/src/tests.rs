use std::sync::atomic::{AtomicUsize, Ordering};

use tokio::sync::mpsc;

use super::*;

struct TestStream {
    encrypted: bool,
    sent: mpsc::Sender<Vec<u8>>,
    received: mpsc::Receiver<Vec<u8>>,
}

#[async_trait]
impl SecureStream for TestStream {
    fn encrypted(&self) -> bool {
        self.encrypted
    }

    async fn send_packet(&mut self, bytes: Vec<u8>) -> Result<()> {
        self.sent
            .send(bytes)
            .await
            .context("Test peer disconnected")
    }

    async fn receive_packet(&mut self, maximum: usize) -> Result<Vec<u8>> {
        let bytes = self
            .received
            .recv()
            .await
            .context("Test peer disconnected")?;
        ensure!(bytes.len() <= maximum, "Test message is too large");
        Ok(bytes)
    }
}

fn peers() -> (TestStream, TestStream) {
    let (host_tx, controller_rx) = mpsc::channel(1);
    let (controller_tx, host_rx) = mpsc::channel(1);
    (
        TestStream {
            encrypted: true,
            sent: host_tx,
            received: host_rx,
        },
        TestStream {
            encrypted: true,
            sent: controller_tx,
            received: controller_rx,
        },
    )
}

#[test]
fn host_repairs_invalid_peer_ids_and_preserves_valid_ids() {
    for original in [
        "",
        "0",
        "12345",
        "-valid123",
        "abcdef/ghi",
        "abcdef测",
        &"a".repeat(33),
    ] {
        assert!(!valid_peer_id(original), "{original:?}");
        let replacement = host_peer_id(original);
        assert!(valid_peer_id(&replacement));
        assert_eq!(host_peer_id(&replacement), replacement);
    }
    for original in ["123456", "device_01-win", &"a".repeat(32)] {
        assert_eq!(host_peer_id(original), original);
    }
}

const KEY: [u8; 32] = [31; 32];
const CONTROLLER: Uuid = Uuid::from_u128(1);

fn invitation() -> Invitation {
    Invitation {
        session_id: Uuid::from_u128(2),
        host_id: Uuid::from_u128(3),
        ticket: general_purpose::URL_SAFE_NO_PAD.encode([42; 32]),
        transport: TransportIdentity {
            peer_id: "123456789".to_owned(),
            public_key: general_purpose::STANDARD.encode(KEY),
        },
    }
}

#[derive(Default)]
struct Signer(AtomicUsize);

#[async_trait]
impl ControllerSigner for Signer {
    async fn sign_challenge(&self, host_id: Uuid, ticket: &str, challenge: &str) -> Result<String> {
        assert_eq!(host_id, invitation().host_id);
        assert_eq!(ticket, invitation().ticket);
        assert!(valid_secret(challenge));
        self.0.fetch_add(1, Ordering::Relaxed);
        Ok(general_purpose::STANDARD.encode([0; 64]))
    }
}

#[tokio::test]
async fn never_sends_a_ticket_to_a_plaintext_or_unpinned_peer() {
    let (mut host, mut controller) = peers();
    let grant = invitation();
    let signer = Signer::default();

    controller.encrypted = false;
    assert!(
        authorize_controller(&mut controller, Some(&KEY), &grant, CONTROLLER, &signer)
            .await
            .is_err()
    );
    controller.encrypted = true;
    assert!(
        authorize_controller(&mut controller, None, &grant, CONTROLLER, &signer)
            .await
            .is_err()
    );
    assert!(
        authorize_controller(&mut controller, Some(&[0; 32]), &grant, CONTROLLER, &signer)
            .await
            .is_err()
    );

    assert_eq!(signer.0.load(Ordering::Relaxed), 0);
    assert!(matches!(
        host.received.try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
}

#[tokio::test]
async fn accepts_only_the_lease_for_the_current_invitation_and_two_devices() {
    let grant = invitation();
    for lease in [
        Lease {
            session_id: grant.session_id,
            controller_id: CONTROLLER,
            host_id: grant.host_id,
        },
        Lease {
            session_id: Uuid::from_u128(4),
            controller_id: CONTROLLER,
            host_id: grant.host_id,
        },
        Lease {
            session_id: grant.session_id,
            controller_id: Uuid::from_u128(5),
            host_id: grant.host_id,
        },
        Lease {
            session_id: grant.session_id,
            controller_id: CONTROLLER,
            host_id: Uuid::from_u128(6),
        },
    ] {
        let matches = lease.session_id == grant.session_id
            && lease.controller_id == CONTROLLER
            && lease.host_id == grant.host_id;
        let signer = Signer::default();
        let (mut host, mut controller) = peers();
        let host = async {
            let claim = receive_claim(&mut host).await.unwrap();
            assert_eq!(claim.ticket, grant.ticket);
            send(&mut host, Packet::Granted(lease)).await.unwrap();
        };
        let controller =
            authorize_controller(&mut controller, Some(&KEY), &grant, CONTROLLER, &signer);
        let ((), outcome) = tokio::join!(host, controller);
        assert_eq!(outcome.is_ok(), matches);
        assert_eq!(signer.0.load(Ordering::Relaxed), 1);
    }
}

#[tokio::test]
async fn rejects_a_claim_from_a_different_handshake() {
    let (mut host, mut controller) = peers();
    let old_nonce = general_purpose::URL_SAFE_NO_PAD.encode([7; 32]);

    let reply = async {
        let Packet::Challenge { nonce } = receive(&mut controller).await.unwrap() else {
            panic!("Unexpected packet");
        };
        assert_ne!(nonce, old_nonce);
        send(
            &mut controller,
            Packet::Claim(Claim {
                ticket: invitation().ticket,
                challenge: old_nonce,
                signature: general_purpose::STANDARD.encode([0; 64]),
            }),
        )
        .await
        .unwrap();
    };
    let (result, ()) = tokio::join!(receive_claim(&mut host), reply);
    assert!(result.is_err());
}

#[tokio::test(start_paused = true)]
async fn times_out_before_asking_the_device_to_sign_when_the_peer_is_silent() {
    let (_host, mut controller) = peers();
    let signer = Signer::default();
    let started = Instant::now();

    assert!(authorize_controller(
        &mut controller,
        Some(&KEY),
        &invitation(),
        CONTROLLER,
        &signer
    )
    .await
    .is_err());

    assert_eq!(started.elapsed(), HANDSHAKE_TIMEOUT);
    assert_eq!(signer.0.load(Ordering::Relaxed), 0);
}

#[tokio::test(start_paused = true)]
async fn an_expired_local_lease_cannot_be_renewed() {
    let host_id = Uuid::from_u128(3);
    let host = HostAuthorization::new("https://localhost/", host_id, invitation().ticket).unwrap();
    let lease = HostLease {
        host,
        session_id: Uuid::from_u128(2),
        controller_id: CONTROLLER,
        requested: Instant::now(),
    };
    tokio::time::advance(LEASE_DURATION).await;
    let error = lease.maintain().await.unwrap_err();
    assert_eq!(error.to_string(), "Remote lease expired");
}
