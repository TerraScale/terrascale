use std::sync::Arc;
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
    workers: Arc<AtomicBool>,
}

struct ServingStatusGuard(PublicServingStatus);

impl Drop for ServingStatusGuard {
    fn drop(&mut self) {
        self.0.serving.store(false, Ordering::Release);
    }
}

struct WorkerGuard {
    publication: Option<crate::lifecycle_publication::LifecyclePublicationTask>,
    dispatcher: Option<crate::api::DispatchWorker>,
    migrations: Option<tokio::task::JoinHandle<()>>,
    exports: Option<Arc<crate::export_api::ExportRuntime>>,
    live: Arc<AtomicBool>,
}

impl Drop for WorkerGuard {
    fn drop(&mut self) {
        if let Some(migrations) = &self.migrations {
            migrations.abort();
        }
        if let Some(exports) = &self.exports {
            exports.stop();
        }
        self.live.store(false, Ordering::Release);
    }
}

impl WorkerGuard {
    async fn shutdown(mut self) {
        if let Some(publication) = self.publication.take() {
            publication.shutdown().await;
        }
        if let Some(exports) = &self.exports {
            exports.stop();
        }
        if let Some(dispatcher) = self.dispatcher.take() {
            dispatcher.shutdown().await;
        }
        if let Some(migrations) = self.migrations.take() {
            migrations.abort();
            let _ = migrations.await;
        }
        self.live.store(false, Ordering::Release);
    }
}

impl PublicServingStatus {
    /// Returns true only while the owned server task is serving requests.
    #[must_use]
    pub fn is_serving(&self) -> bool {
        self.serving.load(Ordering::Acquire) && self.workers.load(Ordering::Acquire)
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
                workers: Arc::new(AtomicBool::new(false)),
            },
        }
    }

    /// Returns an observer that remains valid after this owner is moved to `serve`.
    #[must_use]
    pub fn status(&self) -> PublicServingStatus {
        self.status.clone()
    }

    /// Starts the serving workers and owns their complete shutdown sequence.
    pub async fn serve(
        self,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<(), String> {
        let Self { listener, router, state, publication, registry, status } = self;
        let publication = publication
            .as_ref()
            .map(|config| crate::lifecycle_publication::spawn(config, registry).map_err(failure))
            .transpose()?;
        let dispatcher = crate::api::spawn_webhook_dispatcher(&state);
        let migrations = crate::api::spawn_schema_migrations(&state);
        let workers = WorkerGuard {
            publication,
            dispatcher,
            migrations,
            exports: state.exports.clone(),
            live: Arc::clone(&status.workers),
        };
        status.workers.store(true, Ordering::Release);
        status.serving.store(true, Ordering::Release);
        let _status_guard = ServingStatusGuard(status);
        let result =
            axum::serve(listener, router).with_graceful_shutdown(shutdown).await.map_err(failure);
        _status_guard.0.serving.store(false, Ordering::Release);
        workers.shutdown().await;
        result
    }
}

fn failure(error: impl std::fmt::Display) -> String {
    error.to_string()
}
