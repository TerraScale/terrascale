//! Stripe HTTP calls, webhook signature validation, and idempotent event processing.

use std::{
    time::Duration,
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::{
    AppError,
    models::{AuditEvent, Instance, Subscription},
};
use auth_store::{AuthStore, StoreError, StoreResult};
use axum::{
    Json,
    body::to_bytes,
    extract::{Request, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use crate::{
    AppState,
    features::billing::{ENTERPRISE, FREE, PRO, internal, price_for},
};

const MAX_WEBHOOK_BODY: usize = 1024 * 1024;
const SIGNATURE_TOLERANCE_SECONDS: u64 = 300;
const EVENT_LEASE_SECONDS: i64 = 120;

#[derive(Debug)]
pub(super) enum StripeError {
    Configuration,
    Request(reqwest::Error),
    InvalidResponse,
    Upstream(u16),
}

impl std::fmt::Display for StripeError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Configuration => formatter.write_str("invalid Stripe API configuration"),
            Self::Request(error) => write!(formatter, "Stripe request failed: {error}"),
            Self::InvalidResponse => formatter.write_str("Stripe returned invalid JSON"),
            Self::Upstream(status) => write!(formatter, "Stripe returned HTTP {status}"),
        }
    }
}

pub(super) async fn post(
    state: &AppState,
    secret: &str,
    path: &str,
    fields: &[(String, String)],
    idempotency_key: Option<&str>,
) -> Result<Value, StripeError> {
    let url = api_url(state, path)?;
    send_json(post_request(
        &state.http,
        url,
        secret,
        fields,
        idempotency_key,
    ))
    .await
}

fn post_request(
    client: &reqwest::Client,
    url: reqwest::Url,
    secret: &str,
    fields: &[(String, String)],
    idempotency_key: Option<&str>,
) -> reqwest::RequestBuilder {
    let mut request = client
        .post(url)
        .bearer_auth(secret)
        .timeout(Duration::from_secs(10))
        .form(fields);
    if let Some(key) = idempotency_key {
        request = request.header("Idempotency-Key", key);
    }
    request
}

pub(super) async fn get(state: &AppState, secret: &str, path: &str) -> Result<Value, StripeError> {
    let url = api_url(state, path)?;
    send_json(
        state
            .http
            .get(url)
            .bearer_auth(secret)
            .timeout(Duration::from_secs(10)),
    )
    .await
}

fn api_url(state: &AppState, path: &str) -> Result<reqwest::Url, StripeError> {
    let base = reqwest::Url::parse(&state.options.stripe_api_url)
        .map_err(|_| StripeError::Configuration)?;
    if !matches!(base.scheme(), "http" | "https")
        || base.host_str().is_none()
        || !base.username().is_empty()
        || base.password().is_some()
        || base.query().is_some()
        || base.fragment().is_some()
    {
        return Err(StripeError::Configuration);
    }
    base.join(path).map_err(|_| StripeError::Configuration)
}

async fn send_json(request: reqwest::RequestBuilder) -> Result<Value, StripeError> {
    let response = request.send().await.map_err(StripeError::Request)?;
    if !response.status().is_success() {
        return Err(StripeError::Upstream(response.status().as_u16()));
    }
    response
        .json()
        .await
        .map_err(|_| StripeError::InvalidResponse)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SignatureStatus {
    Valid,
    Invalid,
    Stale,
}

fn verify_signature(
    secret: &str,
    header: Option<&str>,
    payload: &[u8],
    now: i64,
) -> SignatureStatus {
    let mut timestamp = None;
    let mut signatures = Vec::new();
    for item in header.unwrap_or_default().split(',') {
        let Some((key, value)) = item.trim().split_once('=') else {
            continue;
        };
        let value = value.trim();
        match key.trim() {
            "t" => {
                if timestamp.is_some() {
                    return SignatureStatus::Invalid;
                }
                timestamp = value.parse::<i64>().ok();
            }
            "v1" => signatures.push(value),
            _ => {}
        }
    }
    let Some(timestamp) = timestamp else {
        return SignatureStatus::Invalid;
    };
    if signatures.is_empty() {
        return SignatureStatus::Invalid;
    }
    if timestamp.abs_diff(now) > SIGNATURE_TOLERANCE_SECONDS {
        return SignatureStatus::Stale;
    }

    let mut signed = timestamp.to_string().into_bytes();
    signed.push(b'.');
    signed.extend_from_slice(payload);
    if signatures.into_iter().any(|value| {
        decode_hex(value).is_some_and(|signature| {
            auth_security::tokens::verify_hmac_sha256(secret.as_bytes(), &signed, &signature)
        })
    }) {
        SignatureStatus::Valid
    } else {
        SignatureStatus::Invalid
    }
}

fn decode_hex(value: &str) -> Option<Vec<u8>> {
    if value.len() != 64 {
        return None;
    }
    value
        .as_bytes()
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| {
            let high = hex_digit(pair[0])?;
            let low = hex_digit(pair[1])?;
            Some((high << 4) | low)
        })
        .collect()
}

fn hex_digit(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

pub(super) async fn receive(
    State(state): State<AppState>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let Some(secret) = state.options.stripe_webhook_secret.as_deref() else {
        return Ok(api_error(
            StatusCode::SERVICE_UNAVAILABLE,
            "billing_not_configured",
            "Stripe webhooks are not configured for this deployment",
        ));
    };
    let signatures: Vec<_> = headers.get_all("Stripe-Signature").iter().collect();
    let raw = to_bytes(request.into_body(), MAX_WEBHOOK_BODY)
        .await
        .map_err(|_| {
            AppError::new(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "the webhook request is invalid",
            )
        })?;
    let signature = if signatures.len() == 1 {
        signatures[0].to_str().ok()
    } else {
        None
    };
    match verify_signature(secret, signature, &raw, unix_now()) {
        SignatureStatus::Valid => {}
        SignatureStatus::Stale => {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "the webhook could not be accepted",
            ));
        }
        SignatureStatus::Invalid => {
            return Ok(api_error(
                StatusCode::UNAUTHORIZED,
                "unauthorized",
                "the webhook could not be accepted",
            ));
        }
    }
    let event: Value = match serde_json::from_slice::<Value>(&raw) {
        Ok(value) if value.is_object() => value,
        _ => {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_json",
                "the webhook body is not valid JSON",
            ));
        }
    };
    let Some(event_id) = event
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "the webhook could not be accepted",
        ));
    };
    let Some(event_type) = event
        .get("type")
        .and_then(Value::as_str)
        .filter(|value| value.len() <= 128)
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "the webhook could not be accepted",
        ));
    };
    let Some(source) = event
        .pointer("/data/object")
        .filter(|value| value.is_object())
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "the webhook could not be accepted",
        ));
    };

    let claim = match claim_event(&state.store, event_id, event_type, unix_now()).await {
        Ok(EventClaim::Acquired(token)) => token,
        Ok(EventClaim::Complete) => {
            return Ok((
                StatusCode::OK,
                Json(json!({"received":true,"duplicate":true})),
            )
                .into_response());
        }
        Ok(EventClaim::Busy) => {
            return Ok(api_error(
                StatusCode::SERVICE_UNAVAILABLE,
                "event_in_progress",
                "the webhook is being processed; retry shortly",
            ));
        }
        Err(error) => return Err(internal(error)),
    };

    let result = process_event(&state, &event, event_type, source).await;
    match result {
        Ok(()) => {
            if complete_event(&state.store, event_id, &claim, unix_now())
                .await
                .map_err(internal)?
            {
                Ok((StatusCode::OK, Json(json!({"received":true}))).into_response())
            } else {
                Err(internal("Stripe event lease was lost before completion"))
            }
        }
        Err(error) => {
            let _ = fail_event(&state.store, event_id, &claim, unix_now()).await;
            tracing::error!(event_type, ?error, "Stripe event processing failed");
            Err(AppError::internal())
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StripeEventReceipt {
    id: String,
    event_id: String,
    event_type: String,
    status: String,
    claim_token: String,
    lease_until: i64,
    attempts: u32,
    updated_at: i64,
}

enum EventClaim {
    Acquired(String),
    Complete,
    Busy,
}

async fn claim_event(
    store: &AuthStore,
    event_id: &str,
    event_type: &str,
    now: i64,
) -> Result<EventClaim, StoreError> {
    let id = receipt_id(event_id);
    let token = auth_security::tokens::random_token(24);
    let mut record = StripeEventReceipt {
        id: id.clone(),
        event_id: event_id.to_owned(),
        event_type: event_type.to_owned(),
        status: "processing".into(),
        claim_token: token.clone(),
        lease_until: now + EVENT_LEASE_SECONDS,
        attempts: 1,
        updated_at: now,
    };
    match store.create("stripe_events", &id, &record).await {
        Ok(()) => return Ok(EventClaim::Acquired(token)),
        Err(error) if error.is_conflict() => {}
        Err(error) => return Err(error),
    }
    for _ in 0..5 {
        let Some((mut existing, version)) = store
            .get_versioned::<StripeEventReceipt>("stripe_events", &id)
            .await?
        else {
            return Err(StoreError::InvalidConfiguration(
                "Stripe event receipt disappeared".into(),
            ));
        };
        if existing.event_id != event_id {
            return Err(StoreError::InvalidConfiguration(
                "Stripe event receipt collision".into(),
            ));
        }
        if existing.status == "complete" {
            return Ok(EventClaim::Complete);
        }
        if existing.status == "processing" && existing.lease_until > now {
            return Ok(EventClaim::Busy);
        }
        record.attempts = existing.attempts.saturating_add(1);
        existing.status = "processing".into();
        existing.event_type = event_type.to_owned();
        existing.claim_token = token.clone();
        existing.lease_until = now + EVENT_LEASE_SECONDS;
        existing.attempts = record.attempts;
        existing.updated_at = now;
        if store
            .compare_and_put("stripe_events", &id, &existing, &version)
            .await?
        {
            return Ok(EventClaim::Acquired(token));
        }
    }
    Ok(EventClaim::Busy)
}

async fn complete_event(
    store: &AuthStore,
    event_id: &str,
    token: &str,
    now: i64,
) -> Result<bool, StoreError> {
    transition_receipt(store, event_id, token, now, "complete").await
}

async fn fail_event(
    store: &AuthStore,
    event_id: &str,
    token: &str,
    now: i64,
) -> Result<bool, StoreError> {
    transition_receipt(store, event_id, token, now, "failed").await
}

async fn transition_receipt(
    store: &AuthStore,
    event_id: &str,
    token: &str,
    now: i64,
    status: &str,
) -> Result<bool, StoreError> {
    let id = receipt_id(event_id);
    for _ in 0..5 {
        let Some((mut record, version)) = store
            .get_versioned::<StripeEventReceipt>("stripe_events", &id)
            .await?
        else {
            return Ok(false);
        };
        if record.event_id != event_id
            || record.claim_token != token
            || record.status != "processing"
        {
            return Ok(false);
        }
        record.status = status.to_owned();
        record.lease_until = 0;
        record.updated_at = now;
        if store
            .compare_and_put("stripe_events", &id, &record, &version)
            .await?
        {
            return Ok(true);
        }
    }
    Ok(false)
}

async fn process_event(
    state: &AppState,
    event: &Value,
    event_type: &str,
    source: &Value,
) -> Result<(), AppError> {
    let event_created = event
        .get("created")
        .and_then(Value::as_i64)
        .unwrap_or_else(unix_now);
    match event_type {
        "checkout.session.completed" => {
            checkout_completed(state, event, source, event_created).await
        }
        "customer.subscription.updated" | "customer.subscription.deleted" => {
            reconcile_subscription(state, event, source, event_created).await
        }
        "checkout.session.expired" => checkout_expired(state, source).await,
        _ => Ok(()), // Unknown signed events are safely acknowledged.
    }
}

async fn checkout_completed(
    state: &AppState,
    event: &Value,
    session: &Value,
    created: i64,
) -> Result<(), AppError> {
    if session
        .get("mode")
        .and_then(Value::as_str)
        .is_some_and(|value| value != "subscription")
    {
        return Ok(());
    }
    let client_reference = session.get("client_reference_id").and_then(Value::as_str);
    let metadata_id = session
        .pointer("/metadata/instance_id")
        .and_then(Value::as_str);
    if client_reference
        .zip(metadata_id)
        .is_some_and(|(left, right)| left != right)
    {
        return Ok(());
    }
    let Some(instance_id) = client_reference.or(metadata_id) else {
        return Ok(());
    };
    let Some(customer) = session
        .get("customer")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let Some(subscription_id) = session
        .get("subscription")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let Some(session_id) = session
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let Some(instance) = state
        .store
        .get::<Instance>("instances", instance_id)
        .await
        .map_err(internal)?
    else {
        return Ok(());
    };
    if instance
        .stripe_customer_id
        .as_deref()
        .is_some_and(|linked| linked != customer)
    {
        return Ok(());
    }
    if instance.stripe_customer_id.as_deref() != Some(customer)
        && !checkout_intent_matches(&state.store, &instance.id, session_id, session)
            .await
            .map_err(internal)?
    {
        return Ok(());
    }

    let secret = state
        .options
        .stripe_secret_key
        .as_deref()
        .ok_or_else(AppError::unavailable)?;
    for _ in 0..8 {
        let found = state
            .store
            .find_subscription_by_customer(customer)
            .await
            .map_err(internal)?;
        if found
            .as_ref()
            .is_some_and(|value| value.instance_id != instance.id)
        {
            return Ok(());
        }
        let versioned = if let Some(found) = found {
            let Some((existing, version)) = state
                .store
                .get_versioned::<Subscription>("subscriptions", &found.id)
                .await
                .map_err(internal)?
            else {
                continue;
            };
            if existing.instance_id != instance.id || existing.stripe_customer_id != customer {
                return Ok(());
            }
            if existing
                .stripe_subscription_id
                .as_deref()
                .is_some_and(|value| value != subscription_id)
                && existing.status != "canceled"
            {
                return Ok(());
            }
            Some((existing, version))
        } else {
            None
        };

        // Pin the local version before the GET. If an update or cancellation commits
        // while this request is in flight, this write loses CAS and re-fetches Stripe.
        let stripe_subscription = get(
            state,
            secret,
            &format!("/v1/subscriptions/{subscription_id}"),
        )
        .await
        .map_err(|error| {
            tracing::warn!(%error, "Stripe subscription lookup failed; event will be retried");
            AppError::internal()
        })?;
        if stripe_subscription.get("customer").and_then(Value::as_str) != Some(customer)
            || stripe_subscription
                .get("id")
                .and_then(Value::as_str)
                .is_some_and(|value| value != subscription_id)
        {
            return Ok(());
        }
        if !claim_stripe_customer(&state.store, &instance.id, customer)
            .await
            .map_err(internal)?
        {
            return Ok(());
        }
        if !state
            .store
            .try_link_stripe_customer(&instance.id, customer)
            .await
            .map_err(internal)?
        {
            return Ok(());
        }
        let plan = price_plan(first_price_id(&stripe_subscription), state).to_owned();
        let raw_status = stripe_subscription
            .get("status")
            .and_then(Value::as_str)
            .unwrap_or("incomplete");
        let status = if raw_status == "incomplete_expired" {
            "canceled"
        } else {
            raw_status
        };
        let period_end = stripe_subscription
            .get("current_period_end")
            .and_then(Value::as_i64);
        let reconciled_at = unix_now();
        let subscription = if let Some((existing, _)) = versioned.as_ref() {
            let (subscription, _) = apply_stripe_subscription(
                existing,
                StripeSubscriptionUpdate {
                    stripe_subscription_id: subscription_id,
                    plan: &plan,
                    status,
                    period_end,
                    event,
                    event_created: created,
                    observed_at: reconciled_at,
                },
            );
            subscription
        } else {
            Subscription {
                // One durable row per Stripe customer makes competing completion events
                // converge on the same create-only key.
                id: subscription_record_id(customer),
                instance_id: instance.id.clone(),
                stripe_customer_id: customer.to_owned(),
                stripe_subscription_id: Some(subscription_id.to_owned()),
                plan: plan.clone(),
                status: status.to_owned(),
                current_period_end: period_end,
                created_at: created,
                updated_at: reconciled_at,
                last_stripe_event_created: Some(created),
                last_stripe_event_id: Some(event_id(event).to_owned()),
            }
        };
        let source_version = versioned.as_ref().map(|(_, version)| version.as_str());
        let Some(transition) = crate::features::billing::begin_subscription_quota_transition(
            &state.store,
            &instance.id,
            &subscription,
            source_version,
            event_id(event),
        )
        .await
        .map_err(internal)?
        else {
            continue;
        };
        let same_candidate = super::same_subscription(&subscription, &transition.candidate);
        let prepared_subscription = transition.candidate.clone();
        let write_result = if let Some((_, version)) = versioned.as_ref() {
            state
                .store
                .compare_and_put(
                    "subscriptions",
                    &prepared_subscription.id,
                    &prepared_subscription,
                    version,
                )
                .await
        } else {
            match state
                .store
                .create(
                    "subscriptions",
                    &prepared_subscription.id,
                    &prepared_subscription,
                )
                .await
            {
                Ok(()) => Ok(true),
                Err(error) if error.is_conflict() => Ok(false),
                Err(error) => Err(error),
            }
        };
        let write_result = crate::features::billing::finish_subscription_quota_transition(
            &state.store,
            &instance.id,
            &transition,
            write_result,
        )
        .await
        .map_err(internal)?;
        if !write_result || !same_candidate {
            continue;
        }
        audit(
            state,
            &instance.id,
            "billing.checkout_completed",
            json!({
                "checkout_session_id": session.get("id").and_then(Value::as_str),
                "requested_plan": session.pointer("/metadata/plan").and_then(Value::as_str),
                "stripe_customer_id": customer,
                "stripe_subscription_id": subscription_id,
            })
            .as_object()
            .cloned()
            .unwrap_or_default(),
            &format!("checkout_session:{session_id}"),
        )
        .await?;
        crate::features::billing::finish_checkout_intent(
            &state.store,
            &instance.id,
            session_id,
            unix_now(),
        )
        .await
        .map_err(internal)?;
        return Ok(());
    }
    Err(internal("checkout subscription CAS contention"))
}

async fn checkout_intent_matches(
    store: &AuthStore,
    instance_id: &str,
    session_id: &str,
    session: &Value,
) -> StoreResult<bool> {
    let Some(intent) = store
        .get::<super::StripeCheckoutIntent>(
            "stripe_checkout_intents",
            &super::checkout_intent_id(instance_id),
        )
        .await?
    else {
        return Ok(false);
    };
    if intent.instance_id != instance_id {
        return Ok(false);
    }
    let session_nonce = session
        .pointer("/metadata/ts_auth_intent_nonce")
        .and_then(Value::as_str);
    match intent.status.as_str() {
        "open" | "complete" => Ok(intent.checkout_session_id.as_deref() == Some(session_id)
            && (intent.checkout_nonce.is_empty()
                || session_nonce == Some(intent.checkout_nonce.as_str()))),
        // Stripe can deliver the completion event before create_checkout persists
        // the session ID. The active intent's random nonce proves the session came
        // from this intent, even when another service shares the Stripe account.
        "creating" => Ok(!intent.checkout_nonce.is_empty()
            && session_nonce == Some(intent.checkout_nonce.as_str())
            && intent.lease_until > unix_now()
            && session.pointer("/metadata/plan").and_then(Value::as_str)
                == Some(intent.plan.as_str())),
        _ => Ok(false),
    }
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct StripeCustomerClaim {
    id: String,
    instance_id: String,
    stripe_customer_id: String,
    created_at: i64,
}

async fn claim_stripe_customer(
    store: &AuthStore,
    instance_id: &str,
    customer_id: &str,
) -> StoreResult<bool> {
    let id = stripe_customer_claim_id(customer_id);
    let claim = StripeCustomerClaim {
        id: id.clone(),
        instance_id: instance_id.to_owned(),
        stripe_customer_id: customer_id.to_owned(),
        created_at: unix_now(),
    };
    for _ in 0..8 {
        match store.create("stripe_customer_claims", &id, &claim).await {
            Ok(()) => return Ok(true),
            Err(error) if error.is_conflict() => {
                let Some(existing) = store
                    .get::<StripeCustomerClaim>("stripe_customer_claims", &id)
                    .await?
                else {
                    continue;
                };
                return Ok(existing.instance_id == instance_id
                    && existing.stripe_customer_id == customer_id);
            }
            Err(error) => return Err(error),
        }
    }
    Err(StoreError::Contention)
}

fn stripe_customer_claim_id(customer_id: &str) -> String {
    format!(
        "stripe_customer_{}",
        auth_security::tokens::sha256_hex(customer_id)
    )
}

async fn checkout_expired(state: &AppState, session: &Value) -> Result<(), AppError> {
    let Some(session_id) = session
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let Some(instance_id) = session
        .get("client_reference_id")
        .and_then(Value::as_str)
        .or_else(|| {
            session
                .pointer("/metadata/instance_id")
                .and_then(Value::as_str)
        })
    else {
        return Ok(());
    };
    let secret = state
        .options
        .stripe_secret_key
        .as_deref()
        .ok_or_else(AppError::unavailable)?;
    let current = get(state, secret, &format!("/v1/checkout/sessions/{session_id}"))
        .await
        .map_err(|error| {
            tracing::warn!(%error, session_id, "Stripe checkout expiration lookup failed; event will be retried");
            AppError::internal()
        })?;
    if current.get("id").and_then(Value::as_str) != Some(session_id)
        || !super::checkout_session_confirmed_expired(&current)
    {
        return Ok(());
    }
    crate::features::billing::finish_checkout_intent(
        &state.store,
        instance_id,
        session_id,
        unix_now(),
    )
    .await
    .map_err(internal)
}

async fn reconcile_subscription(
    state: &AppState,
    event: &Value,
    event_subscription: &Value,
    created: i64,
) -> Result<(), AppError> {
    let Some(customer) = event_subscription
        .get("customer")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let Some(subscription_id) = event_subscription
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| safe_stripe_id(value))
    else {
        return Ok(());
    };
    let secret = state
        .options
        .stripe_secret_key
        .as_deref()
        .ok_or_else(AppError::unavailable)?;
    let incoming_event_type = event
        .get("type")
        .and_then(Value::as_str)
        .unwrap_or_default();

    for _ in 0..8 {
        let Some(found) = state
            .store
            .find_subscription_by_customer(customer)
            .await
            .map_err(internal)?
        else {
            return Ok(());
        };
        if found.stripe_subscription_id.as_deref() != Some(subscription_id) {
            return Ok(());
        }
        // Read the CAS version before Stripe GET. If another event reconciles while
        // the request is in flight, discard this response and fetch Stripe again.
        let Some((existing, version)) = state
            .store
            .get_versioned::<Subscription>("subscriptions", &found.id)
            .await
            .map_err(internal)?
        else {
            continue;
        };
        if existing.instance_id != found.instance_id
            || existing.stripe_customer_id != customer
            || existing.stripe_subscription_id.as_deref() != Some(subscription_id)
        {
            continue;
        }
        let (status, plan, period_end) = match get(
            state,
            secret,
            &format!("/v1/subscriptions/{subscription_id}"),
        )
        .await
        {
            Ok(current) => {
                if current.get("id").and_then(Value::as_str) != Some(subscription_id)
                    || current.get("customer").and_then(Value::as_str) != Some(customer)
                {
                    return Ok(());
                }
                let Some(raw_status) = current.get("status").and_then(Value::as_str) else {
                    return Err(internal("Stripe subscription response omitted status"));
                };
                let status = if raw_status == "incomplete_expired" {
                    "canceled"
                } else {
                    raw_status
                };
                let plan = price_plan(first_price_id(&current), state).to_owned();
                (
                    status.to_owned(),
                    plan,
                    current.get("current_period_end").and_then(Value::as_i64),
                )
            }
            Err(StripeError::Upstream(404)) => (
                "canceled".to_owned(),
                existing.plan.clone(),
                existing.current_period_end,
            ),
            Err(error) => {
                tracing::warn!(%error, subscription_id, "Stripe subscription reconciliation failed; event will be retried");
                return Err(AppError::internal());
            }
        };
        let (updated, _state_changed) = apply_stripe_subscription(
            &existing,
            StripeSubscriptionUpdate {
                stripe_subscription_id: subscription_id,
                plan: &plan,
                status: &status,
                period_end,
                event,
                event_created: created,
                observed_at: unix_now(),
            },
        );
        let Some(transition) = crate::features::billing::begin_subscription_quota_transition(
            &state.store,
            &existing.instance_id,
            &updated,
            Some(&version),
            event_id(event),
        )
        .await
        .map_err(internal)?
        else {
            continue;
        };
        let same_candidate = super::same_subscription(&updated, &transition.candidate);
        let prepared_subscription = transition.candidate.clone();
        let write_result = state
            .store
            .compare_and_put(
                "subscriptions",
                &prepared_subscription.id,
                &prepared_subscription,
                &version,
            )
            .await;
        if !crate::features::billing::finish_subscription_quota_transition(
            &state.store,
            &existing.instance_id,
            &transition,
            write_result,
        )
        .await
        .map_err(internal)?
            || !same_candidate
        {
            continue;
        }

        audit(
            state,
            &existing.instance_id,
            if incoming_event_type == "customer.subscription.deleted" {
                "billing.subscription_deleted"
            } else {
                "billing.subscription_updated"
            },
            json!({
                "stripe_customer_id": customer,
                "stripe_subscription_id": subscription_id,
                "source_event_type": incoming_event_type,
                "source_status": event_subscription.get("status").and_then(Value::as_str),
                "source_price_id": first_price_id(event_subscription),
                "source_period_end": event_subscription.get("current_period_end").and_then(Value::as_i64),
            })
            .as_object()
            .cloned()
            .unwrap_or_default(),
            event_id(event),
        )
        .await?;
        return Ok(());
    }
    Err(internal("subscription reconciliation contention"))
}

struct StripeSubscriptionUpdate<'a> {
    stripe_subscription_id: &'a str,
    plan: &'a str,
    status: &'a str,
    period_end: Option<i64>,
    event: &'a Value,
    event_created: i64,
    observed_at: i64,
}

fn apply_stripe_subscription(
    existing: &Subscription,
    update: StripeSubscriptionUpdate<'_>,
) -> (Subscription, bool) {
    let state_changed = existing.stripe_subscription_id.as_deref()
        != Some(update.stripe_subscription_id)
        || existing.plan != update.plan
        || existing.status != update.status
        || existing.current_period_end != update.period_end;
    (
        Subscription {
            stripe_subscription_id: Some(update.stripe_subscription_id.to_owned()),
            plan: update.plan.to_owned(),
            status: update.status.to_owned(),
            current_period_end: update.period_end,
            updated_at: if state_changed {
                existing.updated_at.max(update.observed_at)
            } else {
                existing.updated_at
            },
            last_stripe_event_created: Some(update.event_created),
            last_stripe_event_id: Some(event_id(update.event).to_owned()),
            ..existing.clone()
        },
        state_changed,
    )
}

async fn audit(
    state: &AppState,
    instance_id: &str,
    event_type: &str,
    mut data: serde_json::Map<String, Value>,
    source_id: &str,
) -> Result<(), AppError> {
    data.insert("source_id".into(), Value::String(source_id.to_owned()));
    let event = AuditEvent {
        id: audit_id(source_id),
        instance_id: instance_id.to_owned(),
        r#type: event_type.to_owned(),
        actor_id: None,
        data,
        created_at: unix_now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    match state.store.create_audit_event(&event).await {
        Ok(_) => {}
        Err(error) if error.is_conflict() => {
            let existing = state
                .store
                .get_audit_event(&event.id)
                .await
                .map_err(internal)?
                .ok_or_else(|| internal("Stripe audit event conflicted then disappeared"))?;
            if existing.instance_id != event.instance_id
                || existing.r#type != event.r#type
                || existing.data != event.data
            {
                return Err(internal("Stripe audit event ID collision"));
            }
        }
        Err(error) => return Err(internal(error)),
    }
    Ok(())
}

fn event_id(event: &Value) -> &str {
    event.get("id").and_then(Value::as_str).unwrap_or_default()
}

fn first_price_id(subscription: &Value) -> Option<&str> {
    subscription
        .pointer("/items/data/0/price/id")
        .and_then(Value::as_str)
        .or_else(|| {
            subscription
                .pointer("/items/0/price/id")
                .and_then(Value::as_str)
        })
}

fn price_plan(price_id: Option<&str>, state: &AppState) -> &'static str {
    configured_price_plan(
        price_id,
        price_for(PRO, state),
        price_for(ENTERPRISE, state),
    )
}

fn configured_price_plan(
    price_id: Option<&str>,
    pro_price: Option<&str>,
    enterprise_price: Option<&str>,
) -> &'static str {
    let Some(price_id) = price_id else {
        return FREE;
    };
    if pro_price == Some(price_id) {
        PRO
    } else if enterprise_price == Some(price_id) {
        ENTERPRISE
    } else {
        FREE
    }
}

pub(super) fn safe_stripe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 255
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

pub(super) async fn recover_completed_checkout(
    state: &AppState,
    session: &Value,
) -> Result<(), AppError> {
    let Some(session_id) = session.get("id").and_then(Value::as_str) else {
        return Ok(());
    };
    let event = json!({
        "id": format!("evt_checkout_recovery_{}", auth_security::tokens::sha256_hex(session_id)),
        "type": "checkout.session.completed",
        "created": unix_now(),
    });
    checkout_completed(state, &event, session, unix_now()).await
}

fn receipt_id(event_id: &str) -> String {
    format!("stripe_evt_{:x}", Sha256::digest(event_id.as_bytes()))
}

fn audit_id(source_id: &str) -> String {
    format!(
        "evt_stripe_{}",
        auth_security::tokens::sha256_hex(source_id)
    )
}

fn subscription_record_id(customer_id: &str) -> String {
    format!("sub_{}", auth_security::tokens::sha256_hex(customer_id))
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |value| value.as_secs() as i64)
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn signed_header(secret: &str, timestamp: i64, payload: &[u8]) -> String {
        let mut signed = timestamp.to_string().into_bytes();
        signed.push(b'.');
        signed.extend_from_slice(payload);
        format!(
            "t={timestamp},v1={}",
            hex(&auth_security::tokens::hmac_sha256(
                secret.as_bytes(),
                &signed
            ))
        )
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02x}")).collect()
    }

    #[test]
    fn verifies_signed_raw_payload_and_rejects_tampering_or_stale_timestamp() {
        let secret = "whsec_test";
        let payload = br#"{"id":"evt_1"}"#;
        let header = signed_header(secret, 1_700_000_000, payload);

        assert_eq!(
            verify_signature(secret, Some(&header), payload, 1_700_000_000),
            SignatureStatus::Valid
        );
        assert_eq!(
            verify_signature(secret, Some(&header), br#"{"id":"evt_2"}"#, 1_700_000_000),
            SignatureStatus::Invalid
        );
        assert_eq!(
            verify_signature(secret, Some(&header), payload, 1_700_000_301),
            SignatureStatus::Stale
        );
    }

    #[test]
    fn stripe_post_sends_the_required_idempotency_key() {
        let client = reqwest::Client::new();
        let request = post_request(
            &client,
            reqwest::Url::parse("https://api.stripe.test/v1/checkout/sessions").unwrap(),
            "sk_test",
            &[
                ("mode".into(), "subscription".into()),
                ("customer".into(), "cus_linked".into()),
            ],
            Some("checkout-123"),
        )
        .build()
        .unwrap();

        assert_eq!(
            request.headers().get("Idempotency-Key").unwrap(),
            "checkout-123"
        );
        let body = request.body().unwrap().as_bytes().unwrap();
        assert!(
            std::str::from_utf8(body)
                .unwrap()
                .contains("customer=cus_linked")
        );
    }

    #[test]
    fn customer_subscription_key_is_stable_and_customer_scoped() {
        assert_eq!(
            subscription_record_id("cus_same"),
            subscription_record_id("cus_same")
        );
        assert_ne!(
            subscription_record_id("cus_same"),
            subscription_record_id("cus_other")
        );
    }

    #[test]
    fn unknown_or_missing_reconciled_prices_fail_closed_to_free() {
        assert_eq!(
            configured_price_plan(
                Some("price_pro"),
                Some("price_pro"),
                Some("price_enterprise")
            ),
            PRO
        );
        assert_eq!(
            configured_price_plan(
                Some("price_enterprise"),
                Some("price_pro"),
                Some("price_enterprise")
            ),
            ENTERPRISE
        );
        assert_eq!(
            configured_price_plan(
                Some("price_unconfigured"),
                Some("price_pro"),
                Some("price_enterprise")
            ),
            FREE
        );
        assert_eq!(
            configured_price_plan(None, Some("price_pro"), Some("price_enterprise")),
            FREE
        );
        assert_eq!(
            configured_price_plan(Some("price_pro"), None, Some("price_enterprise")),
            FREE
        );
    }

    #[test]
    fn no_op_event_records_watermark_without_changing_subscription_state() {
        let subscription = Subscription {
            id: subscription_record_id("cus_test"),
            instance_id: "ins_test".into(),
            stripe_customer_id: "cus_test".into(),
            stripe_subscription_id: Some("sub_test".into()),
            plan: PRO.into(),
            status: "active".into(),
            current_period_end: Some(2_000),
            created_at: 100,
            updated_at: 100,
            last_stripe_event_created: Some(100),
            last_stripe_event_id: Some("evt_a".into()),
        };
        let event = json!({"id":"evt_noop","created":200});
        let (updated, changed) = apply_stripe_subscription(
            &subscription,
            StripeSubscriptionUpdate {
                stripe_subscription_id: "sub_test",
                plan: PRO,
                status: "active",
                period_end: Some(2_000),
                event: &event,
                event_created: 200,
                observed_at: 300,
            },
        );
        assert!(!changed);
        assert_eq!(updated.status, "active");
        assert_eq!(updated.updated_at, subscription.updated_at);
        assert_eq!(updated.last_stripe_event_created, Some(200));
        assert_eq!(updated.last_stripe_event_id.as_deref(), Some("evt_noop"));
    }

    #[test]
    fn delayed_event_uses_current_stripe_state_and_monotonic_update_time() {
        let subscription = Subscription {
            id: subscription_record_id("cus_test"),
            instance_id: "ins_test".into(),
            stripe_customer_id: "cus_test".into(),
            stripe_subscription_id: Some("sub_test".into()),
            plan: PRO.into(),
            status: "active".into(),
            current_period_end: Some(2_000),
            created_at: 100,
            updated_at: 200,
            last_stripe_event_created: Some(200),
            last_stripe_event_id: Some("evt_newer".into()),
        };
        let delayed_event = json!({"id":"evt_delayed","created":150});
        let (updated, changed) = apply_stripe_subscription(
            &subscription,
            StripeSubscriptionUpdate {
                stripe_subscription_id: "sub_test",
                plan: PRO,
                status: "canceled",
                period_end: Some(2_000),
                event: &delayed_event,
                event_created: 150,
                observed_at: 300,
            },
        );

        assert!(changed);
        assert_eq!(updated.status, "canceled");
        assert_eq!(updated.updated_at, 300);
        assert_eq!(updated.last_stripe_event_created, Some(150));
    }
}
