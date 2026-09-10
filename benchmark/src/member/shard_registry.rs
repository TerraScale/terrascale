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
    /// Physical shard stored at the logical database root.
    pub root_shard_id: u64,
    /// Logical database identity of the admitted source database.
    pub database_id: String,
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
        let restored_binding =
            engine.restored_route_binding(scope).map_err(|_| RegistryError::Catalog)?;
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
            let restored = target
                .restored_route_binding(&target_scope)
                .map_err(|_| RegistryError::TargetUnavailable)?;
            let valid = if let Some(binding) = restored {
                if restored_binding != Some(binding) {
                    return Err(RegistryError::TargetUnavailable);
                }
                target.validate_restored_shard_target(&target_scope, route, generation, &binding)
            } else {
                target.validate_shard_target(&target_scope, incarnation, route, generation)
            }
            .map_err(|_| RegistryError::TargetUnavailable)?;
            if !valid {
                return Err(RegistryError::TargetUnavailable);
            }
            participants.insert(route.shard_id(), target);
        }
        engine
            .recover_cross_file(scope, &participants, generation)
            .map_err(|_| RegistryError::TargetUnavailable)?;
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
        self.cut_from_snapshots(generation, snapshots)
    }

    pub(crate) fn verify_published_restore(
        &self,
        identity: &crate::backup::RestoreIdentity,
    ) -> Result<Option<crate::backup::RestoreMarkerFacts>, crate::backup::BackupError> {
        let engines = self.participants.values().cloned().collect::<Vec<_>>();
        let generation = self.router.as_ref().map(|router| router.table().generation());
        self.source.verify_restore_with_registered_participants(
            &self.tenant,
            generation,
            &engines,
            identity,
            |snapshots| {
                let cut = self
                    .cut_from_snapshots(generation, snapshots)
                    .map_err(|_| crate::backup::BackupError::ChecksumMismatch)?;
                crate::backup::verify_envelope_restore(&self.source, &cut, identity)
            },
        )
    }

    fn cut_from_snapshots(
        &self,
        generation: Option<u64>,
        snapshots: Vec<crate::types::ReadSnapshot>,
    ) -> Result<AdmittedShardReadCut, crate::EngineError> {
        if snapshots.len() != self.participants.len() {
            return Err(crate::EngineError::Conflict);
        }
        let mut participants = Vec::with_capacity(self.participants.len());
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
            root_shard_id: self
                .source
                .file_shard_identity(&self.source.scope_for(&self.tenant))?
                .ok_or(crate::EngineError::Conflict)?
                .shard_id,
            database_id: self.source.database_id().to_string(),
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
        if !Arc::ptr_eq(engine, &self.source) {
            return self.begin_batch(scope, &[key.to_vec()], operation);
        }
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
        if !self.is_current()? || scope.tenant() != &self.tenant {
            return Err(crate::EngineError::Conflict);
        }
        if let Some(router) = &self.router {
            let shards = keys
                .iter()
                .map(|key| router.route(key).ok_or(crate::EngineError::Conflict))
                .collect::<Result<std::collections::BTreeSet<_>, _>>()?;
            let touches_child = shards.iter().any(|shard| {
                self.participants
                    .get(shard)
                    .is_some_and(|engine| !Arc::ptr_eq(engine, &self.source))
            });
            if shards.len() > 1 || touches_child {
                let cut = self.read_cut()?;
                let source_scope = self.source.scope_for(&self.tenant);
                let participants = cut
                    .participants
                    .into_iter()
                    .map(|participant| {
                        (participant.identity.shard_id, (participant.engine, participant.snapshot))
                    })
                    .collect::<BTreeMap<_, _>>();
                let snapshot = participants
                    .get(&cut.root_shard_id)
                    .ok_or(crate::EngineError::Conflict)?
                    .1
                    .clone();
                let mut transaction = self.source.begin(&source_scope, snapshot, operation)?;
                transaction.bind_cross_file_batch(crate::engine::CrossFileBatch {
                    router: router.clone(),
                    participants,
                    keys: keys.iter().cloned().collect(),
                    uniform_contexts: BTreeMap::new(),
                });
                return Ok(transaction);
            }
        }
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

    pub(crate) fn batch_coordinator(
        &self,
        keys: &[Vec<u8>],
    ) -> Result<&Engine, crate::EngineError> {
        if keys.is_empty() {
            return Err(crate::EngineError::InvalidDocument);
        }
        for key in keys {
            self.participant(std::slice::from_ref(key))?;
        }
        if let Some(router) = &self.router {
            let first = router.route(&keys[0]);
            let touches_child = first
                .and_then(|shard| self.participants.get(&shard))
                .is_some_and(|engine| !Arc::ptr_eq(engine, &self.source));
            if touches_child || keys.iter().any(|key| router.route(key) != first) {
                return Ok(&self.source);
            }
        }
        self.participant(keys)
    }

    pub(crate) fn batch_commit_receipts(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<Vec<(Arc<Engine>, crate::CommitReceipt)>>, crate::EngineError> {
        let source_scope = self.source.scope_for(scope.tenant());
        let Some((identity, frontier, completion)) =
            self.source.cross_file_completion(&source_scope, operation)?
        else {
            return Ok(None);
        };
        let mut receipts = Vec::new();
        for (shard, engine) in &self.participants {
            let name = format!("shard-{shard}");
            let baseline = identity.participants.get(&name).ok_or(crate::EngineError::Conflict)?;
            let sequence = frontier.participants.get(&name).ok_or(crate::EngineError::Conflict)?;
            if sequence > baseline {
                let receipt = engine.cross_file_installed_receipt(
                    &engine.scope_for(scope.tenant()),
                    operation,
                    *sequence,
                )?;
                receipts.push((engine.clone(), receipt));
            }
        }
        // The coordinator decision and original request receipt need the
        // promised durability proof as well as every customer origin commit.
        receipts.push((self.source.clone(), completion));
        Ok(Some(receipts))
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

        let source_scope = self.source.scope_for(scope.tenant());
        if self.source.read_cross_file_decision(&source_scope, &operation)?.is_some() {
            self.source.recover_cross_file(
                &source_scope,
                &self.participants,
                self.router.as_ref().map_or(0, |router| router.table().generation()),
            )?;
            return self.source.resolve_batch_request(&source_scope, operation, fingerprint).map(
                |result| result.map(|(receipt, count)| (self.source.clone(), receipt, count)),
            );
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
    use crate::{
        CommitOutcome, EngineConfig, LocalDurability,
        operations::{QueryInput, SqlQueryText},
    };
    use std::collections::BTreeMap;

    fn signed_batch_budget(
        engine: &Engine,
        scope: &TenantScope,
    ) -> crate::usage::VerifiedAllocation {
        use aws_lc_rs::{
            digest::{SHA256, digest},
            rand::SystemRandom,
            signature::{ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair, KeyPair},
        };
        let rng = SystemRandom::new();
        let pkcs8 = EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &rng).unwrap();
        let key =
            EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, pkcs8.as_ref()).unwrap();
        let mut public = vec![
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06,
            0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
        ];
        public.extend_from_slice(key.public_key().as_ref());
        let key_hash = digest(&SHA256, &public);
        let key_id = hex::encode(key_hash.as_ref());
        let database = engine.database_owner().database().to_text();
        let region = engine.database_owner().region().as_str().to_ascii_lowercase();
        let payload = serde_json::to_vec(&serde_json::json!({"schema_version":2,"grant":{
            "schema_version":2,"kind":"regional_write_pool_refill","refill_operation_id":"cross-file-refill",
            "pool_id":"cross-file-pool","cycle_id":"cycle","rate_nano_dollar":1,
            "organization_id":scope.tenant().as_str(),"database_id":database,"branch_id":"main","holder_id":"test-holder",
            "region":region,"selected_regions":[region],"placement":"cross-file-fixture",
            "local_units":8,"aggregate_units":8,"included_local_units":8,"paid_local_units":0,
            "included_aggregate_units":8,"paid_aggregate_units":0,"pool_sequence":1,
            "policy_epoch":1,"consent_epoch":1,"writer_epoch":1,
            "issued_at":"2026-01-01T00:00:00Z","expires_at":"2099-01-01T00:00:00Z"
        }})).unwrap();
        let mut message = crate::usage::ALLOCATION_SIGNING_DOMAIN.to_vec();
        message.extend_from_slice(&payload);
        let signature = key.sign(&rng, &message).unwrap();
        let mut wrapped = b"TSCS".to_vec();
        wrapped.extend_from_slice(&[1, 1]);
        wrapped.extend_from_slice(key_hash.as_ref());
        wrapped.extend_from_slice(&u16::try_from(signature.as_ref().len()).unwrap().to_be_bytes());
        wrapped.extend_from_slice(signature.as_ref());
        let wire = serde_json::to_vec(&crate::usage::SignedAllocation {
            payload,
            signature: wrapped,
            key_id: key_id.clone(),
        })
        .unwrap();
        crate::usage::VerifiedAllocation::verify(
            &wire,
            &key_id,
            &public,
            &region,
            "cross-file-fixture",
            scope.tenant().as_str(),
            &database,
            "test-holder",
            1,
            1,
            1,
            1_767_225_600_000,
        )
        .unwrap()
    }

    fn split_registry(
        tag: &str,
    ) -> (std::path::PathBuf, Arc<RouteParticipantRegistry>, TenantScope) {
        use crate::engine::shard_split::{RouteCatalog, ScopedShardEngine, encode_catalog};
        use crate::shard_split::{
            CutoverContext, RouteTable, ShardSplit, SplitContext, SplitEngineAdapter,
            SplitStateStore, WriterFence,
        };
        let root = std::env::temp_dir()
            .join(format!("cross-file-registry-{tag}-{}", crate::identity::generate()));
        let owner = crate::storage::DatabaseOwner::local_development();
        let keys = crate::engine::tests::test_storage_keys(&owner, 91);
        let source = Arc::new(
            Engine::open(
                EngineConfig::durable(&root)
                    .unwrap()
                    .with_owner(owner.clone())
                    .with_storage_keys(keys.clone()),
            )
            .unwrap(),
        );
        source.activate_origin_epoch(1).unwrap();
        let scope = source
            .scope_for(&TenantId::try_new(&source.database_owner().tenant().to_text()).unwrap());
        let placement_owner = source.database_owner();
        source
            .configure_usage_placement(
                crate::usage::UsagePlacement::try_new(
                    scope.tenant().clone(),
                    &placement_owner.project().to_string(),
                    &placement_owner.database().to_text(),
                    "main",
                    placement_owner.region(),
                    vec![placement_owner.region()],
                    "cross-file-fixture",
                )
                .unwrap(),
            )
            .unwrap();
        let incarnation = u128::from_be_bytes(source.database_id().as_bytes());
        let collection = CollectionName::try_new("records").unwrap();
        let boundary = crate::shard_split::canonical_shard_key(
            &collection,
            &DocumentId::try_new("m").unwrap(),
        );
        let context = SplitContext::new(91, incarnation, 11, 12, 1, 0);
        let mut split = ShardSplit::plan_with_context(None, boundary, context).unwrap();
        source
            .install_shard_route_catalog(
                &scope,
                incarnation,
                None,
                &encode_catalog(&RouteCatalog { table: RouteTable::single(11), generation: 0 })
                    .unwrap(),
                &split.durable_state(),
                OperationId::try_new("initial-route").unwrap(),
            )
            .unwrap();
        let mut adapter = ScopedShardEngine::new(source.clone(), scope.clone());
        adapter.save(&split.durable_state()).unwrap();
        adapter.create_split_target(&context).unwrap();
        split.begin_backfill().unwrap();
        let through = source.published_sequence().unwrap().get();
        split.begin_catch_up(through).unwrap();
        split.note_source_high_water(through);
        split.note_acked(through);
        split.note_target_applied(through);
        adapter.save(&split.durable_state()).unwrap();
        let through = source.published_sequence().unwrap().get();
        split.note_source_high_water(through);
        split.note_acked(through);
        split.note_target_applied(through);
        adapter.prepare_split_verification(&context, through).unwrap();
        let mut fence = WriterFence::new();
        fence.begin_drain(through).unwrap();
        let permit = fence.try_fence(through).unwrap();
        source
            .install_shard_split_route(
                &scope,
                &CutoverContext {
                    split: context,
                    route_generation: 0,
                    drained_through: through,
                    permit,
                },
                &split.durable_state(),
                OperationId::try_new("cutover").unwrap(),
            )
            .unwrap();
        let registry = RouteParticipantRegistry::load(source, &scope).unwrap();
        (root, registry, scope)
    }

    #[test]
    fn mixed_batch_uses_public_transaction_staging_and_replays_complete_receipt() {
        let (root, registry, scope) = split_registry("public-batch");
        let collection = CollectionName::try_new("records").unwrap();
        let ids = [DocumentId::try_new("a").unwrap(), DocumentId::try_new("z").unwrap()];
        let keys = ids
            .iter()
            .map(|id| crate::shard_split::canonical_shard_key(&collection, id))
            .collect::<Vec<_>>();
        let operation = OperationId::try_new("mixed-request").unwrap();
        let mut tx = registry.begin_batch(&scope, &keys, operation.clone()).unwrap();
        for (id, key) in ids.iter().zip(&keys) {
            let frontier = registry
                .participant(std::slice::from_ref(key))
                .unwrap()
                .lock_storage()
                .unwrap()
                .port
                .live_origin_prefixes(scope.tenant())
                .unwrap()
                .into_iter()
                .map(|entry| (entry.origin, entry.origin_sequence))
                .collect();
            tx.require_routed_uniform_context(&collection, id, frontier).unwrap();
        }
        for id in &ids {
            tx.put(
                Document::new(
                    id.clone(),
                    collection.clone(),
                    DocumentValue::Object(BTreeMap::from([(
                        "value".into(),
                        DocumentValue::Integer(1),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        }
        tx.bind_batch_request([7; 32], 2).unwrap();
        let owner = registry.source.database_owner();
        tx.attach_usage_admission(
            crate::usage::UsageAdmissionContext::try_new(
                &owner.project().to_string(),
                &owner.database().to_text(),
                "main",
                owner.region(),
                crate::usage::UsageFactId::try_new(operation.as_str()).unwrap(),
                1_000,
                1,
                2,
            )
            .unwrap(),
        )
        .unwrap();
        tx.attach_usage_budget(signed_batch_budget(&registry.source, &scope)).unwrap();
        let CommitOutcome::Committed(receipt) = tx.commit(LocalDurability::Synced) else {
            panic!("mixed public transaction must commit")
        };
        let replay = registry
            .resolve_batch_request(&scope, &keys, operation.clone(), [7; 32])
            .unwrap()
            .unwrap();
        assert_eq!(replay.1, receipt);
        assert_eq!(replay.2, 2);
        let facts = registry
            .participants
            .values()
            .flat_map(|engine| {
                engine
                    .usage_page(
                        &engine.scope_for(scope.tenant()),
                        crate::usage::UsagePageRequest::try_new(None, 100, None).unwrap(),
                    )
                    .unwrap()
                    .facts()
                    .to_vec()
            })
            .filter(|fact| fact.fact().identity().id().as_str() == operation.as_str())
            .collect::<Vec<_>>();
        assert_eq!(
            facts.len(),
            1,
            "the complete logical request is billed exactly once across files"
        );
        assert_eq!(facts[0].fact().delta(), 2);
        let debits = registry
            .participants
            .values()
            .flat_map(|engine| {
                engine.usage_budget_consumptions(&engine.scope_for(scope.tenant()), 0, 64).unwrap()
            })
            .collect::<Vec<_>>();
        assert_eq!(debits.len(), 1, "replay must not duplicate the full logical budget debit");
        assert_eq!(debits[0].local_units, 2);
        assert!(registry.resolve_batch_request(&scope, &keys, operation.clone(), [8; 32]).is_err());
        let proofs = registry.batch_commit_receipts(&scope, &operation).unwrap().unwrap();
        assert_eq!(proofs.len(), 3, "two origin receipts and the coordinator completion");
        let root_publications = registry
            .source
            .lock_storage()
            .unwrap()
            .port
            .replication_publications(scope.tenant(), 0, 1000)
            .unwrap();
        let active = root_publications
            .iter()
            .find(|(_, publication)| publication.commit_ulid.starts_with("cross-file-prepare-"))
            .unwrap();
        let publications = proofs
            .iter()
            .map(|(engine, receipt)| {
                let rows = engine
                    .lock_storage()
                    .unwrap()
                    .port
                    .replication_publications(scope.tenant(), receipt.sequence().get() - 1, 1)
                    .unwrap();
                let (sequence, publication) = rows.into_iter().next().unwrap();
                assert_eq!(sequence, receipt.sequence().get());
                publication
            })
            .collect::<Vec<_>>();
        let complete = publications
            .iter()
            .find(|publication| publication.commit_ulid.starts_with("cross-file-visible-"))
            .unwrap();
        let complete_context = crate::engine::merge::decode_context(&complete.context).unwrap();
        for installed in
            publications.iter().filter(|publication| publication.commit_ulid == operation.as_str())
        {
            let context = crate::engine::merge::decode_context(&installed.context).unwrap();
            assert!(
                context.frontier().get(&active.1.origin).copied().unwrap_or(0)
                    >= active.1.origin_sequence
            );
            assert!(
                complete_context.frontier().get(&installed.origin).copied().unwrap_or(0)
                    >= installed.origin_sequence
            );
        }
        let cut = registry.read_cut().unwrap();
        let plan = registry
            .source
            .queries()
            .compile(QueryInput::sql(
                SqlQueryText::try_new("SELECT _id, value FROM records ORDER BY _id").unwrap(),
            ))
            .unwrap();
        let result = registry.source.queries().execute_admitted(&plan, &cut).unwrap();
        assert_eq!(result.rows().len(), 2);
        let mut later = registry
            .begin(&scope, &keys[0], OperationId::try_new("later-ordinary").unwrap())
            .unwrap();
        later
            .put(
                Document::new(
                    ids[0].clone(),
                    collection.clone(),
                    DocumentValue::Object(BTreeMap::from([(
                        "value".into(),
                        DocumentValue::Integer(2),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(later.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        let mut patch = registry
            .begin_batch(&scope, &keys, OperationId::try_new("mixed-patch").unwrap())
            .unwrap();
        assert_eq!(
            patch.read_in_collection(&collection, &ids[0]).unwrap().unwrap().value(),
            &DocumentValue::Object(BTreeMap::from([("value".into(), DocumentValue::Integer(2))]))
        );
        assert_eq!(
            patch.read(&ids[0]).unwrap(),
            patch.read_in_collection(&collection, &ids[0]).unwrap()
        );
        let routed = registry
            .begin(&scope, &keys[0], OperationId::try_new("revision-probe").unwrap())
            .unwrap();
        let revision = routed.revision_in_collection(&collection, &ids[0]).unwrap();
        assert!(revision.is_some());
        assert_eq!(patch.revision_in_collection(&collection, &ids[0]).unwrap(), revision);
        drop(routed);
        for id in &ids {
            patch.delete_in_collection(&collection, id.clone(), ExpectedVersion::Any);
        }
        assert!(matches!(patch.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert_eq!(
            registry.source.queries().execute_admitted(&plan, &cut).unwrap().rows().len(),
            2,
            "old pinned cut remains valid"
        );
        assert!(
            registry
                .source
                .queries()
                .execute_admitted(&plan, &registry.read_cut().unwrap())
                .unwrap()
                .rows()
                .is_empty()
        );
        drop(proofs);
        drop(replay);
        drop(cut);
        drop(registry);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn mixed_batch_partial_install_is_hidden_and_recovers_before_registry_publication() {
        let (root, registry, scope) = split_registry("recover-batch");
        let collection = CollectionName::try_new("records").unwrap();
        let ids = [DocumentId::try_new("a").unwrap(), DocumentId::try_new("z").unwrap()];
        let keys = ids
            .iter()
            .map(|id| crate::shard_split::canonical_shard_key(&collection, id))
            .collect::<Vec<_>>();
        let operation = OperationId::try_new("crash-request").unwrap();
        let cut = registry.read_cut().unwrap();
        let mut tx = registry.begin_batch(&scope, &keys, operation.clone()).unwrap();
        for id in &ids {
            tx.put(
                Document::new(id.clone(), collection.clone(), DocumentValue::Integer(1)),
                ExpectedVersion::Any,
            )
            .unwrap();
        }
        tx.bind_batch_request([8; 32], 2).unwrap();
        registry.source.interrupt_cross_file_after_installs(1);
        assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Unknown(_)));
        assert!(matches!(registry.read_cut(), Err(crate::EngineError::Conflict)));
        for engine in registry.participants.values() {
            assert_eq!(
                engine.read_snapshot(&engine.scope_for(scope.tenant())),
                Err(crate::EngineError::Conflict)
            );
            assert!(
                matches!(
                    engine.tenant_commits(
                        scope.tenant(),
                        crate::types::StorageSequence::new(0),
                        100
                    ),
                    Err(crate::EngineError::Conflict)
                ),
                "reactive journal must not leak an installed participant"
            );
        }
        for participant in &cut.participants {
            let read = participant
                .engine
                .begin(
                    &participant.scope,
                    participant.snapshot.clone(),
                    OperationId::try_new("old-cut").unwrap(),
                )
                .unwrap();
            for id in &ids {
                assert!(read.read_in_collection(&collection, id).unwrap().is_none());
            }
        }
        let tenant = scope.tenant().clone();
        let restoration_owner = registry.source.database_owner().clone();
        let restoration_keys = crate::engine::tests::test_storage_keys(&restoration_owner, 91);
        drop(cut);
        drop(registry);
        let reopened = Arc::new(
            Engine::open(
                EngineConfig::existing_durable(&root)
                    .unwrap()
                    .with_owner(restoration_owner)
                    .with_storage_keys(restoration_keys),
            )
            .unwrap(),
        );
        let scope = reopened.scope_for(&tenant);
        let registry = RouteParticipantRegistry::load(reopened, &scope).unwrap();
        let replay = registry
            .resolve_batch_request(&scope, &keys, operation.clone(), [8; 32])
            .unwrap()
            .unwrap();
        assert_eq!(replay.2, 2);
        assert!(registry.source.read_cross_file_active(&scope).unwrap().is_none());
        assert_eq!(registry.batch_commit_receipts(&scope, &operation).unwrap().unwrap().len(), 3);
        for (id, key) in ids.iter().zip(&keys) {
            let read = registry
                .begin(&scope, key, OperationId::try_new("read-after-recovery").unwrap())
                .unwrap();
            assert_eq!(
                read.read_in_collection(&collection, id).unwrap().unwrap().value(),
                &DocumentValue::Integer(1)
            );
        }
        drop(replay);
        drop(registry);
        std::fs::remove_dir_all(root).unwrap();
    }

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

    fn put(
        engine: &Engine,
        tenant: &TenantId,
        operation: &str,
        id: &str,
        collection: &str,
        fields: &[(&str, DocumentValue)],
    ) {
        let scope = engine.scope_for(tenant);
        let mut value = BTreeMap::new();
        for (name, field) in fields {
            value.insert((*name).to_owned(), field.clone());
        }
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).unwrap(),
                OperationId::try_new(operation).unwrap(),
            )
            .unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(id).unwrap(),
                    CollectionName::try_new(collection).unwrap(),
                    DocumentValue::Object(value),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    }

    fn text(value: &str) -> DocumentValue {
        DocumentValue::String(value.to_owned())
    }

    fn execute_routed_sql(
        source: &Arc<Engine>,
        target: &Arc<Engine>,
        tenant: &TenantId,
        sql: &str,
    ) -> crate::query::QueryResult {
        let split_key = |collection: &str| {
            crate::shard_split::canonical_shard_key(
                &CollectionName::try_new(collection).unwrap(),
                &DocumentId::try_new("m-record").unwrap(),
            )
        };
        let records_split_key = split_key("records");
        let archive_split_key = split_key("archive");
        let source_scope = source.scope_for(tenant);
        let target_scope = target.scope_for(tenant);
        let cut = AdmittedShardReadCut {
            root_shard_id: 1,
            database_id: source.database_id().to_string(),
            database_incarnation: 0x1234,
            route_generation: Some(1),
            participants: vec![
                AdmittedShardParticipant {
                    identity: crate::shard_split::FileShardIdentity {
                        database_incarnation: 0x1234,
                        shard_id: 1,
                        region: crate::regions::RegionCode::GRU,
                        origin_lane_incarnation: 0x101,
                    },
                    engine: Arc::clone(source),
                    scope: source_scope.clone(),
                    snapshot: source.read_snapshot(&source_scope).unwrap(),
                    routes: vec![
                        crate::shard_split::ShardRoute::new(
                            1,
                            None,
                            Some(records_split_key.clone()),
                        )
                        .unwrap(),
                        crate::shard_split::ShardRoute::new(
                            1,
                            None,
                            Some(archive_split_key.clone()),
                        )
                        .unwrap(),
                    ],
                    admission: None,
                },
                AdmittedShardParticipant {
                    identity: crate::shard_split::FileShardIdentity {
                        database_incarnation: 0x1234,
                        shard_id: 2,
                        region: crate::regions::RegionCode::GRU,
                        origin_lane_incarnation: 0x102,
                    },
                    engine: Arc::clone(target),
                    scope: target_scope.clone(),
                    snapshot: target.read_snapshot(&target_scope).unwrap(),
                    routes: vec![
                        crate::shard_split::ShardRoute::new(2, Some(records_split_key), None)
                            .unwrap(),
                        crate::shard_split::ShardRoute::new(2, Some(archive_split_key), None)
                            .unwrap(),
                    ],
                    admission: None,
                },
            ],
            admission: None,
        };
        let plan =
            source.queries().compile(QueryInput::sql(SqlQueryText::try_new(sql).unwrap())).unwrap();
        source.queries().execute_admitted(&plan, &cut).unwrap()
    }

    #[test]
    fn routed_sql_reads_patched_target_and_never_falls_back_to_stale_source() {
        let root =
            std::env::temp_dir().join(format!("routed-sql-patch-{}", crate::identity::generate()));
        let source =
            Arc::new(Engine::open(EngineConfig::durable(root.join("source")).unwrap()).unwrap());
        let target =
            Arc::new(Engine::open(EngineConfig::durable(root.join("target")).unwrap()).unwrap());
        let tenant = TenantId::try_new("tenant").unwrap();
        put(&source, &tenant, "source-old", "z-record", "records", &[("status", text("old"))]);
        // This is the routed PATCH destination. It deliberately reuses the
        // same id so a source fallback would return the stale value.
        put(
            &target,
            &tenant,
            "target-patch",
            "z-record",
            "records",
            &[("status", text("patched"))],
        );
        let result =
            execute_routed_sql(&source, &target, &tenant, "SELECT _id, status FROM records");
        assert_eq!(result.rows().len(), 1);
        assert_eq!(result.rows()[0].projected()[1].1, DocumentValue::String("patched".into()));
        drop(target);
        drop(source);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn routed_sql_merges_aggregate_join_group_and_fulltext_across_shards() {
        let root = std::env::temp_dir()
            .join(format!("routed-sql-families-{}", crate::identity::generate()));
        let source =
            Arc::new(Engine::open(EngineConfig::durable(root.join("source")).unwrap()).unwrap());
        let target =
            Arc::new(Engine::open(EngineConfig::durable(root.join("target")).unwrap()).unwrap());
        let tenant = TenantId::try_new("tenant").unwrap();
        put(
            &source,
            &tenant,
            "source-a",
            "a-record",
            "records",
            &[("state", text("open")), ("body", text("alpha quick"))],
        );
        put(
            &target,
            &tenant,
            "target-z",
            "z-record",
            "records",
            &[("state", text("closed")), ("body", text("zeta quick"))],
        );
        put(&source, &tenant, "archive-a", "a-record", "archive", &[("kind", text("invoice"))]);
        put(&target, &tenant, "archive-z", "z-record", "archive", &[("kind", text("receipt"))]);
        let aggregate =
            execute_routed_sql(&source, &target, &tenant, "SELECT COUNT(*) AS total FROM records");
        assert_eq!(aggregate.rows()[0].projected()[0].1, DocumentValue::Integer(2));
        let grouped = execute_routed_sql(
            &source,
            &target,
            &tenant,
            "SELECT state, COUNT(*) AS total FROM records GROUP BY state ORDER BY state",
        );
        assert_eq!(grouped.rows().len(), 2);
        let joined = execute_routed_sql(
            &source,
            &target,
            &tenant,
            "SELECT records._id, archive.kind FROM records JOIN archive ON records._id = archive._id ORDER BY records._id",
        );
        assert_eq!(joined.rows().len(), 2);
        let fulltext = execute_routed_sql(
            &source,
            &target,
            &tenant,
            "SELECT _id FROM records WHERE body MATCH 'quick'",
        );
        assert_eq!(fulltext.rows().len(), 2);
        drop(target);
        drop(source);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn routed_sql_pages_collection_materialization_before_evaluation() {
        let root = std::env::temp_dir()
            .join(format!("routed-sql-paged-input-{}", crate::identity::generate()));
        let source =
            Arc::new(Engine::open(EngineConfig::durable(root.join("source")).unwrap()).unwrap());
        let target =
            Arc::new(Engine::open(EngineConfig::durable(root.join("target")).unwrap()).unwrap());
        let tenant = TenantId::try_new("tenant").unwrap();
        for index in 0..257 {
            put(
                &source,
                &tenant,
                &format!("source-{index}"),
                &format!("a-record-{index:03}"),
                "records",
                &[("state", text("open"))],
            );
        }

        let result =
            execute_routed_sql(&source, &target, &tenant, "SELECT COUNT(*) AS total FROM records");
        assert_eq!(result.rows()[0].projected()[0].1, DocumentValue::Integer(257));
        drop(target);
        drop(source);
        std::fs::remove_dir_all(root).unwrap();
    }
}
