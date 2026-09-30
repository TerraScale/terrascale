use async_trait::async_trait;
use serde_json::json;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
};
use terrascale::*;

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    replies: Mutex<VecDeque<Result<Response>>>,
}
#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().unwrap().push(request);
        self.replies
            .lock()
            .unwrap()
            .pop_front()
            .expect("unexpected transport call")
    }
}
fn id<T: std::str::FromStr>(n: u8) -> T
where
    T::Err: std::fmt::Debug,
{
    format!("00000000-0000-0000-0000-{n:012x}").parse().unwrap()
}
fn scope() -> DatabaseScope {
    DatabaseScope::new(id(1), id(2), u64::MAX)
}
fn sdk(mock: Arc<Mock>) -> TerraScaleClient {
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://gru.api.terrascale.tech")
            .api_key("native-secret")
            .auth_api_key("auth-secret")
            .max_retry_attempts(2)
            .retry_delay_ms(0)
            .build()
            .unwrap(),
        mock,
    )
}
fn operation() -> PreparedTransaction {
    PreparedTransaction::new(
        scope(),
        TransactionId::new(5000),
        Authority::Leaderless {},
        vec![Write {
            collection: id(3),
            document: id(4),
            schema_generation: DecimalU64(1),
            schema_digest: Digest::from_bytes([1; 32]),
            observed: vec![],
            condition: Condition::Absent {},
            value: WriteValue::Live {
                fields: Fields::new(vec![Field {
                    id: 1,
                    value: Value::Utf8("native".into()),
                }])
                .unwrap(),
            },
        }],
    )
    .unwrap()
}
fn receipt(operation: &PreparedTransaction) -> Receipt {
    Receipt {
        version: ProtocolVersion,
        transaction_id: operation.transaction_id().clone(),
        authority: operation.authority().clone(),
        effect_digest: operation.effect_digest(),
        event: Event {
            database: id(2),
            shard: DecimalU64(u64::MAX),
            replica: id(5),
            incarnation: Incarnation::from_bytes([1; 16]),
            sequence: DecimalU64(1),
        },
        commit_sequence: DecimalU64(1),
        durability: Durability::LocalDurable,
    }
}
fn absent() -> serde_json::Value {
    json!({"version":1,"state":"absent","context":[],"siblings":[],"state_version":null})
}

#[test]
fn invalid_custom_ca_fails_before_transport() {
    for pem in [
        &b""[..],
        &b"not a PEM certificate"[..],
        &b"-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"[..],
    ] {
        let error = ReqwestTransport::with_root_certificates(pem).unwrap_err();
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_root_certificate");
    }
}

#[tokio::test]
async fn malformed_native_discovery_credentials_fail_before_transport() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder().api_key("secret").build().unwrap(),
        mock.clone(),
    );
    assert_eq!(
        client
            .documents()
            .get(&scope(), id(3), id(4))
            .await
            .unwrap_err()
            .code,
        "invalid_discovery_credential"
    );
    assert!(mock.requests.lock().unwrap().is_empty());
    assert_eq!(regional_host(RegionCode::Gru), "gru.api.terrascale.tech");
    assert_eq!(
        ClientConfig::builder()
            .api_base_url(DEFAULT_API_BASE_URL)
            .build()
            .unwrap()
            .api_base_url(),
        Some(DEFAULT_API_BASE_URL)
    );
    assert!(ClientConfig::builder()
        .tenant_id("legacy-tenant-slug")
        .build()
        .is_err());
    let guarded = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://gru.api.terrascale.tech")
            .tenant_id("00000000-0000-0000-0000-000000000009")
            .build()
            .unwrap(),
        mock.clone(),
    );
    assert_eq!(
        guarded
            .documents()
            .get(&scope(), id(3), id(4))
            .await
            .unwrap_err()
            .code,
        "scope_mismatch"
    );
    assert!(mock.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn full_u64_uuid_path_and_native_credentials_are_exact() {
    let mock = Arc::new(Mock::default());
    mock.replies
        .lock()
        .unwrap()
        .push_back(Ok(Response::json(200, &absent())));
    sdk(mock.clone())
        .documents()
        .get(&scope(), id(3), id(4))
        .await
        .unwrap();
    let requests = mock.requests.lock().unwrap();
    let request = &requests[0];
    assert_eq!(request.url, "https://gru.api.terrascale.tech/v1/tenants/00000000-0000-0000-0000-000000000001/databases/00000000-0000-0000-0000-000000000002/shards/18446744073709551615/collections/00000000-0000-0000-0000-000000000003/documents/00000000-0000-0000-0000-000000000004");
    assert!(request
        .headers
        .contains(&("authorization".into(), "Bearer native-secret".into())));
    assert!(!format!("{request:?}").contains("native-secret"));
    assert!(!request
        .headers
        .iter()
        .any(|(name, value)| name.eq_ignore_ascii_case("idempotency-key")
            || name.contains("session")
            || value.contains("auth-secret")));
}

#[tokio::test]
async fn commit_transport_failure_is_uncertain_and_never_automatically_replayed() {
    let mock = Arc::new(Mock::default());
    let client = sdk(mock.clone());
    let operation = operation();
    mock.replies
        .lock()
        .unwrap()
        .push_back(Err(Error::transport("connection lost after body send")));
    let CommitOutcome::Uncertain(uncertain) =
        client.transactions().commit(&operation).await.unwrap()
    else {
        panic!("transport failure claimed a receipt")
    };
    assert_eq!(uncertain.operation.body(), operation.body());
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
    mock.replies
        .lock()
        .unwrap()
        .push_back(Ok(Response::json(200, &receipt(&operation))));
    assert!(matches!(
        client
            .transactions()
            .commit(&uncertain.operation)
            .await
            .unwrap(),
        CommitOutcome::Committed(_)
    ));
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests[0].body, requests[1].body);
    assert_eq!(requests[0].body.as_deref(), Some(operation.body()));
}

#[tokio::test]
async fn status_unknown_and_expired_never_claim_definitive_rejection() {
    let mock = Arc::new(Mock::default());
    let operation = operation();
    let client = sdk(mock.clone());
    for state in [
        TransactionState::Unknown,
        TransactionState::Pending,
        TransactionState::Expired,
    ] {
        mock.replies.lock().unwrap().push_back(Ok(Response::json(
            200,
            &TransactionStatus {
                version: ProtocolVersion,
                state,
                transaction_id: operation.transaction_id().clone(),
                effect_digest: operation.effect_digest(),
                authority: operation.authority().clone(),
                outcome: None,
            },
        )));
        assert!(!client
            .transactions()
            .status(&operation)
            .await
            .unwrap()
            .definitively_rejected());
    }
    let requests = mock.requests.lock().unwrap();
    assert!(requests
        .iter()
        .all(|request| request.url.ends_with("/transactions/status")));
    let body: serde_json::Value =
        serde_json::from_slice(requests[0].body.as_ref().unwrap()).unwrap();
    assert_eq!(
        body["transaction_id"],
        serde_json::to_value(operation.transaction_id()).unwrap()
    );
    assert_eq!(body["effect_digest"], operation.effect_digest().to_string());
    assert!(body.get("writes").is_none());
}

#[tokio::test]
async fn status_and_receipt_substitution_fail_closed() {
    let mock = Arc::new(Mock::default());
    let operation = operation();
    let client = sdk(mock.clone());
    let mut substituted = receipt(&operation);
    substituted.event.database = id(9);
    mock.replies
        .lock()
        .unwrap()
        .push_back(Ok(Response::json(200, &substituted)));
    assert!(matches!(
        client.transactions().commit(&operation).await.unwrap(),
        CommitOutcome::Uncertain(_)
    ));
    mock.replies.lock().unwrap().push_back(Ok(Response::json(
        200,
        &TransactionStatus {
            version: ProtocolVersion,
            state: TransactionState::Unknown,
            transaction_id: operation.transaction_id().clone(),
            effect_digest: Digest::from_bytes([9; 32]),
            authority: operation.authority().clone(),
            outcome: None,
        },
    )));
    assert_eq!(
        client
            .transactions()
            .status(&operation)
            .await
            .unwrap_err()
            .kind,
        ErrorKind::InvalidResponse
    );
    mock.replies.lock().unwrap().push_back(Ok(Response::json(
        412,
        &json!({"error":{"code":"precondition_failed","message":"condition rejected"}}),
    )));
    assert_eq!(
        client
            .transactions()
            .commit(&operation)
            .await
            .unwrap_err()
            .kind,
        ErrorKind::Precondition
    );
    assert_eq!(mock.requests.lock().unwrap().len(), 3);
}

#[tokio::test]
async fn native_query_uses_actual_collection_route_and_rejects_malformed_pages() {
    let mock = Arc::new(Mock::default());
    let client = sdk(mock.clone());
    let query = Query::new(Predicate::IsMissing { path: vec![1] }, 1).unwrap();
    mock.replies.lock().unwrap().push_back(Ok(Response::json(
        200,
        &json!({"version":1,"rows":[],"next_cursor":"next"}),
    )));
    client
        .documents()
        .query(&scope(), id(3), &query)
        .await
        .unwrap();
    let request = mock.requests.lock().unwrap()[0].clone();
    assert!(request
        .url
        .ends_with("/collections/00000000-0000-0000-0000-000000000003/documents/query"));
    assert_eq!(request.method, Method::Post);
    mock.replies.lock().unwrap().push_back(Ok(Response::json(200, &json!({"version":1,"rows":[{"document":"00000000-0000-0000-0000-000000000004","point":absent()}]}))));
    assert_eq!(
        client
            .documents()
            .query(&scope(), id(3), &query)
            .await
            .unwrap_err()
            .kind,
        ErrorKind::InvalidResponse
    );
}

#[tokio::test]
async fn unexpected_commit_statuses_and_zero_event_incarnation_remain_uncertain() {
    let mock = Arc::new(Mock::default());
    let operation = operation();
    let client = sdk(mock.clone());
    for status in [307, 408, 429, 503] {
        mock.replies.lock().unwrap().push_back(Ok(Response::json(
            status,
            &json!({"error":{"code":"precondition_failed","message":"unexpected HTTP outcome"}}),
        )));
        let CommitOutcome::Uncertain(uncertain) =
            client.transactions().commit(&operation).await.unwrap()
        else {
            panic!("unexpected status became definitive")
        };
        assert_eq!(uncertain.operation.body(), operation.body());
    }
    let mut invalid = receipt(&operation);
    invalid.event.incarnation = Incarnation::from_bytes([0; 16]);
    mock.replies
        .lock()
        .unwrap()
        .push_back(Ok(Response::json(200, &invalid)));
    assert!(matches!(
        client.transactions().commit(&operation).await.unwrap(),
        CommitOutcome::Uncertain(_)
    ));
    assert_eq!(mock.requests.lock().unwrap().len(), 5);
}

#[tokio::test]
async fn unexpected_success_statuses_never_claim_a_durable_commit() {
    let mock = Arc::new(Mock::default());
    let operation = operation();
    let client = sdk(mock.clone());
    for status in 201..300 {
        mock.replies
            .lock()
            .unwrap()
            .push_back(Ok(Response::json(status, &receipt(&operation))));
        let CommitOutcome::Uncertain(uncertain) =
            client.transactions().commit(&operation).await.unwrap()
        else {
            panic!("HTTP {status} claimed a durable commit")
        };
        assert_eq!(uncertain.cause.kind, ErrorKind::InvalidResponse);
        assert_eq!(uncertain.cause.code, "invalid_response");
        assert_eq!(uncertain.cause.status, Some(status));
        assert_eq!(uncertain.operation.body(), operation.body());
    }
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 99);
    assert!(requests
        .iter()
        .all(|request| request.body.as_deref() == Some(operation.body())));
}

#[tokio::test]
async fn valid_native_read_pages_require_http_200() {
    let mock = Arc::new(Mock::default());
    let operation = operation();
    let client = sdk(mock.clone());
    let predicate = Predicate::IsMissing { path: vec![1] };
    let query = Query::new(predicate.clone(), 1).unwrap();
    let snapshot = SnapshotRequest::new(predicate.clone(), 1).unwrap();
    let poll = PollRequest::new(predicate, "poll".into()).unwrap();
    let status_body = serde_json::to_value(TransactionStatus {
        version: ProtocolVersion,
        state: TransactionState::Unknown,
        transaction_id: operation.transaction_id().clone(),
        effect_digest: operation.effect_digest(),
        authority: operation.authority().clone(),
        outcome: None,
    })
    .unwrap();
    for (route, body) in [
        ("point", absent()),
        ("query", json!({"version":1,"rows":[],"next_cursor":"next"})),
        ("status", status_body),
        (
            "snapshot",
            json!({"version":1,"phase":"snapshot","rows":[],"poll_cursor":"poll"}),
        ),
        (
            "poll",
            json!({"version":1,"phase":"poll","groups":[],"poll_cursor":"next"}),
        ),
    ] {
        for status in [200, 202] {
            mock.replies
                .lock()
                .unwrap()
                .push_back(Ok(Response::json(status, &body)));
            let result = match route {
                "point" => client
                    .documents()
                    .get(&scope(), id(3), id(4))
                    .await
                    .map(|_| ()),
                "query" => client
                    .documents()
                    .query(&scope(), id(3), &query)
                    .await
                    .map(|_| ()),
                "status" => client.transactions().status(&operation).await.map(|_| ()),
                "snapshot" => client
                    .subscriptions()
                    .snapshot(&scope(), id(3), &snapshot)
                    .await
                    .map(|_| ()),
                "poll" => client
                    .subscriptions()
                    .poll(&scope(), id(3), &poll)
                    .await
                    .map(|_| ()),
                _ => unreachable!(),
            };
            if status == 200 {
                result.unwrap();
            } else {
                let error = result.unwrap_err();
                assert_eq!(error.kind, ErrorKind::InvalidResponse, "{route}");
                assert_eq!(error.status, Some(status), "{route}");
            }
        }
    }
    assert_eq!(mock.requests.lock().unwrap().len(), 10);
}
