//! Tenant-scoped application RBAC. Every decision reads current membership,
//! identity and role state from the authority; no permission cache can extend a
//! removed membership or deleted role's lifetime.

use auth_contracts::{
    AppError, AppResult,
    models::{OrgMembership, OrganizationRole},
};
use auth_security::tokens::sha256_hex;
use auth_store::Predicate;
use axum::{
    Json, Router,
    extract::{Path, Query, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{get, post},
};
use serde::{Deserialize, Serialize};
use serde_json::json;
use utoipa::{OpenApi, ToSchema};

use crate::{
    AppState,
    app::storage_error,
    features::{management, users},
};

const COLLECTION: &str = "organization_roles";

fn now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

#[derive(OpenApi)]
#[openapi(paths(create_role, list_roles, get_role, update_role, delete_role, check),
    components(schemas(CreateRole, UpdateRole, RoleView, RolePage, CheckRequest, CheckResponse)),
    tags((name = "Authorization", description = "Organization role permissions and current-state authorization decisions")))]
struct ApiDoc;

pub fn openapi() -> utoipa::openapi::OpenApi {
    ApiDoc::openapi()
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/v1/{slug}/roles", get(list_roles).post(create_role))
        .route(
            "/v1/{slug}/roles/{key}",
            get(get_role).put(update_role).delete(delete_role),
        )
        .route("/v1/{slug}/authorization/check", post(check))
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct CreateRole {
    /// Exact membership role label; 1-64 ASCII letters, digits, underscores, colons, dots or hyphens.
    key: String,
    name: String,
    /// Explicit application permissions; no wildcard or implicit administrator grants.
    permissions: Vec<String>,
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct UpdateRole {
    name: String,
    permissions: Vec<String>,
}

#[derive(Serialize, ToSchema)]
struct RoleView {
    key: String,
    name: String,
    permissions: Vec<String>,
    created_at: i64,
    updated_at: i64,
}

impl From<OrganizationRole> for RoleView {
    fn from(role: OrganizationRole) -> Self {
        Self {
            key: role.key,
            name: role.name,
            permissions: role.permissions,
            created_at: role.created_at,
            updated_at: role.updated_at,
        }
    }
}

#[derive(Serialize, ToSchema)]
struct RolePage {
    items: Vec<RoleView>,
    next_cursor: Option<String>,
}

#[derive(Deserialize)]
struct Page {
    limit: Option<u32>,
    cursor: Option<String>,
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct CheckRequest {
    user_id: String,
    organization_id: String,
    /// All requested permissions must be granted. Must contain 1-100 entries.
    permissions: Vec<String>,
}

#[derive(Debug, Serialize, ToSchema)]
pub struct CheckResponse {
    pub allowed: bool,
    pub role: Option<String>,
    pub permissions: Vec<String>,
}

fn invalid(message: &str) -> AppError {
    AppError::new(StatusCode::BAD_REQUEST, "invalid_request", message)
}

fn missing() -> AppError {
    AppError::new(
        StatusCode::NOT_FOUND,
        "role_not_found",
        "No active role with this key exists.",
    )
}

fn role_id(instance_id: &str, key: &str) -> String {
    format!("role_{}", sha256_hex(&format!("{instance_id}:{key}")))
}

fn valid_identifier(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b':' | b'.'))
}

fn validate(name: &str, permissions: &mut Vec<String>) -> AppResult<()> {
    if name.trim().is_empty() || name.len() > 128 || name.chars().any(char::is_control) {
        return Err(invalid(
            "name must contain 1-128 characters without control characters",
        ));
    }
    if permissions.len() > 100 || permissions.iter().any(|p| !valid_identifier(p, 128)) {
        return Err(invalid(
            "permissions must contain at most 100 explicit identifiers of 1-128 characters",
        ));
    }
    permissions.sort();
    permissions.dedup();
    Ok(())
}

fn version_header(headers: &HeaderMap) -> AppResult<&str> {
    let value = headers
        .get(header::IF_MATCH)
        .and_then(|v| v.to_str().ok())
        .ok_or_else(|| {
            AppError::new(
                StatusCode::PRECONDITION_REQUIRED,
                "version_required",
                "Send the ETag from GET in If-Match.",
            )
        })?;
    if value.len() < 3
        || !value.starts_with('"')
        || !value.ends_with('"')
        || value[1..value.len() - 1].contains(['"', ','])
    {
        return Err(invalid("If-Match must contain one strong ETag from GET"));
    }
    Ok(value)
}

fn changed() -> AppError {
    AppError::new(
        StatusCode::PRECONDITION_FAILED,
        "role_changed",
        "The role changed; reload before retrying.",
    )
}

async fn active_role(
    state: &AppState,
    instance_id: &str,
    key: &str,
) -> AppResult<(OrganizationRole, String)> {
    state
        .store
        .get_versioned::<OrganizationRole>(COLLECTION, &role_id(instance_id, key))
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|(r, _)| r.instance_id == instance_id && r.key == key && !r.deleted)
        .ok_or_else(missing)
}

#[utoipa::path(post, path = "/v1/{slug}/roles", params(("slug" = String, Path)), request_body = CreateRole,
    responses((status = 201, description = "Created role", body = RoleView), (status = 400, description = "Invalid role"), (status = 409, description = "Key is already reserved")), tag = "Authorization")]
async fn create_role(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(mut body): Json<CreateRole>,
) -> AppResult<Response> {
    let context = management::require(&state, &headers, &slug).await?;
    if !valid_identifier(&body.key, 64) || matches!(body.key.as_str(), "." | "..") {
        return Err(invalid("key must be a 1-64 character identifier"));
    }
    validate(&body.name, &mut body.permissions)?;
    let now = now();
    let role = OrganizationRole {
        id: role_id(&context.instance.id, &body.key),
        instance_id: context.instance.id.clone(),
        key: body.key,
        name: body.name.trim().to_owned(),
        permissions: body.permissions,
        deleted: false,
        created_at: now,
        updated_at: now,
    };
    state
        .store
        .create(COLLECTION, &role.id, &role)
        .await
        .map_err(|e| {
            if e.is_conflict() || e.is_precondition() {
                AppError::new(
                    StatusCode::CONFLICT,
                    "role_exists",
                    "This role key is already reserved, including deleted roles.",
                )
            } else {
                storage_error(&e)
            }
        })?;
    users::audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "role.created",
        json!({"key": role.key, "permissions": role.permissions}),
    )
    .await?;
    Ok((StatusCode::CREATED, Json(RoleView::from(role))).into_response())
}

#[utoipa::path(get, path = "/v1/{slug}/roles", params(("slug" = String, Path), ("limit" = Option<u32>, Query), ("cursor" = Option<String>, Query)),
    responses((status = 200, description = "Page of active roles", body = RolePage)), tag = "Authorization")]
async fn list_roles(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Query(page): Query<Page>,
) -> AppResult<Json<RolePage>> {
    let context = management::require(&state, &headers, &slug).await?;
    let limit = page.limit.unwrap_or(50);
    if !(1..=100).contains(&limit) {
        return Err(invalid("limit must be 1-100"));
    }
    let predicates =
        [Predicate::equal("instanceId", &context.instance.id).map_err(|e| storage_error(&e))?];
    let (rows, next_cursor) = state
        .store
        .query_page::<OrganizationRole>(COLLECTION, &predicates, limit, page.cursor.as_deref())
        .await
        .map_err(|e| storage_error(&e))?;
    Ok(Json(RolePage {
        items: rows
            .into_iter()
            .map(|(r, _)| r)
            .filter(|r| !r.deleted && r.instance_id == context.instance.id)
            .map(RoleView::from)
            .collect(),
        next_cursor,
    }))
}

#[utoipa::path(get, path = "/v1/{slug}/roles/{key}", params(("slug" = String, Path), ("key" = String, Path)),
    responses((status = 200, description = "Role and ETag for conditional updates", body = RoleView), (status = 404, description = "Role not found")), tag = "Authorization")]
async fn get_role(
    State(state): State<AppState>,
    Path((slug, key)): Path<(String, String)>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let context = management::require(&state, &headers, &slug).await?;
    let (role, version) = active_role(&state, &context.instance.id, &key).await?;
    let mut response = Json(RoleView::from(role)).into_response();
    let etag = format!("\"{}\"", version.trim_matches('"'));
    response.headers_mut().insert(
        header::ETAG,
        HeaderValue::from_str(&etag).map_err(|_| AppError::internal())?,
    );
    Ok(response)
}

#[utoipa::path(put, path = "/v1/{slug}/roles/{key}", params(("slug" = String, Path), ("key" = String, Path), ("If-Match" = String, Header)), request_body = UpdateRole,
    responses((status = 200, description = "Updated role", body = RoleView), (status = 412, description = "Stale version"), (status = 428, description = "ETag required")), tag = "Authorization")]
async fn update_role(
    State(state): State<AppState>,
    Path((slug, key)): Path<(String, String)>,
    headers: HeaderMap,
    Json(mut body): Json<UpdateRole>,
) -> AppResult<Json<RoleView>> {
    let context = management::require(&state, &headers, &slug).await?;
    let expected = version_header(&headers)?;
    validate(&body.name, &mut body.permissions)?;
    let (mut role, version) = active_role(&state, &context.instance.id, &key).await?;
    if expected.trim_matches('"') != version.trim_matches('"') {
        return Err(changed());
    }
    role.name = body.name.trim().to_owned();
    role.permissions = body.permissions;
    role.updated_at = now();
    if !state
        .store
        .compare_and_put(COLLECTION, &role.id, &role, &version)
        .await
        .map_err(|e| storage_error(&e))?
    {
        return Err(changed());
    }
    users::audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "role.updated",
        json!({"key": role.key, "permissions": role.permissions}),
    )
    .await?;
    Ok(Json(role.into()))
}

#[utoipa::path(delete, path = "/v1/{slug}/roles/{key}", params(("slug" = String, Path), ("key" = String, Path), ("If-Match" = String, Header)),
    responses((status = 204, description = "Role permanently retired; its memberships no longer grant permissions"), (status = 412, description = "Stale version"), (status = 428, description = "ETag required")), tag = "Authorization")]
async fn delete_role(
    State(state): State<AppState>,
    Path((slug, key)): Path<(String, String)>,
    headers: HeaderMap,
) -> AppResult<StatusCode> {
    let context = management::require(&state, &headers, &slug).await?;
    let expected = version_header(&headers)?;
    let (mut role, version) = active_role(&state, &context.instance.id, &key).await?;
    if expected.trim_matches('"') != version.trim_matches('"') {
        return Err(changed());
    }
    role.deleted = true;
    role.permissions.clear();
    role.updated_at = now();
    if !state
        .store
        .compare_and_put(COLLECTION, &role.id, &role, &version)
        .await
        .map_err(|e| storage_error(&e))?
    {
        return Err(changed());
    }
    users::audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "role.deleted",
        json!({"key": role.key}),
    )
    .await?;
    Ok(StatusCode::NO_CONTENT)
}

/// Evaluate only explicitly defined application permissions. Hosted admin roles
/// and management-key scopes remain separate authorization systems.
pub async fn effective_permissions(
    state: &AppState,
    instance_id: &str,
    org_id: &str,
    user_id: &str,
) -> AppResult<CheckResponse> {
    let denied = || CheckResponse {
        allowed: false,
        role: None,
        permissions: vec![],
    };
    let Some(user) = state
        .store
        .get_user(user_id)
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|u| u.instance_id == instance_id && !u.deleted && !u.banned)
    else {
        return Ok(denied());
    };
    if !state
        .store
        .scim_auth_eligible(&user)
        .await
        .map_err(|e| storage_error(&e))?
    {
        return Ok(denied());
    }
    let Some(membership) = state
        .store
        .query_one::<OrgMembership>("memberships", "memberKey", &format!("{org_id}:{user_id}"))
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|m| m.instance_id == instance_id && m.org_id == org_id && m.user_id == user_id)
    else {
        return Ok(denied());
    };
    if !state
        .store
        .org_membership_auth_eligible(&membership)
        .await
        .map_err(|e| storage_error(&e))?
    {
        return Ok(denied());
    }
    let Some(role) = state
        .store
        .get::<OrganizationRole>(COLLECTION, &role_id(instance_id, &membership.role))
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|r| r.instance_id == instance_id && r.key == membership.role && !r.deleted)
    else {
        return Ok(denied());
    };
    Ok(CheckResponse {
        allowed: false,
        role: Some(role.key),
        permissions: role.permissions,
    })
}

#[utoipa::path(post, path = "/v1/{slug}/authorization/check", params(("slug" = String, Path)), request_body = CheckRequest,
    responses((status = 200, description = "All-of decision against current identity, membership and role state", body = CheckResponse), (status = 400, description = "Invalid check"), (status = 503, description = "Authority unavailable; never interpreted as permission granted")), tag = "Authorization")]
async fn check(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(body): Json<CheckRequest>,
) -> AppResult<Json<CheckResponse>> {
    let context = management::require(&state, &headers, &slug).await?;
    if body.user_id.is_empty()
        || body.user_id.len() > 256
        || body.organization_id.is_empty()
        || body.organization_id.len() > 256
        || body.permissions.is_empty()
        || body.permissions.len() > 100
        || body.permissions.iter().any(|p| !valid_identifier(p, 128))
    {
        return Err(invalid(
            "user_id, organization_id and 1-100 explicit permission identifiers are required",
        ));
    }
    let mut decision = effective_permissions(
        &state,
        &context.instance.id,
        &body.organization_id,
        &body.user_id,
    )
    .await?;
    decision.allowed = decision.role.is_some()
        && body
            .permissions
            .iter()
            .all(|p| decision.permissions.contains(p));
    Ok(Json(decision))
}
