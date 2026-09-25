//! Broad integration scenarios that run independently from Fast.

#[path = "support/document/receiver_fixture.rs"]
mod receiver_fixture;

#[path = "support/server/fixture.rs"]
mod server_fixture;

#[path = "support/document/slow.rs"]
mod document_slow;

#[path = "support/storage/slow.rs"]
mod storage_slow;

#[path = "support/format/slow.rs"]
mod format_slow;

// This provider case is required: absent RustFS provisioning fails the test
// through the fixture's explicit environment checks; it is never skipped.
#[path = "support/object/slow.rs"]
mod object_slow;

#[path = "support/server/slow.rs"]
mod server_slow;
