//! Independent checks against the versioned producer HTTP corpus.

use async_trait::async_trait;
use serde_json::{json, Value};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use terrascale::{
    generated_contract, ClientConfig, CreateDatabaseRequest, ErrorKind, HttpTransport, Method,
    OperationOptions, RegionCode, Request, Response, Result, TerraScaleClient,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new(response: Response) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(VecDeque::from([response])),
        })
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().unwrap().push(request);
        self.responses
            .lock()
            .unwrap()
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("unexpected second attempt"))
    }
}

fn response(status: u16, body: &Value) -> Response {
    Response {
        status,
        headers: vec![
            ("content-type".into(), "application/json".into()),
            ("x-request-id".into(), "producer-conformance".into()),
        ],
        body: serde_json::to_vec(body).unwrap(),
    }
}

fn corpus() -> Value {
    serde_json::from_str(include_str!("../contracts/v1/conformance/http.json")).unwrap()
}

fn concrete_path(template: &str) -> String {
    let database = if template.contains("/auth/") { "sample" } else { "demo" };
    template
        .replace("{name}", "demo")
        .replace("{database}", database)
        .replace("{collection}", "orders")
        .replace("{field}", "status")
        .replace("{model}", "sample")
        .replace("{id}", "order-1")
        .replace("{operation}", "sample")
        .replace("{operation_id}", "sample")
        .replace("{job}", "sample")
        .replace("{job_id}", "sample")
        .replace("{webhook}", "order-1")
        .replace("{delivery}", "sample")
        .replace("{delivery_id}", "sample")
        .replace("{member}", "sample")
        .replace("{view}", "sample")
        .replace("{offset}", "sample")
        .replace("{generation}", "sample")
        .replace("{chunk}", "sample")
}

#[test]
fn producer_corpus_binds_to_pinned_openapi_and_exact_generated_inventory() {
    let corpus = corpus();
    let openapi = include_bytes!("../contracts/v1/openapi.json");
    let digest = std::process::Command::new("sha256sum")
        .arg("contracts/v1/openapi.json")
        .output()
        .expect("sha256sum is required for contract verification");
    assert!(digest.status.success());
    let actual = String::from_utf8(digest.stdout).unwrap();
    assert_eq!(actual.split_whitespace().next().unwrap(), corpus["openapiSha256"]);
    assert_eq!(openapi.len(), std::fs::metadata("contracts/v1/openapi.json").unwrap().len() as usize);

    let operations = corpus["operations"].as_array().unwrap();
    assert_eq!(operations.len(), generated_contract::ROUTES.len());
    for route in generated_contract::ROUTES {
        let operation = operations
            .iter()
            .find(|value| value["operationId"] == route.operation_id)
            .unwrap_or_else(|| panic!("missing producer operation {}", route.operation_id));
        let request = &operation["request"];
        assert_eq!(request["path"], concrete_path(route.path), "{} path", route.operation_id);
        assert_eq!(request["method"].as_str().unwrap(), match route.method {
            generated_contract::HttpMethod::Get => "GET",
            generated_contract::HttpMethod::Post => "POST",
            generated_contract::HttpMethod::Put => "PUT",
            generated_contract::HttpMethod::Patch => "PATCH",
            generated_contract::HttpMethod::Delete => "DELETE",
            _ => panic!("unsupported generated method"),
        }, "{} method", route.operation_id);
    }
}

#[tokio::test]
async fn public_database_create_consumes_every_producer_response_and_error_once() {
    let corpus = corpus();
    let operation = corpus["operations"]
        .as_array().unwrap().iter()
        .find(|value| value["operationId"] == "create_database").unwrap();
    let request_fixture = &operation["request"];
    assert_eq!(request_fixture["method"], "POST");
    assert_eq!(request_fixture["path"], "/api/v1/databases");

    let mut cases = Vec::new();
    cases.extend(operation["responses"].as_array().unwrap().iter().map(|v| (true, v)));
    cases.extend(operation["errors"].as_array().unwrap().iter().map(|v| (false, v)));
    assert_eq!(cases.len(), 13);

    for (success, vector) in cases {
        let status = vector["status"].as_u64().unwrap() as u16;
        let body = &vector["body"];
        let mock = Mock::new(response(status, body));
        let client = TerraScaleClient::with_transport(
            ClientConfig::builder().api_key("conformance-key").project_id("proj").build().unwrap(),
            mock.clone(),
        );
        let result = client.management().databases().create_with_placement(
            CreateDatabaseRequest::new("sample", vec![RegionCode::Gru]).unwrap(),
            None,
            OperationOptions::default(),
        ).await;
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 1, "status {status} was retried");
        assert_eq!(requests[0].method, Method::Post);
        assert_eq!(requests[0].url, "https://api.terrascale.tech/api/v1/databases");
        assert_eq!(requests[0].headers.iter().find(|(n, _)| n.eq_ignore_ascii_case("authorization")).map(|(_, v)| v.as_str()), Some("Bearer conformance-key"));
        let wire: Value = serde_json::from_slice(requests[0].body.as_ref().unwrap()).unwrap();
        assert_eq!(wire["name"], "sample");
        assert_eq!(wire["placement"], json!(["GRU"]));
        drop(requests);
        if success {
            assert!(result.is_ok(), "status {status}: {result:?}");
        } else {
            let error = result.expect_err("producer error accepted");
            assert_eq!(error.status, Some(status));
            assert_eq!(error.kind, match status {
                400 | 422 => ErrorKind::Validation,
                401 => ErrorKind::Authentication,
                403 => ErrorKind::Authorization,
                404 => ErrorKind::NotFound,
                409 => ErrorKind::Conflict,
                421 => ErrorKind::RegionRedirect,
                429 => ErrorKind::RateLimit,
                500 => ErrorKind::Server,
                503 | 504 => ErrorKind::Unavailable,
                _ => panic!("unhandled corpus error status {status}"),
            });
            if status != 421 {
                assert_eq!(error.code, body["error"]["code"]);
                assert_eq!(error.message, format!("{} Fix: {}", body["error"]["message"].as_str().unwrap(), body["error"]["fix"].as_str().unwrap()));
                assert_eq!(error.fix.as_deref(), body["error"]["fix"].as_str());
                assert_eq!(error.request_id.as_deref(), Some("producer-conformance"));
            }
        }
    }
}
