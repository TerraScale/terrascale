//! Durable, database-wide reservations for exact cross-file intents.
//!
//! Admission executes the normal publication path under a rollback savepoint.
//! Only the immutable full intent survives. The writer transaction serializes
//! reservation creation with admission; SQL guards cover every existing table,
//! including direct lifecycle, budget, replication and maintenance writes.
use super::*;
use crate::storage::{ParticipantIdentity, PreparedCrossFileIntent, prepared_codec};
use rusqlite::params;

pub(super) const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS cross_file_intents (
 tenant TEXT NOT NULL, operation TEXT NOT NULL, identity BLOB NOT NULL,
 payload BLOB, checksum BLOB NOT NULL, phase TEXT NOT NULL,
 receipt_sequence INTEGER, receipt_root TEXT, receipt_durability INTEGER,
 PRIMARY KEY(tenant,operation));
CREATE UNIQUE INDEX IF NOT EXISTS cross_file_one_reservation
 ON cross_file_intents((1)) WHERE phase IN ('prepared','installed');
CREATE TABLE IF NOT EXISTS cross_file_authorization (operation TEXT PRIMARY KEY);
";

fn identity_bytes(i: &ParticipantIdentity) -> Result<Vec<u8>, ()> {
    if i.participant.is_empty()
        || i.owner.is_empty()
        || i.owner.len() > 4096
        || i.participant.len() > 4096
    {
        return Err(());
    }
    serde_json::to_vec(&(
        i.tenant.to_string(),
        i.database_incarnation.to_string(),
        i.operation.to_string(),
        &i.participant,
        &i.owner,
        i.digest,
        i.epoch,
        i.snapshot.get(),
    ))
    .map_err(|_| ())
}

fn active_identity(c: &rusqlite::Transaction<'_>) -> Result<Option<ParticipantIdentity>, ()> {
    let bytes: Option<Vec<u8>> = c
        .query_row(
            "SELECT identity FROM cross_file_intents WHERE phase IN ('prepared','installed')",
            [],
            |r| r.get(0),
        )
        .optional()
        .map_err(|_| ())?;
    let Some(bytes) = bytes else {
        return Ok(None);
    };
    let (tenant, incarnation, operation, participant, owner, digest, epoch, snapshot): (
        String,
        String,
        String,
        String,
        String,
        [u8; 32],
        u64,
        u64,
    ) = serde_json::from_slice(&bytes).map_err(|_| ())?;
    let identity = ParticipantIdentity {
        tenant: TenantId::try_new(&tenant).map_err(|_| ())?,
        database_incarnation: incarnation.parse().map_err(|_| ())?,
        operation: OperationId::try_new(&operation).map_err(|_| ())?,
        participant,
        owner,
        digest,
        epoch,
        snapshot: StorageSequence::new(snapshot),
    };
    if identity_bytes(&identity)? != bytes {
        return Err(());
    }
    load(c, &identity)?.ok_or(())?;
    Ok(Some(identity))
}

fn checksum(identity: &[u8], payload: Option<&[u8]>) -> [u8; 32] {
    let mut h = blake3::Hasher::new();
    h.update(b"terrascale.prepared-cross-file.v1\0");
    h.update(&(identity.len() as u64).to_le_bytes());
    h.update(identity);
    h.update(&[u8::from(payload.is_some())]);
    if let Some(payload) = payload {
        h.update(payload);
    }
    *h.finalize().as_bytes()
}

pub(super) fn reserved_on(c: &Connection) -> Result<bool, ()> {
    let exists: bool = c.query_row("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='cross_file_intents')", [], |r| r.get(0)).map_err(|_| ())?;
    if !exists {
        return Ok(false);
    }
    c.query_row(
        "SELECT EXISTS(SELECT 1 FROM cross_file_intents WHERE phase IN ('prepared','installed'))",
        [],
        |r| r.get(0),
    )
    .map_err(|_| ())
}

/// Install on each writer handle, after open-time initialization. TEMP guards
/// consult durable reservation rows on every execution, not cached Rust state.
pub(super) fn install_guards(c: &Connection) -> Result<(), ()> {
    c.execute_batch(SCHEMA).map_err(|_| ())?;
    let tables = c.prepare("SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'cross_file_%' ORDER BY name").map_err(|_| ())?
        .query_map([], |r| r.get::<_, String>(0)).map_err(|_| ())?.collect::<Result<Vec<_>, _>>().map_err(|_| ())?;
    for table in tables {
        for action in ["INSERT", "UPDATE", "DELETE"] {
            c.execute_batch(&format!("CREATE TEMP TRIGGER IF NOT EXISTS {} BEFORE {action} ON main.{} WHEN EXISTS(SELECT 1 FROM cross_file_intents WHERE phase IN ('prepared','installed')) AND NOT EXISTS(SELECT 1 FROM cross_file_authorization) BEGIN SELECT RAISE(ABORT, 'cross-file reservation'); END;", quote_identifier(&format!("cross_file_guard_{table}_{action}")), quote_identifier(&table))).map_err(|_| ())?;
        }
    }
    Ok(())
}

pub(super) fn clear_authorization(c: &Connection) -> Result<(), ()> {
    c.execute("DELETE FROM cross_file_authorization", []).map_err(|_| ())?;
    Ok(())
}

fn validate_billing(identity: &ParticipantIdentity, intent: &CommitIntent) -> Result<(), ()> {
    if intent.metadata.iter().any(|m| m.key.starts_with("control:cross-file")) {
        return Err(());
    }
    let Some(billing) = &intent.cross_file_billing else {
        return Ok(());
    };
    if billing.operation != identity.operation
        || billing.participant != identity.participant
        || billing.metering_participant.is_empty()
        || billing.metering_participant.len() > 4096
        || billing.total_write_units == 0
        || intent.internal_maintenance
        || intent.shard_copy
        || intent.replication_publication.is_some()
        || intent.cross_file_control.is_some()
    {
        return Err(());
    }
    let local = intent.writes.iter().try_fold(0_u64, |sum, write| {
        let units = match write {
            StagedWrite::Put { document, .. } => {
                billed_writes_for_bytes(encode_document_body(document).len() as u64)
            }
            StagedWrite::Delete { .. } => 1,
            _ => return Err(()),
        };
        sum.checked_add(units).ok_or(())
    })?;
    if (local == 0 && billing.participant != billing.metering_participant)
        || local > billing.total_write_units
    {
        return Err(());
    }
    if billing.participant != billing.metering_participant
        && (intent.usage_admission.is_some() || intent.budget_allocation.is_some())
    {
        return Err(());
    }
    Ok(())
}

pub(super) fn authorize(
    c: &rusqlite::Transaction<'_>,
    intents: &[CommitIntent],
    prepared: Option<(&ParticipantIdentity, bool)>,
    local_origin: &str,
) -> Result<(), ()> {
    let authorized: bool = c
        .query_row("SELECT EXISTS(SELECT 1 FROM cross_file_authorization)", [], |r| r.get(0))
        .map_err(|_| ())?;
    if authorized {
        return Err(());
    }
    if intents.iter().any(|i| i.cross_file_control.is_some() && !valid_control_shape(i)) {
        return Err(());
    }
    let reservation = active_identity(c)?;
    for intent in intents {
        if let Some(bytes) = &intent.cross_file_dependencies {
            if (prepared.is_none() && intent.cross_file_control.is_none())
                || (intent.cross_file_control.is_some()
                    && !intent
                        .metadata
                        .iter()
                        .any(|m| m.key == "control:cross-file-visible/v1" && m.value.is_some()))
                || intent.replication_publication.is_some()
                || intent.writes.iter().any(|write| {
                    matches!(
                        write,
                        StagedWrite::ReplicatedPut { .. } | StagedWrite::ReplicatedDelete { .. }
                    )
                })
            {
                return Err(());
            }
            decode_dependencies(bytes)?;
        }
        if let Some(operation) = &intent.cross_file_control {
            let prior = if let [change] = intent.metadata.as_slice()
                && change.value.is_none()
            {
                private_metadata::read_private_metadata_on(
                    c,
                    &intent.tenant,
                    &change.key,
                    durable_watermark(c)?,
                )?
            } else {
                None
            };
            if !crate::engine::validate_control_origin(
                &intent.metadata,
                prior.as_ref(),
                local_origin,
            ) || !crate::engine::validate_control_metadata(
                &intent.tenant,
                operation,
                &intent.metadata,
                reservation.as_ref(),
                prior.as_ref(),
            ) {
                return Err(());
            }
        }
    }
    if let Some((identity, _)) = prepared {
        for intent in intents {
            validate_billing(identity, intent)?;
        }
    } else if intents.iter().any(|i| i.cross_file_billing.is_some()) {
        return Err(());
    }
    if !reserved_on(c)? {
        return if prepared.is_none_or(|(_, installing)| !installing) { Ok(()) } else { Err(()) };
    }
    let (tenant, operation): (String, String) = c.query_row("SELECT tenant,operation FROM cross_file_intents WHERE phase IN ('prepared','installed')", [], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| ())?;
    if let Some((identity, installing)) = prepared {
        if !installing {
            return Err(());
        }
        let row = load(c, identity)?.ok_or(())?;
        if row.phase != "prepared"
            || intents.len() != 1
            || row.prepared.intent.as_ref().map(prepared_codec::encode).transpose()?
                != Some(prepared_codec::encode(&intents[0])?)
        {
            return Err(());
        }
    } else {
        let exact = intents.len() == 1
            && intents.iter().all(|i| {
                i.tenant.as_str() == tenant
                    && i.cross_file_control.as_ref().is_some_and(|op| op.as_str() == operation)
                    && valid_control_shape(i)
            });
        if !exact {
            return Err(());
        }
    }
    c.execute("INSERT INTO cross_file_authorization(operation) VALUES (?1)", [operation])
        .map_err(|_| ())?;
    Ok(())
}

pub(super) fn decode_dependencies(
    bytes: &[u8],
) -> Result<crate::engine::merge::DocumentContext, ()> {
    if bytes.len() > 1 << 20 {
        return Err(());
    }
    let context = crate::engine::merge::decode_context(bytes).ok_or(())?;
    if crate::engine::merge::encode_context(&context) != bytes
        || context.frontier().is_empty()
        || context.frontier().iter().any(|(origin, sequence)| {
            origin.len() > 255
                || origin.chars().any(char::is_control)
                || *sequence > i64::MAX as u64
        })
    {
        return Err(());
    }
    Ok(context)
}

fn valid_control_shape(i: &CommitIntent) -> bool {
    let Some(operation) = i.cross_file_control.as_ref().map(OperationId::as_str) else {
        return false;
    };
    i.internal_maintenance
        && i.writes.is_empty()
        && !i.metadata.is_empty()
        && i.schema_envelopes.is_empty()
        && i.usage_admission.is_none()
        && i.budget_allocation.is_none()
        && i.replication_publication.is_none()
        && i.replication_fingerprint.is_none()
        && i.merge_conflict.is_none()
        && !i.shard_copy
        && i.cross_file_billing.is_none()
        && i.metadata.iter().all(|m| {
            m.key == "control:cross-file-active/v1"
                || m.key == "control:cross-file-finishing/v1"
                || m.key == "control:cross-file-visible/v1"
                || m.key == format!("control:cross-file/v1/{operation}")
                || m.key == format!("control:cross-file-request/v1/{operation}")
                || m.key == format!("control:cross-file-complete/v1/{operation}")
        })
}

/// The original user cut is retained. Only this operation's own durable
/// prepare/decision publications may explain its local-origin advancement.
pub(super) fn uniform_control_delta(
    tx: &rusqlite::Transaction<'_>,
    intent: &CommitIntent,
    identity: &ParticipantIdentity,
    local_origin: &str,
) -> Result<bool, ()> {
    let Some(expected) = &intent.expected_uniform_context else {
        return Ok(false);
    };
    let Some(expected) = crate::engine::merge::decode_context(expected) else {
        return Ok(false);
    };
    let Some(current) =
        crate::engine::merge::decode_context(&context_frontier(tx, &intent.tenant)?)
    else {
        return Err(());
    };
    let mut before = expected.frontier().clone();
    let mut after = current.frontier().clone();
    let first = before.remove(local_origin).unwrap_or(0);
    let last = after.remove(local_origin).unwrap_or(0);
    if before != after || last <= first || last - first > 64 {
        return Ok(false);
    }
    let mut query = tx.prepare("SELECT p.origin_seq,p.private_payload,
        NOT EXISTS(SELECT 1 FROM replication_publication_records r WHERE r.tenant=p.tenant AND r.local_seq=p.local_seq)
        AND NOT EXISTS(SELECT 1 FROM documents d WHERE d.tenant=p.tenant AND d.commit_seq=p.local_seq)
        FROM replication_publications p WHERE p.tenant=?1 AND p.origin=?2 AND p.origin_seq>?3 AND p.origin_seq<=?4 ORDER BY p.origin_seq LIMIT 65").map_err(|_| ())?;
    let mut rows = query
        .query(params![
            intent.tenant.as_str(),
            local_origin,
            i64::try_from(first).map_err(|_| ())?,
            i64::try_from(last).map_err(|_| ())?
        ])
        .map_err(|_| ())?;
    let mut next = first + 1;
    while let Some(row) = rows.next().map_err(|_| ())? {
        let sequence: i64 = row.get(0).map_err(|_| ())?;
        let payload: Vec<u8> = row.get(1).map_err(|_| ())?;
        let metadata_only: bool = row.get(2).map_err(|_| ())?;
        if u64::try_from(sequence).ok() != Some(next) || !metadata_only {
            return Ok(false);
        }
        let Some((metadata, envelopes)) = codec::decode_private_changes(&payload) else {
            return Ok(false);
        };
        if !envelopes.is_empty()
            || metadata.iter().any(|m| m.key == "control:cross-file-visible/v1")
            || !crate::engine::validate_control_origin(&metadata, None, local_origin)
            || !crate::engine::validate_control_metadata(
                &intent.tenant,
                &identity.operation,
                &metadata,
                Some(identity),
                None,
            )
        {
            return Ok(false);
        }
        next += 1;
    }
    Ok(next == last + 1)
}

struct Stored {
    prepared: PreparedCrossFileIntent,
    phase: String,
    receipt: Option<StorageReceipt>,
}

fn load(
    c: &rusqlite::Transaction<'_>,
    identity: &ParticipantIdentity,
) -> Result<Option<Stored>, ()> {
    let found = c.query_row("SELECT identity,payload,checksum,phase,receipt_sequence,receipt_root,receipt_durability FROM cross_file_intents WHERE tenant=?1 AND operation=?2", params![identity.tenant.as_str(), identity.operation.as_str()], |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Option<Vec<u8>>>(1)?, r.get::<_, Vec<u8>>(2)?, r.get::<_, String>(3)?, r.get::<_, Option<i64>>(4)?, r.get::<_, Option<String>>(5)?, r.get::<_, Option<u8>>(6)?))).optional().map_err(|_| ())?;
    let Some((bytes, payload, hash, phase, seq, root, durability)) = found else {
        return Ok(None);
    };
    if bytes != identity_bytes(identity)?
        || hash != checksum(&bytes, payload.as_deref())
        || !["prepared", "installed", "finished", "aborted"].contains(&phase.as_str())
    {
        return Err(());
    }
    let intent = payload.as_deref().map(prepared_codec::decode).transpose()?;
    let receipt = match (seq, root, durability) {
        (None, None, None) => None,
        (Some(seq), Some(root), Some(durability)) if seq >= 0 => Some(StorageReceipt {
            sequence: StorageSequence::new(seq as u64),
            root: StorageRootId::from_recorded(root),
            durability: codec::durability_of(durability).ok_or(())?,
        }),
        _ => return Err(()),
    };
    if (phase == "installed" || phase == "finished") && intent.is_some() && receipt.is_none() {
        return Err(());
    }
    if let Some(receipt) = &receipt {
        if phase != "installed" && phase != "finished" {
            return Err(());
        }
        let intent = intent.as_ref().ok_or(())?;
        let fact =
            operation_view(c, &intent.tenant, &intent.operation, &intent.operation)?.ok_or(())?;
        if fact.rejection.is_some()
            || fact.sequence != receipt.sequence.get()
            || fact.root != receipt.root.to_string()
            || fact.requested != receipt.durability
            || fact.digest != intent.digest
        {
            return Err(());
        }
    }
    Ok(Some(Stored {
        prepared: PreparedCrossFileIntent { identity: identity.clone(), intent },
        phase,
        receipt,
    }))
}

impl SqliteStorage {
    fn restore_cross_file_sync(&self) -> Result<(), ()> {
        self.writer
            .lock()
            .map_err(|_| ())?
            .execute_batch("PRAGMA synchronous=NORMAL")
            .map_err(|_| ())
    }
    fn cross_file_context(&self) -> CommitContext {
        CommitContext {
            generated_primary_identity: Arc::clone(&self.generated_primary_identity),
            local_origin: self.local_origin.clone(),
            extracted: Arc::clone(&self.extracted),
            fts: Arc::clone(&self.fts),
            #[cfg(test)]
            faults: Arc::clone(&self.faults),
        }
    }

    pub(super) fn prepare_cross_file(
        &mut self,
        identity: &ParticipantIdentity,
        intent: Option<&CommitIntent>,
    ) -> Result<PreparedCrossFileIntent, CommitRejection> {
        let run = || -> Result<PreparedCrossFileIntent, CommitRejection> {
            let bytes = identity_bytes(identity).map_err(|_| CommitRejection::Validation)?;
            let payload = intent
                .map(prepared_codec::encode)
                .transpose()
                .map_err(|_| CommitRejection::Validation)?;
            if intent.is_some_and(|i| {
                i.tenant != identity.tenant
                    || i.operation != identity.operation
                    || i.snapshot_sequence != identity.snapshot
            }) {
                return Err(CommitRejection::Validation);
            }
            let mut writer = self.writer.lock().map_err(|_| CommitRejection::Storage)?;
            // Include indexes/candidate tables created since this handle opened.
            install_guards(&writer).map_err(|_| CommitRejection::Storage)?;
            writer
                .execute_batch("PRAGMA synchronous=FULL")
                .map_err(|_| CommitRejection::Storage)?;
            let tx = writer
                .transaction_with_behavior(TransactionBehavior::Immediate)
                .map_err(|_| CommitRejection::Storage)?;
            if let Some(stored) =
                load(&tx, identity).map_err(|_| CommitRejection::OperationIdReuse)?
            {
                if stored.phase == "aborted"
                    || stored
                        .prepared
                        .intent
                        .as_ref()
                        .map(prepared_codec::encode)
                        .transpose()
                        .map_err(|_| CommitRejection::Storage)?
                        != payload
                {
                    return Err(CommitRejection::OperationIdReuse);
                }
                return Ok(stored.prepared);
            }
            if reserved_on(&tx).map_err(|_| CommitRejection::Storage)? {
                return Err(CommitRejection::Conflict);
            }
            if !lifecycle_state::permits_origin_activation(&tx)
                .map_err(|_| CommitRejection::Storage)?
            {
                return Err(CommitRejection::Validation);
            }
            if let Some(intent) = intent {
                tx.execute_batch("SAVEPOINT cross_file_admission")
                    .map_err(|_| CommitRejection::Storage)?;
                let result = self
                    .cross_file_context()
                    .stage_transaction(&tx, std::slice::from_ref(intent), Some((identity, false)))
                    .map_err(|_| CommitRejection::Storage)?;
                tx.execute_batch("ROLLBACK TO cross_file_admission; RELEASE cross_file_admission")
                    .map_err(|_| CommitRejection::Storage)?;
                match result.as_slice() {
                    [CommitResult::Committed(_)] => {}
                    [CommitResult::Rejected(reason)] => return Err(*reason),
                    _ => return Err(CommitRejection::Storage),
                }
            }
            tx.execute("INSERT INTO cross_file_intents(tenant,operation,identity,payload,checksum,phase) VALUES(?1,?2,?3,?4,?5,'prepared')", params![identity.tenant.as_str(), identity.operation.as_str(), bytes, payload, checksum(&bytes, payload.as_deref()).as_slice()]).map_err(|_| CommitRejection::Storage)?;
            tx.commit().map_err(|_| CommitRejection::Storage)?;
            Ok(PreparedCrossFileIntent { identity: identity.clone(), intent: intent.cloned() })
        };
        let result = run();
        self.restore_cross_file_sync().map_err(|_| CommitRejection::Storage)?;
        result
    }

    pub(super) fn resolve_cross_file(
        &self,
        identity: &ParticipantIdentity,
    ) -> Result<Option<PreparedCrossFileIntent>, ()> {
        let writer = self.writer.lock().map_err(|_| ())?;
        let tx = writer.unchecked_transaction().map_err(|_| ())?;
        let stored = load(&tx, identity)?;
        if stored.as_ref().is_some_and(|s| s.phase == "aborted") {
            return Err(());
        }
        Ok(stored.map(|s| s.prepared))
    }

    pub(super) fn install_cross_file(
        &mut self,
        prepared: &PreparedCrossFileIntent,
    ) -> CommitResult {
        let run = || -> Result<CommitResult, ()> {
            let mut writer = self.writer.lock().map_err(|_| ())?;
            writer.execute_batch("PRAGMA synchronous=FULL").map_err(|_| ())?;
            let tx =
                writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
            let stored = load(&tx, &prepared.identity)?.ok_or(())?;
            if stored.prepared.intent.as_ref().map(prepared_codec::encode).transpose()?
                != prepared.intent.as_ref().map(prepared_codec::encode).transpose()?
            {
                return Ok(CommitResult::Rejected(CommitRejection::OperationIdReuse));
            }
            if let Some(receipt) = stored.receipt {
                return Ok(CommitResult::Committed(receipt));
            }
            if stored.phase != "prepared" {
                return Err(());
            }
            let Some(intent) = stored.prepared.intent else {
                return Ok(CommitResult::Rejected(CommitRejection::Validation));
            };
            let result = self
                .cross_file_context()
                .stage_transaction(&tx, &[intent], Some((&prepared.identity, true)))?
                .pop()
                .ok_or(())?;
            let CommitResult::Committed(receipt) = &result else {
                return Err(());
            };
            tx.execute("UPDATE cross_file_intents SET phase='installed',receipt_sequence=?3,receipt_root=?4,receipt_durability=?5 WHERE tenant=?1 AND operation=?2", params![prepared.identity.tenant.as_str(), prepared.identity.operation.as_str(), i64::try_from(receipt.sequence.get()).map_err(|_| ())?, receipt.root.to_string(), codec::durability_code(receipt.durability)]).map_err(|_| ())?;
            tx.commit().map_err(|_| ())?;
            Ok(result)
        };
        let result = run().unwrap_or(CommitResult::Unknown(
            super::super::intent::UnknownCause::ResponseInterrupted,
        ));
        if self.restore_cross_file_sync().is_err() && matches!(result, CommitResult::Committed(_)) {
            return CommitResult::Unknown(super::super::intent::UnknownCause::PostCommitSync);
        }
        result
    }

    pub(super) fn complete_cross_file(
        &mut self,
        identity: &ParticipantIdentity,
        abort: bool,
    ) -> Result<(), ()> {
        let run = || -> Result<(), ()> {
            let mut writer = self.writer.lock().map_err(|_| ())?;
            writer.execute_batch("PRAGMA synchronous=FULL").map_err(|_| ())?;
            let tx =
                writer.transaction_with_behavior(TransactionBehavior::Immediate).map_err(|_| ())?;
            let Some(stored) = load(&tx, identity)? else {
                return if abort { Ok(()) } else { Err(()) };
            };
            let target = if abort { "aborted" } else { "finished" };
            if stored.phase == target {
                return Ok(());
            }
            if abort && stored.phase != "prepared"
                || !abort
                    && !(stored.phase == "installed"
                        || stored.phase == "prepared" && stored.prepared.intent.is_none())
            {
                return Err(());
            }
            tx.execute(
                "UPDATE cross_file_intents SET phase=?3 WHERE tenant=?1 AND operation=?2",
                params![identity.tenant.as_str(), identity.operation.as_str(), target],
            )
            .map_err(|_| ())?;
            tx.commit().map_err(|_| ())
        };
        let result = run();
        self.restore_cross_file_sync()?;
        result
    }
}
