//! Admission-owned serving state for a database's durable shard route.

use std::{collections::BTreeMap, sync::Arc};

use crate::{
    engine::Engine,
    shard_split::ShardRouter,
    types::{OperationId, TenantScope},
};

/// One immutable admitted participant at a captured local MVCC cut.
#[derive(Clone)]
pub struct AdmittedShardParticipant {
    /// Durable physical identity initialized by admission before origin service.
    pub identity: crate::shard_split::FileShardIdentity,
    /// Resident engine retained by the database admission owner.
    pub engine: Arc<Engine>,
    /// Scope minted by this exact engine instance for the admitted tenant.
    pub scope: TenantScope,
    /// Owned read snapshot captured with every participant under one publication gate.
    pub snapshot: crate::types::ReadSnapshot,
    /// Canonical ranges assigned to this participant in the captured catalog.
    pub routes: Vec<crate::shard_split::ShardRoute>,
    // Preserve admission even when a participant is moved out of its cut.
    pub(super) admission: Option<(Arc<super::AdmissionPermit>, Arc<super::AdmissionPermit>)>,
}

/// Owned bounded lane inventory and read cut for one admitted database.
#[derive(Clone)]
pub struct AdmittedShardReadCut {
    /// Durable logical database incarnation shared by all participants.
    pub database_incarnation: u128,
    /// Exact catalog generation; absent before the first shard catalog is installed.
    pub route_generation: Option<u64>,
    /// Validated resident participants, each listed once.
    pub participants: Vec<AdmittedShardParticipant>,
    // Owned snapshots must keep counted admission alive, not merely Arc engines.
    pub(super) admission: Option<(Arc<super::AdmissionPermit>, Arc<super::AdmissionPermit>)>,
}

/// Immutable serving snapshot held by one admitted lease.
pub(crate) struct RouteParticipantRegistry {
    source: Arc<Engine>,
    tenant: crate::types::TenantId,
    participants: BTreeMap<u64, Arc<Engine>>,
    router: Option<ShardRouter>,
}

impl RouteParticipantRegistry {
    /// Loads the durable route and validates every committed participant.
    pub(crate) fn load(
        engine: Arc<Engine>,
        scope: &TenantScope,
    ) -> Result<Arc<Self>, RegistryError> {
        let Some((router, generation)) =
            engine.load_shard_router(scope).map_err(|_| RegistryError::Catalog)?
        else {
            return Ok(Arc::new(Self {
                source: Arc::clone(&engine),
                tenant: scope.tenant().clone(),
                participants: BTreeMap::from([(0, engine)]),
                router: None,
            }));
        };
        let identity = engine
            .file_shard_identity(scope)
            .map_err(|_| RegistryError::Catalog)?
            .ok_or(RegistryError::Catalog)?;
        let source_shard = identity.shard_id;
        if !router.table().routes().iter().any(|route| route.shard_id() == source_shard) {
            return Err(RegistryError::Catalog);
        }
        if generation != router.table().generation() {
            return Err(RegistryError::Catalog);
        }
        let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
        let mut participants = BTreeMap::new();
        participants.insert(source_shard, Arc::clone(&engine));
        for route in router.table().routes() {
            if route.shard_id() == source_shard {
                continue;
            }
            let target = engine
                .open_shard_participant(route.shard_id())
                .map_err(|_| RegistryError::TargetUnavailable)?
                .ok_or(RegistryError::TargetUnavailable)?;
            let target_scope = target.scope_for(scope.tenant());
            if !target
                .validate_shard_target(&target_scope, incarnation, route, generation)
                .map_err(|_| RegistryError::TargetUnavailable)?
            {
                return Err(RegistryError::TargetUnavailable);
            }
            participants.insert(route.shard_id(), target);
        }
        Ok(Arc::new(Self {
            source: engine,
            tenant: scope.tenant().clone(),
            participants,
            router: Some(router),
        }))
    }

    pub(crate) fn read_cut(&self) -> Result<AdmittedShardReadCut, crate::EngineError> {
        let engines = self.participants.values().cloned().collect::<Vec<_>>();
        let generation = self.router.as_ref().map(|router| router.table().generation());
        let snapshots = self.source.routed_read_snapshots(&self.tenant, generation, &engines)?;
        let mut participants = Vec::with_capacity(engines.len());
        for ((shard, engine), snapshot) in self.participants.iter().zip(snapshots) {
            let scope = engine.scope_for(&self.tenant);
            let identity =
                engine.file_shard_identity(&scope)?.ok_or(crate::EngineError::Conflict)?;
            if self.router.is_some() && identity.shard_id != *shard {
                return Err(crate::EngineError::Conflict);
            }
            let routes = self
                .router
                .as_ref()
                .map(|router| {
                    router
                        .table()
                        .routes()
                        .iter()
                        .filter(|route| route.shard_id() == *shard)
                        .cloned()
                        .collect()
                })
                .unwrap_or_default();
            participants.push(AdmittedShardParticipant {
                identity,
                engine: engine.clone(),
                scope,
                snapshot,
                routes,
                admission: None,
            });
        }
        Ok(AdmittedShardReadCut {
            database_incarnation: u128::from_be_bytes(self.source.database_id().as_bytes()),
            route_generation: generation,
            participants,
            admission: None,
        })
    }

    pub(crate) fn tenant_id(&self) -> &crate::types::TenantId {
        &self.tenant
    }

    pub(crate) fn is_current(&self) -> Result<bool, crate::EngineError> {
        let scope = self.source.scope_for(&self.tenant);
        let current = self.source.load_shard_router(&scope)?;
        Ok(current.as_ref().map(|(router, _)| router.table().generation())
            == self.router.as_ref().map(|router| router.table().generation()))
    }

    pub(crate) fn participant(&self, keys: &[Vec<u8>]) -> Result<&Engine, crate::EngineError> {
        if !self.is_current()? {
            return Err(crate::EngineError::Conflict);
        }
        let shard = if let Some(router) = &self.router {
            let shard = keys
                .first()
                .and_then(|key| router.route(key))
                .ok_or(crate::EngineError::Conflict)?;
            if keys.iter().any(|key| router.route(key) != Some(shard)) {
                return Err(crate::EngineError::Conflict);
            }
            shard
        } else {
            0
        };
        self.participants.get(&shard).map(AsRef::as_ref).ok_or(crate::EngineError::Conflict)
    }

    pub(crate) fn begin(
        &self,
        scope: &TenantScope,
        key: &[u8],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        self.participant(&[key.to_vec()])?;
        let Some(router) = &self.router else {
            let engine =
                self.participants.values().next().ok_or(crate::engine::EngineError::Conflict)?;
            return Ok(engine.begin(scope, engine.read_snapshot(scope)?, operation)?);
        };
        let shard = router.route(key).ok_or(crate::engine::EngineError::Conflict)?;
        let engine = self.participants.get(&shard).ok_or(crate::engine::EngineError::Conflict)?;
        let scope = engine.scope_for(scope.tenant());
        let (participant_router, _) =
            engine.load_shard_router(&scope)?.ok_or(crate::EngineError::Conflict)?;
        let token = engine.shard_admission_for_key(&scope, &participant_router, key)?;
        let mut transaction = engine.begin(&scope, engine.read_snapshot(&scope)?, operation)?;
        transaction.bind_shard_admission(token)?;
        Ok(transaction)
    }

    pub(crate) fn begin_batch(
        &self,
        scope: &TenantScope,
        keys: &[Vec<u8>],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        self.participant(keys)?;
        let Some(router) = &self.router else {
            let engine =
                self.participants.values().next().ok_or(crate::engine::EngineError::Conflict)?;
            return Ok(engine.begin(scope, engine.read_snapshot(scope)?, operation)?);
        };
        let Some(shard) = keys.first().and_then(|key| router.route(key)) else {
            return Err(crate::engine::EngineError::Conflict);
        };
        let engine = self.participants.get(&shard).ok_or(crate::engine::EngineError::Conflict)?;
        let scope = engine.scope_for(scope.tenant());
        let (participant_router, _) =
            engine.load_shard_router(&scope)?.ok_or(crate::EngineError::Conflict)?;
        let token = engine.shard_admission_for_keys(&scope, &participant_router, keys)?;
        let mut transaction = engine.begin(&scope, engine.read_snapshot(&scope)?, operation)?;
        transaction.bind_shard_admission(token)?;
        Ok(transaction)
    }

    pub(crate) fn resolve_document_request(
        &self,
        scope: &TenantScope,
        key: &[u8],
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<
        Option<(Arc<Engine>, crate::CommitReceipt, Option<String>)>,
        crate::engine::EngineError,
    > {
        // Validate the serving destination first: an unavailable committed
        // target must never turn into source document service. Historical
        // receipts stay on their original file, with original durability and
        // sequence; copying documents does not copy operation provenance.
        self.participant(&[key.to_vec()])?;
        for engine in self.participants.values() {
            let scope = engine.scope_for(scope.tenant());
            if let Some((receipt, revision)) =
                engine.resolve_document_request(&scope, operation.clone(), fingerprint)?
            {
                return Ok(Some((Arc::clone(engine), receipt, revision)));
            }
        }
        Ok(None)
    }
    pub(crate) fn resolve_batch_request(
        &self,
        scope: &TenantScope,
        keys: &[Vec<u8>],
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<Option<(Arc<Engine>, crate::CommitReceipt, usize)>, crate::EngineError> {
        for key in keys {
            self.participant(std::slice::from_ref(key))?;
        }

        for engine in self.participants.values() {
            let scope = engine.scope_for(scope.tenant());
            if let Some((receipt, count)) =
                engine.resolve_batch_request(&scope, operation.clone(), fingerprint)?
            {
                return Ok(Some((Arc::clone(engine), receipt, count)));
            }
        }
        Ok(None)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RegistryError {
    Catalog,
    TargetUnavailable,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, TenantId,
    };
    use crate::{CommitOutcome, EngineConfig, LocalDurability};

    #[test]
    fn ordinary_admission_and_historical_receipts_use_their_own_engine_scope() {
        let root =
            std::env::temp_dir().join(format!("registry-replay-{}", crate::identity::generate()));
        let source =
            Arc::new(Engine::open(EngineConfig::durable(root.join("source")).unwrap()).unwrap());
        let target =
            Arc::new(Engine::open(EngineConfig::durable(root.join("target")).unwrap()).unwrap());
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = source.scope_for(&tenant);
        let collection = CollectionName::try_new("records").unwrap();
        let id = DocumentId::try_new("a").unwrap();
        let key = crate::shard_split::canonical_shard_key(&collection, &id);
        let ordinary = RouteParticipantRegistry::load(source.clone(), &scope).unwrap();
        let operation = OperationId::try_new("original-put").unwrap();
        let mut tx = ordinary.begin(&scope, &key, operation.clone()).unwrap();
        tx.put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(1)),
            ExpectedVersion::Any,
        )
        .unwrap();
        tx.bind_document_request([7; 32]).unwrap();
        let CommitOutcome::Committed(original) = tx.commit(LocalDurability::Synced) else {
            panic!("original write");
        };
        let registry = RouteParticipantRegistry {
            source: source.clone(),
            tenant: tenant.clone(),
            participants: BTreeMap::from([(11, source.clone()), (0, target.clone())]),
            router: None,
        };
        assert!(std::ptr::eq(registry.participant(&[key.clone()]).unwrap(), target.as_ref()));
        let target_scope = target.scope_for(&tenant);
        let target_snapshot = target.read_snapshot(&target_scope).unwrap();
        let (resolved, receipt, revision) = registry
            .resolve_document_request(&target_scope, &key, operation.clone(), [7; 32])
            .unwrap()
            .unwrap();
        assert!(Arc::ptr_eq(&resolved, &source));
        assert_eq!(receipt.sequence(), original.sequence());
        assert!(revision.is_some());
        assert_eq!(
            target.read_snapshot(&target_scope).unwrap(),
            target_snapshot,
            "replay cannot publish a target operation"
        );
        assert!(
            registry.resolve_document_request(&target_scope, &key, operation, [8; 32]).is_err()
        );
        let batch_operation = OperationId::try_new("original-batch").unwrap();
        let mut tx = ordinary.begin(&scope, &key, batch_operation.clone()).unwrap();
        tx.put(Document::new(id, collection, DocumentValue::Integer(2)), ExpectedVersion::Any)
            .unwrap();
        tx.bind_batch_request([9; 32], 1).unwrap();
        let CommitOutcome::Committed(original) = tx.commit(LocalDurability::Synced) else {
            panic!("original batch");
        };
        let (resolved, receipt, count) = registry
            .resolve_batch_request(&target_scope, &[key], batch_operation, [9; 32])
            .unwrap()
            .unwrap();
        assert!(Arc::ptr_eq(&resolved, &source));
        assert_eq!(receipt.sequence(), original.sequence());
        assert_eq!(count, 1);
        drop(registry);
        drop(ordinary);
        drop(resolved);
        drop(source);
        drop(target);
        std::fs::remove_dir_all(root).unwrap();
    }
}
