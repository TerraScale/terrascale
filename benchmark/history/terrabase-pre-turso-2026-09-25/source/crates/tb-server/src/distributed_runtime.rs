//! One signed-v2 document owner and its fixed set of remote inboxes.

use std::collections::BTreeMap;
use std::sync::Arc;

use tb_core::{MemoryBudget, OriginId, WorkerId};
use tb_document::{
    DistributedDocumentStore, DocumentApplyOutcome, DocumentError, DocumentStoreConfig,
    open_distributed,
};
use tb_replication::{
    AcceptedAuthority, CursorLimits, CursorScope, ReplicaReceiver, ReplicationLimits,
};
use tb_storage::StorageConfig;
use tokio::sync::watch;

use crate::replication_server::worker_id;

/// A bounded slice of replay work before the scheduler yields its thread.
pub(crate) const REPLAY_APPLIES_PER_SLICE: usize = 64;

/// The sole owner of the v2 document shard and every signed remote inbox.
pub(crate) struct DocumentMemberRuntime {
    pub(crate) store: DistributedDocumentStore,
    pub(crate) authority: Arc<AcceptedAuthority>,
    origins: Vec<OriginId>,
    applied: BTreeMap<OriginId, watch::Sender<u64>>,
}

impl DocumentMemberRuntime {
    pub(crate) fn first_origin(&self) -> Option<OriginId> {
        self.origins.first().copied()
    }

    pub(crate) fn subscribe_applied(&self, origin: OriginId) -> Option<watch::Receiver<u64>> {
        self.applied.get(&origin).map(watch::Sender::subscribe)
    }

    pub(crate) fn publish_applied(
        &self,
        origin: OriginId,
        sequence: u64,
    ) -> Result<(), DocumentError> {
        let sender = self
            .applied
            .get(&origin)
            .ok_or(DocumentError::Corrupt("signed applied watch missing"))?;
        sender.send_if_modified(|current| {
            if sequence > *current {
                *current = sequence;
                true
            } else {
                false
            }
        });
        Ok(())
    }

    /// Recover one v2 document shard, install exact signed inboxes, and drain
    /// preexisting durable work before process readiness can open.
    pub(crate) fn open(
        authority: &Arc<AcceptedAuthority>,
        budget: &MemoryBudget,
        worker: WorkerId,
        limits: ReplicationLimits,
        cursor_limits: CursorLimits,
    ) -> Result<Self, DocumentError> {
        let view = authority.view();
        let config = DocumentStoreConfig {
            storage: StorageConfig::local(view.documents_path(), view.tenant(), worker),
            database: view.database(),
            shard: view.shard(),
            origin: view.local_origin(),
            epoch: view.epoch(),
            configuration: view.configuration(),
            replication: limits,
        };
        let store = open_distributed(Arc::clone(authority), config, budget.clone())?;
        let origins = view
            .members()
            .filter(|member| member.origin != view.local_origin())
            .map(|member| member.origin)
            .collect::<Vec<_>>();
        let mut receivers = Vec::new();
        receivers
            .try_reserve_exact(origins.len())
            .map_err(|_| DocumentError::Capacity("signed receiver list"))?;
        for &origin in &origins {
            let scope = CursorScope {
                database: view.database(),
                shard: view.shard(),
                origin,
                epoch: view.epoch(),
                configuration: view.configuration(),
            };
            let receiver = ReplicaReceiver::open(
                view,
                StorageConfig::local(
                    view.inbox_path(origin),
                    view.tenant(),
                    worker_id(origin.replica_id),
                ),
                budget.clone(),
                scope,
                cursor_limits,
            )?;
            receivers.push(receiver);
        }
        store.install_receivers(receivers)?;
        store.reconcile_inboxes()?;
        let mut runtime = Self {
            store,
            authority: Arc::clone(authority),
            origins,
            applied: BTreeMap::new(),
        };
        while runtime.replay_slice(REPLAY_APPLIES_PER_SLICE, |_, _| Ok(()))? {
            std::thread::yield_now();
        }
        for &origin in &runtime.origins {
            let applied = runtime.store.applied_prefix(origin)?;
            let (sender, _) = watch::channel(applied);
            runtime.applied.insert(origin, sender);
        }
        Ok(runtime)
    }

    /// Apply at most one slice of newly unblocked durable events. `true` means
    /// a continuation must be scheduled without waiting for another peer.
    pub(crate) fn replay_slice(
        &self,
        max_applied: usize,
        mut on_applied: impl FnMut(OriginId, u64) -> Result<(), DocumentError>,
    ) -> Result<bool, DocumentError> {
        let mut applied = 0;
        loop {
            let mut progressed = false;
            for &origin in &self.origins {
                match self.store.replay_next(origin)? {
                    Some(DocumentApplyOutcome::Applied { sequence }) => {
                        on_applied(origin, sequence)?;
                        applied += 1;
                        progressed = true;
                        metrics::counter!("terrabase_replication_document_replay_applied_total")
                            .increment(1);
                        if applied == max_applied {
                            return Ok(true);
                        }
                    }
                    Some(DocumentApplyOutcome::DurableOnly { .. }) | None => {}
                    Some(DocumentApplyOutcome::Duplicate { .. }) => {
                        return Err(DocumentError::Corrupt("duplicate durable replay outcome"));
                    }
                }
            }
            if !progressed {
                return Ok(false);
            }
        }
    }
}
