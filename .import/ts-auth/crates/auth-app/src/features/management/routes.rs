//! Instance-scoped management endpoints.

use std::time::{SystemTime, UNIX_EPOCH};

use auth_contracts::{
    AppError, AppResult,
    models::{ApiKey, AuditEvent, Session, User},
};
use auth_security::tokens::{new_secret, random_token, sha256_hex};
use auth_store::{Predicate, StoreError};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{delete, get, post},
};
use serde::{
    Deserialize,
    de::{self, DeserializeOwned, Visitor},
};
use serde_json::{Map, Value, json};
use utoipa::{
    OpenApi,
    openapi::{
        Components, Info, OpenApi as OpenApiDoc, Ref, RefOr, Required,
        content::Content,
        path::{HttpMethod, Operation, OperationBuilder, ParameterBuilder, ParameterIn, Paths},
        request_body::RequestBodyBuilder,
        response::Response as OpenApiResponse,
        schema::{ArrayBuilder, ComponentsBuilder, ObjectBuilder, Schema, SchemaType, Type},
        security::{HttpAuthScheme, HttpBuilder, SecurityRequirement, SecurityScheme},
    },
};

use crate::{
    AppState,
    features::{management::InstanceContext, users},
};

const BODY_LIMIT: usize = 1024 * 1024;

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/v1/{slug}/users", get(list_users).post(create_user))
        .route(
            "/v1/{slug}/users/{user_id}",
            get(get_user).patch(update_user).delete(delete_user),
        )
        .route(
            "/v1/{slug}/users/{user_id}/sessions",
            get(list_user_sessions),
        )
        .route(
            "/v1/{slug}/users/{user_id}/sessions/revoke-all",
            post(revoke_all_user_sessions),
        )
        .route(
            "/v1/{slug}/users/{user_id}/impersonate",
            post(impersonate_user),
        )
        .route(
            "/v1/{slug}/sessions/{session_id}/revoke",
            post(revoke_session),
        )
        .route(
            "/v1/{slug}/instance",
            get(get_instance).patch(update_instance),
        )
        .route(
            "/v1/{slug}/api-keys",
            get(list_api_keys).post(create_api_key),
        )
        .route("/v1/{slug}/api-keys/{key_id}", delete(revoke_api_key))
}

pub struct ApiDoc;

impl OpenApi for ApiDoc {
    fn openapi() -> OpenApiDoc {
        let mut paths = Paths::new();
        let endpoints = [
            Endpoint::new(
                "/v1/{slug}/users",
                HttpMethod::Get,
                "List users",
                ("200", "Users in this instance", Some("UserList")),
                None,
                vec![
                    ErrorResponse::bad_request(),
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users",
                HttpMethod::Post,
                "Create a user",
                ("201", "Created user", Some("UserDetail")),
                Some("CreateUserRequest"),
                vec![
                    ErrorResponse::bad_request(),
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::conflict(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}",
                HttpMethod::Get,
                "Get a user",
                ("200", "User details", Some("UserDetail")),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}",
                HttpMethod::Patch,
                "Update a user",
                ("200", "Updated user", Some("UserDetail")),
                Some("UpdateUserRequest"),
                vec![
                    ErrorResponse::bad_request(),
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::conflict(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}",
                HttpMethod::Delete,
                "Delete a user",
                ("204", "User deleted", None),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}/sessions",
                HttpMethod::Get,
                "List user sessions",
                ("200", "Sessions for the user", Some("SessionList")),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}/sessions/revoke-all",
                HttpMethod::Post,
                "Revoke all user sessions",
                ("204", "All active sessions revoked", None),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/users/{user_id}/impersonate",
                HttpMethod::Post,
                "Create an impersonation session",
                ("201", "Impersonation token", Some("ImpersonationSession")),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("User not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/sessions/{session_id}/revoke",
                HttpMethod::Post,
                "Revoke a session",
                ("204", "Session revoked", None),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("Session not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/instance",
                HttpMethod::Get,
                "Get instance settings",
                ("200", "Instance settings", Some("Instance")),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/instance",
                HttpMethod::Patch,
                "Update instance settings",
                ("200", "Updated instance settings", Some("UpdatedInstance")),
                Some("UpdateInstanceRequest"),
                vec![
                    ErrorResponse::bad_request(),
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("Instance not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/api-keys",
                HttpMethod::Get,
                "List API keys",
                ("200", "API keys in this instance", Some("ApiKeyList")),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/api-keys",
                HttpMethod::Post,
                "Create an API key",
                (
                    "201",
                    "Created API key. The secret is returned only once.",
                    Some("CreatedApiKey"),
                ),
                Some("CreateApiKeyRequest"),
                vec![
                    ErrorResponse::bad_request(),
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::new(
                        "500",
                        "A storage error may follow a committed key write. Check the key list and revoke any key whose secret was not returned before retrying.",
                    ),
                    ErrorResponse::unavailable(),
                ],
            ),
            Endpoint::new(
                "/v1/{slug}/api-keys/{key_id}",
                HttpMethod::Delete,
                "Revoke an API key",
                ("204", "API key revoked", None),
                None,
                vec![
                    ErrorResponse::unauthorized(),
                    ErrorResponse::forbidden(),
                    ErrorResponse::not_found("API key not found"),
                    ErrorResponse::rate_limited(),
                    ErrorResponse::internal(),
                    ErrorResponse::unavailable(),
                ],
            ),
        ];
        for endpoint in endpoints {
            paths.add_path_operation(
                endpoint.path,
                vec![endpoint.method.clone()],
                endpoint.operation(),
            );
        }
        if let Some(operation) = paths
            .paths
            .get_mut("/v1/{slug}/users")
            .and_then(|item| item.get.as_mut())
        {
            let parameters = operation.parameters.get_or_insert_with(Vec::new);
            parameters.push(query_parameter(
                "limit",
                list_limit_schema(),
                "Maximum number of users to return (1-100; defaults to 20).",
            ));
            parameters.push(query_parameter(
                "starting_after",
                string_schema(),
                "User ID cursor from which to continue pagination.",
            ));
        }

        let components = management_components();
        let mut document =
            OpenApiDoc::new(Info::new("TerraScale Auth Management API", "1.0.0"), paths);
        document.components = Some(components);
        document
    }
}

struct Endpoint {
    path: &'static str,
    method: HttpMethod,
    summary: &'static str,
    success_status: &'static str,
    success_description: &'static str,
    success_schema: Option<&'static str>,
    request_schema: Option<&'static str>,
    errors: Vec<ErrorResponse>,
}

#[derive(Clone, Copy)]
struct ErrorResponse {
    status: &'static str,
    description: &'static str,
}

impl Endpoint {
    fn new(
        path: &'static str,
        method: HttpMethod,
        summary: &'static str,
        success: (&'static str, &'static str, Option<&'static str>),
        request_schema: Option<&'static str>,
        errors: Vec<ErrorResponse>,
    ) -> Self {
        let (success_status, success_description, success_schema) = success;
        Self {
            path,
            method,
            summary,
            success_status,
            success_description,
            success_schema,
            request_schema,
            errors,
        }
    }

    fn operation(self) -> Operation {
        let mut operation = OperationBuilder::new()
            .tag("Management")
            .summary(Some(self.summary))
            .security(SecurityRequirement::new(
                "ManagementBearer",
                Vec::<String>::new(),
            ));
        if let Some(schema) = self.request_schema {
            operation = operation.request_body(Some(
                RequestBodyBuilder::new()
                    .description(Some("JSON request body."))
                    .required(Some(Required::True))
                    .content("application/json", Content::new(Some(schema_ref(schema))))
                    .build(),
            ));
        }
        operation = operation.response(
            self.success_status,
            json_response(self.success_description, self.success_schema),
        );
        for error in self.errors {
            operation = operation.response(
                error.status,
                json_response(error.description, Some("ManagementError")),
            );
        }
        operation.build()
    }
}

impl ErrorResponse {
    const fn new(status: &'static str, description: &'static str) -> Self {
        Self {
            status,
            description,
        }
    }

    const fn bad_request() -> Self {
        Self::new("400", "The request body or parameters are invalid.")
    }
    const fn unauthorized() -> Self {
        Self::new("401", "Missing or invalid bearer credentials.")
    }
    const fn forbidden() -> Self {
        Self::new(
            "403",
            "The API key lacks the required route-family scope or the action is forbidden.",
        )
    }
    const fn not_found(description: &'static str) -> Self {
        Self::new("404", description)
    }
    const fn conflict() -> Self {
        Self::new("409", "A user identifier is already in use.")
    }
    const fn rate_limited() -> Self {
        Self::new("429", "The management API rate limit was exceeded.")
    }
    const fn internal() -> Self {
        Self::new("500", "An internal error occurred.")
    }
    const fn unavailable() -> Self {
        Self::new("503", "The storage engine is unavailable; retry shortly.")
    }
}

fn management_components() -> Components {
    let mut components = ComponentsBuilder::new()
        .schemas_from_iter([
            (
                "CreateUserRequest",
                object_schema(
                    [
                        ("email", string_schema()),
                        ("username", string_schema()),
                        ("password", string_schema()),
                        ("name", string_schema()),
                    ],
                    [],
                ),
            ),
            (
                "UpdateUserRequest",
                object_schema(
                    [
                        ("email", string_schema()),
                        ("username", nullable_string_schema()),
                        ("password", nullable_string_schema()),
                        ("name", nullable_string_schema()),
                        ("banned", boolean_schema()),
                        ("ban_reason", nullable_string_schema()),
                        ("public_metadata", any_schema()),
                        ("private_metadata", any_schema()),
                    ],
                    [],
                ),
            ),
            (
                "CreateApiKeyRequest",
                object_schema(
                    [
                        ("name", string_schema()),
                        ("scopes", requested_scopes_schema()),
                    ],
                    ["name"],
                ),
            ),
            (
                "UpdateInstanceRequest",
                object_schema(
                    [
                        ("name", string_schema()),
                        ("logo_url", string_schema()),
                        ("theme_color", string_schema()),
                    ],
                    [],
                ),
            ),
            ("UserSummary", user_schema(false)),
            ("UserDetail", user_schema(true)),
            ("UserList", data_list_schema(user_schema(false))),
            ("SessionView", session_schema()),
            ("SessionList", data_list_schema(session_schema())),
            ("Instance", instance_schema(true)),
            ("UpdatedInstance", instance_schema(false)),
            ("ApiKeyView", api_key_schema(false)),
            ("ApiKeyList", data_list_schema(api_key_schema(false))),
            ("CreatedApiKey", api_key_schema(true)),
            (
                "ImpersonationSession",
                object_schema(
                    [("token", string_schema()), ("expires_at", integer_schema())],
                    ["token", "expires_at"],
                ),
            ),
            (
                "ManagementError",
                object_schema(
                    [(
                        "error",
                        object_schema(
                            [("code", string_schema()), ("message", string_schema())],
                            ["code", "message"],
                        ),
                    )],
                    ["error"],
                ),
            ),
        ])
        .build();
    components.add_security_scheme(
        "ManagementBearer",
        SecurityScheme::Http(
            HttpBuilder::new()
                .scheme(HttpAuthScheme::Bearer)
                .description(Some("Instance-scoped Management API key. Supply the key secret as `Bearer sk_…`; the key must have the scope matching the route family."))
                .build(),
        ),
    );
    components
}

fn user_schema(include_private: bool) -> Schema {
    let mut properties = vec![
        ("id", string_schema()),
        ("email", nullable_string_schema()),
        ("email_verified", boolean_schema()),
        ("username", nullable_string_schema()),
        ("name", nullable_string_schema()),
        ("banned", boolean_schema()),
        ("created_at", integer_schema()),
        ("last_sign_in_at", nullable_integer_schema()),
        ("public_metadata", any_schema()),
    ];
    if include_private {
        properties.push(("ban_reason", nullable_string_schema()));
        properties.push(("private_metadata", any_schema()));
    }
    object_schema(properties, ["id", "email_verified", "banned", "created_at"])
}

fn session_schema() -> Schema {
    object_schema(
        [
            ("id", string_schema()),
            ("status", string_schema()),
            ("created_at", integer_schema()),
            ("expires_at", integer_schema()),
            ("last_active_at", nullable_integer_schema()),
            ("user_agent", nullable_string_schema()),
            ("ip_address", nullable_string_schema()),
        ],
        ["id", "status", "created_at", "expires_at"],
    )
}

fn instance_schema(include_created_at: bool) -> Schema {
    let mut properties = vec![
        ("id", string_schema()),
        ("slug", string_schema()),
        ("name", string_schema()),
        ("logo_url", nullable_string_schema()),
        ("theme_color", nullable_string_schema()),
    ];
    if include_created_at {
        properties.push(("created_at", integer_schema()));
    }
    let required = if include_created_at {
        vec!["id", "slug", "name", "created_at"]
    } else {
        vec!["id", "slug", "name"]
    };
    object_schema(properties, required)
}

fn api_key_schema(include_secret: bool) -> Schema {
    if include_secret {
        object_schema(
            [
                ("id", string_schema()),
                ("name", string_schema()),
                ("api_key", string_schema()),
                ("scopes", array_schema(string_schema())),
            ],
            ["id", "name", "api_key", "scopes"],
        )
    } else {
        object_schema(
            [
                ("id", string_schema()),
                ("name", string_schema()),
                ("scopes", array_schema(string_schema())),
                ("created_at", integer_schema()),
                ("last_used_at", nullable_integer_schema()),
                ("revoked", boolean_schema()),
            ],
            ["id", "name", "scopes", "created_at", "revoked"],
        )
    }
}

fn data_list_schema(item: Schema) -> Schema {
    object_schema([("data", array_schema(item))], ["data"])
}

fn object_schema(
    properties: impl IntoIterator<Item = (&'static str, Schema)>,
    required: impl IntoIterator<Item = &'static str>,
) -> Schema {
    let mut object = ObjectBuilder::new().schema_type(Type::Object);
    for (name, schema) in properties {
        object = object.property(name, schema);
    }
    for name in required {
        object = object.required(name);
    }
    object.into()
}

fn string_schema() -> Schema {
    ObjectBuilder::new().schema_type(Type::String).into()
}

fn nullable_string_schema() -> Schema {
    ObjectBuilder::new()
        .schema_type(SchemaType::from_iter([Type::String, Type::Null]))
        .into()
}

fn integer_schema() -> Schema {
    ObjectBuilder::new()
        .schema_type(Type::Integer)
        .format(Some(utoipa::openapi::schema::SchemaFormat::KnownFormat(
            utoipa::openapi::schema::KnownFormat::Int64,
        )))
        .into()
}

fn list_limit_schema() -> Schema {
    ObjectBuilder::new()
        .schema_type(Type::Integer)
        .format(Some(utoipa::openapi::schema::SchemaFormat::KnownFormat(
            utoipa::openapi::schema::KnownFormat::Int32,
        )))
        .minimum(Some(1))
        .maximum(Some(100))
        .into()
}

fn nullable_integer_schema() -> Schema {
    ObjectBuilder::new()
        .schema_type(SchemaType::from_iter([Type::Integer, Type::Null]))
        .format(Some(utoipa::openapi::schema::SchemaFormat::KnownFormat(
            utoipa::openapi::schema::KnownFormat::Int64,
        )))
        .into()
}

fn boolean_schema() -> Schema {
    ObjectBuilder::new().schema_type(Type::Boolean).into()
}

fn any_schema() -> Schema {
    ObjectBuilder::new()
        .schema_type(SchemaType::AnyValue)
        .into()
}

fn array_schema(item: Schema) -> Schema {
    ArrayBuilder::new().items(RefOr::T(item)).into()
}

fn requested_scopes_schema() -> Schema {
    ArrayBuilder::new()
        .items(RefOr::T(string_schema()))
        .description(Some(
            "Requested scopes must be a subset of the caller's scopes. If omitted or empty, the caller's scopes are inherited.",
        ))
        .into()
}

fn schema_ref(name: &str) -> Ref {
    Ref::from_schema_name(name)
}

fn json_response(description: &str, schema: Option<&str>) -> OpenApiResponse {
    let mut response = OpenApiResponse::new(description);
    if let Some(schema) = schema {
        response.content.insert(
            "application/json".to_owned(),
            Content::new(Some(schema_ref(schema))),
        );
    }
    response
}

fn query_parameter(
    name: &str,
    schema: Schema,
    description: &str,
) -> utoipa::openapi::path::Parameter {
    ParameterBuilder::new()
        .name(name)
        .parameter_in(ParameterIn::Query)
        .required(Required::False)
        .description(Some(description))
        .schema(Some(schema))
        .build()
}

#[derive(Deserialize)]
struct CreateUserBody {
    email: Option<String>,
    username: Option<String>,
    password: Option<String>,
    name: Option<String>,
}

#[derive(Default)]
enum Patch<T> {
    #[default]
    Missing,
    Null,
    Value(T),
}

impl<'de, T: Deserialize<'de>> Deserialize<'de> for Patch<T> {
    fn deserialize<D: de::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct PatchVisitor<T>(std::marker::PhantomData<T>);
        impl<'de, T: Deserialize<'de>> Visitor<'de> for PatchVisitor<T> {
            type Value = Patch<T>;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("null or a value")
            }
            fn visit_none<E: de::Error>(self) -> Result<Self::Value, E> {
                Ok(Patch::Null)
            }
            fn visit_unit<E: de::Error>(self) -> Result<Self::Value, E> {
                Ok(Patch::Null)
            }
            fn visit_some<D2: de::Deserializer<'de>>(
                self,
                deserializer: D2,
            ) -> Result<Self::Value, D2::Error> {
                T::deserialize(deserializer).map(Patch::Value)
            }
        }
        deserializer.deserialize_option(PatchVisitor(std::marker::PhantomData))
    }
}

#[derive(Deserialize)]
struct UpdateUserBody {
    #[serde(default)]
    email: Patch<Value>,
    #[serde(default)]
    username: Patch<Value>,
    #[serde(default)]
    password: Patch<Value>,
    #[serde(default)]
    name: Patch<Value>,
    #[serde(default)]
    banned: Option<bool>,
    #[serde(default)]
    ban_reason: Patch<Value>,
    #[serde(default)]
    public_metadata: Patch<Value>,
    #[serde(default)]
    private_metadata: Patch<Value>,
}

#[derive(Deserialize)]
struct CreateApiKeyBody {
    name: Option<String>,
    scopes: Option<Vec<String>>,
}

#[derive(Deserialize)]
struct SettingsPatch {
    name: Option<String>,
    #[serde(rename = "logo_url")]
    logo_url: Option<String>,
    #[serde(rename = "theme_color")]
    theme_color: Option<String>,
}

#[derive(Deserialize)]
struct UserListQuery {
    limit: Option<u32>,
    #[serde(rename = "starting_after")]
    starting_after: Option<String>,
}

async fn list_users(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Query(query): Query<UserListQuery>,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    if query.limit.is_some_and(|limit| !(1..=100).contains(&limit)) {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "limit must be between 1 and 100",
        );
    }
    let limit = query.limit.unwrap_or(20) as usize;
    let predicate = match Predicate::equal("instanceId", &context.instance.id) {
        Ok(predicate) => predicate,
        Err(error) => return store_error(error),
    };
    let mut cursor = query.starting_after;
    let mut selected = Vec::with_capacity(limit);
    loop {
        // Filtering deleted users means the scan cursor can be past the last
        // user returned to the caller, so track page progress separately.
        let page_cursor = cursor.as_deref();
        let (rows, next) = match state
            .store
            .query_page::<User>("users", std::slice::from_ref(&predicate), 100, page_cursor)
            .await
        {
            Ok(page) => page,
            Err(error) => return store_error(error),
        };
        if rows.is_empty() {
            break;
        }
        let mut reached_end = false;
        for (user, _) in rows {
            if user.instance_id != context.instance.id || user.deleted {
                continue;
            }
            selected.push(user);
            if selected.len() == limit {
                reached_end = true;
                break;
            }
        }
        if reached_end || next.is_none() || page_cursor == next.as_deref() {
            break;
        }
        cursor = next;
    }
    Json(json!({ "data": selected.iter().map(|user| user_view(user, false)).collect::<Vec<_>>() }))
        .into_response()
}

async fn create_user(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let body: CreateUserBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    if body
        .password
        .as_deref()
        .is_some_and(|password| !password.is_empty() && !(8..=1024).contains(&password.len()))
    {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_password",
            "password must be between 8 and 1024 bytes",
        );
    }
    if let Some(email) = body.email.as_deref() {
        if !valid_email(email) {
            return api_error(
                StatusCode::BAD_REQUEST,
                "invalid_email",
                "email is not a valid address",
            );
        }
        let normalized = email.trim().to_lowercase();
        match state
            .store
            .find_user_by_email(&context.instance.id, &normalized)
            .await
        {
            Ok(Some(_)) => {
                return api_error(
                    StatusCode::CONFLICT,
                    "email_taken",
                    "an account with this email already exists",
                );
            }
            Ok(None) => {}
            Err(error) => return store_error(error),
        }
    }
    match users::create_user(
        &state,
        &context.instance.id,
        body.email.as_deref(),
        body.username.as_deref(),
        body.password.as_deref(),
        body.name.as_deref(),
    )
    .await
    {
        Ok(user) => {
            if let Err(error) =
                audit(&state, &context, "user.created", json!({"user_id":user.id})).await
            {
                return error.into_response();
            }
            (StatusCode::CREATED, Json(user_view(&user, true))).into_response()
        }
        Err(error) if error.status == StatusCode::CONFLICT => api_error(
            StatusCode::CONFLICT,
            "identifier_taken",
            "email or username already exists",
        ),
        Err(error) => error.into_response(),
    }
}

async fn get_user(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match find_live_user(&state, &context.instance.id, &user_id).await {
        Ok(Some(user)) => Json(user_view(&user, true)).into_response(),
        Ok(None) => api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no user with this id in this instance",
        ),
        Err(error) => error.into_response(),
    }
}

async fn update_user(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let body: UpdateUserBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let current = match find_live_user(&state, &context.instance.id, &user_id).await {
        Ok(Some(user)) => user,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no user with this id in this instance",
            );
        }
        Err(error) => return error.into_response(),
    };

    let email = match patch_string(body.email, "email") {
        Ok(Patch::Missing) => None,
        Ok(Patch::Null) => {
            return api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "email cannot be removed; supply a new address instead",
            );
        }
        Ok(Patch::Value(value)) => {
            if !valid_email(&value) {
                return api_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_email",
                    "email is not a valid address",
                );
            }
            Some(Some(value))
        }
        Err(response) => return response,
    };
    let mut email = email;
    if let Some(Some(value)) = email.as_ref() {
        let normalized = users::normalize_email(value);
        if current.email.as_deref() == Some(normalized.as_str()) {
            email = None;
        }
    }
    let email_was_patched = email.is_some();
    let username = match patch_string(body.username, "username") {
        Ok(patch) => patch_to_option(patch),
        Err(response) => return response,
    };
    let name = match patch_string(body.name, "name") {
        Ok(patch) => patch_to_option(patch),
        Err(response) => return response,
    };
    let password = match patch_string(body.password, "password") {
        Ok(Patch::Missing) => None,
        Ok(Patch::Null) => Some(None),
        Ok(Patch::Value(value)) if !(8..=1024).contains(&value.len()) => {
            return api_error(
                StatusCode::BAD_REQUEST,
                "invalid_password",
                "password must be between 8 and 1024 bytes",
            );
        }
        Ok(Patch::Value(value)) => Some(Some(value)),
        Err(response) => return response,
    };
    let ban_reason = if body.banned == Some(false) {
        Some(None)
    } else {
        match patch_string(body.ban_reason, "ban_reason") {
            Ok(patch) => patch_to_option(patch),
            Err(response) => return response,
        }
    };
    let public_metadata = match patch_metadata(body.public_metadata) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let private_metadata = match patch_metadata(body.private_metadata) {
        Ok(value) => value,
        Err(response) => return response,
    };

    let changes = users::UserChanges {
        email,
        username,
        name,
        password,
        banned: body.banned,
        ban_reason,
        public_metadata,
        private_metadata,
    };
    match users::update_user(&state, &context.instance.id, &user_id, changes).await {
        Ok(updated) => {
            if let Err(error) = audit(
                &state,
                &context,
                "user.updated",
                json!({"user_id":updated.id}),
            )
            .await
            {
                return error.into_response();
            }
            Json(user_view(&updated, true)).into_response()
        }
        Err(error) if error.status == StatusCode::CONFLICT => {
            if email_was_patched {
                api_error(
                    StatusCode::CONFLICT,
                    "email_taken",
                    "an account with this email already exists",
                )
            } else {
                api_error(
                    StatusCode::CONFLICT,
                    "username_taken",
                    "an account with this username already exists",
                )
            }
        }
        Err(error) => error.into_response(),
    }
}

async fn delete_user(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let existing = match state.store.get_user(&user_id).await {
        Ok(Some(user)) if user.instance_id == context.instance.id => user,
        Ok(_) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no user with this id in this instance",
            );
        }
        Err(error) => return store_error(error),
    };
    let was_live = !existing.deleted;
    match users::deprovision_user(&state, &context.instance.id, &user_id).await {
        Ok(true) if was_live => {
            match audit(&state, &context, "user.deleted", json!({"user_id":user_id})).await {
                Ok(()) => StatusCode::NO_CONTENT.into_response(),
                Err(error) => error.into_response(),
            }
        }
        Ok(true) => api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no user with this id in this instance",
        ),
        Ok(false) => api_error(
            StatusCode::NOT_FOUND,
            "user_not_found",
            "no user with this id in this instance",
        ),
        Err(error) => error.into_response(),
    }
}

async fn list_user_sessions(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match find_live_user(&state, &context.instance.id, &user_id).await {
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no user with this id in this instance",
            );
        }
        Err(error) => return error.into_response(),
        Ok(Some(_)) => {}
    }
    let predicates = match (
        Predicate::equal("instanceId", &context.instance.id),
        Predicate::equal("userId", &user_id),
    ) {
        (Ok(instance), Ok(user)) => vec![instance, user],
        (Err(error), _) | (_, Err(error)) => return store_error(error),
    };
    match state
        .store
        .query_all::<Session>("sessions", &predicates, 100)
        .await
    {
        Ok(sessions) => {
            let views = sessions.into_iter().filter(|session| session.instance_id == context.instance.id)
                .map(|session| json!({"id":session.id,"status":session.status,"created_at":session.created_at,"expires_at":session.expires_at,"last_active_at":session.last_active_at,"user_agent":session.user_agent,"ip_address":session.ip_address}))
                .collect::<Vec<_>>();
            Json(json!({"data":views})).into_response()
        }
        Err(error) => store_error(error),
    }
}

async fn revoke_all_user_sessions(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match find_live_user(&state, &context.instance.id, &user_id).await {
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no user with this id in this instance",
            );
        }
        Err(error) => return error.into_response(),
        Ok(Some(_)) => {}
    }
    match users::revoke_sessions(&state, &context.instance.id, &user_id, None).await {
        Ok(()) => StatusCode::NO_CONTENT.into_response(),
        Err(error) => error.into_response(),
    }
}

async fn revoke_session(
    State(state): State<AppState>,
    Path((slug, session_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match revoke_one_session(&state, &context.instance.id, &session_id).await {
        Ok(true) => StatusCode::NO_CONTENT.into_response(),
        Ok(false) => api_error(
            StatusCode::NOT_FOUND,
            "session_not_found",
            "no session with this id in this instance",
        ),
        Err(error) => error.into_response(),
    }
}

async fn impersonate_user(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let user = match find_live_user(&state, &context.instance.id, &user_id).await {
        Ok(Some(user)) => user,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "user_not_found",
                "no user with this id in this instance",
            );
        }
        Err(error) => return error.into_response(),
    };
    if user.banned {
        return api_error(StatusCode::FORBIDDEN, "forbidden", "this user is banned");
    }
    let user_agent = request
        .headers()
        .get(header::USER_AGENT)
        .and_then(|value| value.to_str().ok());
    let ip_address = request
        .extensions()
        .get::<axum::extract::ConnectInfo<std::net::SocketAddr>>()
        .map(|info| info.0.ip().to_string());
    let (session, token) = match users::create_session(
        &state,
        &context.instance.id,
        &user.id,
        user_agent,
        ip_address.as_deref(),
    )
    .await
    {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = audit(
        &state,
        &context,
        "user.impersonated",
        json!({"user_id":user.id,"api_key_id":context.api_key_id}),
    )
    .await
    {
        return error.into_response();
    }
    (
        StatusCode::CREATED,
        Json(json!({"token":token,"expires_at":session.expires_at})),
    )
        .into_response()
}

async fn get_instance(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let instance = &context.instance;
    Json(json!({"id":instance.id,"slug":instance.slug,"name":instance.name,"logo_url":instance.logo_url,"theme_color":instance.theme_color,"created_at":instance.created_at})).into_response()
}

async fn update_instance(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let patch: SettingsPatch = match read_json(request).await {
        Ok(patch) => patch,
        Err(response) => return response,
    };
    if let Some(logo) = patch.logo_url.as_deref()
        && reqwest::Url::parse(logo)
            .ok()
            .filter(|url| matches!(url.scheme(), "http" | "https") && url.host_str().is_some())
            .is_none()
    {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_logo_url",
            "logo_url must be an absolute http(s) URL",
        );
    }
    if let Some(color) = patch.theme_color.as_deref()
        && (color.len() != 7
            || !color.starts_with('#')
            || !color[1..].bytes().all(|byte| byte.is_ascii_hexdigit()))
    {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_theme_color",
            "theme_color must look like #6c5ce7",
        );
    }
    let name = patch
        .name
        .filter(|name| !name.trim().is_empty())
        .map(|name| name.trim().to_owned());
    let logo_url = patch.logo_url;
    let theme_color = patch.theme_color;
    match state
        .store
        .try_update_instance(&context.instance.id, |instance| {
            if let Some(name) = name.as_ref() {
                instance.name.clone_from(name);
            }
            if let Some(logo_url) = logo_url.as_ref() {
                instance.logo_url = Some(logo_url.clone());
            }
            if let Some(theme_color) = theme_color.as_ref() {
                instance.theme_color = Some(theme_color.clone());
            }
            true
        })
        .await
    {
        Ok(true) => {}
        Ok(false) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "instance_not_found",
                "no instance with this slug exists",
            );
        }
        Err(error) => return store_error(error),
    }
    let updated = match state.store.get_instance(&context.instance.id).await {
        Ok(Some(instance)) => instance,
        Ok(None) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "instance_not_found",
                "no instance with this slug exists",
            );
        }
        Err(error) => return store_error(error),
    };
    if let Err(error) = audit(&state, &context, "instance.updated", json!({})).await {
        return error.into_response();
    }
    Json(json!({"id":updated.id,"slug":updated.slug,"name":updated.name,"logo_url":updated.logo_url,"theme_color":updated.theme_color})).into_response()
}

async fn list_api_keys(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    match state.store.list_api_keys(&context.instance.id).await {
        Ok(keys) => Json(json!({"data":keys.into_iter().filter(|key| key.instance_id == context.instance.id).map(api_key_view).collect::<Vec<_>>()})).into_response(),
        Err(error) => store_error(error),
    }
}

async fn create_api_key(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let body: CreateApiKeyBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(name) = body.name.filter(|name| !name.trim().is_empty()) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name is required",
        );
    };
    let Some(scopes) = child_api_key_scopes(body.scopes, &context.scopes) else {
        return api_error(
            StatusCode::FORBIDDEN,
            "insufficient_scope",
            "an API key cannot grant scopes its caller does not have",
        );
    };
    let (id, _, combined) = new_secret("sk");
    let key = ApiKey {
        id,
        instance_id: context.instance.id.clone(),
        name: name.trim().to_owned(),
        secret_key: sha256_hex(&combined),
        scopes,
        created_at: now(),
        last_used_at: None,
        revoked: false,
    };
    if let Err(error) = state.store.create_api_key(&key).await {
        match revoke_unreturned_api_key(&state, &key).await {
            Ok(()) => tracing::warn!(
                api_key_id = %key.id,
                "API-key write failed; confirmed there is no active unreturned key"
            ),
            Err(revoke_error) => tracing::error!(
                ?revoke_error,
                api_key_id = %key.id,
                "API-key write failed and revocation could not be confirmed"
            ),
        }
        return store_error(error);
    }
    if let Err(error) = audit(
        &state,
        &context,
        "api_key.created",
        json!({"api_key_id":key.id}),
    )
    .await
    {
        match revoke_unreturned_api_key(&state, &key).await {
            Ok(()) => tracing::warn!(
                api_key_id = %key.id,
                "API-key audit failed; revoked the key before returning an error"
            ),
            Err(revoke_error) => tracing::error!(
                ?revoke_error,
                api_key_id = %key.id,
                "API-key audit failed and revocation could not be confirmed"
            ),
        }
        return error.into_response();
    }
    (
        StatusCode::CREATED,
        Json(json!({"id":key.id,"name":key.name,"api_key":combined,"scopes":key.scopes})),
    )
        .into_response()
}

async fn revoke_unreturned_api_key(state: &AppState, expected: &ApiKey) -> Result<(), StoreError> {
    let mut last_write_error = None;
    for _ in 0..4 {
        let Some((mut current, version)) = state
            .store
            .get_versioned::<ApiKey>("api_keys", &expected.id)
            .await?
        else {
            // A missing key is already safe, including if the original write was
            // rejected before committing.
            return Ok(());
        };
        if current.revoked {
            return Ok(());
        }
        if current.id != expected.id
            || current.instance_id != expected.instance_id
            || current.name != expected.name
            || current.secret_key != expected.secret_key
            || current.scopes != expected.scopes
            || current.created_at != expected.created_at
        {
            return Err(StoreError::Precondition);
        }

        current.revoked = true;
        match state
            .store
            .compare_and_put("api_keys", &current.id, &current, &version)
            .await
        {
            Ok(true) => return Ok(()),
            Ok(false) => {}
            // The write may have committed before an error was returned. Read
            // the record again on the next iteration to confirm revocation.
            Err(error) => last_write_error = Some(error),
        }
    }
    Err(last_write_error.unwrap_or(StoreError::Contention))
}

async fn revoke_api_key(
    State(state): State<AppState>,
    Path((slug, key_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve(&state, &headers, &slug).await {
        Ok(context) => context,
        Err(response) => return response,
    };
    let mut key = match state.store.get_api_key(&key_id).await {
        Ok(Some(key)) if key.instance_id == context.instance.id => key,
        Ok(_) => {
            return api_error(
                StatusCode::NOT_FOUND,
                "api_key_not_found",
                "no api key with this id in this instance",
            );
        }
        Err(error) => return store_error(error),
    };
    key.revoked = true;
    if let Err(error) = state.store.upsert_api_key(&key).await {
        return store_error(error);
    }
    if let Err(error) = audit(
        &state,
        &context,
        "api_key.revoked",
        json!({"api_key_id":key.id}),
    )
    .await
    {
        return error.into_response();
    }
    StatusCode::NO_CONTENT.into_response()
}

fn api_key_view(key: ApiKey) -> Value {
    json!({"id":key.id,"name":key.name,"scopes":key.scopes,"created_at":key.created_at,"last_used_at":key.last_used_at,"revoked":key.revoked})
}

fn child_api_key_scopes(
    requested: Option<Vec<String>>,
    caller_scopes: &[String],
) -> Option<Vec<String>> {
    let scopes = requested
        .filter(|scopes| !scopes.is_empty())
        .unwrap_or_else(|| caller_scopes.to_vec());
    if caller_scopes.iter().any(|scope| scope == "*")
        || scopes.iter().all(|scope| caller_scopes.contains(scope))
    {
        Some(scopes)
    } else {
        None
    }
}

fn user_view(user: &User, include_private: bool) -> Value {
    let mut view = json!({
        "id": user.id,
        "email": user.email,
        "email_verified": user.email_verified,
        "username": user.username,
        "name": user.name,
        "banned": user.banned,
        "created_at": user.created_at,
        "last_sign_in_at": user.last_sign_in_at,
        "public_metadata": user.public_metadata,
    });
    if include_private && let Some(object) = view.as_object_mut() {
        object.insert("ban_reason".into(), json!(user.ban_reason));
        object.insert("private_metadata".into(), json!(user.private_metadata));
    }
    view
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
    crate::features::management::require(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)
}

async fn find_live_user(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
) -> AppResult<Option<User>> {
    let user = state.store.get_user(user_id).await.map_err(internal)?;
    Ok(user.filter(|user| user.instance_id == instance_id && !user.deleted))
}

async fn revoke_one_session(
    state: &AppState,
    instance_id: &str,
    session_id: &str,
) -> AppResult<bool> {
    for _ in 0..8 {
        let Some((mut session, version)) = state
            .store
            .get_versioned::<Session>("sessions", session_id)
            .await
            .map_err(internal)?
        else {
            return Ok(false);
        };
        if session.instance_id != instance_id {
            return Ok(false);
        }
        if session.status != "active" {
            return Ok(true);
        }
        session.status = "revoked".to_owned();
        if state
            .store
            .compare_and_put("sessions", session_id, &session, &version)
            .await
            .map_err(internal)?
        {
            return Ok(true);
        }
    }
    Err(AppError::unavailable())
}

async fn audit(
    state: &AppState,
    context: &InstanceContext,
    event_type: &str,
    data: Value,
) -> AppResult<()> {
    let data = data.as_object().cloned().unwrap_or_default();
    let event = AuditEvent {
        id: format!("evt_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        r#type: event_type.to_owned(),
        actor_id: Some(context.api_key_id.clone()),
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

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn patch_string(patch: Patch<Value>, field: &str) -> Result<Patch<String>, Response> {
    match patch {
        Patch::Missing => Ok(Patch::Missing),
        Patch::Null => Ok(Patch::Null),
        Patch::Value(Value::String(value)) => Ok(Patch::Value(value)),
        Patch::Value(_) => Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            &format!("{field} must be a string or null"),
        )),
    }
}

fn patch_to_option(patch: Patch<String>) -> Option<Option<String>> {
    match patch {
        Patch::Missing => None,
        Patch::Null => Some(None),
        Patch::Value(value) => Some(Some(value)),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn patch_metadata(patch: Patch<Value>) -> Result<Option<Map<String, Value>>, Response> {
    match patch {
        Patch::Missing => Ok(None),
        Patch::Null => Ok(Some(Map::new())),
        Patch::Value(Value::Object(value)) => Ok(Some(value)),
        Patch::Value(_) => Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "metadata must be an object or null",
        )),
    }
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

fn valid_email(email: &str) -> bool {
    let email = email.trim();
    if email.len() < 3 || email.len() > 254 || email.chars().any(char::is_whitespace) {
        return false;
    }
    let mut parts = email.split('@');
    matches!(parts.next(), Some(local) if !local.is_empty())
        && matches!(parts.next(), Some(domain) if domain.contains('.'))
        && parts.next().is_none()
}

fn store_error(error: StoreError) -> Response {
    tracing::error!(?error, "management persistence failed");
    AppError::internal().into_response()
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "management persistence failed");
    AppError::internal()
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
mod openapi_tests {
    use super::*;

    #[test]
    fn management_openapi_documents_bodies_auth_and_actual_statuses() {
        let document = serde_json::to_value(ApiDoc::openapi()).expect("OpenAPI serializes");

        assert_eq!(
            document["components"]["securitySchemes"]["ManagementBearer"]["scheme"],
            "bearer"
        );
        assert!(document["security"].is_null());

        let create_user = &document["paths"]["/v1/{slug}/users"]["post"];
        assert_eq!(create_user["security"][0]["ManagementBearer"], json!([]));
        assert_eq!(
            create_user["responses"]["201"]["description"],
            "Created user"
        );
        assert_eq!(
            create_user["requestBody"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/CreateUserRequest"
        );
        assert!(
            create_user["responses"]["409"]["content"]["application/json"]["schema"].is_object()
        );

        assert!(
            document["paths"]["/v1/{slug}/api-keys"]["post"]["responses"]["500"]["description"]
                .as_str()
                .is_some_and(|description| description
                    .contains("revoke any key whose secret was not returned"))
        );

        let delete_user = &document["paths"]["/v1/{slug}/users/{user_id}"]["delete"];
        assert_eq!(
            delete_user["responses"]["204"]["description"],
            "User deleted"
        );
        assert!(delete_user["responses"]["204"]["content"].is_null());
        assert_eq!(
            document["paths"]["/v1/{slug}/users/{user_id}/impersonate"]["post"]["responses"]["201"]
                ["description"],
            "Impersonation token"
        );

        let limit = document["paths"]["/v1/{slug}/users"]["get"]["parameters"]
            .as_array()
            .expect("list users has query parameters")
            .iter()
            .find(|parameter| parameter["name"] == "limit")
            .expect("list users documents limit");
        assert_eq!(limit["in"], "query");
        assert_eq!(limit["schema"]["minimum"], 1);
        assert_eq!(limit["schema"]["maximum"], 100);
    }

    #[test]
    fn child_api_keys_cannot_gain_scopes_the_caller_does_not_have() {
        let restricted = vec!["api-keys".to_owned(), "users".to_owned()];
        assert_eq!(
            child_api_key_scopes(None, &restricted),
            Some(restricted.clone())
        );
        assert_eq!(
            child_api_key_scopes(Some(vec![]), &restricted),
            Some(restricted.clone())
        );
        assert_eq!(
            child_api_key_scopes(Some(vec!["users".to_owned()]), &restricted),
            Some(vec!["users".to_owned()])
        );
        assert!(child_api_key_scopes(Some(vec!["*".to_owned()]), &restricted).is_none());
        assert!(child_api_key_scopes(Some(vec!["billing".to_owned()]), &restricted).is_none());

        let operator = vec!["*".to_owned()];
        assert_eq!(
            child_api_key_scopes(Some(vec!["billing".to_owned()]), &operator),
            Some(vec!["billing".to_owned()])
        );
    }
}
