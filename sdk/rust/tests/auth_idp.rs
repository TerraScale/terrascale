use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::{json, Value};
use terrascale::auth_idp::CreateAuthIdpUser;
use terrascale::{
    ClientConfig, ErrorKind, HttpTransport, Method, Request, Response, Result, TerraScaleClient,
};

const SLUG: &str = "acme";

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    fn push(&self, response: Response) {
        self.responses
            .lock()
            .expect("response mutex")
            .push_back(response);
    }

    fn count(&self) -> usize {
        self.requests.lock().expect("request mutex").len()
    }

    fn request(&self, index: usize) -> Request {
        self.requests
            .lock()
            .expect("request mutex")
            .get(index)
            .cloned()
            .expect("captured request")
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock ran out of responses"))
    }
}

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        transport,
    )
}

fn retry_client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    )
}

fn header<'a>(request: &'a Request, name: &str) -> Vec<&'a str> {
    request
        .headers
        .iter()
        .filter(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, value)| value.as_str())
        .collect()
}

fn body_json(request: &Request) -> Value {
    serde_json::from_slice(request.body.as_deref().unwrap_or_default()).expect("json body")
}

fn empty(status: u16) -> Response {
    Response {
        status,
        headers: Vec::new(),
        body: Vec::new().into(),
    }
}

fn user_json(id: &str) -> Value {
    json!({
        "id": id,
        "email": "ada@lovelace.test",
        "email_verified": true,
        "username": "ada",
        "name": "Ada",
        "banned": false,
        "ban_reason": null,
        "created_at": 1_758_000_000_i64,
        "last_sign_in_at": 1_758_000_100_i64,
        "public_metadata": { "tier": "gold" },
        "private_metadata": { "flag": true },
    })
}

#[tokio::test]
async fn management_routes_carry_the_ts_auth_bearer_and_accept_headers() {
    let mock = Mock::new();
    mock.push(Response::json(201, &user_json("user_1")));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_2")] }),
    ));
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    auth.users()
        .create(CreateAuthIdpUser {
            email: Some("ada@lovelace.test".to_owned()),
            ..CreateAuthIdpUser::default()
        })
        .await
        .expect("created user");
    auth.users()
        .list(Some(2), Some("user_1"))
        .await
        .expect("page");

    let create = mock.request(0);
    assert_eq!(create.method, Method::Post);
    assert_eq!(create.url, "https://auth.test/v1/acme/users");
    assert_eq!(header(&create, "authorization"), ["Bearer sk_test_key"]);
    assert_eq!(header(&create, "accept"), ["application/json"]);
    assert_eq!(header(&create, "content-type"), ["application/json"]);
    assert_eq!(body_json(&create), json!({ "email": "ada@lovelace.test" }));

    let list = mock.request(1);
    assert_eq!(list.method, Method::Get);
    assert_eq!(
        list.url,
        "https://auth.test/v1/acme/users?limit=2&starting_after=user_1"
    );
    assert_eq!(header(&list, "authorization"), ["Bearer sk_test_key"]);
}

#[tokio::test]
async fn maps_nested_management_errors() {
    let mock = Mock::new();
    mock.push(Response::json(
        409,
        &json!({ "error": { "code": "email_taken", "message": "Email already taken." } }),
    ));
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .create(CreateAuthIdpUser {
            email: Some("ada@lovelace.test".to_owned()),
            ..CreateAuthIdpUser::default()
        })
        .await
        .expect_err("conflict");
    assert_eq!(error.kind, ErrorKind::Conflict);
    assert_eq!(error.code, "email_taken");
    assert_eq!(error.message, "Email already taken.");
    assert_eq!(error.status, Some(409));
}

#[tokio::test]
async fn retries_reads_but_never_mutations() {
    let mock = Mock::new();
    mock.push(empty(503));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let sdk = retry_client(Arc::clone(&mock));
    sdk.auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .list(None, None)
        .await
        .expect("second attempt succeeds");
    assert_eq!(mock.count(), 2);

    let mock = Mock::new();
    mock.push(empty(503));
    let sdk = retry_client(Arc::clone(&mock));
    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .api_keys()
        .create("CI", Some(&["users:read".to_owned()]))
        .await
        .expect_err("mutations never retry");
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.count(), 1);
}
