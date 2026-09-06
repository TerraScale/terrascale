//! Webhook routes on the authenticated database API.
use super::*;
use crate::webhook_api::{self as webhook, TenantScope, WebhookService};

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
        Arc::new(webhook::AbuseGuardWebhookGuard::new(Arc::clone(&state.guards))),
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

fn json<T: serde::Serialize>(status: StatusCode, value: &T) -> Response {
    match serde_json::to_string(value) {
        Ok(body) => (status, [(CONTENT_TYPE, "application/json")], body).into_response(),
        Err(_) => unavailable(),
    }
}

async fn blocking(work: impl FnOnce() -> Response + Send + 'static) -> Response {
    tokio::task::spawn_blocking(work).await.unwrap_or_else(|_| unavailable())
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/webhooks", responses((status = 200, description = "Durable webhook endpoints", body = WebhookList), (status = 503, description = "Regional key provider unavailable")))]
pub(super) async fn list_webhooks(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
) -> Response {
    let (service, scope) = match service(&state, &name) {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.list(&scope) {
        Ok(webhooks) => json(StatusCode::OK, &WebhookList { webhooks }),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/webhooks", request_body = webhook::CreateWebhook, responses((status = 201, description = "Created durable webhook endpoint", body = webhook::WebhookEndpoint), (status = 503, description = "Regional key provider unavailable")))]
pub(super) async fn create_webhook(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
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
    let (service, scope) = match service(&state, &name) {
        Ok(value) => value,
        Err(response) => return response,
    };
    blocking(move || match service.create(&scope, &input) {
        Ok(endpoint) => json(StatusCode::CREATED, &endpoint),
        Err(error) => error.into_response(),
    })
    .await
}

#[utoipa::path(delete, path = "/api/v1/databases/{name}/webhooks/{id}", responses((status = 200, description = "Deleted webhook and signing secret", body = webhook::WebhookEndpoint)))]
pub(super) async fn delete_webhook(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
) -> Response {
    let (service, scope) = match service(&state, &name) {
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
    let (service, scope) = match service(&state, &name) {
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
    let (service, scope) = match service(&state, &name) {
        Ok(value) => value,
        Err(response) => return response,
    };
    match service.replay(&scope, &id, &delivery_id).await {
        Ok(record) => json(StatusCode::OK, &record),
        Err(error) => error.into_response(),
    }
}
