//! In-crate attachment content-plane tests for the GAP-F01 packet.
//!
//! Each test reads as one end-to-end scenario through the family seam: bytes
//! are staged inside one caller transaction, become readable only after that
//! transaction commits, and are observed through bounded segment-by-segment
//! readers. The V1-observable semantics these tests pin (range normalization,
//! disposal, end-of-stream, truncation) carry their source citations in
//! `docs/families/attachment-content-plane.md`; the tests themselves make no
//! V1 parity claim beyond those pinned behaviors and create no Recovery,
//! Replication, or Benchmark receipt.

#![allow(clippy::too_many_lines)]

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::super::FamilyReadContext;
use super::content::{
    AttachmentContentDelete, AttachmentContentError, AttachmentContentRead,
    AttachmentContentReader, AttachmentContentWrite, AttachmentRange, AttachmentSegment,
    MAX_CONTENT_BYTES, MAX_SEGMENT_BYTES,
};
use super::digest::{Sha256, to_hex};

use crate::{
    engine::{Engine, EngineConfig},
    types::{
        Cancellation, Deadline, DocumentId, ExpectedVersion, LocalDurability, OperationId,
        TenantId, TenantScope,
    },
};

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapf01-content-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(
        TenantId::try_new(tenant_name).expect("valid tenant"),
        engine.identity(),
    )
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(45)).expect("valid deadline")
}

fn cancellation() -> Cancellation {
    Cancellation::new()
}

fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}")).expect("valid identifier")
}

/// Deterministic content pattern: byte `i` is `i % 251`, so ranges are
/// self-describing without randomness.
fn pattern(len: usize) -> Vec<u8> {
    (0..len).map(|i| u8::try_from(i % 251).expect("pattern byte")).collect()
}

fn segments_of(bytes: &[u8], segment_len: usize) -> Vec<AttachmentSegment> {
    if bytes.is_empty() {
        return Vec::new();
    }
    bytes
        .chunks(segment_len)
        .map(|chunk| AttachmentSegment::try_new(chunk).expect("valid segment"))
        .collect()
}

fn digest_of(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    to_hex(&hasher.finish())
}

/// Stages content through the family seam inside one committed transaction.
fn stage_content(
    engine: &Engine,
    tenant_name: &str,
    bytes: &[u8],
    segment_len: usize,
) -> Result<String, AttachmentContentError> {
    stage_content_declared(engine, tenant_name, bytes, segment_len, None)
}

fn stage_content_declared(
    engine: &Engine,
    tenant_name: &str,
    bytes: &[u8],
    segment_len: usize,
    declared: Option<String>,
) -> Result<String, AttachmentContentError> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("content-put");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let write = AttachmentContentWrite::try_new(
        tenant_scope,
        declared,
        segments_of(bytes, segment_len),
        identity,
        deadline(),
        cancellation(),
    )
    .expect("valid content write");
    let recorded = store.stage_attachment_content(&mut transaction, write)?;
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    Ok(recorded.digest().to_owned())
}

/// Deletes content through the family seam inside one committed transaction.
fn delete_content(
    engine: &Engine,
    tenant_name: &str,
    digest: &str,
) -> Result<(), AttachmentContentError> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("content-delete");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let command = AttachmentContentDelete::try_new(
        tenant_scope,
        digest.to_owned(),
        identity,
        deadline(),
        cancellation(),
    )
    .expect("valid content delete");
    store.stage_attachment_content_delete(&mut transaction, command)?;
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    Ok(())
}

fn open_reader<'engine>(
    engine: &'engine Engine,
    tenant_name: &str,
    digest: &str,
    range: Option<AttachmentRange>,
) -> Result<AttachmentContentReader<'engine>, AttachmentContentError> {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.families();
    store.open_attachment_content(
        FamilyReadContext::new(tenant_scope, snapshot),
        AttachmentContentRead::try_new(digest.to_owned(), range, deadline(), cancellation())
            .expect("valid content read"),
    )
}

/// Reads everything the reader offers in bounded `chunk`-sized steps.
fn drain(reader: &mut AttachmentContentReader<'_>, chunk: usize) -> Vec<u8> {
    let mut collected = Vec::new();
    let mut step = vec![0_u8; chunk];
    loop {
        let read = reader.read(&mut step).expect("reads a bounded step");
        if read == 0 {
            return collected;
        }
        collected.extend_from_slice(&step[..read]);
    }
}

// ---------------------------------------------------------------------------
// Byte storage and bounded readers
// ---------------------------------------------------------------------------

#[test]
fn content_round_trips_through_committed_transactions() {
    let (_dir, engine) = open_engine("round-trip");
    let bytes = pattern(5000);
    let digest =
        stage_content(&engine, "tenant-a", &bytes, 1024).expect("stages across five segments");
    assert_eq!(digest, digest_of(&bytes), "the plane derives the digest itself");

    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens after commit");
    assert_eq!(reader.digest(), digest);
    assert_eq!(reader.content_size(), 5000);
    assert_eq!(reader.length(), 5000, "an un-ranged read covers the whole content");
    assert_eq!(reader.position(), 0);
    assert_eq!(drain(&mut reader, 64), bytes);
    assert_eq!(reader.position(), 5000, "position advances to the end");
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn content_reads_stay_bounded_across_segment_boundaries() {
    let (_dir, engine) = open_engine("bounded-reads");
    let bytes = pattern(700);
    let digest = stage_content(&engine, "tenant-a", &bytes, 256).expect("stages three segments");

    // One-byte steps force the reader to cross both segment boundaries and
    // partial buffer fills without ever holding a whole segment's bytes in
    // the caller's buffer.
    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    assert_eq!(drain(&mut reader, 1), bytes);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn staged_content_is_visible_only_after_commit() {
    let (_dir, engine) = open_engine("abort-hides");
    let bytes = pattern(300);
    let digest = digest_of(&bytes);
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("content-put");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let write = AttachmentContentWrite::try_new(
        tenant_scope,
        None,
        segments_of(&bytes, 256),
        identity,
        deadline(),
        cancellation(),
    )
    .expect("valid content write");
    store.stage_attachment_content(&mut transaction, write).expect("stages inside the transaction");
    drop(transaction);

    let error = open_reader(&engine, "tenant-a", &digest, None).expect_err("abort hid the bytes");
    assert_eq!(error, AttachmentContentError::ContentMissing);
    engine.shutdown().expect("shuts down");
}

#[test]
fn content_plane_is_tenant_scoped() {
    let (_dir, engine) = open_engine("tenant-scope");
    let bytes = pattern(100);
    let digest = stage_content(&engine, "tenant-a", &bytes, 64).expect("stages");

    let error =
        open_reader(&engine, "tenant-b", &digest, None).expect_err("bytes are tenant-scoped");
    assert_eq!(error, AttachmentContentError::ContentMissing);
    engine.shutdown().expect("shuts down");
}

#[test]
fn identical_content_deduplicates_by_digest() {
    let (_dir, engine) = open_engine("dedup");
    let bytes = pattern(2048);
    let first = stage_content(&engine, "tenant-a", &bytes, 512).expect("first stage");
    let second = stage_content(&engine, "tenant-a", &bytes, 512).expect("second stage re-dedups");
    assert_eq!(first, second, "the same bytes stage as the same content identity");

    let mut reader = open_reader(&engine, "tenant-a", &first, None).expect("still readable");
    assert_eq!(drain(&mut reader, 128), bytes);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn empty_content_round_trips() {
    let (_dir, engine) = open_engine("empty");
    let digest = stage_content(&engine, "tenant-a", &[], 512).expect("empty content is legal");
    assert_eq!(digest, digest_of(&[]));

    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    assert_eq!(reader.content_size(), 0);
    assert_eq!(reader.length(), 0);
    assert_eq!(reader.read(&mut [0_u8; 8]).expect("reads"), 0, "empty is end-of-stream");
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn empty_segments_stage_as_canonical_content() {
    let (_dir, engine) = open_engine("empty-segments");

    // A write whose every segment is empty is empty content: it must stage
    // as the canonical empty manifest, not as a manifest the reader rejects.
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("content-put");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let write = AttachmentContentWrite::try_new(
        tenant_scope,
        None,
        vec![AttachmentSegment::try_new(&[]).expect("empty segments are structurally legal")],
        identity,
        deadline(),
        cancellation(),
    )
    .expect("valid content write");
    let recorded =
        store.stage_attachment_content(&mut transaction, write).expect("stages as empty content");
    assert_eq!(recorded.digest(), digest_of(&[]), "empty segments hash as empty content");
    assert_eq!(recorded.size(), 0);
    assert_eq!(recorded.segments(), 0, "no segment records are written");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));

    let mut reader = open_reader(&engine, "tenant-a", recorded.digest(), None)
        .expect("the staged manifest reads back");
    assert_eq!(reader.content_size(), 0);
    assert_eq!(reader.read(&mut [0_u8; 4]).expect("reads"), 0);

    // Staging the same (empty) content again deduplicates instead of
    // failing on the manifest written above.
    let again = stage_content_declared(&engine, "tenant-a", &[], 64, Some(digest_of(&[])))
        .expect("re-staging empty content deduplicates");
    assert_eq!(again, digest_of(&[]));
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn empty_segments_among_bytes_do_not_change_content() {
    let (_dir, engine) = open_engine("empty-mixed");
    let head = pattern(100);
    let tail = pattern(100);
    let mut bytes = head.clone();
    bytes.extend_from_slice(&tail);

    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("content-put");
    let store = engine.families();
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
    let write = AttachmentContentWrite::try_new(
        tenant_scope,
        None,
        vec![
            AttachmentSegment::try_new(&head).expect("valid segment"),
            AttachmentSegment::try_new(&[]).expect("empty segments are structurally legal"),
            AttachmentSegment::try_new(&tail).expect("valid segment"),
        ],
        identity,
        deadline(),
        cancellation(),
    )
    .expect("valid content write");
    let recorded = store.stage_attachment_content(&mut transaction, write).expect("stages");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
    assert_eq!(recorded.digest(), digest_of(&bytes), "empty segments contribute no bytes");
    assert_eq!(recorded.size(), 200);

    let digest = recorded.digest().to_owned();
    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    assert_eq!(drain(&mut reader, 33), bytes);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Size limits
// ---------------------------------------------------------------------------

#[test]
fn segment_cap_is_rejected_at_construction() {
    let oversized = pattern(MAX_SEGMENT_BYTES + 1);
    assert!(
        AttachmentSegment::try_new(&oversized).is_err(),
        "one segment can never exceed the per-record segment cap"
    );
    let exact = pattern(MAX_SEGMENT_BYTES);
    assert!(AttachmentSegment::try_new(&exact).is_ok(), "the cap itself is legal");
}

#[test]
fn total_size_limit_is_an_explicit_typed_error() {
    let (_dir, engine) = open_engine("size-limit");
    // One segment over the plane's ceiling, in as few records as possible.
    let over = usize::try_from(MAX_CONTENT_BYTES).expect("fits usize") + 1;
    let bytes = pattern(over);
    let error = stage_content(&engine, "tenant-a", &bytes, MAX_SEGMENT_BYTES)
        .expect_err("over-limit content is refused");
    assert_eq!(error, AttachmentContentError::SizeLimitExceeded);

    let at_limit = pattern(usize::try_from(MAX_CONTENT_BYTES).expect("fits usize"));
    let digest = stage_content(&engine, "tenant-a", &at_limit, MAX_SEGMENT_BYTES)
        .expect("the ceiling itself is legal");
    assert_eq!(digest, digest_of(&at_limit));
    engine.shutdown().expect("shuts down");
}

#[test]
fn declared_digest_mismatch_is_a_typed_error() {
    let (_dir, engine) = open_engine("digest-mismatch");
    let bytes = pattern(400);
    let error =
        stage_content_declared(&engine, "tenant-a", &bytes, 128, Some(digest_of(&pattern(399))))
            .expect_err("a wrong declared digest never stages");
    assert_eq!(error, AttachmentContentError::DigestMismatch);

    let digest = stage_content_declared(&engine, "tenant-a", &bytes, 128, Some(digest_of(&bytes)))
        .expect("a correct declared digest stages");
    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    assert_eq!(drain(&mut reader, 100), bytes);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Range semantics (V1 RangeHelper normalization)
// ---------------------------------------------------------------------------

fn range_bytes(from: u64, to: Option<u64>) -> AttachmentRange {
    AttachmentRange::Bytes { from, to }
}

#[test]
fn open_ended_ranges_clamp_to_the_last_byte() {
    let (_dir, engine) = open_engine("range-clamp");
    let bytes = pattern(1000);
    let digest = stage_content(&engine, "tenant-a", &bytes, 256).expect("stages");

    let mut reader =
        open_reader(&engine, "tenant-a", &digest, Some(range_bytes(10, None))).expect("opens");
    assert_eq!(reader.content_size(), 1000);
    assert_eq!(reader.length(), 990, "`from` to the end is 1000 - 10 bytes");
    assert_eq!(drain(&mut reader, 64), bytes[10..]);

    // V1 clamps an end past the content to the last byte instead of failing.
    let mut clamped =
        open_reader(&engine, "tenant-a", &digest, Some(range_bytes(990, Some(500_000))))
            .expect("opens");
    assert_eq!(clamped.length(), 10);
    assert_eq!(drain(&mut clamped, 7), bytes[990..]);
    drop(reader);
    drop(clamped);
    engine.shutdown().expect("shuts down");
}

#[test]
fn suffix_ranges_resolve_the_last_bytes() {
    let (_dir, engine) = open_engine("range-suffix");
    let bytes = pattern(1000);
    let digest = stage_content(&engine, "tenant-a", &bytes, 256).expect("stages");

    let mut reader =
        open_reader(&engine, "tenant-a", &digest, Some(AttachmentRange::Suffix { bytes: 10 }))
            .expect("opens");
    assert_eq!(reader.length(), 10);
    assert_eq!(drain(&mut reader, 4), bytes[990..]);

    // V1 caps a suffix longer than the content at the whole content.
    let mut whole =
        open_reader(&engine, "tenant-a", &digest, Some(AttachmentRange::Suffix { bytes: 10_000 }))
            .expect("opens");
    assert_eq!(whole.length(), 1000);
    assert_eq!(drain(&mut whole, 64), bytes);
    drop(reader);
    drop(whole);
    engine.shutdown().expect("shuts down");
}

#[test]
fn unsatisfiable_ranges_are_typed_errors() {
    let (_dir, engine) = open_engine("range-invalid");
    let bytes = pattern(1000);
    let digest = stage_content(&engine, "tenant-a", &bytes, 256).expect("stages");

    // V1 rejects a start at or past the content size.
    let start_error = open_reader(&engine, "tenant-a", &digest, Some(range_bytes(1000, None)))
        .expect_err("start at size");
    assert_eq!(start_error, AttachmentContentError::RangeInvalid);
    let past_error = open_reader(&engine, "tenant-a", &digest, Some(range_bytes(1001, Some(1010))))
        .expect_err("start past size");
    assert_eq!(past_error, AttachmentContentError::RangeInvalid);

    // V1 rejects the `-0` suffix as unsatisfiable.
    let zero_suffix =
        open_reader(&engine, "tenant-a", &digest, Some(AttachmentRange::Suffix { bytes: 0 }))
            .expect_err("a zero-length suffix never satisfies");
    assert_eq!(zero_suffix, AttachmentContentError::RangeInvalid);

    // V1 treats any range over empty content as unsatisfiable.
    let empty = stage_content(&engine, "tenant-a", &[], 64).expect("empty content");
    let empty_error = open_reader(&engine, "tenant-a", &empty, Some(range_bytes(0, None)))
        .expect_err("no byte satisfies a range over empty content");
    assert_eq!(empty_error, AttachmentContentError::RangeInvalid);
    engine.shutdown().expect("shuts down");
}

#[test]
fn inverted_ranges_are_rejected_at_construction() {
    let error = AttachmentContentRead::try_new(
        digest_of(&pattern(8)),
        Some(range_bytes(100, Some(40))),
        deadline(),
        cancellation(),
    )
    .unwrap_err();
    assert_eq!(error, crate::operations::RequestValidationError::InvalidRange);
}

// ---------------------------------------------------------------------------
// Disposal, seek, and end-of-stream semantics (V1 LimitedStream)
// ---------------------------------------------------------------------------

#[test]
fn reads_after_disposal_are_typed_errors() {
    let (_dir, engine) = open_engine("disposed");
    let bytes = pattern(600);
    let digest = stage_content(&engine, "tenant-a", &bytes, 128).expect("stages");
    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    let mut step = [0_u8; 16];
    assert_eq!(reader.read(&mut step).expect("reads before disposal"), 16);

    reader.dispose();
    assert!(reader.is_disposed());
    assert_eq!(
        reader.read(&mut step),
        Err(AttachmentContentError::Disposed),
        "V1 throws ObjectDisposedException on reads after disposal"
    );
    assert_eq!(reader.seek(0), Err(AttachmentContentError::Disposed));

    // A dropped reader is disposed too; a fresh reader opens independently.
    drop(reader);
    let mut fresh = open_reader(&engine, "tenant-a", &digest, None).expect("reopens");
    assert_eq!(drain(&mut fresh, 32), bytes);
    drop(fresh);
    engine.shutdown().expect("shuts down");
}

#[test]
fn partial_consumption_and_skip_are_legal() {
    let (_dir, engine) = open_engine("partial");
    let bytes = pattern(900);
    let digest = stage_content(&engine, "tenant-a", &bytes, 256).expect("stages");

    // V1 allows enumerating attachments and reading none or some of them.
    let skipped = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    drop(skipped);

    let mut partial = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    let mut step = [0_u8; 10];
    assert_eq!(partial.read(&mut step).expect("reads"), 10);
    assert_eq!(&step, &bytes[..10]);
    assert_eq!(partial.position(), 10);
    drop(partial);
    engine.shutdown().expect("shuts down");
}

#[test]
fn seek_follows_v1_limited_stream_bounds() {
    let (_dir, engine) = open_engine("seek");
    let bytes = pattern(500);
    let digest = stage_content(&engine, "tenant-a", &bytes, 128).expect("stages");
    let mut reader =
        open_reader(&engine, "tenant-a", &digest, Some(range_bytes(100, None))).expect("opens");
    assert_eq!(reader.length(), 400);

    // Seeking to the exact end is legal; reading there reports end-of-stream.
    reader.seek(400).expect("seek to the end is legal");
    assert_eq!(reader.position(), 400);
    assert_eq!(reader.read(&mut [0_u8; 4]).expect("reads"), 0);

    // V1 throws ArgumentOutOfRangeException past the declared length; the
    // typed form keeps it distinct from an unsatisfiable open-time range.
    assert_eq!(reader.seek(401), Err(AttachmentContentError::PositionInvalid));

    // Seeking back re-reads the same bytes.
    reader.seek(0).expect("seeks back");
    let mut step = [0_u8; 20];
    assert_eq!(reader.read(&mut step).expect("reads"), 20);
    assert_eq!(&step, &bytes[100..120]);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn end_of_stream_reports_zero_repeatedly() {
    let (_dir, engine) = open_engine("end-of-stream");
    let bytes = pattern(50);
    let digest = stage_content(&engine, "tenant-a", &bytes, 16).expect("stages");
    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("opens");
    assert_eq!(drain(&mut reader, 16), bytes);
    for _ in 0..3 {
        assert_eq!(reader.read(&mut [0_u8; 8]).expect("reads"), 0, "end-of-stream stays zero");
    }
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn readers_observe_their_open_time_snapshot() {
    let (_dir, engine) = open_engine("snapshot-reader");
    let first = pattern(400);
    let second = pattern(4000);
    let first_digest = stage_content(&engine, "tenant-a", &first, 128).expect("stages first");
    let mut reader = open_reader(&engine, "tenant-a", &first_digest, None).expect("opens");
    let second_digest = stage_content(&engine, "tenant-a", &second, 128).expect("stages second");
    assert_ne!(first_digest, second_digest);

    // The live reader keeps observing its open-time snapshot.
    assert_eq!(reader.content_size(), 400);
    assert_eq!(drain(&mut reader, 64), first);

    let mut later = open_reader(&engine, "tenant-a", &second_digest, None).expect("opens");
    assert_eq!(drain(&mut later, 64), second);
    drop(reader);
    drop(later);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Truncation and corruption fail closed
// ---------------------------------------------------------------------------

/// Clears one reserved record through the raw document seam.
fn clear_record(engine: &Engine, tenant_name: &str, id: &DocumentId) {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("corrupt");
    let mut transaction = engine.begin(&tenant_scope, snapshot, identity).expect("begins");
    transaction.delete(id.clone(), ExpectedVersion::Any).expect("clears the record");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        crate::engine::CommitOutcome::Committed(_)
    ));
}

fn segment_record_id(digest: &str, index: u64) -> DocumentId {
    super::super::state::record_id("attachments/content", &[digest, &index.to_string()])
        .expect("valid record id")
}

#[test]
fn missing_segments_fail_closed_as_truncated() {
    let (_dir, engine) = open_engine("truncated-missing");
    let bytes = pattern(600);
    let digest = stage_content(&engine, "tenant-a", &bytes, 128).expect("stages five segments");
    clear_record(&engine, "tenant-a", &segment_record_id(&digest, 2));

    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("the manifest stands");
    let mut step = [0_u8; 64];
    let mut seen = Vec::new();
    loop {
        match reader.read(&mut step) {
            Ok(0) | Err(_) => break,
            Ok(read) => seen.extend_from_slice(&step[..read]),
        }
    }
    assert_eq!(seen, bytes[..256], "reads deliver the intact prefix and then fail closed");
    // A read that would need the missing segment reports the typed truncation.
    assert_eq!(reader.position(), 256);
    assert_eq!(reader.read(&mut step), Err(AttachmentContentError::Truncated));
    drop(reader);
    engine.shutdown().expect("shuts down");
}

#[test]
fn a_missing_manifest_is_a_typed_missing_error() {
    let (_dir, engine) = open_engine("missing-manifest");
    let digest = digest_of(&pattern(64));
    let error =
        open_reader(&engine, "tenant-a", &digest, None).expect_err("nothing was ever staged");
    assert_eq!(error, AttachmentContentError::ContentMissing);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Deletion
// ---------------------------------------------------------------------------

#[test]
fn content_delete_removes_bytes_and_reports_missing() {
    let (_dir, engine) = open_engine("delete");
    let bytes = pattern(300);
    let digest = stage_content(&engine, "tenant-a", &bytes, 128).expect("stages");
    delete_content(&engine, "tenant-a", &digest).expect("deletes");

    let error = open_reader(&engine, "tenant-a", &digest, None).expect_err("bytes are gone");
    assert_eq!(error, AttachmentContentError::ContentMissing);

    let again = delete_content(&engine, "tenant-a", &digest)
        .expect_err("deleting missing content is a typed error");
    assert_eq!(again, AttachmentContentError::ContentMissing);
    engine.shutdown().expect("shuts down");
}

#[test]
fn delete_of_other_tenants_content_is_missing() {
    let (_dir, engine) = open_engine("delete-scope");
    let bytes = pattern(200);
    let digest = stage_content(&engine, "tenant-a", &bytes, 64).expect("stages");
    let error = delete_content(&engine, "tenant-b", &digest)
        .expect_err("tenant B cannot delete tenant A bytes");
    assert_eq!(error, AttachmentContentError::ContentMissing);

    let mut reader = open_reader(&engine, "tenant-a", &digest, None).expect("still readable");
    assert_eq!(drain(&mut reader, 64), bytes);
    drop(reader);
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Transaction binding
// ---------------------------------------------------------------------------

#[test]
fn content_stage_validates_transaction_identity() {
    let (_dir, engine) = open_engine("identity");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let identity = unique_operation("transaction");
    let other = unique_operation("payload");
    let store = engine.families();
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, identity).expect("begins with one identity");
    let write = AttachmentContentWrite::try_new(
        tenant_scope,
        None,
        segments_of(&pattern(64), 64),
        other,
        deadline(),
        cancellation(),
    )
    .expect("valid content write bound to another identity");
    let error = store
        .stage_attachment_content(&mut transaction, write)
        .expect_err("the payload identity must match the transaction");
    assert_eq!(error, AttachmentContentError::TransactionIdentityMismatch);
    drop(transaction);
    engine.shutdown().expect("shuts down");
}

#[test]
fn content_stage_rejects_elapsed_deadlines_and_cancelled_signals() {
    let (_dir, engine) = open_engine("guards");
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let store = engine.families();

    // A deadline that is valid at construction but elapsed before staging is
    // the staged typed failure; construction rejects only deadlines already
    // expired when the payload is built.
    let identity = unique_operation("guarded-deadline");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot.clone(), identity.clone()).expect("begins");
    let write = AttachmentContentWrite::try_new(
        tenant_scope.clone(),
        None,
        segments_of(&pattern(32), 32),
        identity,
        Deadline::after(Duration::from_millis(30)).expect("valid deadline"),
        cancellation(),
    )
    .expect("valid at construction");
    std::thread::sleep(Duration::from_millis(80));
    let error = store
        .stage_attachment_content(&mut transaction, write)
        .expect_err("an elapsed deadline never stages");
    assert_eq!(error, AttachmentContentError::DeadlineExceeded);
    drop(transaction);

    // A signal already cancelled when the request is built is a constructor
    // rejection, so no store call is needed (the shared payload rule).
    let cancelled = Cancellation::new();
    cancelled.cancel();
    let identity = unique_operation("guarded-cancel");
    assert_eq!(
        AttachmentContentWrite::try_new(
            scope(&engine, "tenant-a"),
            None,
            segments_of(&pattern(32), 32),
            identity,
            deadline(),
            cancelled,
        )
        .map(|_| ()),
        Err(crate::operations::RequestValidationError::InvalidCancellation)
    );
    engine.shutdown().expect("shuts down");
}

// ---------------------------------------------------------------------------
// Codec
// ---------------------------------------------------------------------------

#[test]
fn non_canonical_base64_trailing_bits_fail_closed() {
    // The unused low bits of the final data sextet must be zero; anything
    // else is a second encoding of the same bytes and fails closed.
    assert!(super::codec::base64_decode(b"QQ==").is_ok(), "canonical one-byte padding-2");
    assert!(super::codec::base64_decode(b"QR==").is_err(), "'R' carries set trailing bits");
    assert!(super::codec::base64_decode(b"QQA=").is_ok(), "canonical two-byte padding-1");
    assert!(super::codec::base64_decode(b"QUC=").is_err(), "'C' carries set trailing bits");
    assert!(super::codec::base64_decode(b"QUD=").is_err(), "'D' carries set trailing bits");
}

#[test]
fn base64_codec_round_trips_every_length_shape() {
    for len in 0..70 {
        let bytes = pattern(len);
        let encoded = super::codec::base64_encode(&bytes);
        assert_eq!(encoded.len() % 4, 0, "padded base64 has a multiple-of-four length");
        let decoded = super::codec::base64_decode(encoded.as_bytes())
            .unwrap_or_else(|error| panic!("decodes len {len}: {error:?}"));
        assert_eq!(decoded, bytes, "round trip at len {len}");
    }
    assert!(super::codec::base64_decode(b"!!!!").is_err(), "invalid alphabet fails closed");
    assert!(super::codec::base64_decode(b"abc").is_err(), "unpadded input fails closed");
    assert_eq!(super::codec::base64_encode(b"foob"), "Zm9vYg==", "RFC 4648 test vector");
    assert_eq!(super::codec::base64_encode(b"fooba"), "Zm9vYmE=", "RFC 4648 test vector");
    assert_eq!(super::codec::base64_encode(b"foobar "), "Zm9vYmFyIA==", "RFC 4648 test vector");
}
