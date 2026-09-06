//! Migration evidence: the manifest digest, the checksum chains, the dry-run
//! plan, and the receipt (SM-03).
//!
//! Every migration states its evidence in the recovery-lane vocabulary: a
//! manifest identity (which contract ran), checksums (what the collection's
//! documents hashed to before and after), and counts (how many documents and
//! transforms were touched). The checksum is a SHA-256 chain over the
//! collection's documents in canonical key order, so it is order-stable
//! across pages and across an interrupted-and-resumed run, and it is computed
//! by the same SHA-256 primitive the backup manifest uses.

use crate::families::payloads::{SchemaDefinition, SchemaVersion, SchemaViolation};
use crate::families::state::ValidationRecord;
use crate::types::{CollectionName, DocumentId, DocumentValue};

use super::transform::{
    Coercion, RollbackLimit, SchemaMigration, TransformCounts, TransformLiteral, TransformRejection,
};
use aws_lc_rs::digest::{Context, SHA256};

/// The domain separator of the document chain.
const CHAIN_START: &[u8] = b"terrabase.schema-migration.chain.v1";

/// The domain separator of one pre-state document link.
const LINK_SOURCE: &[u8] = b"source";

/// The domain separator of one result-state document link.
const LINK_RESULT: &[u8] = b"result";

/// One running SHA-256 chain over a collection's documents.
///
/// The chain links documents in the canonical (collection, identifier) key
/// order the drive scans them in, and each link separates its side (`source`
/// or `result`), the document identity, and the value's canonical encoding,
/// so equal collections chain equally and any changed value changes the
/// digest. The chain state is the running digest, which is bounded and
/// resumable: an interrupted drive records the digest and the next page
/// extends it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct DocumentChain {
    running: [u8; 32],
    links: u64,
}

impl DocumentChain {
    /// Creates the chain over zero documents.
    pub(crate) fn new() -> Self {
        let mut hasher = Context::new(&SHA256);
        hasher.update(CHAIN_START);
        let running = hasher.finish().as_ref().try_into().expect("SHA-256 has a 32-byte digest");
        Self { running, links: 0 }
    }

    /// Restores a chain from its recorded running digest and link count.
    pub(crate) fn from_parts(running: [u8; 32], links: u64) -> Self {
        Self { running, links }
    }

    /// Links one document value under one side separator.
    pub(crate) fn link(&mut self, side: &[u8], id: &DocumentId, value: &DocumentValue) {
        let mut hasher = Context::new(&SHA256);
        hasher.update(side);
        hasher.update(&self.running);
        hasher.update(id.as_str().as_bytes());
        hasher.update(&canonical_value(value));
        self.running = hasher.finish().as_ref().try_into().expect("SHA-256 has a 32-byte digest");
        self.links += 1;
    }

    /// Links one document's pre-migration value.
    pub(crate) fn link_source(&mut self, id: &DocumentId, value: &DocumentValue) {
        self.link(LINK_SOURCE, id, value);
    }

    /// Links one document's migration-result value.
    pub(crate) fn link_result(&mut self, id: &DocumentId, value: &DocumentValue) {
        self.link(LINK_RESULT, id, value);
    }

    /// Returns the running digest bytes.
    pub(crate) fn running(&self) -> [u8; 32] {
        self.running
    }

    /// Returns the number of linked documents.
    pub(crate) fn links(&self) -> u64 {
        self.links
    }
}

impl Default for DocumentChain {
    fn default() -> Self {
        Self::new()
    }
}

/// Encodes one document value into its canonical digest bytes.
///
/// The encoding is total and deterministic: object fields digest in the
/// canonical map order the value type already carries, numbers digest their
/// IEEE-754 bit pattern, and every case is length-prefixed so no two
/// encodings collide by concatenation. The encoding is internal to this
/// module — it establishes identity between two computations over the same
/// values, not a stable cross-version serialization format.
fn canonical_value(value: &DocumentValue) -> Vec<u8> {
    let mut bytes = Vec::new();
    encode_value(value, &mut bytes);
    bytes
}

/// Encodes one value into the digest buffer.
fn encode_value(value: &DocumentValue, bytes: &mut Vec<u8>) {
    match value {
        DocumentValue::Null => bytes.extend_from_slice(b"n;"),
        DocumentValue::Boolean(flag) => {
            bytes.extend_from_slice(if *flag { b"b1;" } else { b"b0;" });
        }
        DocumentValue::Integer(number) => {
            bytes.extend_from_slice(b"i");
            bytes.extend_from_slice(number.to_string().as_bytes());
            bytes.push(b';');
        }
        DocumentValue::Number(number) => {
            bytes.extend_from_slice(b"f");
            bytes.extend_from_slice(&number.to_bits().to_le_bytes());
        }
        DocumentValue::String(text) => {
            bytes.extend_from_slice(b"s");
            extend_length(bytes, text.len());
            bytes.extend_from_slice(text.as_bytes());
        }
        DocumentValue::Array(items) => {
            bytes.extend_from_slice(b"a");
            extend_length(bytes, items.len());
            for item in items {
                encode_value(item, bytes);
            }
        }
        DocumentValue::Object(fields) => {
            bytes.extend_from_slice(b"o");
            extend_length(bytes, fields.len());
            for (name, item) in fields {
                extend_length(bytes, name.len());
                bytes.extend_from_slice(name.as_bytes());
                encode_value(item, bytes);
            }
        }
    }
}

/// Length-prefixes one counted element into the digest buffer.
fn extend_length(bytes: &mut Vec<u8>, length: usize) {
    let length = u64::try_from(length).unwrap_or(u64::MAX);
    bytes.extend_from_slice(&length.to_le_bytes());
}

/// Computes the manifest digest of one migration contract.
///
/// The manifest digest is the contract's identity for idempotency and
/// resume: two drives whose contracts digest equally are the same migration,
/// and any changed transform, target rule, or version changes the digest. It
/// is derived only from contract content — no tenant, path, or document
/// value participates.
pub(crate) fn manifest_digest(contract: &SchemaMigration) -> String {
    let mut hasher = Context::new(&SHA256);
    hasher.update(b"terrabase.schema-migration.manifest.v1");
    hasher.update(contract.collection().as_str().as_bytes());
    hasher.update(contract.from_version().get().to_string().as_bytes());
    hasher.update(contract.to_version().get().to_string().as_bytes());
    hasher.update(&schema_digest_bytes(contract.target()));
    for transform in contract.transforms() {
        hasher.update(transform_digest(transform).as_bytes());
    }
    hex::encode(hasher.finish().as_ref())
}

/// Encodes one transform into its canonical manifest text.
fn transform_digest(transform: &super::transform::FieldTransform) -> String {
    use super::transform::FieldTransform;
    match transform {
        FieldTransform::AddField { field, default } => {
            format!("add|{field}|{}", literal_digest(default))
        }
        FieldTransform::RemoveField { field } => format!("remove|{field}"),
        FieldTransform::RenameField { from, to } => format!("rename|{from}|{to}"),
        FieldTransform::RetypeField { field, coercion } => {
            format!("retype|{field}|{}", coercion_digest(*coercion))
        }
    }
}

/// Encodes one default literal into its canonical manifest text.
///
/// A text default carries both its length and its bytes, so two different
/// defaults never share a manifest identity.
fn literal_digest(literal: &TransformLiteral) -> String {
    match literal {
        TransformLiteral::Boolean(flag) => format!("b{flag}"),
        TransformLiteral::Integer(number) => format!("i{number}"),
        TransformLiteral::Text(text) => format!("s{}|{}", text.len(), text),
    }
}

/// Encodes one coercion into its canonical manifest text.
fn coercion_digest(coercion: Coercion) -> &'static str {
    match coercion {
        Coercion::IntegerToText => "integer-to-text",
        Coercion::TextToInteger => "text-to-integer",
        Coercion::IntegerToNumber => "integer-to-number",
        Coercion::NumberToInteger => "number-to-integer",
    }
}

/// One typed problem a dry run found in one document.
///
/// Content-safe: it names the document, the field, and the rule kind —
/// never a field value or a document body.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlanProblem {
    document: DocumentId,
    field: String,
    detail: PlanDetail,
}

impl PlanProblem {
    /// Creates one plan problem. Crate-only.
    pub(crate) fn new(document: DocumentId, field: String, detail: PlanDetail) -> Self {
        Self { document, field, detail }
    }

    /// Returns the document the problem was found in.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the field the problem names.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the typed problem.
    #[must_use]
    pub fn detail(&self) -> &PlanDetail {
        &self.detail
    }
}

/// The typed detail of one plan problem.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PlanDetail {
    /// A transform's coercion contract rejects the document's value.
    TransformRejected(TransformRejection),
    /// The transformed document violates the target schema; the violations
    /// carry the enforced rule kinds in the schema model's canonical order.
    TargetViolations(Vec<SchemaViolation>),
}

/// The outcome of one dry run: ready to apply, or blocked with the typed
/// problems that must be resolved first.
#[derive(Clone, Debug, PartialEq)]
pub enum PlanOutcome {
    /// Every document of the collection transforms and validates: the plan
    /// carries the evidence the apply pass must reproduce.
    Ready {
        /// The document count of the collection.
        documents: u64,
        /// The checksum chain over the collection's pre-migration values.
        source_digest: String,
        /// The checksum chain over the collection's expected result values.
        result_digest: String,
        /// The per-kind transform application counts the apply pass must
        /// reproduce.
        transform_counts: TransformCounts,
    },
    /// At least one document cannot move under this contract: nothing will
    /// be applied and the problems state why, document by document.
    Blocked {
        /// The typed problems, in canonical document order.
        problems: Vec<PlanProblem>,
    },
}

/// The dry-run result of one migration contract: the transform plan and the
/// validation outcome, computed without mutating anything.
///
/// The plan is the migration's pre-statement of evidence: its manifest
/// digest identifies the contract, and its `Ready` evidence (counts and
/// checksums) is exactly what a later apply must reproduce before it
/// finalizes — a collection that changed between the dry run and the apply
/// fails the comparison closed.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationPlan {
    manifest: String,
    collection: CollectionName,
    from: SchemaVersion,
    to: SchemaVersion,
    outcome: PlanOutcome,
}

impl MigrationPlan {
    /// Creates one plan. Crate-only.
    pub(crate) fn new(
        manifest: String,
        collection: CollectionName,
        from: SchemaVersion,
        to: SchemaVersion,
        outcome: PlanOutcome,
    ) -> Self {
        Self { manifest, collection, from, to, outcome }
    }

    /// Restores ready evidence saved by the host before beginning a drive.
    ///
    /// The contract identity and checksum encoding are verified before any
    /// resumed page can consume the evidence. Finalization still compares
    /// the saved counts and chains against the durable collection.
    ///
    /// # Errors
    /// Returns `PlanMismatch` for a changed contract or malformed checksum.
    pub fn restore_ready(
        contract: &SchemaMigration,
        manifest: &str,
        documents: u64,
        source_digest: String,
        result_digest: String,
        transform_counts: TransformCounts,
    ) -> Result<Self, super::MigrationError> {
        if manifest != manifest_digest(contract)
            || from_hex(&source_digest).is_none()
            || from_hex(&result_digest).is_none()
        {
            return Err(super::MigrationError::PlanMismatch);
        }
        Ok(Self::new(
            manifest.to_owned(),
            contract.collection().clone(),
            contract.from_version(),
            contract.to_version(),
            PlanOutcome::Ready { documents, source_digest, result_digest, transform_counts },
        ))
    }

    /// Returns the manifest digest of the contract the plan was computed
    /// for.
    #[must_use]
    pub fn manifest(&self) -> &str {
        &self.manifest
    }

    /// Returns the collection the plan addresses.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the from-version the plan was computed against.
    #[must_use]
    pub fn from_version(&self) -> SchemaVersion {
        self.from
    }

    /// Returns the to-version the plan advances to.
    #[must_use]
    pub fn to_version(&self) -> SchemaVersion {
        self.to
    }

    /// Returns the plan's outcome.
    #[must_use]
    pub fn outcome(&self) -> &PlanOutcome {
        &self.outcome
    }

    /// Reports whether the plan is ready to apply.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        matches!(self.outcome, PlanOutcome::Ready { .. })
    }
}

/// The durable evidence record of one completed (or idempotently re-run)
/// Schema Migration.
///
/// The receipt carries the recovery-lane evidence the migration contract
/// owes: the manifest identity, the source and result checksums, the
/// document and transform counts, the page count, the rollback limit that
/// was in force, and whether an irreversible migration was applied under
/// explicit acknowledgment. It is secret-safe by construction: no document
/// value, field value, path, or credential appears — digests and counts
/// only.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationReceipt {
    collection: CollectionName,
    manifest: String,
    from: SchemaVersion,
    to: SchemaVersion,
    source_digest: String,
    result_digest: String,
    documents: u64,
    transformed: u64,
    transform_counts: TransformCounts,
    pages: u64,
    rollback_limit: RollbackLimit,
    acknowledged_not_rollbackable: bool,
    already_applied: bool,
}

impl MigrationReceipt {
    /// Creates one receipt. Crate-only.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new(
        collection: CollectionName,
        manifest: String,
        from: SchemaVersion,
        to: SchemaVersion,
        source_digest: String,
        result_digest: String,
        documents: u64,
        transformed: u64,
        transform_counts: TransformCounts,
        pages: u64,
        rollback_limit: RollbackLimit,
        acknowledged_not_rollbackable: bool,
        already_applied: bool,
    ) -> Self {
        Self {
            collection,
            manifest,
            from,
            to,
            source_digest,
            result_digest,
            documents,
            transformed,
            transform_counts,
            pages,
            rollback_limit,
            acknowledged_not_rollbackable,
            already_applied,
        }
    }

    /// Returns the migrated collection.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the manifest digest of the applied contract.
    #[must_use]
    pub fn manifest(&self) -> &str {
        &self.manifest
    }

    /// Returns the version the collection moved from.
    #[must_use]
    pub fn from_version(&self) -> SchemaVersion {
        self.from
    }

    /// Returns the version the collection moved to.
    #[must_use]
    pub fn to_version(&self) -> SchemaVersion {
        self.to
    }

    /// Returns the checksum chain over the collection's pre-migration
    /// values.
    #[must_use]
    pub fn source_digest(&self) -> &str {
        &self.source_digest
    }

    /// Returns the checksum chain over the collection's result values.
    #[must_use]
    pub fn result_digest(&self) -> &str {
        &self.result_digest
    }

    /// Returns the document count the migration scanned.
    #[must_use]
    pub fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the count of documents whose value changed under the
    /// transforms.
    #[must_use]
    pub fn transformed(&self) -> u64 {
        self.transformed
    }

    /// Returns the per-kind transform application counts.
    #[must_use]
    pub fn transform_counts(&self) -> TransformCounts {
        self.transform_counts
    }

    /// Returns the number of bounded pages the apply pass committed.
    #[must_use]
    pub fn pages(&self) -> u64 {
        self.pages
    }

    /// Returns the rollback limit in force for the migration.
    #[must_use]
    pub fn rollback_limit(&self) -> &RollbackLimit {
        &self.rollback_limit
    }

    /// Reports whether an irreversible migration was applied under explicit
    /// acknowledgment.
    #[must_use]
    pub fn acknowledged_not_rollbackable(&self) -> bool {
        self.acknowledged_not_rollbackable
    }

    /// Reports whether this receipt reports an idempotent no-op re-run of an
    /// already-completed migration rather than a fresh application.
    #[must_use]
    pub fn already_applied(&self) -> bool {
        self.already_applied
    }

    /// Returns this receipt marked as an idempotent no-op re-run. Crate-only:
    /// the drive operations use it when they return a recorded receipt
    /// without staging anything.
    pub(crate) fn as_already_applied(&self) -> Self {
        let mut copy = self.clone();
        copy.already_applied = true;
        copy
    }
}

/// Encodes one schema definition into its digest bytes for the manifest.
///
/// The schema's durable record encoding is already canonical — rules in
/// canonical order under the validation record's encoder — so the manifest
/// reuses it instead of defining a second schema encoding.
pub(crate) fn schema_digest_bytes(schema: &SchemaDefinition) -> Vec<u8> {
    let mut bytes = Vec::new();
    encode_value(&ValidationRecord { schema: schema.clone() }.encode(), &mut bytes);
    bytes
}

/// Decodes one lowercase hex digest, failing on any character outside it or
/// any length other than the digest length.
///
/// This is the inverse of the lowercase hex encoding used for digests:
/// the migration record's decoder stays in this module instead of widening
/// the backup checksum module's surface.
pub(crate) fn from_hex(text: &str) -> Option<[u8; 32]> {
    if text.bytes().any(|byte| byte.is_ascii_uppercase()) {
        return None;
    }
    let mut digest = [0; 32];
    hex::decode_to_slice(text, &mut digest).ok()?;
    Some(digest)
}
