//! Embedded admin resources; `include_bytes!` keeps single-binary/container serving reliable.

use axum::{
    body::Body,
    extract::Path,
    http::{HeaderName, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
};

struct Asset {
    bytes: &'static [u8],
    content_type: &'static str,
}

const ADMIN_ASSETS: &[(&str, Asset)] = &[
    (
        "admin.css",
        Asset {
            bytes: include_bytes!("../../../assets/admin/admin.css"),
            content_type: "text/css; charset=utf-8",
        },
    ),
    (
        "datastar.js",
        Asset {
            bytes: include_bytes!("../../../assets/admin/datastar.js"),
            content_type: "text/javascript; charset=utf-8",
        },
    ),
    (
        "logo.svg",
        Asset {
            bytes: include_bytes!("../../../assets/admin/logo.svg"),
            content_type: "image/svg+xml",
        },
    ),
];

const FONT_ASSETS: &[(&str, Asset)] = &[
    (
        "oxanium-latin-wght-normal.woff2",
        Asset {
            bytes: include_bytes!("../../../assets/fonts/oxanium-latin-wght-normal.woff2"),
            content_type: "font/woff2",
        },
    ),
    (
        "jetbrains-mono-latin-wght-normal.woff2",
        Asset {
            bytes: include_bytes!("../../../assets/fonts/jetbrains-mono-latin-wght-normal.woff2"),
            content_type: "font/woff2",
        },
    ),
];

pub(super) async fn admin_asset(Path(asset): Path<String>) -> Response {
    serve(&asset, ADMIN_ASSETS)
}

pub(super) async fn font_asset(Path(asset): Path<String>) -> Response {
    serve(&asset, FONT_ASSETS)
}

fn serve(name: &str, assets: &[(&str, Asset)]) -> Response {
    let Some((_, asset)) = assets.iter().find(|(known, _)| *known == name) else {
        return (StatusCode::NOT_FOUND, "Not found").into_response();
    };
    let mut response = Response::new(Body::from(asset.bytes));
    *response.status_mut() = StatusCode::OK;
    let headers = response.headers_mut();
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static(asset.content_type),
    );
    headers.insert(
        header::CACHE_CONTROL,
        HeaderValue::from_static("public, max-age=3600"),
    );
    headers.insert(
        HeaderName::from_static("x-content-type-options"),
        HeaderValue::from_static("nosniff"),
    );
    headers.insert(
        HeaderName::from_static("content-security-policy"),
        HeaderValue::from_static(
            "default-src 'none'; style-src 'none'; script-src 'none'; sandbox",
        ),
    );
    response
}
