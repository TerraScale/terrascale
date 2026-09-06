//! Owned private metrics listener with observations tied to its serving future.

use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use terrabase::observability::{Component, HealthStatus, MetricsRegistry};

/// Read-only evidence from the actual private listener's lifetime.
#[derive(Clone)]
pub struct MetricsServing {
    serving: Arc<AtomicBool>,
}

impl MetricsServing {
    /// False before serving, after shutdown starts, and after cancellation.
    #[must_use]
    pub fn is_serving(&self) -> bool {
        self.serving.load(Ordering::Acquire)
    }
}

/// Bound listener; constructing this owner starts no background tasks.
pub struct PreparedMetrics {
    listener: tokio::net::TcpListener,
    metrics: Arc<MetricsRegistry>,
    observation: MetricsServing,
}

impl PreparedMetrics {
    /// Binds the configured private address using the bounded operator router.
    ///
    /// # Errors
    /// Returns the socket error if the address cannot be bound.
    pub async fn bind(address: SocketAddr, metrics: Arc<MetricsRegistry>) -> std::io::Result<Self> {
        let listener = tokio::net::TcpListener::bind(address).await?;
        Ok(Self {
            listener,
            metrics,
            observation: MetricsServing { serving: Arc::new(AtomicBool::new(false)) },
        })
    }

    /// Returns an observer that cannot change listener readiness.
    #[must_use]
    pub fn observation(&self) -> MetricsServing {
        self.observation.clone()
    }

    /// Runs operator HTTP until shutdown, retaining the socket in this future.
    /// Cancellation drops the socket and clears its serving observation.
    ///
    /// # Errors
    /// Returns a listener serving error.
    pub async fn serve(
        self,
        shutdown: impl Future<Output = ()> + Send + 'static,
    ) -> std::io::Result<()> {
        let guard = ServingGuard { observation: self.observation, metrics: self.metrics };
        let app = crate::http::router(guard.metrics.clone());
        guard.metrics.set_component_health(Component::Metrics, HealthStatus::Healthy);
        guard.observation.serving.store(true, Ordering::Release);
        let observation = guard.observation.clone();
        let metrics = guard.metrics.clone();
        let result = axum::serve(self.listener, app)
            .with_graceful_shutdown(async move {
                shutdown.await;
                observation.serving.store(false, Ordering::Release);
                metrics.set_component_health(Component::Metrics, HealthStatus::Unhealthy);
            })
            .await;
        drop(guard);
        result
    }
}

struct ServingGuard {
    observation: MetricsServing,
    metrics: Arc<MetricsRegistry>,
}

impl Drop for ServingGuard {
    fn drop(&mut self) {
        self.observation.serving.store(false, Ordering::Release);
        self.metrics.set_component_health(Component::Metrics, HealthStatus::Unhealthy);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt as _, AsyncWriteExt as _};

    #[tokio::test]
    async fn observation_tracks_real_listener_and_graceful_shutdown() {
        let prepared =
            PreparedMetrics::bind("127.0.0.1:0".parse().unwrap(), Arc::new(MetricsRegistry::new()))
                .await
                .unwrap();
        let address = prepared.listener.local_addr().unwrap();
        let observation = prepared.observation();
        assert!(!observation.is_serving());
        let (stop, signal) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(prepared.serve(async {
            let _ = signal.await;
        }));
        let mut client = tokio::net::TcpStream::connect(address).await.unwrap();
        client
            .write_all(
                format!(
                    "GET {} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
                    terrabase::observability::PROMETHEUS_PATH
                )
                .as_bytes(),
            )
            .await
            .unwrap();
        let mut response = Vec::new();
        tokio::time::timeout(std::time::Duration::from_secs(5), client.read_to_end(&mut response))
            .await
            .unwrap()
            .unwrap();
        assert!(response.starts_with(b"HTTP/1.1 200"), "{}", String::from_utf8_lossy(&response));
        assert!(observation.is_serving());
        stop.send(()).unwrap();
        task.await.unwrap().unwrap();
        assert!(!observation.is_serving());
    }

    #[tokio::test]
    async fn cancellation_and_unserved_drop_do_not_leave_serving_evidence() {
        let metrics = Arc::new(MetricsRegistry::new());
        let prepared =
            PreparedMetrics::bind("127.0.0.1:0".parse().unwrap(), metrics.clone()).await.unwrap();
        let observation = prepared.observation();
        drop(prepared);
        assert!(!observation.is_serving());
        let prepared =
            PreparedMetrics::bind("127.0.0.1:0".parse().unwrap(), metrics).await.unwrap();
        let observation = prepared.observation();
        let mut serving = Box::pin(prepared.serve(std::future::pending()));
        assert!(futures_util::poll!(&mut serving).is_pending());
        assert!(observation.is_serving());
        drop(serving);
        assert!(!observation.is_serving());
    }
}
