use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::auth_projects::CreateProjectClient;
use terrascale::{ClientConfig, HttpTransport, Request, Response, Result, TerraScaleClient};

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const USER: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
const SIGN_IN: &str = "header.payload.signature";

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
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

fn json_response(status: u16, body: &serde_json::Value) -> Response {
    Response::json(status, body)
}

fn header<'a>(request: &'a Request, name: &str) -> Vec<&'a str> {
    request
        .headers
        .iter()
        .filter(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, value)| value.as_str())
        .collect()
}

#[tokio::test]
async fn list_users_sends_path_query_and_bearer_and_disable_user_posts() {
    let mock = Arc::new(Mock::default());
    let page = json!({
        "items": [
            { "id": USER, "email": "ada@lovelace.test", "created_at": "2026-10-22T12:00:00Z", "disabled": false }
        ],
        "next": "cursor-1"
    });
    let disabled = json!({
        "id": USER, "email": "ada@lovelace.test", "created_at": "2026-10-22T12:00:00Z", "disabled": true
    });
    mock.responses
        .lock()
        .unwrap()
        .extend([json_response(200, &page), json_response(200, &disabled)]);
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.test")
            .build()
            .expect("config"),
        transport,
    );
    let projects = client.projects().expect("projects client");
    let users = projects
        .list_users(SIGN_IN, PROJECT, Some("ada@"), Some(5), Some("cursor-0"))
        .await
        .expect("list users");
    let user = projects
        .disable_user(SIGN_IN, PROJECT, USER)
        .await
        .expect("disable user");
    let seen = mock.requests.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(
        seen[0].url,
        format!("https://auth.test/v1/{PROJECT}/users?q=ada%40&limit=5&after=cursor-0")
    );
    assert_eq!(seen[0].method, terrascale::Method::Get);
    assert_eq!(
        header(&seen[0], "authorization"),
        vec![format!("Bearer {SIGN_IN}")]
    );
    assert_eq!(
        seen[1].url,
        format!("https://auth.test/v1/{PROJECT}/users/{USER}/disable")
    );
    assert_eq!(seen[1].method, terrascale::Method::Post);
    assert_eq!(
        header(&seen[1], "authorization"),
        vec![format!("Bearer {SIGN_IN}")]
    );
    assert_eq!(users.items.len(), 1);
    assert_eq!(users.items[0].id, USER);
    assert_eq!(users.items[0].email, "ada@lovelace.test");
    assert!(!users.items[0].disabled);
    assert_eq!(users.next.as_deref(), Some("cursor-1"));
    assert!(user.disabled);
}

#[tokio::test]
async fn create_client_posts_with_step_up_proof_and_list_sessions_filters_by_user() {
    let mock = Arc::new(Mock::default());
    let client_body = json!({
        "client_id": "01ARZ3NDEKTSV4RRFFQ69G5FC2",
        "name": "web-app",
        "type": "confidential",
        "redirect_uris": ["https://app.test/callback"],
        "created_at": "2026-11-09T12:00:00Z",
        "client_secret": "cs_secret"
    });
    let sessions = json!({
        "items": [
            { "id": "01ARZ3NDEKTSV4RRFFQ69G5FC3", "user_id": USER, "email": "ada@lovelace.test",
              "created_at": "2026-11-09T10:00:00Z", "expires_at": "2026-11-16T10:00:00Z" }
        ]
    });
    mock.responses.lock().unwrap().extend([
        json_response(201, &client_body),
        json_response(200, &sessions),
    ]);
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.test")
            .build()
            .expect("config"),
        transport,
    );
    let projects = client.projects().expect("projects client");
    let created = projects
        .create_client(
            SIGN_IN,
            PROJECT,
            &CreateProjectClient {
                name: "web-app".to_owned(),
                r#type: "confidential".to_owned(),
                redirect_uris: vec!["https://app.test/callback".to_owned()],
            },
            "proof-token",
        )
        .await
        .expect("create client");
    let page = projects
        .list_sessions(SIGN_IN, PROJECT, Some(USER), Some(10), None)
        .await
        .expect("list sessions");
    let seen = mock.requests.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(
        seen[0].url,
        format!("https://auth.test/v1/{PROJECT}/clients")
    );
    assert_eq!(seen[0].method, terrascale::Method::Post);
    assert_eq!(
        header(&seen[0], "authorization"),
        vec![format!("Bearer {SIGN_IN}")]
    );
    assert_eq!(
        header(&seen[0], "x-terrascale-step-up-proof"),
        vec!["proof-token"]
    );
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(seen[0].body.as_deref().unwrap()).unwrap(),
        json!({ "name": "web-app", "type": "confidential", "redirect_uris": ["https://app.test/callback"] })
    );
    assert_eq!(
        seen[1].url,
        format!("https://auth.test/v1/{PROJECT}/sessions?user={USER}&limit=10")
    );
    assert_eq!(seen[1].method, terrascale::Method::Get);
    assert_eq!(created.client_id, "01ARZ3NDEKTSV4RRFFQ69G5FC2");
    assert_eq!(created.client_secret.as_deref(), Some("cs_secret"));
    assert_eq!(page.items.len(), 1);
    assert_eq!(page.items[0].user_id, USER);
}
