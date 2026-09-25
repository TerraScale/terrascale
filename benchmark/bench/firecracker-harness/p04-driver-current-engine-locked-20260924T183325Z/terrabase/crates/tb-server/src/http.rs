use std::convert::Infallible;
use std::io;
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::extract::State;
use axum::http::header::{ALLOW, CONNECTION, CONTENT_LENGTH, CONTENT_TYPE, TRANSFER_ENCODING};
use axum::http::{HeaderValue, Method, Request, Response, StatusCode};
use axum::routing::get;
use hyper::body::Incoming;
use hyper::server::conn::http1;
use hyper_util::rt::{TokioIo, TokioTimer};
use hyper_util::server::graceful::{GracefulShutdown, Watcher};
use hyper_util::service::TowerToHyperService;
use metrics_exporter_prometheus::{PrometheusBuilder, PrometheusHandle};
use tb_core::MemoryCategory;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{Mutex, OwnedSemaphorePermit, Semaphore, oneshot};
use tokio::task::JoinSet;
use tower::ServiceExt;
use tower::limit::GlobalConcurrencyLimitLayer;
use tower::service_fn;

use crate::{LocalServerState, MemoryMetricsSnapshot};

/// Maximum accepted sockets, including connections waiting on request headers.
const MAX_CONNECTIONS: usize = 32;
/// Maximum concurrently running admin handlers.
const MAX_IN_FLIGHT_HANDLERS: usize = 16;
/// Maximum bytes buffered by Hyper while parsing one request.
const MAX_HTTP_BUFFER_BYTES: usize = 16 * 1024;
/// Maximum number of request headers accepted on one connection.
const MAX_HTTP_HEADERS: usize = 32;
/// Maximum time allowed to finish sending a request header block.
const HTTP_HEADER_TIMEOUT: Duration = Duration::from_secs(5);
/// Maximum time allowed for accepted requests to drain after shutdown.
const SHUTDOWN_DRAIN_TIMEOUT: Duration = Duration::from_secs(10);
/// Periodic Prometheus registry maintenance interval.
const METRICS_UPKEEP_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Clone)]
struct HttpState {
    server: Arc<LocalServerState>,
    prometheus: Arc<PrometheusHandle>,
    metrics_scrape: Arc<Mutex<()>>,
}

/// Install the one process-wide Prometheus recorder used by the local server.
///
/// The server renders the recorder through its loopback-only `/metrics` route;
/// this function does not start an additional listener.
///
/// # Errors
///
/// Returns a readable error if another recorder is already installed or the
/// Prometheus recorder cannot be initialized.
pub fn install_prometheus_recorder() -> Result<PrometheusHandle, String> {
    PrometheusBuilder::new()
        .install_recorder()
        .map_err(|error| format!("could not install the Prometheus recorder: {error}"))
}

/// Serve the loopback administrative routes until shutdown, then drain in-flight work.
///
/// The listener is supplied by the caller so it can bind before opening local
/// storage. Each accepted socket consumes a connection slot before `accept`,
/// and connections are closed after one HTTP request.
///
/// # Errors
///
/// Returns listener failures or a panicked connection task after draining any
/// remaining accepted connections.
pub async fn serve(
    listener: TcpListener,
    server: Arc<LocalServerState>,
    prometheus: PrometheusHandle,
    mut shutdown: oneshot::Receiver<()>,
) -> Result<(), HttpServerError> {
    let address = listener
        .local_addr()
        .map_err(HttpServerError::ListenerAddress)?;
    if !matches!(address, SocketAddr::V4(address) if address.ip() == &Ipv4Addr::LOCALHOST) {
        return Err(HttpServerError::NonLoopbackListener(address));
    }

    let upkeep_handle = prometheus.clone();
    let app = router(HttpState {
        server: Arc::clone(&server),
        prometheus: Arc::new(prometheus),
        metrics_scrape: Arc::new(Mutex::new(())),
    });
    let (stop_upkeep, mut stop_upkeep_receiver) = oneshot::channel();
    let upkeep_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(METRICS_UPKEEP_INTERVAL);
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            tokio::select! {
                _ = &mut stop_upkeep_receiver => break,
                _ = interval.tick() => upkeep_handle.run_upkeep(),
            }
        }
    });
    let connection_slots = Arc::new(Semaphore::new(MAX_CONNECTIONS));
    let mut connections = JoinSet::new();
    let graceful_shutdown = GracefulShutdown::new();
    let mut fatal_error = None;

    'accepting: loop {
        let permit = tokio::select! {
            biased;
            _ = &mut shutdown => {
                break 'accepting;
            }
            completed = connections.join_next(), if !connections.is_empty() => {
                if let Some(Err(error)) = completed {
                    server.mark_failed();
                    fatal_error = Some(HttpServerError::ConnectionTask(error));
                    break 'accepting;
                }
                continue 'accepting;
            }
            permit = Arc::clone(&connection_slots).acquire_owned() => {
                let Ok(permit) = permit else {
                    server.mark_failed();
                    fatal_error = Some(HttpServerError::ConnectionSlotsClosed);
                    break 'accepting;
                };
                permit
            }
        };

        let accepted = tokio::select! {
            biased;
            _ = &mut shutdown => {
                drop(permit);
                break 'accepting;
            }
            accepted = listener.accept() => accepted,
        };

        match accepted {
            Ok((stream, _peer)) => {
                let app = app.clone();
                let watcher = graceful_shutdown.watcher();
                connections.spawn(async move {
                    serve_connection(stream, app, permit, watcher).await;
                });
            }
            Err(error) if error.kind() == io::ErrorKind::Interrupted => {
                drop(permit);
            }
            Err(error) => {
                server.mark_failed();
                fatal_error = Some(HttpServerError::Accept(error));
                break 'accepting;
            }
        }
    }

    // Stop readiness before waiting on any already accepted request.
    server.begin_draining();
    drain_connections(&mut connections, &server, graceful_shutdown).await;
    server.mark_stopped();
    let _ = stop_upkeep.send(());
    upkeep_task.abort();

    match fatal_error {
        Some(error) => Err(error),
        None => Ok(()),
    }
}

fn router(state: HttpState) -> Router {
    router_with_handler_slots(state, Arc::new(Semaphore::new(MAX_IN_FLIGHT_HANDLERS)))
}

fn router_with_handler_slots(state: HttpState, slots: Arc<Semaphore>) -> Router {
    let routes = Router::new()
        .route("/healthz", get(healthz))
        .route("/readyz", get(readyz))
        .route("/metrics", get(metrics))
        .with_state(state);

    apply_handler_limit(routes, slots)
}

fn apply_handler_limit(routes: Router, slots: Arc<Semaphore>) -> Router {
    routes.layer(GlobalConcurrencyLimitLayer::with_semaphore(slots))
}

async fn healthz(State(state): State<HttpState>) -> Response<Body> {
    if state.server.is_live() {
        text_response(StatusCode::OK, "ok\n")
    } else {
        text_response(StatusCode::SERVICE_UNAVAILABLE, "unavailable\n")
    }
}

async fn readyz(State(state): State<HttpState>) -> Response<Body> {
    if state.server.is_ready() {
        text_response(StatusCode::OK, "ready\n")
    } else {
        text_response(StatusCode::SERVICE_UNAVAILABLE, "unavailable\n")
    }
}

async fn metrics(State(state): State<HttpState>) -> Response<Body> {
    // MemoryBudget::snapshot performs bounded attribution aggregation. Keep
    // scrapes serial so multiple clients cannot multiply that temporary work.
    let _scrape = state.metrics_scrape.lock().await;
    let snapshot = state.server.memory_metrics();
    record_metrics(&state, &snapshot);

    let mut response = Response::new(Body::from(state.prometheus.render()));
    response.headers_mut().insert(
        CONTENT_TYPE,
        HeaderValue::from_static("text/plain; version=0.0.4; charset=utf-8"),
    );
    response
}

fn record_metrics(state: &HttpState, snapshot: &MemoryMetricsSnapshot) {
    metrics::gauge!("terrabase_server_live").set(f64::from(u8::from(state.server.is_live())));
    metrics::gauge!("terrabase_server_ready").set(f64::from(u8::from(state.server.is_ready())));
    metrics::gauge!("terrabase_process_memory_limit_bytes")
        .set(metric_value(snapshot.process_limit_bytes()));
    metrics::gauge!("terrabase_process_memory_headroom_bytes")
        .set(metric_value(snapshot.headroom_bytes()));
    metrics::gauge!("terrabase_process_memory_usable_limit_bytes")
        .set(metric_value(snapshot.usable_limit_bytes()));
    metrics::gauge!("terrabase_memory_reserved_bytes").set(metric_value(snapshot.reserved_bytes()));
    metrics::gauge!("terrabase_memory_reserved_items").set(metric_value(snapshot.reserved_items()));

    for (category, bytes) in MemoryCategory::ALL.into_iter().zip(snapshot.categories()) {
        let category = MemoryMetricsSnapshot::category_label(category);
        metrics::gauge!("terrabase_memory_category_reserved_bytes", "category" => category)
            .set(metric_value(*bytes));
    }
}

#[allow(clippy::cast_precision_loss)]
fn metric_value(value: usize) -> f64 {
    // Prometheus samples are f64, so byte counts above 2^53 are approximate.
    u64::try_from(value).unwrap_or(u64::MAX) as f64
}

async fn serve_connection(
    stream: TcpStream,
    app: Router,
    _connection_slot: OwnedSemaphorePermit,
    watcher: Watcher,
) {
    let service = service_fn(move |request: Request<Incoming>| {
        let app = app.clone();
        async move {
            let response = if let Some(status) = rejected_body_status(request.headers()) {
                close_response(status)
            } else if request.method() != Method::GET {
                let mut response = close_response(StatusCode::METHOD_NOT_ALLOWED);
                response
                    .headers_mut()
                    .insert(ALLOW, HeaderValue::from_static("GET"));
                response
            } else {
                app.oneshot(request.map(Body::new)).await?
            };
            Ok::<_, Infallible>(response)
        }
    });

    let mut builder = http1::Builder::new();
    builder
        .keep_alive(false)
        .max_buf_size(MAX_HTTP_BUFFER_BYTES)
        .max_headers(MAX_HTTP_HEADERS)
        .timer(TokioTimer::new())
        .header_read_timeout(HTTP_HEADER_TIMEOUT);

    let connection =
        builder.serve_connection(TokioIo::new(stream), TowerToHyperService::new(service));
    if let Err(error) = watcher.watch(connection).await {
        tracing::debug!(%error, "administrative HTTP connection closed with an error");
    }
}

fn rejected_body_status(headers: &axum::http::HeaderMap) -> Option<StatusCode> {
    if headers.contains_key(TRANSFER_ENCODING) {
        return Some(StatusCode::PAYLOAD_TOO_LARGE);
    }

    for value in headers.get_all(CONTENT_LENGTH) {
        let Ok(value) = value.to_str() else {
            return Some(StatusCode::BAD_REQUEST);
        };
        let Ok(length) = value.parse::<u64>() else {
            return Some(StatusCode::BAD_REQUEST);
        };
        if length != 0 {
            return Some(StatusCode::PAYLOAD_TOO_LARGE);
        }
    }

    None
}

fn text_response(status: StatusCode, text: &'static str) -> Response<Body> {
    let mut response = Response::new(Body::from(text));
    *response.status_mut() = status;
    response.headers_mut().insert(
        CONTENT_TYPE,
        HeaderValue::from_static("text/plain; charset=utf-8"),
    );
    response
}

fn close_response(status: StatusCode) -> Response<Body> {
    let mut response = text_response(status, "request rejected\n");
    response
        .headers_mut()
        .insert(CONNECTION, HeaderValue::from_static("close"));
    response
}

async fn drain_connections(
    connections: &mut JoinSet<()>,
    server: &LocalServerState,
    graceful_shutdown: GracefulShutdown,
) {
    let deadline = tokio::time::sleep(SHUTDOWN_DRAIN_TIMEOUT);
    tokio::pin!(deadline);
    let graceful = graceful_shutdown.shutdown();
    tokio::pin!(graceful);

    while !connections.is_empty() {
        tokio::select! {
            biased;
            () = &mut deadline => {
                tracing::warn!("administrative HTTP shutdown drain timed out; aborting remaining connections");
                connections.abort_all();
                while connections.join_next().await.is_some() {}
                break;
            }
            () = &mut graceful => {
                while let Some(completed) = connections.join_next().await {
                    if let Err(error) = completed {
                        tracing::error!(%error, "administrative HTTP connection task failed during shutdown");
                        server.mark_failed();
                    }
                }
                break;
            }
            completed = connections.join_next() => {
                if let Some(Err(error)) = completed {
                    tracing::error!(%error, "administrative HTTP connection task failed during shutdown");
                    server.mark_failed();
                }
            }
        }
    }
}

/// Listener, semaphore, or connection-task failure from the HTTP service loop.
#[derive(Debug, thiserror::Error)]
pub enum HttpServerError {
    /// The supplied listener address could not be inspected.
    #[error("could not inspect the administrative HTTP listener address: {0}")]
    ListenerAddress(#[source] io::Error),
    /// The HTTP service was given a listener other than IPv4 127.0.0.1.
    #[error("administrative HTTP listener must bind to 127.0.0.1, got {0}")]
    NonLoopbackListener(SocketAddr),
    /// Accepting a new loopback connection failed.
    #[error("failed to accept an administrative HTTP connection: {0}")]
    Accept(#[source] io::Error),
    /// A connection task panicked or was cancelled outside normal shutdown.
    #[error("an administrative HTTP connection task failed: {0}")]
    ConnectionTask(#[source] tokio::task::JoinError),
    /// The private connection-slot semaphore was unexpectedly closed.
    #[error("administrative HTTP connection slots were closed unexpectedly")]
    ConnectionSlotsClosed,
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use axum::http::HeaderMap;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    use super::*;
    use crate::{DEFAULT_LISTEN_PORT, MIN_USABLE_MEMORY_BYTES, ServerConfig};

    static NEXT_TEST_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

    #[tokio::test]
    async fn local_admin_listener_serves_only_health_readiness_and_metrics() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-server-http-{}-{}",
            std::process::id(),
            NEXT_TEST_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        ));
        let config = ServerConfig::new(
            &directory,
            DEFAULT_LISTEN_PORT,
            MIN_USABLE_MEMORY_BYTES + 8 * 1024 * 1024,
            8 * 1024 * 1024,
        )
        .expect("test server configuration");
        let server = Arc::new(LocalServerState::open(&config).expect("local shard recovery"));
        let prometheus = install_prometheus_recorder().expect("Prometheus recorder");
        assert_mixed_route_concurrency(Arc::clone(&server), prometheus.clone()).await;
        let listener = TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0)))
            .await
            .expect("ephemeral loopback listener");
        let address = listener.local_addr().expect("bound address");
        let (shutdown_sender, shutdown_receiver) = oneshot::channel();
        let server_task = tokio::spawn(serve(
            listener,
            Arc::clone(&server),
            prometheus,
            shutdown_receiver,
        ));

        let health =
            raw_request(address, b"GET /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n").await;
        assert!(health.starts_with("HTTP/1.1 200"), "{health}");

        let ready = raw_request(address, b"GET /readyz HTTP/1.1\r\nHost: localhost\r\n\r\n").await;
        assert!(ready.starts_with("HTTP/1.1 200"), "{ready}");

        let metrics =
            raw_request(address, b"GET /metrics HTTP/1.1\r\nHost: localhost\r\n\r\n").await;
        assert!(metrics.starts_with("HTTP/1.1 200"), "{metrics}");
        assert!(metrics.contains("terrabase_server_ready 1"), "{metrics}");
        assert!(
            metrics
                .contains("terrabase_memory_category_reserved_bytes{category=\"database_state\"}"),
            "{metrics}"
        );
        assert!(!metrics.contains("terrabase_disk"), "{metrics}");

        let rejected_body = raw_request(
            address,
            b"GET /healthz HTTP/1.1\r\nHost: localhost\r\nContent-Length: 4\r\n\r\ndata",
        )
        .await;
        assert!(rejected_body.starts_with("HTTP/1.1 413"), "{rejected_body}");

        let rejected_transfer_encoding = raw_request(
            address,
            b"GET /readyz HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n",
        )
        .await;
        assert!(
            rejected_transfer_encoding.starts_with("HTTP/1.1 413"),
            "{rejected_transfer_encoding}"
        );

        let wrong_method = raw_request(
            address,
            b"HEAD /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n",
        )
        .await;
        assert!(wrong_method.starts_with("HTTP/1.1 405"), "{wrong_method}");

        let no_document_endpoint = raw_request(
            address,
            b"GET /v1/documents HTTP/1.1\r\nHost: localhost\r\n\r\n",
        )
        .await;
        assert!(
            no_document_endpoint.starts_with("HTTP/1.1 404"),
            "{no_document_endpoint}"
        );

        shutdown_sender.send(()).expect("request server shutdown");
        server_task
            .await
            .expect("server task")
            .expect("clean server drain");
        assert!(!server.is_ready());
        assert!(!server.is_live());
        drop(server);
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn body_rejection_does_not_consume_request_payload() {
        let mut headers = HeaderMap::new();
        headers.insert(CONTENT_LENGTH, HeaderValue::from_static("2"));
        assert_eq!(
            rejected_body_status(&headers),
            Some(StatusCode::PAYLOAD_TOO_LARGE)
        );

        headers.insert(CONTENT_LENGTH, HeaderValue::from_static("invalid"));
        assert_eq!(
            rejected_body_status(&headers),
            Some(StatusCode::BAD_REQUEST)
        );
    }

    async fn raw_request(address: SocketAddr, request: &[u8]) -> String {
        let mut stream = TcpStream::connect(address).await.expect("connect loopback");
        stream.write_all(request).await.expect("write HTTP request");
        let mut response = Vec::new();
        stream
            .read_to_end(&mut response)
            .await
            .expect("read HTTP response");
        String::from_utf8(response).expect("HTTP response is UTF-8")
    }

    async fn assert_mixed_route_concurrency(
        server: Arc<LocalServerState>,
        prometheus: PrometheusHandle,
    ) {
        let metrics_scrape = Arc::new(Mutex::new(()));
        let handler_slots = Arc::new(Semaphore::new(MAX_IN_FLIGHT_HANDLERS));
        let app = router_with_handler_slots(
            HttpState {
                server,
                prometheus: Arc::new(prometheus),
                metrics_scrape: Arc::clone(&metrics_scrape),
            },
            Arc::clone(&handler_slots),
        );

        let scrape_guard = metrics_scrape.lock().await;
        let mut scrapes = Vec::with_capacity(MAX_IN_FLIGHT_HANDLERS);
        for _ in 0..MAX_IN_FLIGHT_HANDLERS {
            let app = app.clone();
            scrapes.push(tokio::spawn(async move {
                app.oneshot(
                    Request::builder()
                        .uri("/metrics")
                        .body(Body::empty())
                        .expect("metrics request"),
                )
                .await
                .expect("metrics service response")
            }));
        }

        tokio::time::timeout(Duration::from_secs(1), async {
            while handler_slots.available_permits() != 0 {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("all metrics handlers acquired the shared limit");

        let mut health = tokio::spawn({
            let app = app.clone();
            async move {
                app.oneshot(
                    Request::builder()
                        .uri("/healthz")
                        .body(Body::empty())
                        .expect("health request"),
                )
                .await
                .expect("health service response")
            }
        });
        let mut ready = tokio::spawn(async move {
            app.oneshot(
                Request::builder()
                    .uri("/readyz")
                    .body(Body::empty())
                    .expect("readiness request"),
            )
            .await
            .expect("readiness service response")
        });

        assert!(
            tokio::time::timeout(Duration::from_millis(50), &mut health)
                .await
                .is_err()
        );
        assert!(
            tokio::time::timeout(Duration::from_millis(50), &mut ready)
                .await
                .is_err()
        );

        drop(scrape_guard);
        for scrape in scrapes {
            assert_eq!(scrape.await.expect("metrics task").status(), StatusCode::OK);
        }
        assert_eq!(health.await.expect("health task").status(), StatusCode::OK);
        assert_eq!(
            ready.await.expect("readiness task").status(),
            StatusCode::OK
        );
    }
}
