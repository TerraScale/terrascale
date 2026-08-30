//! Public-path result-statistics tests.
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so execution-derived statistics are covered by the
//! in-crate tests. What the public surface pins here is the count-cap
//! decision: a page limit above [`MAX_PAGE_LIMIT`] refuses typed at
//! construction — V2 never answers a query whose paging bound exceeds its
//! cap, so no result ever carries a truncated count in place of a refusal.

use std::time::Duration;

use terrabase::{
    Cancellation, Deadline, RequestValidationError,
    query::{MAX_PAGE_LIMIT, QueryPaging},
};

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

#[test]
fn page_limit_at_the_cap_is_valid_and_above_it_refuses() {
    assert_eq!(
        QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT)).unwrap().limit(),
        Some(MAX_PAGE_LIMIT)
    );
    assert_eq!(
        QueryPaging::try_new(0, Some(MAX_PAGE_LIMIT + 1)).unwrap_err(),
        RequestValidationError::InvalidQuery,
        "a limit above the page cap must refuse at construction, never truncate an answer"
    );
    assert!(!deadline().is_expired());
    assert!(!Cancellation::new().is_cancelled());
}
