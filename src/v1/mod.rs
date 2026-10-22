//! TerraBase's public v1 project and platform HTTP APIs.
//!
//! Project clients use ts-auth bearer access tokens and verified Atlas routing.
//! An explicit endpoint bypasses Atlas. Entity IDs are canonical uppercase
//! ULIDs; database and collection selectors are names. Document keys may be
//! any valid raw key of 1–256 UTF-16 units without control characters.
//!
//! Bodies are generic over `Serialize`/`DeserializeOwned`. The default body
//! type is [`RawValue`](serde_json::value::RawValue), retaining exact JSON text;
//! [`Number`] preserves decimal text and wire quantities retain full precision.
//! Mutations are not automatically retried after an uncertain outcome.
//!
//! ```no_run
//! # async fn demo() -> terrascale::v1::Result<()> {
//! use terrascale::v1::{Client, WriteOptions};
//! let client = Client::for_project("01ARZ3NDEKTSV4RRFFQ69G5FAV", "access-token").build()?;
//! let users = client.database("main").collection("users");
//! let key = "ana@example.com";
//! let w = users.put(key, &serde_json::json!({"email": key}),
//!     WriteOptions::new().if_none_match_any()).await?;
//! let doc = users.get::<serde_json::Value>(key).await?;
//! # let _ = (w, doc); Ok(()) }
//! ```

mod atlas;
mod backups;
mod changes;
mod client;
mod documents;
mod errors;
mod ids;
mod imports;
mod management;
mod query;
mod reporting;
mod shape_crypto;
mod shapes;
mod sql;
mod system;
mod tooling;
mod tx;
mod types;

pub use backups::RestoredDatabase;
pub use changes::{ChangeBytes, ChangesOptions};
pub use client::{
    Client, ClientBuilder, RetryPolicy, DEFAULT_MAX_RESPONSE_BYTES, MAX_REQUEST_BYTES,
};
pub use documents::{
    Collection, Database, Document, DocumentState, Event, Patch, PatchOp, Precondition,
    ProjectCollectionInfo, ProjectCollectionPage, ProjectDatabaseInfo, Sibling, WriteOptions,
    WriteResult,
};
pub use errors::{Error, Result};
pub use ids::{is_valid_name, uuidv5_for, DocumentId, Version};
pub use management::{DatabasePlacement, ManagementPage};
pub use query::{Count, Explain, Filter, Order, Pages, Path, Query, QueryPage, Scalar, SortKey};
pub use shapes::{Shape, ShapeChange, ShapeDocument, ShapeOp, ShapeState, ShapeUpdate};
pub use sql::{SqlCheck, SqlIndexSuggestion, SqlResult, SqlRow};
pub use system::HealthResponse;
pub use tx::{CountBounds, OpResult, Tx, TxResult, MAX_ASSERT_COUNT_OPS, MAX_TX_OPS};
pub use types::{opt_u64_string, u64_string, Durability, Number};
