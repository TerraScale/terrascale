//! Read-only discovery from the durable provisioning authority journal.

use super::{ApiError, ApiState, Arc, Extension, Response, State, StatusCode};
use axum::{extract::Query, response::IntoResponse};
use serde::{Deserialize, Serialize};

#[derive(Deserialize, utoipa::IntoParams)]
#[serde(deny_unknown_fields)]
#[into_params(parameter_in = Query)]
pub(super) struct OperationListQuery {
    /// Opaque cursor returned by the preceding page, bound to the verified project.
    after: Option<String>,
    /// Maximum records, from one through fifty; defaults to fifty.
    #[param(minimum = 1, maximum = 50)]
    limit: Option<usize>,
}

#[derive(Deserialize, Serialize, utoipa::ToSchema)]
pub(crate) struct OperationPage {
    pub operations: Vec<OperationSummary>,
    #[serde(default)]
    pub next_cursor: Option<String>,
    /// Journal kinds currently covered by this endpoint; additional owners remain explicit.
    pub covered_kinds: Vec<String>,
}

#[derive(Deserialize, Serialize, utoipa::ToSchema)]
pub(crate) struct OperationSummary {
    pub operation_id: String,
    pub kind: String,
    pub database_id: String,
    pub name: String,
    /// Queued, running, or succeeded, derived only from committed owner state.
    pub state: String,
    pub stage: String,
    pub created_at: String,
    /// Durable journal revision; viewing does not advance it.
    pub revision: u64,
    pub cancellable: bool,
    pub cancellation_reason: String,
}

#[utoipa::path(get, path = "/api/v1/operations", params(OperationListQuery),
    responses((status = 200, description = "Project-scoped durable operation discovery with explicit journal coverage", body = OperationPage)))]
pub(super) async fn list_operations(
    State(state): State<Arc<ApiState>>,
    scope: Option<Extension<crate::delegated_identity::VerifiedProjectScope>>,
    Query(query): Query<OperationListQuery>,
) -> Response {
    let Some(Extension(scope)) = scope else {
        return ApiError::control_unavailable().into_response();
    };
    let Some(authority) = &state.provisioning else {
        return ApiError::control_unavailable().into_response();
    };
    match authority.list_operations(&scope, query.after.as_deref(), query.limit.unwrap_or(50)).await
    {
        Ok(page) => crate::export_api::json_response(StatusCode::OK, &page),
        Err(error) => super::provisioning_error(error).into_response(),
    }
}

#[cfg(test)]
mod tests {

    #[test]
    fn discovery_is_project_administration_and_has_a_typed_paged_contract() {
        assert!(super::super::is_project_administration(
            &axum::http::Method::GET,
            "/api/v1/operations"
        ));
        assert_eq!(
            super::super::required_verb(&axum::http::Method::GET, "/api/v1/operations"),
            crate::api_keys::Verbs::ADMIN
        );
        let document = serde_json::from_str::<serde_json::Value>(&super::super::openapi_json())
            .expect("contract");
        let route = &document["paths"]["/api/v1/operations"]["get"];
        assert_eq!(route["operationId"], "list_operations");
        assert_eq!(
            route["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/OperationPage"
        );
        assert!(
            document["components"]["schemas"]["OperationSummary"]["properties"]
                .get("key_version")
                .is_none()
        );
    }
}
