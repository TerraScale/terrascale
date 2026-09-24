//! Organization management, invitations, memberships, and domain enrollment.
//!
//! Persisted records use the same camelCase document format as the original service.

use auth_contracts::{
    AppError,
    models::{
        AuditEvent, Instance, Invitation, OrgDomain, OrgMembership, Organization,
        OrganizationStatus, User, VerificationToken,
    },
};
use auth_security::tokens::{random_token, sha256_hex};
use auth_store::{Predicate, StoreError};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{delete, get, patch, post},
};
use serde::Deserialize;
use serde_json::{Value, json};
use utoipa::openapi::{
    Components, Content, Info, OpenApi as OpenApiDoc, Ref, RefOr, Required, Schema,
    path::{HttpMethod, Operation, Parameter, ParameterIn, Paths},
    request_body::RequestBodyBuilder,
    response::Response as OpenApiResponse,
    security::{HttpAuthScheme, HttpBuilder, SecurityRequirement, SecurityScheme},
};

use crate::{AppState, features::management};

const BODY_LIMIT: usize = 1024 * 1024;
const INVITATION_LIFETIME: i64 = 7 * 24 * 60 * 60;

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/v1/{slug}/organizations",
            post(create_organization).get(list_organizations),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}",
            get(get_organization)
                .patch(update_organization)
                .delete(delete_organization),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/memberships",
            post(create_membership).get(list_memberships),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/memberships/{membership_id}",
            patch(update_membership).delete(delete_membership),
        )
        .route(
            "/v1/{slug}/users/{user_id}/organizations",
            get(list_user_organizations),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/invitations",
            post(create_invitation).get(list_invitations),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/invitations/{invitation_id}/revoke",
            post(revoke_invitation),
        )
        .route(
            "/ui/{slug}/invitations/{invitation_id}/accept",
            get(accept_invitation),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/domains",
            post(create_domain).get(list_domains),
        )
        .route(
            "/v1/{slug}/organizations/{org_id}/domains/{domain_id}",
            delete(delete_domain),
        )
        .route("/v1/{slug}/domains/{domain}/enroll", post(enroll_domain))
}

struct DocQueryParameter {
    name: &'static str,
    description: &'static str,
    required: bool,
}

struct DocSuccess {
    status: &'static str,
    description: &'static str,
    schema: Option<&'static str>,
}

struct DocOperation {
    path: &'static str,
    method: HttpMethod,
    summary: &'static str,
    request_schema: Option<&'static str>,
    success: DocSuccess,
    errors: &'static [&'static str],
    protected: bool,
    media_type: &'static str,
    error_schema: &'static str,
    query_parameters: &'static [DocQueryParameter],
}

impl DocOperation {
    fn new(
        path: &'static str,
        method: HttpMethod,
        summary: &'static str,
        success_status: &'static str,
        success_description: &'static str,
        success_schema: Option<&'static str>,
    ) -> Self {
        Self {
            path,
            method,
            summary,
            request_schema: None,
            success: DocSuccess {
                status: success_status,
                description: success_description,
                schema: success_schema,
            },
            errors: &[],
            protected: true,
            media_type: "application/json",
            error_schema: "Error",
            query_parameters: &[],
        }
    }

    fn request(mut self, schema: &'static str) -> Self {
        self.request_schema = Some(schema);
        self
    }

    fn errors(mut self, statuses: &'static [&'static str]) -> Self {
        self.errors = statuses;
        self
    }

    fn public(mut self) -> Self {
        self.protected = false;
        self
    }

    fn media_type(mut self, media_type: &'static str, error_schema: &'static str) -> Self {
        self.media_type = media_type;
        self.error_schema = error_schema;
        self
    }

    fn query_parameters(mut self, parameters: &'static [DocQueryParameter]) -> Self {
        self.query_parameters = parameters;
        self
    }
}

fn schema_from_json(value: Value) -> Schema {
    serde_json::from_value(value).expect("the OpenAPI schema literals are valid")
}

fn add_schema(components: &mut Components, name: &str, value: Value) {
    components
        .schemas
        .insert(name.to_owned(), RefOr::T(schema_from_json(value)));
}

fn add_doc_operation(paths: &mut Paths, contract: DocOperation) {
    let mut operation = Operation::builder()
        .tag("Organizations")
        .summary(Some(contract.summary));

    for segment in contract.path.split('/') {
        if let Some(name) = segment
            .strip_prefix('{')
            .and_then(|name| name.strip_suffix('}'))
        {
            let mut parameter = Parameter::new(name);
            parameter.parameter_in = ParameterIn::Path;
            parameter.schema = Some(schema_from_json(json!({"type":"string"})).into());
            operation = operation.parameter(parameter);
        }
    }
    for parameter in contract.query_parameters {
        let mut openapi_parameter = Parameter::new(parameter.name);
        openapi_parameter.parameter_in = ParameterIn::Query;
        openapi_parameter.description = Some(parameter.description.to_owned());
        openapi_parameter.required = if parameter.required {
            Required::True
        } else {
            Required::False
        };
        openapi_parameter.schema = Some(schema_from_json(json!({"type":"string"})).into());
        operation = operation.parameter(openapi_parameter);
    }
    if let Some(request_schema) = contract.request_schema {
        operation = operation.request_body(Some(
            RequestBodyBuilder::new()
                .description(Some("JSON request payload"))
                .required(Some(Required::True))
                .content(
                    contract.media_type,
                    Content::new(Some(Ref::from_schema_name(request_schema))),
                )
                .build(),
        ));
    }

    let mut success = OpenApiResponse::new(contract.success.description);
    if let Some(schema) = contract.success.schema {
        success.content.insert(
            contract.media_type.to_owned(),
            Content::new(Some(Ref::from_schema_name(schema))),
        );
    }
    operation = operation.response(contract.success.status, success);
    for status in contract.errors {
        let description = match *status {
            "400" => "The request is invalid.",
            "401" => "Bearer authentication is required or invalid.",
            "403" => "The bearer credential does not allow this operation.",
            "404" => "The requested resource was not found.",
            "409" => "The request conflicts with existing state.",
            _ => "The server could not complete the operation.",
        };
        let mut response = OpenApiResponse::new(description);
        response.content.insert(
            contract.media_type.to_owned(),
            Content::new(Some(Ref::from_schema_name(contract.error_schema))),
        );
        operation = operation.response(*status, response);
    }
    if contract.protected {
        operation = operation.security(SecurityRequirement::new(
            "bearerAuth",
            std::iter::empty::<&str>(),
        ));
    }
    paths.add_path_operation(contract.path, vec![contract.method], operation.build());
}

/// Build the management API document for every route in this feature.
pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    let mut components = Components::new();
    add_schema(
        &mut components,
        "Error",
        json!({"type":"object","properties":{"error":{"type":"object","properties":{"code":{"type":"string"},"message":{"type":"string"}},"required":["code","message"]}},"required":["error"]}),
    );
    add_schema(&mut components, "HtmlPage", json!({"type":"string"}));
    add_schema(
        &mut components,
        "Organization",
        json!({"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"slug":{"type":"string"},"created_at":{"type":"integer","format":"int64"}},"required":["id","name","slug","created_at"]}),
    );
    add_schema(
        &mut components,
        "OrganizationList",
        json!({"type":"array","items":{"$ref":"#/components/schemas/Organization"}}),
    );
    add_schema(
        &mut components,
        "CreateOrganizationRequest",
        json!({"type":"object","properties":{"name":{"type":"string"},"slug":{"type":"string"}},"required":["name"]}),
    );
    add_schema(
        &mut components,
        "UpdateOrganizationRequest",
        json!({"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}),
    );
    add_schema(
        &mut components,
        "Membership",
        json!({"type":"object","properties":{"id":{"type":"string"},"organization_id":{"type":"string"},"user_id":{"type":"string"},"role":{"type":"string"},"created_at":{"type":"integer","format":"int64"}},"required":["id","organization_id","user_id","role","created_at"]}),
    );
    add_schema(
        &mut components,
        "MembershipWithUser",
        json!({"type":"object","properties":{"id":{"type":"string"},"organization_id":{"type":"string"},"user_id":{"type":"string"},"role":{"type":"string"},"created_at":{"type":"integer","format":"int64"},"user":{"type":["object","null"],"properties":{"id":{"type":"string"},"email":{"type":["string","null"]},"name":{"type":["string","null"]}}}},"required":["id","organization_id","user_id","role","created_at","user"]}),
    );
    add_schema(
        &mut components,
        "MembershipList",
        json!({"type":"array","items":{"$ref":"#/components/schemas/MembershipWithUser"}}),
    );
    add_schema(
        &mut components,
        "UserOrganizationMembership",
        json!({"type":"object","properties":{"id":{"type":"string"},"role":{"type":"string"},"created_at":{"type":"integer","format":"int64"},"organization":{"oneOf":[{"$ref":"#/components/schemas/Organization"},{"type":"null"}]}},"required":["id","role","created_at","organization"]}),
    );
    add_schema(
        &mut components,
        "UserOrganizationList",
        json!({"type":"array","items":{"$ref":"#/components/schemas/UserOrganizationMembership"}}),
    );
    add_schema(
        &mut components,
        "CreateMembershipRequest",
        json!({"type":"object","properties":{"user_id":{"type":"string"},"role":{"type":"string","minLength":1,"maxLength":64}},"required":["user_id","role"]}),
    );
    add_schema(
        &mut components,
        "UpdateMembershipRequest",
        json!({"type":"object","properties":{"role":{"type":"string","minLength":1,"maxLength":64}},"required":["role"]}),
    );
    add_schema(
        &mut components,
        "Invitation",
        json!({"type":"object","properties":{"id":{"type":"string"},"organization_id":{"type":"string"},"email":{"type":"string","format":"email"},"role":{"type":"string"},"status":{"type":"string","enum":["pending","accepted","revoked"]},"expires_at":{"type":"integer","format":"int64"},"created_at":{"type":"integer","format":"int64"}},"required":["id","organization_id","email","role","status","expires_at","created_at"]}),
    );
    add_schema(
        &mut components,
        "InvitationList",
        json!({"type":"array","items":{"$ref":"#/components/schemas/Invitation"}}),
    );
    add_schema(
        &mut components,
        "CreateInvitationRequest",
        json!({"type":"object","properties":{"email":{"type":"string","format":"email"},"role":{"type":"string","minLength":1,"maxLength":64}},"required":["email","role"]}),
    );
    add_schema(
        &mut components,
        "Domain",
        json!({"type":"object","properties":{"id":{"type":"string"},"organization_id":{"type":"string"},"domain":{"type":"string","format":"hostname"},"created_at":{"type":"integer","format":"int64"}},"required":["id","organization_id","domain","created_at"]}),
    );
    add_schema(
        &mut components,
        "DomainList",
        json!({"type":"array","items":{"$ref":"#/components/schemas/Domain"}}),
    );
    add_schema(
        &mut components,
        "CreateDomainRequest",
        json!({"type":"object","properties":{"domain":{"type":"string","format":"hostname"}},"required":["domain"]}),
    );
    add_schema(
        &mut components,
        "EnrollDomainRequest",
        json!({"type":"object","properties":{"user_id":{"type":"string"}},"required":["user_id"]}),
    );
    add_schema(
        &mut components,
        "EnrollDomainResponse",
        json!({"type":"object","properties":{"org_id":{"type":"string"}},"required":["org_id"]}),
    );
    components.add_security_scheme(
        "bearerAuth",
        SecurityScheme::Http(HttpBuilder::new().scheme(HttpAuthScheme::Bearer).build()),
    );

    const ACCEPT_QUERY: &[DocQueryParameter] = &[DocQueryParameter {
        name: "token",
        description: "Single-use invitation acceptance token.",
        required: true,
    }];
    for contract in [
        DocOperation::new(
            "/v1/{slug}/organizations",
            HttpMethod::Post,
            "Create organization",
            "200",
            "Organization created.",
            Some("Organization"),
        )
        .request("CreateOrganizationRequest")
        .errors(&["400", "401", "403", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations",
            HttpMethod::Get,
            "List organizations",
            "200",
            "Organizations in the instance.",
            Some("OrganizationList"),
        )
        .errors(&["401", "403", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}",
            HttpMethod::Get,
            "Get organization",
            "200",
            "The organization.",
            Some("Organization"),
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}",
            HttpMethod::Patch,
            "Update organization name",
            "200",
            "The updated organization.",
            Some("Organization"),
        )
        .request("UpdateOrganizationRequest")
        .errors(&["400", "401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}",
            HttpMethod::Delete,
            "Delete organization",
            "204",
            "Organization deleted; no response body.",
            None,
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/memberships",
            HttpMethod::Post,
            "Add organization member",
            "200",
            "The created membership.",
            Some("Membership"),
        )
        .request("CreateMembershipRequest")
        .errors(&["400", "401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/memberships",
            HttpMethod::Get,
            "List organization members",
            "200",
            "Memberships and visible user summaries.",
            Some("MembershipList"),
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/memberships/{membership_id}",
            HttpMethod::Patch,
            "Update membership role",
            "200",
            "The updated membership.",
            Some("Membership"),
        )
        .request("UpdateMembershipRequest")
        .errors(&["400", "401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/memberships/{membership_id}",
            HttpMethod::Delete,
            "Remove membership",
            "204",
            "Membership removed; no response body.",
            None,
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/users/{user_id}/organizations",
            HttpMethod::Get,
            "List organizations for a user",
            "200",
            "Memberships for the user with organization summaries.",
            Some("UserOrganizationList"),
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/invitations",
            HttpMethod::Post,
            "Create organization invitation",
            "200",
            "The pending invitation. The acceptance secret is delivered by email.",
            Some("Invitation"),
        )
        .request("CreateInvitationRequest")
        .errors(&["400", "401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/invitations",
            HttpMethod::Get,
            "List organization invitations",
            "200",
            "Invitations for the organization.",
            Some("InvitationList"),
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/invitations/{invitation_id}/revoke",
            HttpMethod::Post,
            "Revoke organization invitation",
            "200",
            "The revoked invitation.",
            Some("Invitation"),
        )
        .errors(&["401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/ui/{slug}/invitations/{invitation_id}/accept",
            HttpMethod::Get,
            "Accept invitation in hosted UI",
            "200",
            "HTML invitation status page.",
            Some("HtmlPage"),
        )
        .public()
        .media_type("text/html", "HtmlPage")
        .query_parameters(ACCEPT_QUERY)
        .errors(&["400", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/domains",
            HttpMethod::Post,
            "Enroll organization domain",
            "200",
            "The enrolled domain.",
            Some("Domain"),
        )
        .request("CreateDomainRequest")
        .errors(&["400", "401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/domains",
            HttpMethod::Get,
            "List organization domains",
            "200",
            "Domains enrolled for the organization.",
            Some("DomainList"),
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/organizations/{org_id}/domains/{domain_id}",
            HttpMethod::Delete,
            "Remove organization domain",
            "204",
            "Domain removed; no response body.",
            None,
        )
        .errors(&["401", "403", "404", "500"]),
        DocOperation::new(
            "/v1/{slug}/domains/{domain}/enroll",
            HttpMethod::Post,
            "Enroll user by verified email domain",
            "200",
            "The organization receiving the user.",
            Some("EnrollDomainResponse"),
        )
        .request("EnrollDomainRequest")
        .errors(&["400", "401", "403", "404", "500"]),
    ] {
        add_doc_operation(&mut paths, contract);
    }
    let mut document = OpenApiDoc::new(
        Info::new("TerraScale Auth Organizations API", "1.0.0"),
        paths,
    );
    document.components = Some(components);
    document
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "organization persistence failed");
    AppError::internal()
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn body_json<T: for<'de> Deserialize<'de>>(request: Request) -> Result<T, Response> {
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "request body is required",
            )
        })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "request body must be valid JSON",
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
    resolve_family(state, headers, slug, "organizations").await
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn resolve_family(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
    family: &str,
) -> Result<management::InstanceContext, Response> {
    let context = management::resolve_instance(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)?;
    let path = format!("/v1/{slug}/{family}");
    if !management::scope_allows(&context.scopes, &path) {
        return Err(api_error(
            StatusCode::FORBIDDEN,
            "insufficient_scope",
            "The API key's scopes do not allow this route.",
        ));
    }
    Ok(context)
}

async fn query_all<T: serde::de::DeserializeOwned>(
    state: &AppState,
    collection: &str,
    field: &str,
    value: &str,
) -> Result<Vec<T>, AppError> {
    let predicate = Predicate::equal(field, value).map_err(internal)?;
    state
        .store
        .query_all(collection, &[predicate], 100)
        .await
        .map_err(internal)
}

async fn find_org(
    state: &AppState,
    instance_id: &str,
    org_id: &str,
) -> Result<Option<Organization>, AppError> {
    let org = state
        .store
        .get("organizations", org_id)
        .await
        .map_err(internal)?;
    Ok(org.filter(|value: &Organization| {
        value.instance_id == instance_id && value.status == OrganizationStatus::Active
    }))
}

async fn membership_auth_eligible(
    state: &AppState,
    membership: &OrgMembership,
) -> Result<bool, AppError> {
    state
        .store
        .org_membership_auth_eligible(membership)
        .await
        .map_err(internal)
}

async fn has_auth_eligible_membership(
    state: &AppState,
    instance_id: &str,
    org_id: &str,
    user_id: &str,
) -> Result<bool, AppError> {
    let memberships: Vec<OrgMembership> = query_all(state, "memberships", "orgId", org_id).await?;
    for membership in memberships.into_iter().filter(|membership| {
        membership.instance_id == instance_id
            && membership.org_id == org_id
            && membership.user_id == user_id
    }) {
        if membership_auth_eligible(state, &membership).await? {
            return Ok(true);
        }
    }
    Ok(false)
}

async fn find_membership(
    state: &AppState,
    instance_id: &str,
    org_id: &str,
    id: &str,
) -> Result<Option<OrgMembership>, AppError> {
    let membership = state.store.get("memberships", id).await.map_err(internal)?;
    let Some(membership) = membership
        .filter(|value: &OrgMembership| value.instance_id == instance_id && value.org_id == org_id)
    else {
        return Ok(None);
    };
    if !membership_auth_eligible(state, &membership).await? {
        return Ok(None);
    }
    Ok(Some(membership))
}

fn org_view(org: &Organization, instance_id: &str) -> Value {
    json!({"id":org.id,"name":org.name,"slug":org.slug_key.strip_prefix(&format!("{instance_id}:")).unwrap_or(&org.slug_key),"created_at":org.created_at})
}

fn membership_view(membership: &OrgMembership) -> Value {
    json!({"id":membership.id,"organization_id":membership.org_id,"user_id":membership.user_id,"role":membership.role,"created_at":membership.created_at})
}

fn invitation_view(invitation: &Invitation) -> Value {
    let status = if invitation.status == "accepting" {
        "pending"
    } else {
        &invitation.status
    };
    json!({"id":invitation.id,"organization_id":invitation.org_id,"email":invitation.email,"role":invitation.role,"status":status,"expires_at":invitation.expires_at,"created_at":invitation.created_at})
}

fn invitation_acceptance_is_open(invitation: &Invitation, now: i64) -> bool {
    (invitation.status == "pending" && invitation.expires_at > now)
        || matches!(invitation.status.as_str(), "accepting" | "accepted")
}

fn domain_view(domain: &OrgDomain) -> Value {
    json!({"id":domain.id,"organization_id":domain.org_id,"domain":domain.domain,"created_at":domain.created_at})
}

fn valid_role(value: &str) -> bool {
    let role = value.trim();
    (1..=64).contains(&role.len())
}

fn slug_from_name(name: &str) -> Option<String> {
    let mut result = String::new();
    let mut pending_dash = false;
    for character in name.chars().flat_map(char::to_lowercase) {
        if character.is_ascii_alphanumeric() {
            if pending_dash && !result.is_empty() {
                result.push('-');
            }
            pending_dash = false;
            result.push(character);
        } else {
            pending_dash = true;
        }
    }
    if result.len() > 48 {
        result.truncate(48);
        while result.ends_with('-') {
            result.pop();
        }
    }
    (3..=48).contains(&result.len()).then_some(result)
}

fn valid_org_slug(value: &str) -> bool {
    (3..=48).contains(&value.len())
        && !value.starts_with('-')
        && !value.ends_with('-')
        && value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

fn audit_data(items: impl IntoIterator<Item = (String, Value)>) -> serde_json::Map<String, Value> {
    items.into_iter().collect()
}

async fn audit(
    state: &AppState,
    instance_id: &str,
    actor_id: Option<String>,
    event_type: &str,
    data: serde_json::Map<String, Value>,
) -> Result<(), AppError> {
    let event = AuditEvent {
        id: format!("evt_{}", random_token(20)),
        instance_id: instance_id.to_owned(),
        r#type: event_type.to_owned(),
        actor_id,
        data,
        created_at: now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    state
        .store
        .create_audit_event(&event)
        .await
        .map(|_| ())
        .map_err(internal)
}

async fn audit_organization_deleted(
    state: &AppState,
    instance_id: &str,
    organization_id: &str,
    actor_id: String,
) -> Result<(), AppError> {
    let data = audit_data([(String::from("organization_id"), json!(organization_id))]);
    let event = AuditEvent {
        id: format!("evt_org_deleted_{}", sha256_hex(organization_id)),
        instance_id: instance_id.to_owned(),
        r#type: "organization.deleted".to_owned(),
        actor_id: Some(actor_id),
        data: data.clone(),
        created_at: now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    match state.store.create_audit_event(&event).await {
        Ok(_) => Ok(()),
        Err(error) => {
            // A deterministic ID makes retries idempotent. Read back after any
            // error because the write may have committed before its response failed.
            let existing = state
                .store
                .get_audit_event(&event.id)
                .await
                .map_err(internal)?;
            if existing.is_some_and(|existing| {
                existing.instance_id == instance_id
                    && existing.r#type == "organization.deleted"
                    && existing.data == data
            }) {
                Ok(())
            } else {
                Err(internal(error))
            }
        }
    }
}

fn now() -> i64 {
    chrono_unix_now()
}
fn chrono_unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

#[derive(Deserialize)]
struct CreateOrganization {
    name: Option<String>,
    slug: Option<String>,
}
#[derive(Deserialize)]
struct UpdateOrganization {
    name: Option<String>,
}
#[derive(Deserialize)]
struct CreateMembership {
    #[serde(alias = "user_id")]
    user_id: Option<String>,
    role: Option<String>,
}
#[derive(Deserialize)]
struct UpdateMembership {
    role: Option<String>,
}
#[derive(Deserialize)]
struct CreateInvitation {
    email: Option<String>,
    role: Option<String>,
}
#[derive(Deserialize)]
struct EnrollBody {
    #[serde(alias = "user_id")]
    user_id: Option<String>,
}
#[derive(Deserialize)]
struct CreateDomain {
    domain: Option<String>,
}

async fn create_organization(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let body: CreateOrganization = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(name) = body
        .name
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.trim().to_owned())
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name is required",
        ));
    };
    let org_slug = if let Some(value) = body.slug.filter(|value| !value.trim().is_empty()) {
        value.trim().to_ascii_lowercase()
    } else if let Some(value) = slug_from_name(&name) {
        value
    } else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_slug",
            "a slug cannot be derived from this name; provide an explicit slug",
        ));
    };
    if !valid_org_slug(&org_slug) {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_slug",
            "slug must be 3-48 lowercase url-safe characters",
        ));
    }
    let slug_key = format!("{}:{org_slug}", context.instance.id);
    let current: Vec<Organization> =
        query_all(&state, "organizations", "instanceId", &context.instance.id).await?;
    if current
        .iter()
        .any(|organization| organization.slug_key == slug_key)
    {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "organization_exists",
            "an organization with this slug already exists",
        ));
    }
    let organization = Organization {
        id: format!("org_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        name,
        slug_key: slug_key.clone(),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: now(),
    };
    if let Err(error) = state.store.create_organization(&organization).await {
        if error.is_conflict() {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "organization_exists",
                "an organization with this slug already exists",
            ));
        }
        return Err(internal(error));
    }
    audit(
        &state,
        &context.instance.id,
        Some(context.api_key_id),
        "organization.created",
        audit_data([(String::from("organization_id"), json!(organization.id))]),
    )
    .await?;
    Ok((
        StatusCode::OK,
        Json(org_view(&organization, &context.instance.id)),
    )
        .into_response())
}

async fn list_organizations(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let organizations: Vec<Organization> =
        query_all(&state, "organizations", "instanceId", &context.instance.id).await?;
    let items: Vec<Value> = organizations
        .iter()
        .filter(|organization| organization.status == OrganizationStatus::Active)
        .map(|organization| org_view(organization, &context.instance.id))
        .collect();
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn get_organization(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    match find_org(&state, &context.instance.id, &org_id).await? {
        Some(organization) => Ok((
            StatusCode::OK,
            Json(org_view(&organization, &context.instance.id)),
        )
            .into_response()),
        None => Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        )),
    }
}

async fn update_organization(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some((mut organization, version)) = state
        .store
        .get_versioned::<Organization>("organizations", &org_id)
        .await
        .map_err(internal)?
        .filter(|(organization, _)| {
            organization.instance_id == context.instance.id
                && organization.status == OrganizationStatus::Active
        })
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let body: UpdateOrganization = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(name) = body.name.filter(|value| !value.trim().is_empty()) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name is required",
        ));
    };
    organization.name = name.trim().to_owned();
    let updated = state
        .store
        .compare_and_put("organizations", &organization.id, &organization, &version)
        .await
        .map_err(internal)?;
    if !updated {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "organization_changed",
            "the organization changed or was deleted; reload and retry",
        ));
    }
    Ok((
        StatusCode::OK,
        Json(org_view(&organization, &context.instance.id)),
    )
        .into_response())
}

async fn delete_organization(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = state
        .store
        .get_organization_for_deletion(&org_id)
        .await
        .map_err(internal)?
        .filter(|organization| organization.instance_id == context.instance.id)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    match state.store.delete_organization(&organization.id).await {
        Ok(()) => {}
        Err(StoreError::Contention) => {
            return Ok(api_error(
                StatusCode::SERVICE_UNAVAILABLE,
                "organization_deletion_in_progress",
                "organization cleanup is still in progress; retry the request",
            ));
        }
        Err(error) => return Err(internal(error)),
    }
    audit_organization_deleted(
        &state,
        &context.instance.id,
        &organization.id,
        context.api_key_id,
    )
    .await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

async fn create_membership(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let body: CreateMembership = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(user_id) = body
        .user_id
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.trim().to_owned())
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "user_id and role (1-64 characters) are required",
        ));
    };
    let Some(role) = body.role.filter(|value| valid_role(value)) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "user_id and role (1-64 characters) are required",
        ));
    };
    let user: Option<User> = state.store.get("users", &user_id).await.map_err(internal)?;
    let Some(user) = user.filter(|user| user.instance_id == context.instance.id && !user.deleted)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no such user in this instance",
        ));
    };
    if has_auth_eligible_membership(&state, &context.instance.id, &organization.id, &user.id)
        .await?
    {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "already_a_member",
            "the user is already a member of this organization",
        ));
    }
    let membership = OrgMembership {
        id: format!("mem_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        org_id: organization.id.clone(),
        user_id: user.id.clone(),
        member_key: format!("{}:{}", organization.id, user.id),
        role: role.trim().to_owned(),
        created_at: now(),
    };
    if let Err(error) = state.store.create_membership(&membership).await {
        if error.is_conflict() {
            let code = if has_auth_eligible_membership(
                &state,
                &context.instance.id,
                &organization.id,
                &user.id,
            )
            .await?
            {
                "already_a_member"
            } else {
                "membership_changed"
            };
            return Ok(api_error(
                StatusCode::CONFLICT,
                code,
                if code == "already_a_member" {
                    "the user is already a member of this organization"
                } else {
                    "membership could not be created; retry after reloading the organization"
                },
            ));
        }
        return Err(internal(error));
    }
    audit(
        &state,
        &context.instance.id,
        Some(context.api_key_id),
        "membership.created",
        audit_data([
            (String::from("organization_id"), json!(organization.id)),
            (String::from("membership_id"), json!(membership.id)),
            (String::from("user_id"), json!(membership.user_id)),
            (String::from("role"), json!(membership.role)),
        ]),
    )
    .await?;
    Ok((StatusCode::OK, Json(membership_view(&membership))).into_response())
}

async fn list_memberships(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let memberships: Vec<OrgMembership> =
        query_all(&state, "memberships", "orgId", &organization.id).await?;
    let mut items = Vec::with_capacity(memberships.len());
    for membership in memberships {
        if !membership_auth_eligible(&state, &membership).await? {
            continue;
        }
        let user: Option<User> = state
            .store
            .get("users", &membership.user_id)
            .await
            .map_err(internal)?;
        let summary = user
            .filter(|user| user.instance_id == context.instance.id && !user.deleted)
            .map(|user| json!({"id":user.id,"email":user.email,"name":user.name}));
        items.push(json!({"id":membership.id,"organization_id":membership.org_id,"user_id":membership.user_id,"role":membership.role,"created_at":membership.created_at,"user":summary}));
    }
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn update_membership(
    State(state): State<AppState>,
    Path((slug, org_id, membership_id)): Path<(String, String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let Some((mut membership, version)) = state
        .store
        .get_versioned::<OrgMembership>("memberships", &membership_id)
        .await
        .map_err(internal)?
        .filter(|(membership, _)| {
            membership.instance_id == context.instance.id && membership.org_id == organization.id
        })
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "membership_not_found",
            "no such membership in this organization",
        ));
    };
    if !membership_auth_eligible(&state, &membership).await? {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "membership_not_found",
            "no such membership in this organization",
        ));
    }
    let body: UpdateMembership = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(role) = body.role.filter(|value| valid_role(value)) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_role",
            "role must be 1-64 characters",
        ));
    };
    membership.role = role.trim().to_owned();
    let updated = state
        .store
        .compare_and_put("memberships", &membership.id, &membership, &version)
        .await
        .map_err(internal)?;
    if !updated {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "membership_changed",
            "the membership changed or was deleted; reload and retry",
        ));
    }
    audit(
        &state,
        &context.instance.id,
        Some(context.api_key_id),
        "membership.updated",
        audit_data([
            (String::from("organization_id"), json!(organization.id)),
            (String::from("membership_id"), json!(membership.id)),
            (String::from("user_id"), json!(membership.user_id)),
            (String::from("role"), json!(membership.role)),
        ]),
    )
    .await?;
    Ok((StatusCode::OK, Json(membership_view(&membership))).into_response())
}

async fn delete_membership(
    State(state): State<AppState>,
    Path((slug, org_id, membership_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let Some(membership) = find_membership(
        &state,
        &context.instance.id,
        &organization.id,
        &membership_id,
    )
    .await?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "membership_not_found",
            "no such membership in this organization",
        ));
    };
    state
        .store
        .delete_membership(&membership.id)
        .await
        .map_err(internal)?;
    audit(
        &state,
        &context.instance.id,
        Some(context.api_key_id),
        "membership.deleted",
        audit_data([
            (String::from("organization_id"), json!(organization.id)),
            (String::from("membership_id"), json!(membership.id)),
            (String::from("user_id"), json!(membership.user_id)),
        ]),
    )
    .await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

async fn list_user_organizations(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve_family(&state, &headers, &slug, "users").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let user: Option<User> = state.store.get("users", &user_id).await.map_err(internal)?;
    let Some(user) = user.filter(|user| user.instance_id == context.instance.id && !user.deleted)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no such user in this instance",
        ));
    };
    let memberships: Vec<OrgMembership> =
        query_all(&state, "memberships", "userId", &user.id).await?;
    let mut items = Vec::new();
    for membership in memberships
        .into_iter()
        .filter(|membership| membership.instance_id == context.instance.id)
    {
        if !membership_auth_eligible(&state, &membership).await? {
            continue;
        }
        let Some(organization) = find_org(&state, &context.instance.id, &membership.org_id).await?
        else {
            continue;
        };
        let summary = org_view(&organization, &context.instance.id);
        items.push(json!({"id":membership.id,"role":membership.role,"created_at":membership.created_at,"organization":summary}));
    }
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn create_invitation(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let body: CreateInvitation = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(email) = body.email.filter(|email| management_email_valid(email)) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_email",
            "email is not a valid address",
        ));
    };
    let Some(role) = body.role.filter(|value| valid_role(value)) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_role",
            "role must be 1-64 characters",
        ));
    };
    let email = crate::features::users::normalize_email(&email);
    let created_at = now();
    let invitation = Invitation {
        id: format!("inv_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        org_id: organization.id.clone(),
        email: email.clone(),
        role: role.trim().to_owned(),
        status: "pending".to_owned(),
        expires_at: created_at + INVITATION_LIFETIME,
        created_at,
    };
    state
        .store
        .create_invitation(&invitation)
        .await
        .map_err(internal)?;
    let secret = random_token(48);
    let token = VerificationToken {
        id: format!("vtok_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        user_id: Some(invitation.id.clone()),
        purpose: "invitation".to_owned(),
        authentication_method: None,
        authentication_next: None,
        user_auth_epoch: None,
        token_key: sha256_hex(&secret),
        email_snapshot: Some(email.clone()),
        expires_at: invitation.expires_at,
        consumed: false,
        failed_attempts: 0,
        created_at,
    };
    if let Err(error) = state
        .store
        .create("verification_tokens", &token.id, &token)
        .await
    {
        let _ = state
            .store
            .delete_ignoring_missing("invitations", &invitation.id)
            .await;
        return Err(internal(error));
    }
    audit(
        &state,
        &context.instance.id,
        Some(context.api_key_id),
        "invitation.created",
        audit_data([
            (String::from("organization_id"), json!(organization.id)),
            (String::from("email"), json!(invitation.email)),
        ]),
    )
    .await?;
    let origin = state.options.public_origin.trim_end_matches('/');
    let acceptance_url = format!(
        "{origin}/ui/{slug}/invitations/{}/accept?token={secret}",
        invitation.id
    );
    state
        .email
        .send(
            &email,
            &format!("You're invited to join {}", organization.name),
            &format!(
                "Accept your invitation to join {}: {acceptance_url}",
                organization.name
            ),
        )
        .await;
    Ok((StatusCode::OK, Json(invitation_view(&invitation))).into_response())
}

fn management_email_valid(email: &str) -> bool {
    crate::features::users::is_valid_email(email)
}

async fn list_invitations(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let invitations: Vec<Invitation> =
        query_all(&state, "invitations", "orgId", &organization.id).await?;
    let items: Vec<Value> = invitations.iter().map(invitation_view).collect();
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn revoke_invitation(
    State(state): State<AppState>,
    Path((slug, org_id, invitation_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let invitation: Option<Invitation> = state
        .store
        .get("invitations", &invitation_id)
        .await
        .map_err(internal)?;
    let Some(invitation) = invitation.filter(|invitation| {
        invitation.instance_id == context.instance.id && invitation.org_id == organization.id
    }) else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "invitation_not_found",
            "no such invitation for this organization",
        ));
    };
    match state
        .store
        .try_transition_invitation(&invitation.id, "pending", "revoked")
        .await
        .map_err(internal)?
    {
        Some(revoked) => Ok((StatusCode::OK, Json(invitation_view(&revoked))).into_response()),
        None => Ok(api_error(
            StatusCode::CONFLICT,
            "invitation_not_pending",
            "only pending invitations can be revoked",
        )),
    }
}

#[derive(Deserialize)]
struct AcceptQuery {
    token: Option<String>,
}

async fn accept_invitation(
    State(state): State<AppState>,
    Path((slug, invitation_id)): Path<(String, String)>,
    Query(query): Query<AcceptQuery>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let instance: Option<Instance> = query_all(&state, "instances", "slug", &slug)
        .await?
        .into_iter()
        .next();
    let Some(instance) = instance else {
        return Ok(invitation_page(
            "Invitation not found",
            "<div class=\"error\">This invitation link is not valid.</div>",
            StatusCode::NOT_FOUND,
        ));
    };
    let invitation: Option<Invitation> = state
        .store
        .get("invitations", &invitation_id)
        .await
        .map_err(internal)?;
    let Some(invitation) = invitation.filter(|invitation| invitation.instance_id == instance.id)
    else {
        return Ok(invitation_page(
            "Invitation not found",
            "<div class=\"error\">This invitation link is not valid.</div>",
            StatusCode::NOT_FOUND,
        ));
    };
    let now = now();
    let token_key = query.token.as_deref().map(sha256_hex);
    let verification = if let Some(token_key) = token_key.as_deref() {
        state
            .store
            .query_one::<VerificationToken>("verification_tokens", "tokenKey", token_key)
            .await
            .map_err(internal)?
    } else {
        None
    };
    let Some(verification) = verification.filter(|record| {
        record.instance_id == instance.id
            && record.user_id.as_deref() == Some(invitation.id.as_str())
            && record.purpose == "invitation"
            && (!record.consumed || invitation.status == "accepted")
            && (record.expires_at > now || invitation.status != "pending")
    }) else {
        return Ok(invitation_page(
            "Invalid link",
            "<div class=\"error\">This acceptance link is invalid or has already been used.</div>",
            StatusCode::BAD_REQUEST,
        ));
    };
    if !invitation_acceptance_is_open(&invitation, now) {
        return Ok(invitation_page(
            "Invitation no longer valid",
            "<div class=\"error\">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>",
            StatusCode::BAD_REQUEST,
        ));
    }
    let organization = find_org(&state, &instance.id, &invitation.org_id).await?;
    let Some(organization) = organization else {
        return Ok(invitation_page(
            "Organization unavailable",
            "<div class=\"error\">The organization that invited you no longer exists.</div>",
            StatusCode::BAD_REQUEST,
        ));
    };
    let origin = state.options.public_origin.trim_end_matches('/');
    let invited_user: Option<User> = state
        .store
        .find_user_by_email(&instance.id, &invitation.email)
        .await
        .map_err(internal)?;
    if invited_user.is_none() {
        let email = crate::features::ui::escape_html(&invitation.email);
        let html = format!(
            "<h1>Accept your invitation</h1><p>No account exists for <strong>{email}</strong> yet. Sign up with this email address first, then open this invitation link again.</p><p class=\"hint\"><a href=\"{origin}/ui/{slug}/sign-up\">Go to sign-up</a></p>"
        );
        return Ok(invitation_page(
            "Create your account",
            &html,
            StatusCode::OK,
        ));
    }
    let Some(session_token) = cookie_value(&headers, crate::features::ui::SESSION_COOKIE_NAME)
    else {
        let email = crate::features::ui::escape_html(&invitation.email);
        let html = format!(
            "<h1>Accept your invitation</h1><p>You're invited to join <strong>{}</strong> as <strong>{email}</strong>.</p><p class=\"hint\"><a href=\"{origin}/ui/{slug}/sign-in\">Sign in</a> with that email address to accept.</p>",
            crate::features::ui::escape_html(&organization.name)
        );
        return Ok(invitation_page("Sign in to accept", &html, StatusCode::OK));
    };
    let current = crate::features::users::resolve_session(&state, &instance.id, session_token)
        .await
        .map_err(|_| AppError::internal())?;
    let Some((_session, user)) = current else {
        return Ok(invitation_page(
            "Sign in to accept",
            &format!(
                "<h1>Accept your invitation</h1><p class=\"hint\"><a href=\"{origin}/ui/{slug}/sign-in\">Sign in</a> to accept this invitation.</p>"
            ),
            StatusCode::OK,
        ));
    };
    if crate::features::users::normalize_email(user.email.as_deref().unwrap_or_default())
        != invitation.email
    {
        let signed_in = crate::features::ui::escape_html(user.email.as_deref().unwrap_or(&user.id));
        let invited = crate::features::ui::escape_html(&invitation.email);
        let html = format!(
            "<h1>Accept your invitation</h1><div class=\"error\">You're signed in as <strong>{signed_in}</strong>, but this invitation is for <strong>{invited}</strong>.</div><p class=\"hint\"><a href=\"{origin}/ui/{slug}/sign-out\">Sign out</a>, then sign in with the invited email address and reopen this link.</p>"
        );
        return Ok(invitation_page("Wrong account", &html, StatusCode::OK));
    }
    let mut already_member =
        has_auth_eligible_membership(&state, &instance.id, &organization.id, &user.id).await?;
    let mut newly_accepted = false;
    if invitation.status != "accepted" {
        // The durable `accepting` marker alone grants no access: every retry still
        // passes the token-to-invitation check above and must authenticate the
        // mailbox named by this invitation below.
        let mut acceptance_in_progress = invitation.status == "accepting";
        if invitation.status == "pending" {
            acceptance_in_progress = state
                .store
                .try_transition_invitation(&invitation.id, "pending", "accepting")
                .await
                .map_err(internal)?
                .is_some();
            if !acceptance_in_progress {
                let current: Option<Invitation> = state
                    .store
                    .get("invitations", &invitation.id)
                    .await
                    .map_err(internal)?;
                acceptance_in_progress = current
                    .as_ref()
                    .is_some_and(|current| current.status == "accepting");
                if current
                    .as_ref()
                    .is_some_and(|current| current.status == "accepted")
                {
                    already_member = has_auth_eligible_membership(
                        &state,
                        &instance.id,
                        &organization.id,
                        &user.id,
                    )
                    .await?;
                    if !already_member {
                        return Ok(invitation_page(
                            "Invitation could not be accepted",
                            "<div class=\"error\">Please retry this invitation link.</div>",
                            StatusCode::CONFLICT,
                        ));
                    }
                } else if !acceptance_in_progress {
                    return Ok(invitation_page(
                        "Invitation no longer valid",
                        "<div class=\"error\">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>",
                        StatusCode::BAD_REQUEST,
                    ));
                }
            }
        }
        if acceptance_in_progress {
            if !already_member {
                let membership = invitation_membership(
                    &instance.id,
                    &organization.id,
                    &user.id,
                    &invitation.role,
                    &invitation.id,
                    invitation.created_at,
                );
                match create_membership_record(&state, &membership).await {
                    Ok(()) => {}
                    Err(error) if error.is_conflict() => {
                        already_member = has_auth_eligible_membership(
                            &state,
                            &instance.id,
                            &organization.id,
                            &user.id,
                        )
                        .await?;
                        if !already_member {
                            return Ok(invitation_page(
                                "Invitation could not be accepted",
                                "<div class=\"error\">Please retry this invitation link.</div>",
                                StatusCode::CONFLICT,
                            ));
                        }
                    }
                    // Keep the invitation in `accepting` and the token unused so the
                    // same authorized request can retry after a transient store failure.
                    Err(error) => return Err(internal(error)),
                }
            }
            newly_accepted = state
                .store
                .try_transition_invitation(&invitation.id, "accepting", "accepted")
                .await
                .map_err(internal)?
                .is_some();
            if !newly_accepted {
                let current: Option<Invitation> = state
                    .store
                    .get("invitations", &invitation.id)
                    .await
                    .map_err(internal)?;
                if !current
                    .as_ref()
                    .is_some_and(|current| current.status == "accepted")
                {
                    return Ok(invitation_page(
                        "Invitation could not be accepted",
                        "<div class=\"error\">Please retry this invitation link.</div>",
                        StatusCode::CONFLICT,
                    ));
                }
            }
        }
    }
    if !verification.consumed {
        // Membership and the accepted state are durable before the single-use link is
        // consumed. If consuming races another retry, the invitation is already safe.
        let _ = state
            .store
            .consume_verification_token(&verification.token_key, |record| {
                record.purpose == "invitation"
                    && record.instance_id == instance.id
                    && record.user_id.as_deref() == Some(invitation.id.as_str())
                    && !record.consumed
                    && record.expires_at > now
            })
            .await
            .map_err(internal)?;
    }
    if newly_accepted {
        audit(
            &state,
            &instance.id,
            Some(user.id.clone()),
            "invitation.accepted",
            audit_data([
                (String::from("organization_id"), json!(organization.id)),
                (String::from("invitation_id"), json!(invitation.id)),
            ]),
        )
        .await?;
    }
    let message = if invitation.status == "accepted" && !already_member {
        format!(
            "This invitation was already accepted for {}.",
            organization.name
        )
    } else if already_member {
        format!("You're already a member of {}.", organization.name)
    } else {
        format!("You've joined {}!", organization.name)
    };
    let html = format!(
        "<h1>Invitation accepted</h1><div class=\"success\">{}</div><p class=\"hint\"><a href=\"{origin}/ui/{slug}/sign-in\">Continue</a></p>",
        crate::features::ui::escape_html(&message)
    );
    Ok(invitation_page(
        "Invitation accepted",
        &html,
        StatusCode::OK,
    ))
}

fn cookie_value<'a>(headers: &'a HeaderMap, name: &str) -> Option<&'a str> {
    headers
        .get(axum::http::header::COOKIE)?
        .to_str()
        .ok()?
        .split(';')
        .find_map(|pair| {
            let (key, value) = pair.trim().split_once('=')?;
            (key == name).then_some(value)
        })
}

fn invitation_page(title: &str, body: &str, status: StatusCode) -> Response {
    crate::features::ui::page(title, body, None, status, None, None)
}

fn new_membership(instance_id: &str, org_id: &str, user_id: &str, role: &str) -> OrgMembership {
    OrgMembership {
        id: format!("mem_{}", random_token(20)),
        instance_id: instance_id.to_owned(),
        org_id: org_id.to_owned(),
        user_id: user_id.to_owned(),
        member_key: format!("{org_id}:{user_id}"),
        role: role.to_owned(),
        created_at: now(),
    }
}

fn invitation_membership(
    instance_id: &str,
    org_id: &str,
    user_id: &str,
    role: &str,
    invitation_id: &str,
    created_at: i64,
) -> OrgMembership {
    let retry_key = format!("{invitation_id}:{user_id}");
    OrgMembership {
        id: format!("mem_inv_{}", sha256_hex(&retry_key)),
        instance_id: instance_id.to_owned(),
        org_id: org_id.to_owned(),
        user_id: user_id.to_owned(),
        member_key: format!("{org_id}:{user_id}"),
        role: role.to_owned(),
        created_at,
    }
}

async fn create_membership_record(
    state: &AppState,
    membership: &OrgMembership,
) -> Result<(), auth_store::StoreError> {
    state.store.create_membership(membership).await?;
    Ok(())
}

async fn create_domain(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let body: CreateDomain = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(normalized_domain) = body.domain.as_deref().and_then(normalize_domain) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_domain",
            "domain must be 1-253 characters of valid hostname labels",
        ));
    };
    let domain = OrgDomain {
        id: format!("dom_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        org_id: organization.id.clone(),
        domain: normalized_domain.clone(),
        domain_key: format!("{}:{normalized_domain}", context.instance.id),
        created_at: now(),
    };
    if let Err(error) = state.store.create_org_domain(&domain).await {
        if error.is_conflict() {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "domain_exists",
                "this domain is already enrolled in an organization",
            ));
        }
        return Err(internal(error));
    }
    Ok((StatusCode::OK, Json(domain_view(&domain))).into_response())
}

fn normalize_domain(value: &str) -> Option<String> {
    let domain = value.trim().to_ascii_lowercase();
    if domain.is_empty() || domain.len() > 253 || domain.starts_with('.') || domain.contains("..") {
        return None;
    }
    let valid = domain.split('.').all(|label| {
        !label.is_empty()
            && label.len() <= 63
            && !label.starts_with('-')
            && !label.ends_with('-')
            && label
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
    });
    valid.then_some(domain)
}

async fn list_domains(
    State(state): State<AppState>,
    Path((slug, org_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let domains: Vec<OrgDomain> =
        query_all(&state, "org_domains", "instanceId", &context.instance.id).await?;
    let items: Vec<Value> = domains
        .iter()
        .filter(|domain| domain.org_id == organization.id)
        .map(domain_view)
        .collect();
    Ok((StatusCode::OK, Json(items)).into_response())
}

async fn delete_domain(
    State(state): State<AppState>,
    Path((slug, org_id, domain_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(organization) = find_org(&state, &context.instance.id, &org_id).await? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "organization_not_found",
            "no such organization",
        ));
    };
    let domain: Option<OrgDomain> = state
        .store
        .get("org_domains", &domain_id)
        .await
        .map_err(internal)?;
    let Some(domain) = domain.filter(|domain| {
        domain.instance_id == context.instance.id && domain.org_id == organization.id
    }) else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "domain_not_found",
            "no such domain for this organization",
        ));
    };
    state
        .store
        .delete_org_domain(&domain.id)
        .await
        .map_err(internal)?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

async fn enroll_domain(
    State(state): State<AppState>,
    Path((slug, domain)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match resolve_family(&state, &headers, &slug, "domains").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(normalized_domain) = normalize_domain(&domain) else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "domain_not_found",
            "no such enrolled domain",
        ));
    };
    let domain_key = format!("{}:{normalized_domain}", context.instance.id);
    let predicate = match Predicate::equal("domainKey", &domain_key) {
        Ok(value) => value,
        Err(error) => return Err(internal(error)),
    };
    let record: Option<OrgDomain> = state
        .store
        .query_page("org_domains", &[predicate], 1, None)
        .await
        .map_err(internal)?
        .0
        .into_iter()
        .next()
        .map(|(domain, _)| domain);
    let Some(record) = record.filter(|record| {
        record.instance_id == context.instance.id && record.domain == normalized_domain
    }) else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "domain_not_found",
            "no such enrolled domain",
        ));
    };
    let body: EnrollBody = match body_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some(user_id) = body
        .user_id
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.trim().to_owned())
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "user_id is required",
        ));
    };
    let user: Option<User> = state.store.get("users", &user_id).await.map_err(internal)?;
    let Some(user) = user.filter(|user| user.instance_id == context.instance.id && !user.deleted)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no such user in this instance",
        ));
    };
    if !user.email_verified {
        return Ok(api_error(
            StatusCode::FORBIDDEN,
            "email_not_verified",
            "the user's email address must be verified before domain enrollment",
        ));
    }
    if !user.email.as_deref().is_some_and(|email| {
        email
            .to_ascii_lowercase()
            .ends_with(&format!("@{normalized_domain}"))
    }) {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "email_domain_mismatch",
            &format!("the user's email is not @{normalized_domain}"),
        ));
    }
    if !has_auth_eligible_membership(&state, &context.instance.id, &record.org_id, &user.id).await?
    {
        let current_domain: Option<OrgDomain> = state
            .store
            .get("org_domains", &record.id)
            .await
            .map_err(internal)?;
        let domain_is_current = current_domain.is_some_and(|current| {
            current.instance_id == context.instance.id
                && current.org_id == record.org_id
                && current.domain == normalized_domain
        });
        if !domain_is_current {
            return Ok(api_error(
                StatusCode::NOT_FOUND,
                "domain_not_found",
                "no such enrolled domain",
            ));
        }
        if find_org(&state, &context.instance.id, &record.org_id)
            .await?
            .is_none()
        {
            return Ok(api_error(
                StatusCode::NOT_FOUND,
                "organization_not_found",
                "the organization that owns this domain no longer exists",
            ));
        }
        let membership = new_membership(&context.instance.id, &record.org_id, &user.id, "member");
        if let Err(error) = create_membership_record(&state, &membership).await {
            if !error.is_conflict() {
                return Err(internal(error));
            }
            if !has_auth_eligible_membership(&state, &context.instance.id, &record.org_id, &user.id)
                .await?
            {
                if find_org(&state, &context.instance.id, &record.org_id)
                    .await?
                    .is_none()
                {
                    return Ok(api_error(
                        StatusCode::NOT_FOUND,
                        "organization_not_found",
                        "the organization that owns this domain no longer exists",
                    ));
                }
                return Ok(api_error(
                    StatusCode::CONFLICT,
                    "membership_changed",
                    "membership could not be created; retry after reloading the organization",
                ));
            }
        }
    }
    audit(
        &state,
        &context.instance.id,
        Some(user.id.clone()),
        "organization.enrolled",
        audit_data([
            (String::from("organization_id"), json!(record.org_id)),
            (String::from("user_id"), json!(user.id)),
            (String::from("domain"), json!(normalized_domain)),
        ]),
    )
    .await?;
    Ok((StatusCode::OK, Json(json!({"org_id":record.org_id}))).into_response())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn organization_slug_derivation_collapses_punctuation_and_truncates() {
        assert_eq!(
            slug_from_name("Acme Corp & Sons!!").as_deref(),
            Some("acme-corp-sons")
        );
        assert_eq!(slug_from_name("!!").as_deref(), None);
        assert_eq!(
            slug_from_name(&format!("{} tail", "a".repeat(55))).as_deref(),
            Some("a".repeat(48).as_str())
        );
        assert!(!valid_org_slug("Upper-case"));
        assert!(!valid_org_slug("a--"));
    }

    #[test]
    fn roles_domains_and_enrollment_addresses_are_validated_at_boundaries() {
        assert!(valid_role(" member "));
        assert!(!valid_role("   "));
        assert!(!valid_role(&"x".repeat(65)));
        assert_eq!(
            normalize_domain(" Example.COM ").as_deref(),
            Some("example.com")
        );
        assert_eq!(normalize_domain("-bad.example"), None);
        assert_eq!(normalize_domain("bad..example"), None);
        assert_eq!(normalize_domain("example.com."), None);
        assert_eq!(normalize_domain(&"a".repeat(254)), None);
    }

    #[test]
    fn invitation_acceptance_remains_retryable_after_membership_failure() {
        let mut invitation = Invitation {
            id: "inv_1".to_owned(),
            instance_id: "ins_1".to_owned(),
            org_id: "org_1".to_owned(),
            email: "person@example.com".to_owned(),
            role: "member".to_owned(),
            status: "accepting".to_owned(),
            expires_at: 10,
            created_at: 1,
        };

        // An `accepting` invitation is only written after validating a live link. If
        // membership persistence failed, keeping this state allows the same request
        // to resume, even after the original invitation deadline.
        assert!(invitation_acceptance_is_open(&invitation, 20));
        assert_eq!(invitation_view(&invitation)["status"], "pending");

        invitation.status = "pending".to_owned();
        assert!(!invitation_acceptance_is_open(&invitation, 20));
    }

    #[test]
    fn invitation_membership_id_is_stable_across_retries() {
        let first = invitation_membership("ins_1", "org_1", "usr_1", "member", "inv_1", 10);
        let retry = invitation_membership("ins_1", "org_1", "usr_1", "member", "inv_1", 10);
        let other_invitation =
            invitation_membership("ins_1", "org_1", "usr_1", "member", "inv_2", 10);
        assert_eq!(first.id, retry.id);
        assert_eq!(
            serde_json::to_value(&first).expect("membership serializes"),
            serde_json::to_value(&retry).expect("membership serializes")
        );
        assert_ne!(first.id, other_invitation.id);
    }

    #[test]
    fn openapi_documents_bearer_contracts_and_public_invitation_acceptance() {
        let document = serde_json::to_value(openapi()).expect("OpenAPI document serializes");
        let organization_create = &document["paths"]["/v1/{slug}/organizations"]["post"];
        assert_eq!(organization_create["security"][0]["bearerAuth"], json!([]));
        assert_eq!(
            organization_create["requestBody"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/CreateOrganizationRequest"
        );
        assert_eq!(
            organization_create["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/Organization"
        );
        assert!(organization_create["responses"].get("409").is_some());
        assert!(
            document["paths"]["/v1/{slug}/organizations/{org_id}"]["patch"]["responses"]
                .get("409")
                .is_some()
        );
        assert!(document["paths"]["/v1/{slug}/organizations/{org_id}/memberships/{membership_id}"]["patch"]["responses"]
            .get("409")
            .is_some());
        assert_eq!(
            document["components"]["securitySchemes"]["bearerAuth"]["scheme"],
            "bearer"
        );

        let accept = &document["paths"]["/ui/{slug}/invitations/{invitation_id}/accept"]["get"];
        assert!(accept.get("security").is_none());
        assert_eq!(accept["parameters"][2]["name"], "token");
        assert_eq!(accept["parameters"][2]["required"], true);
        assert_eq!(
            accept["responses"]["200"]["content"]["text/html"]["schema"]["$ref"],
            "#/components/schemas/HtmlPage"
        );
    }
}
