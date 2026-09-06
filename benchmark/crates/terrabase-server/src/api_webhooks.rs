//! Webhook routes on the authenticated database API.
use super::*;
use crate::webhook_api::{self as webhook, TenantScope, WebhookService};
use bytes::Bytes;

#[derive(serde::Serialize, utoipa::ToSchema)]
struct WebhookList {
    webhooks: Vec<webhook::WebhookEndpoint>,
}

fn unavailable() -> Response {
    (StatusCode::SERVICE_UNAVAILABLE, [(CONTENT_TYPE, "application/json")], r#"{"error":{"code":"webhook_unavailable","message":"authorized database key provider unavailable","fix":"restore the configured regional key provider"}}"#).into_response()
}

fn service(
    state: &ApiState,
    database: &str,
) -> Result<(Arc<WebhookService>, TenantScope), Response> {
    let host = document_host(state, database).map_err(IntoResponse::into_response)?;
    let keys = state.webhook_keys.clone().ok_or_else(unavailable)?;
    let owner = host.engine().database_owner();
    let scope = TenantScope::new(
        owner.tenant().to_text(),
        owner.project().to_text(),
        owner.database().to_text(),
    )
    .map_err(IntoResponse::into_response)?;
    let ports = webhook::WebhookPorts::native_https(
        Arc::new(webhook::EngineWebhookStorage::from_host(Arc::clone(&host))),
        Arc::new(webhook::EngineWebhookJournal::from_host(Arc::clone(&host))),
        Arc::new(webhook::EngineWebhookSecrets::from_host(host, keys)),
        Arc::new(webhook::AbuseGuardWebhookGuard::for_database(
            Arc::clone(&state.guards),
            database,
        )),
        tokio::runtime::Handle::current(),
    )
    .map_err(|_| unavailable())?;
    let service = WebhookService::from_ports(
        ports,
        Arc::new(webhook::SystemWebhookClock),
        state.region.as_str(),
    )
    .map_err(|_| unavailable())?;
    Ok((service, scope))
}

async fn resolve(
    state: Arc<ApiState>,
    name: String,
) -> Result<(Arc<WebhookService>, TenantScope), Response> {
    tokio::task::spawn_blocking(move || service(&state, &name))
        .await
        .unwrap_or_else(|_| Err(unavailable()))
}

fn json<T: serde::Serialize>(status: StatusCode, value: &T) -> Response {
    match serde_json::to_string(value) {
        Ok(body) => (status, [(CONTENT_TYPE, "application/json")], body).into_response(),
        Err(_) => unavailable(),
    }
}

fn secret_response(status: StatusCode, response: &webhook::WebhookSecretResponse) -> Response {
    let mut response = json(status, response);
    response
        .headers_mut()
        .insert(axum::http::header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/webhooks/{id}/rotate-secret", request_body = webhook::RotateWebhookSecret, responses((status = 200, description = "One-time rotated signing secret", body = webhook::WebhookSecretResponse)))]
pub(super) async fn rotate_secret(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
    body: Bytes,
) -> Response {
    if body.len() > webhook::MAX_WEBHOOK_BODY_BYTES {
        return StatusCode::PAYLOAD_TOO_LARGE.into_response();
    }
    let input = if body.is_empty() {
        webhook::RotateWebhookSecret { overlap_seconds: 300 }
    } else {
        match serde_json::from_slice::<webhook::RotateWebhookSecret>(&body) {
            Ok(input) => input,
            Err(_) => {
                return webhook::WebhookError::invalid_request("invalid rotation request")
                    .into_response();
            }
        }
    };
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.rotate_secret(&scope, &id, &input) {
        Ok(secret) => secret_response(StatusCode::OK, &secret),
        Err(error) => error.into_response(),
    })
    .await
}

async fn blocking(work: impl FnOnce() -> Response + Send + 'static) -> Response {
    tokio::task::spawn_blocking(work).await.unwrap_or_else(|_| unavailable())
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/webhooks", responses((status = 200, description = "Durable webhook endpoints", body = WebhookList), (status = 503, description = "Regional key provider unavailable")))]
pub(super) async fn list_webhooks(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
) -> Response {
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.list(&scope) {
        Ok(webhooks) => json(StatusCode::OK, &WebhookList { webhooks }),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/webhooks", request_body = webhook::CreateWebhook, responses((status = 201, description = "Created durable webhook endpoint", body = webhook::WebhookSecretResponse), (status = 503, description = "Regional key provider unavailable")))]
pub(super) async fn create_webhook(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    headers: axum::http::HeaderMap,
    body: Bytes,
) -> Response {
    if body.len() > webhook::MAX_WEBHOOK_BODY_BYTES {
        return StatusCode::PAYLOAD_TOO_LARGE.into_response();
    }
    let input = match serde_json::from_slice::<webhook::CreateWebhook>(&body) {
        Ok(input) => input,
        Err(_) => {
            return webhook::WebhookError::invalid_request("invalid webhook request")
                .into_response();
        }
    };
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let operation = match headers.get("idempotency-key") {
        Some(value) => match value.to_str() { Ok(value) => Some(value.to_owned()), Err(_) => return webhook::WebhookError::invalid_request("invalid webhook idempotency key").into_response() },
        None => None,
    };
    blocking(move || match service.create_with_secret(&scope, &input, operation.as_deref()) {
        Ok(endpoint) => secret_response(StatusCode::CREATED, &endpoint),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(delete, path = "/api/v1/databases/{name}/webhooks/{id}", responses((status = 200, description = "Deleted webhook and signing secret", body = webhook::WebhookEndpoint)))]
pub(super) async fn delete_webhook(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
) -> Response {
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.delete(&scope, &id) {
        Ok(endpoint) => json(StatusCode::OK, &endpoint),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/webhooks/{id}/deliveries", responses((status = 200, description = "Durable delivery attempts", body = webhook::DeliveryLog)))]
pub(super) async fn list_deliveries(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
) -> Response {
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.delivery_log(&scope, &id, webhook::MAX_JOURNAL_PAGE) {
        Ok(log) => json(StatusCode::OK, &log),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/webhooks/{id}/deliveries/{delivery_id}/replay", responses((status = 200, description = "Replayed durable delivery", body = webhook::DeliveryRecord)))]
pub(super) async fn replay_webhook(
    State(state): State<Arc<ApiState>>,
    Path((name, id, delivery_id)): Path<(String, String, String)>,
) -> Response {
    let (service, scope) = match resolve(state, name).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    match service.replay(&scope, &id, &delivery_id).await {
        Ok(record) => json(StatusCode::OK, &record),
        Err(error) => error.into_response(),
    }
}

/// Starts one serial database dispatcher. A weak state reference ends the loop
/// when the serving runtime drops its router; no request or subscriber starts a task.
pub(super) fn spawn_dispatcher(state: &Arc<ApiState>) -> Option<tokio::task::JoinHandle<()>> {
    if state.webhook_keys.is_none() {
        return None;
    }
    let state = Arc::downgrade(state);
    Some(tokio::spawn(async move {
        let mut interval = tokio::time::interval(std::time::Duration::from_secs(1));
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        let mut offset = 0;
        loop {
            interval.tick().await;
            let Some(state) = state.upgrade() else {
                break;
            };
            let Some(control) = state.control.clone() else {
                continue;
            };
            let region = state.region;
            let records = match tokio::task::spawn_blocking(move || control.list()).await {
                Ok(Ok(records)) => records
                    .into_iter()
                    .filter(|record| !record.deleted && record.placement.contains(&region))
                    .collect::<Vec<_>>(),
                _ => continue,
            };
            if records.is_empty() {
                continue;
            }
            // Eight databases and one journal page per database per tick.
            for index in 0..records.len().min(8) {
                let name = records[(offset + index) % records.len()].name.clone();
                let state = Arc::clone(&state);
                let resolved = tokio::task::spawn_blocking(move || {
                    let host = document_host(&state, &name).ok()?;
                    let storage = webhook::EngineWebhookStorage::from_host(host);
                    let (service, scope) = service(&state, &name).ok()?;
                    let cursor = storage.dispatch_cursor(&scope).ok()?;
                    Some((storage, service, scope, cursor))
                })
                .await;
                let Ok(Some((storage, service, scope, cursor))) = resolved else {
                    continue;
                };
                if let Ok(report) = service.dispatch(&scope, cursor).await {
                    let _ = tokio::task::spawn_blocking(move || {
                        storage.checkpoint_dispatch(&scope, cursor, report.cursor)
                    })
                    .await;
                }
            }
            offset = (offset + 8) % records.len();
        }
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::webhook_api::{SecretError, WebhookKeyLeases};
    use terrabase::encryption::{DatabaseKeyLease, DatabaseKeyVersion};

    struct RefusingKeys;
    impl WebhookKeyLeases for RefusingKeys {
        fn lease(
            &self,
            _: &TenantScope,
            _: &str,
            _: Option<DatabaseKeyVersion>,
        ) -> Result<DatabaseKeyLease, SecretError> {
            Err(SecretError::Unavailable)
        }
    }

    #[tokio::test]
    async fn served_http_lists_durable_endpoints_and_rejects_foreign_scope() {
        use crate::api_keys::{KeyStore, Scope, Verbs};
        use http_body_util::BodyExt;
        let mut state = super::super::tests::state_with_keys_and_control();
        let editable = Arc::get_mut(&mut state).expect("sole state");
        editable.webhook_keys = Some(Arc::new(RefusingKeys));
        editable.keys = Some(Arc::new(KeyStore::with_keys(&[
            ("tskey_test_abcdefghijklmnop", "read", Scope::database("hooks", Verbs::READ)),
            (
                "tskey_collection_abcdefghijklmnop",
                "collection",
                Scope::database_collection("hooks", "orders", Verbs::ALL),
            ),
        ])));
        let control = state.control.as_ref().expect("control");
        control.create("hooks", &[terrabase::regions::RegionCode::GRU], None).expect("database");
        control
            .create("foreign", &[terrabase::regions::RegionCode::GRU], None)
            .expect("foreign database");
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("listen");
        let address = listener.local_addr().expect("address");
        let app = super::super::router(state);
        let server = tokio::spawn(async move { axum::serve(listener, app).await.expect("serve") });
        let client: hyper_util::client::legacy::Client<_, http_body_util::Empty<Bytes>> =
            hyper_util::client::legacy::Client::builder(hyper_util::rt::TokioExecutor::new())
                .build_http();
        for (database, token, expected) in [
            ("hooks", Some("Bearer tskey_test_abcdefghijklmnop"), StatusCode::OK),
            ("foreign", Some("Bearer tskey_test_abcdefghijklmnop"), StatusCode::FORBIDDEN),
            ("hooks", None, StatusCode::UNAUTHORIZED),
            ("hooks", Some("Bearer tskey_collection_abcdefghijklmnop"), StatusCode::FORBIDDEN),
        ] {
            let mut request = axum::http::Request::get(format!(
                "http://{address}/api/v1/databases/{database}/webhooks"
            ));
            if let Some(token) = token {
                request = request.header("authorization", token);
            }
            let response = client
                .request(request.body(http_body_util::Empty::new()).expect("request"))
                .await
                .expect("response");
            assert_eq!(response.status(), expected);
            if expected == StatusCode::OK {
                let body = response.into_body().collect().await.expect("body").to_bytes();
                assert_eq!(
                    serde_json::from_slice::<serde_json::Value>(&body).expect("json"),
                    serde_json::json!({"webhooks":[]})
                );
            }
        }
        server.abort();
    }
}
