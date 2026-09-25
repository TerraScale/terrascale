//! Shared application assembly, regional storage authority, and HTTP lifecycle.

use std::{
    any::Any,
    error::Error,
    io,
    net::{SocketAddr, ToSocketAddrs},
    sync::Arc,
    time::{Duration, Instant},
};

use auth_contracts::AppError;
use auth_store::{AuthStore, QueryTransport, StoreError};
use axum::{
    Json, Router,
    extract::{Request, State},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::get,
};
use terrascale::{ClientConfig, RegionAffinity, RegionCode, TerraScaleClient};
use tower_http::catch_panic::CatchPanicLayer;
use utoipa::openapi::{
    Info, OpenApi, Paths,
    path::{Parameter, ParameterIn},
    response::Response as OpenApiResponse,
    schema::{Object, Type},
    security::{
        ApiKey, ApiKeyValue, HttpAuthScheme, HttpBuilder, SecurityRequirement, SecurityScheme,
    },
};
use utoipa_scalar::{Scalar, Servable};

use crate::{
    config::AuthOptions,
    features::{
        self,
        email::{EmailSender, LoggingEmailSender, SmtpEmailSender},
        observability::AuthMetrics,
    },
    key_ring::KeyRing,
};

/// Shared, cloneable handler state. Individual services keep their own concurrency policy.
#[derive(Clone)]
pub struct AppState {
    pub options: Arc<AuthOptions>,
    pub store: Arc<AuthStore>,
    pub key_ring: Arc<KeyRing>,
    pub email: Arc<dyn EmailSender>,
    pub http: reqwest::Client,
    pub metrics: Arc<AuthMetrics>,
}

/// Preserve the public 503 envelope when the storage authority is unavailable.
/// Endpoint-specific 4xx decisions remain with the feature that knows the operation.
#[must_use]
pub fn storage_error(error: &StoreError) -> AppError {
    match error {
        StoreError::HttpClient(error)
            if error.is_connect() || error.is_timeout() || error.is_body() =>
        {
            AppError::unavailable()
        }
        StoreError::Http { status, .. } if *status == 421 || *status >= 500 => {
            AppError::unavailable()
        }
        StoreError::Contention | StoreError::Entropy(_) => AppError::unavailable(),
        StoreError::Sdk(error)
            if error.status.is_some_and(|status| status >= 500)
                || matches!(
                    error.kind,
                    terrascale::ErrorKind::Transport
                        | terrascale::ErrorKind::Server
                        | terrascale::ErrorKind::Unavailable
                        | terrascale::ErrorKind::RegionRedirect
                ) =>
        {
            AppError::unavailable()
        }
        _ => AppError::internal(),
    }
}

/// Returns whether a bounded request-body read failed because its byte limit was hit.
/// Axum exposes the underlying `http-body-util::LengthLimitError` as the source.
pub(crate) fn body_limit_exceeded(error: &axum::Error) -> bool {
    std::error::Error::source(error)
        .is_some_and(|source| source.to_string() == "length limit exceeded")
}

/// Build the exact document served by the public OpenAPI endpoint and Scalar.
pub fn build_openapi() -> OpenApi {
    let mut document = OpenApi::new(Info::new("TerraScale Auth API", "v1"), Paths::new());
    for mut feature in features::openapi_documents() {
        // A feature-level default must not become a default for unrelated
        // routes when utoipa merges its document into the public catalog.
        if let Some(security) = feature.security.take() {
            for item in feature.paths.paths.values_mut() {
                for operation in [
                    &mut item.get,
                    &mut item.put,
                    &mut item.post,
                    &mut item.delete,
                    &mut item.options,
                    &mut item.head,
                    &mut item.patch,
                    &mut item.trace,
                ]
                .into_iter()
                .flatten()
                {
                    operation.security.get_or_insert_with(|| security.clone());
                }
            }
        }
        document.merge(feature);
    }
    complete_openapi(&mut document);
    document
}

/// Manual feature catalog entries need a response and path-parameter schema
/// to satisfy the OpenAPI operation contract. Specific feature annotations win.
fn complete_openapi(document: &mut OpenApi) {
    let components = document.components.get_or_insert_with(Default::default);
    components.add_security_scheme(
        "SessionCookie",
        SecurityScheme::ApiKey(ApiKey::Cookie(ApiKeyValue::new(
            features::ui::SESSION_COOKIE_NAME,
        ))),
    );
    components.add_security_scheme(
        "MetricsKey",
        SecurityScheme::ApiKey(ApiKey::Header(ApiKeyValue::new("X-TerraScale-Metrics-Key"))),
    );
    components.add_security_scheme(
        "OAuthAccessBearer",
        SecurityScheme::Http(
            HttpBuilder::new()
                .scheme(HttpAuthScheme::Bearer)
                .bearer_format("JWT")
                .build(),
        ),
    );
    for (path, item) in &mut document.paths.paths {
        for name in path
            .split('/')
            .filter_map(|segment| segment.strip_prefix('{')?.strip_suffix('}'))
        {
            let parameters = item.parameters.get_or_insert_with(Vec::new);
            if !parameters.iter().any(|parameter| {
                parameter.name == name && parameter.parameter_in == ParameterIn::Path
            }) {
                let mut parameter = Parameter::new(name);
                parameter.schema = Some(Object::with_type(Type::String).into());
                parameters.push(parameter);
            }
        }
        for operation in [
            &mut item.get,
            &mut item.put,
            &mut item.post,
            &mut item.delete,
            &mut item.options,
            &mut item.head,
            &mut item.patch,
            &mut item.trace,
        ]
        .into_iter()
        .flatten()
        {
            // The management key gate covers every instance-scoped API route.
            // OAuth access tokens, hosted session cookies, and operator scrape
            // credentials are distinct and must not inherit that requirement.
            let scheme = if path.starts_with("/v1/{slug}/") {
                Some("ManagementBearer")
            } else if path.starts_with("/ui/{slug}/admin") {
                Some("SessionCookie")
            } else if path == "/metrics" {
                Some("MetricsKey")
            } else if path == "/{slug}/oauth/userinfo" {
                Some("OAuthAccessBearer")
            } else {
                None
            };
            if let Some(scheme) = scheme {
                operation.security.get_or_insert_with(|| {
                    vec![SecurityRequirement::new(scheme, Vec::<String>::new())]
                });
            }
            if operation.responses.responses.is_empty() {
                operation.responses.responses.insert(
                    "default".into(),
                    OpenApiResponse::new("See the endpoint response status and body.").into(),
                );
            }
        }
    }
}

/// Compose feature routes, shared policy, OpenAPI, and Scalar for the real server.
pub fn build_router(state: AppState) -> Router {
    let document = build_openapi();
    let scalar = Scalar::with_url("/scalar", document.clone());
    Router::new()
        .merge(features::router())
        .route(
            "/openapi/v1.json",
            get(move || {
                let document = document.clone();
                async move { Json(document) }
            }),
        )
        .merge(Router::<AppState>::from(scalar))
        .layer(middleware::from_fn_with_state(
            state.clone(),
            features::management::management_gate,
        ))
        .layer(middleware::from_fn_with_state(
            state.clone(),
            features::oauth::cors::cors,
        ))
        .layer(CatchPanicLayer::custom(panic_envelope))
        .layer(middleware::from_fn_with_state(
            state.clone(),
            record_request,
        ))
        .with_state(state)
}

fn panic_envelope(_panic: Box<dyn Any + Send>) -> Response {
    tracing::error!("request handler panicked");
    AppError::internal().into_response()
}

async fn record_request(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let path = request.uri().path();
    let is_metrics = path == "/metrics" || path.starts_with("/metrics/");
    let method = request.method().as_str().to_owned();
    let started = Instant::now();
    let response = next.run(request).await;
    if !is_metrics {
        state
            .metrics
            .record_request(&method, response.status().as_u16(), started.elapsed());
    }
    response
}

/// Build state with one GRU storage authority for all auth replicas.
pub fn build_state(options: AuthOptions) -> Result<AppState, Box<dyn Error>> {
    build_state_with_mode(options, is_development())
}

/// Construct state for an explicitly selected environment. The server uses
/// `build_state` so process configuration controls its production guard.
pub fn build_state_with_mode(
    options: AuthOptions,
    development: bool,
) -> Result<AppState, Box<dyn Error>> {
    let production = !development;
    if production && options.audit_signing_key.is_none() {
        return Err(invalid("TS_AUTH_AUDIT_SIGNING_KEY is required outside Development").into());
    }
    if production && options.metrics_key.is_none() {
        return Err(invalid("TS_AUTH_METRICS_KEY is required outside Development").into());
    }

    let storage_url = reqwest::Url::parse(&options.terrascale_api_url)?;
    let host = storage_url
        .host_str()
        .ok_or_else(|| invalid("TerraScale API URL needs a host"))?;
    let loopback = matches!(host, "localhost" | "127.0.0.1" | "::1");
    if production && loopback {
        return Err(
            invalid("Production auth storage cannot use a loopback TerraScale API URL").into(),
        );
    }
    if !loopback
        && (storage_url.scheme() != "https"
            || !matches!(host, "api.terrascale.tech" | "gru.api.terrascale.tech"))
    {
        return Err(invalid(
            "Global auth storage requires the HTTPS TerraScale API origin or GRU authority origin",
        )
        .into());
    }

    let mut builder = ClientConfig::builder()
        .api_base_url(options.terrascale_api_url.clone())
        .api_key(options.terrascale_api_key.clone())
        .max_retry_attempts(2);
    if !loopback {
        builder = builder
            .preferred_region(RegionCode::Gru)
            .region_affinity(RegionAffinity::Strict);
    }
    if let Some(project) = &options.terrascale_project_id {
        builder = builder.project_id(project.clone());
    }
    if let Some(tenant) = &options.terrascale_tenant_id {
        builder = builder.tenant_id(tenant.clone());
    }
    let client = TerraScaleClient::new(builder.build()?);

    // The narrow query adapter shares the same authority and credentials as SDK reads/writes.
    let normalized_key = normalize_api_key(&options.terrascale_api_key);
    let query = QueryTransport::new(
        &options.terrascale_api_url,
        normalized_key,
        options.terrascale_project_id.clone(),
        options.terrascale_tenant_id.clone(),
    )?;
    let store = Arc::new(AuthStore::new(
        client,
        query,
        options.database.clone(),
        options.audit_signing_key.clone(),
        options.audit_verification_keys.clone(),
    ));
    let key_ring = Arc::new(KeyRing::new(store.clone(), options.master_key.clone()));
    let email: Arc<dyn EmailSender> = match (
        &options.smtp_host,
        &options.smtp_username,
        &options.smtp_password,
        &options.smtp_from_address,
    ) {
        (Some(host), Some(username), Some(password), Some(from)) => Arc::new(SmtpEmailSender::new(
            host.clone(),
            options.smtp_port,
            username.clone(),
            password.clone(),
            from.clone(),
        )),
        _ => Arc::new(LoggingEmailSender),
    };
    let http = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(15))
        .build()?;
    Ok(AppState {
        options: Arc::new(options),
        store,
        key_ring,
        email,
        http,
        metrics: Arc::new(AuthMetrics::new()),
    })
}

fn normalize_api_key(raw: &str) -> String {
    let raw = raw.trim();
    if raw
        .as_bytes()
        .get(..6)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"bearer"))
    {
        let rest = &raw[6..];
        if rest.chars().next().is_some_and(char::is_whitespace) {
            return rest.trim().to_owned();
        }
    }
    raw.to_owned()
}

fn is_development() -> bool {
    std::env::var("TS_AUTH_ENVIRONMENT")
        .is_ok_and(|value| value.eq_ignore_ascii_case("Development"))
}

/// Start the HTTP listener after all required configuration has been validated.
pub async fn serve(options: AuthOptions) -> Result<(), Box<dyn Error>> {
    let listen = parse_listen(&options.listen)?;
    let state = build_state(options)?;
    let app = build_router(state.clone());
    let listener = tokio::net::TcpListener::bind(listen).await?;
    let (shutdown_tx, shutdown_rx) = tokio::sync::oneshot::channel::<()>();
    let dispatcher = tokio::spawn(features::webhooks::run_dispatcher(state, async move {
        let _ = shutdown_rx.await;
    }));
    tracing::info!(address = %listener.local_addr()?, "ts-auth listening");
    let server_result = axum::serve(
        listener,
        app.into_make_service_with_connect_info::<SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown_signal())
    .await;
    let _ = shutdown_tx.send(());
    if let Err(error) = dispatcher.await {
        tracing::error!(%error, "webhook dispatcher stopped unexpectedly");
    }
    server_result?;
    Ok(())
}

async fn shutdown_signal() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{SignalKind, signal};
        match signal(SignalKind::terminate()) {
            Ok(mut terminate) => tokio::select! {
                result = tokio::signal::ctrl_c() => {
                    if let Err(error) = result { tracing::warn!(%error, "failed to wait for Ctrl-C"); }
                }
                _ = terminate.recv() => {}
            },
            Err(error) => {
                tracing::warn!(%error, "failed to install SIGTERM handler");
                let _ = tokio::signal::ctrl_c().await;
            }
        }
    }
    #[cfg(not(unix))]
    if let Err(error) = tokio::signal::ctrl_c().await {
        tracing::warn!(%error, "failed to wait for Ctrl-C");
    }
}

fn parse_listen(raw: &str) -> io::Result<SocketAddr> {
    let authority = raw
        .strip_prefix("http://")
        .ok_or_else(|| invalid("TS_AUTH_LISTEN must be a plain HTTP listen URL"))?;
    if authority.contains('/')
        || authority.contains('?')
        || authority.contains('#')
        || authority.contains('@')
    {
        return Err(invalid(
            "TS_AUTH_LISTEN must contain only an address and port",
        ));
    }
    let authority = authority
        .strip_prefix("+:")
        .or_else(|| authority.strip_prefix("*:"))
        .map(|port| format!("0.0.0.0:{port}"))
        .unwrap_or_else(|| authority.to_owned());
    authority
        .to_socket_addrs()
        .map_err(|_| invalid("TS_AUTH_LISTEN must use a host and port"))?
        .next()
        .ok_or_else(|| invalid("TS_AUTH_LISTEN resolved to no listen address"))
}

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message.into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn listen_accepts_wildcard_and_rejects_unserved_https() {
        assert_eq!(
            parse_listen("http://+:8080").unwrap().to_string(),
            "0.0.0.0:8080"
        );
        assert!(parse_listen("https://0.0.0.0:8080").is_err());
        assert!(parse_listen("http://0.0.0.0:8080/path").is_err());
    }

    #[test]
    fn merged_openapi_has_responses_and_declares_templated_paths() {
        let document = build_openapi();
        for (path, item) in &document.paths.paths {
            for name in path
                .split('/')
                .filter_map(|segment| segment.strip_prefix('{')?.strip_suffix('}'))
            {
                assert!(
                    item.parameters
                        .as_ref()
                        .is_some_and(|parameters| parameters.iter().any(|parameter| parameter
                            .name
                            == name
                            && parameter.parameter_in == ParameterIn::Path
                            && parameter.schema.is_some())),
                    "missing path parameter {name} in {path}"
                );
            }
            for operation in [
                &item.get,
                &item.put,
                &item.post,
                &item.delete,
                &item.options,
                &item.head,
                &item.patch,
                &item.trace,
            ]
            .into_iter()
            .flatten()
            {
                assert!(
                    !operation.responses.responses.is_empty(),
                    "missing response in {path}"
                );
            }
        }
    }

    #[test]
    fn merged_openapi_scopes_security_to_the_actual_caller() {
        use serde_json::json;

        let document = serde_json::to_value(build_openapi()).expect("OpenAPI serializes");
        assert!(document.get("security").is_none());

        let security =
            |path: &str, method: &str| document["paths"][path][method]["security"].clone();
        assert_eq!(
            security("/v1/instances", "post"),
            json!([{"OperatorBearer": []}])
        );
        assert_eq!(
            security("/v1/{slug}/users", "get"),
            json!([{"ManagementBearer": []}])
        );
        assert_eq!(
            security("/v1/{slug}/billing", "get"),
            json!([{"ManagementBearer": []}])
        );
        assert_eq!(
            security("/v1/{slug}/organizations", "post"),
            json!([{"bearerAuth": []}])
        );
        assert_eq!(
            security("/scim/{directory_id}/Users", "get"),
            json!([{"bearerAuth": []}])
        );
        assert_eq!(
            security("/ui/{slug}/admin", "get"),
            json!([{"SessionCookie": []}])
        );
        assert_eq!(security("/metrics", "get"), json!([{"MetricsKey": []}]));
        assert_eq!(
            security("/{slug}/oauth/userinfo", "get"),
            json!([{"OAuthAccessBearer": []}])
        );
        for (path, method) in [
            ("/healthz", "get"),
            ("/{slug}/.well-known/openid-configuration", "get"),
            ("/admin/{asset}", "get"),
            ("/v1/billing/webhook", "post"),
        ] {
            assert!(
                security(path, method).is_null(),
                "{method} {path} should be public"
            );
        }
        let token_security = security("/{slug}/oauth/token", "post");
        assert!(token_security.as_array().is_some_and(|alternatives| {
            alternatives
                .iter()
                .any(|option| option.get("clientBasic").is_some())
                && alternatives.iter().any(|option| option == &json!({}))
        }));

        let schemes = &document["components"]["securitySchemes"];
        assert_eq!(schemes["SessionCookie"]["name"], "tsauth_session");
        assert_eq!(schemes["SessionCookie"]["in"], "cookie");
        assert_eq!(schemes["MetricsKey"]["name"], "X-TerraScale-Metrics-Key");
        assert_eq!(schemes["MetricsKey"]["in"], "header");
        assert_eq!(schemes["OAuthAccessBearer"]["scheme"], "bearer");

        for (path, item) in document["paths"].as_object().expect("paths object") {
            if !path.starts_with("/v1/{slug}/") {
                continue;
            }
            for method in [
                "get", "put", "post", "delete", "options", "head", "patch", "trace",
            ] {
                if item.get(method).is_none() {
                    continue;
                }
                let alternatives = security(path, method);
                assert!(
                    alternatives.as_array().is_some_and(|items| {
                        items.len() == 1
                            && (items[0] == json!({"ManagementBearer": []})
                                || items[0] == json!({"bearerAuth": []}))
                    }),
                    "{method} {path} must require only its instance API key: {alternatives}"
                );
            }
        }
    }

    #[test]
    fn storage_transport_failures_keep_the_unavailable_envelope() {
        let unavailable = StoreError::Sdk(Box::new(terrascale::Error::transport("offline")));
        assert_eq!(
            storage_error(&unavailable).status,
            axum::http::StatusCode::SERVICE_UNAVAILABLE
        );
        let invalid = StoreError::InvalidConfiguration("bad query".into());
        assert_eq!(
            storage_error(&invalid).status,
            axum::http::StatusCode::INTERNAL_SERVER_ERROR
        );
        assert_eq!(
            storage_error(&StoreError::Contention).status,
            axum::http::StatusCode::SERVICE_UNAVAILABLE
        );
    }
}
