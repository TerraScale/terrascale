//! Admission-owned serving state for a database's durable shard route.

use std::{collections::BTreeMap, sync::Arc};

use crate::{
    engine::Engine,
    shard_split::ShardRouter,
    types::{OperationId, TenantScope},
};

/// Immutable serving snapshot held by one admitted lease.
pub(crate) struct RouteParticipantRegistry {
    participants: BTreeMap<u64, Arc<Engine>>,
    router: Option<ShardRouter>,
}

impl RouteParticipantRegistry {
    /// Loads the route and refuses a committed multi-file catalog until the
    /// participant registry and cross-file coordinator are qualified.
    pub(crate) fn load(
        engine: Arc<Engine>,
        scope: &TenantScope,
    ) -> Result<Arc<Self>, RegistryError> {
        let Some((router, generation)) =
            engine.load_shard_router(scope).map_err(|_| RegistryError::Catalog)?
        else {
            return Ok(Arc::new(Self { participants: BTreeMap::new(), router: None }));
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
                .validate_shard_target(&target_scope, incarnation, route.shard_id())
                .map_err(|_| RegistryError::TargetUnavailable)?
            {
                return Err(RegistryError::TargetUnavailable);
            }
            participants.insert(route.shard_id(), Arc::new(target));
        }
        Ok(Arc::new(Self { participants, router: Some(router) }))
    }

    pub(crate) fn begin(
        &self,
        scope: &TenantScope,
        key: &[u8],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        let Some(router) = &self.router else {
            let engine =
                self.participants.values().next().ok_or(crate::engine::EngineError::Conflict)?;
            return Ok(engine.begin(scope, engine.read_snapshot(scope)?, operation)?);
        };
        let shard = router.route(key).ok_or(crate::engine::EngineError::Conflict)?;
        let engine = self.participants.get(&shard).ok_or(crate::engine::EngineError::Conflict)?;
        let token = engine.shard_admission_for_key(scope, router, key)?;
        let mut transaction = engine.begin(scope, engine.read_snapshot(scope)?, operation)?;
        transaction.bind_shard_admission(token)?;
        Ok(transaction)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RegistryError {
    Catalog,
    TargetUnavailable,
}
