// Storage behavior is exercised through the public crate API. These modules
// preserve the standalone integration fixtures under the Slow suite.
#[path = "document_mode_contract.rs"]
mod document_mode_contract;
#[path = "floor_initialization_contract.rs"]
mod floor_initialization_contract;
#[path = "local_document_mode_contract.rs"]
mod local_document_mode_contract;
#[path = "post_sync_apply_contract.rs"]
mod post_sync_apply_contract;
#[path = "prepared_batch_contract.rs"]
mod prepared_batch_contract;
#[path = "retained_lookup_contract.rs"]
mod retained_lookup_contract;
#[path = "retry_deadline_contract.rs"]
mod retry_deadline_contract;
#[path = "scan_clone_contract.rs"]
mod scan_clone_contract;
#[path = "scan_contract.rs"]
mod scan_contract;
#[path = "staged_batch_contract.rs"]
mod staged_batch_contract;
#[path = "storage_contract.rs"]
mod storage_contract;
