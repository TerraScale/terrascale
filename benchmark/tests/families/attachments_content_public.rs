//! Public-construction tests for the attachment content plane (GAP-F01).
//!
//! These tests reach only what an external caller can construct: the
//! bounded segment payload, the read payload with its typed ranges, and the
//! plane's explicit limits and typed errors. Write and delete payloads carry
//! a validated `TenantScope`, which only Platform creates, so their staging
//! behavior is covered by the in-crate tests.

use std::time::Duration;

use terrabase_v2::{
    Cancellation, Deadline, RequestValidationError,
    families::attachments::{
        AttachmentContentRead, AttachmentRange, AttachmentSegment, MAX_CONTENT_BYTES,
        MAX_SEGMENT_BYTES,
    },
};

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn valid_digest() -> String {
    // The hex SHA-256 of the empty message: a digest shape the plane accepts.
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855".to_owned()
}

#[test]
fn segments_are_bounded_at_construction() {
    let exact = vec![0_u8; MAX_SEGMENT_BYTES];
    assert!(AttachmentSegment::try_new(&exact).is_ok(), "the cap itself is legal");
    let oversized = vec![0_u8; MAX_SEGMENT_BYTES + 1];
    assert_eq!(
        AttachmentSegment::try_new(&oversized).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );
    assert!(AttachmentSegment::try_new(&[]).is_ok(), "empty segments are structurally legal");
}

#[test]
fn content_reads_validate_digest_and_range_shapes() {
    assert!(
        AttachmentContentRead::try_new(valid_digest(), None, deadline(), Cancellation::new(),)
            .is_ok()
    );

    let short = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85".to_owned();
    assert_eq!(
        AttachmentContentRead::try_new(short, None, deadline(), Cancellation::new()).map(|_| ()),
        Err(RequestValidationError::InvalidFamilyPayload)
    );

    let inverted = AttachmentRange::Bytes { from: 100, to: Some(40) };
    assert_eq!(
        AttachmentContentRead::try_new(
            valid_digest(),
            Some(inverted),
            deadline(),
            Cancellation::new()
        )
        .map(|_| ()),
        Err(RequestValidationError::InvalidRange)
    );

    let ordered = AttachmentRange::Bytes { from: 40, to: Some(100) };
    let suffix = AttachmentRange::Suffix { bytes: 10 };
    for range in [Some(ordered), Some(suffix)] {
        assert!(
            AttachmentContentRead::try_new(valid_digest(), range, deadline(), Cancellation::new())
                .is_ok(),
            "well-formed ranges construct"
        );
    }
}

#[test]
fn the_plane_publishes_its_explicit_limits() {
    // The limits are contract, not configuration: an external caller can
    // reason about over-limit requests before making them.
    assert_eq!(MAX_SEGMENT_BYTES, 64 * 1024);
    assert_eq!(MAX_CONTENT_BYTES, 8 * 1024 * 1024);
}
