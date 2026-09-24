//! Hosted administrator pages for organization-scoped identity configuration.

mod assets;
mod connections;
mod portal;

pub use connections::pinned_public_client;

use axum::{
    Router,
    routing::{get, post},
};
use utoipa::openapi::{
    Info, OpenApi as OpenApiDoc,
    path::{HttpMethod, Operation, Paths},
};

use crate::AppState;

/// Browser routes for the hosted administrator interface and its bundled assets.
pub fn router() -> Router<AppState> {
    Router::new()
        .route("/ui/{slug}/admin", get(portal::get_portal))
        .route(
            "/ui/{slug}/admin/providers/{provider}",
            get(connections::get_provider_setup),
        )
        .route(
            "/ui/{slug}/admin/connections",
            post(connections::create_connection),
        )
        .route(
            "/ui/{slug}/admin/connections/{connection_id}/{action}",
            post(connections::update_connection),
        )
        .route("/admin/{asset}", get(assets::admin_asset))
        .route("/fonts/{asset}", get(assets::font_asset))
}

/// OpenAPI entries for the hosted administrator page, actions, and assets.
pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/ui/{slug}/admin",
            HttpMethod::Get,
            "Organization administrator portal",
        ),
        (
            "/ui/{slug}/admin/providers/{provider}",
            HttpMethod::Get,
            "Load provider setup fragment",
        ),
        (
            "/ui/{slug}/admin/connections",
            HttpMethod::Post,
            "Create an organization sign-in connection",
        ),
        (
            "/ui/{slug}/admin/connections/{connection_id}/{action}",
            HttpMethod::Post,
            "Enable or disable an organization connection",
        ),
        (
            "/admin/{asset}",
            HttpMethod::Get,
            "Load bundled administrator assets",
        ),
        (
            "/fonts/{asset}",
            HttpMethod::Get,
            "Load bundled interface fonts",
        ),
    ] {
        paths.add_path_operation(
            path,
            vec![method],
            Operation::builder()
                .tag("Hosted Admin")
                .summary(Some(summary))
                .build(),
        );
    }
    OpenApiDoc::new(Info::new("TerraScale Auth Hosted Admin", "1.0.0"), paths)
}
