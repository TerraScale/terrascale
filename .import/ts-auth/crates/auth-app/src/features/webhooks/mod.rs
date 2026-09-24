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
    Components, Content, Info, OpenApi as OpenApiDoc, Ref, RefOr, Required, Schema,
    path::{HttpMethod, Operation, Parameter, ParameterIn, Paths},
    response::Response as OpenApiResponse,
};

use crate::{AppState, features::management};

const BODY_LIMIT: usize = 1024 * 1024;
const POLL_INTERVAL: Duration = Duration::from_secs(2);
const DELIVERY_LEASE_SECONDS: i64 = 60;
const MAX_DELIVERY_ATTEMPTS: i32 = 3;
const MAX_CURSOR_LENGTH: usize = 4_096;

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
    let mut components = Components::new();
    add_openapi_schema(
        &mut components,
        "WebhookAuditEvent",
        json!({
            "type":"object",
            "properties":{
                "id":{"type":"string"},
                "instance_id":{"type":"string"},
                "type":{"type":"string"},
                "actor_id":{"type":"string"},
                "data":{"type":"object","additionalProperties":true},
                "created_at":{"type":"integer","format":"int64"},
                "integrity_version":{"type":["integer","null"]},
                "integrity_hash":{"type":["string","null"]},
                "integrity_signature":{"type":["string","null"]},
                "integrity_key_fingerprint":{"type":["string","null"]}
            },
            "required":["id","instance_id","type","data","created_at"]
        }),
    );
    add_openapi_schema(
        &mut components,
        "WebhookAuditEventPage",
        json!({
            "type":"object",
            "properties":{
                "events":{"type":"array","items":{"$ref":"#/components/schemas/WebhookAuditEvent"}},
                "limit":{"type":"integer","minimum":1,"maximum":100},
                "next_cursor":{"type":["string","null"]},
                "has_more":{"type":"boolean"}
            },
            "required":["events","limit","next_cursor","has_more"]
        }),
    );
    add_openapi_schema(
        &mut components,
        "WebhookAuditVerificationPage",
        json!({
            "type":"object",
            "properties":{
                "checked_scope":{"type":"string","enum":["page"]},
                "checked_count":{"type":"integer","minimum":0},
                "checked_event_ids":{"type":"array","items":{"type":"string"}},
                "event_count":{"type":"integer","minimum":0,"description":"Compatibility alias for checked_count; counts only this page."},
                "valid_integrity_count":{"type":"integer","minimum":0},
                "valid_authenticity_count":{"type":"integer","minimum":0},
                "invalid_integrity_ids":{"type":"array","items":{"type":"string"}},
                "invalid_authenticity_ids":{"type":"array","items":{"type":"string"}},
                "is_valid":{"type":"boolean","description":"Integrity result for checked_event_ids only."},
                "is_authentic":{"type":"boolean","description":"Integrity and signature result for checked_event_ids only."},
                "limit":{"type":"integer","minimum":1,"maximum":1000},
                "next_cursor":{"type":["string","null"]},
                "has_more":{"type":"boolean"}
            },
            "required":["checked_scope","checked_count","checked_event_ids","event_count","valid_integrity_count","valid_authenticity_count","invalid_integrity_ids","invalid_authenticity_ids","is_valid","is_authentic","limit","next_cursor","has_more"]
        }),
    );
    add_openapi_schema(
        &mut components,
        "WebhookDeadLetterPage",
        json!({
            "type":"object",
            "properties":{
                "deliveries":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"endpoint_id":{"type":"string"},"event_id":{"type":"string"},"attempts":{"type":"integer"},"last_error":{"type":["string","null"]},"updated_at":{"type":"integer","format":"int64"}},"required":["id","endpoint_id","event_id","attempts","updated_at"]}},
                "limit":{"type":"integer","minimum":1,"maximum":100},
                "next_cursor":{"type":["string","null"]},
                "has_more":{"type":"boolean"}
            },
            "required":["deliveries","limit","next_cursor","has_more"]
        }),
    );
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
        let mut operation = Operation::builder()
            .tag("Webhooks and Audit")
            .summary(Some(summary));
        if matches!(
            (path, method.clone()),
            ("/v1/{slug}/webhooks/dead-letter", HttpMethod::Get)
                | ("/v1/{slug}/audit-events", HttpMethod::Get)
                | ("/v1/{slug}/audit-events/verify", HttpMethod::Get)
        ) {
            let max_limit = if path.ends_with("/verify") {
                1_000
            } else {
                100
            };
            operation = operation
                .parameter(query_limit_parameter(max_limit))
                .parameter(query_cursor_parameter());
        }
        match (path, method.clone()) {
            ("/v1/{slug}/webhooks/dead-letter", HttpMethod::Get) => {
                operation = operation.response(
                    "200",
                    json_openapi_response(
                        "One bounded page of dead-letter deliveries in provider cursor order.",
                        "WebhookDeadLetterPage",
                    ),
                );
            }
            ("/v1/{slug}/audit-events", HttpMethod::Get) => {
                operation = operation
                    .description(Some("Returns at most `limit` events in the storage provider's cursor order. Ordering is not guaranteed to be chronological. Use `next_cursor` to request the next page."))
                    .response(
                        "200",
                        json_openapi_response(
                            "One bounded page of audit events in provider cursor order.",
                            "WebhookAuditEventPage",
                        ),
                    );
            }
            ("/v1/{slug}/audit-events/verify", HttpMethod::Get) => {
                operation = operation
                    .description(Some("Verifies only the events returned in this cursor page. `is_valid` and `is_authentic` apply to `checked_event_ids` only; this result does not establish global completeness or detect deleted or omitted events. Use `next_cursor` to verify another page."))
                    .response(
                        "200",
                        json_openapi_response(
                            "Integrity and authenticity results for one bounded cursor page only.",
                            "WebhookAuditVerificationPage",
                        ),
                    );
            }
            ("/v1/{slug}/webhooks/dead-letter/{delivery_id}/replay", HttpMethod::Post) => {
                operation = operation.description(Some("Replays the delivery synchronously. The request may remain open during network I/O; the sender timeout is 10 seconds total, including connect time."));
            }
            _ => {}
        }
        paths.add_path_operation(path, vec![method], operation.build());
    }
    let mut document = OpenApiDoc::new(
        Info::new("TerraScale Auth Webhooks and Audit API", "1.0.0"),
        paths,
    );
    document.components = Some(components);
    document
}

fn add_openapi_schema(components: &mut Components, name: &str, value: Value) {
    let schema: Schema =
        serde_json::from_value(value).expect("webhook OpenAPI schema definitions should be valid");
    components.schemas.insert(name.to_owned(), RefOr::T(schema));
}

fn query_limit_parameter(maximum: usize) -> Parameter {
    let mut parameter = Parameter::new("limit");
    parameter.parameter_in = ParameterIn::Query;
    parameter.required = Required::False;
    parameter.description = Some(format!("Page size, from 1 through {maximum}."));
    parameter.schema = Some(
        serde_json::from_value::<Schema>(json!({"type":"integer","minimum":1,"maximum":maximum}))
            .expect("limit parameter schema should be valid")
            .into(),
    );
    parameter
}

fn query_cursor_parameter() -> Parameter {
    let mut parameter = Parameter::new("after");
    parameter.parameter_in = ParameterIn::Query;
    parameter.required = Required::False;
    parameter.description = Some(
        "Opaque provider cursor returned as next_cursor. Omit it for the first page; do not decode or modify it."
            .into(),
    );
    parameter.schema = Some(
        serde_json::from_value::<Schema>(json!({"type":"string","maxLength":MAX_CURSOR_LENGTH}))
            .expect("cursor parameter schema should be valid")
            .into(),
    );
    parameter
}

fn json_openapi_response(description: &str, schema: &str) -> OpenApiResponse {
    let mut response = OpenApiResponse::new(description);
    response.content.insert(
        "application/json".into(),
        Content::new(Some(Ref::from_schema_name(schema))),
    );
    response
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
            "url must be an absolute HTTPS URL with a public destination",
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
    if create_audit(
        &state.store,
        &context.instance.id,
        Some(context.api_key_id),
        "webhook.deleted",
        json!({"endpoint_id":id}),
    )
    .await
    .is_err()
    {
        tracing::warn!(
            endpoint_id = id,
            instance_id = context.instance.id,
            "webhook endpoint deletion committed but its audit event was not recorded"
        );
    }
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
    Query(query): Query<AuditQuery>,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let limit = match parse_limit(query.limit.as_deref(), 100, 100) {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    if let Err(error) = validate_cursor(query.after.as_deref()) {
        return Ok(cursor_error_response(error));
    }
    let (deliveries, next_cursor) = state
        .store
        .list_webhook_deliveries_page(
            &context.instance.id,
            "dead",
            limit as u32,
            query.after.as_deref(),
        )
        .await
        .map_err(internal)?;
    let items: Vec<Value> = deliveries
        .into_iter()
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
    let has_more = next_cursor.is_some();
    Ok((
        StatusCode::OK,
        Json(json!({
            "deliveries": items,
            "limit": limit,
            "next_cursor": next_cursor,
            "has_more": has_more,
        })),
    )
        .into_response())
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
    if let Err(error) = validate_cursor(query.after.as_deref()) {
        return Ok(cursor_error_response(error));
    }
    let (events, next_cursor) = state
        .store
        .list_audit_events_page(&context.instance.id, limit as u32, query.after.as_deref())
        .await
        .map_err(internal)?;
    let result: Vec<Value> = events.into_iter().map(audit_json).collect();
    let has_more = next_cursor.is_some();
    Ok((
        StatusCode::OK,
        Json(json!({
            "events": result,
            "limit": limit,
            "next_cursor": next_cursor,
            "has_more": has_more,
        })),
    )
        .into_response())
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
    if let Err(error) = validate_cursor(query.after.as_deref()) {
        return Ok(cursor_error_response(error));
    }
    let (events, next_cursor) = state
        .store
        .list_audit_events_page(&context.instance.id, limit as u32, query.after.as_deref())
        .await
        .map_err(internal)?;
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
    let checked_event_ids: Vec<_> = events.iter().map(|event| event.id.clone()).collect();
    let checked_count = events.len();
    let has_more = next_cursor.is_some();
    Ok((
        StatusCode::OK,
        Json(json!({
            "checked_scope": "page",
            "checked_count": checked_count,
            "checked_event_ids": checked_event_ids,
            "event_count": checked_count,
            "valid_integrity_count": valid_integrity_count,
            "valid_authenticity_count": valid_authenticity_count,
            "invalid_integrity_ids": invalid_integrity,
            "invalid_authenticity_ids": invalid_authenticity,
            "is_valid": is_valid,
            "is_authentic": is_authentic,
            "limit": limit,
            "next_cursor": next_cursor,
            "has_more": has_more,
        })),
    )
        .into_response())
}

#[derive(Deserialize)]
struct AuditQuery {
    limit: Option<String>,
    after: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CursorValidationError {
    InvalidOpaqueCursor,
}

fn validate_cursor(raw: Option<&str>) -> Result<(), CursorValidationError> {
    if raw.is_some_and(|cursor| {
        cursor.is_empty()
            || cursor.len() > MAX_CURSOR_LENGTH
            || cursor.chars().any(char::is_control)
    }) {
        return Err(CursorValidationError::InvalidOpaqueCursor);
    }
    Ok(())
}

fn cursor_error_response(error: CursorValidationError) -> Response {
    match error {
        CursorValidationError::InvalidOpaqueCursor => api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "after is not a valid opaque cursor",
        ),
    }
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
            tracing::error!(
                event_id = event.id,
                instance_id,
                "audit event disappeared before webhook fanout could be completed"
            );
            return Err(StoreError::Http {
                status: StatusCode::SERVICE_UNAVAILABLE.as_u16(),
                code: "audit_event_missing".into(),
            });
        }
    }
    let deliveries = state
        .store
        .list_pending_webhook_deliveries(instance_id)
        .await?;
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
    if !endpoint.enabled || event.created_at < endpoint.created_at {
        return false;
    }
    if event.r#type == "webhook.test" {
        return event
            .data
            .get("endpoint_id")
            .and_then(Value::as_str)
            .is_some_and(|endpoint_id| endpoint_id == endpoint.id);
    }
    matches(&endpoint.events, &event.r#type)
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
    deliver_one_with_sender(state, id, |client, request| async move {
        client
            .execute(request)
            .await
            .map(|response| response.status())
    })
    .await
}

async fn deliver_one_with_sender<F, Fut>(
    state: &AppState,
    id: &str,
    send: F,
) -> Result<(), AppError>
where
    F: FnOnce(reqwest::Client, reqwest::Request) -> Fut + Send,
    Fut: Future<Output = Result<StatusCode, reqwest::Error>> + Send,
{
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
    let request = client
        .post(&endpoint.url)
        .timeout(Duration::from_secs(10))
        .header("content-type", "application/json")
        .header("X-TsAuth-Event", event_header)
        .header("X-TsAuth-Timestamp", timestamp)
        .header("X-TsAuth-Signature", format!("sha256={signature}"))
        .body(payload)
        .build();
    let response = match request {
        Ok(request) => send(client, request).await,
        Err(error) => Err(error),
    };
    match response {
        Ok(status) if status.is_success() => {
            update_delivery(state, &delivery, "succeeded", None, None).await
        }
        Ok(status) => settle_failure(state, &delivery, format!("HTTP {}", status.as_u16())).await,
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
    use crate::test_support;
    use auth_contracts::models::{ApiKey, Instance, WebhookEndpoint};
    use auth_security::{secretbox, tokens::hmac_sha256};
    use serde_json::json;
    use std::sync::{Arc, Mutex};

    fn sender_test_options(storage_url: String) -> crate::config::AuthOptions {
        crate::config::AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin: "http://localhost:8480".into(),
            terrascale_api_url: storage_url,
            terrascale_api_key: test_support::TEST_TERRASCALE_API_KEY.into(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database: "webhook-sender-test".into(),
            master_key: vec![7; 32],
            audit_signing_key: Some(vec![8; 32]),
            audit_verification_keys: Vec::new(),
            operator_key: "test-only-operator-key".into(),
            metrics_key: Some("test-only-metrics-key".into()),
            rate_limit_per_window: 1_000,
            stripe_secret_key: None,
            stripe_webhook_secret: None,
            stripe_api_url: "http://127.0.0.1:1".into(),
            price_pro: None,
            price_enterprise: None,
            smtp_host: None,
            smtp_port: 587,
            smtp_username: None,
            smtp_password: None,
            smtp_from_address: None,
        }
    }

    async fn management_test_state() -> (
        AppState,
        HeaderMap,
        String,
        test_support::TerraBaseHttpFixture,
    ) {
        let storage = test_support::TerraBaseHttpFixture::start().await;
        let options = sender_test_options(storage.origin.clone());
        let state = crate::app::build_state_with_mode(options, true)
            .expect("test application state should build");
        let now = unix_now();
        let instance_id = "inst_webhook_api".to_owned();
        state
            .store
            .upsert_instance(&Instance {
                id: instance_id.clone(),
                slug: "webhook-api".into(),
                name: "Webhook API test".into(),
                logo_url: None,
                theme_color: None,
                stripe_customer_id: None,
                created_at: now,
            })
            .await
            .expect("test instance should be stored");
        let secret = "sk_webhook_test_management_key";
        state
            .store
            .create_api_key(&ApiKey {
                id: "key_webhook_api".into(),
                instance_id: instance_id.clone(),
                name: "webhook API test".into(),
                secret_key_hash: auth_security::tokens::sha256_hex(secret),
                scopes: vec!["*".into()],
                created_at: now,
                last_used_at: None,
                revoked: false,
            })
            .await
            .expect("test API key should be stored");
        let mut headers = HeaderMap::new();
        headers.insert(
            header::AUTHORIZATION,
            HeaderValue::from_str(&format!("Bearer {secret}"))
                .expect("test authorization header should be valid"),
        );
        (state, headers, instance_id, storage)
    }

    async fn create_test_audit_event(
        state: &AppState,
        instance_id: &str,
        id: &str,
        created_at: i64,
    ) -> AuditEvent {
        state
            .store
            .create_audit_event(&AuditEvent {
                id: id.into(),
                instance_id: instance_id.into(),
                r#type: "user.created".into(),
                actor_id: None,
                data: json!({"user_id":"usr_webhook_page_test"})
                    .as_object()
                    .expect("event data should be an object")
                    .clone(),
                created_at,
                integrity_version: None,
                integrity_hash: None,
                integrity_signature: None,
                integrity_key_fingerprint: None,
            })
            .await
            .expect("test audit event should be stored")
    }

    async fn response_json(response: Response) -> Value {
        let bytes = to_bytes(response.into_body(), 64 * 1024)
            .await
            .expect("response body should fit in test limit");
        serde_json::from_slice(&bytes).expect("response body should be JSON")
    }

    #[tokio::test]
    async fn sender_builds_signed_request_and_persists_success_without_network() {
        let storage = test_support::TerraBaseHttpFixture::start().await;
        let options = sender_test_options(storage.origin.clone());
        let state = crate::app::build_state_with_mode(options, true)
            .expect("test application state should build");
        let now = unix_now();
        let instance_id = "inst_webhook_sender";
        let signing_secret = b"webhook sender fixture secret";
        let endpoint = WebhookEndpoint {
            id: "wh_webhook_sender".into(),
            instance_id: instance_id.into(),
            // A public literal passes the production destination validator. The
            // injected sender records the request without opening a connection.
            url: "https://8.8.8.8/hook".into(),
            events: vec!["user.created".into()],
            signing_secret_box: secretbox::encrypt(&state.options.master_key, signing_secret)
                .expect("webhook secret should encrypt"),
            enabled: true,
            created_at: now - 1,
        };
        state
            .store
            .create_webhook_endpoint(&endpoint)
            .await
            .expect("webhook endpoint should be stored");
        let event = state
            .store
            .create_audit_event(&AuditEvent {
                id: "evt_webhook_sender".into(),
                instance_id: instance_id.into(),
                r#type: "user.created".into(),
                actor_id: None,
                data: json!({"user_id":"usr_webhook_sender"})
                    .as_object()
                    .expect("event data should be an object")
                    .clone(),
                created_at: now,
                integrity_version: None,
                integrity_hash: None,
                integrity_signature: None,
                integrity_key_fingerprint: None,
            })
            .await
            .expect("audit event should be stored");
        let delivery_id = delivery_id(&endpoint.id, &event.id);
        enqueue_delivery(&state.store, &endpoint, &event)
            .await
            .expect("delivery should be queued");

        type RecordedRequest = (reqwest::Method, reqwest::Url, Vec<u8>, HeaderMap);
        let recorded = Arc::new(Mutex::new(None::<RecordedRequest>));
        let sender_recorded = recorded.clone();
        deliver_one_with_sender(&state, &delivery_id, move |client, request| {
            let recorded = sender_recorded.clone();
            async move {
                drop(client);
                let body = request
                    .body()
                    .and_then(reqwest::Body::as_bytes)
                    .unwrap_or_default()
                    .to_vec();
                *recorded
                    .lock()
                    .expect("request capture lock should be healthy") = Some((
                    request.method().clone(),
                    request.url().clone(),
                    body,
                    request.headers().clone(),
                ));
                Ok(StatusCode::OK)
            }
        })
        .await
        .expect("successful mock send should settle the delivery");

        let (method, url, body, headers) = recorded
            .lock()
            .expect("request capture lock should be healthy")
            .take()
            .expect("delivery should call the sender once");
        assert_eq!(method, reqwest::Method::POST);
        assert_eq!(url.as_str(), endpoint.url);
        let payload: Value = serde_json::from_slice(&body).expect("request body should be JSON");
        assert_eq!(
            payload,
            json!({
                "id": event.id,
                "type": event.r#type,
                "created_at": event.created_at,
                "data": event.data,
            })
        );
        assert_eq!(
            headers
                .get("X-TsAuth-Event")
                .and_then(|value| value.to_str().ok()),
            Some("user.created")
        );
        let timestamp = headers
            .get("X-TsAuth-Timestamp")
            .expect("timestamp header should be set")
            .to_str()
            .expect("timestamp header should be text");
        let signature = headers
            .get("X-TsAuth-Signature")
            .expect("signature header should be set")
            .to_str()
            .expect("signature header should be text");
        let mut signed = timestamp.as_bytes().to_vec();
        signed.push(b'.');
        signed.extend_from_slice(&body);
        assert_eq!(
            signature,
            format!("sha256={}", hex(&hmac_sha256(signing_secret, &signed)))
        );

        let settled = state
            .store
            .get_webhook_delivery(&delivery_id)
            .await
            .expect("delivery status should be readable")
            .expect("delivery row should remain stored");
        assert_eq!(settled.status, "succeeded");
    }

    #[tokio::test]
    async fn audit_list_and_verification_use_scoped_cursor_pages() {
        let (state, headers, instance_id, _storage) = management_test_state().await;
        let now = unix_now();
        create_test_audit_event(&state, &instance_id, "evt_webhook_page_a", now - 3).await;
        create_test_audit_event(&state, &instance_id, "evt_webhook_page_b", now - 2).await;
        create_test_audit_event(&state, &instance_id, "evt_webhook_page_c", now - 1).await;

        let (mut tampered, version) = state
            .store
            .get_versioned::<AuditEvent>("audit_events", "evt_webhook_page_b")
            .await
            .expect("tampered event should be readable")
            .expect("tampered event should exist");
        tampered.data.insert("tampered".into(), Value::Bool(true));
        assert!(
            state
                .store
                .compare_and_put("audit_events", &tampered.id, &tampered, &version)
                .await
                .expect("tampered event should be written")
        );

        let first_list_page = list_audit_events(
            State(state.clone()),
            Path("webhook-api".to_owned()),
            headers.clone(),
            Query(AuditQuery {
                limit: Some("2".into()),
                after: None,
            }),
        )
        .await
        .expect("first audit list page should return");
        let first_list = response_json(first_list_page).await;
        assert_eq!(first_list["events"][0]["id"], "evt_webhook_page_a");
        assert_eq!(first_list["events"][1]["id"], "evt_webhook_page_b");
        assert_eq!(first_list["has_more"], true);
        let list_cursor = first_list["next_cursor"]
            .as_str()
            .expect("first audit list page should return a cursor");

        let second_list_page = list_audit_events(
            State(state.clone()),
            Path("webhook-api".to_owned()),
            headers.clone(),
            Query(AuditQuery {
                limit: Some("2".into()),
                after: Some(list_cursor.to_owned()),
            }),
        )
        .await
        .expect("second audit list page should return");
        let second_list = response_json(second_list_page).await;
        assert_eq!(second_list["events"].as_array().unwrap().len(), 1);
        assert_eq!(second_list["events"][0]["id"], "evt_webhook_page_c");
        assert_eq!(second_list["has_more"], false);
        assert!(second_list["next_cursor"].is_null());

        let first_verify_page = verify_audit_events(
            State(state.clone()),
            Path("webhook-api".to_owned()),
            headers.clone(),
            Query(AuditQuery {
                limit: Some("1".into()),
                after: None,
            }),
        )
        .await
        .expect("first verification page should return");
        let first_verify = response_json(first_verify_page).await;
        assert_eq!(first_verify["checked_scope"], "page");
        assert_eq!(first_verify["checked_count"], 1);
        assert_eq!(
            first_verify["checked_event_ids"],
            json!(["evt_webhook_page_a"])
        );
        assert_eq!(first_verify["is_valid"], true);
        let verify_cursor = first_verify["next_cursor"]
            .as_str()
            .expect("first verification page should return a cursor");

        let second_verify_page = verify_audit_events(
            State(state),
            Path("webhook-api".to_owned()),
            headers,
            Query(AuditQuery {
                limit: Some("1".into()),
                after: Some(verify_cursor.to_owned()),
            }),
        )
        .await
        .expect("second verification page should return");
        let second_verify = response_json(second_verify_page).await;
        assert_eq!(second_verify["checked_scope"], "page");
        assert_eq!(second_verify["checked_count"], 1);
        assert_eq!(
            second_verify["checked_event_ids"],
            json!(["evt_webhook_page_b"])
        );
        assert_eq!(second_verify["is_valid"], false);
        assert_eq!(second_verify["is_authentic"], false);
    }

    #[tokio::test]
    async fn dead_letter_list_uses_bounded_status_filtered_cursor_pages() {
        let (state, headers, instance_id, _storage) = management_test_state().await;
        for (id, status) in [
            ("whd_dead_a", "dead"),
            ("whd_succeeded", "succeeded"),
            ("whd_dead_z", "dead"),
        ] {
            state
                .store
                .upsert_webhook_delivery(&WebhookDelivery {
                    id: id.into(),
                    instance_id: instance_id.clone(),
                    endpoint_id: "wh_dead_letter_test".into(),
                    event_id: "evt_dead_letter_test".into(),
                    status: status.into(),
                    attempts: 3,
                    last_error: Some("fixture failure".into()),
                    updated_at: unix_now(),
                    next_attempt_at: None,
                    lease_until: None,
                    lease_token: None,
                })
                .await
                .expect("test delivery should be stored");
        }

        let first_response = list_dead_letters(
            State(state.clone()),
            Path("webhook-api".to_owned()),
            headers.clone(),
            Query(AuditQuery {
                limit: Some("1".into()),
                after: None,
            }),
        )
        .await
        .expect("first dead-letter page should return");
        let first_page = response_json(first_response).await;
        assert_eq!(first_page["deliveries"].as_array().unwrap().len(), 1);
        assert_eq!(first_page["deliveries"][0]["id"], "whd_dead_a");
        assert_eq!(first_page["has_more"], true);
        let cursor = first_page["next_cursor"]
            .as_str()
            .expect("first dead-letter page should return a cursor")
            .to_owned();

        let second_response = list_dead_letters(
            State(state),
            Path("webhook-api".to_owned()),
            headers,
            Query(AuditQuery {
                limit: Some("1".into()),
                after: Some(cursor),
            }),
        )
        .await
        .expect("second dead-letter page should return");
        let second_page = response_json(second_response).await;
        assert_eq!(second_page["deliveries"].as_array().unwrap().len(), 1);
        assert_eq!(second_page["deliveries"][0]["id"], "whd_dead_z");
        assert_eq!(second_page["has_more"], false);
        assert!(second_page["next_cursor"].is_null());
    }

    #[tokio::test]
    async fn webhook_validation_error_describes_https_public_destinations() {
        let (state, headers, _, _storage) = management_test_state().await;
        let request = Request::builder()
            .method("POST")
            .header(header::CONTENT_TYPE, "application/json")
            .body(axum::body::Body::from(
                json!({
                    "url":"http://127.0.0.1:8080/hook",
                    "events":["user.created"]
                })
                .to_string(),
            ))
            .expect("test request should build");

        let response = create_webhook(
            State(state),
            Path("webhook-api".to_owned()),
            headers,
            request,
        )
        .await
        .expect("invalid destination should return a client error");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = response_json(response).await;
        assert_eq!(
            body["error"]["message"],
            "url must be an absolute HTTPS URL with a public destination"
        );
    }

    #[tokio::test]
    async fn webhook_delete_success_is_preserved_if_audit_write_fails() {
        let (state, headers, instance_id, storage) = management_test_state().await;
        let endpoint = WebhookEndpoint {
            id: "wh_delete_audit_failure".into(),
            instance_id: instance_id.clone(),
            url: "https://8.8.8.8/hook".into(),
            events: vec!["user.created".into()],
            signing_secret_box: "unused-in-delete-test".into(),
            enabled: true,
            created_at: unix_now(),
        };
        state
            .store
            .create_webhook_endpoint(&endpoint)
            .await
            .expect("test webhook endpoint should be stored");
        storage.fail_next_create_only_before_commit("audit_events");

        let response = delete_webhook(
            State(state.clone()),
            Path(("webhook-api".into(), endpoint.id.clone())),
            headers,
        )
        .await
        .expect("committed deletion should return a response");
        assert_eq!(response.status(), StatusCode::NO_CONTENT);
        assert!(
            state
                .store
                .get_webhook_endpoint(&endpoint.id)
                .await
                .expect("deleted endpoint lookup should succeed")
                .is_none()
        );
        let (audit_events, _) = state
            .store
            .list_audit_events_page(&instance_id, 100, None)
            .await
            .expect("audit page should remain readable");
        assert!(
            audit_events
                .iter()
                .all(|event| event.r#type != "webhook.deleted")
        );
    }

    #[tokio::test]
    async fn missing_audit_event_during_fanout_is_a_service_failure() {
        let (state, _headers, instance_id, storage) = management_test_state().await;
        let now = unix_now();
        let endpoint = WebhookEndpoint {
            id: "wh_missing_audit_event".into(),
            instance_id: instance_id.clone(),
            url: "https://127.0.0.1/never-connect".into(),
            events: vec!["*".into()],
            signing_secret_box: secretbox::encrypt(
                &state.options.master_key,
                b"webhook missing event test secret",
            )
            .expect("test secret should encrypt"),
            enabled: true,
            created_at: now - 1,
        };
        state
            .store
            .create_webhook_endpoint(&endpoint)
            .await
            .expect("test endpoint should be stored");
        let event =
            create_test_audit_event(&state, &instance_id, "evt_missing_during_fanout", now).await;
        let mut pause = storage.pause_next_query_response("audit_events");
        let dispatch_state = state.clone();
        let dispatch_instance_id = instance_id.clone();
        let dispatch =
            tokio::spawn(
                async move { dispatch_instance(&dispatch_state, &dispatch_instance_id).await },
            );
        pause.wait_until_paused().await;
        state
            .store
            .delete("audit_events", &event.id)
            .await
            .expect("fixture should delete the audit event during the paused query");
        pause.resume();

        let error = dispatch
            .await
            .expect("dispatcher task should complete")
            .expect_err("missing durable audit row must stop marker completion");
        assert!(matches!(
            error,
            StoreError::Http {
                status: 503,
                ref code
            } if code == "audit_event_missing"
        ));
    }

    #[tokio::test]
    async fn webhook_test_route_fans_out_only_to_the_selected_endpoint() {
        let (state, headers, instance_id, _storage) = management_test_state().await;
        let now = unix_now();
        let signing_secret_box =
            secretbox::encrypt(&state.options.master_key, b"webhook targeted test secret")
                .expect("test secret should encrypt");
        let selected = WebhookEndpoint {
            id: "wh_selected".into(),
            instance_id: instance_id.clone(),
            // Dispatch rejects this loopback destination before opening a socket.
            url: "https://127.0.0.1/selected".into(),
            events: vec!["user.created".into()],
            signing_secret_box: signing_secret_box.clone(),
            enabled: true,
            created_at: now - 1,
        };
        let wildcard = WebhookEndpoint {
            id: "wh_wildcard".into(),
            instance_id,
            url: "https://127.0.0.1/wildcard".into(),
            events: vec!["*".into()],
            signing_secret_box,
            enabled: true,
            created_at: now - 1,
        };
        state
            .store
            .create_webhook_endpoint(&selected)
            .await
            .expect("selected endpoint should be stored");
        state
            .store
            .create_webhook_endpoint(&wildcard)
            .await
            .expect("wildcard endpoint should be stored");

        let response = test_webhook(
            State(state.clone()),
            Path(("webhook-api".into(), selected.id.clone())),
            headers,
        )
        .await
        .expect("selected endpoint test should be queued");
        assert_eq!(response.status(), StatusCode::OK);
        dispatch_instance(&state, &selected.instance_id)
            .await
            .expect("test-event fanout should complete");
        let deliveries = state
            .store
            .list_webhook_deliveries(&selected.instance_id)
            .await
            .expect("webhook delivery rows should be readable");
        assert_eq!(deliveries.len(), 1);
        assert_eq!(deliveries[0].endpoint_id, selected.id);
    }

    #[test]
    fn cursor_page_routes_document_bounds_and_checked_scope() {
        let document = serde_json::to_value(openapi()).expect("webhook OpenAPI should serialize");
        let verification = &document["paths"]["/v1/{slug}/audit-events/verify"]["get"];
        let parameters = verification["parameters"]
            .as_array()
            .expect("verification parameters should be documented");
        assert!(parameters.iter().any(|parameter| {
            parameter["name"] == "limit"
                && parameter["in"] == "query"
                && parameter["schema"]["maximum"] == 1000
        }));
        assert!(
            parameters
                .iter()
                .any(|parameter| { parameter["name"] == "after" && parameter["in"] == "query" })
        );
        assert_eq!(
            verification["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/WebhookAuditVerificationPage"
        );
        assert_eq!(
            document["components"]["schemas"]["WebhookAuditVerificationPage"]["properties"]["checked_scope"]
                ["enum"][0],
            "page"
        );
        let description = verification["description"]
            .as_str()
            .expect("verification scope should be documented");
        assert!(description.contains("does not establish global completeness"));
        assert!(description.contains("detect deleted or omitted events"));
    }

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
