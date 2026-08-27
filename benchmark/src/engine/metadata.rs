//! Document metadata and change-vector contract (GAP-D02, ticket #59).
//!
//! V1 identified a document's change with a change vector — a
//! per-database etag list such as `A:5-AAAAAAAAAAAAAAAAAAAAAA,
//! B:12-BBBBBBBBBBBBBBBBBBBBBB` — carried in `@metadata` (`@change-vector`
//! beside `@last-modified` and `@flags`), with the new-document and
//! ignore-changes markers tracked at the session level. V2 replaces that
//! identity with one typed triple, the
//! [`DocumentVersionContract`]:
//!
//! - `version` — the per-key monotone publication version
//!   ([`DocumentMetadata::version`](crate::types::DocumentMetadata::version));
//! - `sequence` — the global storage sequence of the publication that
//!   produced this version, which keeps a delete/re-create cycle (where the
//!   version restarts at one) from colliding with the earlier identity;
//! - `stamp` — the [`ReplicationClockStamp`] of ADR-0004 D-3, the ordering
//!   authority V2 decides last-writer-wins with. Engine-local history has
//!   no replication stamp, so it is served at the lane's origin clock
//!   `(0, 0)` — the same deterministic default the replication transport
//!   uses for unstamped history. The stamp is a slot for the replication
//!   lane's fact, never a laundered V1 etag.
//!
//! The read path serves the contract through two typed operations:
//! [`Transaction::head`] (V1's exists/HEAD: missing, unchanged against a
//! supplied contract, or current) and [`Transaction::load_if_match`]
//! (V1's conditional load under If-Match semantics: an exact contract
//! loads the document; a stale vector is rejected with
//! [`ConditionalLoadError::Stale`] carrying the current contract).
//!
//! The V1→V2 mapping for logical migration (#44) is recorded, not live:
//! [`V1ChangeVector`] parses V1's exact grammar and
//! [`V1ChangeVectorMapping`] reduces it to content-free provenance. No V1
//! etag, database identity, or arity becomes a live V2 version, sequence,
//! or stamp. The decision record is `docs/engine/metadata-change-vector-contract.md`.

use crate::replication::ReplicationClockStamp;
use crate::storage::PublishedWrite;
use crate::types::{CollectionName, Document, DocumentId, StorageSequence};

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

/// The maximum accepted V1 change-vector text length.
///
/// The bound keeps the typed parse cost proportional to a bounded input;
/// real V1 vectors are bounded by the writing cluster's size. A longer text
/// fails closed instead of parsing.
pub const MAX_V1_CHANGE_VECTOR_BYTES: usize = 4096;

/// The maximum number of entries one parsed V1 change vector may hold.
///
/// A wider vector fails closed; the mapping discloses the arity it recorded
/// so the rejection is auditable at the migration seam.
pub const MAX_V1_CHANGE_VECTOR_ENTRIES: usize = 64;

/// The exact byte length of one V1 change-vector database identity.
///
/// V1 renders a 16-byte `Guid` as 22 unpadded base64 characters
/// (`ChangeVectorExtensions.TryToBase64`'s
/// `ConvertToBase64ArrayUnpadded(.., 16)`), and the parser takes exactly
/// that many per entry (`Substring(current + 1, 22)`); its
/// `DbBase64IdSize` is 23 because it counts the `-` separator plus the
/// 22 identity characters when skipping past one entry (pinned V1 commit
/// `fa99b9257776dbe625e6a2388c74c9ce916637ee`).
const V1_DB_ID_BYTES: usize = 22;

// ---------------------------------------------------------------------------
// The version contract
// ---------------------------------------------------------------------------

/// The V2 change identity of one published document version — what a V1
/// change vector becomes in V2 (GAP-D02 decision D1).
///
/// Two contracts are equal exactly when all three components are equal, and
/// equality is the only comparison the read seam performs: HEAD answers
/// "unchanged" on equality and "current" otherwise, and a conditional load
/// serves only an exact match. The contract deliberately carries no
/// ordering; convergence ordering belongs to the `(stamp, Member)` total
/// order of ADR-0004 D-3, which lives in the replication lane.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
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
// The recorded V1 change-vector mapping (for the V1→V2 logical migration)
// ---------------------------------------------------------------------------

/// One parsed entry of a V1 change vector.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct V1ChangeVectorEntry {
    node_tag: u32,
    etag: u64,
    db_id: String,
}

impl V1ChangeVectorEntry {
    /// Returns the entry's node tag under V1's little-endian base-26 fold
    /// (`A` is zero; the tag text's last letter is the most significant
    /// digit, so `AB` is twenty-six).
    #[must_use]
    pub const fn node_tag(&self) -> u32 {
        self.node_tag
    }

    /// Returns the entry's per-database etag.
    #[must_use]
    pub const fn etag(&self) -> u64 {
        self.etag
    }

    /// Returns the entry's 22-character unpadded base64 database identity.
    #[must_use]
    pub fn db_id(&self) -> &str {
        &self.db_id
    }
}

/// A V1 change vector parsed under V1's exact grammar.
///
/// The parser mirrors V1's `ChangeVectorParser.ToChangeVectorList` (pinned
/// V1 commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`): entries of
/// `TAG:ETAG-DBID` with uppercase base-26 tags, decimal etags, exactly
/// 22-character database identities, and separator runs of commas and/or
/// whitespace between entries (a trailing separator run is accepted, as in
/// V1). Two documented differences fail closed where V1 computes silently
/// — an etag past the `u64` range rejects instead of wrapping, and a
/// non-digit inside an etag rejects instead of mis-reading its character
/// code — and one documented leniency accepts a leading separator run
/// where V1's scanner would throw.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct V1ChangeVector {
    entries: Vec<V1ChangeVectorEntry>,
}

/// Parse failures for V1 change-vector text.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum V1ChangeVectorError {
    /// The text does not use V1's grammar, or exceeds the bounded input or
    /// entry budget.
    Malformed,
    /// An etag lies past the `u64` range. V1 accumulates into a signed
    /// 64-bit integer and wraps; the typed parse fails closed instead.
    EtagOverflow,
}

impl V1ChangeVector {
    /// Parses V1 change-vector text under V1's exact grammar.
    ///
    /// An empty text parses to an empty vector, matching V1's null vector
    /// for missing metadata.
    ///
    /// # Errors
    ///
    /// Returns [`V1ChangeVectorError::Malformed`] for text outside V1's
    /// grammar or past the bounded input and entry budgets, and
    /// [`V1ChangeVectorError::EtagOverflow`] for an etag past the `u64`
    /// range.
    pub fn parse(text: &str) -> Result<Self, V1ChangeVectorError> {
        if text.len() > MAX_V1_CHANGE_VECTOR_BYTES {
            return Err(V1ChangeVectorError::Malformed);
        }
        let mut entries = Vec::new();
        let bytes = text.as_bytes();
        let mut position = 0usize;
        loop {
            position = skip_separators(bytes, position);
            if position == bytes.len() {
                break;
            }
            if entries.len() == MAX_V1_CHANGE_VECTOR_ENTRIES {
                return Err(V1ChangeVectorError::Malformed);
            }
            let (node_tag, next) = parse_node_tag(bytes, position)?;
            position = next;
            let (etag, next) = parse_etag(bytes, position)?;
            position = next;
            let Some(db_id) = take_db_id(text, position) else {
                return Err(V1ChangeVectorError::Malformed);
            };
            position += V1_DB_ID_BYTES;
            entries.push(V1ChangeVectorEntry { node_tag, etag, db_id });
        }
        Ok(Self { entries })
    }

    /// Returns the parsed entries in text order.
    #[must_use]
    pub fn entries(&self) -> &[V1ChangeVectorEntry] {
        &self.entries
    }

    /// Returns the dominant entry: the greatest etag, with the database
    /// identity as the deterministic tie-break. This is V1's own etag
    /// order, recorded as provenance only — it is never a live V2 ordering
    /// input (decision D1).
    #[must_use]
    pub fn dominant(&self) -> Option<&V1ChangeVectorEntry> {
        self.entries.iter().max_by_key(|entry| (entry.etag, entry.db_id.as_str()))
    }
}

/// Skips one run of V1 separators: any mix of whitespace and commas.
fn skip_separators(bytes: &[u8], mut position: usize) -> usize {
    while let Some(byte) = bytes.get(position) {
        if byte.is_ascii_whitespace() || *byte == b',' {
            position += 1;
        } else {
            break;
        }
    }
    position
}

/// Parses one uppercase base-26 node tag terminated by `:`, using V1's
/// exact fold (last letter most significant).
fn parse_node_tag(bytes: &[u8], start: usize) -> Result<(u32, usize), V1ChangeVectorError> {
    let mut digits = Vec::new();
    let mut position = start;
    while let Some(byte) = bytes.get(position) {
        match byte {
            b'A'..=b'Z' => {
                digits.push(u32::from(byte - b'A'));
                position += 1;
            }
            b':' => break,
            _ => return Err(V1ChangeVectorError::Malformed),
        }
    }
    if digits.is_empty() || bytes.get(position) != Some(&b':') {
        return Err(V1ChangeVectorError::Malformed);
    }
    // V1 seeds with the last letter and folds leftward, so the final letter
    // is the most significant digit: `A` is 0, `B` is 1, `AB` is 26.
    let mut tag = 0u32;
    for digit in digits.iter().rev() {
        tag = tag
            .checked_mul(26)
            .and_then(|shifted| shifted.checked_add(*digit))
            .ok_or(V1ChangeVectorError::Malformed)?;
    }
    Ok((tag, position + 1))
}

/// Parses one decimal etag terminated by `-`, failing closed on overflow.
fn parse_etag(bytes: &[u8], start: usize) -> Result<(u64, usize), V1ChangeVectorError> {
    let mut etag = 0u64;
    let mut position = start;
    let mut digits = 0usize;
    while let Some(byte) = bytes.get(position) {
        match byte {
            b'0'..=b'9' => {
                let digit = u64::from(byte - b'0');
                etag = etag
                    .checked_mul(10)
                    .and_then(|shifted| shifted.checked_add(digit))
                    .ok_or(V1ChangeVectorError::EtagOverflow)?;
                digits += 1;
                position += 1;
            }
            b'-' => break,
            _ => return Err(V1ChangeVectorError::Malformed),
        }
    }
    if digits == 0 || bytes.get(position) != Some(&b'-') {
        return Err(V1ChangeVectorError::Malformed);
    }
    Ok((etag, position + 1))
}

/// Takes the next exactly-22-character database identity, V1's verbatim
/// slice of the remaining text.
fn take_db_id(text: &str, position: usize) -> Option<String> {
    text.get(position..position.checked_add(V1_DB_ID_BYTES)?).map(str::to_owned)
}

/// The recorded V1→V2 change-identity mapping of one migrated document
/// (GAP-D02 decision D1, consumed by the V1→V2 logical migration #44).
///
/// The mapping is provenance, never identity: a V1 vector of any arity —
/// the five-entry vectors a five-database V1 topology produces included —
/// reduces to the disclosed arity, the dominant entry under V1's own etag
/// order, and an order-canonical provenance digest. The migrated document
/// receives a fresh V2 identity (version one, the migration commit's
/// sequence) assigned by its first V2 publication; no V1 fact is
/// re-stamped into it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct V1ChangeVectorMapping {
    entry_count: u32,
    dominant: Option<V1ChangeVectorEntry>,
    provenance_digest: u64,
}

impl V1ChangeVectorMapping {
    /// Records the mapping of one parsed V1 change vector.
    #[must_use]
    pub fn from_vector(vector: &V1ChangeVector) -> Self {
        let mut canonical = vector.entries().to_vec();
        canonical.sort();
        let mut text = String::new();
        for (index, entry) in canonical.iter().enumerate() {
            if index > 0 {
                text.push_str(", ");
            }
            push_entry(&mut text, entry);
        }
        Self {
            entry_count: u32::try_from(vector.entries().len()).unwrap_or(u32::MAX),
            dominant: vector.dominant().cloned(),
            provenance_digest: provenance_fold(text.as_bytes()),
        }
    }

    /// Returns the number of entries the V1 vector held.
    #[must_use]
    pub const fn entry_count(&self) -> u32 {
        self.entry_count
    }

    /// Returns the dominant entry under V1's etag order, or `None` for an
    /// empty vector.
    #[must_use]
    pub fn dominant(&self) -> Option<&V1ChangeVectorEntry> {
        self.dominant.as_ref()
    }

    /// Returns the order-canonical provenance digest of the entry set.
    ///
    /// The digest is a deterministic FNV-1a fold over the canonically
    /// ordered entries — the same digest family the storage layer's write
    /// set digest uses — for audit identity only. It is not cryptographic
    /// and carries no security claim.
    #[must_use]
    pub const fn provenance_digest(&self) -> u64 {
        self.provenance_digest
    }
}

/// Renders one entry in V1's serialization order (tag, etag, db id).
fn push_entry(text: &mut String, entry: &V1ChangeVectorEntry) {
    let mut tag = entry.node_tag;
    loop {
        let remainder = char::from(b'A' + u8::try_from(tag % 26).unwrap_or(0));
        text.push(remainder);
        tag /= 26;
        if tag == 0 {
            break;
        }
    }
    text.push(':');
    text.push_str(&entry.etag.to_string());
    text.push('-');
    text.push_str(&entry.db_id);
}

/// Folds canonical entry text into a provenance digest (FNV-1a, 64-bit).
fn provenance_fold(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
