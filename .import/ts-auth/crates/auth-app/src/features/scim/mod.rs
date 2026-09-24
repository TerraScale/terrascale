//! SCIM 2.0 directory management and bearer-authenticated user/group provisioning.
//!
//! SCIM resources are isolated by directory and instance. Tokens are only returned at
//! creation/rotation and their hashes are stored in the auth document store.

use auth_contracts::{
    AppError,
    models::{
        AuditEvent, OrgMembership, Organization, OrganizationStatus, ScimDirectory,
        ScimDirectoryStatus, ScimUserLink, User,
    },
};
use auth_security::tokens::{random_token, sha256_hex};
use auth_store::{Predicate, StoreError, unique_claim_id};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Request, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{delete, get, post},
};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use utoipa::openapi::{
    Components, Content, Info, OpenApi as OpenApiDoc, Ref, RefOr, Required, Schema,
    path::{HttpMethod, Operation, Parameter, ParameterIn, Paths},
    request_body::RequestBodyBuilder,
    response::Response as OpenApiResponse,
    schema::{Array, ObjectBuilder, SchemaType, Type},
    security::{HttpAuthScheme, HttpBuilder, SecurityRequirement, SecurityScheme},
};

use crate::{
    AppState,
    features::{management, users},
};

const BODY_LIMIT: usize = 1024 * 1024;
const MAX_RESULTS: usize = 100;
const ERROR_SCHEMA: &str = "urn:ietf:params:scim:api:messages:2.0:Error";
const LIST_SCHEMA: &str = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
const USER_SCHEMA: &str = "urn:ietf:params:scim:schemas:core:2.0:User";
const GROUP_SCHEMA: &str = "urn:ietf:params:scim:schemas:core:2.0:Group";
const GROUPS: &str = "scim_groups";
const USER_LINKS: &str = "scim_user_links";
const MEMBERSHIP_CLAIMS: &str = "scim_membership_claims";
const SCIM_USER_NAME_CLAIM: &str = "scim-user-name";

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScimMembershipClaim {
    id: String,
    instance_id: String,
    org_id: String,
    user_id: String,
    membership_id: String,
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/v1/{slug}/directories",
            post(create_directory).get(list_directories),
        )
        .route(
            "/v1/{slug}/directories/{directory_id}",
            delete(delete_directory),
        )
        .route(
            "/v1/{slug}/directories/{directory_id}/rotate",
            post(rotate_directory_token),
        )
        .route(
            "/scim/{directory_id}/Users",
            get(list_users).post(create_user),
        )
        .route(
            "/scim/{directory_id}/Users/{user_id}",
            get(get_user)
                .put(replace_user)
                .patch(patch_user)
                .delete(delete_user),
        )
        .route(
            "/scim/{directory_id}/Groups",
            get(list_groups).post(create_group),
        )
        .route(
            "/scim/{directory_id}/Groups/{group_id}",
            get(get_group)
                .put(replace_group)
                .patch(patch_group)
                .delete(delete_group),
        )
        .route(
            "/scim/{directory_id}/ServiceProviderConfig",
            get(service_provider_config),
        )
        .route("/scim/{directory_id}/ResourceTypes", get(resource_types))
        .route("/scim/{directory_id}/Schemas", get(schemas))
}

#[derive(Clone, Copy)]
enum DocParameterType {
    String,
    Integer,
}

struct DocQueryParameter {
    name: &'static str,
    description: &'static str,
    required: bool,
    parameter_type: DocParameterType,
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

    fn scim(mut self) -> Self {
        self.media_type = "application/scim+json";
        self.error_schema = "ScimError";
        self
    }

    fn query_parameters(mut self, parameters: &'static [DocQueryParameter]) -> Self {
        self.query_parameters = parameters;
        self
    }
}

fn object_schema_with_type(
    properties: Vec<(&'static str, RefOr<Schema>)>,
    required: &[&str],
    schema_type: SchemaType,
) -> Schema {
    let builder = properties.into_iter().fold(
        ObjectBuilder::new().schema_type(schema_type),
        |builder, (name, schema)| builder.property(name, schema),
    );
    Schema::Object(
        required
            .iter()
            .fold(builder, |builder, field| builder.required(*field))
            .build(),
    )
}

fn object_schema(properties: Vec<(&'static str, RefOr<Schema>)>, required: &[&str]) -> Schema {
    object_schema_with_type(properties, required, SchemaType::new(Type::Object))
}

fn string_schema() -> Schema {
    Schema::Object(ObjectBuilder::new().schema_type(Type::String).build())
}

fn nullable_string_schema() -> Schema {
    Schema::Object(
        ObjectBuilder::new()
            .schema_type(SchemaType::Array(vec![Type::String, Type::Null]))
            .build(),
    )
}

fn integer_schema() -> Schema {
    Schema::Object(ObjectBuilder::new().schema_type(Type::Integer).build())
}

fn boolean_schema() -> Schema {
    Schema::Object(ObjectBuilder::new().schema_type(Type::Boolean).build())
}

fn email_schema() -> Schema {
    Schema::Object(
        ObjectBuilder::new()
            .schema_type(Type::String)
            .format(Some(utoipa::openapi::schema::SchemaFormat::KnownFormat(
                utoipa::openapi::schema::KnownFormat::Email,
            )))
            .build(),
    )
}

fn string_enum_schema(value: &'static str) -> Schema {
    Schema::Object(
        ObjectBuilder::new()
            .schema_type(Type::String)
            .enum_values(Some(vec![value]))
            .build(),
    )
}

fn array_schema(items: impl Into<RefOr<Schema>>) -> Schema {
    Schema::Array(Array::new(items))
}

fn schema_reference(name: &str) -> RefOr<Schema> {
    Ref::from_schema_name(name).into()
}

fn schema_field(
    name: &'static str,
    schema: impl Into<RefOr<Schema>>,
) -> (&'static str, RefOr<Schema>) {
    (name, schema.into())
}

fn add_schema(components: &mut Components, name: &str, schema: Schema) {
    components.schemas.insert(name.to_owned(), RefOr::T(schema));
}

fn add_doc_operation(paths: &mut Paths, contract: DocOperation) {
    let mut operation = Operation::builder()
        .tag("SCIM")
        .summary(Some(contract.summary));

    for segment in contract.path.split('/') {
        if let Some(name) = segment
            .strip_prefix('{')
            .and_then(|name| name.strip_suffix('}'))
        {
            let mut parameter = Parameter::new(name);
            parameter.parameter_in = ParameterIn::Path;
            parameter.schema = Some(string_schema().into());
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
        openapi_parameter.schema = Some(
            match parameter.parameter_type {
                DocParameterType::String => string_schema(),
                DocParameterType::Integer => integer_schema(),
            }
            .into(),
        );
        operation = operation.parameter(openapi_parameter);
    }
    if let Some(request_schema) = contract.request_schema {
        operation = operation.request_body(Some(
            RequestBodyBuilder::new()
                .description(Some("Request payload"))
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
            "403" => "The credential does not allow this operation.",
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

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    let mut components = Components::new();
    let string = || string_schema();
    let integer = || integer_schema();
    let reference = |name| schema_reference(name);
    let string_array = || array_schema(string());
    add_schema(
        &mut components,
        "Error",
        object_schema(
            vec![schema_field(
                "error",
                object_schema(
                    vec![
                        schema_field("code", string()),
                        schema_field("message", string()),
                    ],
                    &["code", "message"],
                ),
            )],
            &["error"],
        ),
    );
    add_schema(
        &mut components,
        "DirectoryCreateRequest",
        object_schema(
            vec![
                schema_field("org_id", string()),
                schema_field("orgId", string()),
                schema_field("instance_wide", boolean_schema()),
                schema_field("instanceWide", boolean_schema()),
            ],
            &[],
        ),
    );
    add_schema(
        &mut components,
        "DirectoryToken",
        object_schema(
            vec![
                schema_field("id", string()),
                schema_field("org_id", nullable_string_schema()),
                schema_field("token", string()),
                schema_field("instance_wide", boolean_schema()),
            ],
            &["id", "org_id", "token"],
        ),
    );
    add_schema(
        &mut components,
        "DirectorySummary",
        object_schema(
            vec![
                schema_field("id", string()),
                schema_field("org_id", nullable_string_schema()),
                schema_field("instance_wide", boolean_schema()),
                schema_field("created_at", integer()),
            ],
            &["id", "org_id", "created_at"],
        ),
    );
    add_schema(
        &mut components,
        "DirectoryList",
        object_schema(
            vec![schema_field(
                "data",
                array_schema(reference("DirectorySummary")),
            )],
            &["data"],
        ),
    );
    add_schema(
        &mut components,
        "ScimError",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("status", string()),
                schema_field("detail", string()),
                schema_field("scimType", string()),
            ],
            &["schemas", "status", "detail"],
        ),
    );
    add_schema(
        &mut components,
        "ScimName",
        object_schema(
            vec![
                schema_field("givenName", string()),
                schema_field("familyName", string()),
            ],
            &[],
        ),
    );
    add_schema(
        &mut components,
        "ScimUserRequest",
        object_schema(
            vec![
                schema_field("userName", email_schema()),
                schema_field("name", reference("ScimName")),
                schema_field("externalId", string()),
                schema_field("active", boolean_schema()),
            ],
            &["userName"],
        ),
    );
    let meta = |resource_type: &'static str| {
        object_schema(
            vec![
                schema_field("resourceType", string_enum_schema(resource_type)),
                schema_field("location", string()),
            ],
            &["resourceType", "location"],
        )
    };
    add_schema(
        &mut components,
        "ScimUser",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("id", string()),
                schema_field("userName", email_schema()),
                schema_field("externalId", string()),
                schema_field("active", boolean_schema()),
                schema_field("name", reference("ScimName")),
                schema_field("meta", meta("User")),
            ],
            &["schemas", "id", "userName", "active", "meta"],
        ),
    );
    let list = |item: &'static str| {
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("totalResults", integer()),
                schema_field("startIndex", integer()),
                schema_field("itemsPerPage", integer()),
                schema_field("Resources", array_schema(reference(item))),
            ],
            &[
                "schemas",
                "totalResults",
                "startIndex",
                "itemsPerPage",
                "Resources",
            ],
        )
    };
    add_schema(&mut components, "ScimUserList", list("ScimUser"));
    add_schema(
        &mut components,
        "ScimMember",
        object_schema(vec![schema_field("value", string())], &[]),
    );
    add_schema(
        &mut components,
        "ScimGroupRequest",
        object_schema(
            vec![
                schema_field("displayName", string()),
                schema_field("members", array_schema(reference("ScimMember"))),
            ],
            &["displayName"],
        ),
    );
    add_schema(
        &mut components,
        "ScimGroup",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("id", string()),
                schema_field("displayName", string()),
                schema_field("members", array_schema(reference("ScimMember"))),
                schema_field("meta", meta("Group")),
            ],
            &["schemas", "id", "displayName", "members", "meta"],
        ),
    );
    add_schema(&mut components, "ScimGroupList", list("ScimGroup"));
    add_schema(
        &mut components,
        "ScimPatchOperation",
        object_schema(
            vec![
                schema_field("op", string_enum_schema("replace")),
                schema_field("path", string()),
                schema_field("value", Schema::Object(ObjectBuilder::new().build())),
            ],
            &["value"],
        ),
    );
    add_schema(
        &mut components,
        "ScimPatchRequest",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("Operations", array_schema(reference("ScimPatchOperation"))),
            ],
            &["Operations"],
        ),
    );
    let supported = || object_schema(vec![schema_field("supported", boolean_schema())], &[]);
    add_schema(
        &mut components,
        "ScimServiceProviderConfig",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("patch", supported()),
                schema_field(
                    "bulk",
                    object_schema(
                        vec![
                            schema_field("supported", boolean_schema()),
                            schema_field("maxOperations", integer()),
                            schema_field("maxPayloadSize", integer()),
                        ],
                        &[],
                    ),
                ),
                schema_field(
                    "filter",
                    object_schema(
                        vec![
                            schema_field("supported", boolean_schema()),
                            schema_field("maxResults", integer()),
                        ],
                        &[],
                    ),
                ),
                schema_field("changePassword", supported()),
                schema_field("sort", supported()),
                schema_field("etag", supported()),
                schema_field(
                    "authenticationSchemes",
                    array_schema(object_schema(vec![], &[])),
                ),
                schema_field("meta", object_schema(vec![], &[])),
            ],
            &[
                "schemas",
                "patch",
                "bulk",
                "filter",
                "changePassword",
                "sort",
                "etag",
                "authenticationSchemes",
                "meta",
            ],
        ),
    );
    let resource_type_item = object_schema(
        vec![
            schema_field("id", string()),
            schema_field("name", string()),
            schema_field("endpoint", string()),
            schema_field("schema", string()),
        ],
        &["id", "name", "endpoint", "schema"],
    );
    add_schema(
        &mut components,
        "ScimResourceTypes",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("totalResults", integer()),
                schema_field("startIndex", integer()),
                schema_field("itemsPerPage", integer()),
                schema_field("Resources", array_schema(resource_type_item)),
            ],
            &[
                "schemas",
                "totalResults",
                "startIndex",
                "itemsPerPage",
                "Resources",
            ],
        ),
    );
    let schema_item = object_schema(
        vec![
            schema_field("id", string()),
            schema_field("name", string()),
            schema_field("attributes", array_schema(object_schema(vec![], &[]))),
        ],
        &["id", "name", "attributes"],
    );
    add_schema(
        &mut components,
        "ScimSchemas",
        object_schema(
            vec![
                schema_field("schemas", string_array()),
                schema_field("totalResults", integer()),
                schema_field("startIndex", integer()),
                schema_field("itemsPerPage", integer()),
                schema_field("Resources", array_schema(schema_item)),
            ],
            &[
                "schemas",
                "totalResults",
                "startIndex",
                "itemsPerPage",
                "Resources",
            ],
        ),
    );
    components.add_security_scheme(
        "bearerAuth",
        SecurityScheme::Http(HttpBuilder::new().scheme(HttpAuthScheme::Bearer).build()),
    );

    const USER_LIST_QUERY: &[DocQueryParameter] = &[
        DocQueryParameter {
            name: "filter",
            description: "Supports userName eq \"value\" and externalId eq \"value\".",
            required: false,
            parameter_type: DocParameterType::String,
        },
        DocQueryParameter {
            name: "startIndex",
            description: "One-based page start; values below 1 are treated as 1.",
            required: false,
            parameter_type: DocParameterType::Integer,
        },
        DocQueryParameter {
            name: "count",
            description: "Maximum results per page, clamped to 100.",
            required: false,
            parameter_type: DocParameterType::Integer,
        },
    ];
    const GROUP_LIST_QUERY: &[DocQueryParameter] = &[
        DocQueryParameter {
            name: "startIndex",
            description: "One-based page start; values below 1 are treated as 1.",
            required: false,
            parameter_type: DocParameterType::Integer,
        },
        DocQueryParameter {
            name: "count",
            description: "Maximum results per page, clamped to 100.",
            required: false,
            parameter_type: DocParameterType::Integer,
        },
    ];
    for contract in [
        DocOperation::new(
            "/v1/{slug}/directories",
            HttpMethod::Post,
            "Create SCIM directory",
            "201",
            "The directory and its one-time bearer token.",
            Some("DirectoryToken"),
        )
        .request("DirectoryCreateRequest")
        .errors(&["400", "401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/v1/{slug}/directories",
            HttpMethod::Get,
            "List SCIM directories",
            "200",
            "Directory metadata; tokens are never included.",
            Some("DirectoryList"),
        )
        .errors(&["401", "403", "500"]),
        DocOperation::new(
            "/v1/{slug}/directories/{directory_id}",
            HttpMethod::Delete,
            "Delete SCIM directory",
            "204",
            "Directory deleted; no response body. Retry deletion after cleanup errors to re-run child sweeps, including groups left by in-flight creates.",
            None,
        )
        .errors(&["401", "403", "404", "500", "503"]),
        DocOperation::new(
            "/v1/{slug}/directories/{directory_id}/rotate",
            HttpMethod::Post,
            "Rotate SCIM directory token",
            "200",
            "The directory and its new one-time bearer token.",
            Some("DirectoryToken"),
        )
        .errors(&["401", "403", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users",
            HttpMethod::Get,
            "List SCIM users",
            "200",
            "A paginated SCIM User ListResponse.",
            Some("ScimUserList"),
        )
        .scim()
        .query_parameters(USER_LIST_QUERY)
        .errors(&["400", "401", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users",
            HttpMethod::Post,
            "Create SCIM user",
            "201",
            "The created SCIM User; its URI is returned in Location.",
            Some("ScimUser"),
        )
        .scim()
        .request("ScimUserRequest")
        .errors(&["400", "401", "403", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users/{user_id}",
            HttpMethod::Get,
            "Get SCIM user",
            "200",
            "The SCIM User resource.",
            Some("ScimUser"),
        )
        .scim()
        .errors(&["401", "404", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users/{user_id}",
            HttpMethod::Put,
            "Replace SCIM user",
            "200",
            "The updated SCIM User resource.",
            Some("ScimUser"),
        )
        .scim()
        .request("ScimUserRequest")
        .errors(&["400", "401", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users/{user_id}",
            HttpMethod::Patch,
            "Patch SCIM user",
            "200",
            "The updated SCIM User resource.",
            Some("ScimUser"),
        )
        .scim()
        .request("ScimPatchRequest")
        .errors(&["400", "401", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Users/{user_id}",
            HttpMethod::Delete,
            "Deprovision SCIM user",
            "204",
            "User deprovisioned; no response body.",
            None,
        )
        .scim()
        .errors(&["401", "404", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups",
            HttpMethod::Get,
            "List SCIM groups",
            "200",
            "A paginated SCIM Group ListResponse.",
            Some("ScimGroupList"),
        )
        .scim()
        .query_parameters(GROUP_LIST_QUERY)
        .errors(&["400", "401", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups",
            HttpMethod::Post,
            "Create SCIM group",
            "201",
            "The created SCIM Group; its URI is returned in Location.",
            Some("ScimGroup"),
        )
        .scim()
        .request("ScimGroupRequest")
        .errors(&["400", "401", "500", "503"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups/{group_id}",
            HttpMethod::Get,
            "Get SCIM group",
            "200",
            "The SCIM Group resource.",
            Some("ScimGroup"),
        )
        .scim()
        .errors(&["401", "404", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups/{group_id}",
            HttpMethod::Put,
            "Replace SCIM group",
            "200",
            "The replaced SCIM Group resource.",
            Some("ScimGroup"),
        )
        .scim()
        .request("ScimGroupRequest")
        .errors(&["400", "401", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups/{group_id}",
            HttpMethod::Patch,
            "Patch SCIM group",
            "200",
            "The updated SCIM Group resource.",
            Some("ScimGroup"),
        )
        .scim()
        .request("ScimPatchRequest")
        .errors(&["400", "401", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Groups/{group_id}",
            HttpMethod::Delete,
            "Delete SCIM group",
            "204",
            "Group deleted; no response body.",
            None,
        )
        .scim()
        .errors(&["401", "404", "409", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/ServiceProviderConfig",
            HttpMethod::Get,
            "SCIM service provider capabilities",
            "200",
            "Supported SCIM service provider features.",
            Some("ScimServiceProviderConfig"),
        )
        .scim()
        .errors(&["401", "404", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/ResourceTypes",
            HttpMethod::Get,
            "SCIM resource types",
            "200",
            "Supported User and Group resource types.",
            Some("ScimResourceTypes"),
        )
        .scim()
        .errors(&["401", "404", "500"]),
        DocOperation::new(
            "/scim/{directory_id}/Schemas",
            HttpMethod::Get,
            "SCIM schemas",
            "200",
            "Supported User and Group attributes.",
            Some("ScimSchemas"),
        )
        .scim()
        .errors(&["401", "404", "500"]),
    ] {
        add_doc_operation(&mut paths, contract);
    }
    let mut document = OpenApiDoc::new(Info::new("TerraScale Auth SCIM 2.0 API", "1.0.0"), paths);
    document.components = Some(components);
    document
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScimGroup {
    id: String,
    instance_id: String,
    directory_id: String,
    display_name: String,
    #[serde(default)]
    member_user_ids: Vec<String>,
    created_at: i64,
    updated_at: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct UserBody {
    user_name: Option<String>,
    name: Option<ScimName>,
    external_id: Option<String>,
    active: Option<bool>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScimName {
    given_name: Option<String>,
    family_name: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct GroupBody {
    display_name: Option<String>,
    members: Option<Vec<ScimMember>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScimMember {
    value: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PatchBody {
    #[serde(rename = "Operations", alias = "operations")]
    operations: Option<Vec<PatchOperation>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PatchOperation {
    op: Option<String>,
    path: Option<String>,
    value: Option<Value>,
}

struct ScimList {
    total: usize,
    items: Vec<Value>,
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "SCIM storage or user operation failed");
    AppError::internal()
}

fn scim_response(status: StatusCode, body: Value) -> Response {
    let mut response = (status, Json(body)).into_response();
    response.headers_mut().insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/scim+json"),
    );
    response
}

fn scim_json(body: Value) -> Response {
    scim_response(StatusCode::OK, body)
}

fn scim_error(status: StatusCode, detail: impl Into<String>, scim_type: Option<&str>) -> Response {
    let mut body = json!({"schemas":[ERROR_SCHEMA],"status":status.as_u16().to_string(),"detail":detail.into()});
    if let Some(kind) = scim_type {
        body["scimType"] = json!(kind);
    }
    let mut response = scim_response(status, body);
    if status == StatusCode::UNAUTHORIZED {
        response
            .headers_mut()
            .insert(header::WWW_AUTHENTICATE, HeaderValue::from_static("Bearer"));
    }
    response
}

fn scim_created(body: Value, location: &str) -> Response {
    let mut response = scim_response(StatusCode::CREATED, body);
    match HeaderValue::from_str(location) {
        Ok(value) => {
            response.headers_mut().insert(header::LOCATION, value);
        }
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to build resource location",
                None,
            );
        }
    }
    response
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn read_json<T: for<'de> Deserialize<'de>>(request: Request) -> Result<T, Response> {
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            scim_error(
                StatusCode::BAD_REQUEST,
                "the request body is too large or could not be read",
                Some("invalidSyntax"),
            )
        })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        scim_error(
            StatusCode::BAD_REQUEST,
            "the request body is not valid JSON",
            Some("invalidSyntax"),
        )
    })
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn directory_for_token(
    state: &AppState,
    directory_id: &str,
    headers: &HeaderMap,
) -> Result<ScimDirectory, Response> {
    let Some(header) = headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
    else {
        return Err(scim_error(
            StatusCode::UNAUTHORIZED,
            "a bearer token is required",
            None,
        ));
    };
    let Some((scheme, token)) = header.split_once(' ') else {
        return Err(scim_error(
            StatusCode::UNAUTHORIZED,
            "a bearer token is required",
            None,
        ));
    };
    let token = token.trim();
    if !scheme.eq_ignore_ascii_case("Bearer") || !token.starts_with("scim_") {
        return Err(scim_error(
            StatusCode::UNAUTHORIZED,
            "invalid bearer token",
            None,
        ));
    }
    let directory: Option<ScimDirectory> = state
        .store
        .query_one("scim_directories", "tokenKey", &sha256_hex(token))
        .await
        .map_err(|error| {
            scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                format!("storage error: {error}"),
                None,
            )
        })?;
    match directory.filter(|directory| {
        directory.id == directory_id && directory.status == ScimDirectoryStatus::Active
    }) {
        Some(directory)
            if parent_org_active(state, &directory).await.map_err(|_| {
                scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to validate directory organization",
                    None,
                )
            })? =>
        {
            Ok(directory)
        }
        None => Err(scim_error(
            StatusCode::UNAUTHORIZED,
            "invalid bearer token for this directory",
            None,
        )),
        Some(_) => Err(scim_error(
            StatusCode::UNAUTHORIZED,
            "directory organization is inactive",
            None,
        )),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn management_context(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
    family: &str,
) -> Result<management::InstanceContext, Response> {
    let context = management::resolve_instance(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)?;
    if !management::scope_allows(&context.scopes, &format!("/v1/{slug}/{family}")) {
        return Err((StatusCode::FORBIDDEN, Json(json!({"error":{"code":"insufficient_scope","message":"The API key's scopes do not allow this route."}}))).into_response());
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

fn now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

async fn audit(
    state: &AppState,
    directory: &ScimDirectory,
    event_type: &str,
    data: Map<String, Value>,
) -> Result<(), AppError> {
    let event = AuditEvent {
        id: format!("evt_{}", random_token(20)),
        instance_id: directory.instance_id.clone(),
        r#type: event_type.to_owned(),
        actor_id: Some(directory.id.clone()),
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

async fn management_audit(
    state: &AppState,
    context: &management::InstanceContext,
    event_type: &str,
    data: Map<String, Value>,
) -> Result<(), AppError> {
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

async fn audit_user_deprovisioned(
    state: &AppState,
    directory: &ScimDirectory,
    link: &ScimUserLink,
) -> Result<(), AppError> {
    let key = format!(
        "{}:{}:{}",
        directory.id,
        link.user_id,
        link.incarnation_id.as_deref().unwrap_or("legacy")
    );
    let data = Map::from_iter([
        (String::from("user_id"), json!(&link.user_id)),
        (
            String::from("incarnation_id"),
            json!(link.incarnation_id.as_deref()),
        ),
    ]);
    let event = AuditEvent {
        id: format!("evt_scim_user_deprovisioned_{}", sha256_hex(&key)),
        instance_id: directory.instance_id.clone(),
        r#type: "directory.user_deprovisioned".to_owned(),
        actor_id: Some(directory.id.clone()),
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
            let existing = state
                .store
                .get_audit_event(&event.id)
                .await
                .map_err(internal)?;
            if existing.is_some_and(|existing| {
                existing.instance_id == directory.instance_id
                    && existing.r#type == event.r#type
                    && existing.actor_id == event.actor_id
                    && existing.data == data
            }) {
                Ok(())
            } else {
                Err(internal(error))
            }
        }
    }
}

async fn audit_directory_deleted(
    state: &AppState,
    context: &management::InstanceContext,
    directory: &ScimDirectory,
) -> Result<(), AppError> {
    let key = format!("{}:{}", context.instance.id, directory.id);
    let data = Map::from_iter([(String::from("directory_id"), json!(directory.id))]);
    let event = AuditEvent {
        id: format!("evt_scim_directory_deleted_{}", sha256_hex(&key)),
        instance_id: context.instance.id.clone(),
        r#type: "directory.deleted".to_owned(),
        actor_id: Some(context.api_key_id.clone()),
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
            let existing = state
                .store
                .get_audit_event(&event.id)
                .await
                .map_err(internal)?;
            if existing.is_some_and(|existing| {
                existing.instance_id == context.instance.id
                    && existing.r#type == event.r#type
                    && existing.data == data
            }) {
                Ok(())
            } else {
                Err(internal(error))
            }
        }
    }
}

async fn create_directory(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Result<Response, AppError> {
    let context = match management_context(&state, &headers, &slug, "directories").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let body: Value = match read_json(request).await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let org_id = body
        .get("org_id")
        .or_else(|| body.get("orgId"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .map(str::to_owned);
    let instance_wide = match body
        .get("instance_wide")
        .or_else(|| body.get("instanceWide"))
    {
        Some(Value::Bool(value)) => *value,
        Some(_) => {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_scope",
                "instance_wide must be a boolean",
            ));
        }
        None => false,
    };
    if instance_wide == org_id.is_some() {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_scope",
            "select exactly one of org_id and instance_wide: true",
        ));
    }
    if let Some(org_id) = &org_id {
        let organization: Option<Organization> = state
            .store
            .get("organizations", org_id)
            .await
            .map_err(internal)?;
        if !organization.is_some_and(|organization| {
            organization.instance_id == context.instance.id
                && organization.status == OrganizationStatus::Active
        }) {
            return Ok(api_error(
                StatusCode::NOT_FOUND,
                "organization_not_found",
                "no organization with this id in this instance",
            ));
        }
    }
    let token = format!("scim_{}", random_token(40));
    let directory = ScimDirectory {
        id: format!("dir_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        org_id,
        instance_wide,
        status: ScimDirectoryStatus::Active,
        token_key: sha256_hex(&token),
        created_at: now(),
    };
    match state.store.create_scim_directory(&directory).await {
        Ok(_) => {}
        Err(error) if error.is_conflict() => {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "token_collision",
                "the generated token collided; retry the request",
            ));
        }
        Err(error) => return Err(internal(error)),
    }
    management_audit(
        &state,
        &context,
        "directory.created",
        Map::from_iter([(String::from("directory_id"), json!(directory.id))]),
    )
    .await?;
    Ok((
        StatusCode::CREATED,
        Json(json!({"id":directory.id,"org_id":directory.org_id,"instance_wide":directory.instance_wide,"token":token})),
    )
        .into_response())
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

async fn list_directories(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match management_context(&state, &headers, &slug, "directories").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let directories: Vec<ScimDirectory> = query_all(
        &state,
        "scim_directories",
        "instanceId",
        &context.instance.id,
    )
    .await?;
    let data: Vec<Value> = directories.into_iter().filter(|directory| directory.status == ScimDirectoryStatus::Active).map(|directory| json!({"id":directory.id,"org_id":directory.org_id,"instance_wide":directory.instance_wide,"created_at":directory.created_at})).collect();
    Ok((StatusCode::OK, Json(json!({"data":data}))).into_response())
}

async fn delete_directory(
    State(state): State<AppState>,
    Path((slug, directory_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match management_context(&state, &headers, &slug, "directories").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some((mut directory, version)) = state
        .store
        .get_versioned::<ScimDirectory>("scim_directories", &directory_id)
        .await
        .map_err(internal)?
        .filter(|(directory, _)| directory.instance_id == context.instance.id)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "directory_not_found",
            "no directory with this id in this instance",
        ));
    };
    if directory.status == ScimDirectoryStatus::Active {
        directory.status = ScimDirectoryStatus::Deleting;
        if !state
            .store
            .compare_and_put("scim_directories", &directory.id, &directory, &version)
            .await
            .map_err(internal)?
        {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "directory_changed",
                "the directory changed concurrently; retry the request",
            ));
        }
    }
    // The tombstone denies new tokens before sweeping. In-flight group writes may
    // finish later; their create handlers clean themselves up, and retrying this
    // DELETE re-runs the sweep while the Deleted tombstone is retained.
    for _ in 0..8 {
        let groups: Vec<ScimGroup> =
            query_all(&state, GROUPS, "directoryId", &directory.id).await?;
        if groups.is_empty() {
            break;
        }
        for group in groups {
            state
                .store
                .delete_ignoring_missing(GROUPS, &group.id)
                .await
                .map_err(internal)?;
        }
    }
    if !query_all::<ScimGroup>(&state, GROUPS, "directoryId", &directory.id)
        .await?
        .is_empty()
    {
        return Ok(api_error(
            StatusCode::SERVICE_UNAVAILABLE,
            "directory_deletion_in_progress",
            "groups are still changing; retry directory deletion",
        ));
    }
    for _ in 0..8 {
        let links: Vec<ScimUserLink> =
            query_all::<ScimUserLink>(&state, USER_LINKS, "directoryId", &directory.id).await?;
        if links.is_empty() {
            break;
        }
        let mut pending = false;
        for link in links {
            if link.cleanup_complete {
                audit_user_deprovisioned(&state, &directory, &link).await?;
                continue;
            }
            pending = true;
            if let Some(tombstone) = tombstone_user_link(
                &state,
                &directory,
                &link.user_id,
                link.incarnation_id.as_deref(),
            )
            .await?
            {
                finish_deprovisioned_link(
                    &state,
                    &directory,
                    &link.user_id,
                    tombstone.incarnation_id.as_deref(),
                    false,
                )
                .await?;
            }
        }
        if !pending {
            break;
        }
    }
    for link in query_all::<ScimUserLink>(&state, USER_LINKS, "directoryId", &directory.id).await? {
        if !link.cleanup_complete {
            return Ok(api_error(
                StatusCode::SERVICE_UNAVAILABLE,
                "directory_deletion_in_progress",
                "links are still changing; retry directory deletion",
            ));
        }
        audit_user_deprovisioned(&state, &directory, &link).await?;
    }
    for _ in 0..8 {
        let Some((mut latest, version)) = state
            .store
            .get_versioned::<ScimDirectory>("scim_directories", &directory.id)
            .await
            .map_err(internal)?
        else {
            break;
        };
        if latest.status == ScimDirectoryStatus::Deleted {
            break;
        }
        if latest.status != ScimDirectoryStatus::Deleting {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "directory_changed",
                "directory state changed concurrently",
            ));
        }
        latest.status = ScimDirectoryStatus::Deleted;
        if state
            .store
            .compare_and_put("scim_directories", &latest.id, &latest, &version)
            .await
            .map_err(internal)?
        {
            break;
        }
    }
    if !matches!(
        state
            .store
            .get::<ScimDirectory>("scim_directories", &directory.id)
            .await
            .map_err(internal)?
            .map(|directory| directory.status),
        Some(ScimDirectoryStatus::Deleted)
    ) {
        return Ok(api_error(
            StatusCode::SERVICE_UNAVAILABLE,
            "directory_deletion_in_progress",
            "directory deletion is still in progress; retry the request",
        ));
    }
    match state
        .store
        .release_unique("scim-token", &directory.token_key, &directory.id)
        .await
    {
        Ok(()) => {}
        Err(StoreError::Contention) => {
            return Ok(api_error(
                StatusCode::SERVICE_UNAVAILABLE,
                "directory_deletion_in_progress",
                "directory cleanup is still in progress; retry the request",
            ));
        }
        Err(error) => return Err(internal(error)),
    }
    audit_directory_deleted(&state, &context, &directory).await?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

async fn rotate_directory_token(
    State(state): State<AppState>,
    Path((slug, directory_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, AppError> {
    let context = match management_context(&state, &headers, &slug, "directories").await {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let Some((directory, version)) = state
        .store
        .get_versioned::<ScimDirectory>("scim_directories", &directory_id)
        .await
        .map_err(internal)?
        .filter(|(directory, _)| {
            directory.instance_id == context.instance.id
                && directory.status == ScimDirectoryStatus::Active
        })
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "directory_not_found",
            "no directory with this id in this instance",
        ));
    };
    let token = format!("scim_{}", random_token(40));
    let new_key = sha256_hex(&token);
    if let Err(error) = state
        .store
        .claim_unique("scim-token", &new_key, &directory.id)
        .await
    {
        if error.is_conflict() {
            return Ok(api_error(
                StatusCode::CONFLICT,
                "token_collision",
                "the generated token collided; retry the request",
            ));
        }
        return Err(internal(error));
    }
    let mut rotated = directory.clone();
    rotated.token_key = new_key.clone();
    match state
        .store
        .compare_and_put("scim_directories", &directory.id, &rotated, &version)
        .await
    {
        Ok(true) => {}
        Ok(false) => {
            state
                .store
                .release_unique("scim-token", &new_key, &directory.id)
                .await
                .map_err(internal)?;
            return Ok(api_error(
                StatusCode::CONFLICT,
                "directory_changed",
                "the directory changed concurrently; retry the request",
            ));
        }
        Err(error) => {
            if let Err(release_error) = state
                .store
                .release_unique("scim-token", &new_key, &directory.id)
                .await
            {
                tracing::error!(
                    ?error,
                    ?release_error,
                    "failed to release unused SCIM token claim after directory update failed"
                );
                return Err(internal(release_error));
            }
            return Err(internal(error));
        }
    }
    state
        .store
        .release_unique("scim-token", &directory.token_key, &directory.id)
        .await
        .map_err(internal)?;
    management_audit(
        &state,
        &context,
        "directory.token_rotated",
        Map::from_iter([(String::from("directory_id"), json!(directory.id))]),
    )
    .await?;
    Ok((
        StatusCode::OK,
        Json(json!({"id":rotated.id,"org_id":rotated.org_id,"instance_wide":rotated.instance_wide,"token":token})),
    )
        .into_response())
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn paginate(request: &Request) -> Result<(usize, usize), Response> {
    let mut start = 1_i64;
    let mut count = MAX_RESULTS as i64;
    if let Some(query) = request.uri().query() {
        for (key, value) in url::form_urlencoded::parse(query.as_bytes()) {
            match key.as_ref() {
                "startIndex" => match value.parse::<i64>() {
                    Ok(value) => start = value,
                    Err(_) => {
                        return Err(scim_error(
                            StatusCode::BAD_REQUEST,
                            "startIndex must be an integer",
                            Some("invalidValue"),
                        ));
                    }
                },
                "count" => match value.parse::<i64>() {
                    Ok(value) => count = value,
                    Err(_) => {
                        return Err(scim_error(
                            StatusCode::BAD_REQUEST,
                            "count must be an integer",
                            Some("invalidValue"),
                        ));
                    }
                },
                _ => {}
            }
        }
    }
    let start = usize::try_from(start.max(1)).unwrap_or(usize::MAX);
    let count = usize::try_from(count.max(0))
        .unwrap_or(usize::MAX)
        .min(MAX_RESULTS);
    Ok((start, count))
}

fn list_response(list: ScimList, start: usize) -> Value {
    json!({"schemas":[LIST_SCHEMA],"totalResults":list.total,"startIndex":start,"itemsPerPage":list.items.len(),"Resources":list.items})
}

fn projected_user_name<'a>(
    directory: &ScimDirectory,
    user: &'a User,
    link: &'a ScimUserLink,
) -> Option<&'a str> {
    if !directory.instance_wide && link.profile_initialized {
        link.profile_user_name.as_deref()
    } else {
        user.email.as_deref()
    }
}

fn projected_name<'a>(
    directory: &ScimDirectory,
    user: &'a User,
    link: &'a ScimUserLink,
) -> Option<&'a str> {
    if !directory.instance_wide && link.profile_initialized {
        link.profile_name.as_deref()
    } else {
        user.name.as_deref()
    }
}

fn user_view(directory: &ScimDirectory, user: &User, link: &ScimUserLink) -> Value {
    let mut body = json!({
        "schemas":[USER_SCHEMA],
        "id":user.id,
        "userName":projected_user_name(directory, user, link).unwrap_or(&user.id),
        "active":link.active && !user.banned,
        "meta":{"resourceType":"User","location":format!("/scim/{}/Users/{}", directory.id, user.id)},
    });
    if let Some(external_id) = &link.external_id {
        body["externalId"] = json!(external_id);
    }
    if let Some(name) = name_view(projected_name(directory, user, link)) {
        body["name"] = name;
    }
    body
}

fn group_view(directory_id: &str, group: &ScimGroup) -> Value {
    json!({
        "schemas":[GROUP_SCHEMA],
        "id":group.id,
        "displayName":group.display_name,
        "members":group.member_user_ids.iter().map(|id| json!({"value":id})).collect::<Vec<_>>(),
        "meta":{"resourceType":"Group","location":format!("/scim/{directory_id}/Groups/{}", group.id)},
    })
}

fn link_id(directory_id: &str, user_id: &str) -> String {
    format!("{directory_id}:{user_id}")
}

fn local_name_key(directory: &ScimDirectory, name: &str) -> String {
    format!("{}:{}", directory.id, users::normalize_email(name))
}

async fn owned_local_name_version(
    state: &AppState,
    directory: &ScimDirectory,
    name: &str,
    owner: &str,
) -> Result<Option<String>, AppError> {
    let id = unique_claim_id(SCIM_USER_NAME_CLAIM, &local_name_key(directory, name));
    let claim = state
        .store
        .get_versioned::<Value>("uniques", &id)
        .await
        .map_err(internal)?;
    Ok(claim.and_then(|(claim, version)| {
        (claim.get("ownerType").and_then(Value::as_str) == Some(SCIM_USER_NAME_CLAIM)
            && claim.get("ownerId").and_then(Value::as_str) == Some(owner)
            && claim.get("released").and_then(Value::as_bool) != Some(true))
        .then_some(version)
    }))
}

async fn claim_local_name(
    state: &AppState,
    directory: &ScimDirectory,
    name: &str,
    owner: &str,
) -> Result<(), AppError> {
    let key = local_name_key(directory, name);
    match state
        .store
        .claim_unique(SCIM_USER_NAME_CLAIM, &key, owner)
        .await
    {
        Ok(()) => Ok(()),
        Err(error) => {
            let id = unique_claim_id(SCIM_USER_NAME_CLAIM, &key);
            for _ in 0..8 {
                let Some((mut claim, version)) = state
                    .store
                    .get_versioned::<Value>("uniques", &id)
                    .await
                    .map_err(internal)?
                else {
                    return Err(internal(error));
                };
                if claim.get("ownerType").and_then(Value::as_str) != Some(SCIM_USER_NAME_CLAIM) {
                    return Err(internal("SCIM userName claim type mismatch"));
                }
                if !local_name_claim_available(&claim, owner) {
                    return Err(AppError::new(
                        StatusCode::CONFLICT,
                        "scim_user_name_taken",
                        "another directory user has this userName",
                    ));
                }
                claim["ownerId"] = json!(owner);
                claim["released"] = json!(false);
                claim["nonce"] = json!(random_token(8));
                if state
                    .store
                    .compare_and_put("uniques", &id, &claim, &version)
                    .await
                    .map_err(internal)?
                {
                    return Ok(());
                }
            }
            Err(internal("SCIM userName claim changed concurrently"))
        }
    }
}

fn local_name_claim_available(claim: &Value, owner: &str) -> bool {
    claim.get("ownerId").and_then(Value::as_str) == Some(owner)
        || claim.get("released").and_then(Value::as_bool) == Some(true)
}

fn local_name_claim_release_matches(
    claim: &Value,
    owner: &str,
    expected_version: Option<&str>,
    version: &str,
) -> bool {
    claim.get("ownerType").and_then(Value::as_str) == Some(SCIM_USER_NAME_CLAIM)
        && claim.get("ownerId").and_then(Value::as_str) == Some(owner)
        && claim.get("released").and_then(Value::as_bool) != Some(true)
        && expected_version.is_none_or(|expected| expected == version)
}

async fn release_local_name(
    state: &AppState,
    directory: &ScimDirectory,
    name: &str,
    owner: &str,
    expected_version: Option<&str>,
) -> Result<(), AppError> {
    let id = unique_claim_id(SCIM_USER_NAME_CLAIM, &local_name_key(directory, name));
    for _ in 0..8 {
        let Some((mut claim, version)) = state
            .store
            .get_versioned::<Value>("uniques", &id)
            .await
            .map_err(internal)?
        else {
            return Ok(());
        };
        if claim.get("ownerType").and_then(Value::as_str) != Some(SCIM_USER_NAME_CLAIM) {
            return Err(internal("SCIM userName claim type mismatch"));
        }
        if !local_name_claim_release_matches(&claim, owner, expected_version, &version) {
            return Ok(());
        }
        claim["released"] = json!(true);
        if state
            .store
            .compare_and_put("uniques", &id, &claim, &version)
            .await
            .map_err(internal)?
        {
            return Ok(());
        }
        // A delayed successful rename only owns the exact claim generation it
        // captured before its link CAS. Never chase a newer generation.
        if expected_version.is_some() {
            return Ok(());
        }
    }
    Err(internal("SCIM userName claim kept changing during release"))
}

async fn find_user_link(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<Option<ScimUserLink>, AppError> {
    Ok(find_user_link_for_cleanup(state, directory, user_id)
        .await?
        .filter(|link| !link.deleting && !link.cleanup_complete))
}

async fn find_user_link_for_cleanup(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<Option<ScimUserLink>, AppError> {
    let link: Option<ScimUserLink> = state
        .store
        .get(USER_LINKS, &link_id(&directory.id, user_id))
        .await
        .map_err(internal)?;
    Ok(link.filter(|link| {
        link.directory_id == directory.id
            && link.instance_id == directory.instance_id
            && link.user_id == user_id
    }))
}

async fn directory_is_active(
    state: &AppState,
    directory: &ScimDirectory,
) -> Result<bool, AppError> {
    let latest: Option<ScimDirectory> = state
        .store
        .get("scim_directories", &directory.id)
        .await
        .map_err(internal)?;
    match latest {
        Some(latest) if latest.status == ScimDirectoryStatus::Active => {
            parent_org_active(state, &latest).await
        }
        _ => Ok(false),
    }
}

async fn parent_org_active(state: &AppState, directory: &ScimDirectory) -> Result<bool, AppError> {
    let Some(org_id) = &directory.org_id else {
        return Ok(directory.instance_wide);
    };
    let organization: Option<Organization> = state
        .store
        .get("organizations", org_id)
        .await
        .map_err(internal)?;
    Ok(organization.is_some_and(|organization| {
        organization.instance_id == directory.instance_id
            && organization.status == OrganizationStatus::Active
    }))
}

enum LinkExpectation {
    Absent,
    Incarnation(Option<String>),
    Completed(Option<String>),
}

fn link_matches_expectation(link: &ScimUserLink, expected: &LinkExpectation) -> bool {
    match expected {
        LinkExpectation::Absent => false,
        LinkExpectation::Incarnation(incarnation) => {
            !link.cleanup_complete && incarnation == &link.incarnation_id
        }
        LinkExpectation::Completed(incarnation) => {
            link.cleanup_complete && incarnation == &link.incarnation_id
        }
    }
}

struct LinkUpdate {
    external_id: Option<Option<String>>,
    active: Option<bool>,
    profile_user_name: Option<String>,
    profile_name: Option<Option<String>>,
    profile_seed: Option<(Option<String>, Option<String>)>,
    create_if_missing: bool,
    expected: LinkExpectation,
}

async fn write_link(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
    update: LinkUpdate,
) -> Result<ScimUserLink, AppError> {
    let LinkUpdate {
        external_id,
        active,
        profile_user_name,
        profile_name,
        profile_seed,
        create_if_missing,
        expected,
    } = update;
    let id = link_id(&directory.id, user_id);
    for _ in 0..8 {
        if !directory_is_active(state, directory).await? {
            return Err(internal("directory is inactive"));
        }
        if let Some((mut link, version)) = state
            .store
            .get_versioned::<ScimUserLink>(USER_LINKS, &id)
            .await
            .map_err(internal)?
        {
            if link.cleanup_complete {
                if !create_if_missing || !link_matches_expectation(&link, &expected) {
                    return Err(AppError::new(
                        StatusCode::CONFLICT,
                        "scim_link_changed",
                        "the directory user changed concurrently; retry the request",
                    ));
                }
                if link.directory_id != directory.id
                    || link.instance_id != directory.instance_id
                    || link.user_id != user_id
                {
                    return Err(internal("SCIM link identity mismatch"));
                }
                audit_user_deprovisioned(state, directory, &link).await?;
                let replacement = ScimUserLink {
                    id: id.clone(),
                    instance_id: directory.instance_id.clone(),
                    directory_id: directory.id.clone(),
                    user_id: user_id.to_owned(),
                    external_id: external_id.clone().flatten(),
                    active: active.unwrap_or(true),
                    deleting: false,
                    cleanup_owner: None,
                    cleanup_complete: false,
                    incarnation_id: Some(random_token(24)),
                    profile_user_name: profile_user_name
                        .clone()
                        .or_else(|| profile_seed.as_ref().and_then(|seed| seed.0.clone())),
                    profile_name: profile_name
                        .clone()
                        .unwrap_or_else(|| profile_seed.as_ref().and_then(|seed| seed.1.clone())),
                    profile_initialized: profile_user_name.is_some()
                        || profile_name.is_some()
                        || profile_seed.is_some(),
                    created_at: now(),
                };
                if !directory.instance_wide
                    && let Some(name) = &replacement.profile_user_name
                {
                    claim_local_name(state, directory, name, &id).await?;
                }
                if state
                    .store
                    .compare_and_put(USER_LINKS, &id, &replacement, &version)
                    .await
                    .map_err(internal)?
                {
                    if !directory_is_active(state, directory).await? {
                        if let Some(tombstone) = tombstone_user_link(
                            state,
                            directory,
                            user_id,
                            replacement.incarnation_id.as_deref(),
                        )
                        .await?
                        {
                            finish_deprovisioned_link(
                                state,
                                directory,
                                user_id,
                                tombstone.incarnation_id.as_deref(),
                                true,
                            )
                            .await?;
                        }
                        return Err(internal("directory became inactive"));
                    }
                    return Ok(replacement);
                }
                continue;
            }
            if !link_matches_expectation(&link, &expected) {
                return Err(AppError::new(
                    StatusCode::CONFLICT,
                    "scim_link_changed",
                    "the directory user changed concurrently; retry the request",
                ));
            }
            if link.directory_id != directory.id
                || link.instance_id != directory.instance_id
                || link.user_id != user_id
            {
                return Err(internal("SCIM link identity mismatch"));
            }
            if link.deleting {
                return Err(internal("SCIM link is being deprovisioned"));
            }
            let old_name = if link.profile_initialized {
                link.profile_user_name.clone()
            } else {
                profile_seed.as_ref().and_then(|seed| seed.0.clone())
            };
            let old_claim_version = if !directory.instance_wide {
                match &old_name {
                    Some(name) => owned_local_name_version(state, directory, name, &id).await?,
                    None => None,
                }
            } else {
                None
            };
            if let Some(external_id) = &external_id {
                link.external_id = external_id.clone();
            }
            if let Some(active) = active {
                link.active = active;
            }
            if !link.profile_initialized
                && let Some((user_name, name)) = &profile_seed
            {
                link.profile_user_name = user_name.clone();
                link.profile_name = name.clone();
                link.profile_initialized = true;
            }
            if let Some(user_name) = &profile_user_name {
                link.profile_user_name = Some(user_name.clone());
                link.profile_initialized = true;
            }
            if let Some(name) = &profile_name {
                link.profile_name = name.clone();
                link.profile_initialized = true;
            }
            if !directory.instance_wide
                && let Some(name) = &link.profile_user_name
                && (old_claim_version.is_none()
                    || old_name.as_deref().is_none_or(|old| {
                        local_name_key(directory, old) != local_name_key(directory, name)
                    }))
            {
                claim_local_name(state, directory, name, &id).await?;
            }
            if state
                .store
                .compare_and_put(USER_LINKS, &id, &link, &version)
                .await
                .map_err(internal)?
            {
                if !directory_is_active(state, directory).await? {
                    if let Some(tombstone) = tombstone_user_link(
                        state,
                        directory,
                        user_id,
                        link.incarnation_id.as_deref(),
                    )
                    .await?
                    {
                        finish_deprovisioned_link(
                            state,
                            directory,
                            user_id,
                            tombstone.incarnation_id.as_deref(),
                            true,
                        )
                        .await?;
                    }
                    return Err(internal("directory became inactive"));
                }
                if !directory.instance_wide
                    && let Some(old_name) = old_name
                    && let Some(old_version) = old_claim_version.as_deref()
                    && link.profile_user_name.as_deref().is_some_and(|name| {
                        local_name_key(directory, name) != local_name_key(directory, &old_name)
                    })
                {
                    release_local_name(state, directory, &old_name, &id, Some(old_version)).await?;
                }
                return Ok(link);
            }
            // A competing writer with the same link owner can refresh this claim
            // before our CAS loses. A loser cannot prove that its claim version is
            // still private, so retain the reservation until a successful rename
            // or deprovision releases it.
        } else {
            if !create_if_missing || !matches!(&expected, LinkExpectation::Absent) {
                return Err(AppError::new(
                    StatusCode::CONFLICT,
                    "scim_link_changed",
                    "the directory user changed concurrently; retry the request",
                ));
            }
            let link = ScimUserLink {
                id: id.clone(),
                instance_id: directory.instance_id.clone(),
                directory_id: directory.id.clone(),
                user_id: user_id.to_owned(),
                external_id: external_id.clone().flatten(),
                active: active.unwrap_or(true),
                deleting: false,
                cleanup_owner: None,
                cleanup_complete: false,
                incarnation_id: Some(random_token(24)),
                profile_user_name: profile_user_name
                    .clone()
                    .or_else(|| profile_seed.as_ref().and_then(|seed| seed.0.clone())),
                profile_name: profile_name
                    .clone()
                    .unwrap_or_else(|| profile_seed.as_ref().and_then(|seed| seed.1.clone())),
                profile_initialized: profile_user_name.is_some()
                    || profile_name.is_some()
                    || profile_seed.is_some(),
                created_at: now(),
            };
            if !directory.instance_wide
                && let Some(name) = &link.profile_user_name
            {
                claim_local_name(state, directory, name, &id).await?;
            }
            match state.store.create(USER_LINKS, &id, &link).await {
                Ok(()) => {
                    if !directory_is_active(state, directory).await? {
                        if let Some(tombstone) = tombstone_user_link(
                            state,
                            directory,
                            user_id,
                            link.incarnation_id.as_deref(),
                        )
                        .await?
                        {
                            finish_deprovisioned_link(
                                state,
                                directory,
                                user_id,
                                tombstone.incarnation_id.as_deref(),
                                true,
                            )
                            .await?;
                        }
                        return Err(internal("directory became inactive"));
                    }
                    return Ok(link);
                }
                Err(error) if error.is_conflict() => {
                    // Concurrent creators share the owner id. This reservation
                    // may belong to the winner or to another pending creator.
                    continue;
                }
                Err(error) => return Err(internal(error)),
            }
        }
    }
    Err(internal("SCIM link changed concurrently"))
}

fn normalize_external_id(external_id: Option<&str>) -> Option<String> {
    external_id
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
}

fn name_view(name: Option<&str>) -> Option<Value> {
    let name = name?.trim();
    if name.is_empty() {
        return None;
    }
    Some(match name.split_once(' ') {
        Some((given, family)) => json!({"givenName":given,"familyName":family}),
        None => json!({"givenName":name}),
    })
}

fn join_name(name: Option<ScimName>) -> Option<String> {
    let name = name?;
    let parts = [name.given_name, name.family_name]
        .into_iter()
        .flatten()
        .map(|part| part.trim().to_owned())
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>();
    (!parts.is_empty()).then(|| parts.join(" "))
}

async fn cleanup_membership_and_groups(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<(), AppError> {
    cleanup_owned_membership(state, directory, user_id).await?;
    let groups: Vec<ScimGroup> = query_all(state, GROUPS, "directoryId", &directory.id).await?;
    for group in groups {
        let mut cleaned = false;
        for _ in 0..8 {
            let Some((mut latest, version)) = state
                .store
                .get_versioned::<ScimGroup>(GROUPS, &group.id)
                .await
                .map_err(internal)?
            else {
                cleaned = true;
                break;
            };
            if latest.instance_id != directory.instance_id
                || latest.directory_id != directory.id
                || !latest.member_user_ids.iter().any(|id| id == user_id)
            {
                cleaned = true;
                break;
            }
            latest.member_user_ids.retain(|id| id != user_id);
            latest.updated_at = now();
            if state
                .store
                .compare_and_put(GROUPS, &latest.id, &latest, &version)
                .await
                .map_err(internal)?
            {
                cleaned = true;
                break;
            }
        }
        if !cleaned {
            return Err(internal(format!(
                "group {} kept changing while removing deprovisioned user {user_id}",
                group.id
            )));
        }
    }
    Ok(())
}

fn membership_claim_id(org_id: &str, user_id: &str) -> String {
    format!("{org_id}:{user_id}")
}

async fn membership_for_user(
    state: &AppState,
    org_id: &str,
    user_id: &str,
) -> Result<Option<OrgMembership>, AppError> {
    state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &membership_claim_id(org_id, user_id),
        )
        .await
        .map_err(internal)
}

async fn active_org_directory_for_user(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<Option<ScimDirectory>, AppError> {
    let Some(org_id) = &directory.org_id else {
        return Ok(None);
    };
    let links: Vec<ScimUserLink> = query_all(state, USER_LINKS, "userId", user_id).await?;
    for link in links.into_iter().filter(|link| {
        link.active
            && !link.deleting
            && !link.cleanup_complete
            && link.instance_id == directory.instance_id
    }) {
        let other: Option<ScimDirectory> = state
            .store
            .get("scim_directories", &link.directory_id)
            .await
            .map_err(internal)?;
        if let Some(other) = other
            && other.status == ScimDirectoryStatus::Active
            && other.instance_id == directory.instance_id
            && other.org_id.as_deref() == Some(org_id)
            && parent_org_active(state, &other).await?
        {
            return Ok(Some(other));
        }
    }
    Ok(None)
}

async fn cleanup_owned_membership(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<(), AppError> {
    let Some(org_id) = &directory.org_id else {
        return Ok(());
    };
    let claim_id = membership_claim_id(org_id, user_id);
    let Some(claim) = state
        .store
        .get::<ScimMembershipClaim>(MEMBERSHIP_CLAIMS, &claim_id)
        .await
        .map_err(internal)?
    else {
        return Ok(());
    };
    if claim.instance_id != directory.instance_id
        || claim.org_id != *org_id
        || claim.user_id != user_id
    {
        return Err(internal("SCIM membership claim identity mismatch"));
    }
    if active_org_directory_for_user(state, directory, user_id)
        .await?
        .is_some()
    {
        return Ok(());
    }
    if let Some(membership) = membership_for_user(state, org_id, user_id).await?
        && membership.id == claim.membership_id
        && membership.instance_id == directory.instance_id
    {
        state
            .store
            .delete_membership(&membership.id)
            .await
            .map_err(internal)?;
    }
    // Reconcile an attachment that raced between the first link scan and deletion.
    if let Some(active_directory) = active_org_directory_for_user(state, directory, user_id).await?
    {
        ensure_membership(state, &active_directory, user_id).await?;
    }
    Ok(())
}

async fn ensure_membership(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<(), AppError> {
    let Some(org_id) = &directory.org_id else {
        return Ok(());
    };
    if !directory_is_active(state, directory).await?
        || !find_user_link(state, directory, user_id)
            .await?
            .is_some_and(|link| link.active)
    {
        return Err(internal("SCIM link is no longer active"));
    }
    if membership_for_user(state, org_id, user_id).await?.is_some() {
        return Ok(());
    }
    let id = membership_claim_id(org_id, user_id);
    let claim = match state
        .store
        .get::<ScimMembershipClaim>(MEMBERSHIP_CLAIMS, &id)
        .await
        .map_err(internal)?
    {
        Some(claim) => claim,
        None => {
            let proposed = ScimMembershipClaim {
                id: id.clone(),
                instance_id: directory.instance_id.clone(),
                org_id: org_id.clone(),
                user_id: user_id.to_owned(),
                membership_id: format!("mem_{}", random_token(20)),
            };
            match state.store.create(MEMBERSHIP_CLAIMS, &id, &proposed).await {
                Ok(()) => proposed,
                Err(error) => state
                    .store
                    .get::<ScimMembershipClaim>(MEMBERSHIP_CLAIMS, &id)
                    .await
                    .map_err(internal)?
                    .ok_or_else(|| internal(error))?,
            }
        }
    };
    if claim.instance_id != directory.instance_id
        || claim.org_id != *org_id
        || claim.user_id != user_id
    {
        return Err(internal("SCIM membership claim identity mismatch"));
    }
    let membership = OrgMembership {
        id: claim.membership_id.clone(),
        instance_id: directory.instance_id.clone(),
        org_id: org_id.clone(),
        user_id: user_id.to_owned(),
        member_key: format!("{org_id}:{user_id}"),
        role: "member".to_owned(),
        created_at: now(),
    };
    match state
        .store
        .create_membership_for_scim_directory(&membership, &directory.id)
        .await
    {
        Ok(_) => {}
        Err(error) => match membership_for_user(state, org_id, user_id).await? {
            Some(existing) if existing.id == membership.id => {}
            Some(_) => return Ok(()),
            None => return Err(internal(error)),
        },
    }
    if !directory_is_active(state, directory).await?
        || !find_user_link(state, directory, user_id)
            .await?
            .is_some_and(|link| link.active)
    {
        Box::pin(cleanup_owned_membership(state, directory, user_id)).await?;
        // DELETE can remove the claim before this create completes. The chosen
        // membership id still proves that this request owns that record.
        if active_org_directory_for_user(state, directory, user_id)
            .await?
            .is_none()
            && let Some(current) = membership_for_user(state, org_id, user_id).await?
            && current.id == membership.id
        {
            state
                .store
                .delete_membership(&current.id)
                .await
                .map_err(internal)?;
        }
        if let Some(other) = active_org_directory_for_user(state, directory, user_id).await? {
            Box::pin(ensure_membership(state, &other, user_id)).await?;
        }
        return Err(internal("SCIM link was removed during membership creation"));
    }
    Ok(())
}

struct ScimUserUpdate {
    email: Option<String>,
    name: Option<Option<String>>,
    external_id: Option<Option<String>>,
    active: Option<bool>,
}

async fn write_scim_user(
    state: &AppState,
    directory: &ScimDirectory,
    prior: &User,
    prior_link: &ScimUserLink,
    update: ScimUserUpdate,
) -> Result<Result<(User, ScimUserLink), Response>, AppError> {
    let ScimUserUpdate {
        email: email_change,
        name: name_change,
        external_id: external_change,
        active: active_change,
    } = update;
    if email_change
        .as_deref()
        .is_some_and(|email| !users::is_valid_email(email))
    {
        return Ok(Err(scim_error(
            StatusCode::BAD_REQUEST,
            "userName must be a valid email address",
            Some("invalidValue"),
        )));
    }
    let email_change = email_change.map(|email| users::normalize_email(&email));
    if !directory.instance_wide {
        let link = match write_link(
            state,
            directory,
            &prior.id,
            LinkUpdate {
                external_id: external_change,
                active: active_change,
                profile_user_name: email_change,
                profile_name: name_change,
                profile_seed: Some((prior.email.clone(), prior.name.clone())),
                create_if_missing: false,
                expected: LinkExpectation::Incarnation(prior_link.incarnation_id.clone()),
            },
        )
        .await
        {
            Ok(link) => link,
            Err(error) if error.status == StatusCode::CONFLICT => {
                return Ok(Err(scim_error(
                    StatusCode::CONFLICT,
                    "another user with this userName exists in this directory",
                    Some("uniqueness"),
                )));
            }
            Err(error) => return Err(error),
        };
        if !link.active {
            cleanup_membership_and_groups(state, directory, &prior.id).await?;
        }
        if link.active {
            ensure_membership(state, directory, &prior.id).await?;
        }
        let updated: User = state
            .store
            .get("users", &prior.id)
            .await
            .map_err(internal)?
            .ok_or_else(AppError::internal)?;
        return Ok(Ok((updated, link)));
    }
    // The directory bearer token authorizes updates while its link exists,
    // including reactivation of an inactive User. The link's active flag gates
    // user authentication, not directory management. An already-authorized
    // profile write may finish its User CAS after a concurrent link DELETE;
    // write_link still refuses to recreate the removed link.
    if !directory_is_active(state, directory).await?
        || find_user_link(state, directory, &prior.id).await?.is_none()
    {
        return Ok(Err(scim_error(
            StatusCode::NOT_FOUND,
            format!("no user with id '{}'", prior.id),
            None,
        )));
    }
    let mut global_profile_committed = false;
    if let Some(email) = &email_change {
        let email_key = format!("{}:{email}", directory.instance_id);
        let name_change = name_change.clone();
        match state
            .store
            .rekey_user(
                &prior.id,
                &directory.instance_id,
                Some(email_key),
                prior.username_key.clone(),
                |user| {
                    if user.email.as_deref() != Some(email) {
                        user.email = Some(email.clone());
                        user.email_verified = false;
                    }
                    if let Some(name) = &name_change {
                        user.name = name.clone();
                    }
                    user.updated_at = now();
                },
            )
            .await
        {
            Ok(true) => global_profile_committed = true,
            Ok(false) => {
                return Ok(Err(scim_error(
                    StatusCode::NOT_FOUND,
                    format!("no user with id '{}'", prior.id),
                    None,
                )));
            }
            Err(error) if error.is_conflict() => {
                return Ok(Err(scim_error(
                    StatusCode::CONFLICT,
                    "another user with this userName already exists",
                    Some("uniqueness"),
                )));
            }
            Err(error) => return Err(internal(error)),
        }
        if prior.email.as_deref() != Some(email) {
            state
                .store
                .invalidate_verification_tokens(&prior.id, "verify_email")
                .await
                .map_err(internal)?;
        }
    } else if let Some(name) = name_change {
        let mut committed = false;
        for _ in 0..8 {
            let Some((mut user, version)) = state
                .store
                .get_versioned::<User>("users", &prior.id)
                .await
                .map_err(internal)?
            else {
                break;
            };
            if user.instance_id != directory.instance_id || user.deleted {
                break;
            }
            user.name = name.clone();
            user.updated_at = now();
            if state
                .store
                .compare_and_put("users", &user.id, &user, &version)
                .await
                .map_err(internal)?
            {
                committed = true;
                break;
            }
        }
        if !committed {
            return Ok(Err(scim_error(
                StatusCode::CONFLICT,
                "user changed concurrently; retry the request",
                None,
            )));
        }
        global_profile_committed = true;
    }
    let link = match write_link(
        state,
        directory,
        &prior.id,
        LinkUpdate {
            external_id: external_change,
            active: active_change,
            profile_user_name: None,
            profile_name: None,
            profile_seed: None,
            create_if_missing: false,
            expected: LinkExpectation::Incarnation(prior_link.incarnation_id.clone()),
        },
    )
    .await
    {
        Ok(link) => link,
        Err(error) => {
            if global_profile_committed
                && (!directory_is_active(state, directory).await?
                    || find_user_link(state, directory, &prior.id).await?.is_none())
            {
                prior_link.clone()
            } else {
                return Err(error);
            }
        }
    };
    if !link.active {
        cleanup_membership_and_groups(state, directory, &prior.id).await?;
    }
    let updated: User = state
        .store
        .get("users", &prior.id)
        .await
        .map_err(internal)?
        .ok_or_else(AppError::internal)?;
    Ok(Ok((updated, link)))
}

fn update_audit_type(prior: &ScimUserLink, updated: &ScimUserLink) -> &'static str {
    if prior.active && !updated.active {
        "directory.user_deprovisioned"
    } else {
        "directory.user_updated"
    }
}

async fn replace_user(
    State(state): State<AppState>,
    Path((directory_id, user_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: UserBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some((prior, prior_link)) = (match find_scim_user(&state, &directory, &user_id).await {
        Ok(user) => user,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to read user",
                None,
            );
        }
    }) else {
        return scim_error(
            StatusCode::NOT_FOUND,
            format!("no user with id '{user_id}'"),
            None,
        );
    };
    let Some(email) = body
        .user_name
        .as_deref()
        .map(str::trim)
        .filter(|email| users::is_valid_email(email))
    else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "userName is required and must be a valid email address",
            Some("invalidValue"),
        );
    };
    let active = body.active.unwrap_or(prior_link.active);
    let name = join_name(body.name);
    let external_id = normalize_external_id(body.external_id.as_deref());
    let (updated, updated_link) = match write_scim_user(
        &state,
        &directory,
        &prior,
        &prior_link,
        ScimUserUpdate {
            email: Some(email.to_owned()),
            name: Some(name),
            external_id: Some(external_id),
            active: Some(active),
        },
    )
    .await
    {
        Ok(Ok(updated)) => updated,
        Ok(Err(response)) => return response,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to update user",
                None,
            );
        }
    };
    let mut data = Map::new();
    data.insert("user_id".into(), json!(updated.id));
    if audit(
        &state,
        &directory,
        update_audit_type(&prior_link, &updated_link),
        data,
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record user update",
            None,
        );
    }
    scim_json(user_view(&directory, &updated, &updated_link))
}

async fn patch_user(
    State(state): State<AppState>,
    Path((directory_id, user_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: PatchBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(operations) = body.operations.filter(|operations| !operations.is_empty()) else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "Operations with at least one replace operation are required",
            Some("invalidValue"),
        );
    };
    let Some((prior, prior_link)) = (match find_scim_user(&state, &directory, &user_id).await {
        Ok(user) => user,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to read user",
                None,
            );
        }
    }) else {
        return scim_error(
            StatusCode::NOT_FOUND,
            format!("no user with id '{user_id}'"),
            None,
        );
    };
    let mut email = None;
    let mut active = None;
    for operation in operations {
        if !operation
            .op
            .as_deref()
            .unwrap_or("replace")
            .trim()
            .eq_ignore_ascii_case("replace")
        {
            return scim_error(
                StatusCode::BAD_REQUEST,
                "only 'replace' operations are supported",
                Some("invalidValue"),
            );
        }
        if let Some(path) = operation.path {
            match replace_user_attribute(&path, operation.value.as_ref(), &mut email, &mut active) {
                Ok(()) => {}
                Err(response) => return response,
            }
        } else {
            let Some(Value::Object(attributes)) = operation.value else {
                return scim_error(
                    StatusCode::BAD_REQUEST,
                    "a patch operation without 'path' requires an object value of attributes to replace",
                    Some("invalidValue"),
                );
            };
            for (attribute, value) in attributes {
                match replace_user_attribute(&attribute, Some(&value), &mut email, &mut active) {
                    Ok(()) => {}
                    Err(response) => return response,
                }
            }
        }
    }
    let (updated, updated_link) = match write_scim_user(
        &state,
        &directory,
        &prior,
        &prior_link,
        ScimUserUpdate {
            email,
            name: None,
            external_id: None,
            active,
        },
    )
    .await
    {
        Ok(Ok(updated)) => updated,
        Ok(Err(response)) => return response,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to update user",
                None,
            );
        }
    };
    let mut data = Map::new();
    data.insert("user_id".into(), json!(updated.id));
    if audit(
        &state,
        &directory,
        update_audit_type(&prior_link, &updated_link),
        data,
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record user update",
            None,
        );
    }
    scim_json(user_view(&directory, &updated, &updated_link))
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn replace_user_attribute(
    path: &str,
    value: Option<&Value>,
    email: &mut Option<String>,
    active: &mut Option<bool>,
) -> Result<(), Response> {
    let Some(value) = value else {
        return Err(scim_error(
            StatusCode::BAD_REQUEST,
            "patch operation requires a value",
            Some("invalidValue"),
        ));
    };
    if path.eq_ignore_ascii_case("active") {
        let Some(value) = value.as_bool() else {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "replacing 'active' requires a boolean value",
                Some("invalidValue"),
            ));
        };
        *active = Some(value);
        return Ok(());
    }
    if path.eq_ignore_ascii_case("userName") {
        let Some(value) = value.as_str() else {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "replacing 'userName' requires a string value",
                Some("invalidValue"),
            ));
        };
        *email = Some(value.to_owned());
        return Ok(());
    }
    Err(scim_error(
        StatusCode::BAD_REQUEST,
        format!("unsupported patch path '{path}'; only 'active' and 'userName' are supported"),
        Some("invalidPath"),
    ))
}

/// Close a link for both authentication and management before any fallible
/// cross-document cleanup. A retry can still discover the tombstone.
async fn tombstone_user_link(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
    expected_incarnation: Option<&str>,
) -> Result<Option<ScimUserLink>, AppError> {
    let id = link_id(&directory.id, user_id);
    for _ in 0..8 {
        let Some((mut link, version)) = state
            .store
            .get_versioned::<ScimUserLink>(USER_LINKS, &id)
            .await
            .map_err(internal)?
        else {
            return Ok(None);
        };
        if link.directory_id != directory.id
            || link.instance_id != directory.instance_id
            || link.user_id != user_id
            || link.incarnation_id.as_deref() != expected_incarnation
        {
            return Err(internal(
                "SCIM link incarnation changed during deprovisioning",
            ));
        }
        if link.cleanup_complete {
            return Ok(None);
        }
        if link.deleting {
            return Ok(Some(link));
        }
        link.active = false;
        link.deleting = true;
        if state
            .store
            .compare_and_put(USER_LINKS, &id, &link, &version)
            .await
            .map_err(internal)?
        {
            return Ok(Some(link));
        }
    }
    Err(internal("SCIM link kept changing during deprovisioning"))
}

async fn finish_deprovisioned_link(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
    expected_incarnation: Option<&str>,
    cleanup_groups: bool,
) -> Result<(), AppError> {
    // Fail read-only preflight before taking the exclusive cleanup claim. Once
    // child writes begin, an error can be ambiguous and only quiescent repair
    // may clear the owner safely.
    if let Some(org_id) = &directory.org_id {
        let _: Option<ScimMembershipClaim> = state
            .store
            .get(MEMBERSHIP_CLAIMS, &membership_claim_id(org_id, user_id))
            .await
            .map_err(internal)?;
    }
    if cleanup_groups {
        let _: Vec<ScimGroup> = query_all(state, GROUPS, "directoryId", &directory.id).await?;
    }
    let id = link_id(&directory.id, user_id);
    let owner = random_token(40);
    let mut acquired = false;
    for _ in 0..8 {
        let Some((mut link, version)) = state
            .store
            .get_versioned::<ScimUserLink>(USER_LINKS, &id)
            .await
            .map_err(internal)?
        else {
            return Ok(());
        };
        if link.directory_id != directory.id
            || link.instance_id != directory.instance_id
            || link.user_id != user_id
        {
            return Err(internal("SCIM deprovision tombstone identity mismatch"));
        }
        if !link.deleting
            || link.cleanup_complete
            || link.incarnation_id.as_deref() != expected_incarnation
        {
            return Err(internal("SCIM deprovision tombstone incarnation changed"));
        }
        if !reserve_cleanup_owner(&mut link, &owner) {
            return Err(AppError::new(
                StatusCode::CONFLICT,
                "scim_cleanup_in_progress",
                "deprovisioning is already in progress or requires quiescent repair",
            ));
        }
        if state
            .store
            .compare_and_put(USER_LINKS, &id, &link, &version)
            .await
            .map_err(internal)?
        {
            acquired = true;
            break;
        }
    }
    if !acquired {
        return Err(internal("SCIM deprovision tombstone kept changing"));
    }
    let Some((link, version)) = state
        .store
        .get_versioned::<ScimUserLink>(USER_LINKS, &id)
        .await
        .map_err(internal)?
    else {
        return Err(internal("SCIM deprovision tombstone disappeared"));
    };
    if !link.deleting
        || link.cleanup_complete
        || link.cleanup_owner.as_deref() != Some(owner.as_str())
        || link.directory_id != directory.id
        || link.instance_id != directory.instance_id
        || link.user_id != user_id
        || link.incarnation_id.as_deref() != expected_incarnation
    {
        return Err(internal("SCIM deprovision cleanup ownership changed"));
    }
    let cleanup = if cleanup_groups {
        cleanup_membership_and_groups(state, directory, user_id).await
    } else {
        cleanup_owned_membership(state, directory, user_id).await
    };
    if let Err(error) = cleanup {
        // No automatic takeover is safe after an ambiguous child mutation.
        // An operator must quiesce this directory, confirm this owner has
        // stopped, reconcile children, then clear cleanup_owner to retry.
        tracing::error!(
            directory_id = %directory.id,
            user_id,
            cleanup_owner = %owner,
            ?error,
            "SCIM cleanup owner retained after child cleanup failure"
        );
        return Err(error);
    }
    if !directory.instance_wide
        && let Some(name) = &link.profile_user_name
        && let Err(error) = release_local_name(state, directory, name, &id, None).await
    {
        tracing::error!(
            directory_id = %directory.id,
            user_id,
            cleanup_owner = %owner,
            ?error,
            "SCIM cleanup owner retained after name release failure"
        );
        return Err(error);
    }
    // Retain this document so its version cannot reset when a future POST
    // replaces the completed link with a new incarnation.
    let mut completed = link;
    completed.active = false;
    completed.deleting = false;
    completed.cleanup_complete = true;
    completed.cleanup_owner = None;
    let saved = state
        .store
        .compare_and_put(USER_LINKS, &id, &completed, &version)
        .await
        .map_err(internal);
    match saved {
        Ok(true) => audit_user_deprovisioned(state, directory, &completed).await,
        Ok(false) => {
            tracing::error!(
                directory_id = %directory.id,
                user_id,
                cleanup_owner = %owner,
                "SCIM cleanup owner retained after terminal link version changed"
            );
            Err(internal("SCIM deprovision tombstone changed"))
        }
        Err(error) => {
            tracing::error!(
                directory_id = %directory.id,
                user_id,
                cleanup_owner = %owner,
                ?error,
                "SCIM terminal link transition outcome is ambiguous"
            );
            Err(error)
        }
    }
}

fn reserve_cleanup_owner(link: &mut ScimUserLink, owner: &str) -> bool {
    if !link.deleting || link.cleanup_complete || link.cleanup_owner.is_some() {
        return false;
    }
    link.cleanup_owner = Some(owner.to_owned());
    true
}

async fn delete_user(
    State(state): State<AppState>,
    Path((directory_id, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let link = match find_user_link_for_cleanup(&state, &directory, &user_id).await {
        Ok(link) => link,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to read directory link",
                None,
            );
        }
    };
    let Some(link) = link else {
        return scim_error(
            StatusCode::NOT_FOUND,
            format!("no user with id '{user_id}'"),
            None,
        );
    };
    if link.cleanup_complete {
        return match audit_user_deprovisioned(&state, &directory, &link).await {
            Ok(()) => StatusCode::NO_CONTENT.into_response(),
            Err(_) => scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to record user deprovisioning; retry the request",
                None,
            ),
        };
    }
    if !link.deleting {
        let user: Option<User> = match state.store.get("users", &user_id).await {
            Ok(user) => user,
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to read user",
                    None,
                );
            }
        };
        if !user.is_some_and(|user| scim_user_belongs_to_directory(&user, &directory, &link)) {
            return scim_error(
                StatusCode::NOT_FOUND,
                format!("no user with id '{user_id}'"),
                None,
            );
        }
    }
    let tombstone =
        tombstone_user_link(&state, &directory, &user_id, link.incarnation_id.as_deref()).await;
    let result = match tombstone {
        Ok(Some(tombstone)) => {
            finish_deprovisioned_link(
                &state,
                &directory,
                &user_id,
                tombstone.incarnation_id.as_deref(),
                true,
            )
            .await
        }
        Ok(None) => {
            return match audit_user_deprovisioned(&state, &directory, &link).await {
                Ok(()) => StatusCode::NO_CONTENT.into_response(),
                Err(_) => scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to record user deprovisioning; retry the request",
                    None,
                ),
            };
        }
        Err(error) => Err(error),
    };
    if let Err(error) = result {
        if error.code == "scim_cleanup_in_progress" {
            return scim_error(
                StatusCode::CONFLICT,
                "user deprovisioning is already in progress or requires repair",
                None,
            );
        }
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to complete user deprovisioning; retry the request",
            None,
        );
    }
    if audit_user_deprovisioned(&state, &directory, &link)
        .await
        .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record user deprovisioning",
            None,
        );
    }
    StatusCode::NO_CONTENT.into_response()
}

async fn list_groups(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    if query_value(&request, "filter").is_some() {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "filters are not supported on the Groups endpoint",
            Some("invalidFilter"),
        );
    }
    let groups: Vec<ScimGroup> =
        match query_all::<ScimGroup>(&state, GROUPS, "directoryId", &directory.id).await {
            Ok(groups) => groups
                .into_iter()
                .filter(|group| group.instance_id == directory.instance_id)
                .collect(),
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to list groups",
                    None,
                );
            }
        };
    let (start, count) = match paginate(&request) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let mut groups = groups;
    groups.sort_by(|left, right| left.id.cmp(&right.id));
    let resources = groups
        .iter()
        .skip(start.saturating_sub(1))
        .take(count)
        .map(|group| group_view(&directory.id, group))
        .collect();
    scim_json(list_response(
        ScimList {
            total: groups.len(),
            items: resources,
        },
        start,
    ))
}

async fn get_group(
    State(state): State<AppState>,
    Path((directory_id, group_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    match find_group(&state, &directory, &group_id).await {
        Ok(Some(group)) => scim_json(group_view(&directory.id, &group)),
        Ok(None) => scim_error(
            StatusCode::NOT_FOUND,
            format!("no group with id '{group_id}'"),
            None,
        ),
        Err(_) => scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to read group",
            None,
        ),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn parse_members(
    state: &AppState,
    directory: &ScimDirectory,
    members: Option<Vec<ScimMember>>,
) -> Result<Vec<String>, Response> {
    let mut ids = Vec::new();
    for member in members.unwrap_or_default() {
        let Some(user_id) = member
            .value
            .filter(|id| !id.trim().is_empty())
            .map(|id| id.trim().to_owned())
        else {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "members must reference existing users by their id",
                Some("invalidValue"),
            ));
        };
        let linked = find_scim_user(state, directory, &user_id)
            .await
            .map_err(|_| {
                scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to validate group member",
                    None,
                )
            })?;
        if !linked
            .as_ref()
            .is_some_and(|(user, link)| scim_user_can_be_group_member(user, directory, link))
        {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "members must reference active users by their id",
                Some("invalidValue"),
            ));
        }
        if !ids.contains(&user_id) {
            ids.push(user_id);
        }
    }
    Ok(ids)
}

async fn prune_inactive_members(
    state: &AppState,
    directory: &ScimDirectory,
    group_id: &str,
) -> Result<Option<ScimGroup>, AppError> {
    for _ in 0..8 {
        let Some((mut group, version)) = find_group_versioned(state, directory, group_id).await?
        else {
            return Ok(None);
        };
        let mut valid = Vec::new();
        for user_id in &group.member_user_ids {
            if let Some((user, link)) = find_scim_user(state, directory, user_id).await?
                && scim_user_can_be_group_member(&user, directory, &link)
            {
                valid.push(user_id.clone());
            }
        }
        if valid == group.member_user_ids {
            return Ok(Some(group));
        }
        group.member_user_ids = valid;
        group.updated_at = now();
        if state
            .store
            .compare_and_put(GROUPS, &group.id, &group, &version)
            .await
            .map_err(internal)?
        {
            return Ok(Some(group));
        }
    }
    Err(internal(
        "SCIM group changed while pruning inactive members",
    ))
}

async fn create_group(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: GroupBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(display_name) = body
        .display_name
        .filter(|name| !name.trim().is_empty())
        .map(|name| name.trim().to_owned())
    else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "displayName is required",
            Some("invalidValue"),
        );
    };
    let members = match parse_members(&state, &directory, body.members).await {
        Ok(ids) => ids,
        Err(response) => return response,
    };
    let created_at = now();
    let group = ScimGroup {
        id: format!("grp_{}", random_token(20)),
        instance_id: directory.instance_id.clone(),
        directory_id: directory.id.clone(),
        display_name,
        member_user_ids: members,
        created_at,
        updated_at: created_at,
    };
    if let Err(error) = state.store.create(GROUPS, &group.id, &group).await {
        tracing::error!(?error, "failed to create SCIM group");
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to create group",
            None,
        );
    }
    if !matches!(directory_is_active(&state, &directory).await, Ok(true)) {
        if let Err(error) = state.store.delete_ignoring_missing(GROUPS, &group.id).await {
            tracing::error!(
                ?error,
                directory_id = %directory.id,
                group_id = %group.id,
                "failed to remove SCIM group created while directory was inactive"
            );
            return scim_error(
                StatusCode::SERVICE_UNAVAILABLE,
                "directory deletion is still in progress; retry directory deletion to finish group cleanup",
                None,
            );
        }
        return scim_error(
            StatusCode::UNAUTHORIZED,
            "directory is no longer active",
            None,
        );
    }
    let group = match prune_inactive_members(&state, &directory, &group.id).await {
        Ok(Some(group)) => group,
        _ => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to validate group members",
                None,
            );
        }
    };
    if audit(
        &state,
        &directory,
        "directory.group_provisioned",
        Map::from_iter([(String::from("group_id"), json!(group.id))]),
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record group provisioning",
            None,
        );
    }
    scim_created(
        group_view(&directory.id, &group),
        &format!("/scim/{}/Groups/{}", directory.id, group.id),
    )
}

async fn replace_group(
    State(state): State<AppState>,
    Path((directory_id, group_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: GroupBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(display_name) = body
        .display_name
        .filter(|name| !name.trim().is_empty())
        .map(|name| name.trim().to_owned())
    else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "displayName is required",
            Some("invalidValue"),
        );
    };
    let members = match parse_members(&state, &directory, body.members).await {
        Ok(ids) => ids,
        Err(response) => return response,
    };
    let mut updated = None;
    for _ in 0..8 {
        let (mut group, version) = match find_group_versioned(&state, &directory, &group_id).await {
            Ok(Some(group)) => group,
            Ok(None) => {
                return scim_error(
                    StatusCode::NOT_FOUND,
                    format!("no group with id '{group_id}'"),
                    None,
                );
            }
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to read group",
                    None,
                );
            }
        };
        group.display_name = display_name.clone();
        group.member_user_ids.clone_from(&members);
        group.updated_at = now();
        if !matches!(directory_is_active(&state, &directory).await, Ok(true)) {
            return scim_error(
                StatusCode::UNAUTHORIZED,
                "directory is no longer active",
                None,
            );
        }
        match state
            .store
            .compare_and_put(GROUPS, &group.id, &group, &version)
            .await
        {
            Ok(true) => {
                updated = Some(group);
                break;
            }
            Ok(false) => continue,
            Err(error) => {
                tracing::error!(?error, "failed to update SCIM group");
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to update group",
                    None,
                );
            }
        }
    }
    let Some(group) = updated else {
        return scim_error(
            StatusCode::CONFLICT,
            "group changed concurrently; retry the request",
            None,
        );
    };
    let group = match prune_inactive_members(&state, &directory, &group.id).await {
        Ok(Some(group)) => group,
        _ => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to validate group members",
                None,
            );
        }
    };
    if audit(
        &state,
        &directory,
        "directory.group_updated",
        Map::from_iter([(String::from("group_id"), json!(group.id))]),
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record group update",
            None,
        );
    }
    scim_json(group_view(&directory.id, &group))
}

async fn patch_group(
    State(state): State<AppState>,
    Path((directory_id, group_id)): Path<(String, String)>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: PatchBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(operations) = body.operations.filter(|operations| !operations.is_empty()) else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "Operations with at least one replace operation are required",
            Some("invalidValue"),
        );
    };
    let mut updated = None;
    for _ in 0..8 {
        let (mut group, version) = match find_group_versioned(&state, &directory, &group_id).await {
            Ok(Some(group)) => group,
            Ok(None) => {
                return scim_error(
                    StatusCode::NOT_FOUND,
                    format!("no group with id '{group_id}'"),
                    None,
                );
            }
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to read group",
                    None,
                );
            }
        };
        for operation in &operations {
            if !operation
                .op
                .as_deref()
                .unwrap_or("replace")
                .trim()
                .eq_ignore_ascii_case("replace")
            {
                return scim_error(
                    StatusCode::BAD_REQUEST,
                    "only 'replace' operations are supported",
                    Some("invalidValue"),
                );
            }
            if let Some(path) = operation.path.as_deref() {
                if let Err(response) = replace_group_attribute(
                    &state,
                    &directory,
                    &mut group,
                    path,
                    operation.value.as_ref(),
                )
                .await
                {
                    return response;
                }
            } else {
                let Some(Value::Object(attributes)) = operation.value.as_ref() else {
                    return scim_error(
                        StatusCode::BAD_REQUEST,
                        "a patch operation without 'path' requires an object value of attributes to replace",
                        Some("invalidValue"),
                    );
                };
                for (attribute, value) in attributes {
                    if let Err(response) = replace_group_attribute(
                        &state,
                        &directory,
                        &mut group,
                        attribute,
                        Some(value),
                    )
                    .await
                    {
                        return response;
                    }
                }
            }
        }
        group.updated_at = now();
        if !matches!(directory_is_active(&state, &directory).await, Ok(true)) {
            return scim_error(
                StatusCode::UNAUTHORIZED,
                "directory is no longer active",
                None,
            );
        }
        match state
            .store
            .compare_and_put(GROUPS, &group.id, &group, &version)
            .await
        {
            Ok(true) => {
                updated = Some(group);
                break;
            }
            Ok(false) => continue,
            Err(error) => {
                tracing::error!(?error, "failed to patch SCIM group");
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to update group",
                    None,
                );
            }
        }
    }
    let Some(group) = updated else {
        return scim_error(
            StatusCode::CONFLICT,
            "group changed concurrently; retry the request",
            None,
        );
    };
    let group = match prune_inactive_members(&state, &directory, &group.id).await {
        Ok(Some(group)) => group,
        _ => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to validate group members",
                None,
            );
        }
    };
    if audit(
        &state,
        &directory,
        "directory.group_updated",
        Map::from_iter([(String::from("group_id"), json!(group.id))]),
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record group update",
            None,
        );
    }
    scim_json(group_view(&directory.id, &group))
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn replace_group_attribute(
    state: &AppState,
    directory: &ScimDirectory,
    group: &mut ScimGroup,
    path: &str,
    value: Option<&Value>,
) -> Result<(), Response> {
    let Some(value) = value else {
        return Err(scim_error(
            StatusCode::BAD_REQUEST,
            "patch operation requires a value",
            Some("invalidValue"),
        ));
    };
    if path.eq_ignore_ascii_case("displayName") {
        let Some(display_name) = value.as_str().filter(|name| !name.trim().is_empty()) else {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "replacing 'displayName' requires a non-empty string value",
                Some("invalidValue"),
            ));
        };
        group.display_name = display_name.trim().to_owned();
        return Ok(());
    }
    if path.eq_ignore_ascii_case("members") {
        let Some(members) = value.as_array() else {
            return Err(scim_error(
                StatusCode::BAD_REQUEST,
                "replacing 'members' requires an array value",
                Some("invalidValue"),
            ));
        };
        let parsed = members
            .iter()
            .map(|member| serde_json::from_value::<ScimMember>(member.clone()))
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| {
                scim_error(
                    StatusCode::BAD_REQUEST,
                    "members must reference active users by their id",
                    Some("invalidValue"),
                )
            })?;
        group.member_user_ids = parse_members(state, directory, Some(parsed)).await?;
        return Ok(());
    }
    Err(scim_error(
        StatusCode::BAD_REQUEST,
        format!("unsupported patch path '{path}'; only 'displayName' and 'members' are supported"),
        Some("invalidPath"),
    ))
}

async fn delete_group(
    State(state): State<AppState>,
    Path((directory_id, group_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let mut deleted = None;
    for _ in 0..8 {
        let (group, version) = match find_group_versioned(&state, &directory, &group_id).await {
            Ok(Some(group)) => group,
            Ok(None) => {
                return scim_error(
                    StatusCode::NOT_FOUND,
                    format!("no group with id '{group_id}'"),
                    None,
                );
            }
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to read group",
                    None,
                );
            }
        };
        match state
            .store
            .compare_and_delete(GROUPS, &group.id, &version)
            .await
        {
            Ok(true) => {
                deleted = Some(group);
                break;
            }
            Ok(false) => continue,
            Err(error) => {
                tracing::error!(?error, "failed to delete SCIM group");
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to delete group",
                    None,
                );
            }
        }
    }
    let Some(group) = deleted else {
        return scim_error(
            StatusCode::CONFLICT,
            "group changed concurrently; retry the request",
            None,
        );
    };
    if audit(
        &state,
        &directory,
        "directory.group_deprovisioned",
        Map::from_iter([(String::from("group_id"), json!(group.id))]),
    )
    .await
    .is_err()
    {
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to record group deletion",
            None,
        );
    }
    StatusCode::NO_CONTENT.into_response()
}

async fn service_provider_config(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    scim_json(json!({
        "schemas":["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
        "patch":{"supported":true},
        "bulk":{"supported":false,"maxOperations":0,"maxPayloadSize":0},
        "filter":{"supported":true,"maxResults":MAX_RESULTS},
        "changePassword":{"supported":false},
        "sort":{"supported":false},
        "etag":{"supported":false},
        "authenticationSchemes":[{"name":"OAuth Bearer Token","description":"The directory's SCIM bearer token","type":"oauthbearertoken","primary":true}],
        "meta":{"resourceType":"ServiceProviderConfig","location":format!("/scim/{}/ServiceProviderConfig", directory.id)},
    }))
}

async fn resource_types(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    scim_json(json!({
        "schemas":[LIST_SCHEMA],
        "totalResults":2,
        "startIndex":1,
        "itemsPerPage":2,
        "Resources":[
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:ResourceType"],"id":"User","name":"User","endpoint":format!("/scim/{}/Users", directory.id),"schema":USER_SCHEMA},
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:ResourceType"],"id":"Group","name":"Group","endpoint":format!("/scim/{}/Groups", directory.id),"schema":GROUP_SCHEMA}
        ]
    }))
}

async fn schemas(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
) -> Response {
    if let Err(response) = directory_for_token(&state, &directory_id, &headers).await {
        return response;
    }
    let schema_schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
    let attributes =
        |items: &[(&str, &str, bool)]| -> Vec<Value> {
            items.iter().map(|(name, kind, read_only)| json!({
            "name":name,"type":kind,"multiValued":false,"required":false,
            "mutability":if *read_only {"readOnly"} else {"readWrite"},"returned":"default"
        })).collect()
        };
    scim_json(json!({
        "schemas":[LIST_SCHEMA],
        "totalResults":2,
        "startIndex":1,
        "itemsPerPage":2,
        "Resources":[
            {"schemas":[schema_schema],"id":USER_SCHEMA,"name":"User","attributes":attributes(&[("id","string",true),("userName","string",false),("active","boolean",false),("externalId","string",false),("name","complex",false),("meta","complex",true)])},
            {"schemas":[schema_schema],"id":GROUP_SCHEMA,"name":"Group","attributes":attributes(&[("id","string",true),("displayName","string",false),("members","complex",false),("meta","complex",true)])}
        ]
    }))
}

async fn find_scim_user(
    state: &AppState,
    directory: &ScimDirectory,
    user_id: &str,
) -> Result<Option<(User, ScimUserLink)>, AppError> {
    let Some(link) = find_user_link(state, directory, user_id).await? else {
        return Ok(None);
    };
    let user: Option<User> = state.store.get("users", user_id).await.map_err(internal)?;
    Ok(user
        .filter(|user| scim_user_belongs_to_directory(user, directory, &link))
        .map(|user| (user, link)))
}

fn scim_user_belongs_to_directory(
    user: &User,
    directory: &ScimDirectory,
    link: &ScimUserLink,
) -> bool {
    user.instance_id == directory.instance_id
        && !user.deleted
        && !link.deleting
        && !link.cleanup_complete
        && link.user_id == user.id
        && link.directory_id == directory.id
        && link.instance_id == directory.instance_id
}

async fn find_group(
    state: &AppState,
    directory: &ScimDirectory,
    group_id: &str,
) -> Result<Option<ScimGroup>, AppError> {
    let group: Option<ScimGroup> = state.store.get(GROUPS, group_id).await.map_err(internal)?;
    Ok(group.filter(|group| scim_group_belongs_to_directory(group, directory)))
}

async fn find_group_versioned(
    state: &AppState,
    directory: &ScimDirectory,
    group_id: &str,
) -> Result<Option<(ScimGroup, String)>, AppError> {
    let group = state
        .store
        .get_versioned::<ScimGroup>(GROUPS, group_id)
        .await
        .map_err(internal)?;
    Ok(group.filter(|(group, _)| scim_group_belongs_to_directory(group, directory)))
}

fn scim_group_belongs_to_directory(group: &ScimGroup, directory: &ScimDirectory) -> bool {
    group.instance_id == directory.instance_id && group.directory_id == directory.id
}

fn scim_user_can_be_group_member(
    user: &User,
    directory: &ScimDirectory,
    link: &ScimUserLink,
) -> bool {
    scim_user_belongs_to_directory(user, directory, link) && link.active && !user.banned
}

fn user_list(
    directory: &ScimDirectory,
    request: &Request,
    users: Vec<(User, ScimUserLink)>,
) -> Result<Response, AppError> {
    let users: Vec<(User, ScimUserLink)> = users
        .into_iter()
        .filter(|(user, link)| scim_user_belongs_to_directory(user, directory, link))
        .collect();
    let filter = query_value(request, "filter");
    let mut matched = match filter.as_deref() {
        None => users
            .into_iter()
            .filter(|(user, _)| !user.deleted)
            .collect::<Vec<_>>(),
        Some(filter) => {
            let Some((attribute, value)) = parse_filter(filter) else {
                return Ok(scim_error(
                    StatusCode::BAD_REQUEST,
                    "only simple filters of the form 'userName eq \"value\"' or 'externalId eq \"value\"' are supported",
                    Some("invalidFilter"),
                ));
            };
            if attribute.eq_ignore_ascii_case("username") {
                let normalized = users::normalize_email(&value);
                users
                    .into_iter()
                    .filter(|(user, link)| {
                        !user.deleted
                            && projected_user_name(directory, user, link)
                                .map(users::normalize_email)
                                .as_deref()
                                == Some(normalized.as_str())
                    })
                    .collect()
            } else {
                users
                    .into_iter()
                    .filter(|(user, link)| {
                        !user.deleted && link.external_id.as_deref() == Some(value.as_str())
                    })
                    .collect()
            }
        }
    };
    matched.sort_by(|left, right| left.0.id.cmp(&right.0.id));
    let (start, count) = match paginate(request) {
        Ok(value) => value,
        Err(response) => return Ok(response),
    };
    let resources = matched
        .iter()
        .skip(start.saturating_sub(1))
        .take(count)
        .map(|(user, link)| user_view(directory, user, link))
        .collect();
    Ok(scim_json(list_response(
        ScimList {
            total: matched.len(),
            items: resources,
        },
        start,
    )))
}

fn query_value(request: &Request, name: &str) -> Option<String> {
    request.uri().query().and_then(|query| {
        url::form_urlencoded::parse(query.as_bytes())
            .find(|(key, _)| key == name)
            .map(|(_, value)| value.into_owned())
    })
}

fn parse_filter(filter: &str) -> Option<(String, String)> {
    let filter = filter.trim();
    let (attribute, rest) = filter.split_once(char::is_whitespace)?;
    if !attribute.eq_ignore_ascii_case("userName") && !attribute.eq_ignore_ascii_case("externalId")
    {
        return None;
    }
    let rest = rest.trim_start();
    let (operator, value) = rest.split_once(char::is_whitespace)?;
    if !operator.eq_ignore_ascii_case("eq") {
        return None;
    }
    let value = value.trim();
    let value = value.strip_prefix('"')?.strip_suffix('"')?;
    if value.contains('"') {
        return None;
    }
    Some((attribute.to_owned(), value.to_owned()))
}

async fn list_users(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let links: Vec<ScimUserLink> =
        match query_all(&state, USER_LINKS, "directoryId", &directory.id).await {
            Ok(links) => links,
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to list users",
                    None,
                );
            }
        };
    let mut users = Vec::new();
    for link in links.into_iter().filter(|link| {
        link.instance_id == directory.instance_id && !link.deleting && !link.cleanup_complete
    }) {
        match state.store.get::<User>("users", &link.user_id).await {
            Ok(Some(user)) => users.push((user, link)),
            Ok(None) => {}
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to list users",
                    None,
                );
            }
        }
    }
    match user_list(&directory, &request, users) {
        Ok(response) => response,
        Err(_) => scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to list users",
            None,
        ),
    }
}

async fn get_user(
    State(state): State<AppState>,
    Path((directory_id, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    match find_scim_user(&state, &directory, &user_id).await {
        Ok(Some((user, link))) => scim_json(user_view(&directory, &user, &link)),
        Ok(None) => scim_error(
            StatusCode::NOT_FOUND,
            format!("no user with id '{user_id}'"),
            None,
        ),
        Err(_) => scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to read user",
            None,
        ),
    }
}

async fn compensate_new_scim_user(
    state: &AppState,
    directory: &ScimDirectory,
    created: &User,
) -> Result<(), AppError> {
    // Only this directory can attach a pending identity. Read the link afresh so
    // an ambiguous create result can be rolled back too.
    if let Some(link) = find_user_link(state, directory, &created.id).await? {
        let local_name_version = if !directory.instance_wide {
            match &link.profile_user_name {
                Some(name) => owned_local_name_version(state, directory, name, &link.id).await?,
                None => None,
            }
        } else {
            None
        };
        if let Some((current, version)) = state
            .store
            .get_versioned::<ScimUserLink>(USER_LINKS, &link.id)
            .await
            .map_err(internal)?
            && serde_json::to_value(&current).map_err(internal)?
                == serde_json::to_value(&link).map_err(internal)?
            && state
                .store
                .compare_and_delete(USER_LINKS, &link.id, &version)
                .await
                .map_err(internal)?
            && !directory.instance_wide
            && let Some(name) = &link.profile_user_name
            && let Some(claim_version) = local_name_version.as_deref()
        {
            release_local_name(state, directory, name, &link.id, Some(claim_version)).await?;
        }
    }
    cleanup_owned_membership(state, directory, &created.id).await?;
    // The store CAS checks the persisted provisional owner and deleted flag.
    // Reference scans cannot make deletion safe: another directory could attach
    // between the scan and User CAS without changing the User version.
    let _ = state
        .store
        .compensate_provisional_scim_user(&created.id, &directory.id)
        .await
        .map_err(internal)?;
    Ok(())
}

fn existing_user_attach_trusted(user: &User) -> bool {
    user.identity_established || user.email_verified
}

async fn create_user(
    State(state): State<AppState>,
    Path(directory_id): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    let directory = match directory_for_token(&state, &directory_id, &headers).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: UserBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    let Some(email) = body
        .user_name
        .as_deref()
        .map(str::trim)
        .filter(|email| users::is_valid_email(email))
    else {
        return scim_error(
            StatusCode::BAD_REQUEST,
            "userName is required and must be a valid email address",
            Some("invalidValue"),
        );
    };
    let name = join_name(body_name_clone(&body));
    if !matches!(directory_is_active(&state, &directory).await, Ok(true)) {
        return scim_error(
            StatusCode::UNAUTHORIZED,
            "directory is no longer active",
            None,
        );
    }
    let normalized = users::normalize_email(email);
    let existing = match state
        .store
        .find_user_by_email(&directory.instance_id, &normalized)
        .await
    {
        Ok(user) => user,
        Err(_) => {
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to find user",
                None,
            );
        }
    };
    let (mut created, created_here) = if let Some(mut existing) = existing {
        if existing.deleted || existing.banned {
            return scim_error(
                StatusCode::CONFLICT,
                "an unavailable identity has this userName",
                Some("uniqueness"),
            );
        }
        if !existing_user_attach_trusted(&existing) {
            return scim_error(
                StatusCode::CONFLICT,
                "this user must verify their email before directory linking",
                Some("uniqueness"),
            );
        }
        if !existing.identity_established {
            // A verified legacy identity is trusted, but persist that fact
            // before SCIM attaches or rekeys it. An unverified public signup
            // must never gain this marker merely by matching its email.
            match state
                .store
                .try_update_user(&existing.id, &directory.instance_id, |user| {
                    if !user.email_verified
                        || user.email.as_deref().map(users::normalize_email).as_deref()
                            != Some(normalized.as_str())
                    {
                        return false;
                    }
                    user.identity_established = true;
                    true
                })
                .await
            {
                Ok(true) => existing.identity_established = true,
                Ok(false) => {
                    return scim_error(
                        StatusCode::CONFLICT,
                        "this user changed while directory linking; retry the request",
                        None,
                    );
                }
                Err(_) => {
                    return scim_error(
                        StatusCode::INTERNAL_SERVER_ERROR,
                        "unable to establish verified user identity",
                        None,
                    );
                }
            }
        }
        if let Some(org_id) = &directory.org_id {
            let membership: Option<OrgMembership> = match state
                .store
                .query_one(
                    "memberships",
                    "memberKey",
                    &format!("{org_id}:{}", existing.id),
                )
                .await
            {
                Ok(membership) => membership,
                Err(_) => {
                    return scim_error(
                        StatusCode::INTERNAL_SERVER_ERROR,
                        "unable to validate organization membership",
                        None,
                    );
                }
            };
            let associated = if let Some(membership) = membership.filter(|membership| {
                membership.instance_id == directory.instance_id
                    && membership.org_id == *org_id
                    && membership.user_id == existing.id
            }) {
                match state.store.org_membership_auth_eligible(&membership).await {
                    Ok(eligible) => eligible,
                    Err(_) => {
                        return scim_error(
                            StatusCode::INTERNAL_SERVER_ERROR,
                            "unable to validate organization membership",
                            None,
                        );
                    }
                }
            } else {
                false
            };
            // An inactive or completed link is durable proof that this exact
            // directory previously managed the user. Deprovisioning removes
            // its physical membership, so re-provisioning can recreate it.
            let previously_managed = if associated {
                false
            } else {
                match find_user_link_for_cleanup(&state, &directory, &existing.id).await {
                    Ok(Some(link)) => link.cleanup_complete || (!link.deleting && !link.active),
                    Ok(None) => false,
                    Err(_) => {
                        return scim_error(
                            StatusCode::INTERNAL_SERVER_ERROR,
                            "unable to verify prior directory ownership",
                            None,
                        );
                    }
                }
            };
            if !associated && !previously_managed {
                return scim_error(
                    StatusCode::CONFLICT,
                    "this user is not already associated with the organization",
                    Some("uniqueness"),
                );
            }
        }
        (existing, false)
    } else {
        match users::create_provisional_scim_user(
            &state,
            &directory.instance_id,
            &directory.id,
            email,
            name.as_deref(),
        )
        .await
        {
            Ok(user) => (user, true),
            Err(error) if error.status == StatusCode::CONFLICT => {
                return scim_error(
                    StatusCode::CONFLICT,
                    "another user with this userName already exists; retry to link it",
                    Some("uniqueness"),
                );
            }
            Err(error) if error.code == "quota_exceeded" => {
                return scim_error(
                    StatusCode::FORBIDDEN,
                    "the instance has reached its plan's user quota",
                    None,
                );
            }
            Err(_) => {
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to provision user",
                    None,
                );
            }
        }
    };
    let existing_link = match find_user_link_for_cleanup(&state, &directory, &created.id).await {
        Ok(link) => link,
        Err(_) => {
            if created_here
                && let Err(error) = compensate_new_scim_user(&state, &directory, &created).await
            {
                tracing::error!(?error, "failed to compensate unlinked SCIM-created user");
            }
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to read directory link",
                None,
            );
        }
    };
    if existing_link
        .as_ref()
        .is_some_and(|link| link.deleting || (link.active && !link.cleanup_complete))
    {
        return scim_error(
            StatusCode::CONFLICT,
            "user already exists in this directory",
            Some("uniqueness"),
        );
    }
    if directory.instance_wide && !created_here {
        // Attach authority before exposing the link. Until the link is active,
        // authentication fails closed for this managed identity. A failed link
        // write leaves the association for a safe retry or directory deletion.
        match state
            .store
            .attach_instance_wide_scim_directory(&created.id, &directory.id)
            .await
        {
            Ok(true) => {}
            Ok(false) => {
                return scim_error(
                    StatusCode::CONFLICT,
                    "directory is no longer eligible to manage this user",
                    None,
                );
            }
            Err(error) => {
                tracing::error!(?error, "failed to attach instance-wide SCIM authority");
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to attach directory authority to user",
                    None,
                );
            }
        }
    }
    let external_id = normalize_external_id(body.external_id.as_deref());
    let active = body.active.unwrap_or(true);
    let link = match write_link(
        &state,
        &directory,
        &created.id,
        LinkUpdate {
            external_id: Some(external_id.clone()),
            active: Some(active),
            profile_user_name: (!directory.instance_wide).then(|| normalized.clone()),
            profile_name: (!directory.instance_wide).then(|| name.clone()),
            profile_seed: None,
            create_if_missing: true,
            expected: existing_link.map_or(LinkExpectation::Absent, |link| {
                if link.cleanup_complete {
                    LinkExpectation::Completed(link.incarnation_id)
                } else {
                    LinkExpectation::Incarnation(link.incarnation_id)
                }
            }),
        },
    )
    .await
    {
        Ok(link) => link,
        Err(error) => {
            if created_here
                && let Err(compensation) =
                    compensate_new_scim_user(&state, &directory, &created).await
            {
                tracing::error!(
                    ?compensation,
                    "failed to compensate SCIM-created user after link error"
                );
            }
            if error.status == StatusCode::CONFLICT {
                return scim_error(
                    StatusCode::CONFLICT,
                    "another user with this userName exists in this directory",
                    Some("uniqueness"),
                );
            }
            return scim_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "unable to link user to directory",
                None,
            );
        }
    };
    if active
        && ensure_membership(&state, &directory, &created.id)
            .await
            .is_err()
    {
        if created_here
            && let Err(error) = compensate_new_scim_user(&state, &directory, &created).await
        {
            tracing::error!(
                ?error,
                "failed to compensate SCIM-created user after membership error"
            );
        }
        return scim_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "unable to add user to organization",
            None,
        );
    }
    if created_here {
        // This CAS is the creation commit point. Before it, the User is
        // deleted/provisional and only this directory may attach membership.
        // A concurrent directory tombstone may win the guard; a tombstone
        // after the guarded CAS is an overlapping, already committed create.
        match state
            .store
            .activate_scim_user(&created.id, &directory.id)
            .await
        {
            Ok(
                auth_store::ScimActivationOutcome::Activated
                | auth_store::ScimActivationOutcome::AlreadyActiveOwned,
            ) => {
                created.deleted = false;
            }
            Ok(auth_store::ScimActivationOutcome::NoLongerEligible) => {
                if let Err(error) = compensate_new_scim_user(&state, &directory, &created).await {
                    tracing::error!(?error, "failed to compensate ineligible SCIM-created user");
                }
                return scim_error(
                    StatusCode::CONFLICT,
                    "directory or user link is no longer active",
                    None,
                );
            }
            Err(error) => {
                // A transport error can hide a committed CAS. Never delete the
                // User or its link based on an ambiguous activation outcome.
                tracing::error!(?error, "SCIM user activation outcome is unknown");
                return scim_error(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "unable to confirm user provisioning; retry the request",
                    None,
                );
            }
        }
    }
    let mut data = Map::from_iter([(String::from("user_id"), json!(created.id))]);
    if let Some(external_id) = external_id {
        data.insert("external_id".into(), json!(external_id));
    }
    if let Err(error) = audit(&state, &directory, "directory.user_provisioned", data).await {
        // Provisioning is already committed. Reporting failure here would
        // encourage a retry that cannot roll back the activated User.
        tracing::error!(?error, "failed to record committed SCIM provisioning");
    }
    scim_created(
        user_view(&directory, &created, &link),
        &format!("/scim/{}/Users/{}", directory.id, created.id),
    )
}

fn body_name_clone(body: &UserBody) -> Option<ScimName> {
    body.name.as_ref().map(|name| ScimName {
        given_name: name.given_name.clone(),
        family_name: name.family_name.clone(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;

    fn test_directory(instance_id: &str, id: &str) -> ScimDirectory {
        ScimDirectory {
            id: id.to_owned(),
            instance_id: instance_id.to_owned(),
            org_id: Some("org-a".to_owned()),
            instance_wide: false,
            status: ScimDirectoryStatus::Active,
            token_key: "hash".to_owned(),
            created_at: 1,
        }
    }

    fn user(instance_id: &str, id: &str) -> User {
        User {
            id: id.to_owned(),
            instance_id: instance_id.to_owned(),
            email: Some("person@example.test".to_owned()),
            email_verified: true,
            phone: None,
            username: None,
            password_hash: None,
            name: Some("Ada Lovelace".to_owned()),
            image_url: None,
            banned: false,
            ban_reason: None,
            deleted: false,
            auth_epoch: 0,
            identity_established: false,
            public_metadata: Map::new(),
            private_metadata: Map::new(),
            quota_seat: None,
            created_at: 1,
            updated_at: 1,
            last_sign_in_at: None,
            email_key: None,
            username_key: None,
            scim_provisioning: None,
            scim_instance_wide_directory_ids: Vec::new(),
        }
    }

    fn link(instance_id: &str, directory_id: &str, user_id: &str) -> ScimUserLink {
        ScimUserLink {
            id: link_id(directory_id, user_id),
            instance_id: instance_id.to_owned(),
            directory_id: directory_id.to_owned(),
            user_id: user_id.to_owned(),
            external_id: Some("external-1".to_owned()),
            active: true,
            deleting: false,
            cleanup_owner: None,
            cleanup_complete: false,
            incarnation_id: Some("incarnation-1".to_owned()),
            profile_user_name: None,
            profile_name: None,
            profile_initialized: false,
            created_at: 1,
        }
    }

    #[test]
    fn scim_existing_user_attach_requires_prior_identity_proof() {
        let mut signup = user("instance-a", "user-a");
        signup.email_verified = false;
        signup.identity_established = false;
        signup.password_hash = Some("attacker-password".to_owned());
        assert!(!existing_user_attach_trusted(&signup));

        signup.email_verified = true;
        assert!(existing_user_attach_trusted(&signup));
        signup.email_verified = false;
        signup.identity_established = true;
        assert!(existing_user_attach_trusted(&signup));
    }

    #[test]
    fn cleanup_owner_is_exclusive_until_terminal_transition() {
        let mut link = link("instance-a", "dir-a", "user-a");
        link.active = false;
        link.deleting = true;
        assert!(reserve_cleanup_owner(&mut link, "request-one"));
        assert!(!reserve_cleanup_owner(&mut link, "request-two"));
        assert_eq!(link.cleanup_owner.as_deref(), Some("request-one"));

        link.cleanup_complete = true;
        link.deleting = false;
        link.cleanup_owner = None;
        assert!(!reserve_cleanup_owner(&mut link, "stale-request"));
    }

    #[test]
    fn completed_link_reinitialization_requires_captured_incarnation() {
        let mut link = link("instance-a", "dir-a", "user-a");
        link.active = false;
        link.cleanup_complete = true;
        let old = link.incarnation_id.clone();
        assert!(link_matches_expectation(
            &link,
            &LinkExpectation::Completed(old.clone())
        ));
        assert!(!link_matches_expectation(&link, &LinkExpectation::Absent));
        assert!(!link_matches_expectation(
            &link,
            &LinkExpectation::Incarnation(old.clone())
        ));

        link.incarnation_id = Some("incarnation-2".to_owned());
        link.cleanup_complete = false;
        link.active = true;
        assert!(!link_matches_expectation(
            &link,
            &LinkExpectation::Completed(old.clone())
        ));
        assert!(!link_matches_expectation(
            &link,
            &LinkExpectation::Incarnation(old)
        ));
        assert!(link_matches_expectation(
            &link,
            &LinkExpectation::Incarnation(link.incarnation_id.clone())
        ));
    }

    #[test]
    fn delayed_name_release_cannot_take_a_reacquired_claim() {
        let mut claim = json!({
            "ownerType": SCIM_USER_NAME_CLAIM,
            "ownerId": "link-a",
            "released": false,
        });
        assert!(!local_name_claim_available(&claim, "link-b"));
        assert!(local_name_claim_release_matches(
            &claim,
            "link-a",
            Some("v1"),
            "v1"
        ));
        claim["released"] = json!(true);
        assert!(local_name_claim_available(&claim, "link-b"));

        claim["ownerId"] = json!("link-b");
        claim["released"] = json!(false);
        assert!(!local_name_claim_release_matches(
            &claim,
            "link-a",
            Some("v1"),
            "v2"
        ));
        claim["ownerId"] = json!("link-a");
        assert!(!local_name_claim_release_matches(
            &claim,
            "link-a",
            Some("v1"),
            "v3"
        ));
    }

    #[test]
    fn scim_filters_accept_only_quoted_eq_user_name_and_external_id() {
        assert_eq!(
            parse_filter("userName eq \"ada@example.test\""),
            Some(("userName".into(), "ada@example.test".into()))
        );
        assert_eq!(
            parse_filter("externalId EQ \"workday-1\""),
            Some(("externalId".into(), "workday-1".into()))
        );
        assert_eq!(parse_filter("userName co \"ada\""), None);
        assert_eq!(parse_filter("displayName eq \"Ada\""), None);
        assert_eq!(parse_filter("userName eq ada"), None);
        assert_eq!(parse_filter("userName eq \"ada\" extra"), None);
    }

    #[test]
    fn scim_pagination_clamps_to_rfc_start_and_server_page_limits() {
        let request = Request::builder()
            .uri("/scim/dir/Users?startIndex=0&count=-1")
            .body(Body::empty())
            .unwrap();
        assert_eq!(paginate(&request).unwrap(), (1, 0));
        let request = Request::builder()
            .uri("/scim/dir/Users?startIndex=3&count=10000")
            .body(Body::empty())
            .unwrap();
        assert_eq!(paginate(&request).unwrap(), (3, MAX_RESULTS));
        let request = Request::builder()
            .uri("/scim/dir/Users?count=bad")
            .body(Body::empty())
            .unwrap();
        assert!(paginate(&request).is_err());
    }

    #[test]
    fn directory_and_user_checks_reject_cross_tenant_data_and_inactive_group_members() {
        let directory = test_directory("instance-a", "dir-a");
        let same_instance = user("instance-a", "user-a");
        let foreign_instance = user("instance-b", "user-b");
        let linked = link("instance-a", "dir-a", "user-a");
        assert!(scim_user_belongs_to_directory(
            &same_instance,
            &directory,
            &linked
        ));
        assert!(!scim_user_belongs_to_directory(
            &foreign_instance,
            &directory,
            &linked
        ));
        assert!(scim_user_can_be_group_member(
            &same_instance,
            &directory,
            &linked
        ));
        let mut banned = same_instance.clone();
        banned.banned = true;
        assert!(!scim_user_can_be_group_member(&banned, &directory, &linked));
        let mut deleted = same_instance.clone();
        deleted.deleted = true;
        assert!(!scim_user_belongs_to_directory(
            &deleted, &directory, &linked
        ));
        let other_link = link("instance-a", "dir-b", "user-a");
        assert!(!scim_user_belongs_to_directory(
            &same_instance,
            &directory,
            &other_link
        ));

        let group = ScimGroup {
            id: "grp-a".into(),
            instance_id: "instance-a".into(),
            directory_id: "dir-a".into(),
            display_name: "Engineering".into(),
            member_user_ids: vec![],
            created_at: 1,
            updated_at: 1,
        };
        assert!(scim_group_belongs_to_directory(&group, &directory));
        let other_directory = test_directory("instance-a", "dir-b");
        assert!(!scim_group_belongs_to_directory(&group, &other_directory));
    }

    #[test]
    fn scim_views_keep_external_identity_and_auth_errors_advertise_bearer() {
        let user = user("instance-a", "user-a");
        let view = user_view(
            &test_directory("instance-a", "dir-a"),
            &user,
            &link("instance-a", "dir-a", "user-a"),
        );
        assert_eq!(view["externalId"], "external-1");
        assert_eq!(view["name"]["givenName"], "Ada");
        assert_eq!(view["name"]["familyName"], "Lovelace");
        assert!(view.get("privateMetadata").is_none());

        let response = scim_error(StatusCode::UNAUTHORIZED, "token required", None);
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(
            response.headers()[header::CONTENT_TYPE],
            "application/scim+json"
        );
        assert_eq!(response.headers()[header::WWW_AUTHENTICATE], "Bearer");
    }

    #[test]
    fn openapi_documents_scim_bearer_payloads_paging_and_resource_errors() {
        let document = serde_json::to_value(openapi()).expect("OpenAPI document serializes");
        let user_list = &document["paths"]["/scim/{directory_id}/Users"]["get"];
        assert_eq!(user_list["security"][0]["bearerAuth"], json!([]));
        let query_parameters = user_list["parameters"].as_array().expect("parameters list");
        assert!(
            query_parameters
                .iter()
                .any(|parameter| { parameter["name"] == "filter" && parameter["in"] == "query" })
        );
        assert!(
            query_parameters.iter().any(|parameter| {
                parameter["name"] == "startIndex" && parameter["in"] == "query"
            })
        );

        let user_create = &document["paths"]["/scim/{directory_id}/Users"]["post"];
        assert_eq!(
            user_create["requestBody"]["content"]["application/scim+json"]["schema"]["$ref"],
            "#/components/schemas/ScimUserRequest"
        );
        assert_eq!(
            user_create["responses"]["201"]["content"]["application/scim+json"]["schema"]["$ref"],
            "#/components/schemas/ScimUser"
        );
        assert_eq!(
            user_create["responses"]["401"]["content"]["application/scim+json"]["schema"]["$ref"],
            "#/components/schemas/ScimError"
        );

        let group_patch = &document["paths"]["/scim/{directory_id}/Groups/{group_id}"]["patch"];
        assert_eq!(
            group_patch["requestBody"]["content"]["application/scim+json"]["schema"]["$ref"],
            "#/components/schemas/ScimPatchRequest"
        );
        let patch_schema = &document["components"]["schemas"]["ScimPatchRequest"];
        assert!(patch_schema["properties"]["Operations"].is_object());
        assert!(
            patch_schema["required"]
                .as_array()
                .is_some_and(|required| required.contains(&json!("Operations")))
        );
        assert!(group_patch["responses"].get("409").is_some());

        let directory_create = &document["paths"]["/v1/{slug}/directories"]["post"];
        assert_eq!(directory_create["security"][0]["bearerAuth"], json!([]));
        assert_eq!(
            directory_create["responses"]["201"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/DirectoryToken"
        );
        assert_eq!(
            document["components"]["securitySchemes"]["bearerAuth"]["scheme"],
            "bearer"
        );
    }
}
