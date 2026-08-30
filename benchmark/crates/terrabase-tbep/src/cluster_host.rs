//! The cluster-backed TBEP host (P5): a multi-member cluster served
//! through the protocol — quorum writes, repairing reads, and the
//! backup cluster's permanent typed write refusal.
//!
//! [`ClusterTbepHost`] implements [`TbepHost`] over one
//! `terrabase-cluster` [`ClusterHost`]:
//! every bound channel's tenant must be the cluster's tenant, document
//! writes coordinate through the leaderless coordinator at the
//! requested acknowledgement level, reads repair behind the regional
//! quorum, and `topology.read` answers the cluster's REAL mode —
//! `active` or `backup` — with the engine topology available. A backup
//! cluster answers every client mutation with the typed
//! `backup_cluster_read_only` error, its replication position in the
//! bounded machine-readable detail.

use std::sync::Arc;

use terrabase::replication::LeaderlessLevel;
use terrabase::types::DocumentValue;
use terrabase_cluster::{ClusterHost, ClusterMode, ClusterWriteRefusal, ComposedWrite};

use crate::value::TbepValue;
use crate::registry::{
    ChannelBindRequest, ChannelBindResponse, CommitReceipt, DocumentBody, DocumentPut,
    DocumentReadRequest, DocumentReadResponse, DocumentWriteRequest, DocumentWriteResponse,
    WriteOutcome,
};
use crate::host::{HostingError, TbepHost};

/// Converts one wire value into the cluster's value model.
fn to_cluster_value(value: &TbepValue) -> DocumentValue {
    match value {
        TbepValue::Null => DocumentValue::Null,
        TbepValue::Boolean(flag) => DocumentValue::Boolean(*flag),
        TbepValue::Integer(number) => DocumentValue::Integer(*number),
        TbepValue::Number(number) => DocumentValue::Number(*number),
        TbepValue::String(text) => DocumentValue::String(text.clone()),
        TbepValue::Array(items) => {
            DocumentValue::Array(items.iter().map(to_cluster_value).collect())
        }
        TbepValue::Object(fields) => DocumentValue::Object(
            fields.iter().map(|(name, value)| (name.clone(), to_cluster_value(value))).collect(),
        ),
    }
}

/// Converts one cluster value into the wire's.
fn to_wire_value(value: &DocumentValue) -> TbepValue {
    match value {
        DocumentValue::Null => TbepValue::Null,
        DocumentValue::Boolean(flag) => TbepValue::Boolean(*flag),
        DocumentValue::Integer(number) => TbepValue::Integer(*number),
        DocumentValue::Number(number) => TbepValue::Number(*number),
        DocumentValue::String(text) => TbepValue::String(text.clone()),
        DocumentValue::Array(items) => TbepValue::Array(items.iter().map(to_wire_value).collect()),
        DocumentValue::Object(fields) => TbepValue::Object(
            fields.iter().map(|(name, value)| (name.clone(), to_wire_value(value))).collect(),
        ),
    }
}

// The planes the cluster host does not serve: every one answers the
// typed refusal naming the engine-backed host — the cluster serves
// the document plane and the cluster topology.
macro_rules! refused_plane {
    ($($method:ident($($arg:ident: $ty:ty),*) -> $ret:ty, $name:expr);* $(;)?) => {
        $(
            fn $method(&self, $($arg: $ty),*) -> $ret {
                let _ = ($($arg),*);
                Err(HostingError::Refused(format!(
                    "{} is served by the engine-backed host",
                    $name
                )))
            }
        )*
    };
}

/// The cluster-backed TBEP host: one multi-member cluster behind the
/// protocol's document operations.
pub struct ClusterTbepHost {
    cluster: Arc<std::sync::Mutex<ClusterHost>>,
    serving_member: String,
}

impl ClusterTbepHost {
    /// Wraps a cluster host for protocol serving. The serving member is
    /// the entry point client operations coordinate through — any
    /// member is eligible; the coordinator fans to the voters itself.
    #[must_use]
    pub fn new(cluster: ClusterHost, serving_member: &str) -> Self {
        Self {
            cluster: Arc::new(std::sync::Mutex::new(cluster)),
            serving_member: serving_member.to_owned(),
        }
    }

    fn lock(&self) -> Result<std::sync::MutexGuard<'_, ClusterHost>, HostingError> {
        self.cluster
            .lock()
            .map_err(|_| HostingError::EngineFailure("the cluster is unavailable".into()))
    }

    /// Attaches the backup target set — the active-to-backup
    /// replication setup that arms the `backup_ack` level.
    pub fn attach_backup(&self, set: terrabase_cluster::BackupSet) {
        if let Ok(mut cluster) = self.cluster.lock() {
            cluster.attach_backup(set);
        }
    }
}

impl TbepHost for ClusterTbepHost {
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<ChannelBindResponse, HostingError> {
        let cluster = self.lock()?;
        if request.tenant != cluster.tenant() {
            return Err(HostingError::Refused("the cluster serves its own tenant only".into()));
        }
        Ok(ChannelBindResponse { tenant: request.tenant.clone(), serving_instance: 1 })
    }

    fn document_read(
        &self,
        tenant: &str,
        request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError> {
        let mut cluster = self.lock()?;
        if tenant != cluster.tenant() {
            return Err(HostingError::Refused("the cluster serves its own tenant only".into()));
        }
        let (value, _repair) = cluster
            .client_read(&self.serving_member, &request.document)
            .map_err(cluster_write_refusal)?;
        let document = value.map(|fields| DocumentBody {
            id: request.document.clone(),
            collection: request.collection.clone(),
            // The composed plane has no per-document version counter:
            // the cluster's replication position stands in for it.
            version: cluster.position_of(&self.serving_member).unwrap_or_default(),
            body: to_wire_value(&DocumentValue::Object(fields)),
        });
        Ok(DocumentReadResponse { found: document.is_some(), document, position: None })
    }

    fn document_write(
        &self,
        tenant: &str,
        request: &DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError> {
        let mut cluster = self.lock()?;
        if tenant != cluster.tenant() {
            return Err(HostingError::Refused("the cluster serves its own tenant only".into()));
        }
        let DocumentPut { id, collection: _, body } = &request.document;
        if id.trim().is_empty() {
            return Err(HostingError::Invalid("the document identifier is empty".into()));
        }
        let operation = if idempotency_key.trim().is_empty() {
            request.operation_id.clone()
        } else {
            idempotency_key.to_owned()
        };
        let write = ComposedWrite::Put {
            operation,
            document: id.clone(),
            body: match to_cluster_value(body) {
                DocumentValue::Object(fields) => fields,
                _ => return Err(HostingError::Invalid("the body is not an object".into())),
            },
        };
        // The backup_ack level is ordered: the regional quorum commits
        // first, then the 2-of-3 backup durability names FULL or
        // PARTIAL on the receipt — the active write never rolls back.
        if matches!(request.ack, Some(crate::registry::Acknowledgement::BackupAck)) {
            let (receipt, backup) = cluster
                .client_write_backup(&self.serving_member, &write)
                .map_err(cluster_write_refusal)?;
            let local = receipt.local();
            return Ok(DocumentWriteResponse {
                outcome: WriteOutcome::Committed(CommitReceipt {
                    sequence: local.sequence().get(),
                    version: local.sequence().get(),
                    operation_id: receipt.operation().to_owned(),
                    acknowledgement: Some(crate::registry::Acknowledgement::BackupAck),
                    backup: Some(match backup {
                        terrabase_cluster::BackupOutcome::Full { .. } => {
                            crate::registry::BackupOutcome::Full
                        }
                        terrabase_cluster::BackupOutcome::Partial { .. } => {
                            crate::registry::BackupOutcome::Partial
                        }
                    }),
                }),
            });
        }
        let level = match request.ack {
            Some(crate::registry::Acknowledgement::GlobalQuorum) => LeaderlessLevel::GlobalQuorum,
            _ => LeaderlessLevel::RegionalQuorum,
        };
        let receipt = cluster
            .client_write(&self.serving_member, &write, level)
            .map_err(cluster_write_refusal)?;
        let local = receipt.local();
        Ok(DocumentWriteResponse {
            outcome: WriteOutcome::Committed(CommitReceipt {
                sequence: local.sequence().get(),
                version: local.sequence().get(),
                operation_id: receipt.operation().to_owned(),
                acknowledgement: Some(match receipt.achieved() {
                    LeaderlessLevel::RegionalQuorum => {
                        crate::registry::Acknowledgement::RegionalQuorum
                    }
                    LeaderlessLevel::GlobalQuorum => {
                        crate::registry::Acknowledgement::GlobalQuorum
                    }
                }),
                backup: None,
            }),
        })
    }

    fn document_delete(
        &self,
        tenant: &str,
        request: &crate::registry::DocumentDeleteRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::DocumentDeleteResponse, HostingError> {
        let mut cluster = self.lock()?;
        if tenant != cluster.tenant() {
            return Err(HostingError::Refused("the cluster serves its own tenant only".into()));
        }
        let operation = if idempotency_key.trim().is_empty() {
            request.operation_id.clone()
        } else {
            idempotency_key.to_owned()
        };
        let write = ComposedWrite::Delete { operation, document: request.document.clone() };
        let receipt = cluster
            .client_write(&self.serving_member, &write, LeaderlessLevel::RegionalQuorum)
            .map_err(cluster_write_refusal)?;
        // A delete with an attached backup set fans its tombstone: a
        // backup that misses tombstones would resurrect deleted
        // documents on the last-resort read.
        let backup = cluster.fan_to_backup(&write, terrabase_cluster::BackupPolicy::default()).map(
            |outcome| match outcome {
                terrabase_cluster::BackupOutcome::Full { .. } => {
                    crate::registry::BackupOutcome::Full
                }
                terrabase_cluster::BackupOutcome::Partial { .. } => {
                    crate::registry::BackupOutcome::Partial
                }
            },
        );
        let local = receipt.local();
        Ok(crate::registry::DocumentDeleteResponse {
            outcome: WriteOutcome::Committed(CommitReceipt {
                sequence: local.sequence().get(),
                version: local.sequence().get(),
                operation_id: receipt.operation().to_owned(),
                acknowledgement: Some(crate::registry::Acknowledgement::RegionalQuorum),
                backup,
            }),
        })
    }

    refused_plane! {
        query_execute(_tenant: &str, _request: &crate::registry::QueryExecuteRequest) -> Result<crate::registry::QueryExecuteResponse, HostingError>, "query.execute";
        query_cancel(_tenant: &str, _request: &crate::registry::QueryCancelRequest) -> Result<crate::registry::QueryCancelResponse, HostingError>, "query.cancel";
        family_read(_tenant: &str, _request: &crate::registry::FamilyReadRequest) -> Result<crate::registry::FamilyReadResponse, HostingError>, "family.read";
        family_write(_tenant: &str, _request: &crate::registry::FamilyWriteRequest, _key: &str) -> Result<crate::registry::FamilyWriteResponse, HostingError>, "family.write";
        backbone_subscribe(_tenant: &str, _request: &crate::registry::BackboneSubscribeRequest) -> Result<(crate::registry::BackboneSubscribeResponse, std::sync::mpsc::Receiver<crate::host::TbepNotification>), HostingError>, "backbone.subscribe";
        backbone_acknowledge(_tenant: &str, _request: &crate::registry::BackboneAcknowledgeRequest) -> Result<crate::registry::BackboneAcknowledgeResponse, HostingError>, "backbone.acknowledge";
        backbone_unsubscribe(_tenant: &str, _request: &crate::registry::BackboneUnsubscribeRequest) -> Result<crate::registry::BackboneUnsubscribeResponse, HostingError>, "backbone.unsubscribe";
        sync_session_setup(_tenant: &str, _request: &crate::registry::SyncSessionSetupRequest) -> Result<crate::registry::SyncSessionSetupResponse, HostingError>, "sync.session_setup";
        sync_delta_pull(_tenant: &str, _request: &crate::registry::SyncDeltaPullRequest) -> Result<crate::registry::SyncDeltaPullResponse, HostingError>, "sync.delta_pull";
        sync_snapshot_pull(_tenant: &str, _request: &crate::registry::SyncSnapshotPullRequest) -> Result<crate::registry::SyncSnapshotPullResponse, HostingError>, "sync.snapshot_pull";
        sync_checkpoint_ack(_tenant: &str, _request: &crate::registry::SyncCheckpointAckRequest) -> Result<crate::registry::SyncCheckpointAckResponse, HostingError>, "sync.checkpoint_ack";
        sync_mutation_submit(_tenant: &str, _request: &crate::registry::SyncMutationSubmitRequest, _key: &str) -> Result<crate::registry::SyncMutationSubmitResponse, HostingError>, "sync.mutation_submit";
        sync_session_close(_tenant: &str, _request: &crate::registry::SyncSessionCloseRequest) -> Result<crate::registry::SyncSessionCloseResponse, HostingError>, "sync.session_close";
        schema_declare(_tenant: &str, _request: &crate::registry::SchemaDeclareRequest, _key: &str) -> Result<crate::registry::SchemaDeclareResponse, HostingError>, "schema.declare";
        schema_status(_tenant: &str, _request: &crate::registry::SchemaStatusRequest) -> Result<crate::registry::SchemaStatusResponse, HostingError>, "schema.status";
        schema_validate(_tenant: &str, _request: &crate::registry::SchemaValidateRequest) -> Result<crate::registry::SchemaValidateResponse, HostingError>, "schema.validate";
        tier_configure(_tenant: &str, _request: &crate::registry::TierConfigureRequest, _key: &str) -> Result<crate::registry::TierConfigureResponse, HostingError>, "tier.configure";
        tier_inspect(_tenant: &str) -> Result<crate::registry::TierInspectResponse, HostingError>, "tier.inspect";
        tier_explain(_tenant: &str) -> Result<crate::registry::TierExplainResponse, HostingError>, "tier.explain";
        tier_dry_run(_tenant: &str) -> Result<crate::registry::TierDryRunResponse, HostingError>, "tier.dry_run";
        tier_run(_tenant: &str, _key: &str) -> Result<crate::registry::TierRunResponse, HostingError>, "tier.run";
        tier_pause(_tenant: &str, _key: &str) -> Result<crate::registry::TierPauseResponse, HostingError>, "tier.pause";
        tier_resume(_tenant: &str, _key: &str) -> Result<crate::registry::TierResumeResponse, HostingError>, "tier.resume";
        document_batch(_tenant: &str, _request: &crate::registry::DocumentBatchRequest, _key: &str) -> Result<crate::registry::DocumentBatchResponse, HostingError>, "document.batch";
        migration_status(_tenant: &str, _request: &crate::registry::MigrationStatusRequest) -> Result<crate::registry::MigrationStatusResponse, HostingError>, "migration.status";
        migration_dry_run(_tenant: &str, _request: &crate::registry::MigrationDryRunRequest) -> Result<crate::registry::MigrationDryRunResponse, HostingError>, "migration.dry_run";
        migration_apply(_tenant: &str, _request: &crate::registry::MigrationApplyRequest, _key: &str) -> Result<crate::registry::MigrationApplyResponse, HostingError>, "migration.apply";
    }

    fn topology_read(
        &self,
        _tenant: &str,
    ) -> Result<crate::registry::TopologyReadResponse, HostingError> {
        let cluster = self.lock()?;
        Ok(crate::registry::TopologyReadResponse {
            binding: crate::registry::BindingKind::LocalUds,
            subsystems: vec![
                "documents".into(),
                "cluster".into(),
                "supervision".into(),
                "topology".into(),
            ],
            engine_topology_available: true,
            cluster_mode: Some(match cluster.mode() {
                ClusterMode::Active => crate::registry::ClusterMode::Active,
                ClusterMode::Backup => crate::registry::ClusterMode::Backup,
            }),
        })
    }

    fn admin_status(&self) -> Result<crate::registry::AdminStatusResponse, HostingError> {
        Ok(crate::registry::AdminStatusResponse {
            registry_version: crate::registry::REGISTRY_VERSION.into(),
            frames_served: 0,
            sessions: 0,
            subscriptions: 0,
            scopes: u32::try_from(self.member_count()).unwrap_or(u32::MAX),
        })
    }

    fn health(&self) -> Result<crate::registry::SupervisionHealthResponse, HostingError> {
        Ok(crate::registry::SupervisionHealthResponse {
            state: crate::registry::HealthState::Serving,
            sessions: 0,
            subscriptions: 0,
            frames_served: 0,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &crate::registry::SupervisionShutdownRequest,
    ) -> Result<crate::registry::SupervisionShutdownResponse, HostingError> {
        Ok(crate::registry::SupervisionShutdownResponse {
            state: crate::registry::HealthState::Draining,
            in_flight: 0,
        })
    }
}

impl ClusterTbepHost {
    fn member_count(&self) -> usize {
        self.cluster.lock().map_or(0, |cluster| cluster.member_names().len())
    }
}

/// Maps the cluster host's write refusal onto the wire's typed error.
fn cluster_write_refusal(refusal: ClusterWriteRefusal) -> HostingError {
    match refusal {
        ClusterWriteRefusal::InsufficientRegions => HostingError::Refused(
            "global_quorum requires at least two selected active Regions".into(),
        ),
        ClusterWriteRefusal::ClusterReadOnly(refusal) => {
            HostingError::ClusterReadOnly(format!("position={}", refusal.position))
        }
        ClusterWriteRefusal::UnknownMember => {
            HostingError::EngineFailure("the serving member is unknown".into())
        }
        ClusterWriteRefusal::QuorumNotReached => {
            HostingError::EngineFailure("the quorum was not reached".into())
        }
        ClusterWriteRefusal::ApplyFailed => {
            HostingError::EngineFailure("a member's apply failed".into())
        }
        ClusterWriteRefusal::AdmissionRefused => {
            HostingError::Refused("the write was refused at admission".into())
        }
        ClusterWriteRefusal::ReadQuorumNotReached => {
            HostingError::EngineFailure("the read quorum was not reached".into())
        }
        ClusterWriteRefusal::EpochRefused => HostingError::Refused("the epoch step refused".into()),
        ClusterWriteRefusal::NoBackupSet => {
            HostingError::Refused("the backup_ack level needs an attached backup set".into())
        }
    }
}
