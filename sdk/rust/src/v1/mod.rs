//! TerraBase's public v1 project and platform HTTP APIs.
//!
//! Project clients use ts-auth bearer access tokens or API keys against
//! [`DEFAULT_BASE_URL`]. An explicit endpoint overrides it. Entity IDs are
//! canonical uppercase ULIDs; database selectors are names or ULIDs.
//! Mutations are not automatically retried after an uncertain outcome.
//!
//! ```no_run
//! # async fn demo() -> terrascale::v1::Result<()> {
//! use terrascale::v1::Client;
//! let client = Client::for_project("01ARZ3NDEKTSV4RRFFQ69G5FAV", "access-token").build()?;
//! let sql = client.database("main").sql();
//! let rows = sql
//!     .query("SELECT id, email FROM users WHERE email = ?", &["ana@example.com".into()])
//!     .await?;
//! # let _ = rows; Ok(()) }
//! ```

mod auth;
mod backups;
mod buckets;
mod client;
mod database;
mod errors;
mod file_transfer;
mod files;
mod functions;
mod ids;
mod management;
mod observability;
mod reporting;
mod request_logs;
mod sql_exec;
mod sql_session;
mod system;
mod types;

pub use backups::RestoredDatabase;
pub use buckets::{BucketRules, FileBucket, FileBucketPage, ReadWriteGrants};
pub use client::{
    Client, ClientBuilder, RetryPolicy, DEFAULT_BASE_URL, DEFAULT_MAX_RESPONSE_BYTES,
    MAX_REQUEST_BYTES,
};
pub use database::{Database, ProjectDatabaseInfo};
pub use errors::{Error, Result, SqlError};
pub use files::{FileObject, FileObjectPage, FileSignedUrl, FileUploadRequest};
pub use functions::{Function, FunctionDeployment, FunctionInvocation, FunctionLog};
pub use ids::{is_valid_name, new_id};
pub use management::{DatabasePlacement, ManagementPage};
pub use observability::ServingRegions;
pub use request_logs::{DatabaseRequestLog, DatabaseRequestLogs};
pub use sql_exec::{
    sql_error_codes, Sql, SqlColumn, SqlColumnIndex, SqlRecord, SqlStatement, SqlStatementResult,
    SqlValue, MAX_SQL_BYTES, MAX_SQL_STATEMENTS,
};
pub use sql_session::{SqlSession, MAX_SQL_FRAME_BYTES, SQL_SUBPROTOCOL};
pub use system::HealthResponse;
pub use types::{opt_u64_string, u64_string, Number};
