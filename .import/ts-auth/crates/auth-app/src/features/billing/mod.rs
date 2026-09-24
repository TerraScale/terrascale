//! Stripe billing, plan quotas, and quota reconciliation.

mod stripe;

use auth_contracts::{
    AppError,
    models::{Instance, Subscription, User},
};
use auth_store::{Predicate, StoreError, StoreResult};
use axum::{
    Json, Router,
    body::{Body, to_bytes},
    extract::{Path, Request, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{get, post},
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::time::{SystemTime, UNIX_EPOCH};
use utoipa::openapi::{
    Info, OpenApi as OpenApiDoc,
    path::{HttpMethod, Operation, Paths},
};

use crate::{AppState, features::management};

const BODY_LIMIT: usize = 1024 * 1024;
const CHECKOUT_INTENT_LEASE_SECONDS: i64 = 90;
pub const FREE: &str = "free";
pub const PRO: &str = "pro";
pub const ENTERPRISE: &str = "enterprise";

/// Free, Pro, and Enterprise user limits. Unknown Stripe prices receive the free quota.
pub fn quota_for_plan(plan: &str) -> u32 {
    match plan {
        PRO => 10_000,
        ENTERPRISE => 1_000_000,
        _ => 100,
    }
}

#[derive(Debug, Clone)]
pub struct BillingState {
    pub plan: String,
    pub status: String,
    pub quota: u32,
    pub subscription: Option<Subscription>,
}

/// The newest active, trialing, or past-due subscription determines the plan.
pub async fn current(
    store: &auth_store::AuthStore,
    instance_id: &str,
) -> StoreResult<BillingState> {
    let subscriptions: Vec<Subscription> = store
        .query_all(
            "subscriptions",
            &[Predicate::equal("instanceId", instance_id)?],
            100,
        )
        .await?;
    let subscription = effective_subscription(subscriptions);
    Ok(match subscription {
        Some(subscription) => BillingState {
            quota: quota_for_plan(&subscription.plan),
            plan: subscription.plan.clone(),
            status: subscription.status.clone(),
            subscription: Some(subscription),
        },
        None => BillingState {
            plan: FREE.to_owned(),
            status: FREE.to_owned(),
            quota: quota_for_plan(FREE),
            subscription: None,
        },
    })
}

pub(super) async fn quota_after_subscription(
    store: &auth_store::AuthStore,
    instance_id: &str,
    replacement: &Subscription,
) -> StoreResult<u32> {
    let mut subscriptions: Vec<Subscription> = store
        .query_all(
            "subscriptions",
            &[Predicate::equal("instanceId", instance_id)?],
            100,
        )
        .await?;
    if let Some(existing) = subscriptions
        .iter_mut()
        .find(|value| value.id == replacement.id)
    {
        *existing = replacement.clone();
    } else {
        subscriptions.push(replacement.clone());
    }
    Ok(effective_subscription(subscriptions).map_or_else(
        || quota_for_plan(FREE),
        |subscription| quota_for_plan(&subscription.plan),
    ))
}

#[derive(Debug, Clone)]
pub(super) struct SubscriptionQuotaTransition {
    pub id: String,
    pub generation: u64,
    pub candidate: Subscription,
    source_version: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SubscriptionWriteState {
    Committed,
    Fenced,
    Unresolved,
}

/// Prepare the admission cap before writing a subscription. An existing
/// transition is resolved from its exact subscription row and pinned ETag;
/// uncertain writes keep the lower gate in place and block newer changes.
pub(super) async fn begin_subscription_quota_transition(
    store: &auth_store::AuthStore,
    instance_id: &str,
    candidate: &Subscription,
    source_version: Option<&str>,
    operation_id: &str,
) -> StoreResult<Option<SubscriptionQuotaTransition>> {
    for _ in 0..8 {
        if let Some(active) = store.active_quota_cap_transition(instance_id).await? {
            let active_state = subscription_write_state(
                store,
                &active.expected_subscription,
                active.source_version.as_deref(),
            )
            .await?;
            if active_state == SubscriptionWriteState::Committed {
                // The previous subscription write committed. Replace its gate
                // atomically only when this candidate was prepared against the
                // exact row version now on disk; a stale Stripe response must
                // not overwrite a newer event.
                if subscription_source_is_current(store, candidate, source_version).await? {
                    let (target_cap, transition_id) = projected_subscription_quota_transition(
                        store,
                        instance_id,
                        candidate,
                        source_version,
                        operation_id,
                    )
                    .await?;
                    let generation = if active.id == transition_id {
                        store
                            .begin_quota_cap_transition(
                                instance_id,
                                current(store, instance_id).await?.quota,
                                target_cap,
                                &transition_id,
                                candidate,
                                source_version,
                            )
                            .await?
                    } else {
                        store
                            .replace_quota_cap_transition(
                                instance_id,
                                &active.id,
                                active.generation,
                                target_cap,
                                &transition_id,
                                candidate,
                                source_version,
                            )
                            .await?
                    };
                    return refresh_subscription_quota_transition(
                        store,
                        instance_id,
                        operation_id,
                        SubscriptionQuotaTransition {
                            id: transition_id,
                            generation,
                            candidate: candidate.clone(),
                            source_version: source_version.map(str::to_owned),
                        },
                    )
                    .await
                    .map(Some);
                }

                store
                    .complete_quota_cap_transition(instance_id, &active.id, active.generation)
                    .await?;
                return Ok(None);
            }

            if active_state == SubscriptionWriteState::Fenced {
                store
                    .abort_quota_cap_transition(instance_id, &active.id, active.generation)
                    .await?;
                continue;
            }

            // Reissue only the exact same operation. The store verifies that
            // ID, row, target cap, and source ETag all still match; racing
            // retries write the same candidate under the same CAS.
            let (target_cap, transition_id) = projected_subscription_quota_transition(
                store,
                instance_id,
                candidate,
                source_version,
                operation_id,
            )
            .await?;
            if active.id == transition_id
                && active.expected_subscription.id == candidate.id
                && active.target_cap == target_cap
                && active.source_version.as_deref() == source_version
            {
                let generation = store
                    .begin_quota_cap_transition(
                        instance_id,
                        current(store, instance_id).await?.quota,
                        active.target_cap,
                        &transition_id,
                        &active.expected_subscription,
                        active.source_version.as_deref(),
                    )
                    .await?;
                return Ok(Some(SubscriptionQuotaTransition {
                    id: transition_id,
                    generation,
                    candidate: active.expected_subscription,
                    source_version: active.source_version,
                }));
            }

            // The source still has its pinned version (or has not appeared for
            // a create-only write), so the old request may still commit.
            return Err(StoreError::Contention);
        }

        if !subscription_source_is_current(store, candidate, source_version).await? {
            return Ok(None);
        }

        let (target_cap, transition_id) = projected_subscription_quota_transition(
            store,
            instance_id,
            candidate,
            source_version,
            operation_id,
        )
        .await?;
        let initial_cap = current(store, instance_id).await?.quota;
        match store
            .begin_quota_cap_transition(
                instance_id,
                initial_cap,
                target_cap,
                &transition_id,
                candidate,
                source_version,
            )
            .await
        {
            Ok(generation) => {
                return refresh_subscription_quota_transition(
                    store,
                    instance_id,
                    operation_id,
                    SubscriptionQuotaTransition {
                        id: transition_id,
                        generation,
                        candidate: candidate.clone(),
                        source_version: source_version.map(str::to_owned),
                    },
                )
                .await
                .map(Some);
            }
            Err(StoreError::Contention) => continue,
            Err(error) => return Err(error),
        }
    }

    Err(StoreError::Contention)
}

async fn projected_subscription_quota_transition(
    store: &auth_store::AuthStore,
    instance_id: &str,
    candidate: &Subscription,
    source_version: Option<&str>,
    operation_id: &str,
) -> StoreResult<(u32, String)> {
    let target_cap = quota_after_subscription(store, instance_id, candidate).await?;
    let transition_id =
        subscription_quota_transition_id(operation_id, candidate, source_version, target_cap);
    Ok((target_cap, transition_id))
}

/// A completed competing transition can land between projection and ledger
/// acquisition. Re-project while this intent holds the instance gate, then
/// replace its target atomically if the subscription set changed.
async fn refresh_subscription_quota_transition(
    store: &auth_store::AuthStore,
    instance_id: &str,
    operation_id: &str,
    mut transition: SubscriptionQuotaTransition,
) -> StoreResult<SubscriptionQuotaTransition> {
    for _ in 0..8 {
        let (target_cap, transition_id) = projected_subscription_quota_transition(
            store,
            instance_id,
            &transition.candidate,
            transition.source_version.as_deref(),
            operation_id,
        )
        .await?;
        if transition.id == transition_id {
            return Ok(transition);
        }
        let generation = store
            .replace_quota_cap_transition(
                instance_id,
                &transition.id,
                transition.generation,
                target_cap,
                &transition_id,
                &transition.candidate,
                transition.source_version.as_deref(),
            )
            .await?;
        transition = SubscriptionQuotaTransition {
            id: transition_id,
            generation,
            candidate: transition.candidate,
            source_version: transition.source_version,
        };
    }
    Err(StoreError::Contention)
}

/// Settle a prepared gate after a subscription write. A successful CAS raises
/// only after it is confirmed; an ambiguous write is settled by exact readback
/// and otherwise remains active to fail closed.
pub(super) async fn finish_subscription_quota_transition(
    store: &auth_store::AuthStore,
    instance_id: &str,
    transition: &SubscriptionQuotaTransition,
    write_result: StoreResult<bool>,
) -> StoreResult<bool> {
    let candidate = &transition.candidate;
    let source_version = transition.source_version.as_deref();
    match write_result {
        Ok(true) => {
            store
                .complete_quota_cap_transition(instance_id, &transition.id, transition.generation)
                .await?;
            Ok(true)
        }
        Ok(false) => {
            let state = subscription_write_state(store, candidate, source_version).await?;
            if state == SubscriptionWriteState::Committed {
                store
                    .complete_quota_cap_transition(
                        instance_id,
                        &transition.id,
                        transition.generation,
                    )
                    .await?;
                return Ok(true);
            }
            store
                .abort_quota_cap_transition(instance_id, &transition.id, transition.generation)
                .await?;
            Ok(false)
        }
        Err(error) => match subscription_write_state(store, candidate, source_version).await? {
            SubscriptionWriteState::Committed => {
                store
                    .complete_quota_cap_transition(
                        instance_id,
                        &transition.id,
                        transition.generation,
                    )
                    .await?;
                Ok(true)
            }
            SubscriptionWriteState::Fenced => {
                store
                    .abort_quota_cap_transition(instance_id, &transition.id, transition.generation)
                    .await?;
                Err(error)
            }
            SubscriptionWriteState::Unresolved => Err(error),
        },
    }
}

async fn subscription_source_is_current(
    store: &auth_store::AuthStore,
    candidate: &Subscription,
    source_version: Option<&str>,
) -> StoreResult<bool> {
    let current = store
        .get_versioned::<Subscription>("subscriptions", &candidate.id)
        .await?;
    Ok(match (source_version, current) {
        (Some(source), Some((_, version))) => version == source,
        (None, None) => true,
        _ => false,
    })
}

async fn subscription_write_state(
    store: &auth_store::AuthStore,
    expected: &Subscription,
    source_version: Option<&str>,
) -> StoreResult<SubscriptionWriteState> {
    let current = store
        .get_versioned::<Subscription>("subscriptions", &expected.id)
        .await?;
    if let Some((row, version)) = &current
        && same_subscription(row, expected)
    {
        return Ok(if source_version == Some(version.as_str()) {
            SubscriptionWriteState::Unresolved
        } else {
            SubscriptionWriteState::Committed
        });
    }

    Ok(match (source_version, current) {
        (Some(source), Some((_, version))) if version != source => SubscriptionWriteState::Fenced,
        (None, Some(_)) => SubscriptionWriteState::Fenced,
        _ => SubscriptionWriteState::Unresolved,
    })
}

pub(super) fn same_subscription(left: &Subscription, right: &Subscription) -> bool {
    serde_json::to_value(left).ok() == serde_json::to_value(right).ok()
}

fn subscription_quota_transition_id(
    operation_id: &str,
    candidate: &Subscription,
    source_version: Option<&str>,
    target_cap: u32,
) -> String {
    let source = source_version.map_or_else(|| "create".to_owned(), |v| format!("etag:{v}"));
    let key = format!("{operation_id}\0{}\0{source}\0{target_cap}", candidate.id);
    format!("billing_{}", auth_security::tokens::sha256_hex(&key))
}

fn effective_subscription(subscriptions: Vec<Subscription>) -> Option<Subscription> {
    subscriptions
        .into_iter()
        .filter(|value| matches!(value.status.as_str(), "active" | "trialing" | "past_due"))
        .max_by_key(|value| (value.updated_at, value.created_at))
}

/// Count live users up to `cap`, advancing by the raw collection cursor across tenants.
pub async fn count_users(
    store: &auth_store::AuthStore,
    instance_id: &str,
    cap: u32,
) -> StoreResult<u32> {
    let mut count = 0_u32;
    let mut after: Option<String> = None;
    while count < cap {
        let (page, next) = store
            .list_users(instance_id, 1_000, after.as_deref())
            .await?;
        count =
            count.saturating_add(page.iter().filter(|user: &&User| !user.deleted).count() as u32);
        match next {
            Some(cursor) if after.as_deref() != Some(cursor.as_str()) => after = Some(cursor),
            Some(_) => {
                return Err(StoreError::InvalidConfiguration(
                    "user cursor did not advance".into(),
                ));
            }
            None => return Ok(count),
        }
    }
    Ok(count)
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/v1/{slug}/billing", get(get_billing))
        .route("/v1/{slug}/billing/checkout", post(create_checkout))
        .route("/v1/{slug}/billing/portal", post(create_portal))
        .route("/v1/{slug}/billing/enforce-check", post(check_quota))
        .route("/v1/{slug}/billing/quota/reconcile", post(reconcile_quota))
        .route("/v1/billing/webhook", post(stripe::receive))
        .route("/ui/{slug}/billing", get(billing_page))
        .route("/ui/{slug}/billing/return", get(billing_return))
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/v1/{slug}/billing",
            HttpMethod::Get,
            "Read billing state and quota",
        ),
        (
            "/v1/{slug}/billing/checkout",
            HttpMethod::Post,
            "Create a Stripe checkout session",
        ),
        (
            "/v1/{slug}/billing/portal",
            HttpMethod::Post,
            "Create a Stripe billing portal session",
        ),
        (
            "/v1/{slug}/billing/enforce-check",
            HttpMethod::Post,
            "Check whether a user count is within quota",
        ),
        (
            "/v1/{slug}/billing/quota/reconcile",
            HttpMethod::Post,
            "Reconcile durable quota seat claims",
        ),
        (
            "/v1/billing/webhook",
            HttpMethod::Post,
            "Receive a signed Stripe event",
        ),
        (
            "/ui/{slug}/billing",
            HttpMethod::Get,
            "View the hosted billing page",
        ),
        (
            "/ui/{slug}/billing/return",
            HttpMethod::Get,
            "Return from Stripe checkout",
        ),
    ] {
        let mut operation = Operation::builder().tag("Billing").summary(Some(summary));
        if path == "/v1/{slug}/billing/enforce-check" {
            operation = operation.description(Some(
                "`allowed` compares the greater of the supplied user count and the current admission count against `quota`. `quota` is the effective admission cap and can temporarily differ from `plan_quota` while a Stripe plan change is being committed.",
            ));
        }
        paths.add_path_operation(path, vec![method], operation.build());
    }
    OpenApiDoc::new(Info::new("TerraScale Auth Billing API", "1.0.0"), paths)
}

async fn get_billing(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let billing = current(&state.store, &context.instance.id)
        .await
        .map_err(internal)?;
    let count = count_users(
        &state.store,
        &context.instance.id,
        billing.quota.saturating_add(1),
    )
    .await
    .map_err(internal)?;
    let subscription = billing.subscription.as_ref();
    Ok((StatusCode::OK, Json(json!({
        "plan": billing.plan,
        "status": billing.status,
        "stripe_customer_id": context.instance.stripe_customer_id.as_ref().or_else(|| subscription.map(|value| &value.stripe_customer_id)),
        "stripe_subscription_id": subscription.and_then(|value| value.stripe_subscription_id.as_ref()),
        "current_period_end": subscription.and_then(|value| value.current_period_end),
        "user_count": count,
        "quota": billing.quota,
    }))).into_response())
}

#[derive(Deserialize)]
struct CheckoutBody {
    plan: Option<String>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StripeCheckoutIntent {
    id: String,
    instance_id: String,
    caller_key_hash: String,
    #[serde(default)]
    checkout_nonce: String,
    plan: String,
    price_id: String,
    stripe_idempotency_key: String,
    status: String,
    claim_token: String,
    lease_until: i64,
    checkout_session_id: Option<String>,
    checkout_url: Option<String>,
    expires_at: Option<i64>,
    updated_at: i64,
}

enum CheckoutIntentClaim {
    Reuse(String),
    Acquired {
        token: String,
        stripe_key: String,
        checkout_nonce: String,
    },
    NeedsRecovery(String),
    Busy,
}

async fn create_checkout(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let body: CheckoutBody = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(plan) = body
        .plan
        .as_deref()
        .filter(|value| matches!(*value, PRO | ENTERPRISE))
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_plan",
            "plan must be \"pro\" or \"enterprise\"",
        ));
    };
    let Some(secret) = state.options.stripe_secret_key.as_deref() else {
        return Ok(billing_unconfigured());
    };
    let Some(price) = price_for(plan, &state) else {
        return Ok(billing_unconfigured());
    };
    let idempotency_key = headers
        .get("Idempotency-Key")
        .and_then(|value| value.to_str().ok())
        .unwrap_or_default();
    if idempotency_key.is_empty()
        || idempotency_key.len() > 255
        || !idempotency_key.bytes().all(|b| (0x21..=0x7e).contains(&b))
    {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_idempotency_key",
            "a printable Idempotency-Key header of at most 255 bytes is required",
        ));
    }
    if has_non_canceled_subscription(&state.store, &context.instance.id)
        .await
        .map_err(internal)?
    {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "subscription_exists",
            "manage an existing subscription through the billing portal",
        ));
    }
    let caller_key_hash = auth_security::tokens::sha256_hex(idempotency_key);
    let mut intent_claim = claim_checkout_intent(
        &state.store,
        &context.instance.id,
        &caller_key_hash,
        plan,
        price,
        unix_now(),
    )
    .await
    .map_err(internal)?;
    if let CheckoutIntentClaim::NeedsRecovery(session_id) = &intent_claim {
        let Some(secret) = state.options.stripe_secret_key.as_deref() else {
            return Ok(billing_unconfigured());
        };
        let current = match stripe::get(
            &state,
            secret,
            &format!("/v1/checkout/sessions/{session_id}"),
        )
        .await
        {
            Ok(value) => value,
            Err(error) => return Ok(stripe_error(error)),
        };
        if current.get("id").and_then(Value::as_str) != Some(session_id.as_str()) {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "checkout_in_progress",
                "Stripe has not confirmed the earlier checkout is terminal",
            ));
        }
        match current.get("status").and_then(Value::as_str) {
            Some("expired") if checkout_session_confirmed_expired(&current) => {
                finish_checkout_intent(&state.store, &context.instance.id, session_id, unix_now())
                    .await
                    .map_err(internal)?;
                intent_claim = claim_checkout_intent(
                    &state.store,
                    &context.instance.id,
                    &caller_key_hash,
                    plan,
                    price,
                    unix_now(),
                )
                .await
                .map_err(internal)?;
            }
            Some("complete")
                if current
                    .get("subscription")
                    .is_some_and(|value| !value.is_null()) =>
            {
                stripe::recover_completed_checkout(&state, &current).await?;
                return Ok(api_error(
                    StatusCode::CONFLICT,
                    "subscription_exists",
                    "manage the completed subscription through the billing portal",
                ));
            }
            _ => {
                return Ok(api_error(
                    StatusCode::CONFLICT,
                    "checkout_in_progress",
                    "Stripe has not confirmed the earlier checkout is terminal",
                ));
            }
        }
    }
    let (intent_token, stripe_idempotency_key, checkout_nonce) = match intent_claim {
        CheckoutIntentClaim::Reuse(url) => {
            return Ok((StatusCode::OK, Json(json!({"checkout_url": url}))).into_response());
        }
        CheckoutIntentClaim::Acquired {
            token,
            stripe_key,
            checkout_nonce,
        } => (token, stripe_key, checkout_nonce),
        CheckoutIntentClaim::NeedsRecovery(_) => {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "checkout_in_progress",
                "Stripe has not confirmed the earlier checkout is terminal",
            ));
        }
        CheckoutIntentClaim::Busy => {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "checkout_in_progress",
                "an earlier checkout is still active; retry with its Idempotency-Key",
            ));
        }
    };
    let origin = state.options.public_origin.trim_end_matches('/');
    let fields = vec![
        ("mode".to_owned(), "subscription".to_owned()),
        ("line_items[0][price]".to_owned(), price.to_owned()),
        ("line_items[0][quantity]".to_owned(), "1".to_owned()),
        (
            "success_url".to_owned(),
            format!("{origin}/ui/{slug}/billing/return?status=success"),
        ),
        (
            "cancel_url".to_owned(),
            format!("{origin}/ui/{slug}/billing/return?status=cancel"),
        ),
        (
            "client_reference_id".to_owned(),
            context.instance.id.clone(),
        ),
        (
            "metadata[instance_id]".to_owned(),
            context.instance.id.clone(),
        ),
        ("metadata[plan]".to_owned(), plan.to_owned()),
        ("metadata[ts_auth_intent_nonce]".to_owned(), checkout_nonce),
        (
            "subscription_data[metadata][instance_id]".to_owned(),
            context.instance.id.clone(),
        ),
        (
            "subscription_data[metadata][plan]".to_owned(),
            plan.to_owned(),
        ),
    ];
    let mut fields = fields;
    if let Some(customer) = context.instance.stripe_customer_id.as_deref() {
        fields.push(("customer".to_owned(), customer.to_owned()));
    }
    if has_non_canceled_subscription(&state.store, &context.instance.id)
        .await
        .map_err(internal)?
    {
        settle_checkout_claim_without_session(
            &state.store,
            &context.instance.id,
            &intent_token,
            unix_now(),
        )
        .await
        .map_err(internal)?;
        return Ok(api_error(
            StatusCode::CONFLICT,
            "subscription_exists",
            "manage an existing subscription through the billing portal",
        ));
    }
    let response = match stripe::post(
        &state,
        secret,
        "/v1/checkout/sessions",
        &fields,
        Some(&stripe_idempotency_key),
    )
    .await
    {
        Ok(value) => value,
        Err(error) => return Ok(stripe_error(error)),
    };
    let Some(url) = response.get("url").and_then(Value::as_str) else {
        return Ok(api_error(
            StatusCode::BAD_GATEWAY,
            "stripe_error",
            "Stripe returned an unusable checkout session.",
        ));
    };
    if !safe_redirect_url(url) {
        return Ok(api_error(
            StatusCode::BAD_GATEWAY,
            "stripe_error",
            "Stripe returned an unsafe checkout URL.",
        ));
    }
    let Some(session_id) = response
        .get("id")
        .and_then(Value::as_str)
        .filter(|value| stripe::safe_stripe_id(value))
    else {
        return Ok(api_error(
            StatusCode::BAD_GATEWAY,
            "stripe_error",
            "Stripe returned an unusable checkout session.",
        ));
    };
    let Some(expires_at) = response.get("expires_at").and_then(Value::as_i64) else {
        return Ok(api_error(
            StatusCode::BAD_GATEWAY,
            "stripe_error",
            "Stripe returned an unusable checkout session.",
        ));
    };
    if !persist_checkout_session(
        &state.store,
        &context.instance.id,
        &intent_token,
        session_id,
        url,
        expires_at,
        unix_now(),
    )
    .await
    .map_err(internal)?
    {
        return Err(internal("Stripe checkout intent lease was lost"));
    }
    Ok((StatusCode::OK, Json(json!({"checkout_url": url}))).into_response())
}

fn checkout_intent_id(instance_id: &str) -> String {
    format!(
        "checkout_{}",
        auth_security::tokens::sha256_hex(instance_id)
    )
}

pub(super) fn checkout_session_confirmed_expired(session: &Value) -> bool {
    session.get("status").and_then(Value::as_str) == Some("expired")
        && session.get("subscription").is_none_or(Value::is_null)
}

fn expired_intent_session_id(intent: &StripeCheckoutIntent, now: i64) -> Option<&str> {
    (intent.status == "open" && intent.expires_at.is_some_and(|expiry| expiry <= now))
        .then_some(intent.checkout_session_id.as_deref())
        .flatten()
}

async fn has_non_canceled_subscription(
    store: &auth_store::AuthStore,
    instance_id: &str,
) -> StoreResult<bool> {
    let subscriptions: Vec<Subscription> = store
        .query_all(
            "subscriptions",
            &[Predicate::equal("instanceId", instance_id)?],
            100,
        )
        .await?;
    Ok(subscriptions.iter().any(|subscription| {
        !matches!(
            subscription.status.as_str(),
            "canceled" | "incomplete_expired"
        )
    }))
}

async fn claim_checkout_intent(
    store: &auth_store::AuthStore,
    instance_id: &str,
    caller_key_hash: &str,
    plan: &str,
    price_id: &str,
    now: i64,
) -> StoreResult<CheckoutIntentClaim> {
    let id = checkout_intent_id(instance_id);
    let token = auth_security::tokens::random_token(24);
    for _ in 0..8 {
        let record = StripeCheckoutIntent {
            id: id.clone(),
            instance_id: instance_id.to_owned(),
            caller_key_hash: caller_key_hash.to_owned(),
            checkout_nonce: auth_security::tokens::random_token(32),
            plan: plan.to_owned(),
            price_id: price_id.to_owned(),
            stripe_idempotency_key: format!("tsauth_{}", auth_security::tokens::random_token(32)),
            status: "creating".into(),
            claim_token: token.clone(),
            lease_until: now.saturating_add(CHECKOUT_INTENT_LEASE_SECONDS),
            checkout_session_id: None,
            checkout_url: None,
            expires_at: None,
            updated_at: now,
        };
        match store.create("stripe_checkout_intents", &id, &record).await {
            Ok(()) => {
                return Ok(CheckoutIntentClaim::Acquired {
                    token,
                    stripe_key: record.stripe_idempotency_key,
                    checkout_nonce: record.checkout_nonce,
                });
            }
            Err(error) if error.is_conflict() => {}
            Err(error) => return Err(error),
        }
        let Some((mut existing, version)) = store
            .get_versioned::<StripeCheckoutIntent>("stripe_checkout_intents", &id)
            .await?
        else {
            continue;
        };
        if existing.instance_id != instance_id {
            return Err(StoreError::InvalidConfiguration(
                "Stripe checkout intent key collision".into(),
            ));
        }
        let same_request = existing.caller_key_hash == caller_key_hash
            && existing.plan == plan
            && existing.price_id == price_id;
        match existing.status.as_str() {
            "open" if same_request => {
                if existing.expires_at.is_some_and(|expiry| expiry > now)
                    && let Some(url) = existing.checkout_url
                {
                    return Ok(CheckoutIntentClaim::Reuse(url));
                }
                if let Some(session_id) = expired_intent_session_id(&existing, now) {
                    return Ok(CheckoutIntentClaim::NeedsRecovery(session_id.to_owned()));
                }
                return Ok(CheckoutIntentClaim::Busy);
            }
            "open" => {
                if let Some(session_id) = expired_intent_session_id(&existing, now) {
                    return Ok(CheckoutIntentClaim::NeedsRecovery(session_id.to_owned()));
                }
                return Ok(CheckoutIntentClaim::Busy);
            }
            "creating" if same_request => {
                if existing.lease_until > now {
                    return Ok(CheckoutIntentClaim::Busy);
                }
                if existing.checkout_nonce.is_empty() {
                    return Ok(CheckoutIntentClaim::Busy);
                }
                existing.claim_token.clone_from(&token);
                existing.lease_until = now.saturating_add(CHECKOUT_INTENT_LEASE_SECONDS);
                existing.updated_at = now;
                if store
                    .compare_and_put("stripe_checkout_intents", &id, &existing, &version)
                    .await?
                {
                    return Ok(CheckoutIntentClaim::Acquired {
                        token,
                        stripe_key: existing.stripe_idempotency_key,
                        checkout_nonce: existing.checkout_nonce,
                    });
                }
            }
            "complete" => {
                existing.caller_key_hash = caller_key_hash.to_owned();
                existing.checkout_nonce.clone_from(&record.checkout_nonce);
                existing.plan = plan.to_owned();
                existing.price_id = price_id.to_owned();
                existing.stripe_idempotency_key = record.stripe_idempotency_key;
                existing.status = "creating".into();
                existing.claim_token.clone_from(&token);
                existing.lease_until = now.saturating_add(CHECKOUT_INTENT_LEASE_SECONDS);
                existing.checkout_session_id = None;
                existing.checkout_url = None;
                existing.expires_at = None;
                existing.updated_at = now;
                if store
                    .compare_and_put("stripe_checkout_intents", &id, &existing, &version)
                    .await?
                {
                    return Ok(CheckoutIntentClaim::Acquired {
                        token,
                        stripe_key: existing.stripe_idempotency_key,
                        checkout_nonce: existing.checkout_nonce,
                    });
                }
            }
            _ => return Ok(CheckoutIntentClaim::Busy),
        }
    }
    Ok(CheckoutIntentClaim::Busy)
}

async fn persist_checkout_session(
    store: &auth_store::AuthStore,
    instance_id: &str,
    token: &str,
    session_id: &str,
    checkout_url: &str,
    expires_at: i64,
    now: i64,
) -> StoreResult<bool> {
    let id = checkout_intent_id(instance_id);
    for _ in 0..8 {
        let Some((mut intent, version)) = store
            .get_versioned::<StripeCheckoutIntent>("stripe_checkout_intents", &id)
            .await?
        else {
            return Ok(false);
        };
        if intent.claim_token != token || intent.status != "creating" {
            return Ok(false);
        }
        intent.status = "open".into();
        intent.checkout_session_id = Some(session_id.to_owned());
        intent.checkout_url = Some(checkout_url.to_owned());
        intent.expires_at = Some(expires_at);
        intent.lease_until = 0;
        intent.updated_at = now;
        if store
            .compare_and_put("stripe_checkout_intents", &id, &intent, &version)
            .await?
        {
            return Ok(true);
        }
    }
    Ok(false)
}

async fn settle_checkout_claim_without_session(
    store: &auth_store::AuthStore,
    instance_id: &str,
    claim_token: &str,
    now: i64,
) -> StoreResult<bool> {
    let id = checkout_intent_id(instance_id);
    for _ in 0..8 {
        let Some((mut intent, version)) = store
            .get_versioned::<StripeCheckoutIntent>("stripe_checkout_intents", &id)
            .await?
        else {
            return Ok(false);
        };
        if !settle_checkout_claim_record(&mut intent, instance_id, claim_token, now) {
            return Ok(false);
        }
        if store
            .compare_and_put("stripe_checkout_intents", &id, &intent, &version)
            .await?
        {
            return Ok(true);
        }
    }
    Err(StoreError::Contention)
}

fn settle_checkout_claim_record(
    intent: &mut StripeCheckoutIntent,
    instance_id: &str,
    claim_token: &str,
    now: i64,
) -> bool {
    if intent.instance_id != instance_id
        || intent.status != "creating"
        || intent.claim_token != claim_token
    {
        return false;
    }
    intent.status = "complete".into();
    intent.lease_until = 0;
    intent.checkout_session_id = None;
    intent.checkout_url = None;
    intent.expires_at = None;
    intent.updated_at = now;
    true
}

pub(super) async fn finish_checkout_intent(
    store: &auth_store::AuthStore,
    instance_id: &str,
    session_id: &str,
    now: i64,
) -> StoreResult<()> {
    let id = checkout_intent_id(instance_id);
    for _ in 0..8 {
        let Some((mut intent, version)) = store
            .get_versioned::<StripeCheckoutIntent>("stripe_checkout_intents", &id)
            .await?
        else {
            return Ok(());
        };
        if intent.checkout_session_id.as_deref() != Some(session_id) || intent.status == "complete"
        {
            return Ok(());
        }
        intent.status = "complete".into();
        intent.lease_until = 0;
        intent.updated_at = now;
        if store
            .compare_and_put("stripe_checkout_intents", &id, &intent, &version)
            .await?
        {
            return Ok(());
        }
    }
    Err(StoreError::Contention)
}

async fn create_portal(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(secret) = state.options.stripe_secret_key.as_deref() else {
        return Ok(billing_unconfigured());
    };
    let Some(customer) = context.instance.stripe_customer_id.as_deref() else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "no_customer",
            "this instance has no Stripe customer yet; complete a checkout first",
        ));
    };
    let form = vec![
        ("customer".to_owned(), customer.to_owned()),
        (
            "return_url".to_owned(),
            format!(
                "{}/ui/{}/billing",
                state.options.public_origin.trim_end_matches('/'),
                slug
            ),
        ),
    ];
    let response =
        match stripe::post(&state, secret, "/v1/billing_portal/sessions", &form, None).await {
            Ok(value) => value,
            Err(error) => return Ok(stripe_error(error)),
        };
    let Some(url) = response
        .get("url")
        .and_then(Value::as_str)
        .filter(|value| safe_redirect_url(value))
    else {
        return Ok(api_error(
            StatusCode::BAD_GATEWAY,
            "stripe_error",
            "Stripe returned an unusable portal URL.",
        ));
    };
    Ok((StatusCode::OK, Json(json!({"portal_url": url}))).into_response())
}

#[derive(Deserialize)]
struct EnforceCheckBody {
    #[serde(alias = "current_users")]
    current_users: Option<i64>,
}

async fn check_quota(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let body: EnforceCheckBody = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(current_users) = body.current_users.filter(|value| *value >= 0) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "current_users must be a non-negative integer",
        ));
    };
    let billing = current(&state.store, &context.instance.id)
        .await
        .map_err(internal)?;
    let (admitted_users, effective_quota) = state
        .store
        .quota_admission_status(&context.instance.id, billing.quota)
        .await
        .map_err(internal)?;
    let reported_users = u64::try_from(current_users)
        .map_err(internal)?
        .max(admitted_users);
    Ok((
        StatusCode::OK,
        Json(json!({
            "allowed": reported_users < u64::from(effective_quota),
            "plan": billing.plan,
            "quota": effective_quota,
            "plan_quota": billing.quota,
        })),
    )
        .into_response())
}

async fn reconcile_quota(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let result = state
        .store
        .reconcile_quota_seats(&context.instance.id)
        .await
        .map_err(internal)?;
    Ok((
        StatusCode::OK,
        Json(json!({
            "claims_examined": result.claims_examined,
            "claims_removed": result.claims_removed,
            "claims_retained": result.claims_retained,
        })),
    )
        .into_response())
}

async fn billing_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    request: Request,
) -> Result<Response, AppError> {
    // This hosted account page intentionally accepts the management key as a query parameter,
    // matching the existing product flow. The key is only hashed for store lookup.
    let key = request.uri().query().and_then(|query| {
        url::form_urlencoded::parse(query.as_bytes())
            .find(|(name, _)| name == "key")
            .map(|(_, value)| value.into_owned())
    });
    let Some(key) = key.filter(|value| value.starts_with("sk_")) else {
        return Ok(ui_page(
            "Billing",
            "<h1>Not authorized</h1><p>A valid management key is required.</p>",
            StatusCode::UNAUTHORIZED,
        ));
    };
    let key_hash = auth_security::tokens::sha256_hex(&key);
    let api_key = state
        .store
        .query_one::<auth_contracts::models::ApiKey>("api_keys", "secretKey", &key_hash)
        .await
        .map_err(internal)?;
    let Some(api_key) = api_key.filter(|value| !value.revoked) else {
        return Ok(ui_page(
            "Billing",
            "<h1>Not authorized</h1><p>A valid management key is required.</p>",
            StatusCode::UNAUTHORIZED,
        ));
    };
    let instance = state
        .store
        .get::<Instance>("instances", &api_key.instance_id)
        .await
        .map_err(internal)?;
    let Some(instance) = instance.filter(|value| value.slug == slug) else {
        return Ok(ui_page(
            "Billing",
            "<h1>Not authorized</h1><p>A valid management key is required.</p>",
            StatusCode::UNAUTHORIZED,
        ));
    };
    let billing = current(&state.store, &instance.id)
        .await
        .map_err(internal)?;
    let count = count_users(&state.store, &instance.id, billing.quota.saturating_add(1))
        .await
        .map_err(internal)?;
    let renews = billing
        .subscription
        .as_ref()
        .and_then(|value| value.current_period_end)
        .map_or_else(|| "—".to_owned(), |end| end.to_string());
    let content = format!(
        "<h1>Plan: {}</h1><div class=\"list\"><div class=\"row\"><span>Status</span><span>{}</span></div><div class=\"row\"><span>Users</span><span>{count} of {}</span></div><div class=\"row\"><span>Stripe customer</span><span>{}</span></div><div class=\"row\"><span>Renews (Unix time)</span><span>{renews}</span></div></div>",
        escape_html(&billing.plan),
        escape_html(&billing.status),
        billing.quota,
        escape_html(instance.stripe_customer_id.as_deref().unwrap_or("—"))
    );
    Ok(ui_page("Billing", &content, StatusCode::OK))
}

async fn billing_return(Path(slug): Path<String>, request: Request) -> Response {
    let status = request.uri().query().and_then(|query| {
        url::form_urlencoded::parse(query.as_bytes())
            .find(|(name, _)| name == "status")
            .map(|(_, value)| value.into_owned())
    });
    let (title, content) = if status.as_deref() == Some("success") {
        (
            "Subscription confirmed",
            "<h1>Subscription confirmed</h1><p>Your subscription is now active.</p>",
        )
    } else {
        (
            "Checkout canceled",
            "<h1>Checkout canceled</h1><p>The checkout was canceled; nothing was charged.</p>",
        )
    };
    let _ = slug; // Keep the slug path segment explicit without reflecting untrusted input.
    ui_page(title, content, StatusCode::OK)
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

fn price_for<'a>(plan: &str, state: &'a AppState) -> Option<&'a str> {
    match plan {
        PRO => state.options.price_pro.as_deref(),
        ENTERPRISE => state.options.price_enterprise.as_deref(),
        _ => None,
    }
}

fn billing_unconfigured() -> Response {
    api_error(
        StatusCode::SERVICE_UNAVAILABLE,
        "billing_not_configured",
        "Stripe billing is not configured for this deployment",
    )
}

fn stripe_error(error: stripe::StripeError) -> Response {
    tracing::warn!(%error, "Stripe request failed");
    api_error(
        StatusCode::BAD_GATEWAY,
        "stripe_error",
        "Stripe was unreachable or returned an unusable response.",
    )
}

fn safe_redirect_url(value: &str) -> bool {
    let Ok(url) = reqwest::Url::parse(value) else {
        return false;
    };
    url.scheme() == "https"
        && url.host_str().is_some()
        && url.username().is_empty()
        && url.password().is_none()
}

fn ui_page(title: &str, content: &str, status: StatusCode) -> Response {
    let html = format!(
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{}</title><main>{content}</main></html>",
        escape_html(title)
    );
    (
        status,
        [
            (
                header::CONTENT_TYPE,
                HeaderValue::from_static("text/html; charset=utf-8"),
            ),
            (
                header::X_CONTENT_TYPE_OPTIONS,
                HeaderValue::from_static("nosniff"),
            ),
        ],
        Body::from(html),
    )
        .into_response()
}

fn escape_html(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());
    for character in value.chars() {
        match character {
            '&' => escaped.push_str("&amp;"),
            '<' => escaped.push_str("&lt;"),
            '>' => escaped.push_str("&gt;"),
            '"' => escaped.push_str("&quot;"),
            '\'' => escaped.push_str("&#39;"),
            _ => escaped.push(character),
        }
    }
    escaped
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "billing persistence failed");
    AppError::internal()
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn intent(status: &str, expires_at: Option<i64>) -> StripeCheckoutIntent {
        StripeCheckoutIntent {
            id: "checkout_test".into(),
            instance_id: "ins_test".into(),
            caller_key_hash: "hash".into(),
            checkout_nonce: "nonce_test".into(),
            plan: PRO.into(),
            price_id: "price_test".into(),
            stripe_idempotency_key: "tsauth_test".into(),
            status: status.into(),
            claim_token: "claim".into(),
            lease_until: 0,
            checkout_session_id: Some("cs_test".into()),
            checkout_url: Some("https://checkout.stripe.test/session".into()),
            expires_at,
            updated_at: 10,
        }
    }

    #[test]
    fn expired_open_intents_require_session_recovery_before_reuse() {
        let open = intent("open", Some(99));
        assert_eq!(expired_intent_session_id(&open, 100), Some("cs_test"));
        assert_eq!(expired_intent_session_id(&open, 98), None);
        assert_eq!(
            expired_intent_session_id(&intent("complete", Some(99)), 100),
            None
        );
    }

    #[test]
    fn checkout_claim_settlement_is_fenced_by_the_current_claim_token() {
        let mut creating = intent("creating", Some(99));
        creating.claim_token = "current_claim".into();
        assert!(!settle_checkout_claim_record(
            &mut creating,
            "ins_test",
            "stale_claim",
            200
        ));
        assert_eq!(creating.status, "creating");

        assert!(settle_checkout_claim_record(
            &mut creating,
            "ins_test",
            "current_claim",
            200
        ));
        assert_eq!(creating.status, "complete");
        assert_eq!(creating.lease_until, 0);
        assert_eq!(creating.checkout_session_id, None);
        assert_eq!(creating.checkout_url, None);
        assert_eq!(creating.expires_at, None);
        assert_eq!(creating.updated_at, 200);
    }

    #[test]
    fn intent_is_released_only_for_stripe_confirmed_expiry_without_subscription() {
        assert!(checkout_session_confirmed_expired(&json!({
            "status":"expired",
            "subscription":null
        })));
        assert!(checkout_session_confirmed_expired(
            &json!({"status":"expired"})
        ));
        assert!(!checkout_session_confirmed_expired(&json!({
            "status":"expired",
            "subscription":"sub_test"
        })));
        assert!(!checkout_session_confirmed_expired(&json!({
            "status":"complete",
            "subscription":null
        })));
    }
}
