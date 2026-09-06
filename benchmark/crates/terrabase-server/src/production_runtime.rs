//! Concrete production data-plane adapter assembled from live owners.

use std::sync::Arc;
use std::time::Duration;

use terrabase_cluster::production::{
    ProductionDataPlane, ProductionDataPlaneDrain, ProductionDataPlaneError,
    ProductionDataPlaneFacts,
};

use crate::production_data_runtime::ProductionDataLifecycle;
use crate::production_data_runtime::PublicServingStatus;
use crate::production_metrics::MetricsServing;

/// Binds readiness and shutdown facts to the already-owned production workers.
/// This adapter creates no engines, workers, listeners, or credentials.
pub struct ProductionRuntime {
    lifecycle: Arc<ProductionDataLifecycle>,
    metrics: MetricsServing,
    public: PublicServingStatus,
}

impl ProductionRuntime {
    /// Binds the live lifecycle, metrics, and public-serving status providers.
    #[must_use]
    pub fn new(
        lifecycle: Arc<ProductionDataLifecycle>,
        metrics: MetricsServing,
        public: PublicServingStatus,
    ) -> Self {
        Self { lifecycle, metrics, public }
    }
}

impl ProductionDataPlane for ProductionRuntime {
    fn facts(&self) -> ProductionDataPlaneFacts {
        let replication = self.lifecycle.replication_readiness();
        let hosts = self.lifecycle.writer_host_readiness();
        let all_hosts_encrypted = self.lifecycle.all_hosts_encrypted();
        let metrics_serving = self.metrics.is_serving();
        let public_serving = self.public.is_serving();
        ProductionDataPlaneFacts {
            databases_admitted: hosts.admitted,
            local_rf2_reconciled: replication.physical_ready,
            replication_serving: replication.physical_ready,
            repair_serving: replication.repair_ready,
            durable_progress_available: replication.durable_progress_available,
            publication_journal_authoritative: hosts.journal_available,
            storage_encrypted: all_hosts_encrypted,
            complete_member_host: hosts.admitted && hosts.encrypted && public_serving,
            metrics_serving,
            shutdown_frontiers_persistable: !self.lifecycle.is_draining()
                && self.lifecycle.frontiers_persistable(),
        }
    }

    fn begin_drain(&self) {
        let _ = self.lifecycle.begin_drain();
    }

    fn drain(&self, timeout: Duration) -> ProductionDataPlaneDrain<'_> {
        Box::pin(async move {
            tokio::time::timeout(timeout, self.lifecycle.drain())
                .await
                .map_err(|_| ProductionDataPlaneError::Timeout)?
                .map_err(|_| ProductionDataPlaneError::Persistence)
        })
    }
}
