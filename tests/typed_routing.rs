#![allow(deprecated)] // Exercises the native API kept until v1 replaces it.
use async_trait::async_trait;
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
