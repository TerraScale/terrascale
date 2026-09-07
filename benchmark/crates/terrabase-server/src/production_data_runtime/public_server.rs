use std::sync::Arc;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};

use axum::Router;
use tokio::net::TcpListener;

/// A successfully bound public listener whose maintenance workers have not
/// started yet. Dropping this value before [`Self::serve`] starts no tasks.
pub struct PreparedProductionPublic {
    listener: TcpListener,
    router: Router,
    state: Arc<crate::api::ApiState>,
    publication: Option<crate::config::LifecyclePublicationConfig>,
    registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    status: PublicServingStatus,
}

/// Cloneable evidence that a prepared public listener is actively serving.
#[derive(Clone)]
pub struct PublicServingStatus {
    serving: Arc<AtomicBool>,
    workers: Arc<OnceLock<Vec<tokio::task::AbortHandle>>>,
}

struct ServingStatusGuard {
    status: PublicServingStatus,
    cancel: Option<tokio::sync::oneshot::Sender<()>>,
}

impl Drop for ServingStatusGuard {
    fn drop(&mut self) {
        self.status.serving.store(false, Ordering::Release);
        if let Some(cancel) = self.cancel.take() {
            let _ = cancel.send(());
        }
    }
}

struct WorkerGuard {
    publication: Option<crate::lifecycle_publication::LifecyclePublicationTask>,
    dispatcher: Option<crate::api::DispatchWorker>,
    migrations: Option<tokio::task::JoinHandle<()>>,
    exports: Option<Arc<crate::export_api::ExportRuntime>>,
    imports: Option<Arc<crate::api::imports::ImportRuntime>>,
    import_worker: Option<tokio::task::JoinHandle<()>>,
}

impl Drop for WorkerGuard {
    fn drop(&mut self) {
        if let Some(imports) = &self.imports {
            imports.stop();
        }
        if let Some(worker) = &self.import_worker {
            worker.abort();
        }
        if let Some(migrations) = &self.migrations {
            migrations.abort();
        }
        if let Some(exports) = &self.exports {
            exports.stop();
        }
    }
}

impl WorkerGuard {
    async fn shutdown(mut self) -> Result<(), String> {
        if let Some(imports) = &self.imports {
            imports.shutdown().await;
        }
        if let Some(worker) = &self.import_worker {
            worker.abort();
        }
        let publication = match self.publication.take() {
            Some(publication) => publication.shutdown().await.map_err(failure),
            None => Ok(()),
        };
        if let Some(exports) = &self.exports {
            exports.stop();
        }
        let dispatcher = match self.dispatcher.take() {
            Some(dispatcher) => dispatcher.shutdown().await.map_err(failure),
            None => Ok(()),
        };
        let migrations = if let Some(migrations) = self.migrations.as_mut() {
            migrations.abort();
            match migrations.await {
                Ok(()) => Ok(()),
                Err(error) if error.is_cancelled() => Ok(()),
                Err(error) => Err(failure(error)),
            }
        } else {
            Ok(())
        };
        publication.and(dispatcher).and(migrations)
    }
}

impl PublicServingStatus {
    /// Returns true only while the owned server task is serving requests.
    #[must_use]
    pub fn is_serving(&self) -> bool {
        self.serving.load(Ordering::Acquire)
            && self
                .workers
                .get()
                .is_some_and(|workers| workers.iter().all(|worker| !worker.is_finished()))
    }

    /// Requests every public maintenance worker to stop before listener drain.
    pub fn begin_drain(&self) {
        if let Some(workers) = self.workers.get() {
            for worker in workers {
                worker.abort();
            }
        }
        self.serving.store(false, Ordering::Release);
    }
}

impl PreparedProductionPublic {
    pub(crate) fn new(
        listener: TcpListener,
        router: Router,
        state: Arc<crate::api::ApiState>,
        publication: Option<crate::config::LifecyclePublicationConfig>,
        registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    ) -> Self {
        Self {
            listener,
            router,
            state,
            publication,
            registry,
            status: PublicServingStatus {
                serving: Arc::new(AtomicBool::new(false)),
                workers: Arc::new(OnceLock::new()),
            },
        }
    }

    /// Returns an observer that remains valid after this owner is moved to `serve`.
    #[must_use]
    pub fn status(&self) -> PublicServingStatus {
        self.status.clone()
    }

    /// Starts the serving workers and owns their complete shutdown sequence.
    ///
    /// # Errors
    /// Reports listener initialization, serving, or maintenance worker failures.
    pub async fn serve(
        self,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<(), String> {
        let Self { listener, router, state, publication, registry, status } = self;
        let publication = publication
            .as_ref()
            .map(|config| crate::lifecycle_publication::spawn(config, registry).map_err(failure))
            .transpose()?;
        crate::api::imports::recover_imports(&state)?;
        let dispatcher = crate::api::spawn_webhook_dispatcher(&state);
        let migrations = crate::api::spawn_schema_migrations(&state);
        let workers = WorkerGuard {
            publication,
            dispatcher,
            migrations,
            exports: state.exports.clone(),
            imports: state.imports.clone(),
            import_worker: crate::api::imports::spawn_imports(&state),
        };
        let mut worker_handles = Vec::new();
        if let Some(worker) = &workers.import_worker {
            worker_handles.push(worker.abort_handle());
        }
        if let Some(publication) = workers.publication.as_ref() {
            worker_handles.push(publication.abort_handle());
        }
        if let Some(dispatcher) = workers.dispatcher.as_ref() {
            worker_handles.push(dispatcher.abort_handle());
        }
        if let Some(migrations) = workers.migrations.as_ref() {
            worker_handles.push(migrations.abort_handle());
        }
        let _ = status.workers.set(worker_handles);
        status.serving.store(true, Ordering::Release);
        let (cancel, cancelled) = tokio::sync::oneshot::channel();
        let status_guard = ServingStatusGuard { status, cancel: Some(cancel) };
        let serving = status_guard.status.serving.clone();
        let shutdown = async move {
            tokio::select! { () = shutdown => {}, _ = cancelled => {} }
            serving.store(false, Ordering::Release);
        };
        let result =
            axum::serve(listener, router).with_graceful_shutdown(shutdown).await.map_err(failure);
        status_guard.status.serving.store(false, Ordering::Release);
        let workers = workers.shutdown().await;
        result.and(workers)
    }
}

fn failure(error: impl std::fmt::Debug) -> String {
    format!("{error:?}")
}

#[cfg(test)]
mod tests {
    use super::*;

    struct ShutdownDrop(Option<tokio::sync::oneshot::Sender<()>>);
    impl Drop for ShutdownDrop {
        fn drop(&mut self) {
            if let Some(notice) = self.0.take() {
                let _ = notice.send(());
            }
        }
    }

    use axum::http::StatusCode;
    use axum::response::IntoResponse;
    use axum::routing::get;
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::time::Duration;
    use terrabase::observability::MetricsRegistry;
    use terrabase::regions::RegionCode;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpStream;
    use tokio::sync::{Notify, oneshot};
    use tokio::time::{sleep, timeout};

    fn state() -> Arc<crate::api::ApiState> {
        let backups = Arc::new(crate::backup::BackupStreams::open_member());
        let backup_root = unique_path("backup");
        let timetravel =
            Arc::new(crate::backup::TimeTravelCache::new(Arc::clone(&backups), &backup_root));
        Arc::new(crate::api::ApiState {
            budget: None,
            native_backups: None,
            native_recovery: None,
            native_restore_target: None,
            admitted_databases: None,
            replication_progress: None,
            standalone_durability: false,
            development: Arc::default(),
            provisioning: None,
            region: RegionCode::GRU,
            member_id: "test-member".into(),
            metrics: Arc::new(MetricsRegistry::new()),
            keys: None,
            delegated_identity: None,
            byo_jwt: None,
            control: None,
            catalog: None,
            scope: ("test-tenant".into(), "test-project".into()),
            backups: Some(backups),
            backup_root,
            timetravel: Some(timetravel),
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
            webhook_keys: None,
            exports: None,
            imports: None,
        })
    }

    fn unique_path(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "terrabase-public-server-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ))
    }

    fn registry() -> Arc<crate::admitted_databases::AdmittedDatabaseRegistry> {
        Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| true)))
    }

    async fn prepared(router: axum::Router) -> (PreparedProductionPublic, std::net::SocketAddr) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let address = listener.local_addr().expect("address");
        let prepared = PreparedProductionPublic::new(listener, router, state(), None, registry());
        (prepared, address)
    }

    async fn wait_until_serving(status: &PublicServingStatus) {
        timeout(Duration::from_secs(1), async {
            while !status.is_serving() {
                sleep(Duration::from_millis(1)).await;
            }
        })
        .await
        .expect("server starts");
    }

    async fn request(address: std::net::SocketAddr, path: &str) -> Vec<u8> {
        let mut stream = TcpStream::connect(address).await.expect("connect");
        stream
            .write_all(
                format!("GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
                    .as_bytes(),
            )
            .await
            .expect("request");
        let mut response = Vec::new();
        stream.read_to_end(&mut response).await.expect("response");
        response
    }

    #[tokio::test]
    async fn prepared_listener_is_false_until_serve_and_serves_real_http() {
        let (prepared, address) = prepared(
            axum::Router::new()
                .route("/health", get(|| async { (StatusCode::OK, "ready").into_response() })),
        )
        .await;
        let status = prepared.status();
        assert!(!status.is_serving());
        let (shutdown, stop) = oneshot::channel();
        let task = tokio::spawn(prepared.serve(async {
            stop.await.ok();
        }));
        wait_until_serving(&status).await;
        let response = request(address, "/health").await;
        assert!(response.starts_with(b"HTTP/1.1 200"), "{response:?}");
        shutdown.send(()).expect("shutdown");
        task.await.expect("join").expect("serve");
        assert!(!status.is_serving());
    }

    #[tokio::test]
    async fn graceful_shutdown_reports_false_while_a_request_is_held() {
        let entered = Arc::new(Notify::new());
        let release = Arc::new(Notify::new());
        let release_handler = Arc::clone(&release);
        let entered_handler = Arc::clone(&entered);
        let router = axum::Router::new().route(
            "/held",
            get(move || async move {
                entered_handler.notify_one();
                release_handler.notified().await;
                StatusCode::OK
            }),
        );
        let (prepared, address) = prepared(router).await;
        let status = prepared.status();
        let (shutdown, stop) = oneshot::channel();
        let task = tokio::spawn(prepared.serve(async {
            stop.await.ok();
        }));
        wait_until_serving(&status).await;
        let request_task = tokio::spawn(request(address, "/held"));
        timeout(Duration::from_secs(1), entered.notified()).await.expect("request entered");
        shutdown.send(()).expect("shutdown");
        timeout(Duration::from_secs(1), async {
            while status.is_serving() {
                sleep(Duration::from_millis(1)).await;
            }
        })
        .await
        .expect("shutdown fences status");
        assert!(!task.is_finished(), "graceful shutdown still waits for held request");
        release.notify_one();
        assert!(request_task.await.expect("request join").starts_with(b"HTTP/1.1 200"));
        task.await.expect("join").expect("serve");
    }

    #[tokio::test]
    async fn cancellation_clears_serving_and_stops_exports() {
        let mut state = state();
        let export_root = unique_path("exports");
        let exports = Arc::new(
            crate::export_api::ExportRuntime::local(&export_root, RegionCode::GRU).expect("export"),
        );
        Arc::get_mut(&mut state).expect("unique state").exports = Some(Arc::clone(&exports));
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let prepared =
            PreparedProductionPublic::new(listener, axum::Router::new(), state, None, registry());
        let status = prepared.status();
        let (notice, released) = oneshot::channel();
        let notice = ShutdownDrop(Some(notice));
        let task = tokio::spawn(prepared.serve(async move {
            let _notice = notice;
            std::future::pending::<()>().await;
        }));
        wait_until_serving(&status).await;
        task.abort();
        let _ = task.await;
        assert!(!status.is_serving());
        assert!(exports.is_stopping());
        timeout(Duration::from_secs(1), released)
            .await
            .expect("shutdown waiter terminates after owner cancellation")
            .expect("shutdown closure released");
        let _ = std::fs::remove_dir_all(export_root);
    }

    #[tokio::test]
    async fn finished_registered_worker_invalidates_serving_status() {
        let (prepared, _) = prepared(axum::Router::new()).await;
        let status = prepared.status();
        let (finished, worker) = tokio::sync::oneshot::channel::<()>();
        let worker = tokio::spawn(async move {
            let _ = worker.await;
        });
        status.workers.set(vec![worker.abort_handle()]).expect("register worker");
        let (shutdown, stop) = oneshot::channel();
        let task = tokio::spawn(prepared.serve(async {
            stop.await.ok();
        }));
        wait_until_serving(&status).await;
        finished.send(()).expect("finish worker");
        timeout(Duration::from_secs(1), async {
            while status.is_serving() {
                sleep(Duration::from_millis(1)).await;
            }
        })
        .await
        .expect("status observes worker completion");
        shutdown.send(()).expect("shutdown");
        task.await.expect("join").expect("serve");
    }

    #[tokio::test]
    async fn worker_shutdown_reports_a_panicked_migration() {
        let migration = tokio::spawn(async {
            panic!("synthetic migration failure");
        });
        timeout(Duration::from_secs(1), async {
            while !migration.is_finished() {
                sleep(Duration::from_millis(1)).await;
            }
        })
        .await
        .expect("migration reaches terminal state");
        let workers = WorkerGuard {
            publication: None,
            dispatcher: None,
            migrations: Some(migration),
            exports: None,
            imports: None,
            import_worker: None,
        };
        let result = workers.shutdown().await;
        assert!(result.is_err(), "panic must be surfaced by worker shutdown");
    }
    #[tokio::test]
    async fn cancelled_owner_aborts_its_retained_migration() {
        let migration = tokio::spawn(std::future::pending());
        let observation = migration.abort_handle();
        let guard = WorkerGuard {
            publication: None,
            dispatcher: None,
            migrations: Some(migration),
            exports: None,
            imports: None,
            import_worker: None,
        };
        drop(guard);
        timeout(Duration::from_secs(1), async {
            while !observation.is_finished() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("cancelled migration terminates");
    }
}
