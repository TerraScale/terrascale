//! Packaged server scenarios required on the release-critical path.

#[path = "../../server_process.rs"]
mod server_process;

#[path = "../../client_process.rs"]
mod client_process;

#[path = "../../replication_process.rs"]
mod replication_process;

#[path = "../../distributed_client_process.rs"]
mod distributed_client_process;
