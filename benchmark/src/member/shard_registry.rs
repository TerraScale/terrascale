//! Admission-owned serving state for a database's durable shard route.

use std::sync::Arc;

use crate::{
    engine::Engine,
    shard_split::ShardRouter,
    types::{OperationId, TenantScope},
};

/// Immutable serving snapshot held by one admitted lease.
pub(crate) struct RouteParticipantRegistry {
    engine: Arc<Engine>,
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
            return Ok(Arc::new(Self { engine, router: None }));
        };
        if router.table().routes().len() != 1 {
            return Err(RegistryError::CommittedMultiParticipant);
        }
        if generation != router.table().generation() {
            return Err(RegistryError::Catalog);
        }
        Ok(Arc::new(Self { engine, router: Some(router) }))
    }

    pub(crate) fn begin(
        &self,
        scope: &TenantScope,
        key: &[u8],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        let mut transaction =
            self.engine.begin(scope, self.engine.read_snapshot(scope)?, operation)?;
        if let Some(router) = &self.router {
            let token = self.engine.shard_admission_for_key(scope, router, key)?;
            transaction.bind_shard_admission(token)?;
        }
        Ok(transaction)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RegistryError {
    Catalog,
    CommittedMultiParticipant,
}
