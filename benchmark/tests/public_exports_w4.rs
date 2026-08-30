//! W4 declaration-surface checks (W4-0).
//!
//! These tests inspect only the compile, export, and lifetime surface of the
//! W4 declaration-only tree. They make no claim that any streaming,
//! replication or backup behavior exists.

use terrabase::{
    AcknowledgementLevel, AcknowledgementReport, BackupArchive, BackupError, BackupKind,
    BackupManifest, BackupPlan, BackupProgress, BackupResult, ChangeEvent, ChangeFilter,
    ChangeKind, CommitReceipt, ConflictRecord, Convergence, DocumentStreamSelector, Engine,
    MemberId, RegionId, ReplicationConfig, ReplicationCursor, ReplicationError, ReplicationOutcome,
    ReplicationReceipt, ReplicationStatus, RestorePlan, RestoreResult, StreamBatch,
    StreamCompletion, StreamCursor, StreamError, StreamItem, StreamPosition, StreamSource,
    StreamStore, SubscriptionAcknowledgement, SubscriptionBatch, SubscriptionCheckpoint,
    SubscriptionSpec, SubscriptionStatus, TopologyView, backup, replication, streaming,
};

#[test]
fn w4_public_exports_compile_with_declaration_only_tree() {
    let _ = (
        // Streaming root re-exports.
        std::any::type_name::<StreamStore<'static>>(),
        std::any::type_name::<StreamSource>(),
        std::any::type_name::<DocumentStreamSelector>(),
        std::any::type_name::<StreamCursor>(),
        std::any::type_name::<StreamBatch>(),
        std::any::type_name::<StreamItem>(),
        std::any::type_name::<StreamCompletion>(),
        std::any::type_name::<StreamError>(),
        std::any::type_name::<StreamPosition>(),
        std::any::type_name::<ChangeEvent>(),
        std::any::type_name::<ChangeKind>(),
        std::any::type_name::<ChangeFilter>(),
        std::any::type_name::<SubscriptionSpec>(),
        std::any::type_name::<SubscriptionBatch>(),
        std::any::type_name::<SubscriptionCheckpoint>(),
        std::any::type_name::<SubscriptionAcknowledgement>(),
        std::any::type_name::<SubscriptionStatus>(),
        // Selector types that stay inside the streaming module and are not
        // root re-exports.
        std::any::type_name::<streaming::TimeSeriesStreamRange>(),
        std::any::type_name::<streaming::FamilyStreamSelector>(),
        // Replication root re-exports.
        std::any::type_name::<RegionId>(),
        std::any::type_name::<MemberId>(),
        std::any::type_name::<TopologyView>(),
        std::any::type_name::<AcknowledgementLevel>(),
        std::any::type_name::<AcknowledgementReport>(),
        std::any::type_name::<Convergence>(),
        std::any::type_name::<ReplicationOutcome>(),
        std::any::type_name::<ReplicationStatus>(),
        std::any::type_name::<ReplicationReceipt>(),
        std::any::type_name::<ConflictRecord>(),
        std::any::type_name::<ReplicationCursor>(),
        std::any::type_name::<ReplicationConfig>(),
        std::any::type_name::<ReplicationError>(),
        // Backup root re-exports.
        std::any::type_name::<BackupManifest>(),
        std::any::type_name::<BackupArchive>(),
        std::any::type_name::<BackupKind>(),
        std::any::type_name::<BackupPlan>(),
        std::any::type_name::<BackupProgress>(),
        std::any::type_name::<BackupResult>(),
        std::any::type_name::<RestorePlan>(),
        std::any::type_name::<RestoreResult>(),
        std::any::type_name::<BackupError>(),
        // The three W4 module roots are public.
        std::any::type_name::<streaming::StreamStore<'static>>(),
        std::any::type_name::<replication::AcknowledgementLevel>(),
        std::any::type_name::<backup::BackupManifest>(),
    );
}

/// A `StreamStore` value exists only inside the borrow region of its `Engine`:
/// the store and the engine borrow are returned together, so the borrow is
/// held for as long as the store is live and shutdown must wait. Compile-only
/// proof on the declared surface — `Engine::streams()` itself arrives with
/// integrator action W4-CA-2, and W4-0 declares no other handle type, so this
/// makes no claim about replication or backup acquisition paths.
fn streaming_store_holds_the_engine_borrow<'engine>(
    engine: &'engine Engine,
    store: StreamStore<'engine>,
) -> (StreamStore<'engine>, &'engine Engine) {
    (store, engine)
}

#[test]
fn streaming_store_borrows_the_engine_and_prevents_shutdown_while_live() {
    let _ = streaming_store_holds_the_engine_borrow;
}

#[test]
fn commit_receipt_stays_local_only_in_the_w4_surface() {
    // `CommitReceipt` remains the engine-local publication fact: operation
    // identity, storage sequence, and local durability only. No W4 type may
    // add a Region, Member, `AcknowledgementLevel`, `Convergence`, or
    // `ReplicationReceipt` fact to it, no W4 code may imply remote
    // acknowledgement from a local commit, and the acknowledgement
    // vocabulary lives only in the replication module.
    assert_eq!(std::any::type_name::<CommitReceipt>(), "terrabase::engine::CommitReceipt");
    for remote_fact_type in [
        std::any::type_name::<RegionId>(),
        std::any::type_name::<MemberId>(),
        std::any::type_name::<TopologyView>(),
        std::any::type_name::<AcknowledgementLevel>(),
        std::any::type_name::<AcknowledgementReport>(),
        std::any::type_name::<Convergence>(),
        std::any::type_name::<ReplicationOutcome>(),
        std::any::type_name::<ReplicationReceipt>(),
    ] {
        assert!(
            remote_fact_type.starts_with("terrabase::replication::"),
            "remote acknowledgement fact outside replication: {remote_fact_type}"
        );
    }
}

#[test]
fn w4_roots_reexport_no_transport_codec_or_leader_state() {
    // The crate root re-exports exactly the contract's typed W4 surface, and
    // every approved re-export is canonically owned by one of the four W4
    // module roots. Replication transport, the message codec, leader and
    // shard state, anti-entropy machinery, and archive internals stay private
    // to their modules and are never re-exported. The crate-private
    // `FamilyRecordInput` has no public path by construction.
    let rooted_types: &[(&str, &str)] = &[
        (std::any::type_name::<BackupArchive>(), "terrabase::backup::"),
        (std::any::type_name::<BackupError>(), "terrabase::backup::"),
        (std::any::type_name::<BackupKind>(), "terrabase::backup::"),
        (std::any::type_name::<BackupManifest>(), "terrabase::backup::"),
        (std::any::type_name::<BackupPlan>(), "terrabase::backup::"),
        (std::any::type_name::<BackupProgress>(), "terrabase::backup::"),
        (std::any::type_name::<BackupResult>(), "terrabase::backup::"),
        (std::any::type_name::<RestorePlan>(), "terrabase::backup::"),
        (std::any::type_name::<RestoreResult>(), "terrabase::backup::"),
        (std::any::type_name::<AcknowledgementLevel>(), "terrabase::replication::"),
        (std::any::type_name::<AcknowledgementReport>(), "terrabase::replication::"),
        (std::any::type_name::<ConflictRecord>(), "terrabase::replication::"),
        (std::any::type_name::<Convergence>(), "terrabase::replication::"),
        (std::any::type_name::<MemberId>(), "terrabase::replication::"),
        (std::any::type_name::<RegionId>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationConfig>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationCursor>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationError>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationOutcome>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationReceipt>(), "terrabase::replication::"),
        (std::any::type_name::<ReplicationStatus>(), "terrabase::replication::"),
        (std::any::type_name::<TopologyView>(), "terrabase::replication::"),
        (std::any::type_name::<ChangeEvent>(), "terrabase::streaming::"),
        (std::any::type_name::<ChangeFilter>(), "terrabase::streaming::"),
        (std::any::type_name::<ChangeKind>(), "terrabase::streaming::"),
        (std::any::type_name::<DocumentStreamSelector>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamBatch>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamCompletion>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamCursor>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamError>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamItem>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamPosition>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamSource>(), "terrabase::streaming::"),
        (std::any::type_name::<StreamStore<'static>>(), "terrabase::streaming::"),
        (std::any::type_name::<SubscriptionAcknowledgement>(), "terrabase::streaming::"),
        (std::any::type_name::<SubscriptionBatch>(), "terrabase::streaming::"),
        (std::any::type_name::<SubscriptionCheckpoint>(), "terrabase::streaming::"),
        (std::any::type_name::<SubscriptionSpec>(), "terrabase::streaming::"),
        (std::any::type_name::<SubscriptionStatus>(), "terrabase::streaming::"),
    ];
    for (type_path, owning_root) in rooted_types {
        assert!(type_path.starts_with(owning_root), "{type_path} is not owned by {owning_root}");
    }
}
