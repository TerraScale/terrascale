//! The name-based TerraBase v1 client.
//!
//! Project clients implement C# TerraBase's `/v1/projects/{project}` document
//! contract with bearer credentials and verified Atlas session routing.
//! `Client::for_project` selects it; an explicit endpoint bypasses Atlas.
//!
//! Legacy clients implement the frozen v1 contracts (terrabase `docs/api-v1.md`,
//! `query-v1.md` and `control-plane-v1.md`): documents, transactions, queries
//! and the `/v1/dashboard` control plane, over HTTPS with a `tbk1` key.
//!
//! Numbers are lossless without serde_json's `arbitrary_precision` feature:
//! document bodies are generic over `Serialize`/`DeserializeOwned`, the
//! default body type is [`RawValue`](serde_json::value::RawValue) (exact JSON
//! text), and [`Number`] keeps exact decimal text. `u64` wire quantities are
//! decimal strings decoded into `u64`.
//!
//! ```no_run
//! # async fn demo() -> terrascale::v1::Result<()> {
//! use terrascale::v1::{Client, WriteOptions, uuidv5_for};
//! let client = Client::builder("https://gru.terrabase.example", "tbk1.…")
//!     .build()?;
//! let users = client.database("main").collection("users");
//! let id = uuidv5_for("users", "ana@example.com");
//! let w = users
//!     .put(id, &serde_json::json!({"email": "ana@example.com"}), WriteOptions::new().if_none_match_any())
//!     .await?;
//! let doc = users.get::<serde_json::Value>(id).await?;
//! # let _ = (w, doc); Ok(()) }
//! ```

mod atlas;
mod client;
mod control;
mod documents;
mod errors;
mod ids;
mod query;
mod tx;
mod types;

pub use client::{
    Client, ClientBuilder, RetryPolicy, DEFAULT_MAX_RESPONSE_BYTES, MAX_REQUEST_BYTES,
};
pub use control::{
    CollectionInfo, ControlClient, CreateCollection, CreateDatabase, CreateKey, CreateTenant,
    DatabaseInfo, Element, Field, Grant, IndexKey, IndexSpec, Key, KeyRate, KeyWithToken,
    ListOptions, Operation, OperationFailure, Page, Region, Retention, Schema, Tenant,
    TenantLimits, UniqueConstraint, UpdateCollection, UpdateTenant,
};
pub use documents::{
    Collection, Database, Document, DocumentState, Event, Patch, PatchOp, Precondition,
    ProjectCollectionInfo, ProjectCollectionPage, ProjectDatabaseInfo, Sibling, WriteOptions,
    WriteResult,
};
pub use errors::{Error, Result};
pub use ids::{is_valid_name, uuidv5_for, DocumentId, IdempotencyKey, Version};
pub use query::{Count, Explain, Filter, Order, Pages, Path, Query, QueryPage, Scalar, SortKey};
pub use tx::{CountBounds, OpResult, Tx, TxResult, MAX_ASSERT_COUNT_OPS, MAX_TX_OPS};
pub use types::{opt_u64_string, u64_string, Durability, Number};
