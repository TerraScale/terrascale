//! A logical restore publishes exactly one complete directory tree.

use super::{
    BackupError, BackupReader, CollectionName, DocumentId, Engine, EngineConfig, Path, PathBuf,
    Replay, RestoreIdentity, RestoreMarker, RestorePlan, RestoreTargetReservation, StagingDir,
    TenantId, admit_target, publish_with_identity, read_restore_marker, reconcile_staging,
    verify_manifest_logical_facts, verify_storage, write_marker,
};
use crate::backup::BackupEnvelope;
use crate::member::AdmittedShardReadCut;
use serde::{Deserialize, Serialize};

/// Verified result of one complete routed database restore.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EnvelopeRecoveryReceipt {
    /// Authenticated complete source envelope.
    pub envelope: BackupEnvelope,
    /// Total records independently read back from all staged files.
    pub documents_read_back: u64,
    /// Canonical checksum combining the verified participant checksums.
    pub logical_checksum: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub(super) struct EnvelopeRestoreMarker {
    pub(super) source: String,
    files: Vec<RestoredFile>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
struct RestoredFile {
    shard_id: u64,
    sequence: u64,
    checksum: String,
    documents: u64,
}

fn binding(identity: &RestoreIdentity) -> Result<[u8; 32], BackupError> {
    let bytes = serde_json::to_vec(identity).map_err(|_| BackupError::ManifestInvalid)?;
    let mut digest = blake3::Hasher::new();
    digest.update(b"terrabase-envelope-restore-v1\0");
    digest.update(&bytes);
    Ok(*digest.finalize().as_bytes())
}

fn file_config(
    target: &EngineConfig,
    path: &Path,
    shard: u64,
    binding: &[u8; 32],
) -> Result<EngineConfig, BackupError> {
    let region = crate::types::RegionId::try_new(
        target.owner().ok_or(BackupError::DestinationUnavailable)?.region().as_str(),
    )
    .map_err(|_| BackupError::DestinationUnavailable)?;
    let lane = crate::origin_lane::OriginLaneId::try_new(
        region,
        crate::engine::shard_split::restored_lane_incarnation(binding, shard),
    )
    .map_err(|_| BackupError::DestinationUnavailable)?;
    Ok(target.clone().at_location(path).with_replication_lane_origin(lane))
}

struct EnvelopeReplayFacts {
    files: Vec<RestoredFile>,
    documents: u64,
    family_records: u64,
}

fn replay_envelope_files(
    envelope: &BackupEnvelope,
    archives: &[(String, PathBuf)],
    target: &EngineConfig,
    reader: &dyn BackupReader,
    identity: &RestoreIdentity,
    staging: &StagingDir,
    binding: &[u8; 32],
) -> Result<EnvelopeReplayFacts, BackupError> {
    let routes =
        envelope.participants().iter().flat_map(|p| p.routes.iter().cloned()).collect::<Vec<_>>();
    let mut files = Vec::new();
    let mut documents = 0_u64;
    let mut family_records = 0_u64;
    for (participant, (_, archive)) in envelope.participants().iter().zip(archives) {
        let shard = participant.file_identity.shard_id;
        let location = if participant.is_root {
            staging.path().to_owned()
        } else {
            staging.path().join("shards").join(format!("{shard:016x}.sqlite3"))
        };
        std::fs::create_dir_all(&location).map_err(|_| BackupError::DestinationUnavailable)?;
        let config = file_config(target, &location, shard, binding)?;
        let engine =
            Engine::open(config.clone()).map_err(|_| BackupError::DestinationUnavailable)?;
        let tenant = TenantId::try_new(&engine.database_owner().tenant().to_text())
            .map_err(|_| BackupError::ManifestInvalid)?;
        if participant.manifest.tenants().iter().any(|count| count.tenant() != &tenant) {
            return Err(BackupError::ManifestInvalid);
        }
        let mut replay = Replay::new();
        let chain = std::slice::from_ref(&participant.manifest);
        replay.chain(&engine, archive, chain, Some(reader))?;
        for (_, collection, id) in replay.content_hashes.keys() {
            let key = crate::shard_split::canonical_shard_key(
                &CollectionName::try_new(collection).map_err(|_| BackupError::ManifestInvalid)?,
                &DocumentId::try_new(id).map_err(|_| BackupError::ManifestInvalid)?,
            );
            if !participant.routes.iter().any(|route| {
                route.lower().is_none_or(|lower| lower <= key.as_slice())
                    && route.upper().is_none_or(|upper| key.as_slice() < upper)
            }) {
                return Err(BackupError::ChecksumMismatch);
            }
        }
        engine
            .activate_origin_epoch(identity.restore_epoch)
            .map_err(|_| BackupError::DestinationUnavailable)?;
        engine
            .install_restored_shard_catalog(
                &engine.scope_for(&tenant),
                shard,
                if participant.is_root { routes.clone() } else { participant.routes.clone() },
                envelope.route_generation(),
                binding,
            )
            .map_err(|_| BackupError::DestinationUnavailable)?;
        if participant.is_root {
            engine
                .lifecycle_pause(&engine.scope_for(&tenant), "restore-pause")
                .map_err(|_| BackupError::DestinationUnavailable)?;
        }
        drop(engine);
        let reopened = Engine::open(config).map_err(|_| BackupError::DestinationUnavailable)?;
        let storage = reopened.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
        let verified = verify_storage(storage.port.as_ref(), &replay)?;
        verify_manifest_logical_facts(chain, None, &verified)?;
        drop(storage);
        let facts = replay.into_facts(verified);
        files.push(RestoredFile {
            shard_id: shard,
            sequence: facts.target_sequence.get(),
            checksum: facts.logical_checksum,
            documents: facts.documents_read_back,
        });
        documents =
            documents.checked_add(facts.restored_documents).ok_or(BackupError::ChecksumMismatch)?;
        family_records = family_records
            .checked_add(facts.restored_family_records)
            .ok_or(BackupError::ChecksumMismatch)?;
    }
    Ok(EnvelopeReplayFacts { files, documents, family_records })
}

fn validate_envelope_request(
    envelope: &BackupEnvelope,
    archives: &[(String, PathBuf)],
    target: &EngineConfig,
    identity: &RestoreIdentity,
) -> Result<u64, BackupError> {
    let cut = envelope
        .participants()
        .iter()
        .map(|p| p.boundary)
        .max()
        .ok_or(BackupError::ChainIncomplete)?;
    if identity.source_database_id != envelope.database_id()
        || identity.expected_manifest_digest != envelope.digest()
        || identity.selection != envelope.digest()
        || identity.expected_journal_boundary != cut
        || identity.restore_epoch == 0
        || !target.has_encrypted_owner()
        || target
            .owner()
            .is_none_or(|owner| owner.database().to_text() != identity.target_database_id)
        || archives.len() != envelope.participants().len()
    {
        return Err(BackupError::ManifestInvalid);
    }
    for (participant, (id, _)) in envelope.participants().iter().zip(archives) {
        if id != &participant.participant_id || participant.manifest.base_sequence().is_some() {
            return Err(BackupError::ChainIncomplete);
        }
    }
    Ok(cut)
}

impl RestorePlan {
    /// Replays all authenticated participants privately, writes one durable
    /// operation marker, and exchanges the complete database tree once.
    ///
    /// # Errors
    /// Refuses mismatched identity, incomplete participants, corrupt archives,
    /// unavailable keys, and any existing target that is not durably paused.
    pub fn restore_envelope_encrypted_with_config(
        envelope: &BackupEnvelope,
        archives: &[(String, PathBuf)],
        target: &EngineConfig,
        reader: &dyn BackupReader,
        identity: &RestoreIdentity,
    ) -> Result<EnvelopeRecoveryReceipt, BackupError> {
        let envelope = BackupEnvelope::decode(&envelope.encode()?)?;
        let _cut = validate_envelope_request(&envelope, archives, target, identity)?;
        let path = target.location();
        let _reservation = RestoreTargetReservation::acquire(path)?;
        let replace = path.exists()
            && std::fs::read_dir(path)
                .map_err(|_| BackupError::DestinationUnavailable)?
                .next()
                .is_some();
        if replace {
            let engine =
                Engine::open(target.clone()).map_err(|_| BackupError::DestinationUnavailable)?;
            let tenant = TenantId::try_new(&engine.database_owner().tenant().to_text())
                .map_err(|_| BackupError::ManifestInvalid)?;
            let state = engine
                .lifecycle_state(&engine.scope_for(&tenant))
                .map_err(|_| BackupError::DestinationUnavailable)?;
            if !state.paused
                || state.access_revoked()
                || read_restore_marker(&engine, identity)?.is_some()
            {
                return Err(BackupError::TargetNotEmpty);
            }
        } else {
            admit_target(path)?;
            if path.exists() {
                std::fs::remove_dir(path).map_err(|_| BackupError::TargetNotEmpty)?;
            }
        }
        reconcile_staging(path)?;
        let staging = StagingDir::create(path)?;
        let binding = binding(identity)?;
        let replay = replay_envelope_files(
            &envelope, archives, target, reader, identity, &staging, &binding,
        )?;
        let EnvelopeReplayFacts { files, documents, family_records } = replay;
        let root = envelope
            .participants()
            .iter()
            .find(|p| p.is_root)
            .ok_or(BackupError::ManifestInvalid)?;
        let root_sequence = files
            .iter()
            .find(|file| file.shard_id == root.file_identity.shard_id)
            .ok_or(BackupError::ManifestInvalid)?
            .sequence;
        let root = Engine::open(file_config(
            target,
            staging.path(),
            root.file_identity.shard_id,
            &binding,
        )?)
        .map_err(|_| BackupError::DestinationUnavailable)?;
        let marker = RestoreMarker {
            envelope: Some(EnvelopeRestoreMarker {
                source: String::from_utf8(envelope.encode()?)
                    .map_err(|_| BackupError::ManifestInvalid)?,
                files,
            }),
            identity: identity.clone(),
            target_sequence: root_sequence,
            logical_checksum: envelope.logical_checksum(),
            restored_documents: documents,
            restored_family_records: family_records,
            source_logical_checksum: envelope.logical_checksum(),
            source_logical_documents: envelope.logical_documents(),
            source_captured_unix_millis: envelope.captured_unix_millis(),
            source_retain_until_unix_millis: envelope.retain_until_unix_millis(),
        };
        write_marker(&root, &marker)?;
        if read_restore_marker(&root, identity)?.is_none() {
            return Err(BackupError::ChecksumMismatch);
        }
        drop(root);
        if staging.path().join("shards").exists() {
            crate::backup::sync_directory(&staging.path().join("shards"))?;
        }
        crate::backup::sync_directory(staging.path())?;
        publish_with_identity(&staging, path, replace)?;
        if replace {
            staging.discard_replaced();
        }
        Ok(EnvelopeRecoveryReceipt {
            documents_read_back: envelope.logical_documents(),
            logical_checksum: envelope.logical_checksum(),
            envelope,
        })
    }
}

/// Validates all admitted restored files before a recovery ledger may mark a
/// routed operation complete, including after a crash between swap and receipt.
///
/// # Errors
/// Rejects missing files, mismatched route/lane identity or corrupted readback.
pub fn verify_envelope_restore(
    engine: &Engine,
    cut: &AdmittedShardReadCut,
    identity: &RestoreIdentity,
) -> Result<(), BackupError> {
    let tenant = TenantId::try_new(&engine.database_owner().tenant().to_text())
        .map_err(|_| BackupError::ManifestInvalid)?;
    let storage = engine.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
    let value = storage
        .port
        .private_metadata_at(
            &tenant,
            &format!("restore/{}", identity.operation_id),
            storage.port.published_state().0,
        )
        .map_err(|()| BackupError::DestinationUnavailable)?
        .ok_or(BackupError::ChecksumMismatch)?;
    let crate::types::DocumentValue::String(value) = value else {
        return Err(BackupError::ChecksumMismatch);
    };
    let marker: RestoreMarker =
        serde_json::from_str(&value).map_err(|_| BackupError::ChecksumMismatch)?;
    drop(storage);
    let Some(envelope_marker) = marker.envelope else { return Ok(()) };
    let source = BackupEnvelope::decode(envelope_marker.source.as_bytes())?;
    if marker.identity != *identity
        || source.digest() != identity.expected_manifest_digest
        || cut.database_id != identity.target_database_id
        || cut.route_generation != Some(source.route_generation())
        || cut.participants.len() != source.participants().len()
        || envelope_marker.files.len() != cut.participants.len()
    {
        return Err(BackupError::ChecksumMismatch);
    }
    let binding = binding(identity)?;
    for ((expected, file), actual) in
        source.participants().iter().zip(&envelope_marker.files).zip(&cut.participants)
    {
        if actual.identity.shard_id != expected.file_identity.shard_id
            || actual.routes != expected.routes
            || file.shard_id != actual.identity.shard_id
            || expected.is_root != (cut.root_shard_id == actual.identity.shard_id)
            || actual.identity.origin_lane_incarnation
                != crate::engine::shard_split::restored_lane_incarnation(
                    &binding,
                    actual.identity.shard_id,
                )
            || actual
                .engine
                .restored_route_binding(&actual.scope)
                .map_err(|_| BackupError::ChecksumMismatch)?
                != Some(binding)
        {
            return Err(BackupError::ChecksumMismatch);
        }
        let stats = actual
            .engine
            .recovery_statistics(&actual.scope, file.sequence, source.captured_unix_millis(), None)
            .map_err(|_| BackupError::ChecksumMismatch)?;
        if blake3::Hash::from(stats.checksum).to_hex().as_str() != file.checksum
            || stats.row_count != file.documents
            || file.checksum != expected.manifest.logical_checksum()
            || file.documents != expected.manifest.logical_documents()
        {
            return Err(BackupError::ChecksumMismatch);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::backup::restore::FAIL_NEXT_RESTORE_PARENT_SYNC;
    use crate::backup::{BackupEncryption, BackupEnvelopeParticipant, BackupPlan, run_backup};
    use crate::encryption::{
        DatabaseKey, DatabaseKeyLease, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    };
    use crate::storage::DatabaseOwner;
    use crate::types::{DocumentValue, LocalDurability, OperationId};
    use std::sync::Arc;

    fn capture_restore_participants(
        workspace: &std::path::Path,
        source_owner: &DatabaseOwner,
        lease: &DatabaseKeyLease,
        collection: &CollectionName,
    ) -> (BackupEnvelope, Vec<(String, PathBuf)>) {
        let split =
            crate::shard_split::canonical_shard_key(collection, &DocumentId::try_new("m").unwrap());
        let mut participants = Vec::new();
        let mut archives = Vec::new();
        let incarnation = u128::from_be_bytes(source_owner.database().as_bytes());
        for (shard, id, routes) in [
            (
                1,
                "a",
                vec![crate::shard_split::ShardRoute::new(1, None, Some(split.clone())).unwrap()],
            ),
            (2, "z", vec![crate::shard_split::ShardRoute::new(2, Some(split), None).unwrap()]),
        ] {
            let source = Engine::open(
                EngineConfig::durable(workspace.join(format!("source-{shard}")))
                    .unwrap()
                    .with_owner(source_owner.clone())
                    .with_storage_keys(crate::engine::test_storage_keys(source_owner, 41)),
            )
            .unwrap();
            source.activate_origin_epoch(1).unwrap();
            assert!(matches!(
                crate::backup::tests::put(
                    &source,
                    &source_owner.tenant().to_text(),
                    "put",
                    id,
                    "items",
                    DocumentValue::Integer(shard),
                    LocalDurability::Synced
                ),
                crate::engine::CommitOutcome::Committed(_)
            ));
            let archive = workspace.join(format!("archive-{shard}"));
            let encryption =
                BackupEncryption::from_database_key_lease(lease, source_owner.region(), 0).unwrap();
            let mut plan = BackupPlan::snapshot_at(
                source.journaled_high_water().unwrap().unwrap(),
                encryption,
            )
            .with_snapshot_routes(routes.clone())
            .unwrap();
            plan.capture_kind = crate::backup::BackupCaptureKind::RoutedShardParticipant;
            let manifest = run_backup(&source, plan, &archive).unwrap().manifest().clone();
            let shard = u64::try_from(shard).unwrap();
            let participant_id = format!("shard-{shard:016x}");
            archives.push((participant_id.clone(), archive));
            participants.push(BackupEnvelopeParticipant {
                file_identity: crate::shard_split::FileShardIdentity {
                    database_incarnation: incarnation,
                    shard_id: shard,
                    region: source_owner.region(),
                    origin_lane_incarnation: u128::from(shard),
                },
                is_root: shard == 1,
                routes,
                participant_id,
                database_id: source_owner.database().to_text(),
                database_incarnation: incarnation,
                route_generation: 3,
                boundary: manifest.boundary_sequence().get(),
                manifest,
            });
        }
        let envelope = BackupEnvelope::assemble(
            source_owner.database().to_text(),
            incarnation,
            3,
            participants,
        )
        .unwrap();
        (envelope, archives)
    }

    fn assert_restored_physical_files(
        reopened: &Arc<Engine>,
        tenant: &TenantId,
        collection: &CollectionName,
        envelope: &BackupEnvelope,
        identity: &RestoreIdentity,
    ) {
        let root_scope = reopened.scope_for(tenant);
        assert_eq!(reopened.load_shard_router(&root_scope).unwrap().unwrap().1, 3);
        let child = reopened.open_shard_participant(2).unwrap().unwrap();
        for (engine, id) in [(reopened.as_ref(), "a"), (child.as_ref(), "z")] {
            let scope = engine.scope_for(tenant);
            let tx = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new("verify").unwrap(),
                )
                .unwrap();
            assert!(
                tx.read_in_collection(collection, &DocumentId::try_new(id).unwrap())
                    .unwrap()
                    .is_some()
            );
        }
        assert!(
            child
                .validate_restored_shard_target(
                    &child.scope_for(tenant),
                    &envelope.participants()[1].routes[0],
                    3,
                    &binding(identity).unwrap()
                )
                .unwrap()
        );
        drop(child);
    }

    #[test]
    fn envelope_restore_installs_all_files_and_failed_exchange_reconciles_one_marker() {
        let source_owner = DatabaseOwner::local_development();
        let target_owner = DatabaseOwner::try_new(
            source_owner.environment().as_str(),
            source_owner.tenant(),
            source_owner.project(),
            crate::identity::DatabaseUlid::generate(),
            source_owner.region(),
        )
        .unwrap();
        let workspace = crate::backup::tests::location("envelope-tree-restore");
        std::fs::create_dir_all(&workspace).unwrap();
        let scope = KeyScope::new_with_project(
            source_owner.environment().as_str(),
            source_owner.tenant(),
            source_owner.project(),
            source_owner.database(),
            KeyPurpose::Backup,
        )
        .unwrap();
        let lease = DatabaseKeyLease::test_oci_vault(
            KeyVersionMetadata::new(scope, DatabaseKeyVersion::generate()),
            DatabaseKey::from_bytes([81; 32]),
            std::time::Duration::from_mins(5),
        )
        .unwrap();
        let collection = CollectionName::try_new("items").unwrap();
        let (envelope, archives) =
            capture_restore_participants(&workspace, &source_owner, &lease, &collection);
        let config = EngineConfig::durable(workspace.join("target"))
            .unwrap()
            .with_owner(target_owner.clone())
            .with_storage_keys(crate::engine::test_storage_keys(&target_owner, 42));
        let old = Engine::open(config.clone()).unwrap();
        let tenant = TenantId::try_new(&target_owner.tenant().to_text()).unwrap();
        old.lifecycle_pause(&old.scope_for(&tenant), "pause").unwrap();
        drop(old);
        let identity = RestoreIdentity {
            operation_id: "routed-restore".into(),
            source_database_id: source_owner.database().to_text(),
            target_database_id: target_owner.database().to_text(),
            provider: "oci".into(),
            selection: envelope.digest().into(),
            expected_manifest_digest: envelope.digest().into(),
            expected_journal_boundary: envelope
                .participants()
                .iter()
                .map(|p| p.boundary)
                .max()
                .unwrap(),
            restore_epoch: 2,
        };
        let missing =
            archives[1].1.join(format!("rp-{}", envelope.participants()[1].manifest.identity()));
        let hidden = workspace.join("hidden-point");
        std::fs::rename(&missing, &hidden).unwrap();
        assert!(
            RestorePlan::restore_envelope_encrypted_with_config(
                &envelope, &archives, &config, &lease, &identity
            )
            .is_err()
        );
        assert!(
            read_restore_marker(&Engine::open(config.clone()).unwrap(), &identity)
                .unwrap()
                .is_none()
        );
        std::fs::rename(&hidden, &missing).unwrap();
        FAIL_NEXT_RESTORE_PARENT_SYNC.with(|fail| fail.set(true));
        assert_eq!(
            RestorePlan::restore_envelope_encrypted_with_config(
                &envelope, &archives, &config, &lease, &identity
            ),
            Err(BackupError::DestinationUnavailable)
        );
        let reopened = Arc::new(Engine::open(config.clone()).unwrap());
        assert!(!FAIL_NEXT_RESTORE_PARENT_SYNC.with(std::cell::Cell::get));
        assert_eq!(
            read_restore_marker(&reopened, &identity).unwrap().unwrap().restored_documents,
            2
        );
        assert_restored_physical_files(&reopened, &tenant, &collection, &envelope, &identity);
        drop(reopened);
        assert_eq!(
            RestorePlan::restore_envelope_encrypted_with_config(
                &envelope, &archives, &config, &lease, &identity
            ),
            Err(BackupError::TargetNotEmpty)
        );
    }
}
