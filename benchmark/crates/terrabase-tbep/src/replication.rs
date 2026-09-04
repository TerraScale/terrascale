//! The cross-region journal tailer, in-process form (S-05 slice 2a,
//! D-35/D-36): pull origin rows from one member's publication journal,
//! apply them on a peer member, idempotently.
//!
//! This module is the apply path the QUIC lanes (slice 2b) will drive from
//! a real peer: [`pull_origin_records`] is the shipping side — it filters
//! the durable journal by provenance so replicated rows never re-ship and
//! the tail cannot loop — and [`Engine::apply_replicated`] (on the
//! terrabase crate) is the receiving side, binding each record to the
//! deterministic identity `repl-{commit_ulid}` so a duplicate delivery
//! replays the recorded publication instead of double-writing. No wire
//! encoding, batching, coalescing, or compression lives here yet; the
//! transport mechanics are D-45's, unchanged by this slice.

use terrabase::engine::ReplicatedRecord;
use terrabase::streaming::{ChangeCheckpoint, JournalKind};
use terrabase::types::{Cancellation, DocumentValue};

use crate::engine_host::EngineHost;
use crate::host::HostingError;
use crate::registry::BoundScope;

/// Pulls one bounded page of this host's ORIGIN journal rows as replicated
/// records, with the cursor that resumes exactly after everything the page
/// examined.
///
/// Loop prevention is the filter: a row applied from a peer region is
/// `kind='replicated'` and never ships again, so a tailer pointing back at
/// its own writes sees nothing.
///
/// # Errors
///
/// Returns [`HostingError`] when the durable journal cannot be replayed
/// under the bound scope.
pub fn pull_origin_records(
    host: &EngineHost,
    scope: &BoundScope,
    after: Option<ChangeCheckpoint>,
) -> Result<(Vec<ReplicatedRecord>, Option<ChangeCheckpoint>), HostingError> {
    pull_origin_records_with_cancellation(host, scope, after, &Cancellation::default())
}

/// Pulls one bounded page while observing a caller-owned cancellation signal.
///
/// Cancellation is checked at the page boundary and again after the durable
/// read.  The journal cursor is returned only on success, so a cancelled
/// tailer never accidentally commits progress it did not hand to its peer.
/// This is the synchronous counterpart of the async member pump: callers can
/// use the same signal to stop a retry loop without losing idempotent replay.
pub fn pull_origin_records_with_cancellation(
    host: &EngineHost,
    scope: &BoundScope,
    after: Option<ChangeCheckpoint>,
    cancellation: &Cancellation,
) -> Result<(Vec<ReplicatedRecord>, Option<ChangeCheckpoint>), HostingError> {
    if cancellation.is_cancelled() {
        return Err(HostingError::Refused("replication pull cancelled".into()));
    }
    let batch = host.replay_sync_journal(scope, after)?;
    if cancellation.is_cancelled() {
        return Err(HostingError::Refused("replication pull cancelled".into()));
    }
    let mut records = Vec::with_capacity(batch.records().len());
    for record in batch.records() {
        let provenance = record.provenance();
        if provenance.kind() != JournalKind::Origin {
            continue;
        }
        let (body, deleted) = match record.mutation() {
            terrabase::streaming::SyncJournalMutation::Put(value) => (value.clone(), false),
            terrabase::streaming::SyncJournalMutation::Delete => (DocumentValue::Null, true),
        };
        records.push(ReplicatedRecord {
            collection: record.collection().clone(),
            id: record.document().clone(),
            body,
            deleted,
            commit_ulid: record.operation_id().as_str().to_owned(),
            hlc: provenance.hlc(),
            origin: provenance.origin().to_owned(),
            origin_sequence: provenance.origin_sequence(),
            // JournalProvenance currently exposes the source dot but not the
            // opaque context blob. The storage/streaming owner must add that
            // accessor; until then an empty context is the only legacy-safe
            // representation (and decodes as the empty frontier).
            context: Vec::new(),
        });
    }
    Ok((records, batch.next_checkpoint()))
}

/// Applies replicated records on this host's engine, returning how many
/// published now — records already applied replay their receipt and count
/// as skips, never as double-writes.
///
/// # Errors
///
/// Returns [`HostingError`] when any record's apply fails on the engine.
pub fn apply_records(
    host: &EngineHost,
    scope: &BoundScope,
    records: &[ReplicatedRecord],
) -> Result<usize, HostingError> {
    apply_records_with_cancellation(host, scope, records, &Cancellation::default())
}

/// Applies a bounded batch while observing cancellation between records.
/// Already committed records remain safe to replay on a later retry because
/// the engine binds the source operation identity durably.
pub fn apply_records_with_cancellation(
    host: &EngineHost,
    scope: &BoundScope,
    records: &[ReplicatedRecord],
    cancellation: &Cancellation,
) -> Result<usize, HostingError> {
    let engine_scope = host.engine_scope_of(scope)?;
    let mut applied = 0;
    for record in records {
        if cancellation.is_cancelled() {
            return Err(HostingError::Refused("replication apply cancelled".into()));
        }
        let outcome = host.engine().apply_replicated(&engine_scope, record).map_err(|error| {
            HostingError::EngineFailure(format!(
                "the replicated record could not be applied: {error:?}"
            ))
        })?;
        if !outcome.replayed() {
            applied += 1;
        }
    }
    Ok(applied)
}

/// Ships every origin row the source has past the cursor onto the target,
/// advancing the cursor through every page it applied. Returns the number
/// of newly applied records; re-pulled records that had already been
/// applied are idempotent replays.
///
/// # Errors
///
/// Returns [`HostingError`] when a pull or an apply fails.
pub fn replicate_once(
    source: &EngineHost,
    target: &EngineHost,
    scope: &BoundScope,
    cursor: &mut Option<ChangeCheckpoint>,
) -> Result<usize, HostingError> {
    replicate_once_with_cancellation(source, target, scope, cursor, &Cancellation::default())
}

/// Ships every origin row past `cursor`, stopping at a cancellation boundary.
/// The cursor is advanced only after a complete page has been handed to the
/// target, so cancellation and transient transport failures are retry-safe.
pub fn replicate_once_with_cancellation(
    source: &EngineHost,
    target: &EngineHost,
    scope: &BoundScope,
    cursor: &mut Option<ChangeCheckpoint>,
    cancellation: &Cancellation,
) -> Result<usize, HostingError> {
    let mut applied = 0;
    loop {
        if cancellation.is_cancelled() {
            return Err(HostingError::Refused("replication cancelled".into()));
        }
        let (records, next) =
            pull_origin_records_with_cancellation(source, scope, *cursor, cancellation)?;
        if !records.is_empty() {
            applied += apply_records_with_cancellation(target, scope, &records, cancellation)?;
        }
        match next {
            Some(next) => *cursor = Some(next),
            // The journal is drained past the cursor.
            None => return Ok(applied),
        }
    }
}
