//! Feature modules and the HTTP/OpenAPI contract shared by the app assembler.

pub mod admin;
pub mod authorization;
pub mod billing;
pub mod connections;
pub mod email;
pub mod instances;
pub mod management;
pub mod mfa;
pub mod oauth;
pub mod observability;
pub mod organizations;
pub mod passcodes;
pub mod passkeys;
pub mod scim;
pub mod templates;
pub mod ui;
pub mod users;
pub mod webauthn;
pub mod webhooks;

use axum::Router;
use utoipa::openapi::OpenApi;

use crate::AppState;

/// Register each feature's real HTTP routes at its declared absolute path.
pub fn router() -> Router<AppState> {
    Router::new()
        .merge(admin::router())
        .merge(authorization::router())
        .merge(billing::router())
        .merge(connections::router())
        .merge(instances::router())
        .merge(management::router())
        .merge(mfa::router())
        .merge(oauth::router())
        .merge(observability::router())
        .merge(organizations::router())
        .merge(passcodes::router())
        .merge(passkeys::router())
        .merge(scim::router())
        .merge(templates::router())
        .merge(users::router())
        .merge(webhooks::router())
}

/// Pair every served route family with its feature-authored OpenAPI paths.
pub fn openapi_documents() -> Vec<OpenApi> {
    vec![
        admin::openapi(),
        authorization::openapi(),
        billing::openapi(),
        connections::openapi(),
        instances::openapi(),
        management::openapi(),
        mfa::openapi(),
        oauth::openapi(),
        observability::openapi(),
        organizations::openapi(),
        passcodes::openapi(),
        passkeys::openapi(),
        scim::openapi(),
        templates::openapi(),
        users::openapi(),
        webhooks::openapi(),
    ]
}
