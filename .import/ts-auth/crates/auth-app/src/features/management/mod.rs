//! Instance-scoped Management API routes and shared API-key policy.

mod gate;
mod routes;

pub use gate::{InstanceContext, management_gate, require, resolve_instance, scope_allows};

use axum::Router;
use utoipa::OpenApi;

use crate::AppState;

/// Management API routes for the `users`, `sessions`, `instance`, and `api-keys` families.
pub fn router() -> Router<AppState> {
    routes::router()
}

/// OpenAPI document for the management routes implemented in this feature.
pub fn openapi() -> utoipa::openapi::OpenApi {
    routes::ApiDoc::openapi()
}
