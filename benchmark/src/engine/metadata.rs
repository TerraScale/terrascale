//! Native document version and conditional-read contracts.
//!
//! - `version` — the per-key monotone publication version
//!   ([`DocumentMetadata::version`](crate::types::DocumentMetadata::version));
//! - `sequence` — the global storage sequence of the publication that
//!   produced this version, which keeps a delete/re-create cycle (where the
//!   version restarts at one) from colliding with the earlier identity;
//! - `stamp` — the [`ReplicationClockStamp`] used by the replication lane.
//!   Engine-local history has no replication stamp, so it uses the
//!   deterministic origin clock `(0, 0)`.
//!
//! The read path serves the contract through two typed operations:
//! [`Transaction::head`] (missing, unchanged against a supplied contract, or
//! current) and [`Transaction::load_if_match`] (an exact contract
//! loads the document; a stale vector is rejected with
//! [`ConditionalLoadError::Stale`] carrying the current contract).

use crate::storage::PublishedWrite;
use crate::types::{CollectionName, Document, DocumentId, ReplicationClockStamp, StorageSequence};

use super::{EngineError, Transaction};

/// The stamp engine-local history is served at: the replication lane's
/// origin clock, `(0, 0)`.
///
/// The engine core does not mint replication stamps; documents published
/// and read on one engine carry this deterministic constant so the contract
/// of a given publication is stable and comparable. The replication lane
/// records its own stamps when it exists (ADR-0004 D-3); this constant
/// never varies with the reader's clock.
const ENGINE_LOCAL_ORIGIN_STAMP: ReplicationClockStamp = ReplicationClockStamp::new(0, 0);

/// The native change identity of one published document version.
///
/// Two contracts are equal exactly when all three components are equal, and
/// equality is the only comparison the read seam performs: HEAD answers
/// "unchanged" on equality and "current" otherwise, and a conditional load
/// serves only an exact match. The contract deliberately carries no
/// ordering; convergence ordering belongs to the `(stamp, Member)` total
/// order of ADR-0004 D-3, which lives in the replication lane. The type
/// derives no `Ord`/`PartialOrd` on purpose: an order here would afford
/// exactly the recency misuse D-3 assigns elsewhere (warden MINOR at
/// integration, 2026-08-27).
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct DocumentVersionContract {
    version: u64,
    sequence: StorageSequence,
    stamp: ReplicationClockStamp,
}

/// Impossible version-contract facts.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DocumentVersionContractError {
    /// No publication ever assigns version zero; the first publication of a
    /// key is version one.
    ZeroVersion,
    /// Committed publication sequences start at one; zero is not a
    /// publication.
    ZeroSequence,
}

impl DocumentVersionContract {
    /// Creates a contract from validated publication facts.
    ///
    /// # Errors
    ///
    /// Returns [`DocumentVersionContractError::ZeroVersion`] for version
    /// zero and [`DocumentVersionContractError::ZeroSequence`] for sequence
    /// zero: both are facts no publication can produce, so a caller holding
    /// one did not obtain it from the engine.
    pub fn try_new(
        version: u64,
        sequence: u64,
        stamp: ReplicationClockStamp,
    ) -> Result<Self, DocumentVersionContractError> {
        if version == 0 {
            return Err(DocumentVersionContractError::ZeroVersion);
        }
        if sequence == 0 {
            return Err(DocumentVersionContractError::ZeroSequence);
        }
        Ok(Self { version, sequence: StorageSequence::new(sequence), stamp })
    }

    /// Returns the per-key monotone publication version.
    #[must_use]
    pub const fn version(&self) -> u64 {
        self.version
    }

    /// Returns the global storage sequence of the publication that produced
    /// this version.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the ADR-0004 D-3 clock stamp of the publication, or the
    /// engine-local origin clock `(0, 0)` when the engine core published
    /// without a replication stamp.
    #[must_use]
    pub const fn stamp(&self) -> ReplicationClockStamp {
        self.stamp
    }
}

/// The condition a HEAD read is asked under (GAP-D02 decision D2).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HeadCondition {
    /// Report existence and the current contract (V1 HEAD without a
    /// supplied change vector).
    Any,
    /// Compare the supplied contract first (V1 HEAD with a
    /// `?changeVector=` parameter): equality answers "unchanged", anything
    /// else answers the current contract.
    IfUnchanged(DocumentVersionContract),
}

/// The typed HEAD answer over the version contract (GAP-D02 decision D2).
///
/// The three variants are V1's three HEAD outcomes: `404` for a missing
/// key, `304 Not Modified` when the supplied vector still matches, and
/// `200` with the current `ETag` otherwise.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DocumentHead {
    /// The key does not exist at the read snapshot (V1 404).
    Missing,
    /// The key exists and matches the supplied contract (V1 304). The
    /// answer carries the contract so it is self-describing, like V1's 304
    /// with its `ETag` header.
    Unchanged(DocumentVersionContract),
    /// The key exists; carries the current contract (V1 200 + `ETag`). This
    /// is also the answer under [`HeadCondition::Any`] whenever the key
    /// exists.
    Current(DocumentVersionContract),
}

impl DocumentHead {
    /// Returns the version contract this answer carries, or `None` for a
    /// missing key.
    #[must_use]
    pub const fn contract(&self) -> Option<&DocumentVersionContract> {
        match self {
            Self::Missing => None,
            Self::Unchanged(contract) | Self::Current(contract) => Some(contract),
        }
    }
}

/// The typed rejection vocabulary of a conditional (If-Match) load
/// (GAP-D02 decision D3).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ConditionalLoadError {
    /// The key does not exist: an If-Match precondition on a missing key
    /// cannot hold, so the load is rejected rather than answered with an
    /// empty success.
    Missing,
    /// The stored contract differs from the supplied one — the supplied
    /// vector is stale. Carries the current contract so the caller can
    /// re-issue against it.
    Stale {
        /// The document's current version contract.
        current: DocumentVersionContract,
    },
    /// The engine could not serve the contract at all, for example because
    /// storage is unreachable or the durable publication history can no
    /// longer prove the document's publication sequence. The read fails
    /// closed instead of answering an unprovable identity.
    Engine(EngineError),
}

// ---------------------------------------------------------------------------
// Contract resolution over the publication history
// ---------------------------------------------------------------------------

/// One resolved published state: the document at the snapshot and the
/// version contract of its publication.
struct PublishedState {
    document: Document,
    contract: DocumentVersionContract,
}

/// Resolves the document at this transaction's snapshot together with the
/// version contract of the publication that produced its version.
///
/// The document and its version come from the snapshot table; the sequence
/// comes from a backward walk of the tenant's publication history to the
/// last commit whose write set touched this exact key. Staged, unpublished
/// writes are invisible here by design: they have no published identity.
/// A document the history cannot account for fails closed — the contract
/// is a publication fact, never a guess.
fn resolve_published(
    transaction: &Transaction<'_>,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<Option<PublishedState>, EngineError> {
    // Child-module access to the transaction's engine, scope, and snapshot
    // sequence: the same private facts `Transaction::read` reads through.
    let engine = transaction.engine;
    let tenant = transaction.scope.tenant();
    let snapshot = transaction.snapshot_sequence;
    let storage = engine.lock_storage()?;
    let document = storage.port.read_document_in_collection_at(snapshot, tenant, collection, id);
    let Some(document) = document else {
        return Ok(None);
    };

    // Walk the tenant's publication history backward from the snapshot to
    // the last commit touching this exact key. `tenant_sequence` names the
    // tenant's latest commit at or below the bound, so each step lands on
    // one of this tenant's commits and skips every other tenant's
    // sequences; the walk stops at the first touching commit.
    let mut bound = snapshot;
    let touching = loop {
        let Some(commit_sequence) = storage.port.tenant_sequence(tenant, bound) else {
            // The table holds the document but the history holds no commit
            // for this tenant: the publication cannot be proven.
            return Err(EngineError::StorageFailure);
        };
        if commit_sequence.get() == 0 {
            // Publication sequences start at one; zero is not a commit.
            return Err(EngineError::StorageFailure);
        }
        let after = StorageSequence::new(commit_sequence.get() - 1);
        let commit = storage
            .port
            .tenant_commits(tenant, after, 1)
            .map_err(|()| EngineError::StorageFailure)?
            .into_iter()
            .next()
            .filter(|commit| commit.sequence() == commit_sequence);
        let Some(commit) = commit else {
            // The index names a sequence the journal does not serve.
            return Err(EngineError::StorageFailure);
        };
        let touching = commit.writes().iter().find_map(|write| match write {
            PublishedWrite::Put { document } => (document.collection() == collection
                && document.id() == id)
                .then(|| (commit.sequence(), document.metadata().version())),
            PublishedWrite::Delete {
                collection: Some(deleted_collection), id: deleted_id, ..
            } => {
                // A delete of this exact key as the last touching write
                // contradicts the snapshot table, which holds the document.
                (deleted_collection == collection && deleted_id == id)
                    .then(|| (commit.sequence(), 0))
            }
            // A collection-less delete is the provable no-op: it removed
            // nothing and cannot be the publication behind an existing
            // document, so the walk continues past it.
            PublishedWrite::Delete { collection: None, .. } => None,
        });
        if let Some(found) = touching {
            break found;
        }
        bound = after;
    };

    let (sequence, published_version) = touching;
    if published_version != document.metadata().version() {
        // The history's published form and the snapshot table disagree on
        // the version; the identity is unprovable.
        return Err(EngineError::StorageFailure);
    }
    let contract = DocumentVersionContract::try_new(
        document.metadata().version(),
        sequence.get(),
        ENGINE_LOCAL_ORIGIN_STAMP,
    )
    .map_err(|_| EngineError::StorageFailure)?;
    Ok(Some(PublishedState { document, contract }))
}

impl Transaction<'_> {
    /// Answers a typed exists/HEAD read over the version contract.
    ///
    /// Under [`HeadCondition::Any`] the answer is [`DocumentHead::Missing`]
    /// or [`DocumentHead::Current`] (V1 HEAD without a vector: 404 or 200 +
    /// `ETag`). Under [`HeadCondition::IfUnchanged`] an exact match answers
    /// [`DocumentHead::Unchanged`] (V1 304) and anything else answers
    /// [`DocumentHead::Current`] with the live contract — a stale supplied
    /// vector is never silently matched.
    ///
    /// The read observes only published state: staged writes of this
    /// transaction are invisible because they have no published identity
    /// until commit.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached or the durable publication history cannot prove the
    /// document's publication. The HEAD fails closed rather than answering
    /// an unprovable identity.
    pub fn head(
        &self,
        collection: &CollectionName,
        id: &DocumentId,
        condition: HeadCondition,
    ) -> Result<DocumentHead, EngineError> {
        let Some(state) = resolve_published(self, collection, id)? else {
            return Ok(DocumentHead::Missing);
        };
        match condition {
            HeadCondition::Any => Ok(DocumentHead::Current(state.contract)),
            HeadCondition::IfUnchanged(expected) => {
                if expected == state.contract {
                    Ok(DocumentHead::Unchanged(state.contract))
                } else {
                    Ok(DocumentHead::Current(state.contract))
                }
            }
        }
    }

    /// Loads one document under If-Match semantics on the version contract.
    ///
    /// The load succeeds only when the stored contract equals `expected`;
    /// the answer is the published document. A stale or missing key is
    /// rejected with the typed [`ConditionalLoadError`] vocabulary, never
    /// answered with different data or an empty success: the caller asked
    /// for one version of the document and anything else is a rejection it
    /// can act on. Like [`Transaction::head`], this read observes only
    /// published state.
    ///
    /// # Errors
    ///
    /// Returns [`ConditionalLoadError::Missing`] when the key does not
    /// exist, [`ConditionalLoadError::Stale`] when the stored contract
    /// differs from `expected` (carrying the current contract), and
    /// [`ConditionalLoadError::Engine`] when the contract cannot be served
    /// at all.
    pub fn load_if_match(
        &self,
        collection: &CollectionName,
        id: &DocumentId,
        expected: &DocumentVersionContract,
    ) -> Result<Document, ConditionalLoadError> {
        let Some(state) =
            resolve_published(self, collection, id).map_err(ConditionalLoadError::Engine)?
        else {
            return Err(ConditionalLoadError::Missing);
        };
        if state.contract != *expected {
            return Err(ConditionalLoadError::Stale { current: state.contract });
        }
        Ok(state.document)
    }
}

// ---------------------------------------------------------------------------
