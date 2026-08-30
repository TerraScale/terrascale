//! Public-construction tests for the lifecycle execution payloads
//! (GAP-F06).
//!
//! These tests reach only what an external caller can construct. The
//! execution behavior — planned passes, staged deletions, refresh
//! consumption, visibility filtering — is covered by the in-crate suite in
//! `src/families/lifecycle.rs`, which can seed documents through the
//! crate-internal `TenantScope` fixture.

use std::time::Duration;

use terrabase::{
    Cancellation, Deadline, DocumentId, RequestValidationError,
    families::lifecycle::{LifecyclePassStaged, VisibilityRead, VisibilityState},
};

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn document(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

#[test]
fn visibility_reads_validate_typed_fields_before_observation() {
    assert_eq!(
        VisibilityRead::try_new(Vec::new(), deadline(), Cancellation::new()).err(),
        Some(RequestValidationError::InvalidFamilyPayload),
        "an empty batch is not a visibility read"
    );
    let elapsed = Deadline::after(Duration::from_millis(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(40));
    assert_eq!(
        VisibilityRead::try_new(vec![document("doc-1")], elapsed, Cancellation::new()).err(),
        Some(RequestValidationError::InvalidDeadline)
    );
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        VisibilityRead::try_new(vec![document("doc-1")], deadline(), cancelled).err(),
        Some(RequestValidationError::InvalidCancellation)
    );

    let read = VisibilityRead::try_new(
        vec![document("doc-1"), document("doc-2")],
        deadline(),
        Cancellation::new(),
    )
    .expect("valid visibility read");
    assert_eq!(read.documents(), &[document("doc-1"), document("doc-2")]);
}

#[test]
fn staged_pass_receipts_and_visibility_states_are_observed_through_accessors() {
    // The staged receipt observes only counts and identities: an empty
    // receipt reports every action list empty.
    let staged = LifecyclePassStaged::default();
    assert!(staged.deleted().is_empty());
    assert!(staged.refreshed().is_empty());
    assert!(staged.scheduled().is_empty());
    assert!(staged.archived().is_empty());
    assert!(staged.cleared_stale().is_empty());
    assert!(staged.skipped().is_empty());

    // The three visibility states are one closed vocabulary.
    let states = [VisibilityState::Visible, VisibilityState::Expired, VisibilityState::Missing];
    assert_eq!(states.len(), 3);
    assert_ne!(VisibilityState::Visible, VisibilityState::Expired);
    assert_ne!(VisibilityState::Expired, VisibilityState::Missing);
}
