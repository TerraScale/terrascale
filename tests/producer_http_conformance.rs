//! Independent checks against the versioned producer HTTP corpus.

use async_trait::async_trait;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::VecDeque;
use std::future::Future;
use std::sync::{Arc, Mutex};
use terrascale::{
    generated_contract, ClientConfig, CreateDatabaseRequest, CreateExport, ErrorKind,
    HttpTransport, Method, OperationOptions, RegionCode, Request, Response, Result, ResumeExport,
    TerraScaleClient,
};
use terrascale::{BatchOperation, DatabaseScope, ListOptions, Query};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

async fn exercise_public_route<F, Fut>(operation: &Value, invoke: F)
where
    F: Fn(TerraScaleClient) -> Fut + Copy,
    Fut: Future<Output = Result<Value>>,
{
    let mut vectors = operation["responses"].as_array().unwrap().clone();
    vectors.extend(operation["errors"].as_array().unwrap().iter().cloned());
    for vector in vectors {
        let status = vector["status"].as_u64().unwrap() as u16;
        let mock = Mock::new(response(status, &vector));
        let client = TerraScaleClient::with_transport(
            ClientConfig::builder()
                .api_base_url("https://api.test")
                .api_key("conformance-key")
                .project_id("proj")
                .build()
                .unwrap(),
            mock.clone(),
        );
        let result = invoke(client).await;
        let requests = mock.requests.lock().unwrap();
        assert_eq!(
            requests.len(),
            1,
            "{} status {status} retried",
            operation["operationId"]
        );
        assert_eq!(
            requests[0].method.as_str(),
            operation["request"]["method"].as_str().unwrap()
        );
        assert_eq!(
            url_path_query(&requests[0].url),
            operation["request"]["path"].as_str().unwrap(),
            "{} request URL",
            operation["operationId"]
        );
        assert_eq!(
            requests[0]
                .headers
                .iter()
                .find(|(n, _)| n.eq_ignore_ascii_case("authorization"))
                .map(|(_, v)| v.as_str()),
            Some("Bearer conformance-key")
        );
        if (200..300).contains(&status) {
            let actual = result
                .unwrap_or_else(|e| panic!("{} status {status}: {e}", operation["operationId"]));
            assert_eq!(
                actual, vector["body"],
                "{} status {status} body",
                operation["operationId"]
            );
        } else {
            let error = result.expect_err("producer error accepted");
            assert_eq!(error.status, Some(status));
            let expected_kind = match status {
                400 | 422 => ErrorKind::Validation,
                401 => ErrorKind::Authentication,
                403 => ErrorKind::Authorization,
                404 => ErrorKind::NotFound,
                409 => ErrorKind::Conflict,
                421 => ErrorKind::RegionRedirect,
                429 => ErrorKind::RateLimit,
                500 => ErrorKind::Server,
                503 | 504 => ErrorKind::Unavailable,
                _ => panic!("unhandled producer status {status}"),
            };
            assert_eq!(error.kind, expected_kind);
            assert_eq!(error.code, vector["body"]["error"]["code"]);
            assert_eq!(
                error.message,
                format!(
                    "{} Fix: {}",
                    vector["body"]["error"]["message"].as_str().unwrap(),
                    vector["body"]["error"]["fix"].as_str().unwrap()
                )
            );
            assert_eq!(
                error.fix.as_deref(),
                vector["body"]["error"]["fix"].as_str()
            );
            assert_eq!(error.request_id.as_deref(), Some("producer-conformance"));
        }
    }
}

#[tokio::test]
async fn public_documents_sql_and_exports_consume_producer_vectors() {
    let corpus = corpus();
    let op = |id: &str| {
        corpus["operations"]
            .as_array()
            .unwrap()
            .iter()
            .find(|v| v["operationId"] == id)
            .unwrap()
            .clone()
    };
    let scope = DatabaseScope::new("demo");
    exercise_public_route(&op("get_document"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .get(&s, "orders", "order-1")
                .await
                .map(|v| v.body)
        }
    })
    .await;
    exercise_public_route(&op("list_documents"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .list(&s, "orders", ListOptions::default())
                .await
                .map(|v| serde_json::to_value(v).unwrap())
        }
    })
    .await;
    exercise_public_route(&op("document_history"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .history(&s, "orders", "order-1")
                .await
                .map(|v| json!({"history":v}))
        }
    })
    .await;
    exercise_public_route(&op("put_document"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .put(&s, "orders", "order-1", json!({"sample":null}))
                .await
                .map(|v| json!({"revision":v.revision}))
        }
    })
    .await;
    exercise_public_route(&op("patch_document"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .patch(&s, "orders", "order-1", json!({"sample":null}))
                .await
                .map(|v| json!({"revision":v.revision}))
        }
    })
    .await;
    exercise_public_route(&op("delete_document"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .delete(&s, "orders", "order-1")
                .await
                .map(|_| Value::Null)
        }
    })
    .await;
    exercise_public_route(&op("batch_documents"), |c| {
        let s = scope.clone();
        async move {
            c.documents()
                .batch(
                    &s,
                    "orders",
                    vec![BatchOperation::put("sample", Value::Null)],
                )
                .await
                .map(|v| serde_json::to_value(v).unwrap())
        }
    })
    .await;
    exercise_public_route(&op("execute_sql"), |c| {
        let s = scope.clone();
        async move {
            c.sql()
                .execute(&s, &Query::raw("SELECT 1", json!({})))
                .await
                .map(|v| serde_json::to_value(v).unwrap())
        }
    })
    .await;
    exercise_public_route(&op("get_export"), |c| async move {
        c.management()
            .exports()
            .get("demo", "sample")
            .await
            .map(|v| serde_json::to_value(v).unwrap())
    })
    .await;
    exercise_public_route(&op("list_exports"), |c| async move {
        c.management()
            .exports()
            .list("demo", None, None)
            .await
            .map(|v| serde_json::to_value(v).unwrap())
    })
    .await;
    exercise_public_route(&op("download_export"), |c| async move {
        c.management()
            .exports()
            .download("demo", "sample", "sample")
            .await
            .map(|v| Value::String(String::from_utf8(v.bytes.to_vec()).unwrap()))
    })
    .await;
    exercise_public_route(&op("create_export"), |c| async move {
        c.management()
            .exports()
            .create(
                "demo",
                CreateExport {
                    idempotency_key: "sample".into(),
                },
                None,
                OperationOptions::default(),
            )
            .await
            .map(|v| serde_json::to_value(v).unwrap())
    })
    .await;
    exercise_public_route(&op("cancel_export"), |c| async move {
        c.management()
            .exports()
            .cancel("demo", "sample", OperationOptions::default())
            .await
            .map(|v| serde_json::to_value(v).unwrap())
    })
    .await;
    exercise_public_route(&op("resume_export"), |c| async move {
        c.management()
            .exports()
            .resume(
                "demo",
                "sample",
                ResumeExport {
                    resume_cursor: "sample".into(),
                },
                OperationOptions::default(),
            )
            .await
            .map(|v| serde_json::to_value(v).unwrap())
    })
    .await;
}

impl Mock {
    fn new(response: Response) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(VecDeque::from([response])),
        })
    }

    fn queued(responses: Vec<Response>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into()),
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

fn response(status: u16, vector: &Value) -> Response {
    let media = vector["mediaType"].as_str().unwrap_or("application/json");
    let bytes = if media == "application/octet-stream" {
        vector["body"]
            .as_str()
            .unwrap_or_default()
            .as_bytes()
            .to_vec()
    } else {
        serde_json::to_vec(&vector["body"]).unwrap()
    };
    Response {
        status,
        headers: {
            let mut headers = vec![
                ("content-type".into(), media.into()),
                ("x-request-id".into(), "producer-conformance".into()),
            ];
            if media == "application/octet-stream" {
                headers.push((
                    "x-terrascale-artifact-length".into(),
                    bytes.len().to_string(),
                ));
                headers.push(("x-terrascale-artifact-checksum".into(), "0".repeat(64)));
            }
            headers
        },
        body: bytes.into(),
    }
}

fn corpus() -> Value {
    serde_json::from_str(include_str!("../contracts/v1/conformance/http.json")).unwrap()
}

fn url_path_query(url: &str) -> String {
    let parsed = reqwest::Url::parse(url).unwrap();
    let mut value = parsed.path().to_owned();
    if let Some(query) = parsed.query() {
        value.push('?');
        value.push_str(query);
    }
    value
}

fn concrete_path(template: &str) -> String {
    let database = if template.contains("/auth/") {
        "sample"
    } else {
        "demo"
    };
    template
        .replace("{name}", "demo")
        .replace("{tenant}", "sample")
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

#[tokio::test]
async fn producer_421_diagnostics_and_placement_repair_are_preserved() {
    let error_body = json!({"error":{"code":"misdirected_request","message":"conformance 421 response","fix":"follow the contract and retry when safe"}});
    let success_body = json!({"name":"sample","placement":"sample","residency":"sample","created_at":0,"deleted":false,"parent":null});
    let mock = Mock::queued(vec![
        Response {
            status: 421,
            headers: vec![
                ("content-type".into(), "application/json".into()),
                ("x-request-id".into(), "producer-conformance".into()),
                ("x-terrascale-placement".into(), "GRU".into()),
            ],
            body: serde_json::to_vec(&error_body).unwrap().into(),
        },
        response(
            200,
            &json!({"status":200,"mediaType":"application/json","body":success_body}),
        ),
    ]);
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("conformance-key")
            .project_id("proj")
            .build()
            .unwrap(),
        mock.clone(),
    );
    let error = client
        .management()
        .databases()
        .get("sample", None)
        .await
        .expect_err("421");
    assert_eq!(error.kind, ErrorKind::RegionRedirect);
    assert_eq!(error.status, Some(421));
    assert_eq!(error.code, "misdirected_request");
    assert_eq!(
        error.message,
        "conformance 421 response Fix: follow the contract and retry when safe"
    );
    assert_eq!(
        error.fix.as_deref(),
        Some("follow the contract and retry when safe")
    );
    assert_eq!(error.request_id.as_deref(), Some("producer-conformance"));
    assert_eq!(error.placement.as_deref(), Some("GRU"));
    client
        .management()
        .databases()
        .get("sample", None)
        .await
        .unwrap();
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    // A custom origin is never rewritten by the repaired pin; the placement
    // travels as the typed preferred-region header instead.
    assert!(requests[1].url.starts_with("https://api.test"));
    assert_eq!(
        requests[1]
            .headers
            .iter()
            .find(|(n, _)| n.eq_ignore_ascii_case("x-terrascale-preferred-region"))
            .map(|(_, v)| v.as_str()),
        Some("GRU")
    );
}

#[test]
fn producer_corpus_binds_to_pinned_openapi_and_exact_generated_inventory() {
    let corpus = corpus();
    let openapi = include_bytes!("../contracts/v1/openapi.json");
    let actual = format!("{:x}", Sha256::digest(openapi));
    assert_eq!(actual, corpus["openapiSha256"]);
    assert_eq!(
        openapi.len(),
        std::fs::metadata("contracts/v1/openapi.json")
            .unwrap()
            .len() as usize
    );

    let operations = corpus["operations"].as_array().unwrap();
    assert_eq!(operations.len(), generated_contract::ROUTES.len());
    for route in generated_contract::ROUTES {
        let operation = operations
            .iter()
            .find(|value| value["operationId"] == route.operation_id)
            .unwrap_or_else(|| panic!("missing producer operation {}", route.operation_id));
        let request = &operation["request"];
        assert_eq!(
            request["path"],
            concrete_path(route.path),
            "{} path",
            route.operation_id
        );
        assert_eq!(
            request["method"].as_str().unwrap(),
            match route.method {
                generated_contract::HttpMethod::Get => "GET",
                generated_contract::HttpMethod::Post => "POST",
                generated_contract::HttpMethod::Put => "PUT",
                generated_contract::HttpMethod::Patch => "PATCH",
                generated_contract::HttpMethod::Delete => "DELETE",
                _ => panic!("unsupported generated method"),
            },
            "{} method",
            route.operation_id
        );
    }
}

#[tokio::test]
async fn public_database_create_consumes_every_producer_response_and_error_once() {
    let corpus = corpus();
    let operation = corpus["operations"]
        .as_array()
        .unwrap()
        .iter()
        .find(|value| value["operationId"] == "create_database")
        .unwrap();
    let request_fixture = &operation["request"];
    assert_eq!(request_fixture["method"], "POST");
    assert_eq!(request_fixture["path"], "/api/v1/databases");

    let mut cases = Vec::new();
    cases.extend(
        operation["responses"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| (true, v)),
    );
    cases.extend(
        operation["errors"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| (false, v)),
    );
    assert_eq!(cases.len(), 12);

    for (success, vector) in cases {
        let status = vector["status"].as_u64().unwrap() as u16;
        let body = &vector["body"];
        let mock = Mock::new(response(status, vector));
        let client = TerraScaleClient::with_transport(
            ClientConfig::builder()
                .api_base_url("https://api.test")
                .api_key("conformance-key")
                .project_id("proj")
                .build()
                .unwrap(),
            mock.clone(),
        );
        let result = client
            .management()
            .databases()
            .create_with_placement(
                CreateDatabaseRequest::new("sample", vec![RegionCode::Gru])
                    .unwrap()
                    .operation_id("op")
                    .unwrap()
                    .residency("br-only")
                    .unwrap(),
                None,
                OperationOptions::default(),
            )
            .await;
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 1, "status {status} was retried");
        assert_eq!(requests[0].method, Method::Post);
        assert_eq!(requests[0].url, "https://api.test/api/v1/databases");
        assert_eq!(
            requests[0]
                .headers
                .iter()
                .find(|(n, _)| n.eq_ignore_ascii_case("authorization"))
                .map(|(_, v)| v.as_str()),
            Some("Bearer conformance-key")
        );
        let wire: Value = serde_json::from_slice(requests[0].body.as_ref().unwrap()).unwrap();
        assert_eq!(wire["name"], "sample");
        assert_eq!(wire["placement"], json!(["GRU"]));
        drop(requests);
        if success {
            assert!(result.is_ok(), "status {status}: {result:?}");
        } else {
            let error = result.expect_err("producer error accepted");
            assert_eq!(error.status, Some(status));
            assert_eq!(
                error.kind,
                match status {
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
                }
            );
            assert_eq!(error.code, body["error"]["code"]);
            assert_eq!(error.fix.as_deref(), body["error"]["fix"].as_str());
            assert_eq!(error.request_id.as_deref(), Some("producer-conformance"));
            assert_eq!(
                error.message,
                format!(
                    "{} Fix: {}",
                    body["error"]["message"].as_str().unwrap(),
                    body["error"]["fix"].as_str().unwrap()
                )
            );
        }
    }
}
