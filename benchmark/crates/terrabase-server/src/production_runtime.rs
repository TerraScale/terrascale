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
use terrabase_tbep::grpc::TbepServingStatus;

/// Binds readiness and shutdown facts to the already-owned production workers.
/// This adapter creates no engines, workers, listeners, or credentials.
pub struct ProductionRuntime {
    lifecycle: Arc<ProductionDataLifecycle>,
    metrics: MetricsServing,
    public: PublicServingStatus,
    tbep: TbepServingStatus,
}

#[allow(clippy::fn_params_excessive_bools, reason = "one fact per serving surface")]
fn complete_member_host(
    admitted: bool,
    encrypted: bool,
    public_serving: bool,
    tbep_serving: bool,
) -> bool {
    admitted && encrypted && public_serving && tbep_serving
}

impl ProductionRuntime {
    /// Binds the live lifecycle, metrics, and public-serving status providers.
    #[must_use]
    pub fn new(
        lifecycle: Arc<ProductionDataLifecycle>,
        metrics: MetricsServing,
        public: PublicServingStatus,
        tbep: TbepServingStatus,
    ) -> Self {
        Self { lifecycle, metrics, public, tbep }
    }
}

impl ProductionDataPlane for ProductionRuntime {
    fn facts(&self) -> ProductionDataPlaneFacts {
        let replication = self.lifecycle.replication_readiness();
        let hosts = self.lifecycle.writer_host_readiness();
        let all_hosts_encrypted = self.lifecycle.all_hosts_encrypted();
        let metrics_serving = self.metrics.is_serving();
        let public_serving = self.public.is_serving();
        let tbep_serving = self.tbep.is_serving();
        ProductionDataPlaneFacts {
            databases_admitted: hosts.admitted,
            local_rf2_reconciled: replication.physical_ready,
            replication_serving: replication.physical_ready,
            repair_serving: replication.repair_ready,
            durable_progress_available: replication.durable_progress_available,
            publication_journal_authoritative: hosts.journal_available,
            storage_encrypted: all_hosts_encrypted,
            complete_member_host: complete_member_host(
                hosts.admitted,
                hosts.encrypted,
                public_serving,
                tbep_serving,
            ),
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

#[cfg(test)]
mod tests {
    use super::complete_member_host;

    #[test]
    fn complete_member_host_requires_tbep_serving_evidence() {
        for (tbep_serving, want) in [(false, false), (true, true)] {
            assert_eq!(complete_member_host(true, true, true, tbep_serving), want);
        }
        assert!(!complete_member_host(true, true, false, true));
    }
}
