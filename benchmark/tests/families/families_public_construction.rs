//! Public-construction tests for the typed family payloads.
//!
//! These tests reach only what an external caller can construct: read
//! payloads, the closed request wrappers, and the closed family kinds. Write
//! payloads carry a validated `TenantScope`, which only Platform creates, so
//! their construction and staging behavior is covered by the in-crate tests.

use std::time::Duration;

use terrabase_v2::{
    Cancellation, Deadline, DocumentId, FamilyKind, FamilyReadRequest, RequestValidationError,
    families::{
        ArchivalReadRequest, AttachmentRead, CompareExchangeRead, CounterRead, IdentityRead,
        LifecycleRead, RevisionPaging, RevisionRead, SchemaDefinition, SchemaFieldType, SchemaRule,
        SchemaVersion, TimeSeriesRange, TimeSeriesRead, TombstoneRead, ValidationRead,
    },
    types::CollectionName,
};

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn document(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

#[test]
fn read_payloads_validate_typed_fields_before_observation() {
    assert_eq!(TimeSeriesRange::try_new(200, 100), Err(RequestValidationError::InvalidRange));
    assert_eq!(RevisionPaging::try_new(0, 0), Err(RequestValidationError::InvalidRange));
    assert_eq!(
        CounterRead::try_new(
            document("doc-1"),
            vec![String::new()],
            deadline(),
            Cancellation::new(),
        ),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        IdentityRead::try_new(vec![], deadline(), Cancellation::new()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        CompareExchangeRead::try_new(" ".to_owned(), deadline(), Cancellation::new()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(
        SchemaDefinition::try_new(
            SchemaVersion::try_new(1).expect("valid version"),
            vec![SchemaRule::Required { field: String::new() }],
        ),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert_eq!(SchemaVersion::try_new(0), Err(RequestValidationError::InvalidFamilyPayload));
    assert_eq!(SchemaVersion::try_new(1).expect("valid version").get(), 1);

    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        AttachmentRead::try_new(document("doc-1"), deadline(), cancelled),
        Err(RequestValidationError::InvalidCancellation)
    );

    let elapsed = Deadline::after(Duration::from_nanos(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(5));
    assert_eq!(
        TombstoneRead::try_new(document("doc-1"), elapsed, Cancellation::new()),
        Err(RequestValidationError::InvalidDeadline)
    );
}

#[test]
fn read_payloads_expose_validated_fields_and_deadline() {
    let range = TimeSeriesRange::try_new(100, 200).expect("valid range");
    assert_eq!((range.start(), range.end()), (100, 200));
    let read = TimeSeriesRead::try_new(
        document("doc-1"),
        "cpu".to_owned(),
        Some(range),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid time-series read");
    assert_eq!(read.document().as_str(), "doc-1");
    assert_eq!(read.name(), "cpu");
    assert_eq!(read.range().map(|value| (value.start(), value.end())), Some((100, 200)));
    assert!(!read.deadline().is_expired());
    assert!(!read.cancellation().is_cancelled());

    let validation = ValidationRead::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        Some(document("doc-1")),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid validation read");
    assert_eq!(validation.collection().as_str(), "users");
    assert_eq!(validation.document().map(|value| value.as_str()), Some("doc-1"));
}

#[test]
fn read_request_wrappers_are_named_and_closed() {
    let read = FamilyReadRequest::counter_group(
        CounterRead::try_new(document("doc-1"), vec![], deadline(), Cancellation::new())
            .expect("valid counter read"),
    );
    assert!(matches!(read, FamilyReadRequest::CounterGroup(_)));
    assert!(!read.deadline().is_expired());
    assert!(!read.cancellation().is_cancelled());

    let wrapped = [
        FamilyReadRequest::attachment_metadata(
            AttachmentRead::try_new(document("doc-1"), deadline(), Cancellation::new())
                .expect("valid attachment read"),
        ),
        FamilyReadRequest::time_series_range(
            TimeSeriesRead::try_new(
                document("doc-1"),
                "cpu".to_owned(),
                None,
                deadline(),
                Cancellation::new(),
            )
            .expect("valid time-series read"),
        ),
        FamilyReadRequest::revision_page(
            RevisionRead::try_new(
                document("doc-1"),
                RevisionPaging::try_new(0, 10).expect("valid paging"),
                deadline(),
                Cancellation::new(),
            )
            .expect("valid revision read"),
        ),
        FamilyReadRequest::compare_exchange_value(
            CompareExchangeRead::try_new("leader".to_owned(), deadline(), Cancellation::new())
                .expect("valid compare-exchange read"),
        ),
        FamilyReadRequest::identity_range(
            IdentityRead::try_new(vec!["orders".to_owned()], deadline(), Cancellation::new())
                .expect("valid identity read"),
        ),
        FamilyReadRequest::lifecycle(
            LifecycleRead::try_new(document("doc-1"), deadline(), Cancellation::new())
                .expect("valid lifecycle read"),
        ),
        FamilyReadRequest::archival(
            ArchivalReadRequest::try_new(document("doc-1"), deadline(), Cancellation::new())
                .expect("valid archival read"),
        ),
        FamilyReadRequest::validation(
            ValidationRead::try_new(
                CollectionName::try_new("users").expect("valid identifier"),
                None,
                deadline(),
                Cancellation::new(),
            )
            .expect("valid validation read"),
        ),
        FamilyReadRequest::tombstones(
            TombstoneRead::try_new(document("doc-1"), deadline(), Cancellation::new())
                .expect("valid tombstone read"),
        ),
    ];
    assert_eq!(wrapped.len(), 9);
    assert!(wrapped.iter().all(|request| !request.deadline().is_expired()));
}

#[test]
fn family_kinds_stay_closed_and_named() {
    let kinds = [
        FamilyKind::Attachments,
        FamilyKind::Counters,
        FamilyKind::TimeSeries,
        FamilyKind::Revisions,
        FamilyKind::CompareExchange,
        FamilyKind::Identities,
        FamilyKind::ExpirationRefresh,
        FamilyKind::Archival,
        FamilyKind::SchemaValidation,
        FamilyKind::ConflictTombstones,
    ];
    assert_eq!(kinds.len(), 10);
    assert_eq!(FamilyKind::Attachments.record_key(), "attachments");
    assert_eq!(FamilyKind::ConflictTombstones.record_key(), "tombstones");
    assert_ne!(FamilyKind::Counters, FamilyKind::TimeSeries);
}

#[test]
fn schema_rules_report_their_ruled_field_in_canonical_order() {
    let schema = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![
            SchemaRule::Required { field: "email".to_owned() },
            SchemaRule::FieldType { field: "age".to_owned(), kind: SchemaFieldType::Integer },
        ],
    )
    .expect("valid schema");
    assert_eq!(schema.version().get(), 1);
    let rules = schema.rules();
    // Rules are canonical: ordered by field name, then by rule kind, so a
    // declaration order of (email, age) is stored as (age, email).
    assert_eq!(rules[0].field(), "age");
    assert_eq!(rules[1].field(), "email");
}
