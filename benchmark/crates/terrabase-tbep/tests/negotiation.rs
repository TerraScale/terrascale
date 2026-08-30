//! Connection-open capability and version negotiation (P4): the client
//! names its supported wire versions and capabilities, the peers select
//! the highest common version, unknown capabilities are ignored, and a
//! version mismatch fails closed with the typed error before any
//! operation frame runs.

use std::os::unix::net::UnixStream;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use terrabase_tbep::generated::{
    ChannelBindRequest, ErrorCode, NegotiationRequest, SupervisionHealthRequest,
};
use terrabase_tbep::{EnvelopeError, SyncDeliveryHost, TbepBody, TbepFrame, TbepServer};

/// One server over the sync-delivery host plus fresh client streams.
struct Fixture {
    server: terrabase_tbep::TbepServerHandle,
    socket: PathBuf,
}

impl Fixture {
    fn bind() -> Self {
        let socket = std::env::temp_dir().join(format!(
            "tbep-negotiation-{}-{}.sock",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(1)))
            .expect("the listener binds");
        Self { server, socket }
    }

    fn connect(&self) -> UnixStream {
        UnixStream::connect(&self.socket).expect("the client connects")
    }

    fn call(stream: &mut UnixStream, body: impl Into<TbepBody>, correlation: u64) -> TbepFrame {
        let frame = TbepFrame::seal(body, correlation, 0, None).expect("the body seals");
        frame.write_stream(stream).expect("the client writes");
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let answer = TbepFrame::read_stream(stream).expect("healthy").expect("a frame arrives");
            if answer.causation() == correlation {
                return answer;
            }
            assert!(std::time::Instant::now() < deadline, "the answer never arrived");
        }
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        self.server.shutdown();
    }
}

#[test]
fn negotiation_selects_the_highest_common_version_and_ignores_unknown_capabilities() {
    let fixture = Fixture::bind();
    let mut client = fixture.connect();

    let answer = Fixture::call(
        &mut client,
        NegotiationRequest {
            supported_versions: vec![9, 2, 1],
            capabilities: vec![
                "streaming-lanes".into(),
                "query-surface".into(),
                "quantum-tunneling".into(),
            ],
        },
        1,
    );
    let TbepBody::NegotiationResponse(response) = answer.body() else {
        panic!("the negotiation answered, got {:?}", answer.kind());
    };
    assert_eq!(response.selected_version, 1, "the highest COMMON version wins");
    assert_eq!(response.accepted_capabilities, vec!["streaming-lanes", "query-surface"]);

    // An operation frame still runs after the negotiation.
    let answer = Fixture::call(&mut client, SupervisionHealthRequest {}, 2);
    assert!(matches!(answer.body(), TbepBody::SupervisionHealthResponse(_)));
}

#[test]
fn a_version_mismatch_fails_closed_with_the_typed_error_before_any_operation() {
    let fixture = Fixture::bind();
    let mut client = fixture.connect();

    let answer = Fixture::call(
        &mut client,
        NegotiationRequest {
            supported_versions: vec![2, 3],
            capabilities: vec!["streaming-lanes".into()],
        },
        1,
    );
    let TbepBody::Error(error) = answer.body() else {
        panic!("the mismatch answers the typed error, got {:?}", answer.kind());
    };
    assert_eq!(error.code, ErrorCode::ProtocolVersionMismatch);

    // The connection failed closed: the write may already meet the
    // closed socket (EPIPE), and no further frame ever answers.
    let frame = TbepFrame::seal(SupervisionHealthRequest {}, 2, 0, None).expect("seals");
    let _ = frame.write_stream(&mut client);
    match TbepFrame::read_stream(&mut client) {
        Ok(None) | Err(EnvelopeError::Io(_)) => {}
        Ok(Some(frame)) => panic!("the closed stream answered with {:?}", frame.kind()),
        Err(error) => panic!("the closed stream returned a decode error: {error:?}"),
    }
}

#[test]
fn negotiation_needs_no_channel_binding_and_never_carries_a_key() {
    let fixture = Fixture::bind();
    let mut client = fixture.connect();

    // Before any bind, the negotiation answers — it is connection-open
    // control, not a tenant-scoped operation.
    let answer = Fixture::call(
        &mut client,
        NegotiationRequest { supported_versions: vec![1], capabilities: vec![] },
        1,
    );
    let TbepBody::NegotiationResponse(response) = answer.body() else {
        panic!("the bare negotiation answered");
    };
    assert_eq!(response.selected_version, 1);
    assert!(response.accepted_capabilities.is_empty());

    // The channel still binds afterwards.
    let answer = Fixture::call(
        &mut client,
        ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() },
        2,
    );
    assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
}
