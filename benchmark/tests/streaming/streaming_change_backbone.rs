//! Public-construction tests for the SE-03 change-event backbone.
//!
//! These tests reach only what an external caller can construct: the
//! extended `ChangeFilter` selector, the journal `ChangeCheckpoint`, and
//! the `ChangeReplayRequest` validation rules. Scope-bound replay and
//! conflict-delivery behavior is proved by the in-crate tests in
//! `src/streaming/tests.rs`, because `TenantScope` has no public
//! constructor by design.

use std::time::Duration;

use terrabase_v2::types::CollectionName;
use terrabase_v2::{
    ChangeFilter, ChangeKind, DocumentId, OperationId, RequestValidationError, streaming,
    streaming::ChangeCheckpoint,
};

fn deadline_seconds() -> Duration {
    Duration::from_secs(30)
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

#[test]
fn change_filter_selector_refinements_validate_like_typed_selectors() {
    // Per-document refinement is infallible; prefix and operation
    // refinements validate their text and list shape.
    let base = ChangeFilter::try_new(None, None).expect("filter");
    let refined = base.clone().with_document(document("doc-1"));
    assert_eq!(refined.document(), Some(&document("doc-1")));
    assert_eq!(refined.prefix(), None);
    assert_eq!(refined.operations(), None);

    let prefixed = base.clone().with_prefix("doc-").expect("valid prefix");
    assert_eq!(prefixed.prefix(), Some("doc-"));
    assert_eq!(
        base.clone().with_prefix("").unwrap_err(),
        RequestValidationError::InvalidIdentifier
    );
    assert_eq!(
        base.clone().with_prefix(&"x".repeat(256)).unwrap_err(),
        RequestValidationError::InvalidIdentifier
    );

    let operations =
        base.clone().for_operations(vec![operation("op-1")]).expect("valid operations");
    assert_eq!(operations.operations(), Some(&[operation("op-1")][..]));
    assert_eq!(
        base.clone().for_operations(Vec::new()).unwrap_err(),
        RequestValidationError::EmptyField
    );

    // Conflict is a public, closed kind alongside the V1 shape set.
    let conflicts = ChangeFilter::try_new(Some(vec![ChangeKind::Conflict]), None).expect("filter");
    assert_eq!(conflicts.kinds(), Some(&[ChangeKind::Conflict][..]));
    let _ = deadline_seconds();
}

#[test]
fn change_checkpoint_and_replay_request_validate_their_bounds() {
    // A checkpoint is a public journal position with no engine binding:
    // any consumer may construct one to resume from.
    let checkpoint = ChangeCheckpoint::after(41, 3);
    assert_eq!(checkpoint.sequence(), 41);
    assert_eq!(checkpoint.write_index(), 3);
    assert_eq!(checkpoint, ChangeCheckpoint::after(41, 3));
    assert_ne!(checkpoint, ChangeCheckpoint::after(41, 4));

    let filter = ChangeFilter::try_new(None, Some(vec![collection("users")])).expect("filter");
    let request = streaming::ChangeReplayRequest::try_new(filter.clone(), Some(checkpoint), 128)
        .expect("valid request");
    assert_eq!(request.filter(), &filter);
    assert_eq!(request.after(), Some(checkpoint));
    assert_eq!(request.limit(), 128);

    assert_eq!(
        streaming::ChangeReplayRequest::try_new(filter.clone(), None, 0).unwrap_err(),
        RequestValidationError::InvalidRange
    );
    assert_eq!(
        streaming::ChangeReplayRequest::try_new(
            filter,
            None,
            streaming::MAX_STREAM_BATCH_ITEMS + 1
        )
        .unwrap_err(),
        RequestValidationError::InvalidRange
    );
    // `None` names the servable beginning: the oldest publication history
    // the journal can still serve.
    let beginning = streaming::ChangeReplayRequest::try_new(
        ChangeFilter::try_new(None, None).expect("filter"),
        None,
        16,
    )
    .expect("valid request");
    assert_eq!(beginning.after(), None);
}

#[test]
fn change_notification_tap_serves_the_public_trigger_surface() {
    // The tap is the public Change Notification surface (PD-2026-08-26-13
    // D3): an external caller acquires one through the stream store, drains
    // content-free two-identifier hints, and reads the counted drops - with
    // no scope at all, because a hint is engine-scoped and carries no
    // tenant dimension and no document data.
    let dir = std::env::temp_dir().join(format!("tbv2-se03-trigger-public-{}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    let engine = terrabase_v2::Engine::open(
        terrabase_v2::EngineConfig::durable(&dir).expect("durable location"),
    )
    .expect("opens");
    let store = engine.streams();
    let tap = store.change_notifications(4).expect("tap opens");
    assert!(tap.try_pop().is_none(), "an idle tap serves no hint");
    assert_eq!(tap.dropped(), 0);

    // The capacity bound is part of the request contract.
    assert_eq!(
        store.change_notifications(0).unwrap_err(),
        terrabase_v2::StreamError::InvalidSource
    );
    assert_eq!(
        store.change_notifications(streaming::MAX_CHANGE_NOTIFICATION_CAPACITY + 1).unwrap_err(),
        terrabase_v2::StreamError::InvalidSource
    );

    // Dropping the tap detaches its hint ingress; the engine keeps serving.
    drop(tap);
    let replacement = store.change_notifications(4).expect("a fresh tap opens after detach");
    assert!(replacement.try_pop().is_none());

    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}
