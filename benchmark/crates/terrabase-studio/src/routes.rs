//! The HTTP surface: one router, server-rendered pages, and Datastar actions.

use std::fmt::Write as _;
use std::net::{IpAddr, SocketAddr};
use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::extract::{DefaultBodyLimit, Extension, Form, Query, State};
use axum::http::uri::Authority;
use axum::http::{HeaderValue, Request, StatusCode, Uri, header};
use axum::middleware::{self, Next};
use axum::response::{Html, Response};
use axum::routing::{get, post};
use serde::Deserialize;

use crate::datastar::{self, MergeMode};
use crate::engine_ops;
use crate::html::{self, Tone};
use crate::metrics;
use crate::pages::{
    backups, documents, indexes, metrics_page, overview, query, replication, surfaces, tiers, usage,
};
use crate::s3_backup::{
    BackupRunMode, S3BackupRepository, S3BackupSettings, backup_to_s3, restore_from_s3,
    verify_s3_chain,
};
use crate::state::StudioState;

/// Maximum encoded form body accepted by any action. The engine accepts a
/// 1 MiB document; allowing roughly 8x for JSON and form escaping plus form
/// fields keeps that document usable without admitting an unbounded body.
const MAX_ACTION_BODY_BYTES: usize = 9 * 1024 * 1024;
/// Keep one schema declaration within the engine's one-document value ceiling.
const MAX_SCHEMA_INPUT_BYTES: usize = 1 << 20;
const MAX_CONFIRMATION_TARGET_BYTES: usize = 1_024;
const MAX_CONFIRMATION_PHRASE_BYTES: usize = 128;
const CONFIRM_TARGET_FIELD: &str = "confirm_target";
const CONFIRM_PHRASE_FIELD: &str = "confirm_phrase";

/// The vendored Basecoat stylesheet, embedded so the binary is self-contained.
const BASECOAT_CSS: &str = include_str!("../static/basecoat.css");
/// The vendored Datastar browser runtime.
const DATASTAR_JS: &[u8] = include_bytes!("../static/datastar-1.0.3.js");
/// The small Studio-owned Monaco loader and form bridge.
const STUDIO_MONACO_JS: &[u8] = include_bytes!("../static/studio-monaco.js");
/// Monaco 0.52.2 assets are vendored so the private Studio makes no CDN calls.
const MONACO_LOADER: &[u8] = include_bytes!("../static/monaco/vs/loader.js");
const MONACO_EDITOR_JS: &[u8] = include_bytes!("../static/monaco/vs/editor/editor.main.js");
const MONACO_EDITOR_CSS: &[u8] = include_bytes!("../static/monaco/vs/editor/editor.main.css");
const MONACO_WORKER_MAIN: &[u8] = include_bytes!("../static/monaco/vs/base/worker/workerMain.js");
const MONACO_JSON_MODE: &[u8] = include_bytes!("../static/monaco/vs/language/json/jsonMode.js");
const MONACO_JSON_WORKER: &[u8] = include_bytes!("../static/monaco/vs/language/json/jsonWorker.js");
const MONACO_CODICON_FONT: &[u8] =
    include_bytes!("../static/monaco/vs/base/browser/ui/codicons/codicon/codicon.ttf");

/// Builds the studio's router.
pub fn router(state: Arc<StudioState>) -> Router {
    let security = SecurityState::try_new(&state.config.addr)
        .expect("StudioState must carry the validated numeric loopback listener");
    Router::new()
        .route("/static/basecoat.css", get(stylesheet))
        .route("/static/datastar-1.0.3.js", get(datastar_runtime))
        .route("/static/studio-monaco.js", get(studio_monaco_runtime))
        .route("/static/monaco/vs/loader.js", get(monaco_loader))
        .route("/static/monaco/vs/editor/editor.main.js", get(monaco_editor_js))
        .route("/static/monaco/vs/editor/editor.main.css", get(monaco_editor_css))
        .route("/static/monaco/vs/base/worker/workerMain.js", get(monaco_worker_main))
        .route("/static/monaco/vs/language/json/jsonMode.js", get(monaco_json_mode))
        .route("/static/monaco/vs/language/json/jsonWorker.js", get(monaco_json_worker))
        .route(
            "/static/monaco/vs/base/browser/ui/codicons/codicon/codicon.ttf",
            get(monaco_codicon_font),
        )
        .route("/", get(overview_page))
        .route("/healthz", get(healthz))
        .route("/readyz", get(readyz))
        .route("/metrics", get(metrics_page_handler))
        .route("/cluster", get(cluster_page))
        .route("/surfaces", get(surfaces_page))
        .route("/documents", get(documents_page))
        .route("/documents/rows", post(documents_rows))
        .route("/documents/read", post(documents_read))
        .route("/documents/put", post(documents_put))
        .route("/documents/delete", post(documents_delete))
        .route("/documents/sample", post(documents_sample))
        .route("/documents/clear-view", post(documents_clear_view))
        .route("/indexes", get(indexes_page))
        .route("/indexes/create", post(indexes_create))
        .route("/indexes/action", post(indexes_action))
        .route("/query", get(query_page))
        .route("/query/run", post(query_run))
        .route("/query/tql", post(query_tql))
        .route("/replication", get(replication_page))
        .route("/replication/regions/admit", post(regions_admit))
        .route("/replication/regions/retire", post(regions_retire))
        .route("/replication/topology/add-region", post(topology_add_region))
        .route("/replication/topology/remove-region", post(topology_remove_region))
        .route("/replication/durability/submit", post(durability_submit))
        .route("/replication/durability/toggle-member", post(durability_toggle_member))
        .route("/tiers", get(tiers_page))
        .route("/tiers/configure", post(tiers_configure))
        .route("/tiers/pause", post(tiers_pause))
        .route("/tiers/resume", post(tiers_resume))
        .route("/tiers/dry-run", post(tiers_dry_run))
        .route("/tiers/run", post(tiers_run))
        .route("/usage", get(usage_page))
        .route("/usage/record", post(usage_record))
        .route("/backups", get(backups_page))
        .route("/backup/configure", post(backup_configure))
        .route("/backup/run", post(backup_run))
        .route("/backup/verify", post(backup_verify))
        .route("/backup/restore", post(backup_restore))
        .route("/deletion/request", post(deletion_request))
        .route("/deletion/admit", post(deletion_admit))
        .route("/deletion/receipt", post(deletion_receipt))
        .route("/deletion/hold", post(deletion_hold))
        .route("/deletion/end-hold", post(deletion_end_hold))
        .route("/deletion/verify", post(deletion_verify))
        .route("/deletion/complete", post(deletion_complete))
        .route("/maintenance/compact", post(maintenance_compact))
        .route("/maintenance/maintain-indexes", post(maintenance_maintain_indexes))
        .route("/families/validation", post(family_validation))
        .route("/families/schema/declare", post(family_schema_declare))
        .route("/streams/documents", post(stream_documents))
        .route("/metrics/prometheus", get(prometheus_metrics))
        .route("/events/metrics", get(metrics_stream))
        .layer(DefaultBodyLimit::max(MAX_ACTION_BODY_BYTES))
        .layer(middleware::from_fn_with_state(security, security_headers))
        .with_state(state)
}

#[derive(Clone, Debug)]
struct SecurityState {
    listener: SocketAddr,
}

impl SecurityState {
    fn try_new(address: &str) -> Result<Self, String> {
        let listener = address
            .parse::<SocketAddr>()
            .map_err(|_| "the Studio listener is not a numeric socket address".to_owned())?;
        if !listener.ip().is_loopback() {
            return Err("the Studio listener is not loopback".to_owned());
        }
        Ok(Self { listener })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct CspNonce(String);

impl CspNonce {
    fn generate() -> Result<Self, getrandom::Error> {
        let mut bytes = [0_u8; 16];
        getrandom::fill(&mut bytes)?;
        let mut encoded = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            write!(&mut encoded, "{byte:02x}").expect("writing hexadecimal into a String");
        }
        Ok(Self(encoded))
    }

    fn as_str(&self) -> &str {
        &self.0
    }
}

/// Adds browser hardening headers to every Studio response. The Studio is a
/// loopback-only owner tool with no authentication, so the listener refuses
/// non-loopback binds and this middleware rejects cross-origin writes.
async fn security_headers(
    State(security): State<SecurityState>,
    mut request: Request<Body>,
    next: Next,
) -> Response {
    let nonce = match CspNonce::generate() {
        Ok(nonce) => nonce,
        Err(_) => {
            return hardened_without_scripts(status_text(
                StatusCode::INTERNAL_SERVER_ERROR,
                "secure response nonce generation failed\n",
            ));
        }
    };
    if !trusted_request_host(&request, &security) {
        return hardened(
            status_text(StatusCode::FORBIDDEN, "untrusted Host header refused\n"),
            &nonce,
        );
    }
    if request.method() == axum::http::Method::POST && !same_origin(&request, &security) {
        return hardened(
            status_text(StatusCode::FORBIDDEN, "cross-origin action refused\n"),
            &nonce,
        );
    }
    request.extensions_mut().insert(nonce.clone());
    let request = match reject_duplicate_confirmations(request).await {
        Ok(request) => request,
        Err(response) => return hardened(response, &nonce),
    };
    hardened(next.run(request).await, &nonce)
}

/// Rejects DNS-rebinding authorities on every method, including read-only
/// pages. A missing Host remains available to in-process tests and explicit
/// non-browser clients; HTTP/1.1 browsers always send one.
fn trusted_request_host(request: &Request<Body>, security: &SecurityState) -> bool {
    let Some(host) = request.headers().get(header::HOST) else {
        return true;
    };
    host.to_str()
        .ok()
        .and_then(|host| host.parse::<Authority>().ok())
        .and_then(|authority| trusted_authority(&authority, security.listener))
        .is_some()
}

fn hardened(mut response: Response, nonce: &CspNonce) -> Response {
    let headers = response.headers_mut();
    headers.insert(header::X_CONTENT_TYPE_OPTIONS, HeaderValue::from_static("nosniff"));
    headers.insert(header::REFERRER_POLICY, HeaderValue::from_static("no-referrer"));
    headers.insert(header::X_FRAME_OPTIONS, HeaderValue::from_static("DENY"));
    let policy = format!(
        "default-src 'self'; script-src 'self' 'nonce-{}'; style-src 'self'; font-src 'self' data:; img-src 'self' data:; connect-src 'self'; worker-src 'self' data: blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
        nonce.as_str(),
    );
    headers.insert(
        header::CONTENT_SECURITY_POLICY,
        HeaderValue::from_str(&policy).expect("a hexadecimal nonce makes a valid CSP header"),
    );
    response
}

fn hardened_without_scripts(mut response: Response) -> Response {
    let headers = response.headers_mut();
    headers.insert(header::X_CONTENT_TYPE_OPTIONS, HeaderValue::from_static("nosniff"));
    headers.insert(header::REFERRER_POLICY, HeaderValue::from_static("no-referrer"));
    headers.insert(header::X_FRAME_OPTIONS, HeaderValue::from_static("DENY"));
    headers.insert(
        header::CONTENT_SECURITY_POLICY,
        HeaderValue::from_static(
            "default-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
        ),
    );
    response
}

/// Rejects repeated confirmation fields before Axum's form decoder can apply
/// last-value-wins semantics. The request body is restored unchanged for the
/// normal extractor when no duplicate is present.
async fn reject_duplicate_confirmations(request: Request<Body>) -> Result<Request<Body>, Response> {
    if request.method() != axum::http::Method::POST
        || !request
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_some_and(|value| {
                value.split(';').next().is_some_and(|kind| {
                    kind.trim().eq_ignore_ascii_case("application/x-www-form-urlencoded")
                })
            })
    {
        return Ok(request);
    }
    let (parts, body) = request.into_parts();
    let body = match axum::body::to_bytes(body, MAX_ACTION_BODY_BYTES + 1).await {
        Ok(body) if body.len() <= MAX_ACTION_BODY_BYTES => body,
        Ok(_) => {
            return Err(status_text(
                StatusCode::PAYLOAD_TOO_LARGE,
                "request body exceeds the Studio action limit\n",
            ));
        }
        Err(_) => {
            return Err(status_text(StatusCode::BAD_REQUEST, "request body could not be read\n"));
        }
    };
    for field in [CONFIRM_TARGET_FIELD, CONFIRM_PHRASE_FIELD] {
        if form_field_count(&body, field) > 1 {
            return Err(status_text(
                StatusCode::BAD_REQUEST,
                "duplicate confirmation fields refused\n",
            ));
        }
    }
    Ok(Request::from_parts(parts, Body::from(body)))
}

fn form_field_count(body: &[u8], field: &str) -> usize {
    body.split(|byte| *byte == b'&')
        .filter(|pair| {
            pair.split(|byte| *byte == b'=')
                .next()
                .is_some_and(|name| decode_form_component(name).is_some_and(|name| name == field))
        })
        .count()
}

fn decode_form_component(value: &[u8]) -> Option<String> {
    let mut decoded = Vec::with_capacity(value.len());
    let mut index = 0;
    while index < value.len() {
        match value[index] {
            b'+' => decoded.push(b' '),
            b'%' if index + 2 < value.len() => {
                let high = hex_digit(value[index + 1])?;
                let low = hex_digit(value[index + 2])?;
                decoded.push(high * 16 + low);
                index += 2;
            }
            byte => decoded.push(byte),
        }
        index += 1;
    }
    String::from_utf8(decoded).ok()
}

fn hex_digit(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

/// Requires a bounded, already-normalized operator confirmation. Trimming is
/// the sole normalization: any leading/trailing Unicode whitespace, control
/// character, or oversized value is rejected rather than silently rewritten.
/// Submitted values are never included in this error, journal, or metric.
fn require_confirmation(
    target: &str,
    phrase: &str,
    expected_target: &str,
    expected_phrase: &str,
) -> Result<(), String> {
    if target.is_empty()
        || target.len() > MAX_CONFIRMATION_TARGET_BYTES
        || phrase.is_empty()
        || phrase.len() > MAX_CONFIRMATION_PHRASE_BYTES
        || target != target.trim()
        || phrase != phrase.trim()
        || target.chars().any(char::is_control)
        || phrase.chars().any(char::is_control)
    {
        return Err("confirmation is missing, oversized, contains a control character, or is not normalized".to_owned());
    }
    if target != expected_target || phrase != expected_phrase {
        return Err("confirmation does not match the requested target and action".to_owned());
    }
    Ok(())
}

fn status_text(status: StatusCode, body: &'static str) -> Response {
    let mut response = text(body, "text/plain; charset=utf-8");
    *response.status_mut() = status;
    response
}

/// Accepts state-changing requests only when their browser origin proves they
/// are same-origin. Non-browser operators may omit browser metadata entirely;
/// that is the explicit compatibility policy for curl/CLI clients. A
/// `Sec-Fetch-Site` value without an `Origin` is browser metadata but not proof
/// of this listener's origin, so it is rejected (including `same-site`).
fn same_origin(request: &Request<Body>, security: &SecurityState) -> bool {
    let fetch_header = request.headers().get("sec-fetch-site");
    let fetch_site = fetch_header.and_then(|value| value.to_str().ok());
    let origin_header = request.headers().get(header::ORIGIN);
    let origin = origin_header.and_then(|value| value.to_str().ok());
    let host = request.headers().get(header::HOST).and_then(|value| value.to_str().ok());

    // A malformed browser metadata header is not equivalent to an omitted
    // one. The headerless exception below is deliberately limited to clients
    // that sent no browser metadata at all.
    if fetch_header.is_some() && fetch_site.is_none() {
        return false;
    }
    if origin_header.is_some() && origin.is_none() {
        return false;
    }
    if fetch_site.is_some_and(|value| value.eq_ignore_ascii_case("cross-site")) {
        return false;
    }
    let Some(origin) = origin else {
        // No browser-only headers means this is allowed as a non-browser
        // operator request. If Sec-Fetch-Site is present, however, a browser
        // asserted a relationship that cannot establish this exact origin.
        return fetch_site.is_none();
    };
    let Some(host) = host else {
        return false;
    };
    let Ok(origin) = origin.parse::<Uri>() else {
        return false;
    };
    if !origin.scheme_str().is_some_and(|scheme| scheme.eq_ignore_ascii_case("http"))
        || origin.query().is_some()
        || origin.path() != "/"
    {
        return false;
    }
    let Some(origin_authority) = origin.authority() else {
        return false;
    };
    let Ok(host_authority) = host.parse::<Authority>() else {
        return false;
    };
    trusted_authority(origin_authority, security.listener)
        .zip(trusted_authority(&host_authority, security.listener))
        .is_some_and(|(origin_host, request_host)| origin_host == request_host)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum TrustedHost {
    ConfiguredLoopback,
    Localhost,
}

fn trusted_authority(authority: &Authority, listener: SocketAddr) -> Option<TrustedHost> {
    if authority.as_str().contains('@') {
        return None;
    }
    let port = authority.port_u16().or_else(|| (listener.port() == 80).then_some(80))?;
    if port != listener.port() {
        return None;
    }
    let host = authority.host().trim_matches(['[', ']']);
    if host.eq_ignore_ascii_case("localhost") {
        return Some(TrustedHost::Localhost);
    }
    let address = host.parse::<IpAddr>().ok()?;
    (address == listener.ip()).then_some(TrustedHost::ConfiguredLoopback)
}

/* ------------------------------------------------------------------ static */

fn text(body: &'static str, kind: &'static str) -> Response {
    let mut response = Response::new(body.to_owned().into());
    response.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static(kind));
    response
}

async fn stylesheet() -> Response {
    let mut response = text(BASECOAT_CSS, "text/css; charset=utf-8");
    response.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
    response
}

fn asset(body: &'static [u8], kind: &'static str) -> Response {
    let mut response = Response::new(axum::body::Body::from(body));
    response.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static(kind));
    response.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
    response
}

async fn datastar_runtime() -> Response {
    asset(DATASTAR_JS, "text/javascript; charset=utf-8")
}

async fn studio_monaco_runtime() -> Response {
    asset(STUDIO_MONACO_JS, "text/javascript; charset=utf-8")
}

async fn monaco_loader() -> Response {
    asset(MONACO_LOADER, "text/javascript; charset=utf-8")
}

async fn monaco_editor_js() -> Response {
    asset(MONACO_EDITOR_JS, "text/javascript; charset=utf-8")
}

async fn monaco_editor_css() -> Response {
    asset(MONACO_EDITOR_CSS, "text/css; charset=utf-8")
}

async fn monaco_worker_main() -> Response {
    asset(MONACO_WORKER_MAIN, "text/javascript; charset=utf-8")
}

async fn monaco_json_mode() -> Response {
    asset(MONACO_JSON_MODE, "text/javascript; charset=utf-8")
}

async fn monaco_json_worker() -> Response {
    asset(MONACO_JSON_WORKER, "text/javascript; charset=utf-8")
}

async fn monaco_codicon_font() -> Response {
    asset(MONACO_CODICON_FONT, "font/ttf")
}

async fn prometheus_metrics(State(state): State<Arc<StudioState>>) -> Response {
    let outcome =
        on_blocking(state, |state| Ok(metrics::render_prometheus(&metrics::gather(state)))).await;
    let (status, body) = match outcome {
        Ok(body) => (axum::http::StatusCode::OK, body),
        Err(failure) => (
            axum::http::StatusCode::INTERNAL_SERVER_ERROR,
            format!("# metrics collection failed: {}\n", failure.replace(['\r', '\n'], " ")),
        ),
    };
    let mut response = Response::new(body.into());
    *response.status_mut() = status;
    response.headers_mut().insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("text/plain; version=0.0.4; charset=utf-8"),
    );
    response.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
}

/* -------------------------------------------------------------- html pages */

async fn full_page<F>(state: Arc<StudioState>, nonce: CspNonce, render: F) -> Html<String>
where
    F: FnOnce(&StudioState, &str) -> String + Send + 'static,
{
    let failure_nonce = nonce.clone();
    Html(
        on_blocking(state, move |state| Ok(render(state, nonce.as_str())))
            .await
            .unwrap_or_else(|message| page_failure(failure_nonce.as_str(), message)),
    )
}

async fn overview_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, overview::render).await
}

async fn metrics_page_handler(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, metrics_page::render).await
}

async fn cluster_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, replication::render).await
}

async fn indexes_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, indexes::render).await
}

async fn healthz() -> Response {
    text("ok\n", "text/plain; charset=utf-8")
}

async fn readyz(State(state): State<Arc<StudioState>>) -> Response {
    match on_blocking(state, |state| {
        engine_ops::published_sequence(state.engine.as_ref()).map(|_| ()).map_err(String::from)
    })
    .await
    {
        Ok(_) => text("ready\n", "text/plain; charset=utf-8"),
        Err(error) => {
            let mut response = text("not ready\n", "text/plain; charset=utf-8");
            *response.status_mut() = StatusCode::SERVICE_UNAVAILABLE;
            response.headers_mut().insert(
                axum::http::HeaderName::from_static("x-terrabase-readiness"),
                HeaderValue::from_static("engine snapshot unavailable"),
            );
            let _ = error;
            response
        }
    }
}

#[derive(Deserialize)]
struct DocumentsQuery {
    tenant: Option<String>,
    collection: Option<String>,
    page: Option<u64>,
}

async fn documents_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
    parameters: Query<DocumentsQuery>,
) -> Html<String> {
    let requested = parameters.0;
    let fallback = documents::Selection::default_for(&state);
    let selection = documents::Selection {
        tenant: requested.tenant.unwrap_or(fallback.tenant),
        collection: requested.collection.unwrap_or(fallback.collection),
        page: requested.page.unwrap_or(0),
    };
    full_page(state, nonce, move |state, nonce| documents::render(state, &selection, nonce)).await
}

async fn query_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
    parameters: Query<DocumentsQuery>,
) -> Html<String> {
    let pair = parameters.0.tenant.zip(parameters.0.collection);
    full_page(state, nonce, move |state, nonce| query::render(state, &pair, nonce)).await
}

async fn replication_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, replication::render).await
}

async fn surfaces_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, surfaces::render).await
}

async fn tiers_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    full_page(state, nonce, tiers::render).await
}

#[derive(Deserialize)]
struct UsageQuery {
    tenant: Option<String>,
}

async fn usage_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
    Query(parameters): Query<UsageQuery>,
) -> Html<String> {
    let tenant = parameters
        .tenant
        .filter(|tenant| !tenant.trim().is_empty())
        .or_else(|| state.tenants().into_iter().next())
        .unwrap_or_else(|| "demo".to_owned());
    full_page(state, nonce, move |state, nonce| usage::render(state, &tenant, nonce)).await
}

async fn backups_page(
    State(state): State<Arc<StudioState>>,
    Extension(nonce): Extension<CspNonce>,
) -> Html<String> {
    let points = load_remote_recovery_points(state.as_ref()).await;
    Html(backups::render(state.as_ref(), points.as_deref().map_err(String::as_str), nonce.as_str()))
}

fn page_failure(nonce: &str, message: String) -> String {
    html::page(
        nonce,
        "Unavailable",
        "/",
        "",
        &format!(
            "{}{}",
            html::heading("The page did not render", "The engine refused the read."),
            html::alert(Tone::Bad, "Failure", &message),
        ),
    )
}

/* --------------------------------------------------------------- documents */

#[derive(Deserialize)]
struct RowsForm {
    tenant: String,
    collection: String,
    page: Option<u64>,
}

async fn documents_rows(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<RowsForm>,
) -> Response {
    let page = form.page.unwrap_or(0);
    let outcome = on_blocking(state, move |state| {
        state.observe(&form.tenant, Some(&form.collection));
        let selection =
            documents::Selection { tenant: form.tenant, collection: form.collection, page };
        Ok(documents::rows_fragment(state, &selection))
    })
    .await;
    merge(documents::ROWS_ID, outcome)
}

#[derive(Deserialize)]
struct ReadForm {
    tenant: String,
    collection: String,
    id: String,
}

async fn documents_read(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ReadForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        state.observe(&form.tenant, Some(&form.collection));
        Ok(read_one(state, &form.tenant, &form.collection, &form.id))
    })
    .await;
    merge(documents::VIEW_ID, outcome)
}

fn read_one(state: &StudioState, tenant: &str, collection: &str, id: &str) -> String {
    let outcome = (|| {
        let tenant = engine_ops::tenant(tenant)?;
        let collection = engine_ops::collection(collection)?;
        let id = engine_ops::document_id(id)?;
        let session = engine_ops::session(state.engine.as_ref(), &tenant)?;
        session.read(&collection, &id)
    })();
    match outcome {
        Ok(Some(document)) => {
            html::definition(&[
                ("id", document.id().as_str().to_owned()),
                ("collection", document.collection().as_str().to_owned()),
                ("version", document.metadata().version().to_string()),
            ]) + &format!(
                "<pre class=\"mt-3 overflow-x-auto rounded-lg border bg-muted/40 p-4 text-xs leading-relaxed\">{}</pre>",
                html::esc(&crate::json::to_display(document.value()))
            )
        }
        Ok(None) => html::alert(
            Tone::Warn,
            "No such document",
            &format!("`{id}` is absent from `{collection}` for tenant `{tenant}`."),
        ),
        Err(failure) => html::alert(Tone::Bad, "The read failed", &failure.0),
    }
}

#[derive(Deserialize)]
struct PutForm {
    tenant: String,
    collection: String,
    id: String,
    json: String,
    expected: Option<String>,
    version: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn documents_put(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<PutForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &format!("{}/{}/{}", form.tenant, form.collection, form.id),
            "replace document",
        )?;
        state.observe(&form.tenant, Some(&form.collection));
        let tenant = engine_ops::tenant(&form.tenant)?;
        let collection = engine_ops::collection(&form.collection)?;
        let id = engine_ops::document_id(&form.id)?;
        let expected = engine_ops::expected_version(
            form.expected.as_deref().unwrap_or("any"),
            form.version.as_deref().unwrap_or(""),
        )?;
        let parsed: serde_json::Value = serde_json::from_str(&form.json)
            .map_err(|error| format!("the editor's JSON does not parse: {error}"))?;
        let operation = state.next_operation()?;
        commit_outcome(
            state,
            "documents/put",
            engine_ops::put_document(
                state.engine.as_ref(),
                operation,
                &tenant,
                &collection,
                &id,
                crate::json::from_json(parsed),
                expected,
            ),
        )
    })
    .await;
    receipt("editor-receipt", outcome)
}

#[derive(Clone, Deserialize)]
struct DeleteForm {
    tenant: String,
    collection: String,
    id: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn documents_delete(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeleteForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let refresh_form = form.clone();
    let outcome = on_blocking(state, move |state| {
        let confirmation_target = format!("{}/{}/{}", form.tenant, form.collection, form.id);
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &confirmation_target,
            "delete document",
        )?;
        let tenant = engine_ops::tenant(&form.tenant)?;
        let collection = engine_ops::collection(&form.collection)?;
        let id = engine_ops::document_id(&form.id)?;
        let operation = state.next_operation()?;
        state.observe(&form.tenant, Some(&form.collection));
        commit_outcome(
            state,
            "documents/delete",
            engine_ops::delete_document(
                state.engine.as_ref(),
                operation,
                &tenant,
                &collection,
                &id,
                terrabase::ExpectedVersion::Any,
            ),
        )
    })
    .await;
    let rows = if outcome.is_ok() {
        on_blocking(refresh_state, move |state| {
            state.observe(&refresh_form.tenant, Some(&refresh_form.collection));
            Ok(documents::rows_fragment(
                state,
                &documents::Selection {
                    tenant: refresh_form.tenant,
                    collection: refresh_form.collection,
                    page: 0,
                },
            ))
        })
        .await
    } else {
        Ok(String::new())
    };
    response_with_refresh(
        "delete-receipt",
        outcome,
        vec![datastar::merge_fragments(
            &rows.unwrap_or_default(),
            Some(&format!("#{}", documents::ROWS_ID)),
            MergeMode::Outer,
        )],
    )
}

/// Answers with the protocol's removal event.
///
/// This is the one studio action that takes something off the page rather than
/// merging a replacement in, so it exercises `datastar-remove-fragments`.
async fn documents_clear_view() -> Response {
    datastar::response(vec![datastar::remove_fragments(&format!("#{}", documents::VIEW_ID))])
}

#[derive(Deserialize)]
struct IndexActionForm {
    tenant: String,
    name: String,
    action: String,
    confirm_target: String,
    confirm_phrase: String,
}

#[derive(Deserialize)]
struct IndexCreateForm {
    tenant: String,
    name: String,
    collection: String,
    fields: String,
    capabilities: String,
    priority: String,
    lock: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn indexes_create(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<IndexCreateForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &format!("{}/{}", form.tenant, form.name),
            "create or update index",
        )?;
        let result = indexes::create_index(
            state,
            &form.tenant,
            &form.name,
            &form.collection,
            &form.fields,
            &form.capabilities,
            &form.priority,
            &form.lock,
        );
        let rendered = result.as_ref().map_or_else(Clone::clone, Clone::clone);
        state.record("indexes/create", rendered, result.is_ok());
        result
    })
    .await;
    let refresh = index_surface_refresh(refresh_state).await;
    response_with_refresh(indexes::RECEIPT_ID, outcome, refresh)
}

async fn indexes_action(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<IndexActionForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &format!("{}/{}", form.tenant, form.name),
            "run index action",
        )?;
        let result = indexes::run_action(state, &form.tenant, &form.name, &form.action);
        let rendered = result.as_ref().map_or_else(Clone::clone, Clone::clone);
        state.record("indexes/action", rendered, result.is_ok());
        result
    })
    .await;
    let extra = index_surface_refresh(refresh_state).await;
    response_with_refresh(indexes::RECEIPT_ID, outcome, extra)
}

async fn index_surface_refresh(state: Arc<StudioState>) -> Vec<String> {
    on_blocking(state, |state| Ok(indexes::catalog_fragment(state)))
        .await
        .map(|markup| {
            vec![datastar::merge_fragments(
                &markup,
                Some(&format!("#{}", indexes::CATALOG_ID)),
                MergeMode::Outer,
            )]
        })
        .unwrap_or_default()
}

async fn documents_sample(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "sample documents",
            "load sample documents",
        )?;
        seed_sample(state)
    })
    .await;
    receipt("sample-receipt", outcome)
}

/// Writes a small deterministic sample so a fresh location is not empty.
#[cfg(test)]
pub(crate) fn test_seed_sample(state: &StudioState) -> Result<String, String> {
    seed_sample(state)
}

fn seed_sample(state: &StudioState) -> Result<String, String> {
    let samples: [(&str, &str, serde_json::Value); 6] = [
        ("demo", "users", serde_json::json!({"name": "Ada Lovelace", "level": 3, "active": true})),
        ("demo", "users", serde_json::json!({"name": "Grace Hopper", "level": 9, "active": true})),
        (
            "demo",
            "users",
            serde_json::json!({"name": "Edsger Dijkstra", "level": 7, "active": false}),
        ),
        ("demo", "orders", serde_json::json!({"total": 1250, "currency": "USD", "lines": 3})),
        ("demo", "orders", serde_json::json!({"total": 480, "currency": "EUR", "lines": 1})),
        ("demo", "sessions", serde_json::json!({"minutes": 42, "region": "gru"})),
    ];
    let mut written = 0;
    for (index, (tenant, collection, value)) in samples.iter().enumerate() {
        let tenant = engine_ops::tenant(tenant)?;
        let collection = engine_ops::collection(collection)?;
        let id = engine_ops::document_id(&format!("sample-{index:03}"))?;
        let operation = state.next_operation()?;
        let outcome = engine_ops::put_document(
            state.engine.as_ref(),
            operation,
            &tenant,
            &collection,
            &id,
            crate::json::from_json(value.clone()),
            terrabase::ExpectedVersion::Any,
        )?;
        if matches!(outcome, terrabase::CommitOutcome::Committed(_)) {
            written += 1;
        }
        state.observe(tenant.as_str(), Some(collection.as_str()));
    }
    Ok(format!(
        "wrote {written} sample document(s) across the demo tenant's users, orders, and sessions collections"
    ))
}

/// Renders a commit outcome with its receipt, and journals the action.
pub fn commit_outcome(
    state: &StudioState,
    action: &str,
    outcome: Result<terrabase::CommitOutcome, engine_ops::OpError>,
) -> Result<String, String> {
    match outcome {
        Ok(terrabase::CommitOutcome::Committed(receipt)) => {
            let text = format!(
                "committed operation `{}` at sequence {} ({:?} durability)",
                receipt.operation_id().as_str(),
                receipt.sequence().get(),
                receipt.durability(),
            );
            state.record(action, text.clone(), true);
            Ok(text)
        }
        Ok(terrabase::CommitOutcome::Rejected(rejection)) => {
            let text = format!(
                "rejected: {rejection:?} — retry policy {:?}",
                rejection.retry_disposition()
            );
            state.record(action, text.clone(), false);
            Err(text)
        }
        Ok(terrabase::CommitOutcome::Unknown(uncertain)) => {
            let text = format!(
                "outcome uncertain for operation `{}` ({:?}); resolve it by that identity before retrying",
                uncertain.operation_id().as_str(),
                uncertain.reason(),
            );
            state.record(action, text.clone(), false);
            Err(text)
        }
        Err(failure) => {
            state.record(action, failure.0.clone(), false);
            Err(failure.0)
        }
    }
}

/* ------------------------------------------------------------------- query */

#[derive(Deserialize)]
struct QueryFormParts {
    tenant: String,
    collection: String,
    field: Option<Vec<String>>,
    kind: Option<Vec<String>>,
    value: Option<Vec<String>>,
    high: Option<Vec<String>>,
    order: Option<String>,
    direction: Option<String>,
    offset: Option<String>,
    limit: Option<String>,
    projection: Option<String>,
}

async fn query_run(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<QueryFormParts>,
) -> Response {
    let tenant = form.tenant.clone();
    let mut built = query::form_from_parts(query::RawQueryForm {
        fields: form.field.unwrap_or_default(),
        kinds: form.kind.unwrap_or_default(),
        values: form.value.unwrap_or_default(),
        highs: form.high.unwrap_or_default(),
        order: form.order.unwrap_or_default(),
        direction: form.direction.unwrap_or_else(|| "asc".to_owned()),
        offset: form.offset.unwrap_or_else(|| "0".to_owned()),
        limit: form.limit.unwrap_or_else(|| "25".to_owned()),
        projection: form.projection.unwrap_or_default(),
    });
    if built.collection.trim().is_empty() {
        built.collection = form.collection;
    }
    let outcome = on_blocking(state, move |state| {
        if built.collection.trim().is_empty() {
            return Err("choose a collection".to_owned());
        }
        state.observe(&tenant, Some(&built.collection));
        Ok(query::result_fragment(state, &tenant, &built))
    })
    .await;
    merge(query::RESULT_ID, outcome)
}

#[derive(Deserialize)]
struct TqlForm {
    tenant: String,
    text: String,
}

async fn query_tql(State(state): State<Arc<StudioState>>, Form(form): Form<TqlForm>) -> Response {
    let outcome = on_blocking(state, move |state| {
        state.observe(&form.tenant, None);
        Ok(query::tql_result_fragment(state, &form.tenant, &form.text))
    })
    .await;
    merge(query::RESULT_ID, outcome)
}

/* ------------------------------------------------------------- replication */

#[derive(Deserialize)]
struct RegionForm {
    code: String,
    confirm_target: String,
    confirm_phrase: String,
}

#[derive(Deserialize)]
struct RegionRetireForm {
    code: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn regions_admit(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<RegionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        let code = terrabase::RegionCode::from_text(form.code.trim().to_uppercase().as_str())
            .map_err(|error| format!("`{}` is not a region code: {error:?}", form.code))?;
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            code.as_str(),
            "admit region",
        )?;
        let mut catalog = state.regions.lock().unwrap_or_else(|poison| poison.into_inner());
        catalog
            .admit(code)
            .map_err(|error| format!("admission refused: {error:?}"))
            .map(|()| format!("`{}` admitted; the catalog now holds it as active", code.as_str()))
    })
    .await;
    receipt("region-receipt", outcome)
}

async fn regions_retire(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<RegionRetireForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        let canonical = form.code.trim().to_uppercase();
        let code = terrabase::RegionCode::from_text(&canonical)
            .map_err(|error| format!("`{}` is not a region code: {error:?}", form.code))?;
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            code.as_str(),
            "retire region",
        )?;
        let mut catalog = state.regions.lock().unwrap_or_else(|poison| poison.into_inner());
        catalog
            .retire(code)
            .map_err(|error| format!("retirement refused: {error:?}"))
            .map(|()| format!("`{}` retired from the active set", code.as_str()))
    })
    .await;
    receipt("region-receipt", outcome)
}

#[derive(Deserialize)]
struct TopologyAddForm {
    region: String,
    first: String,
    second: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn topology_add_region(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<TopologyAddForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &form.region,
            "add region",
        )?;
        let mut spec = state.topology();
        let region = replication::region_from_parts(&form.region, &form.first, &form.second)?;
        if spec.regions.iter().any(|existing| existing.name == region.name) {
            return Err(format!("region `{}` is already in the topology", region.name));
        }
        if spec.regions.len() >= terrabase::REGION_CEILING {
            return Err(format!(
                "the topology already has the maximum of {} regions",
                terrabase::REGION_CEILING
            ));
        }
        spec.regions.push(region);
        state.set_topology(spec);
        let view = state.topology_view();
        Ok(if let Err(failure) = view {
            format!("region added and persisted, but the view reports: {failure}")
        } else {
            "region added and the specification persisted".to_owned()
        })
    })
    .await;
    let refresh = on_blocking(refresh_state, |state| Ok(topology_fragment_of(state)))
        .await
        .unwrap_or_default();
    response_with_refresh(TOPOLOGY_TARGET, outcome, refresh)
}

#[derive(Deserialize)]
struct TopologyRemoveForm {
    index: usize,
    confirm_target: String,
    confirm_phrase: String,
}

async fn topology_remove_region(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<TopologyRemoveForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        let spec = state.topology();
        let region = spec
            .regions
            .get(form.index)
            .ok_or_else(|| format!("region {} does not exist", form.index))?;
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &region.name,
            "remove region",
        )?;
        let next = replication::without_region(&spec, form.index)?;
        state.set_topology(next);
        Ok("region removed and the specification persisted".to_owned())
    })
    .await;
    let refresh = on_blocking(refresh_state, |state| Ok(topology_fragment_of(state)))
        .await
        .unwrap_or_default();
    response_with_refresh(TOPOLOGY_TARGET, outcome, refresh)
}

const TOPOLOGY_TARGET: &str = "topology-table";

fn topology_fragment_of(state: &StudioState) -> Vec<String> {
    let spec = state.topology();
    let mut rows = String::new();
    for (index, region) in spec.regions.iter().enumerate() {
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&region.name));
        rows.push_str(&html::cell(&region.members.join(", ")));
        rows.push_str(&html::cell_raw(&format!(
            "<form data-on:submit=\"@post('/replication/topology/remove-region', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"index\" value=\"{index}\">\
{}{}\
<button type=\"submit\" class=\"text-sm text-muted-foreground hover:text-foreground underline\">remove</button>\
</form>",
            html::text_input("confirm_target", "Type region exactly", "", &region.name),
            html::text_input("confirm_phrase", "Type action phrase", "", "remove region"),
        )));
        rows.push_str("</tr>");
    }
    let table = html::table(&["region", "members", ""], &[rows], "No region in the topology.");
    vec![datastar::merge_fragments(
        &format!("<div id=\"{TOPOLOGY_TARGET}\">{table}</div>"),
        Some(&format!("#{TOPOLOGY_TARGET}")),
        MergeMode::Outer,
    )]
}

#[derive(Deserialize)]
struct DurabilityForm {
    level: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn durability_submit(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DurabilityForm>,
) -> Response {
    let level = form.level.unwrap_or_else(|| "regional".to_owned());
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "durability model",
            "submit durability",
        )?;
        let spec = state.topology();
        let inactive = state.inactive_voter();
        replication::submit_durability(
            &spec,
            &level,
            inactive.as_deref(),
            &format!("studio-{}", now_millis()),
        )
    })
    .await;
    receipt(replication::DURABILITY_ID, outcome)
}

async fn durability_toggle_member(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "durability model",
            "toggle inactive voter",
        )?;
        let spec = state.topology();
        let membership = replication::build_membership(&spec)?;
        let members = membership.all_members();
        let current = state.inactive_voter();
        let next = match current.as_deref() {
            None => members.first().map(|member| member.as_str().to_owned()),
            Some(active) => members
                .iter()
                .position(|member| member.as_str() == active)
                .and_then(|position| members.get((position + 1) % members.len()))
                .map(|member| member.as_str().to_owned())
                .filter(|candidate| Some(candidate.as_str()) != current.as_deref()),
        };
        state.set_inactive_voter(next.clone());
        Ok(match next {
            Some(member) => format!("the model now treats `{member}` as inactive"),
            None => "every voter is active again".to_owned(),
        })
    })
    .await;
    receipt(replication::DURABILITY_ID, outcome)
}

/* ------------------------------------------------------------------- tiers */

#[derive(Deserialize)]
struct TierConfigureForm {
    policy_id: String,
    version: String,
    budget: String,
    residence: String,
    promotion: String,
    pinned: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn tiers_configure(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<TierConfigureForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &form.policy_id,
            "configure tier policy",
        )?;
        let policy = tiers::policy_from_parts(
            &form.policy_id,
            &form.version,
            &form.budget,
            &form.residence,
            &form.promotion,
            form.pinned.as_deref().unwrap_or(""),
        )?;
        state
            .engine
            .tiers()
            .configure(policy)
            .map(|policy| {
                format!(
                    "policy `{}` v{} configured with a budget of {}; the pause flag is cleared",
                    policy.policy_id(),
                    policy.version(),
                    html::human_bytes(policy.nvme_budget_bytes())
                )
            })
            .map_err(tiers::render_failure)
    })
    .await;
    receipt("tier-receipt", outcome)
}

#[derive(Deserialize)]
struct ConfirmedActionForm {
    confirm_target: String,
    confirm_phrase: String,
}

async fn tiers_pause(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "tier mover",
            "pause tier mover",
        )?;
        let paused = state.engine.tiers().pause();
        state.record("tiers/pause", format!("paused = {paused}"), true);
        Ok(format!(
            "pause returned {paused}: the mover now refuses real passes until it is resumed"
        ))
    })
    .await;
    receipt("tier-receipt", outcome)
}

async fn tiers_resume(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "tier mover",
            "resume tier mover",
        )?;
        let paused = state.engine.tiers().resume();
        state.record("tiers/resume", format!("paused = {paused}"), true);
        Ok(format!("resume returned {paused}: real passes are accepted again"))
    })
    .await;
    receipt("tier-receipt", outcome)
}

async fn tiers_dry_run(State(state): State<Arc<StudioState>>) -> Response {
    let outcome = on_blocking(state, |state| {
        state
            .engine
            .tiers()
            .dry_run()
            .map(|receipt| {
                state.record("tiers/dry-run", format!("decision {:?}", receipt.decision()), true);
                tiers::render_receipt(&receipt)
            })
            .map_err(tiers::render_failure)
    })
    .await;
    receipt("tier-receipt", outcome)
}

async fn tiers_run(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "tier mover",
            "run tier mover",
        )?;
        state
            .engine
            .tiers()
            .run()
            .map(|receipt| {
                state.record("tiers/run", format!("decision {:?}", receipt.decision()), true);
                tiers::render_receipt(&receipt)
            })
            .map_err(tiers::render_failure)
    })
    .await;
    receipt("tier-receipt", outcome)
}

/* ------------------------------------------------------------------- usage */

#[derive(Deserialize)]
struct UsageRecordForm {
    tenant: String,
    meter: String,
    project: String,
    database: String,
    partition: String,
    region: String,
    fact_id: String,
    integer_units: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn usage_record(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<UsageRecordForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let refresh_tenant = form.tenant.clone();
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "usage ledger",
            "record usage fact",
        )?;
        let result = usage::record_fact(
            state,
            &form.tenant,
            &form.meter,
            &form.project,
            &form.database,
            &form.partition,
            &form.region,
            &form.fact_id,
            &form.integer_units,
            now_millis(),
        );
        state.record(
            "usage/record",
            result.clone().unwrap_or_else(|failure| failure.clone()),
            result.is_ok(),
        );
        result
    })
    .await;
    let refresh = on_blocking(refresh_state, move |state| {
        Ok(usage::authoritative_fragment(state, &refresh_tenant))
    })
    .await;
    let refresh = refresh
        .map(|markup| {
            vec![datastar::merge_fragments(
                &markup,
                Some(&format!("#{}", usage::LEDGER_ID)),
                MergeMode::Outer,
            )]
        })
        .unwrap_or_default();
    response_with_refresh("usage-receipt", outcome, refresh)
}

/* ----------------------------------------------------------------- backups */

#[derive(Deserialize)]
struct BackupRunForm {
    tenants: Option<String>,
    mode: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

#[derive(Deserialize)]
struct BackupConfigureForm {
    bucket: String,
    region: String,
    prefix: String,
    endpoint: Option<String>,
    kms_key_id: Option<String>,
    incremental_interval_minutes: String,
    full_interval_hours: String,
    enabled: Option<String>,
    virtual_hosted_style: Option<String>,
    allow_http: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn backup_configure(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<BackupConfigureForm>,
) -> Response {
    if let Err(failure) = require_confirmation(
        &form.confirm_target,
        &form.confirm_phrase,
        "backup settings",
        "configure backup",
    ) {
        return receipt("backup-configuration-receipt", Err(failure));
    }
    let outcome: Result<String, String> = async {
        let mut settings = S3BackupSettings {
            enabled: form.enabled.is_some(),
            bucket: form.bucket,
            region: form.region,
            prefix: form.prefix,
            endpoint: form.endpoint.unwrap_or_default(),
            virtual_hosted_style: form.virtual_hosted_style.is_some(),
            allow_http: form.allow_http.is_some(),
            incremental_interval_minutes: parse_positive(
                &form.incremental_interval_minutes,
                "incremental interval",
            )?,
            full_interval_hours: parse_positive(&form.full_interval_hours, "full interval")?,
            kms_key_id: form.kms_key_id.unwrap_or_default(),
        };
        settings.normalize();
        settings.validate()?;
        let repository = S3BackupRepository::from_settings(&settings)?;
        let count = repository.check().await?;
        state.set_backup_settings(settings.clone())?;
        let message = format!(
            "S3 connection verified for s3://{}/{}; {count} recovery point(s) found; scheduler {}",
            settings.bucket,
            settings.prefix,
            if settings.enabled { "enabled" } else { "disabled" },
        );
        state.record("backup/configure", message.clone(), true);
        Ok(message)
    }
    .await;
    if let Err(failure) = &outcome {
        state.record("backup/configure", failure.clone(), false);
    }
    receipt("backup-configuration-receipt", outcome)
}

async fn backup_run(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<BackupRunForm>,
) -> Response {
    if let Err(failure) = require_confirmation(
        &form.confirm_target,
        &form.confirm_phrase,
        "backup repository",
        "run backup",
    ) {
        return receipt("backup-receipt", Err(failure));
    }
    let outcome = async {
        let _job = state.backup_job.lock().await;
        let settings = state.backup_settings();
        let repository = S3BackupRepository::from_settings(&settings)?;
        let tenants = backups::parse_tenants(form.tenants.as_deref().unwrap_or(""))?;
        let mode = match form.mode.as_deref() {
            Some("full") => BackupRunMode::Full,
            Some("automatic" | "") | None => BackupRunMode::Automatic,
            Some(_) => return Err("the backup mode is invalid".to_owned()),
        };
        let receipt = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &state.config.backup_scratch_dir(),
            &settings,
            mode,
            tenants,
        )
        .await?;
        let text = format!(
            "recovery point {} committed to S3 ({} document(s), {}, {} object(s) uploaded)",
            &receipt.result.manifest().identity()[..12],
            receipt.result.manifest().documents(),
            html::human_bytes(receipt.result.manifest().total_bytes()),
            receipt.uploaded_objects,
        );
        state.record("backup/run", text, true);
        Ok(receipt)
    }
    .await;
    state.record_backup_run(outcome.is_ok());
    if let Err(failure) = &outcome {
        state.record("backup/run", failure.clone(), false);
    }
    let rendered = outcome.map(|upload| {
        let receipt = backups::backup_receipt(
            &upload.result,
            &upload.destination,
            upload.uploaded_objects,
        );
        format!(
            "{}<pre class=\"mt-3 overflow-x-auto rounded-lg border bg-muted/40 p-4 text-xs leading-relaxed\">{}</pre>",
            html::esc(&format!(
                "Recovery point {} is committed in S3.",
                upload.result.manifest().identity()
            )),
            html::esc(&receipt)
        )
    });
    let chain = backup_chain_fragment(state.as_ref()).await;
    receipt_with_refresh(
        "backup-receipt",
        rendered,
        vec![datastar::merge_fragments(
            &chain,
            Some(&format!("#{}", backups::CHAIN_ID)),
            MergeMode::Inner,
        )],
    )
}

async fn backup_verify(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    if let Err(failure) = require_confirmation(
        &form.confirm_target,
        &form.confirm_phrase,
        "backup repository",
        "verify backup chain",
    ) {
        return receipt("backup-receipt", Err(failure));
    }
    let outcome: Result<String, String> = async {
        let _job = state.backup_job.lock().await;
        let repository = configured_backup_repository(state.as_ref())?;
        let chain = verify_s3_chain(&repository, &state.config.backup_scratch_dir(), None).await?;
        let message = format!(
            "verified {} S3 recovery point(s); every member length and SHA-256 digest matched",
            chain.len(),
        );
        state.record("backup/verify", message.clone(), true);
        Ok(message)
    }
    .await;
    if let Err(failure) = &outcome {
        state.record("backup/verify", failure.clone(), false);
    }
    let chain = backup_chain_fragment(state.as_ref()).await;
    receipt_with_refresh(
        "backup-receipt",
        outcome,
        vec![datastar::merge_fragments(
            &chain,
            Some(&format!("#{}", backups::CHAIN_ID)),
            MergeMode::Inner,
        )],
    )
}

#[derive(Deserialize)]
struct BackupRestoreForm {
    target: String,
    identity: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn backup_restore(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<BackupRestoreForm>,
) -> Response {
    let target = match backups::restore_target(&form.target) {
        Ok(target) => target,
        Err(failure) => return receipt("restore-receipt", Err(failure)),
    };
    if let Err(failure) = require_confirmation(
        &form.confirm_target,
        &form.confirm_phrase,
        &target.display().to_string(),
        "restore recovery point",
    ) {
        return receipt("restore-receipt", Err(failure));
    }
    let outcome: Result<String, String> = async {
        let _job = state.backup_job.lock().await;
        let repository = configured_backup_repository(state.as_ref())?;
        let identity = form
            .identity
            .as_deref()
            .map(str::trim)
            .filter(|text| !text.is_empty())
            .map(str::to_owned);
        let receipt = restore_from_s3(
            &repository,
            &state.config.backup_scratch_dir(),
            target.clone(),
            identity.as_deref(),
        )
        .await?;
        let text = format!(
            "restored {} into {} ({} document(s), {} member byte(s) verified)",
            &receipt.selected_identity()[..12],
            target.display(),
            receipt.restored_documents(),
            receipt.member_bytes_verified(),
        );
        state.record("backup/restore", text.clone(), true);
        Ok(format!(
            "{}<pre class=\"mt-3 overflow-x-auto rounded-lg border bg-muted/40 p-4 text-xs leading-relaxed\">{}</pre>",
            html::esc(&text),
            html::esc(&receipt.render())
        ))
    }
    .await;
    if let Err(failure) = &outcome {
        state.record("backup/restore", failure.clone(), false);
    }
    receipt("restore-receipt", outcome)
}

fn configured_backup_repository(state: &StudioState) -> Result<S3BackupRepository, String> {
    S3BackupRepository::from_settings(&state.backup_settings())
}

async fn load_remote_recovery_points(
    state: &StudioState,
) -> Result<Vec<crate::s3_backup::RemoteRecoveryPoint>, String> {
    configured_backup_repository(state)?.list_recovery_points().await
}

async fn backup_chain_fragment(state: &StudioState) -> String {
    let points = load_remote_recovery_points(state).await;
    backups::chain_fragment(points.as_deref().map_err(String::as_str))
}

fn parse_positive(text: &str, label: &str) -> Result<u64, String> {
    text.trim()
        .parse::<u64>()
        .ok()
        .filter(|value| *value > 0)
        .ok_or_else(|| format!("the {label} must be a positive whole number"))
}

/* ---------------------------------------------------------------- deletion */

#[derive(Deserialize)]
struct DeletionRequestForm {
    request_id: String,
    tenant: String,
    subject: String,
    regions: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn deletion_request(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionRequestForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "request deletion",
        )?;
        let workflow = backups::workflow_from_parts(
            &form.request_id,
            &form.tenant,
            &form.subject,
            &form.regions,
            now_millis(),
        )?;
        let mut ledger = state.deletions.lock().unwrap_or_else(|poison| poison.into_inner());
        ledger
            .submit(workflow)
            .map_err(|error| format!("the ledger refused the workflow: {error:?}"))
    })
    .await;
    let refresh = on_blocking(refresh_state, |state| Ok(backups::deletion_fragment(state)))
        .await
        .map(|markup| vec![fragment(DELETION_TARGET, &markup)])
        .unwrap_or_default();
    response_with_refresh(
        DELETION_TARGET,
        outcome.map(|outcome| format!("the ledger answered {outcome:?}")),
        refresh,
    )
}

#[derive(Deserialize)]
struct DeletionTargetForm {
    request_id: String,
    confirm_target: String,
    confirm_phrase: String,
}

#[derive(Deserialize)]
struct DeletionHoldForm {
    request_id: String,
    confirm_target: String,
    confirm_phrase: String,
}

async fn deletion_admit(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionTargetForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "admit deletion",
        )?;
        with_workflow(state, &form.request_id, |workflow| {
            backups::admit_workflow(workflow, now_millis())
        })
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

async fn deletion_receipt(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionTargetForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "record deletion receipt",
        )?;
        with_workflow(state, &form.request_id, |workflow| {
            backups::record_receipt(workflow, now_millis())
        })
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

async fn deletion_hold(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionHoldForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "add legal hold",
        )?;
        with_workflow(state, &form.request_id, |workflow| backups::add_hold(workflow, now_millis()))
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

async fn deletion_end_hold(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionTargetForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "end deletion hold",
        )?;
        with_workflow(state, &form.request_id, |workflow| backups::end_hold(workflow, now_millis()))
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

async fn deletion_verify(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionTargetForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "verify deletion",
        )?;
        with_workflow(state, &form.request_id, |workflow| {
            backups::verify_workflow(workflow, now_millis())
        })
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

async fn deletion_complete(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<DeletionTargetForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            form.request_id.trim(),
            "complete deletion",
        )?;
        with_workflow(state, &form.request_id, |workflow| {
            backups::complete_workflow(workflow, now_millis())
        })
    })
    .await;
    deletion_response(refresh_state, outcome).await
}

const DELETION_TARGET: &str = "deletion-state";

fn with_workflow<F>(state: &StudioState, request_id: &str, step: F) -> Result<String, String>
where
    F: FnOnce(&mut terrabase::deletion::DeletionWorkflow) -> Result<String, String>,
{
    let mut ledger = state.deletions.lock().unwrap_or_else(|poison| poison.into_inner());
    let id = terrabase::deletion::DeletionRequestId::try_new(request_id.trim())
        .map_err(|error| format!("request id: {error:?}"))?;
    let workflow =
        ledger.get_mut(&id).ok_or_else(|| format!("no deletion workflow named `{request_id}`"))?;
    step(workflow)
}

async fn deletion_response(state: Arc<StudioState>, outcome: Result<String, String>) -> Response {
    let refresh = on_blocking(state, |state| Ok(backups::deletion_fragment(state))).await;
    response_with_refresh(
        DELETION_TARGET,
        outcome,
        refresh.map(|markup| vec![fragment(DELETION_TARGET, &markup)]).unwrap_or_default(),
    )
}

/* --------------------------------------------------------- engine surfaces */

#[derive(Deserialize)]
struct FamilyValidationForm {
    tenant: String,
    collection: String,
    document: Option<String>,
}

#[derive(Deserialize)]
struct FamilySchemaDeclareForm {
    tenant: String,
    collection: String,
    version: String,
    schema: String,
    confirm_target: String,
    confirm_phrase: String,
}

/// Declares one typed schema-validation family policy through a synced
/// Engine transaction. JSON is only the host representation; the engine sees
/// the closed `SchemaDefinition` vocabulary.
async fn family_schema_declare(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<FamilySchemaDeclareForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        let target = format!("{}/{}", form.tenant.trim(), form.collection.trim());
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            &target,
            "declare schema",
        )?;
        let version = form
            .version
            .trim()
            .parse::<u64>()
            .map_err(|_| "schema version is not an unsigned integer".to_owned())?;
        let version = terrabase::families::SchemaVersion::try_new(version)
            .map_err(|error| engine_ops::validation_error("schema version", error))?;
        if form.schema.len() > MAX_SCHEMA_INPUT_BYTES {
            return Err(format!(
                "schema JSON exceeds the Studio limit of {MAX_SCHEMA_INPUT_BYTES} bytes"
            ));
        }
        let parsed: serde_json::Value = serde_json::from_str(&form.schema)
            .map_err(|error| format!("schema JSON does not parse: {error}"))?;
        let definition = terrabase::families::SchemaDefinition::from_json_schema(
            version,
            &crate::json::from_json(parsed),
        )
        .map_err(|error| format!("schema declaration refused: {error:?}"))?;
        let tenant = engine_ops::tenant(&form.tenant)?;
        let collection = engine_ops::collection(&form.collection)?;
        let session = engine_ops::session(state.engine.as_ref(), &tenant)?;
        let operation = state.next_operation()?;
        let deadline = terrabase::Deadline::after(Duration::from_secs(30))
            .map_err(|error| engine_ops::validation_error("schema deadline", error))?;
        let write = terrabase::families::ValidationWrite::try_new(
            session.scope.clone(),
            collection,
            Some(definition),
            operation.clone(),
            deadline,
            terrabase::Cancellation::new(),
        )
        .map_err(|error| engine_ops::validation_error("schema declaration", error))?;
        let mut transaction = session.begin(operation)?;
        state
            .engine
            .families()
            .stage(&mut transaction, terrabase::FamilyWriteRequest::schema_validation_update(write))
            .map_err(|error| format!("schema declaration failed: {error:?}"))?;
        let receipt = transaction.commit(terrabase::LocalDurability::Synced);
        let outcome = match receipt {
            terrabase::CommitOutcome::Committed(receipt) => Ok(format!(
                "schema version declared at sequence {} with {:?} durability",
                receipt.sequence().get(),
                receipt.durability(),
            )),
            terrabase::CommitOutcome::Rejected(error) => {
                Err(format!("schema declaration commit refused: {error:?}"))
            }
            terrabase::CommitOutcome::Unknown(unknown) => {
                Err(format!("schema declaration commit is uncertain: {unknown:?}"))
            }
        };
        state.record(
            "families/schema/declare",
            outcome.clone().unwrap_or_else(|failure| failure.clone()),
            outcome.is_ok(),
        );
        outcome
    })
    .await;
    merge(surfaces::FAMILY_ID, outcome)
}

/// Reads the public schema-validation family through a fresh engine snapshot.
/// This is deliberately read-only; declaration is handled by the adjacent
/// typed declaration route and editor.
async fn family_validation(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<FamilyValidationForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        state.observe(&form.tenant, Some(&form.collection));
        let tenant = engine_ops::tenant(&form.tenant)?;
        let collection = engine_ops::collection(&form.collection)?;
        let document = form
            .document
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(engine_ops::document_id)
            .transpose()?;
        let session = engine_ops::session(state.engine.as_ref(), &tenant)?;
        let deadline = terrabase::Deadline::after(Duration::from_secs(10))
            .map_err(|error| engine_ops::validation_error("family deadline", error))?;
        let read = terrabase::families::ValidationRead::try_new(
            collection,
            document,
            deadline,
            terrabase::Cancellation::new(),
        )
        .map_err(|error| engine_ops::validation_error("family validation", error))?;
        let context = terrabase::FamilyReadContext::try_new(session.scope, session.snapshot)
            .map_err(|error| format!("family context refused: {error:?}"))?;
        let result = state
            .engine
            .families()
            .read(context, terrabase::FamilyReadRequest::validation(read))
            .map_err(|error| format!("family read failed: {error:?}"))?;
        let terrabase::FamilyReadResult::Validation(result) = result else {
            return Err("family returned an unexpected result".to_owned());
        };
        let schema = result
            .policy()
            .map(|policy| crate::json::to_display(&policy.to_json_schema()))
            .unwrap_or_else(|| "null".to_owned());
        let violations = if result.violations().is_empty() {
            html::alert(Tone::Good, "Validation", "No violation was reported for the requested document.")
        } else {
            let mut rows = String::new();
            for violation in result.violations() {
                rows.push_str("<tr class=\"border-b\"><td class=\"px-3 py-2\">");
                rows.push_str(&html::esc(violation.field()));
                rows.push_str("</td><td class=\"px-3 py-2\">");
                rows.push_str(&html::esc(&format!("{:?}", violation.kind())));
                rows.push_str("</td></tr>");
            }
            html::table(&["field", "kind"], &[rows], "No violations.")
        };
        Ok(format!(
            "<div class=\"grid gap-4\"><div class=\"grid gap-4 md:grid-cols-2\"><div><p class=\"text-xs text-muted-foreground\">enforced version</p><p class=\"font-medium\">{}</p></div><div><p class=\"text-xs text-muted-foreground\">collection</p><p class=\"font-medium\">{}</p></div></div><label class=\"grid gap-1.5 text-sm\"><span class=\"font-medium\">JSON Schema projection</span><div data-monaco-json class=\"hidden min-h-48 overflow-hidden rounded-lg border bg-[#0b1020]\"></div><textarea data-monaco-fallback class=\"min-h-48 w-full rounded-lg border bg-[#0b1020] px-4 py-3 font-mono text-xs text-[#d8dee9]\" readonly>{}</textarea><span data-monaco-status class=\"text-xs text-muted-foreground\">Read-only JSON projection.</span></label>{}</div>",
            result.enforced_version().map(|version| version.get().to_string()).unwrap_or_else(|| "schema-free".to_owned()),
            html::esc(result.collection().as_str()),
            html::esc(&schema),
            violations,
        ))
    })
    .await;
    merge(surfaces::FAMILY_ID, outcome)
}

#[derive(Deserialize)]
struct StreamDocumentsForm {
    tenant: String,
    collection: String,
    prefix: Option<String>,
    limit: Option<u32>,
}

/// Opens and serves one bounded document-stream batch. Cursors are intentionally
/// consumed within this request; browser input can never forge a continuation.
async fn stream_documents(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<StreamDocumentsForm>,
) -> Response {
    let outcome = on_blocking(state, move |state| {
        state.observe(&form.tenant, Some(&form.collection));
        let tenant = engine_ops::tenant(&form.tenant)?;
        let collection = engine_ops::collection(&form.collection)?;
        let deadline = terrabase::Deadline::after(Duration::from_secs(10))
            .map_err(|error| engine_ops::validation_error("stream deadline", error))?;
        let cancellation = terrabase::Cancellation::new();
        let mut selector = terrabase::DocumentStreamSelector::try_new(
            collection,
            deadline,
            cancellation.clone(),
        )
        .map_err(|error| engine_ops::validation_error("stream selector", error))?;
        if let Some(prefix) = form.prefix.as_deref().filter(|value| !value.is_empty()) {
            selector = selector
                .with_prefix(prefix)
                .map_err(|error| engine_ops::validation_error("stream prefix", error))?;
        }
        let session = engine_ops::session(state.engine.as_ref(), &tenant)?;
        let source = terrabase::StreamSource::Documents(selector);
        let streams = state.engine.streams();
        let cursor = streams
            .open(&session.scope, &session.snapshot, source.clone())
            .map_err(|error| format!("stream open failed: {error:?}"))?;
        let batch = streams
            .next_batch(
                &session.scope,
                &session.snapshot,
                &source,
                cursor,
                form.limit.unwrap_or(20).clamp(1, 100),
                &cancellation,
            )
            .map_err(|error| format!("stream batch failed: {error:?}"))?;
        let mut rows = String::new();
        for item in batch.items() {
            if let terrabase::StreamItem::Document(document) = item {
                rows.push_str("<tr class=\"border-b\"><td class=\"px-3 py-2\">");
                rows.push_str(&html::esc(document.id().as_str()));
                rows.push_str("</td><td class=\"px-3 py-2\">");
                rows.push_str(&html::esc(&crate::json::to_compact(document.value())));
                rows.push_str("</td></tr>");
            }
        }
        let completion = batch
            .completion()
            .map(|value| format!("{:?}", value))
            .unwrap_or_else(|| "more available".to_owned());
        Ok(format!(
            "<div class=\"grid gap-3\"><p class=\"text-sm text-muted-foreground\">{} item(s); completion: <code>{}</code>; snapshot sequence: <code>{}</code></p>{}</div>",
            batch.items().len(),
            html::esc(&completion),
            session.snapshot.sequence().get(),
            html::table(&["document", "JSON"], &[rows], "The stream returned no documents."),
        ))
    })
    .await;
    merge(surfaces::STREAM_ID, outcome)
}

/* ------------------------------------------------------------- maintenance */

#[derive(Deserialize)]
struct CompactForm {
    policy: Option<String>,
    confirm_target: String,
    confirm_phrase: String,
}

async fn maintenance_compact(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<CompactForm>,
) -> Response {
    let policy = form.policy.unwrap_or_else(|| "reclaim".to_owned());
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        let (compaction_policy, phrase) = match policy.as_str() {
            "reclaim" => (terrabase::CompactionPolicy::Reclaim, "compact reclaim"),
            "reclaim-history" => (
                terrabase::CompactionPolicy::ReclaimHistory,
                "compact reclaim history",
            ),
            _ => return Err("the compaction policy is invalid".to_owned()),
        };
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "local engine",
            phrase,
        )?;
        let request = terrabase::CompactionRequest::new(compaction_policy);
        let receipt = engine_ops::compact(state.engine.as_ref(), request)?;
        let text = format!(
            "WAL {} → {}, journal {} → {}, {} root(s) removed, {} phase(s)",
            html::human_bytes(receipt.wal_bytes_before()),
            html::human_bytes(receipt.wal_bytes_after()),
            html::human_bytes(receipt.journal_bytes_before()),
            html::human_bytes(receipt.journal_bytes_after()),
            receipt.roots_removed(),
            receipt.phases().len(),
        );
        state.record("maintenance/compact", text.clone(), true);
        Ok(format!(
            "<pre class=\"overflow-x-auto rounded-lg border bg-muted/40 p-4 text-xs leading-relaxed\">{}</pre>",
            html::esc(&text)
        ))
    })
    .await;
    let extra = metrics_refresh(&refresh_state).await;
    response_with_refresh(html::RECEIPT_TARGET, outcome, extra)
}

async fn maintenance_maintain_indexes(
    State(state): State<Arc<StudioState>>,
    Form(form): Form<ConfirmedActionForm>,
) -> Response {
    let refresh_state = Arc::clone(&state);
    let outcome = on_blocking(state, move |state| {
        require_confirmation(
            &form.confirm_target,
            &form.confirm_phrase,
            "local engine",
            "maintain indexes",
        )?;
        state.engine.maintain_indexes();
        let text = "one auto-index lifecycle pass ran: idle, superseded, and retired automatic indexes were re-evaluated against the configured thresholds".to_owned();
        state.record("maintenance/maintain-indexes", text.clone(), true);
        Ok(text)
    })
    .await;
    let extra = metrics_refresh(&refresh_state).await;
    response_with_refresh(html::RECEIPT_TARGET, outcome, extra)
}

/* ------------------------------------------------------------- the stream */

async fn metrics_stream(State(state): State<Arc<StudioState>>) -> Response {
    let permit = match Arc::clone(&state.metrics_slots).try_acquire_owned() {
        Ok(permit) => Arc::new(permit),
        Err(_) => {
            let mut response = text("too many metrics streams\n", "text/plain; charset=utf-8");
            *response.status_mut() = StatusCode::TOO_MANY_REQUESTS;
            return response;
        }
    };
    let shutdown = state.shutdown_receiver();
    datastar::periodic(metrics::REFRESH, shutdown, move || {
        let state = Arc::clone(&state);
        let permit = Arc::clone(&permit);
        async move {
            let _permit = permit;
            let snapshot = match on_blocking(state, |state| Ok(metrics::gather(state))).await {
                Ok(snapshot) => snapshot,
                Err(_) => return Vec::new(),
            };
            vec![
                datastar::merge_fragments(
                    &metrics::render_panel(&snapshot),
                    Some(&format!("#{}", metrics::PANEL_ID)),
                    MergeMode::Outer,
                ),
                datastar::merge_signals(&metrics::sequence_signals(&snapshot), false),
            ]
        }
    })
}

/* ---------------------------------------------------------------- helpers */

async fn metrics_refresh(state: &Arc<StudioState>) -> Vec<String> {
    on_blocking(Arc::clone(state), |state| {
        let snapshot = metrics::gather(state);
        Ok(datastar::merge_fragments(
            &metrics::render_panel(&snapshot),
            Some(&format!("#{}", metrics::PANEL_ID)),
            MergeMode::Outer,
        ))
    })
    .await
    .map(|frame| vec![frame])
    .unwrap_or_default()
}

/// Runs engine work on a blocking thread, keeping the executor responsive.
///
/// The engine's calls are synchronous and several of them fsync, so every unit
/// of engine work runs here rather than on an executor thread. Each unit owns
/// its own typed failure as `Result<T, String>`; a panic on the blocking
/// thread surfaces as the string rather than taking the page down.
async fn on_blocking<T, F>(state: Arc<StudioState>, work: F) -> Result<T, String>
where
    F: FnOnce(&StudioState) -> Result<T, String> + Send + 'static,
    T: Send + 'static,
{
    let permit = Arc::clone(&state.blocking_slots)
        .try_acquire_owned()
        .map_err(|_| "Studio is busy; retry after an in-process operation finishes".to_owned())?;
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        work(&state)
    })
    .await
    .map_err(|error| error.to_string())?
}

#[cfg(test)]
#[allow(clippy::items_after_test_module)]
mod tests {
    use super::*;
    use tower::ServiceExt;

    fn studio(name: &str) -> Arc<StudioState> {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-studio-routes-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(),
        ));
        Arc::new(
            StudioState::open(crate::state::Config {
                data_dir: directory,
                addr: crate::state::DEFAULT_ADDR.to_owned(),
            })
            .unwrap(),
        )
    }

    fn post(headers: &[(&str, &str)]) -> Request<Body> {
        let mut builder = Request::builder().method("POST").uri("/");
        for (name, value) in headers {
            builder = builder.header(*name, *value);
        }
        builder.body(Body::empty()).expect("test request")
    }

    fn security() -> SecurityState {
        SecurityState::try_new(crate::state::DEFAULT_ADDR).expect("default listener")
    }

    fn form_component(value: &str) -> String {
        let mut encoded = String::new();
        for byte in value.bytes() {
            if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
                encoded.push(byte as char);
            } else {
                encoded.push_str(&format!("%{byte:02X}"));
            }
        }
        encoded
    }

    #[test]
    fn headerless_requests_are_allowed_for_non_browser_operators() {
        let request = post(&[]);
        assert!(trusted_request_host(&request, &security()));
        assert!(same_origin(&request, &security()));
    }

    #[test]
    fn host_gate_rejects_dns_rebinding_and_wrong_ports() {
        for host in [
            "evil.example:8090",
            "127.0.0.2:8090",
            "127.0.0.1:8091",
            "localhost:8091",
            "user@127.0.0.1:8090",
        ] {
            assert!(!trusted_request_host(&post(&[("host", host)]), &security()));
        }
        for host in ["127.0.0.1:8090", "localhost:8090"] {
            assert!(trusted_request_host(&post(&[("host", host)]), &security()));
        }
    }

    #[test]
    fn same_site_without_origin_is_not_same_origin_proof() {
        assert!(!same_origin(
            &post(&[("host", "127.0.0.1:8090"), ("sec-fetch-site", "same-site"),]),
            &security(),
        ));
    }

    #[test]
    fn matching_origin_and_host_are_allowed() {
        assert!(same_origin(
            &post(&[
                ("host", "127.0.0.1:8090"),
                ("origin", "http://127.0.0.1:8090"),
                ("sec-fetch-site", "same-origin"),
            ]),
            &security(),
        ));
        assert!(same_origin(
            &post(&[("host", "localhost:8090"), ("origin", "http://localhost:8090")]),
            &security(),
        ));
    }

    #[test]
    fn cross_port_and_cross_origin_requests_are_rejected() {
        for (host, origin) in [
            ("127.0.0.1:8090", "http://127.0.0.1:8091"),
            ("127.0.0.1:8090", "http://evil.example:8090"),
            ("evil.example:8090", "http://evil.example:8090"),
            ("127.0.0.2:8090", "http://127.0.0.2:8090"),
            ("localhost:8090", "http://127.0.0.1:8090"),
            ("127.0.0.1:8090", "https://127.0.0.1:8090"),
        ] {
            assert!(!same_origin(&post(&[("host", host), ("origin", origin)]), &security(),));
        }
    }

    #[test]
    fn origin_without_host_is_rejected() {
        assert!(!same_origin(&post(&[("origin", "http://127.0.0.1:8090")]), &security(),));
    }

    #[test]
    fn malformed_origin_is_rejected_instead_of_treated_as_absent() {
        let request = Request::builder()
            .method("POST")
            .uri("/")
            .header("host", "127.0.0.1:8090")
            .header(header::ORIGIN, HeaderValue::from_bytes(&[0xff]).expect("header bytes"))
            .body(Body::empty())
            .expect("test request");
        assert!(!same_origin(&request, &security()));
    }

    #[test]
    fn confirmations_require_exact_normalized_values_without_echoing_input() {
        assert!(
            require_confirmation(
                "demo/users/42",
                "delete document",
                "demo/users/42",
                "delete document",
            )
            .is_ok()
        );
        for (target, phrase) in [
            (" demo/users/42", "delete document"),
            ("demo/users/42\u{2003}", "delete document"),
            ("demo/users/42", "delete document\n"),
            ("demo/users/42", "wrong phrase"),
        ] {
            let error = require_confirmation(target, phrase, "demo/users/42", "delete document")
                .expect_err("invalid confirmation");
            assert!(!error.contains(target));
            assert!(!error.contains(phrase));
        }
        let oversized = "x".repeat(MAX_CONFIRMATION_TARGET_BYTES + 1);
        assert!(
            require_confirmation(&oversized, "delete document", "x", "delete document").is_err()
        );
    }

    #[test]
    fn duplicate_confirmation_detection_decodes_form_field_names() {
        let body = b"confirm_target=one&%63onfirm%5Ftarget=two&confirm_phrase=delete+document";
        assert_eq!(form_field_count(body, CONFIRM_TARGET_FIELD), 2);
        assert_eq!(form_field_count(body, CONFIRM_PHRASE_FIELD), 1);
    }

    #[tokio::test]
    async fn duplicate_confirmations_are_rejected_before_the_action_extractor() {
        let app = router(studio("duplicate-confirmation"));
        for path in [
            "/documents/rows",
            "/documents/read",
            "/documents/put",
            "/documents/delete",
            "/documents/sample",
            "/documents/clear-view",
            "/indexes/create",
            "/indexes/action",
            "/query/run",
            "/query/tql",
            "/replication/regions/admit",
            "/replication/regions/retire",
            "/replication/topology/add-region",
            "/replication/topology/remove-region",
            "/replication/durability/submit",
            "/replication/durability/toggle-member",
            "/tiers/configure",
            "/tiers/pause",
            "/tiers/resume",
            "/tiers/dry-run",
            "/tiers/run",
            "/usage/record",
            "/backup/configure",
            "/backup/run",
            "/backup/verify",
            "/backup/restore",
            "/deletion/request",
            "/deletion/admit",
            "/deletion/receipt",
            "/deletion/hold",
            "/deletion/end-hold",
            "/deletion/verify",
            "/deletion/complete",
            "/maintenance/compact",
            "/maintenance/maintain-indexes",
            "/families/validation",
            "/families/schema/declare",
            "/streams/documents",
        ] {
            let response = app
                .clone()
                .oneshot(
                    Request::builder()
                        .method("POST")
                        .uri(path)
                        .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                        .body(Body::from(
                            "confirm_target=one&confirm_target=two&confirm_phrase=action",
                        ))
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::BAD_REQUEST, "{path}");
            assert_security_headers(&response);
        }
    }

    #[tokio::test]
    async fn cross_origin_duplicate_confirmation_is_rejected_as_csrf_first() {
        let response = router(studio("duplicate-csrf-order"))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/tiers/pause")
                    .header(header::HOST, "127.0.0.1:8090")
                    .header(header::ORIGIN, "https://evil.example:8090")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from("confirm_target=one&confirm_target=two&confirm_phrase=action"))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        assert_security_headers(&response);
    }

    #[tokio::test]
    async fn untrusted_host_cannot_read_studio_pages() {
        let response = router(studio("dns-rebinding-read"))
            .oneshot(
                Request::builder()
                    .method("GET")
                    .uri("/backups")
                    .header(header::HOST, "attacker.example:8090")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        assert_security_headers(&response);
    }

    #[tokio::test]
    async fn every_destructive_form_rejects_missing_typed_confirmation() {
        let app = router(studio("missing-confirmations"));
        let cases = [
            ("/documents/put", "tenant=demo&collection=users&id=one&json=%7B%7D"),
            ("/documents/delete", "tenant=demo&collection=users&id=one"),
            ("/documents/sample", ""),
            (
                "/indexes/create",
                "tenant=demo&name=Users%2FByName&collection=users&fields=name&capabilities=full&priority=normal&lock=unlock",
            ),
            ("/indexes/action", "tenant=demo&name=Users%2FByName&action=delete"),
            ("/replication/regions/admit", "code=AMS"),
            ("/replication/regions/retire", "code=FRA"),
            ("/replication/topology/add-region", "region=fra&first=fra-a&second=fra-b"),
            ("/replication/topology/remove-region", "index=0"),
            ("/replication/durability/submit", "level=regional"),
            ("/replication/durability/toggle-member", ""),
            (
                "/tiers/configure",
                "policy_id=default&version=1&budget=1048576&residence=1&promotion=1",
            ),
            ("/tiers/pause", ""),
            ("/tiers/resume", ""),
            ("/tiers/run", ""),
            (
                "/backup/configure",
                "bucket=backups&region=us-east-1&prefix=studio&incremental_interval_minutes=5&full_interval_hours=24",
            ),
            ("/backup/run", ""),
            ("/backup/verify", ""),
            ("/backup/restore", "target=%2Ftmp%2Fstudio-restore"),
            (
                "/deletion/request",
                "request_id=del-1&tenant=demo&subject=subject-1&regions=gru%2Csfo",
            ),
            ("/deletion/admit", "request_id=del-1"),
            ("/deletion/receipt", "request_id=del-1"),
            ("/deletion/hold", "request_id=del-1"),
            ("/deletion/end-hold", "request_id=del-1"),
            ("/deletion/verify", "request_id=del-1"),
            ("/deletion/complete", "request_id=del-1"),
            ("/maintenance/compact", "policy=reclaim-history"),
            ("/maintenance/maintain-indexes", ""),
            ("/families/schema/declare", "tenant=demo&collection=users&version=1&schema=%7B%7D"),
        ];
        for (path, body) in cases {
            let response = app
                .clone()
                .oneshot(
                    Request::builder()
                        .method("POST")
                        .uri(path)
                        .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                        .body(Body::from(body))
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(
                response.status(),
                StatusCode::UNPROCESSABLE_ENTITY,
                "{path} accepted a form without typed confirmation",
            );
            assert_security_headers(&response);
        }
    }

    #[tokio::test]
    async fn a_wrong_tier_confirmation_has_no_mutation_or_journal_entry() {
        let state = studio("confirmation-no-side-effect");
        let response = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/tiers/pause")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from("confirm_target=wrong&confirm_phrase=pause%20tier%20mover"))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert!(!state.engine.tiers().inspect().paused());
        assert!(state.journal().iter().all(|entry| entry.action != "tiers/pause"));
    }

    #[tokio::test]
    async fn a_wrong_document_replacement_confirmation_cannot_observe_or_write() {
        let state = studio("replacement-confirmation");
        let tenants_before = state.tenants();
        let response = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/documents/put")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(
                        "tenant=unseen&collection=users&id=42&json=%7B%7D&expected=any&version=1&confirm_target=wrong&confirm_phrase=replace%20document",
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(state.tenants(), tenants_before);
        assert!(state.journal().iter().all(|entry| entry.action != "documents/put"));
    }

    #[tokio::test]
    async fn oversized_form_bodies_are_rejected_before_deserialization() {
        let response = router(studio("body-limit"))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/documents/put")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(vec![b'x'; MAX_ACTION_BODY_BYTES + 1]))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::PAYLOAD_TOO_LARGE);
        assert_security_headers(&response);
    }

    #[tokio::test]
    async fn each_page_nonce_is_fresh_and_matches_its_strict_csp() {
        let app = router(studio("csp"));
        let mut observed = Vec::new();
        for _ in 0..2 {
            let response = app
                .clone()
                .oneshot(Request::builder().uri("/documents").body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let policy = response
                .headers()
                .get(header::CONTENT_SECURITY_POLICY)
                .unwrap()
                .to_str()
                .unwrap()
                .to_owned();
            let body = axum::body::to_bytes(response.into_body(), 2 << 20).await.unwrap();
            let body = String::from_utf8(body.to_vec()).unwrap();
            let marker = "data-nonce=\"";
            let start = body.find(marker).expect("page nonce") + marker.len();
            let end = start + body[start..].find('"').expect("nonce terminator");
            let nonce = body[start..end].to_owned();
            assert_eq!(nonce.len(), 32);
            assert!(nonce.bytes().all(|byte| byte.is_ascii_hexdigit()));
            assert!(policy.contains(&format!("script-src 'self' 'nonce-{nonce}'")));
            assert!(policy.contains("worker-src 'self' data: blob:"));
            assert!(policy.contains("object-src 'none'"));
            assert!(!policy.contains("cdn.jsdelivr"));
            assert!(!policy.contains("'unsafe-inline'"));
            assert!(!policy.contains("'unsafe-eval'"));
            observed.push(nonce);
        }
        assert_ne!(observed[0], observed[1]);
    }

    #[tokio::test]
    async fn referenced_browser_runtimes_are_embedded_and_served() {
        let app = router(studio("browser-assets"));
        for path in
            ["/static/datastar-1.0.3.js", "/static/studio-monaco.js", "/static/monaco/vs/loader.js"]
        {
            let response = app
                .clone()
                .oneshot(Request::builder().uri(path).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK, "{path}");
            assert_eq!(
                response.headers().get(header::CONTENT_TYPE).unwrap(),
                "text/javascript; charset=utf-8",
                "{path}",
            );
            assert_eq!(response.headers().get(header::CACHE_CONTROL).unwrap(), "no-cache");
        }
    }

    #[tokio::test]
    async fn metrics_preview_has_a_private_text_route_separate_from_the_ui() {
        let app = router(studio("metrics-boundary"));
        let ui = app
            .clone()
            .oneshot(Request::builder().uri("/metrics").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(ui.status(), StatusCode::OK);
        assert_eq!(ui.headers().get(header::CONTENT_TYPE).unwrap(), "text/html; charset=utf-8");
        assert_security_headers(&ui);
        let ui_body = axum::body::to_bytes(ui.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&ui_body).contains("Metrics"));

        let preview = app
            .oneshot(Request::builder().uri("/metrics/prometheus").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(preview.status(), StatusCode::OK);
        assert_eq!(
            preview.headers().get(header::CONTENT_TYPE).unwrap(),
            "text/plain; version=0.0.4; charset=utf-8"
        );
        assert_security_headers(&preview);
        let preview_body = axum::body::to_bytes(preview.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&preview_body).contains("# HELP terrabase_build_info"));
    }

    fn assert_security_headers(response: &Response) {
        assert_eq!(response.headers().get(header::X_CONTENT_TYPE_OPTIONS).unwrap(), "nosniff");
        assert_eq!(response.headers().get(header::X_FRAME_OPTIONS).unwrap(), "DENY");
        assert_eq!(response.headers().get(header::REFERRER_POLICY).unwrap(), "no-referrer");
        assert!(response.headers().get(header::CONTENT_SECURITY_POLICY).is_some());
    }

    #[tokio::test]
    async fn no_raw_storage_tbep_or_request_editor_routes_exist() {
        let app = router(studio("closed-routes"));
        for path in ["/tbep", "/raw", "/storage/files", "/request", "/shell"] {
            let response = app
                .clone()
                .oneshot(Request::builder().uri(path).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::NOT_FOUND, "{path}");
        }
    }

    #[tokio::test]
    async fn schema_family_declaration_is_confirmed_and_tenant_bound() {
        let app = router(studio("schema-family"));
        let schema = "%7B%22type%22%3A%22object%22%2C%22properties%22%3A%7B%22name%22%3A%7B%22type%22%3A%22string%22%7D%7D%2C%22required%22%3A%5B%22name%22%5D%2C%22x-terrabase-schema-version%22%3A1%7D";
        let declared = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/schema/declare")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=demo&collection=users&version=1&schema={schema}&confirm_target=demo%2Fusers&confirm_phrase=declare%20schema"
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(declared.status(), StatusCode::OK);
        let declared_body = axum::body::to_bytes(declared.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&declared_body).contains("schema version declared"));

        let wrong = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/schema/declare")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=other&collection=users&version=1&schema={schema}&confirm_target=demo%2Fusers&confirm_phrase=declare%20schema"
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        let wrong_body = axum::body::to_bytes(wrong.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&wrong_body).contains("confirmation does not match"));

        let inspected = app
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/validation")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from("tenant=demo&collection=users"))
                    .unwrap(),
            )
            .await
            .unwrap();
        let inspected_body = axum::body::to_bytes(inspected.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&inspected_body).contains("enforced version"));
    }

    #[tokio::test]
    async fn schema_version_mismatch_is_refused_without_mutation() {
        let state = studio("schema-version-mismatch");
        let sequence_before = state.engine.published_sequence().expect("published sequence");
        let schema = serde_json::json!({
            "type": "object",
            "x-terrabase-schema-version": 1,
        })
        .to_string();
        let response = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/schema/declare")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=demo&collection=users&version=2&schema={}&confirm_target=demo%2Fusers&confirm_phrase=declare%20schema",
                        form_component(&schema),
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
        let body = String::from_utf8_lossy(&body);
        assert!(body.contains("VersionMismatch"));
        assert_eq!(state.engine.published_sequence().expect("published sequence"), sequence_before);
        assert!(state.journal().iter().all(|entry| entry.action != "families/schema/declare"));
    }

    #[tokio::test]
    async fn oversized_schema_input_is_refused_before_parsing_or_mutation() {
        let state = studio("schema-input-limit");
        let sequence_before = state.engine.published_sequence().expect("published sequence");
        let schema = format!(
            r#"{{"type":"object","x-terrabase-schema-version":1,"padding":"{}"}}"#,
            "x".repeat(MAX_SCHEMA_INPUT_BYTES),
        );
        let response = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/schema/declare")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=demo&collection=users&version=1&schema={}&confirm_target=demo%2Fusers&confirm_phrase=declare%20schema",
                        form_component(&schema),
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
        let body = String::from_utf8_lossy(&body);
        assert!(body.contains("schema JSON exceeds the Studio limit"));
        assert_eq!(state.engine.published_sequence().expect("published sequence"), sequence_before);
        assert!(state.journal().iter().all(|entry| entry.action != "families/schema/declare"));
    }

    #[tokio::test]
    async fn changed_schema_confirmation_target_is_refused_without_observation_or_write() {
        let state = studio("schema-confirmation-binding");
        let tenants_before = state.tenants();
        let sequence_before = state.engine.published_sequence().expect("published sequence");
        let schema = r#"{"type":"object","x-terrabase-schema-version":1}"#;
        let response = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/families/schema/declare")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=other&collection=users&version=1&schema={}&confirm_target=demo%2Fusers&confirm_phrase=declare%20schema",
                        form_component(schema),
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
        assert!(String::from_utf8_lossy(&body).contains("confirmation does not match"));
        assert_eq!(state.tenants(), tenants_before);
        assert_eq!(state.engine.published_sequence().expect("published sequence"), sequence_before);
        assert!(state.journal().iter().all(|entry| entry.action != "families/schema/declare"));
    }

    #[tokio::test]
    async fn document_stream_route_returns_a_bounded_snapshot_batch() {
        let state = studio("document-stream");
        test_seed_sample(state.as_ref()).expect("sample documents are durable");
        let response = router(state)
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/streams/documents")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from("tenant=demo&collection=users&prefix=sample-&limit=2"))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
        let body = String::from_utf8_lossy(&body);
        assert!(body.contains("sample-000"));
        assert!(body.contains("snapshot sequence"));
    }

    #[tokio::test]
    async fn document_stream_rejects_invalid_prefixes() {
        let state = studio("document-stream-prefix-bounds");
        test_seed_sample(state.as_ref()).expect("sample documents are durable");
        for prefix in ["bad\u{0}".to_owned(), "x".repeat(256)] {
            let response = router(Arc::clone(&state))
                .oneshot(
                    Request::builder()
                        .method("POST")
                        .uri("/streams/documents")
                        .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                        .body(Body::from(format!(
                            "tenant=demo&collection=users&prefix={}&limit=20",
                            form_component(&prefix),
                        )))
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
            assert!(String::from_utf8_lossy(&body).contains("stream prefix"));
        }
    }

    #[tokio::test]
    async fn document_stream_clamps_zero_and_oversized_limits() {
        let state = studio("document-stream-limit-bounds");
        test_seed_sample(state.as_ref()).expect("sample documents are durable");
        for (limit, expected) in [("0", "1 item(s)"), ("10000", "3 item(s)")] {
            let response = router(Arc::clone(&state))
                .oneshot(
                    Request::builder()
                        .method("POST")
                        .uri("/streams/documents")
                        .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                        .body(Body::from(format!(
                            "tenant=demo&collection=users&prefix=sample-&limit={limit}"
                        )))
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let body = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
            assert!(String::from_utf8_lossy(&body).contains(expected));
        }
    }

    #[tokio::test]
    async fn document_stream_escapes_json_before_rendering_html() {
        let state = studio("document-stream-html-escaping");
        let put = router(Arc::clone(&state))
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/documents/put")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from(format!(
                        "tenant=demo&collection=users&id=escaped&json={}&expected=any&version=1&confirm_target=demo%2Fusers%2Fescaped&confirm_phrase=replace%20document",
                        form_component(r#"{"danger":"<tag>&\"quoted\""}"#),
                    )))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(put.status(), StatusCode::OK);
        let stream = router(state)
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/streams/documents")
                    .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
                    .body(Body::from("tenant=demo&collection=users&prefix=escaped&limit=1"))
                    .unwrap(),
            )
            .await
            .unwrap();
        let body = axum::body::to_bytes(stream.into_body(), 1 << 20).await.unwrap();
        let body = String::from_utf8_lossy(&body);
        assert!(body.contains("&lt;tag&gt;&amp;"));
        assert!(body.contains("&quot;danger&quot;"));
        assert!(!body.contains("<tag>"));
    }

    #[tokio::test]
    async fn blocking_work_and_metrics_streams_have_finite_admission() {
        let state = studio("admission");
        let all_work = Arc::clone(&state.blocking_slots)
            .acquire_many_owned(crate::state::MAX_BLOCKING_OPERATIONS as u32)
            .await
            .unwrap();
        let failure = on_blocking(Arc::clone(&state), |_| Ok::<_, String>(())).await.unwrap_err();
        assert!(failure.contains("Studio is busy"));
        drop(all_work);

        let mut streams = Vec::new();
        for _ in 0..crate::state::MAX_METRICS_STREAMS {
            let response = metrics_stream(State(Arc::clone(&state))).await;
            assert_eq!(response.status(), StatusCode::OK);
            streams.push(response);
        }
        let rejected = metrics_stream(State(state)).await;
        assert_eq!(rejected.status(), StatusCode::TOO_MANY_REQUESTS);
        drop(streams);
    }
}

fn fragment(id: &str, markup: &str) -> String {
    datastar::merge_fragments(
        &format!("<div id=\"{id}\">{markup}</div>", id = html::attribute(id), markup = markup),
        Some(&format!("#{id}")),
        MergeMode::Outer,
    )
}

fn merge(id: &str, outcome: Result<String, String>) -> Response {
    let markup = match outcome {
        Ok(markup) => markup,
        Err(failure) => html::alert(Tone::Bad, "The action failed", &failure),
    };
    datastar::response(vec![fragment(id, &markup)])
}

fn receipt(id: &str, outcome: Result<String, String>) -> Response {
    receipt_with_refresh(id, outcome, Vec::new())
}

fn receipt_with_refresh(id: &str, outcome: Result<String, String>, extra: Vec<String>) -> Response {
    response_with_refresh(id, outcome, extra)
}

fn response_with_refresh(
    id: &str,
    outcome: Result<String, String>,
    extra: Vec<String>,
) -> Response {
    let markup = match &outcome {
        Ok(text) => html::alert(Tone::Good, "", text),
        Err(failure) => html::alert(Tone::Bad, "The action was refused", failure),
    };
    let mut frames = vec![fragment(id, &markup)];
    frames.extend(extra);
    datastar::response(frames)
}

fn now_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
