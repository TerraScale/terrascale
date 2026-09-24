//! Per-instance JWT template management and token minting.

use std::time::{SystemTime, UNIX_EPOCH};

use auth_contracts::{
    AppError,
    models::{JwtTemplate, OrgMembership, User},
};
use auth_security::{jwt, tokens::random_token};
use auth_store::{Predicate, StoreError};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Request, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{get, patch, post},
};
use serde::{Deserialize, de::DeserializeOwned};
use serde_json::{Map, Value, json};
use utoipa::openapi::{
    Content, Info, OpenApi as OpenApiDoc, Required, Schema,
    path::{HttpMethod, Operation, Paths},
    request_body::RequestBodyBuilder,
    schema::{ObjectBuilder, Type},
};

use crate::{
    AppState,
    features::{management, management::InstanceContext},
};

const BODY_LIMIT: usize = 1024 * 1024;
const RESERVED_CLAIMS: &[&str] = &["token_use", "iss", "exp", "iat", "nbf"];

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/v1/{slug}/jwt-templates",
            get(list_templates).post(create_template),
        )
        .route(
            "/v1/{slug}/jwt-templates/{name}",
            patch(update_template).delete(delete_template),
        )
        .route("/v1/{slug}/jwt-templates/{name}/tokens", post(mint_token))
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/v1/{slug}/jwt-templates",
            HttpMethod::Get,
            "List JWT templates",
        ),
        (
            "/v1/{slug}/jwt-templates",
            HttpMethod::Post,
            "Create a JWT template",
        ),
        (
            "/v1/{slug}/jwt-templates/{name}",
            HttpMethod::Patch,
            "Update a JWT template",
        ),
        (
            "/v1/{slug}/jwt-templates/{name}",
            HttpMethod::Delete,
            "Delete a JWT template",
        ),
        (
            "/v1/{slug}/jwt-templates/{name}/tokens",
            HttpMethod::Post,
            "Mint a token from a template",
        ),
    ] {
        let mut operation = Operation::builder()
            .tag("JWT Templates")
            .summary(Some(summary));
        if method == HttpMethod::Post && path == "/v1/{slug}/jwt-templates" {
            operation = operation.request_body(Some(template_request_body(true)));
        } else if method == HttpMethod::Patch {
            operation = operation.request_body(Some(template_request_body(false)));
        } else if path.ends_with("/tokens") {
            operation = operation
                .description(Some("Mints a JWT with immutable token_use=template; it is not an OAuth access token."))
                .request_body(Some(RequestBodyBuilder::new()
                    .required(Some(Required::True))
                    .content("application/json", Content::new(Some(
                        ObjectBuilder::new().schema_type(Type::Object)
                            .property("user_id", ObjectBuilder::new().schema_type(Type::String))
                            .required("user_id")
                    )))
                    .build()));
        }
        paths.add_path_operation(path, vec![method], operation.build());
    }
    OpenApiDoc::new(
        Info::new("TerraScale Auth JWT Templates API", "1.0.0"),
        paths,
    )
}

fn template_request_body(create: bool) -> utoipa::openapi::request_body::RequestBody {
    let claims = ObjectBuilder::new()
        .schema_type(Type::Object)
        .description(Some("String-valued custom claims. token_use, iss, exp, iat, and nbf are reserved and rejected."))
        .additional_properties(Some(ObjectBuilder::new().schema_type(Type::String)));
    let mut schema = ObjectBuilder::new()
        .schema_type(Type::Object)
        .property("name", ObjectBuilder::new().schema_type(Type::String))
        .property("claims", claims)
        .property(
            "lifetime_seconds",
            ObjectBuilder::new().schema_type(Type::Integer),
        );
    if create {
        schema = schema.required("name").required("claims");
    }
    RequestBodyBuilder::new()
        .required(Some(Required::True))
        .content(
            "application/json",
            Content::new(Some(Schema::Object(schema.build()))),
        )
        .build()
}

#[derive(Deserialize)]
struct CreateTemplateBody {
    name: Option<String>,
    claims: Option<Map<String, Value>>,
    #[serde(default, rename = "lifetime_seconds")]
    lifetime_seconds: Option<i32>,
}

#[derive(Deserialize)]
struct UpdateTemplateBody {
    name: Option<String>,
    claims: Option<Map<String, Value>>,
    #[serde(default, rename = "lifetime_seconds")]
    lifetime_seconds: Option<i32>,
}

#[derive(Deserialize)]
struct MintBody {
    user_id: Option<String>,
}

async fn list_templates(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match state.store.list_jwt_templates(&context.instance.id).await {
        Ok(templates) => Json(Value::Array(
            templates
                .into_iter()
                .filter(|template| template.instance_id == context.instance.id)
                .map(template_view)
                .collect(),
        ))
        .into_response(),
        Err(error) => store_error(error),
    }
}

async fn create_template(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let body: CreateTemplateBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(name) = body.name.filter(|name| valid_name(name)) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name must be 1-48 url-safe characters (a-z, 0-9, -, _)",
        );
    };
    let Some(claims) = body.claims.filter(|claims| !claims.is_empty()) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "claims must be a non-empty object",
        );
    };
    let Some(claims) = string_claims(claims) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "claims must contain only string values",
        );
    };
    if has_reserved_claims(&claims) {
        return reserved_claim_error();
    }
    if body.lifetime_seconds.is_some_and(|lifetime| lifetime <= 0) {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "lifetime_seconds must be positive",
        );
    }
    let name = normalize_name(&name);
    let templates = match state.store.list_jwt_templates(&context.instance.id).await {
        Ok(templates) => templates,
        Err(error) => return store_error(error),
    };
    if templates.iter().any(|template| template.name == name) {
        return api_error(
            StatusCode::CONFLICT,
            "template_exists",
            "a template with this name already exists",
        );
    }
    let id = format!("jtp_{}", random_token(20));
    let name_key = template_name_key(&context.instance.id, &name);
    if let Err(error) = state
        .store
        .claim_unique("jwt-template-name", &name_key, &id)
        .await
    {
        return if error.is_conflict() {
            api_error(
                StatusCode::CONFLICT,
                "template_exists",
                "a template with this name already exists",
            )
        } else {
            store_error(error)
        };
    }
    let template = JwtTemplate {
        id,
        instance_id: context.instance.id,
        name,
        claims,
        lifetime_seconds: body.lifetime_seconds.unwrap_or(300),
        created_at: now(),
    };
    match state.store.create_jwt_template(&template).await {
        Ok(_) => Json(template_view(template)).into_response(),
        Err(error) => {
            // A transport error can arrive after the create-only write committed.
            // Only a definitive conflict with no document permits compensation.
            match state.store.get_jwt_template(&template.id).await {
                Ok(Some(stored)) if same_template(&stored, &template) => {
                    return Json(template_view(stored)).into_response();
                }
                Ok(None) if error.is_conflict() => {
                    if let Err(release_error) = release_name_claim(&state, &template).await {
                        return store_error(release_error);
                    }
                }
                Ok(_) => {}
                Err(read_error) => {
                    tracing::error!(?read_error, template_id = %template.id, "JWT template create readback failed");
                }
            }
            store_error(error)
        }
    }
}

async fn update_template(
    State(state): State<AppState>,
    Path((slug, route_name)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let template = match find_template(&state, &context, &route_name).await {
        Ok(Some(template)) => template,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "template_not_found",
                "no such jwt template",
            );
        }
        Err(response) => return response,
    };
    let body: UpdateTemplateBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    if body.name.as_deref().is_some_and(|name| !valid_name(name)) {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name must be 1-48 url-safe characters (a-z, 0-9, -, _)",
        );
    }
    if body.lifetime_seconds.is_some_and(|lifetime| lifetime <= 0) {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "lifetime_seconds must be positive",
        );
    }
    let claims = match body.claims {
        Some(claims) => match string_claims(claims) {
            Some(claims) if has_reserved_claims(&claims) => return reserved_claim_error(),
            Some(claims) => Some(claims),
            None => {
                return api_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_request",
                    "claims must contain only string values",
                );
            }
        },
        None => None,
    };
    let new_name = body.name.map(|name| normalize_name(&name));
    let rename_to = new_name.as_ref().filter(|name| *name != &template.name);
    if let Some(name) = rename_to {
        let name_key = template_name_key(&context.instance.id, name);
        if let Err(error) = state
            .store
            .claim_unique("jwt-template-name", &name_key, &template.id)
            .await
        {
            return store_error(error);
        }
    }
    for _ in 0..8 {
        let (current, version) = match state
            .store
            .get_versioned::<JwtTemplate>("jwt_templates", &template.id)
            .await
        {
            Ok(Some(value)) => value,
            Ok(None) => {
                if let Some(name) = rename_to
                    && let Err(error) =
                        release_name_claim_by_name(&state, &context.instance.id, name, &template.id)
                            .await
                {
                    return store_error(error);
                }
                return api_error(
                    StatusCode::NOT_FOUND,
                    "template_not_found",
                    "no such jwt template",
                );
            }
            Err(error) => {
                if let Some(name) = rename_to
                    && let Err(release_error) =
                        release_name_claim_by_name(&state, &context.instance.id, name, &template.id)
                            .await
                {
                    return store_error(release_error);
                }
                return store_error(error);
            }
        };
        if current.instance_id != context.instance.id || current.name != template.name {
            if let Some(name) = rename_to
                && let Err(error) =
                    release_name_claim_by_name(&state, &context.instance.id, name, &template.id)
                        .await
            {
                return store_error(error);
            }
            return api_error(
                StatusCode::CONFLICT,
                "template_changed",
                "the template changed; retry using its current name",
            );
        }
        let mut updated = current.clone();
        if let Some(name) = &new_name {
            updated.name = name.clone();
        }
        if let Some(claims) = &claims {
            updated.claims = claims.clone();
        }
        if let Some(lifetime) = body.lifetime_seconds {
            updated.lifetime_seconds = lifetime;
        }
        match state
            .store
            .compare_and_put("jwt_templates", &template.id, &updated, &version)
            .await
        {
            Ok(true) => {
                if rename_to.is_some() {
                    release_old_name_claim(&state, &current).await;
                }
                return Json(template_view(updated)).into_response();
            }
            Ok(false) => continue,
            Err(error) => {
                // The CAS write may have committed before the error reached us.
                // Retain the new name claim unless readback proves this exact update.
                if let Ok(Some(stored)) = state.store.get_jwt_template(&template.id).await
                    && same_template(&stored, &updated)
                {
                    if rename_to.is_some() {
                        release_old_name_claim(&state, &current).await;
                    }
                    return Json(template_view(stored)).into_response();
                }
                return store_error(error);
            }
        }
    }
    if let Some(name) = rename_to
        && let Err(error) =
            release_name_claim_by_name(&state, &context.instance.id, name, &template.id).await
    {
        return store_error(error);
    }
    store_error(StoreError::Contention)
}

async fn delete_template(
    State(state): State<AppState>,
    Path((slug, route_name)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let template = match find_template(&state, &context, &route_name).await {
        Ok(Some(template)) => template,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "template_not_found",
                "no such jwt template",
            );
        }
        Err(response) => return response,
    };
    for _ in 0..8 {
        let (current, version) = match state
            .store
            .get_versioned::<JwtTemplate>("jwt_templates", &template.id)
            .await
        {
            Ok(Some(value)) => value,
            Ok(None) => {
                return api_error(
                    StatusCode::NOT_FOUND,
                    "template_not_found",
                    "no such jwt template",
                );
            }
            Err(error) => return store_error(error),
        };
        if current.instance_id != context.instance.id || current.name != route_name {
            return api_error(
                StatusCode::NOT_FOUND,
                "template_not_found",
                "no such jwt template",
            );
        }
        match state
            .store
            .compare_and_delete("jwt_templates", &template.id, &version)
            .await
        {
            Ok(true) => {
                if let Err(error) = release_name_claim(&state, &current).await {
                    tracing::error!(?error, template_id = %current.id, "failed to release JWT template name claim");
                }
                return StatusCode::NO_CONTENT.into_response();
            }
            Ok(false) => continue,
            Err(error) => {
                // An ambiguous delete can still finish later; a confirmed absent
                // document permits owner-checked claim release.
                if let Ok(None) = state.store.get_jwt_template(&template.id).await {
                    if let Err(release_error) = release_name_claim(&state, &current).await {
                        tracing::error!(?release_error, template_id = %current.id, "failed to release JWT template name claim");
                    }
                    return StatusCode::NO_CONTENT.into_response();
                }
                return store_error(error);
            }
        }
    }
    store_error(StoreError::Contention)
}

async fn mint_token(
    State(state): State<AppState>,
    Path((slug, route_name)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let template = match find_template(&state, &context, &route_name).await {
        Ok(Some(template)) => template,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "template_not_found",
                "no such jwt template",
            );
        }
        Err(response) => return response,
    };
    let body: MintBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(user_id) = body.user_id.filter(|user_id| !user_id.trim().is_empty()) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "user_id is required",
        );
    };
    let user = match state.store.get_user(user_id.trim()).await {
        Ok(Some(user)) if user.instance_id == context.instance.id && !user.deleted => user,
        Ok(_) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no such user in this instance",
            );
        }
        Err(error) => return store_error(error),
    };
    if user.banned {
        return api_error(StatusCode::FORBIDDEN, "forbidden", "this user is banned");
    }
    match state.store.scim_auth_eligible(&user).await {
        Ok(true) => {}
        Ok(false) => {
            return api_error(
                StatusCode::FORBIDDEN,
                "forbidden",
                "this user is disabled by SCIM",
            );
        }
        Err(error) => return store_error(error),
    }
    let membership = if template
        .claims
        .values()
        .any(|value| has_org_placeholder(value))
    {
        let predicate = match Predicate::equal("userId", &user.id) {
            Ok(predicate) => predicate,
            Err(error) => return store_error(error),
        };
        match state
            .store
            .query_all::<OrgMembership>("memberships", &[predicate], 100)
            .await
        {
            Ok(items) => {
                let mut scoped: Vec<_> = items
                    .into_iter()
                    .filter(|item| item.instance_id == context.instance.id)
                    .collect();
                scoped.sort_by_key(|item| item.created_at);
                let mut selected = None;
                for item in scoped {
                    match state.store.org_membership_auth_eligible(&item).await {
                        Ok(true) => {
                            selected = Some(item);
                            break;
                        }
                        Ok(false) => {}
                        Err(error) => return store_error(error),
                    }
                }
                selected
            }
            Err(error) => return store_error(error),
        }
    } else {
        None
    };
    let claims = render_token_claims(
        &template,
        &user,
        membership.as_ref(),
        &format!(
            "{}/{}",
            state.options.public_origin.trim_end_matches('/'),
            slug
        ),
    );
    let signing_key = match state.key_ring.get_current(&context.instance.id).await {
        Ok(key) => key,
        Err(_) => return AppError::internal().into_response(),
    };
    let expires = now().saturating_add(i64::from(template.lifetime_seconds));
    match jwt::sign(Value::Object(claims), Some(expires), &signing_key, now()) {
        Ok(jwt) => Json(json!({"jwt":jwt})).into_response(),
        Err(error) => {
            tracing::error!(?error, "failed to sign a JWT template token");
            AppError::internal().into_response()
        }
    }
}

fn render_claim(value: &str, user: &User, membership: Option<&OrgMembership>) -> String {
    let mut rendered = String::with_capacity(value.len());
    let mut remaining = value;
    while let Some(open) = remaining.find("{{") {
        rendered.push_str(&remaining[..open]);
        let after_open = &remaining[open + 2..];
        let Some(close) = after_open.find("}}") else {
            rendered.push_str(&remaining[open..]);
            return rendered;
        };
        let raw_placeholder = &after_open[..close];
        let placeholder = raw_placeholder.trim();
        if !placeholder.is_empty()
            && placeholder
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte == b'_' || byte == b'.')
        {
            rendered.push_str(match placeholder {
                "user.id" => &user.id,
                "user.email" => user.email.as_deref().unwrap_or(""),
                "user.username" => user.username.as_deref().unwrap_or(""),
                "user.name" => user.name.as_deref().unwrap_or(""),
                "user.email_verified" => {
                    if user.email_verified {
                        "true"
                    } else {
                        "false"
                    }
                }
                "org.id" => membership.map_or("", |item| item.org_id.as_str()),
                "org.role" => membership.map_or("", |item| item.role.as_str()),
                _ => "",
            });
        } else {
            rendered.push_str("{{");
            rendered.push_str(raw_placeholder);
            rendered.push_str("}}");
        }
        remaining = &after_open[close + 2..];
    }
    rendered.push_str(remaining);
    rendered
}

fn has_org_placeholder(value: &str) -> bool {
    let mut remaining = value;
    while let Some(open) = remaining.find("{{") {
        let after_open = &remaining[open + 2..];
        let Some(close) = after_open.find("}}") else {
            return false;
        };
        if matches!(after_open[..close].trim(), "org.id" | "org.role") {
            return true;
        }
        remaining = &after_open[close + 2..];
    }
    false
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn resolve(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
) -> Result<InstanceContext, Response> {
    management::require(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn find_template(
    state: &AppState,
    context: &InstanceContext,
    name: &str,
) -> Result<Option<JwtTemplate>, Response> {
    let templates = state
        .store
        .list_jwt_templates(&context.instance.id)
        .await
        .map_err(store_error)?;
    Ok(templates
        .into_iter()
        .find(|template| template.instance_id == context.instance.id && template.name == name))
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn read_json<T: DeserializeOwned>(request: Request) -> Result<T, Response> {
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "a JSON body is required",
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

fn valid_name(name: &str) -> bool {
    let name = name.trim();
    (1..=48).contains(&name.len())
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
}

fn normalize_name(name: &str) -> String {
    name.trim().to_ascii_lowercase()
}

fn string_claims(claims: Map<String, Value>) -> Option<std::collections::BTreeMap<String, String>> {
    claims
        .into_iter()
        .map(|(key, value)| value.as_str().map(|value| (key, value.to_owned())))
        .collect()
}

fn has_reserved_claims(claims: &std::collections::BTreeMap<String, String>) -> bool {
    claims
        .keys()
        .any(|key| RESERVED_CLAIMS.contains(&key.as_str()))
}

fn reserved_claim_error() -> Response {
    api_error(
        StatusCode::BAD_REQUEST,
        "reserved_claim",
        "token_use, iss, exp, iat, and nbf are reserved template claims",
    )
}

fn render_token_claims(
    template: &JwtTemplate,
    user: &User,
    membership: Option<&OrgMembership>,
    issuer: &str,
) -> Map<String, Value> {
    let mut claims: Map<_, _> = template
        .claims
        .iter()
        .filter(|(key, _)| !RESERVED_CLAIMS.contains(&key.as_str()))
        .map(|(key, value)| {
            (
                key.clone(),
                Value::String(render_claim(value, user, membership)),
            )
        })
        .collect();
    claims.insert("token_use".into(), Value::String("template".into()));
    claims.insert("iss".into(), Value::String(issuer.to_owned()));
    claims
}

fn same_template(left: &JwtTemplate, right: &JwtTemplate) -> bool {
    left.id == right.id
        && left.instance_id == right.instance_id
        && left.name == right.name
        && left.claims == right.claims
        && left.lifetime_seconds == right.lifetime_seconds
        && left.created_at == right.created_at
}

async fn release_name_claim(state: &AppState, template: &JwtTemplate) -> Result<(), StoreError> {
    release_name_claim_by_name(state, &template.instance_id, &template.name, &template.id).await
}

async fn release_name_claim_by_name(
    state: &AppState,
    instance_id: &str,
    name: &str,
    id: &str,
) -> Result<(), StoreError> {
    state
        .store
        .release_unique_if_owned(
            "jwt-template-name",
            &template_name_key(instance_id, name),
            id,
        )
        .await?;
    Ok(())
}

async fn release_old_name_claim(state: &AppState, template: &JwtTemplate) {
    if let Err(error) = release_name_claim(state, template).await {
        tracing::error!(?error, template_id = %template.id, "failed to release old JWT template name claim");
    }
}

fn template_name_key(instance_id: &str, name: &str) -> String {
    format!("{instance_id}:{name}")
}

fn template_view(template: JwtTemplate) -> Value {
    json!({"id":template.id,"name":template.name,"claims":template.claims,"lifetime_seconds":template.lifetime_seconds,"created_at":template.created_at})
}

fn store_error(error: StoreError) -> Response {
    if error.is_conflict() {
        return api_error(
            StatusCode::CONFLICT,
            "template_exists",
            "a template with this name already exists",
        );
    }
    tracing::error!(?error, "JWT template persistence failed");
    AppError::internal().into_response()
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use auth_security::jwt::{Es256Key, JwtKey};
    use std::collections::BTreeMap;

    fn template(claims: BTreeMap<String, String>) -> JwtTemplate {
        JwtTemplate {
            id: "jtp_test".into(),
            instance_id: "ins_test".into(),
            name: "custom".into(),
            claims,
            lifetime_seconds: 300,
            created_at: 1,
        }
    }

    #[test]
    fn org_placeholder_detection_matches_rendering_whitespace() {
        assert!(has_org_placeholder("before {{ org.id }} after"));
        assert!(has_org_placeholder("{{user.id}}/{{ org.role }}"));
        assert!(!has_org_placeholder("{{ org.unknown }}"));
        assert!(!has_org_placeholder("{{ org.id"));
    }

    #[test]
    fn forged_access_claims_cannot_escape_template_token_use() {
        let claims = BTreeMap::from([
            ("token_use".into(), "access".into()),
            ("iss".into(), "https://attacker.example".into()),
            ("exp".into(), "9999999999".into()),
            ("iat".into(), "1".into()),
            ("nbf".into(), "1".into()),
            ("aud".into(), "client_test".into()),
            ("scope".into(), "openid profile".into()),
            ("sub".into(), "{{user.id}}".into()),
        ]);
        assert!(has_reserved_claims(&claims));
        let user: User =
            serde_json::from_value(json!({"id":"usr_test","instanceId":"ins_test"})).unwrap();
        let rendered =
            render_token_claims(&template(claims), &user, None, "https://auth.example/slug");
        let key = Es256Key::generate("kid_test");
        let secret = key.export_secret();
        let signed = jwt::sign(
            Value::Object(rendered),
            Some(1_700_000_300),
            &key,
            1_700_000_000,
        )
        .unwrap();
        let verified = jwt::verify(
            &signed,
            |kid| Some(Box::new(Es256Key::from_secret(kid, &secret).unwrap()) as Box<dyn JwtKey>),
            1_700_000_001,
        )
        .unwrap();
        assert_eq!(verified["token_use"], "template");
        assert_ne!(verified["token_use"], "access");
        assert_eq!(verified["iss"], "https://auth.example/slug");
        assert_eq!(verified["sub"], "usr_test");
        assert_eq!(verified["aud"], "client_test");
        assert_eq!(verified["iat"], 1_700_000_000);
        assert_eq!(verified["exp"], 1_700_000_300);
        assert!(verified.get("nbf").is_none());
    }

    #[test]
    fn openapi_describes_reserved_claims_and_token_purpose() {
        let document = serde_json::to_value(openapi()).unwrap().to_string();
        assert!(document.contains("token_use, iss, exp, iat, and nbf are reserved"));
        assert!(document.contains("token_use=template"));
    }
}
