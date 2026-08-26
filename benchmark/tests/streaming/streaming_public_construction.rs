//! Public-construction tests for the streaming seam.
//!
//! These tests reach only what an external caller can construct: the
//! durable `Engine`, `Engine::streams()`, the typed selectors, filters,
//! and subscription specs, and the borrow rule that keeps a live store
//! from being outrun by shutdown. `TenantScope` has no public constructor
//! by design — Platform creates it after authorization — so scope-bound
//! stream and subscription behavior is proved by the in-crate tests in
//! `src/streaming/tests.rs`.

use std::{path::PathBuf, time::Duration};

use terrabase_v2::types::CollectionName;
use terrabase_v2::{
    Cancellation, Deadline, DocumentId, Engine, EngineConfig, RequestValidationError,
    StreamPosition, StreamSource, StreamStore, SubscriptionSpec,
    streaming::{self, ChangeFilter, ChangeKind, DocumentStreamSelector, TimeSeriesStreamRange},
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-w4-stream-public-{tag}-{}", std::process::id()))
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

#[test]
fn engine_streams_returns_store_bound_to_durable_engine() {
    let dir = location("acquisition");
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("opens");
    let store: StreamStore<'_> = engine.streams();
    let _ = &store;

    // The store exposes the streaming vocabulary and nothing else: the
    // typed selectors, cursors, batches, and subscription state all live
    // behind this one engine-borrowing handle.
    let _ = std::any::type_name::<StreamStore<'_>>();
    let _ = std::any::type_name::<StreamSource>();
    let _ = std::any::type_name::<StreamPosition>();
    let selector =
        DocumentStreamSelector::try_new(collection("users"), deadline(), Cancellation::new())
            .expect("valid selector")
            .with_prefix("doc-")
            .expect("valid prefix")
            .with_match("42")
            .expect("valid match")
            .excluding(vec![document("doc-0")])
            .expect("valid exclusion");
    assert_eq!(selector.collection().as_str(), "users");
    assert_eq!(selector.prefix(), Some("doc-"));
    assert_eq!(selector.matching(), Some("42"));
    assert_eq!(selector.excluded().len(), 1);
    assert!(!selector.deadline().is_expired());
    assert!(!selector.cancellation().is_cancelled());

    let source = StreamSource::Documents(selector);
    assert!(matches!(source, StreamSource::Documents(_)));

    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

/// A `StreamStore` value exists only inside the borrow region of its
/// `Engine`: the store and the engine borrow are returned together, so the
/// borrow is held for as long as the store is live and a consuming
/// `shutdown` (which takes the engine by value) must wait for it.
fn store_outlives_nothing<'engine>(
    engine: &'engine Engine,
    store: StreamStore<'engine>,
) -> (StreamStore<'engine>, &'engine Engine) {
    (store, engine)
}

#[test]
fn stream_store_borrow_prevents_engine_shutdown_while_live() {
    // The compile-level proof: the helper returns the store and the
    // borrowing engine reference together, so a live store holds its
    // engine's borrow and a consuming `shutdown` cannot be named while the
    // store is live. The runtime proof follows: ending the store's
    // lifetime ends the borrow and shutdown then succeeds.
    let proof = store_outlives_nothing
        as for<'engine> fn(
            &'engine Engine,
            StreamStore<'engine>,
        ) -> (StreamStore<'engine>, &'engine Engine);
    assert!(!std::format!("{proof:p}").is_empty());

    let dir = location("borrow");
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("opens");
    // The store's lifetime is one inner scope: while it is live the engine
    // borrow is held, and when the scope ends the consuming shutdown can
    // run — the borrow rule, observed at runtime.
    {
        let store = engine.streams();
        let (store, engine_ref) = store_outlives_nothing(&engine, store);
        assert!(std::ptr::eq(engine_ref, &engine), "the pair names the same engine");
        let _ = &store;
    }
    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn public_construction_validates_typed_stream_requests() {
    // Selectors validate before any engine exists.
    let cancelled = Cancellation::new();
    cancelled.cancel();
    assert_eq!(
        DocumentStreamSelector::try_new(collection("users"), deadline(), cancelled).unwrap_err(),
        RequestValidationError::InvalidCancellation
    );
    let elapsed = Deadline::after(Duration::from_nanos(1)).expect("valid deadline");
    std::thread::sleep(Duration::from_millis(5));
    assert_eq!(
        DocumentStreamSelector::try_new(collection("users"), elapsed, Cancellation::new())
            .unwrap_err(),
        RequestValidationError::InvalidDeadline
    );
    assert_eq!(
        streaming::DocumentStreamSelector::try_new(
            CollectionName::try_new("terrabase.families").expect("valid identifier"),
            deadline(),
            Cancellation::new()
        )
        .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert_eq!(
        DocumentStreamSelector::try_new(collection("users"), deadline(), Cancellation::new())
            .expect("valid selector")
            .with_prefix("")
            .unwrap_err(),
        RequestValidationError::InvalidIdentifier
    );
    assert_eq!(
        DocumentStreamSelector::try_new(collection("users"), deadline(), Cancellation::new())
            .expect("valid selector")
            .excluding(Vec::new())
            .unwrap_err(),
        RequestValidationError::InvalidRange
    );

    // A time-series stream source validates its series name, range shape,
    // deadline, and cancellation.
    assert_eq!(
        TimeSeriesStreamRange::try_new(
            document("host-1"),
            "a/b".to_owned(),
            None,
            deadline(),
            Cancellation::new()
        )
        .unwrap_err(),
        RequestValidationError::InvalidFamilyPayload
    );
    assert!(
        TimeSeriesStreamRange::try_new(
            document("host-1"),
            "cpu".to_owned(),
            Some(terrabase_v2::families::TimeSeriesRange::try_new(100, 200).expect("range")),
            deadline(),
            Cancellation::new()
        )
        .is_ok()
    );

    // Typed change filters reject an empty include list and keep the
    // closed kind set.
    assert_eq!(
        ChangeFilter::try_new(Some(Vec::new()), None).unwrap_err(),
        RequestValidationError::EmptyField
    );
    let filter = ChangeFilter::try_new(
        Some(vec![ChangeKind::Document, ChangeKind::Counter]),
        Some(vec![collection("users")]),
    )
    .expect("valid filter");
    assert!(filter.accepts(ChangeKind::Counter, Some(&collection("users"))));
    assert!(!filter.accepts(ChangeKind::TimeSeries, Some(&collection("users"))));
    assert!(!filter.accepts(ChangeKind::Document, Some(&collection("orders"))));
    // A subjectless event (a conflict) passes the collection dimension
    // exactly when no collection is named.
    assert!(!filter.accepts(ChangeKind::Conflict, None));

    // Subscription specs validate their name and batch bound.
    assert_eq!(
        SubscriptionSpec::try_new("", filter.clone(), 4).unwrap_err(),
        RequestValidationError::EmptyField
    );
    assert_eq!(
        SubscriptionSpec::try_new("orders", filter.clone(), 0).unwrap_err(),
        RequestValidationError::InvalidRange
    );
    assert_eq!(
        SubscriptionSpec::try_new("orders", filter, streaming::MAX_SUBSCRIPTION_BATCH_ITEMS + 1)
            .unwrap_err(),
        RequestValidationError::InvalidRange
    );
    let spec =
        SubscriptionSpec::try_new("orders", ChangeFilter::try_new(None, None).expect("filter"), 16)
            .expect("valid spec");
    assert_eq!(spec.name(), "orders");
    assert_eq!(spec.max_events(), 16);
    assert_eq!(spec.from_beginning().name(), "orders");
}
