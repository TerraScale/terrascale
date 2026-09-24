//! Operator-only instance bootstrap.

use auth_contracts::{
    AppError,
    models::{ApiKey, Instance},
};
use auth_security::tokens::{fixed_time_equals, hmac_sha256, random_token, sha256_hex};
use auth_store::StoreError;
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Request, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Response},
    routing::post,
};
use serde::{Deserialize, Serialize};
use serde_json::json;
use utoipa::openapi::{
    Content, Info, OpenApi as OpenApiDoc, Ref, Required,
    path::{HttpMethod, Operation, ParameterBuilder, ParameterIn, Paths},
    request_body::RequestBodyBuilder,
    response::Response as OpenApiResponse,
    schema::{ComponentsBuilder, ObjectBuilder, Schema, Type},
    security::{HttpAuthScheme, HttpBuilder, SecurityRequirement, SecurityScheme},
};

use crate::{AppState, app::body_limit_exceeded};

const BODY_LIMIT: usize = 1024 * 1024;
const IDEMPOTENCY_KEY_HEADER: &str = "idempotency-key";
const BOOTSTRAP_REQUESTS: &str = "operator_bootstrap_requests";

pub fn router() -> Router<AppState> {
    Router::new().route("/v1/instances", post(create_instance))
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    paths.add_path_operation(
        "/v1/instances",
        vec![HttpMethod::Post],
        Operation::builder()
            .tag("Instances")
            .summary(Some("Create an instance and its initial API key"))
            .security(SecurityRequirement::new(
                "OperatorBearer",
                Vec::<String>::new(),
            ))
            .description(Some("Requires the configured operator bearer key and a stable Idempotency-Key. Replaying the same key and request returns the same instance and API key."))
            .parameter(
                ParameterBuilder::new()
                    .name(IDEMPOTENCY_KEY_HEADER)
                    .parameter_in(ParameterIn::Header)
                    .required(Required::True)
                    .description(Some("Stable client-generated request key, 16-128 visible ASCII characters."))
                    .schema(Some(string_schema()))
                    .build(),
            )
            .request_body(Some(
                RequestBodyBuilder::new()
                    .required(Some(Required::True))
                    .content("application/json", Content::new(Some(schema_ref("CreateInstanceRequest"))))
                    .build(),
            ))
            .response("200", json_response("Created or replayed instance and initial API key.", Some("CreateInstanceResponse")))
            .response("400", json_response("Invalid request body or idempotency key.", Some("ManagementError")))
            .response("413", json_response("The request body exceeds the 1 MiB limit.", Some("ManagementError")))
            .response("401", json_response("Missing or invalid operator credentials.", Some("ManagementError")))
            .response("409", json_response("The slug already exists or the key was reused for different input.", Some("ManagementError")))
            .response("500", json_response("Bootstrap storage failed.", Some("ManagementError")))
            .response("503", json_response("The bootstrap outcome is not yet visible; retry with the same key.", Some("ManagementError")))
            .build(),
    );
    let mut components = ComponentsBuilder::new()
        .schemas_from_iter([
            (
                "CreateInstanceRequest",
                object_schema(
                    [("slug", string_schema()), ("name", string_schema())],
                    ["slug", "name"],
                ),
            ),
            (
                "CreateInstanceResponse",
                object_schema(
                    [
                        (
                            "instance",
                            object_schema(
                                [
                                    ("id", string_schema()),
                                    ("slug", string_schema()),
                                    ("name", string_schema()),
                                ],
                                ["id", "slug", "name"],
                            ),
                        ),
                        ("api_key", string_schema()),
                    ],
                    ["instance", "api_key"],
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
        "OperatorBearer",
        SecurityScheme::Http(
            HttpBuilder::new()
                .scheme(HttpAuthScheme::Bearer)
                .description(Some("Configured operator bearer credential."))
                .build(),
        ),
    );
    let mut document = OpenApiDoc::new(Info::new("TerraScale Auth Instances API", "1.0.0"), paths);
    document.components = Some(components);
    document
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

#[derive(Deserialize)]
struct CreateInstance {
    slug: Option<String>,
    name: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BootstrapRequest {
    payload_hash: String,
    instance_id: String,
    api_key_id: String,
    created_at: i64,
}

async fn create_instance(
    State(state): State<AppState>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    if !operator_authorized(&headers, &state.options.operator_key) {
        return api_error(
            StatusCode::UNAUTHORIZED,
            "unauthorized",
            "Missing or invalid credentials.",
        );
    }
    let Some(idempotency_key) = headers
        .get(IDEMPOTENCY_KEY_HEADER)
        .and_then(|value| value.to_str().ok())
        .map(str::trim)
        .filter(|value| {
            (16..=128).contains(&value.len()) && value.bytes().all(|b| b.is_ascii_graphic())
        })
    else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "idempotency_key_required",
            "Supply an Idempotency-Key header between 16 and 128 visible ASCII characters.",
        );
    };

    let body: CreateInstance = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let (Some(slug), Some(name)) = (body.slug, body.name) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "slug and name are required",
        );
    };
    let slug = slug.trim().to_ascii_lowercase();
    let name = name.trim();
    if name.is_empty()
        || !(3..=48).contains(&slug.len())
        || !slug.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
    {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "slug must be 3-48 url-safe characters and name is required",
        );
    }

    let payload_hash = sha256_hex(&format!("{slug}\0{name}"));
    let request_id = sha256_hex(&format!(
        "operator-instance-bootstrap:v1\0{idempotency_key}"
    ));
    let candidate = BootstrapRequest {
        payload_hash: payload_hash.clone(),
        instance_id: format!("ins_{}", random_token(20)),
        api_key_id: format!("sk_{}", random_token(22)),
        created_at: now(),
    };
    let operation = match state
        .store
        .create(BOOTSTRAP_REQUESTS, &request_id, &candidate)
        .await
    {
        Ok(()) => candidate,
        Err(error) if error.is_conflict() => match state
            .store
            .get::<BootstrapRequest>(BOOTSTRAP_REQUESTS, &request_id)
            .await
        {
            Ok(Some(operation)) => operation,
            Ok(None) => return AppError::unavailable().into_response(),
            Err(error) => return store_error(error),
        },
        Err(error) => {
            tracing::error!(
                ?error,
                request_id,
                "failed to persist operator bootstrap request"
            );
            return AppError::unavailable().into_response();
        }
    };
    if operation.payload_hash != payload_hash {
        return api_error(
            StatusCode::CONFLICT,
            "idempotency_key_reused",
            "This Idempotency-Key was already used with a different request.",
        );
    }

    let instance = Instance {
        id: operation.instance_id.clone(),
        slug: slug.clone(),
        name: name.to_owned(),
        logo_url: None,
        theme_color: None,
        stripe_customer_id: None,
        created_at: operation.created_at,
    };
    let existing_instance = match state.store.find_instance_by_slug(&slug).await {
        Ok(Some(existing)) => Some(existing),
        Ok(None) => None,
        Err(error) => return store_error(error),
    };
    if let Some(existing) = existing_instance {
        if existing.id != instance.id || existing.name != instance.name {
            return api_error(
                StatusCode::CONFLICT,
                "instance_exists",
                "an instance with this slug already exists",
            );
        }
    } else if let Err(error) = state.store.create_instance(&instance).await {
        if error.is_conflict() {
            let existing = match state.store.find_instance_by_slug(&slug).await {
                Ok(Some(existing)) => existing,
                Ok(None) => return AppError::unavailable().into_response(),
                Err(read_error) => return store_error(read_error),
            };
            if existing.id != instance.id || existing.name != instance.name {
                return api_error(
                    StatusCode::CONFLICT,
                    "instance_exists",
                    "an instance with this slug already exists",
                );
            }
        } else {
            tracing::error!(?error, instance_id = %instance.id, "failed to create bootstrap instance");
            return AppError::unavailable().into_response();
        }
    }

    let combined = bootstrap_api_key_secret(&state.options.master_key, &request_id, &operation);
    let key = ApiKey {
        id: operation.api_key_id.clone(),
        instance_id: instance.id.clone(),
        name: "default".to_owned(),
        secret_key_hash: sha256_hex(&combined),
        scopes: vec!["*".to_owned()],
        created_at: operation.created_at,
        last_used_at: None,
        revoked: false,
    };
    if let Err(error) = state.store.create_api_key(&key).await {
        if !error.is_conflict() {
            tracing::error!(?error, instance_id = %instance.id, "failed to create initial API key");
            return AppError::unavailable().into_response();
        }
        let existing = match state.store.get_api_key(&key.id).await {
            Ok(Some(existing)) => existing,
            Ok(None) => return AppError::unavailable().into_response(),
            Err(read_error) => return store_error(read_error),
        };
        if existing.instance_id != key.instance_id
            || existing.name != key.name
            || existing.secret_key_hash != key.secret_key_hash
            || existing.scopes != key.scopes
            || existing.revoked
        {
            return api_error(
                StatusCode::CONFLICT,
                "bootstrap_key_conflict",
                "The idempotent bootstrap API key no longer matches this request.",
            );
        }
    }

    (
        StatusCode::OK,
        Json(json!({
            "instance": { "id": instance.id, "slug": instance.slug, "name": instance.name },
            "api_key": combined,
        })),
    )
        .into_response()
}

fn bootstrap_api_key_secret(
    master_key: &[u8],
    request_id: &str,
    operation: &BootstrapRequest,
) -> String {
    let message = format!(
        "operator-instance-bootstrap-key:v1\0{request_id}\0{}\0{}",
        operation.instance_id, operation.api_key_id
    );
    let material = hmac_sha256(master_key, message.as_bytes());
    let secret = material
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<String>();
    format!("{}_{}", operation.api_key_id, secret)
}

fn operator_authorized(headers: &HeaderMap, expected: &str) -> bool {
    let Some(value) = headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
    else {
        return false;
    };
    let Some(scheme) = value.get(..7) else {
        return false;
    };
    if !scheme.eq_ignore_ascii_case("Bearer ") {
        return false;
    }
    let presented = &value[7..];
    fixed_time_equals(presented, expected)
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn read_json<T: for<'de> Deserialize<'de>>(request: Request) -> Result<T, Response> {
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|error| {
            if body_limit_exceeded(&error) {
                api_error(
                    StatusCode::PAYLOAD_TOO_LARGE,
                    "payload_too_large",
                    "the request body exceeds the 1 MiB limit",
                )
            } else {
                api_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_request",
                    "a JSON body is required",
                )
            }
        })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        api_error(
            StatusCode::BAD_REQUEST,
            "invalid_json",
            "the request body is not valid JSON",
        )
    })
}

fn store_error(error: StoreError) -> Response {
    if error.is_conflict() {
        return api_error(
            StatusCode::CONFLICT,
            "instance_exists",
            "an instance with this slug already exists",
        );
    }
    tracing::error!(?error, "instance persistence failed");
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
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bootstrap_secret_is_stable_for_a_durable_request_and_instance() {
        let operation = BootstrapRequest {
            payload_hash: "payload-hash".to_owned(),
            instance_id: "ins_fixed".to_owned(),
            api_key_id: "sk_fixed".to_owned(),
            created_at: 1,
        };
        let first =
            bootstrap_api_key_secret(b"high-entropy-server-master-key", "request-id", &operation);
        let retry =
            bootstrap_api_key_secret(b"high-entropy-server-master-key", "request-id", &operation);
        let other_request = bootstrap_api_key_secret(
            b"high-entropy-server-master-key",
            "other-request",
            &operation,
        );

        assert_eq!(first, retry);
        assert_ne!(first, other_request);
        assert!(first.starts_with("sk_fixed_"));
        assert!(!first.contains("high-entropy-server-master-key"));
    }

    #[test]
    fn operator_bootstrap_openapi_requires_idempotency_and_bearer_auth() {
        let document = serde_json::to_value(openapi()).expect("OpenAPI serializes");
        let operation = &document["paths"]["/v1/instances"]["post"];

        assert_eq!(
            document["components"]["securitySchemes"]["OperatorBearer"]["scheme"],
            "bearer"
        );
        assert!(document["security"].is_null());
        assert_eq!(
            operation["security"][0]["OperatorBearer"],
            serde_json::json!([])
        );
        assert_eq!(operation["parameters"][0]["name"], IDEMPOTENCY_KEY_HEADER);
        assert_eq!(operation["parameters"][0]["required"], true);
        assert_eq!(
            operation["responses"]["200"]["description"],
            "Created or replayed instance and initial API key."
        );
        assert!(
            operation["requestBody"]["content"]["application/json"]["schema"]["$ref"].is_string()
        );
    }
}
