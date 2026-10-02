//! Transactions (api-v1.md "Transactions").

use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::value::RawValue;

use super::documents::{encode_object, Patch, PatchOp, Precondition};
use super::errors::{Error, Result};
use super::ids::{check_name, DocumentId, IdempotencyKey, Version};
use super::query::Filter;
use super::types::{opt_u64_string, u64_string, Durability};

/// Maximum ops per transaction.
pub const MAX_TX_OPS: usize = 1024;
/// Maximum `assert_count` ops per transaction.
pub const MAX_ASSERT_COUNT_OPS: usize = 16;

#[derive(Debug, Clone, Serialize)]
struct TxOp {
    op: &'static str,
    collection: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    id: Option<DocumentId>,
    #[serde(skip_serializing_if = "Option::is_none")]
    document: Option<Box<RawValue>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    if_match: Option<Version>,
    #[serde(skip_serializing_if = "Option::is_none")]
    if_none_match: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    merge: Option<Box<RawValue>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    ops: Option<Vec<PatchOp>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    filter: Option<Filter>,
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    absent: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    gte: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    lte: Option<u64>,
}

impl TxOp {
    fn new(op: &'static str, collection: &str, id: Option<DocumentId>) -> Self {
        Self {
            op,
            collection: collection.to_owned(),
            id,
            document: None,
            if_match: None,
            if_none_match: None,
            merge: None,
            ops: None,
            filter: None,
            absent: false,
            gte: None,
            lte: None,
        }
    }
}

/// Bounds for [`Tx::assert_count`]; at least one is required.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CountBounds {
    pub gte: Option<u64>,
    pub lte: Option<u64>,
}

impl CountBounds {
    /// `count >= n`.
    #[must_use]
    pub fn at_least(n: u64) -> Self {
        Self {
            gte: Some(n),
            lte: None,
        }
    }
    /// `count <= n`.
    #[must_use]
    pub fn at_most(n: u64) -> Self {
        Self {
            gte: None,
            lte: Some(n),
        }
    }
    /// `lo <= count <= hi`.
    #[must_use]
    pub fn between(lo: u64, hi: u64) -> Self {
        Self {
            gte: Some(lo),
            lte: Some(hi),
        }
    }
}

/// An atomic transaction of up to 1024 ops across the collections of one
/// database, committed with [`Database::commit`](super::Database::commit).
///
/// Builder methods never fail; the first invalid op is reported, with its
/// index, by `commit`. A `Tx` can be committed again with the same
/// idempotency key to learn the outcome after an uncertain failure.
#[derive(Debug, Clone, Default)]
pub struct Tx {
    ops: Vec<TxOp>,
    error: Option<(usize, String)>,
    count_ops: usize,
    key: Option<IdempotencyKey>,
}

impl Tx {
    /// An empty transaction.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The number of ops.
    #[must_use]
    pub fn len(&self) -> usize {
        self.ops.len()
    }

    /// True when there are no ops.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.ops.is_empty()
    }

    /// Sets the transaction's `Idempotency-Key`.
    pub fn idempotency_key(&mut self, key: IdempotencyKey) -> &mut Self {
        self.key = Some(key);
        self
    }

    pub(crate) fn key(&self) -> Option<&IdempotencyKey> {
        self.key.as_ref()
    }

    fn fail(&mut self, msg: impl Into<String>) {
        if self.error.is_none() {
            self.error = Some((self.ops.len(), msg.into()));
        }
    }

    fn push(&mut self, op: TxOp) -> &mut Self {
        if let Err(e) = check_name("collection", &op.collection) {
            self.fail(e.to_string());
        }
        self.ops.push(op);
        self
    }

    fn document<T: Serialize + ?Sized>(&mut self, doc: &T) -> Option<Box<RawValue>> {
        match encode_object(doc) {
            Ok(raw) => Some(raw),
            Err(e) => {
                self.fail(e.to_string());
                None
            }
        }
    }

    /// `create`: a put that requires absence. `None` lets the server generate
    /// a UUIDv7, returned in the op's result.
    pub fn create<T: Serialize + ?Sized>(
        &mut self,
        collection: &str,
        id: Option<DocumentId>,
        doc: &T,
    ) -> &mut Self {
        let mut op = TxOp::new("create", collection, id);
        op.document = self.document(doc);
        self.push(op)
    }

    /// `put`: a full-document write with an optional precondition.
    pub fn put<T: Serialize + ?Sized>(
        &mut self,
        collection: &str,
        id: DocumentId,
        doc: &T,
        precondition: Option<Precondition>,
    ) -> &mut Self {
        let mut op = TxOp::new("put", collection, Some(id));
        op.document = self.document(doc);
        match precondition {
            Some(Precondition::IfMatch(v)) => op.if_match = Some(v),
            Some(Precondition::IfNoneMatchAny) => op.if_none_match = Some("*"),
            None => {}
        }
        self.push(op)
    }

    /// `patch` with a merge or ops patch and an optional `if_match`.
    pub fn patch(
        &mut self,
        collection: &str,
        id: DocumentId,
        patch: Patch,
        if_match: Option<Version>,
    ) -> &mut Self {
        if let Err(e) = patch.validate() {
            self.fail(e.to_string());
        }
        let mut op = TxOp::new("patch", collection, Some(id));
        match patch {
            Patch::Merge(raw) => op.merge = Some(raw),
            Patch::Ops(ops) => op.ops = Some(ops),
        }
        op.if_match = if_match;
        self.push(op)
    }

    /// `delete` with an optional `if_match`.
    pub fn delete(
        &mut self,
        collection: &str,
        id: DocumentId,
        if_match: Option<Version>,
    ) -> &mut Self {
        let mut op = TxOp::new("delete", collection, Some(id));
        op.if_match = if_match;
        self.push(op)
    }

    /// `assert` with a filter: the document must exist, be unconflicted and
    /// match, without being written.
    pub fn assert(&mut self, collection: &str, id: DocumentId, filter: Filter) -> &mut Self {
        if let Err(e) = filter.validate() {
            self.fail(e.to_string());
        }
        let mut op = TxOp::new("assert", collection, Some(id));
        op.filter = Some(filter);
        self.push(op)
    }

    /// `assert` with `absent:true`: the document must be absent, deleted or
    /// expired.
    pub fn assert_absent(&mut self, collection: &str, id: DocumentId) -> &mut Self {
        let mut op = TxOp::new("assert", collection, Some(id));
        op.absent = true;
        self.push(op)
    }

    /// `assert_count`: the number of documents in `collection` matching
    /// `filter` must satisfy `bounds`. Evaluated inside the commit before
    /// this transaction's writes, so it is serializable with every other write
    /// to the database. At most 16 per transaction.
    pub fn assert_count(
        &mut self,
        collection: &str,
        filter: Filter,
        bounds: CountBounds,
    ) -> &mut Self {
        if bounds.gte.is_none() && bounds.lte.is_none() {
            self.fail("assert_count needs gte or lte");
        }
        if let Err(e) = filter.validate() {
            self.fail(e.to_string());
        }
        self.count_ops += 1;
        if self.count_ops > MAX_ASSERT_COUNT_OPS {
            self.fail("at most 16 assert_count ops per transaction");
        }
        let mut op = TxOp::new("assert_count", collection, None);
        op.filter = Some(filter);
        op.gte = bounds.gte;
        op.lte = bounds.lte;
        self.push(op)
    }

    /// The request body.
    pub(crate) fn encode(&self) -> Result<Vec<u8>> {
        if let Some((op, message)) = &self.error {
            return Err(Error::InvalidTxOp {
                op: *op,
                message: message.clone(),
            });
        }
        if self.ops.is_empty() || self.ops.len() > MAX_TX_OPS {
            return Err(Error::InvalidInput("a transaction needs 1-1024 ops".into()));
        }
        #[derive(Serialize)]
        struct Body<'a> {
            ops: &'a [TxOp],
        }
        serde_json::to_vec(&Body { ops: &self.ops })
            .map_err(|e| Error::InvalidInput(format!("encode transaction: {e}")))
    }
}

/// A committed transaction.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct TxResult {
    #[serde(with = "u64_string")]
    pub offset: u64,
    pub durability: Durability,
    /// One result per op, in order.
    pub results: Vec<OpResult>,
}

/// The outcome of one transaction op.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OpResult {
    /// A write (`create`, `put`, `patch`, `delete`).
    Write { id: DocumentId, version: Version },
    /// An `assert` (`count` is `None`) or `assert_count` (`count` is set).
    Asserted { count: Option<u64> },
}

impl OpResult {
    /// The written document's version, for write ops.
    #[must_use]
    pub fn version(&self) -> Option<&Version> {
        match self {
            Self::Write { version, .. } => Some(version),
            Self::Asserted { .. } => None,
        }
    }

    /// The written document's ID, for write ops.
    #[must_use]
    pub fn id(&self) -> Option<DocumentId> {
        match self {
            Self::Write { id, .. } => Some(*id),
            Self::Asserted { .. } => None,
        }
    }
}

impl<'de> Deserialize<'de> for OpResult {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        #[derive(Deserialize)]
        struct Wire {
            id: Option<DocumentId>,
            version: Option<Version>,
            #[serde(default)]
            asserted: bool,
            #[serde(default, with = "opt_u64_string")]
            count: Option<u64>,
        }
        let w = Wire::deserialize(d)?;
        match (w.id, w.version, w.asserted) {
            (Some(id), Some(version), false) => Ok(Self::Write { id, version }),
            (None, None, true) => Ok(Self::Asserted { count: w.count }),
            _ => Err(D::Error::custom(
                "transaction result is neither a write nor an assert",
            )),
        }
    }
}
