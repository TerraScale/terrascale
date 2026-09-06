//! Public discovery of durable database deletion work.
use super::*;

/// Reads the durable deletion operation while local access remains revoked.
#[utoipa::path(get, path = "/api/v1/databases/{name}/deletions/{operation_id}", responses((status = 200, description = "Authoritative deletion progress including regional stop receipts")))]
pub(super) async fn deletion_status(
    State(state): State<Arc<ApiState>>,
    Path((name, operation)): Path<(String, String)>,
    Extension(principal): Extension<crate::api_keys::Principal>,
) -> Response {
    if !principal.scope().admits_database(Verbs::ADMIN, &name) {
        return ApiError::forbidden("whole database deletion status").into_response();
    }
    let Some(authority) = &state.provisioning else {
        return ApiError::control_unavailable().into_response();
    };
    let (tenant_id, project_id) = match database_owner_scope(&state, &name) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    let scope = crate::delegated_identity::VerifiedProjectScope { tenant_id, project_id };
    match authority.deletion(&scope, &name, &operation, false).await {
        Ok(value) => json_response(StatusCode::OK, value.to_string()),
        Err(error) => provisioning_error(error).into_response(),
    }
}
