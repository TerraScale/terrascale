//! TerraBase's public v1 project and platform HTTP APIs.
//!
//! Project clients use ts-auth bearer access tokens against [`DEFAULT_BASE_URL`].
//! An explicit endpoint overrides it. Entity IDs are canonical uppercase
//! ULIDs; database and collection selectors are names. Document IDs are
//! [`DocumentId`] ULIDs; [`ulid_for`] derives a stable one from a natural key.
//!
//! Bodies are generic over `Serialize`/`DeserializeOwned`. The default body
//! type is [`RawValue`](serde_json::value::RawValue), retaining exact JSON text;
//! [`Number`] preserves decimal text and wire quantities retain full precision.
//! Mutations are not automatically retried after an uncertain outcome.
//!
//! ```no_run
//! # async fn demo() -> terrascale::v1::Result<()> {
//! use terrascale::v1::{ulid_for, Client, WriteOptions};
//! let client = Client::for_project("01ARZ3NDEKTSV4RRFFQ69G5FAV", "access-token").build()?;
//! let users = client.database("main").collection("users");
//! let email = "ana@example.com";
//! let key = ulid_for("users", email);
//! let w = users.put(key, &serde_json::json!({"email": email}),
//!     WriteOptions::new().if_none_match_any()).await?;
//! let doc = users.get::<serde_json::Value>(key).await?;
//! # let _ = (w, doc); Ok(()) }
//! ```

mod auth;
mod backups;
mod changes;
mod client;
mod documents;
mod errors;
mod functions;
mod ids;
mod imports;
mod management;
mod query;
mod reporting;
mod rules;
mod shape_crypto;
mod shapes;
mod sql;
mod sql_exec;
mod system;
mod tooling;
mod tx;
mod types;
mod webhooks;

pub use backups::RestoredDatabase;
pub use changes::{
    ChangeBytes, ChangeEvent, ChangeEvents, ChangeOp, ChangesOptions, DocumentChange,
};
pub use client::{
    Client, ClientBuilder, RetryPolicy, DEFAULT_BASE_URL, DEFAULT_MAX_RESPONSE_BYTES,
    MAX_REQUEST_BYTES,
};
pub use documents::{
    Collection, Database, Document, DocumentState, Event, Patch, PatchOp, Precondition,
    ProjectCollectionInfo, ProjectCollectionPage, ProjectDatabaseInfo, Sibling, WriteOptions,
    WriteResult,
};
pub use errors::{Error, Result, SqlError};
pub use functions::{
    Function, FunctionDeployment, FunctionInvocation, FunctionLog, FunctionTrigger,
};
pub use ids::{is_valid_name, ulid_for, DocumentId, Version};
pub use management::{DatabasePlacement, ManagementPage};
pub use query::{
    Aggregate, Area, Count, Filter, GeoNear, Group, Metric, Near, Order, Pages, Path, Query,
    QueryPage, Scalar, SortKey,
};
pub use rules::{CollectionRules, ReadWriteGrants};
pub use shapes::{Shape, ShapeChange, ShapeDocument, ShapeOp, ShapeState, ShapeUpdate};
pub use sql::{SqlCheck, SqlIndexSuggestion, SqlResult, SqlRow};
pub use sql_exec::{
    Sql, SqlColumn, SqlStatement, SqlStatementResult, SqlValue, MAX_SQL_BYTES, MAX_SQL_STATEMENTS,
};
pub use system::HealthResponse;
pub use tooling::{Index, IndexKind, IndexRequest};
pub use tx::{CountBounds, OpResult, Tx, TxResult, MAX_ASSERT_COUNT_OPS, MAX_TX_OPS};
pub use types::{opt_u64_string, u64_string, Durability, Number};
pub use webhooks::{
    CreatedWebhook, Webhook, WebhookDelivery, WebhookDeliveryError, WebhookDeliveryStatus,
    WebhookStatus,
};
