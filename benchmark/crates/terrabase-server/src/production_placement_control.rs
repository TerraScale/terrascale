//! Production implementation of the host-scoped placement data control.

use std::sync::Arc;

use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::grpc::PlacementDataControl;
use terrabase_tbep::placement::transition::{TransitionReceiptSigner, VerifiedPreparedTransition};
use terrabase_tbep::{placement::PlacementJournal, proto::terrabase::v1 as pb};

use crate::admitted_databases::AdmittedDatabaseRegistry;
use crate::production_data_runtime::{ProductionDataLifecycle, RetiredReplicationFrontiers};
use crate::replication_proof;

/// Adapter binding placement control to the retained production data owner.
pub struct ProductionPlacementDataControl {
    runtime: Arc<terrabase_cluster::production::ProductionMemberRuntime>,
    lifecycle: Arc<ProductionDataLifecycle>,
    registry: Arc<AdmittedDatabaseRegistry>,
    journal: Arc<PlacementJournal>,
    signer: Arc<TransitionReceiptSigner>,
    readiness_signer: Arc<crate::replication_proof::MemberReceiptSigner>,
    member_identity: String,
}

impl ProductionPlacementDataControl {
    /// Creates a control adapter over the already admitted runtime and leases.
    pub fn new(
        runtime: Arc<terrabase_cluster::production::ProductionMemberRuntime>,
        lifecycle: Arc<ProductionDataLifecycle>,
        registry: Arc<AdmittedDatabaseRegistry>,
        journal: Arc<PlacementJournal>,
        signer: Arc<TransitionReceiptSigner>,
        readiness_signer: Arc<crate::replication_proof::MemberReceiptSigner>,
        member_identity: String,
    ) -> Self {
        Self { runtime, lifecycle, registry, journal, signer, readiness_signer, member_identity }
    }

    fn frontier_digest(frontiers: &RetiredReplicationFrontiers) -> String {
        let mut value = serde_json::Map::new();
        value.insert(
            "prepared".into(),
            serde_json::Value::String(frontiers.prepared().proof_digest().into()),
        );
        let writers = frontiers
            .writers()
            .iter()
            .map(|(scope, frontier)| {
                (
                    scope.to_string(),
                    serde_json::json!({
                        "fence_sequence": frontier.fence_sequence.get(),
                        "origins": frontier.origins,
                    }),
                )
            })
            .collect::<std::collections::BTreeMap<_, _>>();
        value.insert("writers".into(), serde_json::to_value(writers).expect("BTreeMap JSON"));
        let peers = frontiers
            .peers()
            .iter()
            .map(|((peer, scope), lane)| {
                let physical = lane.physical.as_ref().map(|entry| match entry {
                    crate::replication_peers::RetiredPhysicalLane::Writer => "writer",
                    crate::replication_peers::RetiredPhysicalLane::Follower(_) => "follower",
                });
                (
                    (peer.clone(), scope.to_string()),
                    serde_json::json!({
                        "physical": physical,
                        "causal": lane.causal_frontier,
                    }),
                )
            })
            .collect::<std::collections::BTreeMap<_, _>>();
        value.insert("peers".into(), serde_json::to_value(peers).expect("BTreeMap JSON"));
        blake3::hash(&serde_json::to_vec(&value).expect("frontier JSON")).to_hex().to_string()
    }

    fn scope(
        request: &pb::PlacementControlServiceReadinessRequest,
    ) -> Result<DatabaseRouteScope, terrabase_tbep::placement::PlacementError> {
        DatabaseRouteScope::try_new(&request.tenant, &request.project, &request.database)
            .map_err(|_| terrabase_tbep::placement::PlacementError::Invalid)
    }
}

impl PlacementDataControl for ProductionPlacementDataControl {
    fn drain(
        &self,
        operation: &str,
        prepared: &[u8],
    ) -> Result<Vec<u8>, terrabase_tbep::placement::PlacementError> {
        let verified = self.journal.begin_transition_drain(operation, prepared)?;
        let runtime = Arc::clone(&self.runtime);
        let lifecycle = Arc::clone(&self.lifecycle);
        let frontiers = tokio::runtime::Handle::current()
            .block_on(lifecycle.retire_transition_replication(runtime, &verified))
            .map_err(|_| terrabase_tbep::placement::PlacementError::Conflict)?;
        let accepted = self.journal.accepted()?;
        if accepted.proof_digest != verified.previous_proof_digest() {
            return Err(terrabase_tbep::placement::PlacementError::Conflict);
        }
        let digest = Self::frontier_digest(&frontiers);
        self.signer.sign_old_epoch_fenced(&verified, &self.member_identity, &digest)
    }

    fn readiness(
        &self,
        request: &pb::PlacementControlServiceReadinessRequest,
    ) -> Result<Vec<u8>, terrabase_tbep::placement::PlacementError> {
        let scope = Self::scope(request)?;
        let snapshot = self
            .registry
            .retained_snapshot()
            .map_err(|_| terrabase_tbep::placement::PlacementError::Storage)?;
        let database = snapshot
            .into_iter()
            .find_map(|entry| {
                (DatabaseRouteScope::try_new(
                    &entry.database.record.tenant_id,
                    &entry.database.record.project_id,
                    &entry.database.record.database_id,
                )
                .ok()
                    == Some(scope)
                    && entry.database.record.operation_id == request.operation_id)
                    .then_some(entry.database)
            })
            .ok_or(terrabase_tbep::placement::PlacementError::NotFound)?;
        let assignment = terrabase_cluster::wal_route::DurableWalAssignmentSource::load_assignment(
            &*self.journal,
            scope,
        )
        .map_err(|_| terrabase_tbep::placement::PlacementError::Conflict)?;
        let digest = self.journal.accepted_placement_digest()?;
        let nonce = hex::decode(&request.nonce)
            .map_err(|_| terrabase_tbep::placement::PlacementError::Invalid)?;
        let observations = self
            .lifecycle
            .observations()
            .ok_or(terrabase_tbep::placement::PlacementError::Conflict)?;
        let binding = replication_proof::ReadinessBinding {
            environment: self
                .runtime
                .transport()
                .config()
                .local_identity()
                .environment()
                .to_owned(),
            scope,
            key_version: database.record.key_version.clone(),
            operation_id: request.operation_id.clone(),
            member_identity: self.member_identity.clone(),
            region: assignment.writer.region().as_str().to_owned(),
            membership_epoch: assignment.epoch,
            generation: assignment.generation,
            placement_digest: digest,
            selected_regions: database.record.regions.iter().cloned().collect(),
        };
        replication_proof::issue_readiness(
            &binding,
            &nonce,
            &self.readiness_signer,
            &self.lifecycle.progress(),
            &observations,
        )
        .map_err(|_| terrabase_tbep::placement::PlacementError::Conflict)
    }
}
