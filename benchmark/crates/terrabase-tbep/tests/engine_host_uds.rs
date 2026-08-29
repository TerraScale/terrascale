//! The engine-backed host end to end over a real UDS socket (P4): the
//! real durable Engine serves the protocol's document spine — channel
//! binding, writes with idempotent retry, reads with versions, deletes
//! with conflict outcomes, durability across a server restart, and
//! tenant isolation — through the typed wire frames a C# client sends.

use std::os::unix::net::UnixStream;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use terrabase_tbep::codec::TbepValue;
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::generated::{
    ChannelBindRequest, DocumentDeleteRequest, DocumentPut, DocumentReadRequest,
    DocumentWriteRequest, ExpectedVersion, WriteOutcome,
};
use terrabase_tbep::{IdempotencyKey, TbepBody, TbepFrame, TbepServer};

/// One bound engine-backed server plus one connected client stream.
struct Fixture {
    server: terrabase_tbep::TbepServerHandle,
    socket: PathBuf,
    stream: UnixStream,
    correlation: u64,
}

impl Fixture {
    fn bind(tag: &str, location: &std::path::Path, instance: u64) -> Self {
        let socket =
            std::env::temp_dir().join(format!("tbep-engine-{tag}-{}.sock", std::process::id()));
        let host = EngineHost::open(location, instance).expect("the engine host opens");
        let server = TbepServer::bind(&socket, Arc::new(host)).expect("the UDS listener binds");
        let stream = UnixStream::connect(&socket).expect("the client connects");
        Self { server, socket, stream, correlation: 0 }
    }

    fn next_correlation(&mut self) -> u64 {
        self.correlation += 1;
        self.correlation
    }

    fn call(
        &mut self,
        body: impl Into<TbepBody>,
        idempotency: Option<&IdempotencyKey>,
    ) -> TbepFrame {
        let correlation = self.next_correlation();
        let frame = TbepFrame::seal(body, correlation, 0, idempotency).expect("the body seals");
        frame.write_stream(&mut self.stream).expect("the client writes");
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let answer = TbepFrame::read_stream(&mut self.stream)
                .expect("the stream stays healthy")
                .expect("a frame arrives");
            if answer.causation() == correlation {
                return answer;
            }
            assert!(std::time::Instant::now() < deadline, "the answer never arrived");
        }
    }

    /// Opens a fresh client channel: one bind per channel, so another
    /// tenant needs its own stream.
    fn reconnect(&mut self) {
        self.stream = UnixStream::connect(&self.socket).expect("the second client connects");
        self.correlation = 0;
    }

    fn bind_channel(&mut self, tenant: &str) {
        let answer = self
            .call(ChannelBindRequest { principal: "backend".into(), tenant: tenant.into() }, None);
        assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = self.stream.shutdown(std::net::Shutdown::Both);
        self.server.shutdown();
    }
}

fn location(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbep-engine-host-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn order_body() -> TbepValue {
    TbepValue::Object(vec![
        ("item".to_owned(), TbepValue::String("bookshelf".to_owned())),
        ("quantity".to_owned(), TbepValue::Integer(2)),
    ])
}

#[test]
fn the_engine_serves_the_document_spine_over_uds() {
    let dir = location("spine");
    let mut fixture = Fixture::bind("spine", &dir, 1);
    fixture.bind_channel("tenant-a");

    // Write: committed, with the published version and the idempotency
    // key as the operation identity.
    let key = IdempotencyKey::new("op-write-1").expect("valid key");
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-write-1".into(),
        },
        Some(&key),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the write answered, got {:?}", answer.kind());
    };
    let WriteOutcome::Committed(receipt) = &response.outcome else {
        panic!("the first write commits, got {:?}", response.outcome);
    };
    assert_eq!(receipt.operation_id, "op-write-1");
    assert_eq!(receipt.version, 1);

    // Read: the published body and version.
    let answer = fixture.call(
        DocumentReadRequest { collection: "orders".into(), document: "order-1".into() },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the read answered");
    };
    assert!(response.found);
    let document = response.document.as_ref().expect("the body is served");
    assert_eq!(document.version, 1);
    assert_eq!(document.body, order_body());

    // Idempotent retry: the same key replays the recorded receipt —
    // no second publication.
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-write-1".into(),
        },
        Some(&key),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the retry answered");
    };
    let WriteOutcome::Committed(retried) = &response.outcome else {
        panic!("the retry replays the receipt");
    };
    assert_eq!(retried.sequence, receipt.sequence, "the replay names the same sequence");

    // An absent expectation on an existing document is a conflict.
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-write-2".into(),
        },
        Some(&IdempotencyKey::new("op-write-2").expect("valid key")),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the conflicting write answered");
    };
    assert_eq!(response.outcome, WriteOutcome::Conflict);

    // Tenant isolation: another tenant's own channel sees nothing.
    fixture.reconnect();
    fixture.bind_channel("tenant-b");
    let answer = fixture.call(
        DocumentReadRequest { collection: "orders".into(), document: "order-1".into() },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the foreign read answered");
    };
    assert!(!response.found, "the document does not cross tenants");

    drop(fixture);

    // Durability across a server restart: a fresh host over the same
    // durable location serves the committed body.
    let mut reopened = Fixture::bind("spine-restart", &dir, 2);
    reopened.bind_channel("tenant-a");
    let answer = reopened.call(
        DocumentReadRequest { collection: "orders".into(), document: "order-1".into() },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the restarted read answered");
    };
    assert!(response.found, "the committed write survives the restart");
    assert_eq!(response.document.as_ref().expect("served").body, order_body());

    drop(reopened);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn the_engine_serves_deletes_with_outcomes() {
    let dir = location("delete");
    let mut fixture = Fixture::bind("delete", &dir, 1);
    fixture.bind_channel("tenant-a");

    let write = DocumentWriteRequest {
        document: DocumentPut {
            id: "order-9".into(),
            collection: "orders".into(),
            body: order_body(),
        },
        expected: ExpectedVersion::Any,
        operation_id: "op-del-1".into(),
    };
    let answer = fixture.call(write, Some(&IdempotencyKey::new("op-del-1").expect("valid key")));
    assert!(matches!(answer.body(), TbepBody::DocumentWriteResponse(_)));

    // Delete with the matching expectation commits.
    let key = IdempotencyKey::new("op-del-2").expect("valid key");
    let answer = fixture.call(
        DocumentDeleteRequest {
            collection: "orders".into(),
            document: "order-9".into(),
            expected: ExpectedVersion::Exact(1),
            operation_id: "op-del-2".into(),
        },
        Some(&key),
    );
    let TbepBody::DocumentDeleteResponse(response) = answer.body() else {
        panic!("the delete answered");
    };
    assert!(matches!(response.outcome, WriteOutcome::Committed(_)));

    // The read no longer finds it.
    let answer = fixture.call(
        DocumentReadRequest { collection: "orders".into(), document: "order-9".into() },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the post-delete read answered");
    };
    assert!(!response.found);

    // The idempotent retry replays the recorded delete.
    let answer = fixture.call(
        DocumentDeleteRequest {
            collection: "orders".into(),
            document: "order-9".into(),
            expected: ExpectedVersion::Exact(1),
            operation_id: "op-del-2".into(),
        },
        Some(&key),
    );
    let TbepBody::DocumentDeleteResponse(response) = answer.body() else {
        panic!("the retried delete answered");
    };
    assert!(matches!(response.outcome, WriteOutcome::Committed(_)));

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn an_invalid_tenant_binding_is_refused_before_any_engine_scope() {
    let dir = location("invalid");
    let mut fixture = Fixture::bind("invalid", &dir, 1);
    let answer = fixture.call(
        ChannelBindRequest { principal: "backend".into(), tenant: " leading-space".into() },
        None,
    );
    assert!(
        matches!(answer.body(), TbepBody::Error(_)),
        "the invalid tenant never binds, got {:?}",
        answer.kind()
    );
    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}
