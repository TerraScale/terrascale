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
            ack: None,
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
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-1".into(),
            level: None,
        },
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
            ack: None,
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
            ack: None,
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
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-1".into(),
            level: None,
        },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the foreign read answered");
    };
    assert!(!response.found, "the document does not cross tenants");

    // The restart durability row continues in its own test below.
    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn the_committed_body_survives_a_full_restart() {
    let dir = location("restart");
    let mut fixture = Fixture::bind("restart", &dir, 1);
    fixture.bind_channel("tenant-a");
    let key = IdempotencyKey::new("op-restart-1").expect("valid key");
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: order_body(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-restart-1".into(),
            ack: None,
        },
        Some(&key),
    );
    assert!(matches!(answer.body(), TbepBody::DocumentWriteResponse(_)));
    drop(fixture);

    // A fresh host over the same durable location serves the committed
    // body.
    let mut reopened = Fixture::bind("restart-reopened", &dir, 2);
    reopened.bind_channel("tenant-a");
    let answer = reopened.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-1".into(),
            level: None,
        },
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
        ack: None,
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
            ack: None,
        },
        Some(&key),
    );
    let TbepBody::DocumentDeleteResponse(response) = answer.body() else {
        panic!("the delete answered");
    };
    assert!(matches!(response.outcome, WriteOutcome::Committed(_)));

    // The read no longer finds it.
    let answer = fixture.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-9".into(),
            level: None,
        },
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
            ack: None,
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

#[test]
fn the_engine_serves_v1_queries_over_uds() {
    use terrabase_tbep::generated::{QueryExecuteRequest, QueryTerminal};

    let dir = location("query");
    let mut fixture = Fixture::bind("query", &dir, 1);
    fixture.bind_channel("tenant-a");

    // Seed three documents in one collection.
    for index in 1..=3u64 {
        let body = TbepValue::Object(vec![
            ("item".to_owned(), TbepValue::String(format!("item-{index}"))),
            ("quantity".to_owned(), TbepValue::Integer(i64::try_from(index).expect("fits"))),
        ]);
        let answer = fixture.call(
            DocumentWriteRequest {
                document: DocumentPut {
                    id: format!("order-{index}"),
                    collection: "orders".into(),
                    body,
                },
                expected: ExpectedVersion::Absent,
                operation_id: format!("op-q-{index}"),
                ack: None,
            },
            Some(&IdempotencyKey::new(&format!("op-q-{index}")).expect("valid key")),
        );
        assert!(matches!(answer.body(), TbepBody::DocumentWriteResponse(_)));
    }

    // The full scan serves every matched document, complete.
    let answer = fixture.call(
        QueryExecuteRequest {
            query_id: 1,
            text: "from orders".into(),
            max_items: 256,
            level: None,
        },
        None,
    );
    let TbepBody::QueryExecuteResponse(response) = answer.body() else {
        panic!("the query answered, got {:?}", answer.kind());
    };
    assert_eq!(response.count, 3, "the matched count is the query's total");
    assert_eq!(response.documents.len(), 3);
    assert_eq!(response.documents[0].id, "order-1");
    assert_eq!(response.terminal, QueryTerminal::Complete);

    // A page below the matched count is truncated, and still names the
    // total.
    let answer = fixture.call(
        QueryExecuteRequest { query_id: 2, text: "from orders".into(), max_items: 2, level: None },
        None,
    );
    let TbepBody::QueryExecuteResponse(response) = answer.body() else {
        panic!("the paged query answered");
    };
    assert_eq!(response.count, 3);
    assert_eq!(response.documents.len(), 2);
    assert_eq!(response.terminal, QueryTerminal::Truncated);

    // A filtered query matches its predicate.
    let answer = fixture.call(
        QueryExecuteRequest {
            query_id: 3,
            text: "from orders where quantity = 2".into(),
            max_items: 256,
            level: None,
        },
        None,
    );
    let TbepBody::QueryExecuteResponse(response) = answer.body() else {
        panic!("the filtered query answered");
    };
    assert_eq!(response.count, 1);
    assert_eq!(response.documents[0].id, "order-2");

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

/// Declares the required-string-status policy for `orders` and asserts
/// its adoption: version 1, two rules (`Required` + `FieldType`).
fn declare_status_policy(fixture: &mut Fixture, key: &str) {
    use terrabase_tbep::generated::SchemaDeclareRequest;
    let answer = fixture.call(
        SchemaDeclareRequest {
            collection: "orders".into(),
            schema: Some(TbepValue::Object(vec![
                ("type".to_owned(), TbepValue::String("object".to_owned())),
                (
                    "required".to_owned(),
                    TbepValue::Array(vec![TbepValue::String("status".to_owned())]),
                ),
                (
                    "properties".to_owned(),
                    TbepValue::Object(vec![(
                        "status".to_owned(),
                        TbepValue::Object(vec![(
                            "type".to_owned(),
                            TbepValue::String("string".to_owned()),
                        )]),
                    )]),
                ),
            ])),
            operation_id: key.into(),
        },
        Some(&IdempotencyKey::new(key).expect("valid key")),
    );
    let TbepBody::SchemaDeclareResponse(response) = answer.body() else {
        panic!("the declare answered, got {:?}", answer.kind());
    };
    assert_eq!(response.enforced_version, Some(1));
    assert_eq!(response.rule_count, 2);
}

#[test]
fn the_engine_serves_schema_declare_status_and_enforcement() {
    use terrabase_tbep::generated::SchemaStatusRequest;

    let dir = location("schema");
    let mut fixture = Fixture::bind("schema", &dir, 1);
    fixture.bind_channel("tenant-a");
    declare_status_policy(&mut fixture, "op-schema-1");

    // Status names the same recorded policy.
    let answer = fixture.call(SchemaStatusRequest { collection: "orders".into() }, None);
    let TbepBody::SchemaStatusResponse(response) = answer.body() else {
        panic!("the status answered");
    };
    assert_eq!(response.enforced_version, Some(1));
    assert_eq!(response.rule_count, 2);

    // A conforming write commits.
    let conforming =
        TbepValue::Object(vec![("status".to_owned(), TbepValue::String("open".to_owned()))]);
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-ok".into(),
                collection: "orders".into(),
                body: conforming,
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-schema-2".into(),
            ack: None,
        },
        Some(&IdempotencyKey::new("op-schema-2").expect("valid key")),
    );
    assert!(matches!(answer.body(), TbepBody::DocumentWriteResponse(_)));

    // A violating write is refused — the recorded policy enforces on the
    // staged put — and nothing durable happens.
    let violating = TbepValue::Object(vec![("status".to_owned(), TbepValue::Integer(7))]);
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-bad".into(),
                collection: "orders".into(),
                body: violating.clone(),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-schema-3".into(),
            ack: None,
        },
        Some(&IdempotencyKey::new("op-schema-3").expect("valid key")),
    );
    assert!(
        matches!(answer.body(), TbepBody::Error(_)),
        "the violating write is refused, got {:?}",
        answer.kind()
    );
    let answer = fixture.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-bad".into(),
            level: None,
        },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the post-refusal read answered");
    };
    assert!(!response.found, "the refused write recorded nothing");

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn the_schema_validate_surface_names_field_and_kind() {
    use terrabase_tbep::generated::{SchemaValidateRequest, ViolationKind};

    let dir = location("validate");
    let mut fixture = Fixture::bind("validate", &dir, 1);
    fixture.bind_channel("tenant-a");
    declare_status_policy(&mut fixture, "op-validate-1");

    // The conforming body is valid against the enforced version.
    let answer = fixture.call(
        SchemaValidateRequest {
            collection: "orders".into(),
            document: TbepValue::Object(vec![(
                "status".to_owned(),
                TbepValue::String("open".to_owned()),
            )]),
        },
        None,
    );
    let TbepBody::SchemaValidateResponse(response) = answer.body() else {
        panic!("the validate answered, got {:?}", answer.kind());
    };
    assert_eq!(response.enforced_version, Some(1));
    assert!(response.valid);
    assert!(response.violations.is_empty());

    // The violating body names its field and kind — with nothing staged.
    let answer = fixture.call(
        SchemaValidateRequest {
            collection: "orders".into(),
            document: TbepValue::Object(vec![("status".to_owned(), TbepValue::Integer(7))]),
        },
        None,
    );
    let TbepBody::SchemaValidateResponse(response) = answer.body() else {
        panic!("the invalid validate answered");
    };
    assert!(!response.valid);
    assert_eq!(response.violations.len(), 1);
    assert_eq!(response.violations[0].field, "status");
    assert!(matches!(response.violations[0].kind, ViolationKind::TypeMismatch));

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn schema_clearing_returns_the_collection_to_schema_free() {
    use terrabase_tbep::generated::SchemaDeclareRequest;

    let dir = location("clear");
    let mut fixture = Fixture::bind("clear", &dir, 1);
    fixture.bind_channel("tenant-a");
    declare_status_policy(&mut fixture, "op-clear-1");

    // Clearing: no enforced version.
    let answer = fixture.call(
        SchemaDeclareRequest {
            collection: "orders".into(),
            schema: None,
            operation_id: "op-clear-2".into(),
        },
        Some(&IdempotencyKey::new("op-clear-2").expect("valid key")),
    );
    let TbepBody::SchemaDeclareResponse(response) = answer.body() else {
        panic!("the clear answered");
    };
    assert_eq!(response.enforced_version, None);

    // The previously violating body now commits: schema-free again.
    let violating = TbepValue::Object(vec![("status".to_owned(), TbepValue::Integer(7))]);
    let answer = fixture.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-any".into(),
                collection: "orders".into(),
                body: violating,
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-clear-3".into(),
            ack: None,
        },
        Some(&IdempotencyKey::new("op-clear-3").expect("valid key")),
    );
    assert!(matches!(answer.body(), TbepBody::DocumentWriteResponse(_)));

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn the_engine_serves_acknowledgement_levels_read_levels_and_cluster_mode() {
    use terrabase_tbep::generated::{Acknowledgement, ClusterMode, ReadLevel, TopologyReadRequest};

    let dir = location("levels");
    let mut fixture = Fixture::bind("levels", &dir, 1);
    fixture.bind_channel("tenant-a");

    // A write with an explicit global-quorum acknowledgement names it in
    // the receipt; one without carries the regional default.
    let write = |id: &str, ack: Option<Acknowledgement>| DocumentWriteRequest {
        document: DocumentPut { id: id.into(), collection: "orders".into(), body: order_body() },
        expected: ExpectedVersion::Absent,
        operation_id: format!("op-level-{id}"),
        ack,
    };
    let answer = fixture.call(
        write("a", Some(Acknowledgement::GlobalQuorum)),
        Some(&IdempotencyKey::new("op-level-a").expect("valid key")),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the global write answered");
    };
    let WriteOutcome::Committed(receipt) = &response.outcome else {
        panic!("the global write commits");
    };
    assert_eq!(receipt.acknowledgement, Some(Acknowledgement::GlobalQuorum));

    let answer = fixture
        .call(write("b", None), Some(&IdempotencyKey::new("op-level-b").expect("valid key")));
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the default write answered");
    };
    let WriteOutcome::Committed(receipt) = &response.outcome else {
        panic!("the default write commits");
    };
    assert_eq!(receipt.acknowledgement, Some(Acknowledgement::RegionalQuorum));

    // Reads name the position they observed, at every level.
    let answer = fixture.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "a".into(),
            level: Some(ReadLevel::RegionalQuorumRead),
        },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the leveled read answered");
    };
    assert!(response.found);
    let position = response.position.expect("the read names its position");
    assert!(position >= receipt.sequence, "the position is at least the write's");

    // Queries name the snapshot position too.
    let answer = fixture.call(
        terrabase_tbep::generated::QueryExecuteRequest {
            query_id: 1,
            text: "from orders".into(),
            max_items: 16,
            level: Some(ReadLevel::GlobalQuorumRead),
        },
        None,
    );
    let TbepBody::QueryExecuteResponse(response) = answer.body() else {
        panic!("the leveled query answered");
    };
    assert_eq!(response.count, 2);
    assert!(response.position.is_some());

    // Topology answers the honest standalone mode.
    let answer = fixture.call(TopologyReadRequest {}, None);
    let TbepBody::TopologyReadResponse(response) = answer.body() else {
        panic!("the topology answered, got {:?}", answer.kind());
    };
    assert_eq!(response.cluster_mode, Some(ClusterMode::Standalone));
    assert!(!response.engine_topology_available);
    assert!(response.subsystems.contains(&"documents".to_owned()));

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn query_cancellation_answers_typed_outcomes_for_finished_and_unknown_queries() {
    use terrabase_tbep::generated::{CancelOutcome, QueryCancelRequest};

    let dir = location("cancel");
    let mut fixture = Fixture::bind("cancel", &dir, 1);
    fixture.bind_channel("tenant-a");

    // A query that completes retires its id: a later cancel names it
    // already complete, inside the bounded horizon.
    let answer = fixture.call(
        terrabase_tbep::generated::QueryExecuteRequest {
            query_id: 41,
            text: "from orders".into(),
            max_items: 16,
            level: None,
        },
        None,
    );
    assert!(matches!(answer.body(), TbepBody::QueryExecuteResponse(_)));
    let answer = fixture.call(QueryCancelRequest { query_id: 41 }, None);
    let TbepBody::QueryCancelResponse(response) = answer.body() else {
        panic!("the finished cancel answered, got {:?}", answer.kind());
    };
    assert!(matches!(response.outcome, CancelOutcome::AlreadyComplete));

    // A cancel naming a query this host never saw is unknown.
    let answer = fixture.call(QueryCancelRequest { query_id: 404 }, None);
    let TbepBody::QueryCancelResponse(response) = answer.body() else {
        panic!("the unknown cancel answered");
    };
    assert!(matches!(response.outcome, CancelOutcome::UnknownQuery));

    drop(fixture);
    let _ = std::fs::remove_dir_all(&dir);
}
