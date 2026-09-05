//! Durable schema-catalog records and the `SQLite` adapter contract.
//!
//! `CanonicalSchemaDocument` is the customer-facing `terrascale.schema.json`
//! value.  This module owns the envelope that makes that value durable: the
//! database/branch pin, catalog entry identity, schema version, migration
//! generation and status, and a checksum covering the complete envelope.
//!
//! The module deliberately does not open `SQLite` or know a table handle.  A
//! member adapter implements [`SchemaCatalogAdapter`] and must perform the
//! load and compare-and-swap in one `SQLite` write transaction.  The exact
//! columns, constraints, and transaction ordering are documented by
//! [`SQLITE_SCHEMA_CATALOG_DDL`] and the trait method documentation.  This
//! keeps the record format testable without adding a second storage path.

use std::collections::BTreeMap;
use std::fmt;

use crate::families::schemas::SchemaFormatError;
use crate::families::{CanonicalSchemaDocument, SchemaVersion};
use crate::identity::{DatabaseUlid, SchemaCatalogEntryUlid};
use crate::types::DocumentValue;

/// The reserved durable-record format marker.
pub const SCHEMA_CATALOG_FORMAT: &str = "terrascale.schema.catalog.v1";

/// The logical `SQLite` table an adapter must own.
pub const SQLITE_SCHEMA_CATALOG_TABLE: &str = "terrascale_schema_catalog";

/// `SQLite` schema required by [`SchemaCatalogAdapter`].
//
// Keep this string as a reviewable contract.  The adapter may add SQLite
// query-plan indexes, but may not remove a column, weaken a CHECK, or use a
// lossy TEXT/JSON representation for the canonical schema and checksum.
pub const SQLITE_SCHEMA_CATALOG_DDL: &str = r"
CREATE TABLE IF NOT EXISTS terrascale_schema_catalog (
    database_id BLOB NOT NULL CHECK (length(database_id) = 16),
    branch TEXT NOT NULL CHECK (length(branch) BETWEEN 1 AND 255),
    branch_incarnation INTEGER NOT NULL CHECK (branch_incarnation > 0),
    entry_id BLOB NOT NULL CHECK (length(entry_id) = 16),
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    schema_json BLOB NOT NULL,
    migration_generation INTEGER NOT NULL CHECK (migration_generation >= 0),
    migration_status TEXT NOT NULL CHECK (
        migration_status IN ('none', 'in-progress', 'rolling-back', 'stopped', 'complete')
    ),
    checksum BLOB NOT NULL CHECK (length(checksum) = 32),
    PRIMARY KEY (database_id, branch, branch_incarnation),
    UNIQUE (entry_id)
);
";

/// A branch/database incarnation to which a schema catalog is pinned.
///
/// A branch name is a route label, not an entity identity.  The database is
/// therefore represented by the strict [`DatabaseUlid`], while the positive
/// incarnation prevents a restored/replaced branch from accepting an old
/// catalog record.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SchemaBranchPin {
    database: DatabaseUlid,
    branch: String,
    incarnation: u64,
}

/// Short alias used by adapters that already have a branch-scoped API.
pub type BranchPin = SchemaBranchPin;

impl SchemaBranchPin {
    /// Creates a pin after validating the branch label and incarnation.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::InvalidBranchPin`] when the branch label
    /// is empty, too long, non-ASCII, or carries an unexpected byte, or when
    /// the incarnation is zero.
    pub fn try_new(
        database: DatabaseUlid,
        branch: impl Into<String>,
        incarnation: u64,
    ) -> Result<Self, SchemaCatalogError> {
        let branch = branch.into();
        validate_branch(&branch)?;
        if incarnation == 0 {
            return Err(SchemaCatalogError::InvalidBranchPin);
        }
        Ok(Self { database, branch, incarnation })
    }

    /// Returns the database identity.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.database
    }

    /// Returns the branch route label.
    #[must_use]
    pub fn branch(&self) -> &str {
        &self.branch
    }

    /// Returns the branch incarnation.
    #[must_use]
    pub const fn incarnation(&self) -> u64 {
        self.incarnation
    }
}

fn validate_branch(value: &str) -> Result<(), SchemaCatalogError> {
    if value.is_empty()
        || value.len() > 255
        || !value.is_ascii()
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
    {
        Err(SchemaCatalogError::InvalidBranchPin)
    } else {
        Ok(())
    }
}

/// The closed migration status vocabulary persisted beside the active schema.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum SchemaMigrationStatus {
    /// The branch has no migration drive.
    None,
    /// A forward migration is applying pages.
    InProgress,
    /// A reversible migration is being rolled back.
    RollingBack,
    /// A migration stopped before completion and needs operator action.
    Stopped,
    /// The migration completed and its target schema is active.
    Complete,
}

impl SchemaMigrationStatus {
    /// Returns the one persisted spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::None => "none",
            Self::InProgress => "in-progress",
            Self::RollingBack => "rolling-back",
            Self::Stopped => "stopped",
            Self::Complete => "complete",
        }
    }

    /// Parses the one accepted persisted spelling; aliases are rejected.
    #[allow(
        clippy::should_implement_trait,
        reason = "the catalog persists an Option-based spelling without an Error type"
    )]
    #[must_use]
    pub fn from_str(value: &str) -> Option<Self> {
        Some(match value {
            "none" => Self::None,
            "in-progress" => Self::InProgress,
            "rolling-back" => Self::RollingBack,
            "stopped" => Self::Stopped,
            "complete" => Self::Complete,
            _ => return None,
        })
    }
}

/// A BLAKE3 checksum of the complete schema-catalog record body.
#[derive(Clone, Copy, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SchemaChecksum([u8; 32]);

impl fmt::Debug for SchemaChecksum {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_tuple("SchemaChecksum").field(&self.to_hex()).finish()
    }
}

impl SchemaChecksum {
    /// Constructs a checksum from its fixed-width bytes.
    #[must_use]
    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    /// Returns the checksum bytes.
    #[must_use]
    pub const fn as_bytes(self) -> [u8; 32] {
        self.0
    }

    /// Returns lowercase hexadecimal text.
    #[must_use]
    pub fn to_hex(self) -> String {
        let mut text = String::with_capacity(64);
        for byte in self.0 {
            use fmt::Write;
            let _ = write!(text, "{byte:02x}");
        }
        text
    }

    fn from_hex(value: &str) -> Option<Self> {
        if value.len() != 64 {
            return None;
        }
        let mut bytes = [0_u8; 32];
        for (index, pair) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
            let high = hex_nibble(pair[0])?;
            let low = hex_nibble(pair[1])?;
            bytes[index] = (high << 4) | low;
        }
        Some(Self(bytes))
    }
}

fn hex_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}

/// The complete durable schema catalog record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaCatalogRecord {
    entry_id: SchemaCatalogEntryUlid,
    branch: SchemaBranchPin,
    schema: CanonicalSchemaDocument,
    schema_version: SchemaVersion,
    migration_generation: u64,
    migration_status: SchemaMigrationStatus,
    checksum: SchemaChecksum,
}

impl SchemaCatalogRecord {
    /// Creates a record and computes its checksum over every persisted fact.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::InvalidSchemaVersion`],
    /// [`SchemaCatalogError::InvalidMigrationGeneration`], or
    /// [`SchemaCatalogError::Schema`] when the record fails validation.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        entry_id: SchemaCatalogEntryUlid,
        branch: SchemaBranchPin,
        schema: CanonicalSchemaDocument,
        schema_version: SchemaVersion,
        migration_generation: u64,
        migration_status: SchemaMigrationStatus,
    ) -> Result<Self, SchemaCatalogError> {
        let mut record = Self {
            entry_id,
            branch,
            schema,
            schema_version,
            migration_generation,
            migration_status,
            checksum: SchemaChecksum::from_bytes([0; 32]),
        };
        record.validate_without_checksum()?;
        record.checksum = record.compute_checksum();
        Ok(record)
    }

    /// Returns the catalog entry identity (also the idempotency identity).
    #[must_use]
    pub const fn entry_id(&self) -> SchemaCatalogEntryUlid {
        self.entry_id
    }

    /// Returns the database/branch pin.
    #[must_use]
    pub fn branch(&self) -> &SchemaBranchPin {
        &self.branch
    }

    /// Returns the complete canonical schema document.
    #[must_use]
    pub fn schema(&self) -> &CanonicalSchemaDocument {
        &self.schema
    }

    /// Returns the monotonic logical schema version.
    #[must_use]
    pub const fn schema_version(&self) -> SchemaVersion {
        self.schema_version
    }

    /// Alias for callers that use the shorter version vocabulary.
    #[must_use]
    pub const fn version(&self) -> SchemaVersion {
        self.schema_version
    }

    /// Returns the migration generation.
    #[must_use]
    pub const fn migration_generation(&self) -> u64 {
        self.migration_generation
    }

    /// Returns the migration status.
    #[must_use]
    pub const fn migration_status(&self) -> SchemaMigrationStatus {
        self.migration_status
    }

    /// Returns the verified checksum.
    #[must_use]
    pub const fn checksum(&self) -> SchemaChecksum {
        self.checksum
    }

    /// Encodes the strict durable value, including the checksum.
    #[must_use]
    pub fn to_value(&self) -> DocumentValue {
        self.encode_body(true)
    }

    /// Alias for adapters that call the record codec directly.
    #[must_use]
    pub fn encode(&self) -> DocumentValue {
        self.to_value()
    }

    /// Decodes and verifies a strict durable value.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::MalformedRecord`],
    /// [`SchemaCatalogError::UnsupportedFormat`],
    /// [`SchemaCatalogError::UnsupportedKeyword`],
    /// [`SchemaCatalogError::Schema`],
    /// [`SchemaCatalogError::MalformedChecksum`], or
    /// [`SchemaCatalogError::ChecksumMismatch`] when the value fails to decode
    /// or verify.
    pub fn from_value(value: &DocumentValue) -> Result<Self, SchemaCatalogError> {
        let DocumentValue::Object(fields) = value else {
            return Err(SchemaCatalogError::MalformedRecord);
        };
        reject_unknown(
            fields,
            &[
                "format",
                "entryId",
                "branch",
                "schema",
                "schemaVersion",
                "migrationGeneration",
                "migrationStatus",
                "checksum",
            ],
        )?;
        let format = string(fields, "format")?;
        if format != SCHEMA_CATALOG_FORMAT {
            return Err(SchemaCatalogError::UnsupportedFormat);
        }
        let entry_id = SchemaCatalogEntryUlid::from_text(&string(fields, "entryId")?)
            .map_err(|_| SchemaCatalogError::MalformedRecord)?;
        let branch = decode_branch(fields.get("branch"))?;
        let schema = CanonicalSchemaDocument::from_value(
            fields.get("schema").ok_or(SchemaCatalogError::MalformedRecord)?,
        )
        .map_err(SchemaCatalogError::Schema)?;
        let schema_version = version(fields, "schemaVersion")?;
        let migration_generation = nonnegative(fields, "migrationGeneration")?;
        let migration_status = SchemaMigrationStatus::from_str(&string(fields, "migrationStatus")?)
            .ok_or(SchemaCatalogError::MalformedRecord)?;
        let checksum = SchemaChecksum::from_hex(&string(fields, "checksum")?)
            .ok_or(SchemaCatalogError::MalformedChecksum)?;
        let record = Self {
            entry_id,
            branch,
            schema,
            schema_version,
            migration_generation,
            migration_status,
            checksum,
        };
        record.validate_without_checksum()?;
        if record.compute_checksum() != checksum {
            return Err(SchemaCatalogError::ChecksumMismatch);
        }
        Ok(record)
    }

    /// Alias for adapters that call the record codec directly.
    ///
    /// # Errors
    ///
    /// Returns the same typed [`SchemaCatalogError`] cases as
    /// [`Self::from_value`].
    pub fn decode(value: &DocumentValue) -> Result<Self, SchemaCatalogError> {
        Self::from_value(value)
    }

    /// Returns the canonical UTF-8 `terrascale.schema.json` representation.
    ///
    /// `SQLite` adapters persist this exact byte sequence in `schema_json`;
    /// metadata is stored in its typed columns and reconstructed into this
    /// envelope before checksum verification on load.
    ///
    /// # Panics
    ///
    /// Panics when the validated canonical schema cannot serialize; this is
    /// unreachable for records built by [`Self::try_new`].
    #[must_use]
    pub fn schema_json(&self) -> String {
        self.schema.to_json().expect("validated canonical schema serializes")
    }

    /// Re-checks the checksum after an adapter has loaded a record.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::ChecksumMismatch`] when the stored
    /// checksum does not match the body, or the validation case when the
    /// record itself is malformed.
    pub fn verify(&self) -> Result<(), SchemaCatalogError> {
        self.validate_without_checksum()?;
        if self.compute_checksum() == self.checksum {
            Ok(())
        } else {
            Err(SchemaCatalogError::ChecksumMismatch)
        }
    }

    fn validate_without_checksum(&self) -> Result<(), SchemaCatalogError> {
        if self.schema_version.get() == 0 {
            return Err(SchemaCatalogError::InvalidSchemaVersion);
        }
        if self.migration_status == SchemaMigrationStatus::None {
            if self.migration_generation != 0 {
                return Err(SchemaCatalogError::InvalidMigrationGeneration);
            }
        } else if self.migration_generation == 0 {
            return Err(SchemaCatalogError::InvalidMigrationGeneration);
        }
        validate_schema_document(&self.schema).map_err(SchemaCatalogError::Schema)
    }

    fn encode_body(&self, with_checksum: bool) -> DocumentValue {
        let mut fields = vec![
            ("format", DocumentValue::String(SCHEMA_CATALOG_FORMAT.to_owned())),
            ("entryId", DocumentValue::String(self.entry_id.to_text())),
            ("branch", encode_branch(&self.branch)),
            ("schema", self.schema.to_value()),
            ("schemaVersion", DocumentValue::Integer(interchange(self.schema_version.get()))),
            ("migrationGeneration", DocumentValue::Integer(interchange(self.migration_generation))),
            ("migrationStatus", DocumentValue::String(self.migration_status.as_str().to_owned())),
        ];
        if with_checksum {
            fields.push(("checksum", DocumentValue::String(self.checksum.to_hex())));
        }
        object(fields)
    }

    fn compute_checksum(&self) -> SchemaChecksum {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(SCHEMA_CATALOG_FORMAT.as_bytes());
        bytes.push(0);
        append_json(&self.encode_body(false), &mut bytes);
        SchemaChecksum::from_bytes(*blake3::hash(&bytes).as_bytes())
    }
}

fn validate_schema_document(schema: &CanonicalSchemaDocument) -> Result<(), SchemaFormatError> {
    // `CanonicalSchemaDocument` exposes no borrowed validator; re-running the
    // canonical constructor over a clone reuses its exact validation.
    CanonicalSchemaDocument::try_new(schema.collections().to_vec(), schema.version()).map(|_| ())
}

fn interchange(value: u64) -> i64 {
    // SchemaVersion already guarantees this bound.  Generation is bounded at
    // the record boundary so it cannot become an ambiguous SQLite integer.
    i64::try_from(value).unwrap_or(i64::MAX)
}

fn encode_branch(branch: &SchemaBranchPin) -> DocumentValue {
    object(vec![
        ("database", DocumentValue::String(branch.database.to_text())),
        ("name", DocumentValue::String(branch.branch.clone())),
        ("incarnation", DocumentValue::Integer(interchange(branch.incarnation))),
    ])
}

fn decode_branch(value: Option<&DocumentValue>) -> Result<SchemaBranchPin, SchemaCatalogError> {
    let DocumentValue::Object(fields) = value.ok_or(SchemaCatalogError::MalformedRecord)? else {
        return Err(SchemaCatalogError::MalformedRecord);
    };
    reject_unknown(fields, &["database", "name", "incarnation"])?;
    let database = DatabaseUlid::from_text(&string(fields, "database")?)
        .map_err(|_| SchemaCatalogError::InvalidBranchPin)?;
    SchemaBranchPin::try_new(database, string(fields, "name")?, nonnegative(fields, "incarnation")?)
}

fn object(fields: Vec<(&str, DocumentValue)>) -> DocumentValue {
    DocumentValue::Object(fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect())
}

fn reject_unknown(
    fields: &BTreeMap<String, DocumentValue>,
    allowed: &[&str],
) -> Result<(), SchemaCatalogError> {
    if fields.keys().all(|key| allowed.contains(&key.as_str())) {
        Ok(())
    } else {
        Err(SchemaCatalogError::UnsupportedKeyword)
    }
}

fn string(
    fields: &BTreeMap<String, DocumentValue>,
    key: &str,
) -> Result<String, SchemaCatalogError> {
    match fields.get(key) {
        Some(DocumentValue::String(value)) => Ok(value.clone()),
        _ => Err(SchemaCatalogError::MalformedRecord),
    }
}

fn nonnegative(
    fields: &BTreeMap<String, DocumentValue>,
    key: &str,
) -> Result<u64, SchemaCatalogError> {
    match fields.get(key) {
        Some(DocumentValue::Integer(value)) if *value >= 0 => {
            u64::try_from(*value).map_err(|_| SchemaCatalogError::MalformedRecord)
        }
        _ => Err(SchemaCatalogError::MalformedRecord),
    }
}

fn version(
    fields: &BTreeMap<String, DocumentValue>,
    key: &str,
) -> Result<SchemaVersion, SchemaCatalogError> {
    let value = nonnegative(fields, key)?;
    SchemaVersion::try_new(value).map_err(|_| SchemaCatalogError::InvalidSchemaVersion)
}

/// Reasons a catalog value or transition was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SchemaCatalogError {
    /// A persisted record has the wrong shape or a missing required field.
    MalformedRecord,
    /// The record format marker is not supported.
    UnsupportedFormat,
    /// A key outside the strict record vocabulary was found.
    UnsupportedKeyword,
    /// The canonical schema failed its own strict validation.
    Schema(SchemaFormatError),
    /// A schema version is zero or outside the `SQLite` interchange domain.
    InvalidSchemaVersion,
    /// A branch name or incarnation is invalid.
    InvalidBranchPin,
    /// A migration generation is inconsistent with its status.
    InvalidMigrationGeneration,
    /// The checksum is not lowercase fixed-width hexadecimal.
    MalformedChecksum,
    /// The record checksum does not match its body.
    ChecksumMismatch,
    /// A successor would move the schema version backwards.
    NonMonotonicVersion,
    /// A successor changed schema contents without advancing its version.
    VersionContentMismatch,
    /// A successor moved the migration generation backwards.
    NonMonotonicMigrationGeneration,
    /// A CAS request expected a different current record.
    CompareAndSwapConflict,
    /// An idempotency identity was reused for different contents.
    IdempotencyConflict,
    /// The successor belongs to another branch/database incarnation.
    BranchMismatch,
}

/// The expected current catalog identity in a CAS request.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SchemaCatalogExpectation {
    version: Option<SchemaVersion>,
    checksum: Option<SchemaChecksum>,
}

impl SchemaCatalogExpectation {
    /// Expects no row (the insert case).
    #[must_use]
    pub const fn absent() -> Self {
        Self { version: None, checksum: None }
    }

    /// Expects the exact version and checksum of a loaded row.
    #[must_use]
    pub const fn exact(record: &SchemaCatalogRecord) -> Self {
        Self { version: Some(record.schema_version), checksum: Some(record.checksum) }
    }

    /// Returns the expected schema version.
    #[must_use]
    pub const fn version(self) -> Option<SchemaVersion> {
        self.version
    }

    /// Returns the expected checksum.
    #[must_use]
    pub const fn checksum(self) -> Option<SchemaChecksum> {
        self.checksum
    }
}

/// One idempotent schema-catalog CAS operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaCatalogCasRequest {
    expected: SchemaCatalogExpectation,
    desired: SchemaCatalogRecord,
}

impl SchemaCatalogCasRequest {
    /// Creates a request after verifying the desired record itself.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::CompareAndSwapConflict`] when only one
    /// of the expected version and checksum is set, or the verification case
    /// when the desired record is malformed.
    pub fn try_new(
        expected: SchemaCatalogExpectation,
        desired: SchemaCatalogRecord,
    ) -> Result<Self, SchemaCatalogError> {
        desired.verify()?;
        if expected.version.is_some() != expected.checksum.is_some() {
            return Err(SchemaCatalogError::CompareAndSwapConflict);
        }
        Ok(Self { expected, desired })
    }

    /// Returns the expected current identity.
    #[must_use]
    pub const fn expected(&self) -> SchemaCatalogExpectation {
        self.expected
    }

    /// Returns the desired record.
    #[must_use]
    pub fn desired(&self) -> &SchemaCatalogRecord {
        &self.desired
    }

    /// Applies the pure CAS rules to a loaded current row.
    ///
    /// # Errors
    ///
    /// Returns [`SchemaCatalogError::BranchMismatch`],
    /// [`SchemaCatalogError::NonMonotonicVersion`],
    /// [`SchemaCatalogError::VersionContentMismatch`],
    /// [`SchemaCatalogError::NonMonotonicMigrationGeneration`], or
    /// [`SchemaCatalogError::IdempotencyConflict`] when the successor breaks
    /// the transition rules.
    pub fn evaluate(
        &self,
        current: Option<&SchemaCatalogRecord>,
    ) -> Result<SchemaCatalogCasOutcome, SchemaCatalogError> {
        self.desired.verify()?;
        if let Some(current) = current {
            current.verify()?;
            if current.entry_id == self.desired.entry_id {
                return if current.checksum == self.desired.checksum {
                    Ok(SchemaCatalogCasOutcome::AlreadyApplied)
                } else {
                    Err(SchemaCatalogError::IdempotencyConflict)
                };
            }
            if self.expected != SchemaCatalogExpectation::exact(current) {
                return Ok(SchemaCatalogCasOutcome::Conflict);
            }
            if current.branch != self.desired.branch {
                return Err(SchemaCatalogError::BranchMismatch);
            }
            if self.desired.schema_version.get() < current.schema_version.get() {
                return Err(SchemaCatalogError::NonMonotonicVersion);
            }
            if self.desired.schema_version == current.schema_version
                && self.desired.schema != current.schema
            {
                return Err(SchemaCatalogError::VersionContentMismatch);
            }
            if self.desired.migration_generation < current.migration_generation {
                return Err(SchemaCatalogError::NonMonotonicMigrationGeneration);
            }
            Ok(SchemaCatalogCasOutcome::Applied)
        } else if self.expected != SchemaCatalogExpectation::absent() {
            Ok(SchemaCatalogCasOutcome::Conflict)
        } else {
            Ok(SchemaCatalogCasOutcome::Applied)
        }
    }
}

/// The result an adapter returns after its atomic CAS.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SchemaCatalogCasOutcome {
    /// The desired row was installed.
    Applied,
    /// The same entry identity and checksum were already installed.
    AlreadyApplied,
    /// Another writer won the expected-version/checksum race.
    Conflict,
}

/// Storage adapter required by the schema catalog.
pub trait SchemaCatalogAdapter {
    /// Adapter-specific storage failure.  Decode, checksum, and transition
    /// errors must be returned as the adapter's typed error rather than
    /// treated as an absent row.
    type Error;

    /// Loads the row for exactly this branch pin.
    ///
    /// The `SQLite` implementation must run a point lookup using the complete
    /// `(database_id, branch, branch_incarnation)` primary key, decode
    /// `schema_json` with [`SchemaCatalogRecord::from_value`], and return a
    /// checksum failure for any row that cannot be proved.  `NULL`, a missing
    /// row, and a corrupt row are distinct outcomes.
    ///
    /// # Errors
    ///
    /// Returns the adapter's `Error` when the point lookup cannot be served.
    fn load(
        &mut self,
        branch: &SchemaBranchPin,
    ) -> Result<Option<SchemaCatalogRecord>, Self::Error>;

    /// Atomically installs one idempotent successor.
    ///
    /// The `SQLite` implementation must use one `BEGIN IMMEDIATE` transaction:
    /// load and verify the current row, call [`SchemaCatalogCasRequest::evaluate`],
    /// and then insert/update the complete row before commit.  The update
    /// predicate must include the branch primary key plus the expected
    /// `schema_version` and `checksum`; a unique `entry_id` conflict is
    /// `AlreadyApplied` only when the stored bytes and checksum are identical.
    /// Never update schema JSON, metadata, or checksum in separate commits.
    ///
    /// # Errors
    ///
    /// Returns the adapter's `Error` when the atomic install cannot complete.
    fn compare_and_swap(
        &mut self,
        request: &SchemaCatalogCasRequest,
    ) -> Result<SchemaCatalogCasOutcome, Self::Error>;
}

fn append_json(value: &DocumentValue, output: &mut Vec<u8>) {
    match value {
        DocumentValue::Boolean(value) => {
            output.extend_from_slice(if *value { b"true" } else { b"false" });
        }
        DocumentValue::Integer(value) => output.extend_from_slice(value.to_string().as_bytes()),
        DocumentValue::Number(value) if value.is_finite() => {
            output.extend_from_slice(value.to_string().as_bytes());
        }
        DocumentValue::Null | DocumentValue::Number(_) => output.extend_from_slice(b"null"),
        DocumentValue::String(value) => append_json_string(value, output),
        DocumentValue::Array(values) => {
            output.push(b'[');
            for (index, value) in values.iter().enumerate() {
                if index != 0 {
                    output.push(b',');
                }
                append_json(value, output);
            }
            output.push(b']');
        }
        DocumentValue::Object(values) => {
            output.push(b'{');
            for (index, (key, value)) in values.iter().enumerate() {
                if index != 0 {
                    output.push(b',');
                }
                append_json_string(key, output);
                output.push(b':');
                append_json(value, output);
            }
            output.push(b'}');
        }
    }
}

fn append_json_string(value: &str, output: &mut Vec<u8>) {
    output.push(b'"');
    for character in value.chars() {
        match character {
            '"' => output.extend_from_slice(b"\\\""),
            '\\' => output.extend_from_slice(b"\\\\"),
            '\n' => output.extend_from_slice(b"\\n"),
            '\r' => output.extend_from_slice(b"\\r"),
            '\t' => output.extend_from_slice(b"\\t"),
            character if character.is_control() => {
                use fmt::Write;
                let mut escaped = String::new();
                let _ = write!(escaped, "\\u{:04x}", character as u32);
                output.extend_from_slice(escaped.as_bytes());
            }
            character => {
                let mut encoded = [0_u8; 4];
                output.extend_from_slice(character.encode_utf8(&mut encoded).as_bytes());
            }
        }
    }
    output.push(b'"');
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::families::schemas::{CanonicalIndex, SchemaMergePolicy, SchemaTtl};
    use crate::families::{CanonicalCollectionSchema, CanonicalField, CanonicalFieldType};

    fn schema() -> CanonicalSchemaDocument {
        let field = CanonicalField::try_new(
            "expiresAt",
            CanonicalFieldType::Timestamp,
            true,
            true,
            false,
            None,
        )
        .expect("valid field");
        let collection = CanonicalCollectionSchema::try_new(
            "events",
            vec![field],
            vec![CanonicalIndex::btree("expires", vec!["expiresAt".to_owned()]).expect("index")],
            Some(SchemaTtl::try_new("expiresAt").expect("ttl")),
            vec![],
            Some(SchemaVersion::try_new(1).expect("version")),
            30,
            SchemaMergePolicy::RemoveWins,
        )
        .expect("collection");
        CanonicalSchemaDocument::try_new(
            vec![collection],
            Some(SchemaVersion::try_new(1).expect("version")),
        )
        .expect("document")
    }

    fn record(version: u64, status: SchemaMigrationStatus, generation: u64) -> SchemaCatalogRecord {
        SchemaCatalogRecord::try_new(
            SchemaCatalogEntryUlid::generate(),
            SchemaBranchPin::try_new(DatabaseUlid::generate(), "main", 1).expect("pin"),
            schema(),
            SchemaVersion::try_new(version).expect("version"),
            generation,
            status,
        )
        .expect("record")
    }

    #[test]
    fn complete_record_round_trips_and_preserves_checksum() {
        let original = record(1, SchemaMigrationStatus::None, 0);
        let decoded = SchemaCatalogRecord::from_value(&original.to_value()).expect("decode");
        assert_eq!(decoded, original);
        assert_eq!(decoded.schema().to_json(), original.schema().to_json());
    }

    #[test]
    fn unknown_fields_and_tampering_fail_closed() {
        let original = record(1, SchemaMigrationStatus::None, 0);
        let mut value = original.to_value();
        if let DocumentValue::Object(fields) = &mut value {
            fields.insert("extra".to_owned(), DocumentValue::Boolean(true));
        }
        assert_eq!(
            SchemaCatalogRecord::from_value(&value),
            Err(SchemaCatalogError::UnsupportedKeyword)
        );

        let mut value = original.to_value();
        if let DocumentValue::Object(fields) = &mut value {
            fields.insert("migrationGeneration".to_owned(), DocumentValue::Integer(1));
        }
        assert_eq!(
            SchemaCatalogRecord::from_value(&value),
            Err(SchemaCatalogError::InvalidMigrationGeneration)
        );
    }

    #[test]
    fn cas_is_idempotent_and_rejects_version_regression() {
        let first = record(1, SchemaMigrationStatus::None, 0);
        let retry =
            SchemaCatalogCasRequest::try_new(SchemaCatalogExpectation::absent(), first.clone())
                .expect("request");
        assert_eq!(retry.evaluate(None), Ok(SchemaCatalogCasOutcome::Applied));
        assert_eq!(retry.evaluate(Some(&first)), Ok(SchemaCatalogCasOutcome::AlreadyApplied));

        let first = record(2, SchemaMigrationStatus::None, 0);
        let lower = SchemaCatalogRecord::try_new(
            SchemaCatalogEntryUlid::generate(),
            first.branch().clone(),
            first.schema().clone(),
            SchemaVersion::try_new(1).expect("version"),
            0,
            SchemaMigrationStatus::None,
        )
        .expect("record");
        let request =
            SchemaCatalogCasRequest::try_new(SchemaCatalogExpectation::exact(&first), lower)
                .expect("request");
        assert_eq!(request.evaluate(Some(&first)), Err(SchemaCatalogError::NonMonotonicVersion));
    }
}
