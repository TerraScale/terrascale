//! ts-auth -> TerraBase token contract: issuer, audience, scopes, 401 vs 403.
//! Fixtures only; every test owns its mock transport.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, UNIX_EPOCH};

use async_trait::async_trait;
use serde_json::json;
use terrascale::auth_idp::{
    OidcClientAuthentication, OidcPrincipalKind, OidcResourceActive, TERRABASE_AUDIENCE,
    TERRABASE_SCOPES, TERRASCALE_ISSUER,
};
use terrascale::{
    ClientConfig, ErrorKind, HttpTransport, Method, Request, Response, Result, TerraScaleClient,
};

const CLIENT_ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const SECRET: &str = "fixture-secret";

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().unwrap().push(request);
        self.responses
            .lock()
            .unwrap()
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock ran out of responses"))
    }
}

/// The production auth origin: one global host, no regional or Atlas hop.
fn platform(responses: Vec<Response>) -> (Arc<Mock>, TerraScaleClient) {
    let mock = Arc::new(Mock::default());
    mock.responses.lock().unwrap().extend(responses);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.terrascale.tech")
            .max_retry_attempts(3)
            .retry_delay_ms(0)
            .build()
            .unwrap(),
        mock.clone(),
    );
    (mock, sdk)
}

fn form() -> OidcClientAuthentication<'static> {
    OidcClientAuthentication::Form {
        client_id: CLIENT_ID,
        client_secret: SECRET,
    }
}

fn token(scope: &str) -> Response {
    Response::json(
        200,
        &json!({"access_token": "at", "token_type": "Bearer", "expires_in": 900, "scope": scope}),
    )
}

#[test]
fn constants_equal_the_contract() {
    assert_eq!(TERRASCALE_ISSUER, "https://auth.terrascale.tech/terrascale");
    assert_eq!(TERRABASE_AUDIENCE, "https://api.terrascale.tech");
    assert_eq!(
        TERRABASE_SCOPES,
        [
            "databases:read",
            "databases:write",
            "documents:read",
            "documents:write"
        ]
    );
}

#[tokio::test]
async fn discovery_and_token_go_straight_to_the_global_issuer() {
    // A retried discovery read stays on the global host; there is no regional failover.
    let (mock, sdk) = platform(vec![
        Response::json(503, &json!({})),
        Response::json(404, &json!({})),
        token(""),
    ]);
    let oidc = sdk.auth_idp("terrascale").unwrap().oidc();
    let _ = oidc.discovery().await;
    oidc.token_client_credentials(form(), TERRABASE_AUDIENCE, "documents:read")
        .await
        .unwrap();
    let urls: Vec<_> = mock
        .requests
        .lock()
        .unwrap()
        .iter()
        .map(|r| r.url.clone())
        .collect();
    let discovery = format!("{TERRASCALE_ISSUER}/.well-known/openid-configuration");
    assert_eq!(
        urls,
        [
            discovery.clone(),
            discovery,
            format!("{TERRASCALE_ISSUER}/oauth/token")
        ]
    );
}

#[tokio::test]
async fn client_credentials_sends_the_exact_audience_and_space_separated_scope() {
    let scope = TERRABASE_SCOPES.join(" ");
    let (mock, sdk) = platform(vec![token(&scope)]);
    let granted = sdk
        .auth_idp("terrascale")
        .unwrap()
        .oidc()
        .token_client_credentials(form(), TERRABASE_AUDIENCE, &scope)
        .await
        .unwrap();
    assert_eq!(granted.scope, scope);
    let request = mock.requests.lock().unwrap().remove(0);
    assert_eq!(request.method, Method::Post);
    assert_eq!(
        String::from_utf8(request.body.unwrap()).unwrap(),
        format!(
            "grant_type=client_credentials&resource=https%3A%2F%2Fapi.terrascale.tech\
             &scope=databases%3Aread%20databases%3Awrite%20documents%3Aread%20documents%3Awrite\
             &client_id={CLIENT_ID}&client_secret={SECRET}"
        )
    );
}

#[tokio::test]
async fn scopes_are_never_broadened_or_invented() {
    let (mock, sdk) = platform(vec![token("documents:read"), token("documents:read")]);
    let oidc = sdk.auth_idp("terrascale").unwrap().oidc();
    oidc.token_client_credentials(form(), TERRABASE_AUDIENCE, "documents:read")
        .await
        .unwrap();
    // A refresh without scope inherits the grant; the SDK adds none.
    oidc.token_refresh(form(), "rt", None, None).await.unwrap();
    // Missing or duplicated scope is refused before any request.
    for scope in ["", " ", "documents:read documents:read"] {
        let error = oidc
            .token_client_credentials(form(), TERRABASE_AUDIENCE, scope)
            .await
            .unwrap_err();
        assert_eq!(error.kind, ErrorKind::Validation, "{scope:?}");
    }
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    let bodies: Vec<String> = requests
        .iter()
        .map(|r| String::from_utf8(r.body.clone().unwrap()).unwrap())
        .collect();
    assert!(bodies[0].contains("&scope=documents%3Aread&"));
    assert!(!bodies[1].contains("scope="));
}

fn claims(iss: &str, aud: &str) -> OidcResourceActive {
    OidcResourceActive {
        token_type: "Bearer".into(),
        iss: iss.into(),
        sub: CLIENT_ID.into(),
        aud: aud.into(),
        client_id: CLIENT_ID.into(),
        scope: "databases:read documents:read".into(),
        exp: 2_000,
        iat: 1_100,
        sid: None,
        principal_kind: OidcPrincipalKind::Service,
    }
}

#[test]
fn binding_accepts_only_the_global_issuer_and_exact_audience() {
    let now = UNIX_EPOCH + Duration::from_secs(1_500);
    claims(TERRASCALE_ISSUER, TERRABASE_AUDIENCE)
        .check_binding_at(TERRASCALE_ISSUER, TERRABASE_AUDIENCE, now)
        .unwrap();
    for (iss, aud) in [
        (
            "https://gru.auth.terrascale.tech/terrascale",
            TERRABASE_AUDIENCE,
        ),
        (
            "https://auth.terrascale.tech/terrascale/",
            TERRABASE_AUDIENCE,
        ),
        ("https://auth.terrascale.tech", TERRABASE_AUDIENCE),
        (TERRASCALE_ISSUER, "https://api.terrascale.tech/"),
        (TERRASCALE_ISSUER, "https://gru.api.terrascale.tech"),
    ] {
        let error = claims(iss, aud)
            .check_binding_at(TERRASCALE_ISSUER, TERRABASE_AUDIENCE, now)
            .unwrap_err();
        assert_eq!(error.kind, ErrorKind::Authorization, "{iss} {aud}");
    }
}

#[tokio::test]
async fn token_401_and_403_stay_distinct_and_are_never_replayed() {
    for (status, kind) in [
        (401, ErrorKind::Authentication),
        (403, ErrorKind::Authorization),
    ] {
        let (mock, sdk) = platform(vec![
            Response::json(status, &json!({"error": "denied"})),
            token("documents:read"),
        ]);
        let error = sdk
            .auth_idp("terrascale")
            .unwrap()
            .oidc()
            .token_client_credentials(form(), TERRABASE_AUDIENCE, "documents:read")
            .await
            .unwrap_err();
        assert_eq!((error.status, error.kind), (Some(status), kind));
        assert_eq!(mock.requests.lock().unwrap().len(), 1);
    }
}
