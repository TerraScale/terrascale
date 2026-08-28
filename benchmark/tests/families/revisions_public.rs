//! Public-construction tests for the revisions completeness layer (GAP-F05).
//!
//! These tests reach only what an external caller can construct: the
//! collection and database configuration policies, the read payloads, the
//! recorded limits, and the typed error vocabulary. Write payloads carry a
//! validated `TenantScope`, which only Platform creates, so their staging
//! behavior is covered by the in-crate tests.

use std::time::Duration;

use terrabase_v2::{
    Cancellation, Deadline, DocumentId, RequestValidationError,
    families::RevisionPaging,
    families::revisions::{
        CollectionRevisionsConfiguration, DEFAULT_BIN_MINIMUM_AGE_MS, MAX_BIN_BATCH_DOCUMENTS,
        RevisionSnapshotsRead, RevisionsBinRead, RevisionsConfigurationRead,
        RevisionsDatabaseConfiguration, RevisionsError,
    },
};

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn collection_policy() -> CollectionRevisionsConfiguration {
    CollectionRevisionsConfiguration::try_new(Some(5), Some(60_000), Some(64), true, false)
        .expect("valid configuration")
}

#[test]
fn collection_policies_validate_their_v1_field_shapes() {
    assert_eq!(
        CollectionRevisionsConfiguration::try_new(Some(0), None, None, false, false).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CollectionRevisionsConfiguration::try_new(Some(5), Some(-1), None, false, false)
            .map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CollectionRevisionsConfiguration::try_new(Some(5), None, Some(0), false, false).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    let policy = collection_policy();
    assert_eq!(policy.minimum_revisions_to_keep(), Some(5));
    assert_eq!(policy.minimum_revision_age_to_keep_ms(), Some(60_000));
    assert_eq!(policy.maximum_revisions_to_delete_upon_update(), Some(64));
    assert!(policy.purge_on_delete());
    assert!(!policy.is_disabled());
    let disabled = CollectionRevisionsConfiguration::disabled();
    assert!(disabled.is_disabled());
    assert_eq!(disabled.minimum_revisions_to_keep(), None);
}

#[test]
fn database_policies_resolve_collection_then_default_then_disabled() {
    let per_collection =
        CollectionRevisionsConfiguration::try_new(Some(3), None, None, false, false)
            .expect("valid per-collection policy");
    let users = terrabase_v2::types::CollectionName::try_new("users").expect("valid collection");
    let configuration = RevisionsDatabaseConfiguration::try_new(
        Some(collection_policy()),
        vec![(users.clone(), per_collection)],
        DEFAULT_BIN_MINIMUM_AGE_MS,
    )
    .expect("valid configuration");
    assert_eq!(
        configuration.effective(&users).expect("resolves").minimum_revisions_to_keep(),
        Some(3)
    );
    let orders = terrabase_v2::types::CollectionName::try_new("orders").expect("valid collection");
    assert_eq!(
        configuration.effective(&orders).expect("resolves default").minimum_revisions_to_keep(),
        Some(5)
    );
    assert!(RevisionsDatabaseConfiguration::absent().effective(&orders).is_none());
    assert_eq!(
        RevisionsDatabaseConfiguration::try_new(None, vec![], -1).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(configuration.bin_minimum_age_ms(), DEFAULT_BIN_MINIMUM_AGE_MS);
}

#[test]
fn read_payloads_validate_their_bounds_before_observation() {
    let document = DocumentId::try_new("doc-1").expect("valid identifier");
    let paging = RevisionPaging::try_new(0, 10).expect("valid paging");
    assert!(
        RevisionSnapshotsRead::try_new(
            document.clone(),
            None,
            paging,
            deadline(),
            Cancellation::new()
        )
        .is_ok()
    );

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        RevisionSnapshotsRead::try_new(document.clone(), None, paging, deadline(), cancelled)
            .map(|_| ()),
        Err(RequestValidationError::InvalidCancellation)
    );

    assert_eq!(
        RevisionsBinRead::try_new(1_000, 0, deadline(), Cancellation::new()).map(|_| ()),
        Err(RequestValidationError::InvalidRange)
    );
    assert_eq!(
        RevisionsBinRead::try_new(
            1_000,
            u32::try_from(MAX_BIN_BATCH_DOCUMENTS + 1).expect("fits"),
            deadline(),
            Cancellation::new()
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidRange)
    );
    assert_eq!(
        RevisionsBinRead::try_new(-1, 10, deadline(), Cancellation::new()).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert!(
        RevisionsConfigurationRead::try_new(deadline(), Cancellation::new()).is_ok(),
        "a configuration read constructs"
    );
}

#[test]
fn the_revisions_layer_publishes_its_v1_derived_limits() {
    // The limits are contract, not configuration: V1's 30-day bin age and
    // 10*1024 cleaner batch bound.
    assert_eq!(DEFAULT_BIN_MINIMUM_AGE_MS, 30 * 24 * 60 * 60 * 1000);
    assert_eq!(MAX_BIN_BATCH_DOCUMENTS, 10 * 1024);
    // The typed error vocabulary is closed and distinct: the disable case
    // and the reference case never collapse into one generic failure.
    assert_ne!(RevisionsError::RevisionsDisabled, RevisionsError::ReferenceInvalid);
    assert_ne!(RevisionsError::BinEntryNotEligible, RevisionsError::StorageFailure);
}
