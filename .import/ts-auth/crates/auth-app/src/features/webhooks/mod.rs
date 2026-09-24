//! Durable outbound webhooks, dead-letter replay, and audit query verification.

use std::{
    future::Future,
    net::{IpAddr, Ipv4Addr, SocketAddr},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use auth_contracts::{
    AppError,
    models::{AuditEvent, WebhookDelivery, WebhookEndpoint},
};
use auth_store::{AuthStore, StoreError};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{delete, get, post},
};
use serde::Deserialize;
use serde_json::{Value, json};
use utoipa::openapi::{
    Info, OpenApi as OpenApiDoc,
    path::{HttpMethod, Operation, Paths},
};

use crate::{AppState, features::management};

const BODY_LIMIT: usize = 1024 * 1024;
const POLL_INTERVAL: Duration = Duration::from_secs(2);
const DELIVERY_LEASE_SECONDS: i64 = 60;
const MAX_DELIVERY_ATTEMPTS: i32 = 3;

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/v1/{slug}/webhooks",
            post(create_webhook).get(list_webhooks),
        )
        .route("/v1/{slug}/webhooks/dead-letter", get(list_dead_letters))
        .route(
            "/v1/{slug}/webhooks/dead-letter/{delivery_id}/replay",
            post(replay_dead_letter),
        )
        .route("/v1/{slug}/webhooks/{id}", delete(delete_webhook))
        .route("/v1/{slug}/webhooks/{id}/test", post(test_webhook))
        .route("/v1/{slug}/audit-events", get(list_audit_events))
        .route("/v1/{slug}/audit-events/verify", get(verify_audit_events))
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/v1/{slug}/webhooks",
            HttpMethod::Post,
            "Create a signed webhook endpoint",
        ),
        (
            "/v1/{slug}/webhooks",
            HttpMethod::Get,
            "List webhook endpoints",
        ),
        (
            "/v1/{slug}/webhooks/{id}",
            HttpMethod::Delete,
            "Delete a webhook endpoint",
        ),
        (
            "/v1/{slug}/webhooks/{id}/test",
            HttpMethod::Post,
            "Queue a test event",
        ),
        (
            "/v1/{slug}/webhooks/dead-letter",
            HttpMethod::Get,
            "List webhook dead letters",
        ),
        (
            "/v1/{slug}/webhooks/dead-letter/{delivery_id}/replay",
            HttpMethod::Post,
            "Replay a dead-letter delivery",
        ),
        (
            "/v1/{slug}/audit-events",
            HttpMethod::Get,
            "Query signed audit events",
        ),
        (
            "/v1/{slug}/audit-events/verify",
            HttpMethod::Get,
            "Verify audit event integrity and authenticity",
        ),
    ] {
        paths.add_path_operation(
            path,
            vec![method],
            Operation::builder()
                .tag("Webhooks and Audit")
                .summary(Some(summary))
                .build(),
        );
    }
    OpenApiDoc::new(
        Info::new("TerraScale Auth Webhooks and Audit API", "1.0.0"),
        paths,
    )
}

#[derive(Deserialize)]
struct CreateWebhookBody {
    url: Option<String>,
    events: Option<Vec<String>>,
}

async fn create_webhook(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let body: CreateWebhookBody = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(url) = body.url.filter(|value| valid_webhook_url(value)) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "url must be an absolute HTTPS URL (HTTP is limited to loopback development sinks)",
        ));
    };
    let Some(events) = body.events.filter(|items| {
        !items.is_empty() && items.len() <= 100 && items.iter().all(|item| valid_event_filter(item))
    }) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "events must contain 1 to 100 valid event names",
        ));
    };
    let secret = auth_security::tokens::random_token(32);
    let boxed = auth_security::secretbox::encrypt(&state.options.master_key, secret.as_bytes())
        .map_err(internal)?;
    let endpoint = WebhookEndpoint {
        id: format!("wh_{}", auth_security::tokens::random_token(20)),
        instance_id: context.instance.id.clone(),
        url,
        events,
        signing_secret_box: boxed,
        enabled: true,
        created_at: unix_now(),
    };
    if let Err(error) = state.store.create_webhook_endpoint(&endpoint).await {
        cleanup_unreturned_endpoint(&state.store, &endpoint).await;
        return Err(internal(error));
    }
    if let Err(error) = create_audit(
        &state.store,
        &context.instance.id,
        Some(context.api_key_id),
        "webhook.created",
        json!({"endpoint_id":endpoint.id}),
    )
    .await
    {
        cleanup_unreturned_endpoint(&state.store, &endpoint).await;
        return Err(error);
    }
    Ok((
        StatusCode::OK,
        Json(json!({
            "id": endpoint.id,
            "url": endpoint.url,
            "events": endpoint.events,
            "enabled": endpoint.enabled,
            "signing_secret": secret,
        })),
    )
        .into_response())
}

/// Remove the exact newly-created endpoint when audit persistence fails before
/// its one-time secret can be returned. A timeout may have committed despite
/// returning an error, so retry by readback and disable as a bounded fallback.
/// If storage commits only after every readback attempt fails, an operator must
/// list and delete the endpoint; there is no cross-document transaction here.
async fn cleanup_unreturned_endpoint(store: &AuthStore, created: &WebhookEndpoint) {
    for attempt in 0..8 {
        let versioned = match store
            .get_versioned::<WebhookEndpoint>("webhooks", &created.id)
            .await
        {
            Ok(value) => value,
            Err(error) => {
                tracing::warn!(
                    endpoint_id = created.id,
                    attempt,
                    ?error,
                    "webhook cleanup read failed"
                );
                continue;
            }
        };
        let Some((mut current, version)) = versioned else {
            return;
        };
        if current.instance_id != created.instance_id
            || current.signing_secret_box != created.signing_secret_box
            || current.url != created.url
        {
            tracing::error!(
                endpoint_id = created.id,
                "webhook cleanup refused to alter a different endpoint record"
            );
            return;
        }
        if !current.enabled {
            return;
        }
        match store
            .compare_and_delete("webhooks", &created.id, &version)
            .await
        {
            Ok(true) => return,
            Ok(false) => continue,
            Err(delete_error) => {
                tracing::warn!(
                    endpoint_id = created.id,
                    ?delete_error,
                    "webhook cleanup delete was ambiguous; attempting to disable endpoint"
                );
                current.enabled = false;
                match store
                    .compare_and_put("webhooks", &created.id, &current, &version)
                    .await
                {
                    Ok(true) => return,
                    Ok(false) => continue,
                    Err(disable_error) => tracing::warn!(
                        endpoint_id = created.id,
                        ?disable_error,
                        "webhook cleanup disable failed"
                    ),
                }
            }
        }
    }
    tracing::error!(
        endpoint_id = created.id,
        instance_id = created.instance_id,
        "could not confirm cleanup of webhook whose one-time secret was not returned; operator repair may be required"
    );
}

async fn list_webhooks(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let endpoints = state
        .store
        .list_webhook_endpoints(&context.instance.id)
        .await
        .map_err(internal)?;
    let items: Vec<Value> = endpoints
        .into_iter()
        .map(|endpoint| {
            json!({
                "id": endpoint.id,
                "url": endpoint.url,
                "events": endpoint.events,
                "enabled": endpoint.enabled,
                "created_at": endpoint.created_at,
            })
        })
        .collect();
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn delete_webhook(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(endpoint) = state
        .store
        .get_webhook_endpoint(&id)
        .await
        .map_err(internal)?
        .filter(|value| value.instance_id == context.instance.id)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such webhook endpoint",
        ));
    };
    state
        .store
        .delete_webhook_endpoint(&endpoint.id)
        .await
        .map_err(internal)?;
    create_audit(
        &state.store,
        &context.instance.id,
        Some(context.api_key_id),
        "webhook.deleted",
        json!({"endpoint_id":id}),
    )
    .await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

async fn test_webhook(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(endpoint) = state
        .store
        .get_webhook_endpoint(&id)
        .await
        .map_err(internal)?
        .filter(|value| value.instance_id == context.instance.id && value.enabled)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such enabled webhook endpoint",
        ));
    };
    let event = create_audit(
        &state.store,
        &context.instance.id,
        Some(context.api_key_id),
        "webhook.test",
        json!({"endpoint_id":endpoint.id}),
    )
    .await?;
    enqueue_delivery(&state.store, &endpoint, &event)
        .await
        .map_err(internal)?;
    Ok((StatusCode::OK, Json(json!({"queued":true}))).into_response())
}

async fn list_dead_letters(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let mut deliveries = state
        .store
        .list_webhook_deliveries(&context.instance.id)
        .await
        .map_err(internal)?;
    deliveries.retain(|item| item.status == "dead");
    deliveries.sort_by_key(|item| std::cmp::Reverse(item.updated_at));
    let items: Vec<Value> = deliveries
        .into_iter()
        .take(100)
        .map(|item| {
            json!({
                "id": item.id,
                "endpoint_id": item.endpoint_id,
                "event_id": item.event_id,
                "attempts": item.attempts,
                "last_error": item.last_error,
                "updated_at": item.updated_at,
            })
        })
        .collect();
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn replay_dead_letter(
    State(state): State<AppState>,
    Path((slug, delivery_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(delivery) = state
        .store
        .get_webhook_delivery(&delivery_id)
        .await
        .map_err(internal)?
        .filter(|value| value.instance_id == context.instance.id && value.status == "dead")
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such dead-letter delivery",
        ));
    };
    let now = unix_now();
    let changed = state
        .store
        .try_update_webhook_delivery(&delivery.id, |current| {
            if current.instance_id != context.instance.id || current.status != "dead" {
                return false;
            }
            current.status = "pending".into();
            current.attempts = 0;
            current.last_error = None;
            current.next_attempt_at = Some(now);
            current.lease_until = None;
            current.lease_token = None;
            current.updated_at = now;
            true
        })
        .await
        .map_err(internal)?;
    if !changed {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "delivery_changed",
            "the dead-letter delivery changed before replay",
        ));
    }
    deliver_one(&state, &delivery.id).await?;
    Ok((StatusCode::OK, Json(json!({"replayed":true}))).into_response())
}

async fn list_audit_events(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Query(query): Query<AuditQuery>,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let limit = match parse_limit(query.limit.as_deref(), 50, 100) {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let events = state
        .store
        .list_audit_events(&context.instance.id, limit)
        .await
        .map_err(internal)?;
    let result: Vec<Value> = events.into_iter().map(audit_json).collect();
    Ok((StatusCode::OK, Json(result)).into_response())
}

async fn verify_audit_events(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Query(query): Query<AuditQuery>,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let limit = match parse_limit(query.limit.as_deref(), 100, 1_000) {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let predicate =
        auth_store::Predicate::equal("instanceId", &context.instance.id).map_err(internal)?;
    let events: Vec<AuditEvent> = state
        .store
        .query_all("audit_events", &[predicate], 1_000)
        .await
        .map_err(internal)?;
    let mut events: Vec<_> = events.into_iter().collect();
    events.sort_by_key(|event| std::cmp::Reverse((event.created_at, event.id.clone())));
    events.truncate(limit);
    let mut invalid_integrity = Vec::new();
    let mut invalid_authenticity = Vec::new();
    let mut valid_integrity_count = 0;
    let mut valid_authenticity_count = 0;
    for event in &events {
        if !state.store.has_valid_audit_integrity(event) {
            invalid_integrity.push(event.id.clone());
            invalid_authenticity.push(event.id.clone());
        } else {
            valid_integrity_count += 1;
            if state.store.has_authentic_audit_integrity(event) {
                valid_authenticity_count += 1;
            } else {
                invalid_authenticity.push(event.id.clone());
            }
        }
    }
    let is_valid = invalid_integrity.is_empty();
    let is_authentic = is_valid && invalid_authenticity.is_empty();
    Ok((
        StatusCode::OK,
        Json(json!({
            "event_count": events.len(),
            "valid_integrity_count": valid_integrity_count,
            "valid_authenticity_count": valid_authenticity_count,
            "invalid_integrity_ids": invalid_integrity,
            "invalid_authenticity_ids": invalid_authenticity,
            "is_valid": is_valid,
            "is_authentic": is_authentic,
        })),
    )
        .into_response())
}

#[derive(Deserialize)]
struct AuditQuery {
    limit: Option<String>,
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn body_json<T: for<'de> Deserialize<'de>>(request: Request) -> Result<T, Response> {
    let is_json = request
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.split(';').next())
        .is_some_and(|value| value.trim().eq_ignore_ascii_case("application/json"));
    if !is_json {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_json",
            "the request body must use application/json",
        ));
    }
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "request body is required and must be at most 1 MiB",
            )
        })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        api_error(
            StatusCode::BAD_REQUEST,
            "invalid_json",
            "the request body is not valid JSON",
        )
    })
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn resolve(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
) -> Result<management::InstanceContext, Response> {
    management::resolve_instance(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)
}

async fn create_audit(
    store: &AuthStore,
    instance_id: &str,
    actor_id: Option<String>,
    event_type: &str,
    data: Value,
) -> Result<AuditEvent, AppError> {
    let event = AuditEvent {
        id: format!("evt_{}", auth_security::tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        r#type: event_type.to_owned(),
        actor_id,
        data: data.as_object().cloned().unwrap_or_default(),
        created_at: unix_now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    store.create_audit_event(&event).await.map_err(internal)
}

fn audit_json(event: AuditEvent) -> Value {
    let mut value = json!({
        "id": event.id,
        "instance_id": event.instance_id,
        "type": event.r#type,
        "data": event.data,
        "created_at": event.created_at,
        "integrity_version": event.integrity_version,
        "integrity_hash": event.integrity_hash,
        "integrity_signature": event.integrity_signature,
        "integrity_key_fingerprint": event.integrity_key_fingerprint,
    });
    if let Some(actor) = event.actor_id {
        value["actor_id"] = Value::String(actor);
    }
    value
}

async fn dispatch_instance(state: &AppState, instance_id: &str) -> Result<(), StoreError> {
    let discovery_endpoints = state.store.list_webhook_endpoints(instance_id).await?;
    // A new endpoint never receives events created before its start time.
    // Querying from the oldest enabled endpoint bounds first-run work even when
    // the tenant has a large audit backlog from before it configured webhooks.
    let earliest_endpoint_start = discovery_endpoints
        .iter()
        .filter(|endpoint| endpoint.enabled)
        .map(|endpoint| endpoint.created_at)
        .min();
    let events = match earliest_endpoint_start {
        Some(started_at) => {
            state
                .store
                .list_pending_audit_events_since(instance_id, started_at)
                .await?
        }
        None => Vec::new(),
    };
    // An endpoint may become eligible after the discovery snapshot but before the
    // pending-event query. Refresh after enumerating events so a marker is never
    // completed using an endpoint set known to predate those events.
    let endpoints = if events.is_empty() {
        discovery_endpoints
    } else {
        state.store.list_webhook_endpoints(instance_id).await?
    };
    for event in &events {
        for endpoint in endpoints
            .iter()
            .filter(|endpoint| accepts_audit_event(endpoint, event))
        {
            enqueue_delivery(&state.store, endpoint, event).await?;
        }
        if !state.store.complete_audit_event_dispatch(&event.id).await? {
            return Err(StoreError::InvalidConfiguration(
                "pending audit event disappeared during webhook fanout".into(),
            ));
        }
    }
    let deliveries = state.store.list_webhook_deliveries(instance_id).await?;
    for delivery in deliveries.into_iter().filter(|item| {
        item.status == "pending" && item.next_attempt_at.is_none_or(|due| due <= unix_now())
    }) {
        if let Err(error) = deliver_one(state, &delivery.id).await {
            tracing::warn!(
                instance_id,
                delivery_id = delivery.id,
                ?error,
                "webhook delivery failed to settle"
            );
        }
    }
    Ok(())
}

fn accepts_audit_event(endpoint: &WebhookEndpoint, event: &AuditEvent) -> bool {
    endpoint.enabled
        && event.created_at >= endpoint.created_at
        && matches(&endpoint.events, &event.r#type)
}

async fn enqueue_delivery(
    store: &AuthStore,
    endpoint: &WebhookEndpoint,
    event: &AuditEvent,
) -> Result<(), StoreError> {
    let id = delivery_id(&endpoint.id, &event.id);
    let delivery = WebhookDelivery {
        id: id.clone(),
        instance_id: endpoint.instance_id.clone(),
        endpoint_id: endpoint.id.clone(),
        event_id: event.id.clone(),
        status: "pending".into(),
        attempts: 0,
        last_error: None,
        updated_at: unix_now(),
        next_attempt_at: Some(unix_now()),
        lease_until: None,
        lease_token: None,
    };
    match store.create("webhook_deliveries", &id, &delivery).await {
        Ok(()) => Ok(()),
        Err(error) if error.is_conflict() => Ok(()),
        Err(error) => Err(error),
    }
}

async fn deliver_one(state: &AppState, id: &str) -> Result<(), AppError> {
    let Some(delivery) = state
        .store
        .try_claim_webhook_delivery(id, unix_now(), DELIVERY_LEASE_SECONDS)
        .await
        .map_err(internal)?
    else {
        return Ok(());
    };
    let Some(endpoint) = state
        .store
        .get_webhook_endpoint(&delivery.endpoint_id)
        .await
        .map_err(internal)?
        .filter(|value| value.instance_id == delivery.instance_id && value.enabled)
    else {
        update_delivery(state, &delivery, "dead", Some("endpoint_unavailable"), None).await?;
        return Ok(());
    };
    let Some(event) = state
        .store
        .get_audit_event(&delivery.event_id)
        .await
        .map_err(internal)?
        .filter(|value| value.instance_id == delivery.instance_id)
    else {
        update_delivery(state, &delivery, "dead", Some("event_unavailable"), None).await?;
        return Ok(());
    };
    let secret = match auth_security::secretbox::decrypt(
        &state.options.master_key,
        &endpoint.signing_secret_box,
    ) {
        Ok(value) => value,
        Err(error) => {
            tracing::error!(
                endpoint_id = endpoint.id,
                ?error,
                "webhook secret could not be decrypted"
            );
            update_delivery(state, &delivery, "dead", Some("secret_unavailable"), None).await?;
            return Ok(());
        }
    };
    let payload = match serde_json::to_vec(&json!({
        "id": event.id,
        "type": event.r#type,
        "created_at": event.created_at,
        "data": event.data,
    })) {
        Ok(value) => value,
        Err(error) => {
            tracing::error!(
                event_id = event.id,
                ?error,
                "webhook payload could not be serialized"
            );
            settle_failure(state, &delivery, "serialization_error".to_owned()).await?;
            return Ok(());
        }
    };
    let timestamp = unix_now().to_string();
    let mut signed = timestamp.as_bytes().to_vec();
    signed.push(b'.');
    signed.extend_from_slice(&payload);
    let signature = hex(&auth_security::tokens::hmac_sha256(&secret, &signed));
    let event_header = match HeaderValue::from_str(&event.r#type) {
        Ok(value) => value,
        Err(error) => {
            tracing::error!(
                event_id = event.id,
                ?error,
                "invalid event type in webhook payload"
            );
            update_delivery(state, &delivery, "dead", Some("invalid_event_type"), None).await?;
            return Ok(());
        }
    };
    let client = match webhook_client(&endpoint.url).await {
        Ok(value) => value,
        Err(reason) => {
            settle_failure(state, &delivery, reason.to_owned()).await?;
            return Ok(());
        }
    };
    let response = client
        .post(&endpoint.url)
        .timeout(Duration::from_secs(10))
        .header("content-type", "application/json")
        .header("X-TsAuth-Event", event_header)
        .header("X-TsAuth-Timestamp", timestamp)
        .header("X-TsAuth-Signature", format!("sha256={signature}"))
        .body(payload)
        .send()
        .await;
    match response {
        Ok(response) if response.status().is_success() => {
            update_delivery(state, &delivery, "succeeded", None, None).await
        }
        Ok(response) => {
            settle_failure(
                state,
                &delivery,
                format!("HTTP {}", response.status().as_u16()),
            )
            .await
        }
        Err(error) => {
            let category = if error.is_timeout() {
                "timeout"
            } else {
                "network_error"
            };
            settle_failure(state, &delivery, category.to_owned()).await
        }
    }
}

/// Resolve and pin webhook destinations before connecting so DNS rebinding cannot turn an
/// accepted public HTTPS URL into access to loopback or private service addresses.
async fn webhook_client(raw_url: &str) -> Result<reqwest::Client, &'static str> {
    let url = url::Url::parse(raw_url).map_err(|_| "invalid_url")?;
    let host = url.host().ok_or("invalid_url")?;
    let port = url.port_or_known_default().ok_or("invalid_url")?;
    if !url.username().is_empty() || url.password().is_some() || url.fragment().is_some() {
        return Err("invalid_url");
    }
    if url.scheme() != "https" {
        return Err("unsafe_scheme");
    }
    let (addresses, resolve_host): (Vec<SocketAddr>, Option<String>) = match host {
        url::Host::Domain(domain) => {
            if domain == "localhost" || domain.ends_with(".localhost") {
                return Err("unsafe_destination");
            }
            let addresses = tokio::net::lookup_host((domain, port))
                .await
                .map_err(|_| "dns_lookup_failed")?
                .collect();
            (addresses, Some(domain.to_owned()))
        }
        url::Host::Ipv4(address) => (vec![SocketAddr::new(address.into(), port)], None),
        url::Host::Ipv6(address) => (vec![SocketAddr::new(address.into(), port)], None),
    };
    if addresses.is_empty() {
        return Err("dns_lookup_failed");
    }
    if addresses
        .iter()
        .any(|address| !public_destination(address.ip()))
    {
        return Err("unsafe_destination");
    }
    let selected = addresses[0];
    let mut builder = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .no_proxy()
        .connect_timeout(Duration::from_secs(3))
        .timeout(Duration::from_secs(10));
    if let Some(host) = resolve_host {
        builder = builder.resolve(&host, selected);
    }
    builder.build().map_err(|_| "client_configuration_error")
}

fn public_destination(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(address) => {
            !address.is_loopback()
                && !address.is_private()
                && !address.is_link_local()
                && !address.is_broadcast()
                && !address.is_unspecified()
                && !address.is_multicast()
                && address.octets()[0] != 0
                && !is_special_ipv4(address)
        }
        IpAddr::V6(address) => {
            let segments = address.segments();
            address.to_ipv4_mapped().is_none()
                && (segments[0] & 0xe000) == 0x2000
                && segments[0] != 0x2002
                && !(segments[0] == 0x2001 && (segments[1] <= 0x01ff || segments[1] == 0x0db8))
        }
    }
}

fn is_special_ipv4(address: Ipv4Addr) -> bool {
    let [first, second, third, _] = address.octets();
    (first == 100 && (64..=127).contains(&second))
        || (first == 192 && second == 0 && third == 0)
        || (first == 192 && second == 0 && third == 2)
        || (first == 198 && matches!(second, 18 | 19))
        || (first == 198 && second == 51 && third == 100)
        || (first == 203 && second == 0 && third == 113)
        || first >= 224
}

async fn settle_failure(
    state: &AppState,
    delivery: &WebhookDelivery,
    error: String,
) -> Result<(), AppError> {
    let terminal = delivery.attempts >= MAX_DELIVERY_ATTEMPTS;
    let retry_at = if terminal {
        None
    } else {
        let delay = if delivery.attempts <= 1 { 1 } else { 4 };
        Some(unix_now().saturating_add(delay))
    };
    update_delivery(
        state,
        delivery,
        if terminal { "dead" } else { "pending" },
        Some(&error),
        retry_at,
    )
    .await
}

async fn update_delivery(
    state: &AppState,
    claimed: &WebhookDelivery,
    status: &str,
    error: Option<&str>,
    next_attempt: Option<i64>,
) -> Result<(), AppError> {
    let token = claimed.lease_token.clone();
    let now = unix_now();
    let updated = state
        .store
        .try_update_webhook_delivery(&claimed.id, |current| {
            if current.status != "pending" || current.lease_token != token {
                return false;
            }
            current.status = status.to_owned();
            current.last_error = error.map(str::to_owned);
            current.next_attempt_at = next_attempt;
            current.lease_until = None;
            current.lease_token = None;
            current.updated_at = now;
            true
        })
        .await
        .map_err(internal)?;
    if !updated {
        tracing::warn!(
            delivery_id = claimed.id,
            "webhook result lost its delivery lease"
        );
    }
    Ok(())
}

fn valid_webhook_url(value: &str) -> bool {
    let Ok(url) = url::Url::parse(value) else {
        return false;
    };
    if !url.username().is_empty() || url.password().is_some() || url.fragment().is_some() {
        return false;
    }
    if url.scheme() != "https" {
        return false;
    }
    let Some(host) = url.host() else {
        return false;
    };
    match host {
        url::Host::Domain(domain) => domain != "localhost" && !domain.ends_with(".localhost"),
        url::Host::Ipv4(address) => public_destination(address.into()),
        url::Host::Ipv6(address) => public_destination(address.into()),
    }
}

fn valid_event_filter(value: &str) -> bool {
    if value.is_empty() || value.len() > 128 {
        return false;
    }
    if value == "*" {
        return true;
    }
    let base = value.strip_suffix(".*").unwrap_or(value);
    !base.is_empty()
        && base
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

fn matches(filters: &[String], event_type: &str) -> bool {
    filters.iter().any(|filter| {
        filter == "*"
            || filter == event_type
            || filter.strip_suffix(".*").is_some_and(|prefix| {
                event_type.starts_with(prefix)
                    && event_type.as_bytes().get(prefix.len()) == Some(&b'.')
            })
    })
}

fn delivery_id(endpoint_id: &str, event_id: &str) -> String {
    let hash = auth_security::tokens::sha256_hex(&format!("{endpoint_id}:{event_id}"));
    format!("whd_{hash}")
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn parse_limit(raw: Option<&str>, default: usize, max: usize) -> Result<usize, Response> {
    match raw {
        None => Ok(default),
        Some(raw) => raw
            .parse::<usize>()
            .ok()
            .filter(|value| (1..=max).contains(value))
            .ok_or_else(|| {
                api_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_request",
                    "limit is outside the supported range",
                )
            }),
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "webhook or audit operation failed");
    AppError::internal()
}

pub async fn run_dispatcher<F>(state: AppState, shutdown: F)
where
    F: Future<Output = ()> + Send + 'static,
{
    let mut shutdown = Box::pin(shutdown);
    let mut interval = tokio::time::interval(POLL_INTERVAL);
    interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    loop {
        tokio::select! {
            () = &mut shutdown => break,
            _ = interval.tick() => {
                match state.store.list_webhook_instance_ids().await {
                    Ok(instances) => {
                        for instance_id in instances {
                            if let Err(error) = dispatch_instance(&state, &instance_id).await {
                                tracing::warn!(instance_id, ?error, "webhook dispatch tick failed");
                            }
                        }
                    }
                    Err(error) => tracing::warn!(?error, "webhook dispatch discovery failed"),
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hosted_webhooks_require_https_and_public_literal_addresses() {
        assert!(!valid_webhook_url("http://localhost:8080/hook"));
        assert!(!valid_webhook_url("https://localhost/hook"));
        assert!(!valid_webhook_url("https://127.0.0.1/hook"));
        assert!(!valid_webhook_url("https://[::1]/hook"));
        assert!(!valid_webhook_url("https://[fc00::1]/hook"));
        assert!(!valid_webhook_url("https://[fec0::1]/hook"));
        assert!(!valid_webhook_url("https://[2002::1]/hook"));
        assert!(!valid_webhook_url("https://[2001::1]/hook"));
        assert!(!valid_webhook_url("https://[::ffff:127.0.0.1]/hook"));
        assert!(valid_webhook_url("https://[2001:4860:4860::8888]/hook"));
        assert!(valid_webhook_url("https://hooks.example.test/endpoint"));

        assert!(!public_destination("127.0.0.1".parse().unwrap()));
        assert!(!public_destination("10.0.0.1".parse().unwrap()));
        assert!(public_destination("8.8.8.8".parse().unwrap()));
    }

    #[test]
    fn delivery_id_is_stable_for_endpoint_event_pair() {
        assert_eq!(delivery_id("wh_1", "evt_1"), delivery_id("wh_1", "evt_1"));
        assert_ne!(delivery_id("wh_1", "evt_1"), delivery_id("wh_2", "evt_1"));
        assert_ne!(delivery_id("wh_1", "evt_1"), delivery_id("wh_1", "evt_2"));
    }

    #[test]
    fn endpoint_fanout_preserves_start_time_and_event_filters() {
        let endpoint = WebhookEndpoint {
            id: "wh_test".into(),
            instance_id: "ins_test".into(),
            url: "https://hooks.example.test".into(),
            events: vec!["user.*".into()],
            signing_secret_box: "sealed".into(),
            enabled: true,
            created_at: 100,
        };
        let mut event = AuditEvent {
            id: "evt_test".into(),
            instance_id: "ins_test".into(),
            r#type: "user.created".into(),
            actor_id: None,
            data: Default::default(),
            created_at: 99,
            integrity_version: None,
            integrity_hash: None,
            integrity_signature: None,
            integrity_key_fingerprint: None,
        };

        assert!(!accepts_audit_event(&endpoint, &event));
        event.created_at = 100;
        assert!(accepts_audit_event(&endpoint, &event));
        event.r#type = "billing.subscription_updated".into();
        assert!(!accepts_audit_event(&endpoint, &event));
    }
}
