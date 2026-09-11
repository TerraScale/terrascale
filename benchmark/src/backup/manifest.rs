//! The private manifest model and its strict text codec.
//!
//! A manifest is a small, line-oriented, deterministic text document. The
//! recovery-point identity is the SHA-256 digest of every manifest line
//! except the `identity` line itself, so the identity is content-derived:
//! the same logical archive always yields the same identity, and any edit
//! of a bound fact breaks the digest. Validation is strict — unknown lines,
//! reordered blocks, out-of-range numbers, malformed identifiers, member
//! names outside the canonical writer form, and digest mismatches are all
//! [`BackupError::ManifestInvalid`] — and it runs before any target is
//! touched. Variable-length values that a human might read as tokens are
//! never rendered bare: a tenant name is length-prefixed, because tenant
//! identifiers may carry interior whitespace and a bare name would not
//! parse back.

use crate::backup::{BackupError, BackupKind};
use crate::{
    identity::DatabaseUlid,
    types::{StorageSequence, TenantId},
};
use aws_lc_rs::digest::{Context, SHA256, digest};
use std::io::Read;

/// The manifest file name inside one recovery-point directory.
pub(crate) const MANIFEST_FILE: &str = "manifest.tbv1m";

/// Manifest file name for the authenticated current backup format. The
/// legacy plaintext path above is intentionally retained only for explicit
/// compatibility reads.
pub(crate) const ENCRYPTED_MANIFEST_FILE: &str = "manifest.tbv3e";

/// The manifest format header and version.
///
/// Version 4 authenticates the capture class, distinguishing complete
/// standalone points from physical participants of a routed envelope.
/// Version 3 binds database, partition, and tenant scope identities in
/// addition to the facts already covered by Version 2. It also keeps the
/// Version 2 length-prefix for the `tenant` line. Version 1
/// rendered the name space-separated, so a valid tenant identifier with
/// interior whitespace (`team a` is committable) produced a manifest that
/// could never be parsed back; version 2 encodes the name's byte length so
/// the line is unambiguous for every representable tenant name.
pub(crate) const HEADER: &str = "terrabase-backup-manifest 4";

/// Authenticated scope of the data captured by a backup manifest.
///
/// This class is included in the manifest identity. Moving an encrypted
/// participant artifact into a standalone path cannot make it a complete
/// database recovery point.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupCaptureKind {
    /// A standalone capture containing the complete database.
    WholeDatabase,
    /// A participant capture authenticated inside a routed envelope.
    RoutedShardParticipant,
}

impl BackupCaptureKind {
    fn parse(value: &str) -> Result<Self, BackupError> {
        match value {
            "whole_database" => Ok(Self::WholeDatabase),
            "routed_shard_participant" => Ok(Self::RoutedShardParticipant),
            _ => Err(BackupError::ManifestInvalid),
        }
    }
    fn as_str(self) -> &'static str {
        match self {
            Self::WholeDatabase => "whole_database",
            Self::RoutedShardParticipant => "routed_shard_participant",
        }
    }
}

/// Hard upper bound for one serialized manifest.
///
/// Manifest parsing happens before a restore target is admitted.  Keeping the
/// bound here prevents a destination containing an attacker-controlled file
/// from turning validation into an unbounded allocation.
pub(crate) const MAX_MANIFEST_BYTES: usize = 1024 * 1024;
/// Maximum number of tenant bindings a manifest may carry.
const MAX_TENANTS: usize = 65_536;
/// Maximum number of archive members a manifest may carry.
const MAX_MEMBERS: usize = 65_536;
/// Maximum length of one line in the line-oriented manifest codec.
const MAX_LINE_BYTES: usize = 64 * 1024;

/// One bound archive member: its ordered canonical name, exact byte length,
/// and SHA-256 digest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupMember {
    name: String,
    length: u64,
    digest: String,
}

impl BackupMember {
    /// Creates one member fact from validated parts.
    pub(crate) fn new(name: String, length: u64, digest: String) -> Self {
        Self { name, length, digest }
    }

    /// Returns the member's canonical archive name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the member's exact byte length.
    #[must_use]
    pub fn length(&self) -> u64 {
        self.length
    }

    /// Returns the member's SHA-256 digest as lowercase hex.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns whether `bytes` match the exact length and SHA-256 digest
    /// bound into this member fact.
    ///
    /// Object-store transports use this before publishing a manifest commit
    /// marker, so corruption in their local transfer staging cannot seal an
    /// unusable remote recovery point.
    #[must_use]
    pub fn matches_bytes(&self, bytes: &[u8]) -> bool {
        if u64::try_from(bytes.len()).ok() != Some(self.length) {
            return false;
        }
        hex::encode(digest(&SHA256, bytes).as_ref()) == self.digest
    }
}

/// One tenant's archived record counts inside a manifest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupTenantCount {
    tenant: TenantId,
    documents: u64,
    family_records: u64,
}

impl BackupTenantCount {
    /// Creates one tenant count from validated parts.
    pub(crate) fn new(tenant: TenantId, documents: u64, family_records: u64) -> Self {
        Self { tenant, documents, family_records }
    }

    /// Returns the tenant the counts belong to.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the tenant's archived user-document count.
    #[must_use]
    pub fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the tenant's archived family-record count.
    #[must_use]
    pub fn family_records(&self) -> u64 {
        self.family_records
    }
}

/// The validated facts one sealed run binds into a manifest.
pub(crate) struct ManifestFacts {
    /// Identity of the source database instance that produced the point.
    pub(crate) database_id: String,
    /// Logical partition covered by this implementation's archive seam.
    pub(crate) partition_id: String,
    /// The backup kind.
    pub(crate) kind: BackupKind,
    pub(crate) capture_kind: BackupCaptureKind,
    /// The storage-sequence boundary the recovery point covers.
    pub(crate) boundary: StorageSequence,
    /// Durable timestamp of the included commit boundary.
    pub(crate) boundary_unix_millis: u64,
    /// Wall clock when the source boundary was captured.
    pub(crate) captured_unix_millis: u64,
    /// Immutable minimum retention deadline for the encrypted artifact.
    pub(crate) retain_until_unix_millis: u64,
    /// Canonical logical checksum at the captured boundary.
    pub(crate) logical_checksum: String,
    /// Live logical records, including reserved family records.
    pub(crate) logical_documents: u64,
    /// The archived user-document count.
    pub(crate) documents: u64,
    /// The archived family-record count.
    pub(crate) family_records: u64,
    /// The archived journal-delete count.
    pub(crate) deletes: u64,
    /// The count of durability disclosures.
    pub(crate) disclosures: u64,
    /// The per-tenant archived record counts, in canonical tenant order.
    pub(crate) tenants: Vec<BackupTenantCount>,
    /// The ordered archive members.
    pub(crate) members: Vec<BackupMember>,
}

/// An immutable recovery-point identity plus the ordered archive members it
/// binds.
///
/// The manifest binds the ordered archive names, lengths, and SHA-256
/// digests, the boundary and kind facts, and the per-kind record counts.
/// Validation rejects path traversal, in-progress manifests, and identity
/// mismatches before any target is created.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupManifest {
    database_id: String,
    partition_id: String,
    tenant_scope: String,
    identity: String,
    kind: BackupKind,
    capture_kind: BackupCaptureKind,
    boundary: StorageSequence,
    boundary_unix_millis: u64,
    captured_unix_millis: u64,
    retain_until_unix_millis: u64,
    logical_checksum: String,
    logical_documents: u64,
    documents: u64,
    family_records: u64,
    deletes: u64,
    disclosures: u64,
    tenants: Vec<BackupTenantCount>,
    members: Vec<BackupMember>,
    total_bytes: u64,
}

impl BackupManifest {
    /// Assembles a manifest from validated facts and derives its
    /// content-derived identity.
    pub(crate) fn assemble(facts: ManifestFacts) -> Self {
        let ManifestFacts {
            database_id,
            partition_id,
            kind,
            capture_kind,
            boundary,
            boundary_unix_millis,
            captured_unix_millis,
            retain_until_unix_millis,
            logical_checksum,
            logical_documents,
            documents,
            family_records,
            deletes,
            disclosures,
            tenants,
            members,
        } = facts;
        let tenant_scope = tenant_scope_digest(&tenants);
        let total_bytes = members.iter().map(BackupMember::length).sum();
        let mut manifest = Self {
            database_id,
            partition_id,
            tenant_scope,
            identity: String::new(),
            kind,
            capture_kind,
            boundary,
            boundary_unix_millis,
            captured_unix_millis,
            retain_until_unix_millis,
            logical_checksum,
            logical_documents,
            documents,
            family_records,
            deletes,
            disclosures,
            tenants,
            members,
            total_bytes,
        };
        manifest.identity =
            hex::encode(digest(&SHA256, manifest.body_lines().join("\n").as_bytes()).as_ref());
        manifest
    }

    /// Returns the recovery-point identity: the SHA-256 digest of the
    /// manifest body, as lowercase hex.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the source database binding carried by this point.
    #[must_use]
    pub fn database_id(&self) -> &str {
        &self.database_id
    }

    /// Returns the logical partition binding carried by this point.
    #[must_use]
    pub fn partition_id(&self) -> &str {
        &self.partition_id
    }

    /// Returns the digest binding the complete ordered tenant scope.
    #[must_use]
    pub fn tenant_scope_digest(&self) -> &str {
        &self.tenant_scope
    }

    /// Returns the backup kind this manifest records.
    #[must_use]
    pub fn kind(&self) -> BackupKind {
        self.kind
    }

    /// Returns the storage-sequence boundary the recovery point covers.
    #[must_use]
    pub fn boundary_sequence(&self) -> StorageSequence {
        self.boundary
    }

    /// Returns the durable commit clock at this point's boundary, in Unix milliseconds.
    #[must_use]
    pub fn boundary_unix_millis(&self) -> u64 {
        self.boundary_unix_millis
    }

    /// Returns the wall clock when this backup captured its source boundary.
    #[must_use]
    pub fn captured_unix_millis(&self) -> u64 {
        self.captured_unix_millis
    }

    /// Returns the authenticated provenance class of this recovery point.
    ///
    /// `WholeDatabase` points may be resolved from the standalone namespace;
    /// `RoutedShardParticipant` points are valid only as members of a routed
    /// envelope.
    #[must_use]
    pub fn capture_kind(&self) -> BackupCaptureKind {
        self.capture_kind
    }

    /// Returns the immutable earliest deletion time bound to this artifact.
    #[must_use]
    pub fn retain_until_unix_millis(&self) -> u64 {
        self.retain_until_unix_millis
    }

    /// Returns the inclusive source-clock interval covered by this capture.
    /// Historical pinned snapshots prove only their original commit boundary.
    #[must_use]
    pub fn recoverable_through_unix_millis(&self) -> u64 {
        match self.kind {
            BackupKind::Snapshot => self.boundary_unix_millis,
            BackupKind::Full | BackupKind::Incremental { .. } => self.captured_unix_millis,
        }
    }

    /// Returns the base boundary an incremental manifest builds on, or
    /// `None` for a full or snapshot manifest.
    #[must_use]
    pub fn base_sequence(&self) -> Option<StorageSequence> {
        match self.kind {
            BackupKind::Incremental { base } => Some(base),
            BackupKind::Full | BackupKind::Snapshot => None,
        }
    }

    /// Returns the canonical BLAKE3 logical checksum at this exact boundary.
    #[must_use]
    pub fn logical_checksum(&self) -> &str {
        &self.logical_checksum
    }

    /// Returns live logical records at the boundary, including reserved family records.
    #[must_use]
    pub fn logical_documents(&self) -> u64 {
        self.logical_documents
    }

    /// Returns the archived user-document count.
    #[must_use]
    pub fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the archived family-record count.
    #[must_use]
    pub fn family_records(&self) -> u64 {
        self.family_records
    }

    /// Returns the archived journal-delete count.
    #[must_use]
    pub fn deletes(&self) -> u64 {
        self.deletes
    }

    /// Returns the count of durability disclosures bound into the manifest.
    #[must_use]
    pub fn disclosures(&self) -> u64 {
        self.disclosures
    }

    /// Returns the per-tenant archived record counts, in canonical tenant
    /// order.
    #[must_use]
    pub fn tenants(&self) -> &[BackupTenantCount] {
        &self.tenants
    }

    /// Returns the ordered archive members this manifest binds.
    #[must_use]
    pub fn members(&self) -> &[BackupMember] {
        &self.members
    }

    /// Returns the sum of the bound member lengths.
    #[must_use]
    pub fn total_bytes(&self) -> u64 {
        self.total_bytes
    }

    /// Encodes this validated manifest in the canonical recovery-point
    /// format.
    ///
    /// Object-store adapters use these bytes as the commit marker for a
    /// recovery point. They upload every member first and this manifest last,
    /// so a listed manifest never names a partially uploaded point.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        self.render().into_bytes()
    }

    /// Verifies every archive member against this manifest before restore.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ChecksumMismatch`] when a member is missing or
    /// does not match its authenticated length and digest.
    pub fn verify(&self, destination: &std::path::Path) -> Result<(), BackupError> {
        super::verify_members(destination, &self.identity, &self.members)
    }

    /// Parses and validates one canonical recovery-point manifest.
    ///
    /// The same strict size, ordering, identity, and path checks used by the
    /// filesystem restore path apply here. This lets an object-store adapter
    /// select a chain before downloading its members without duplicating the
    /// backup format parser.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] when `bytes` are not one
    /// complete canonical manifest.
    pub fn decode(bytes: &[u8]) -> Result<Self, BackupError> {
        if bytes.len() > MAX_MANIFEST_BYTES {
            return Err(BackupError::ManifestInvalid);
        }
        let text = std::str::from_utf8(bytes).map_err(|_| BackupError::ManifestInvalid)?;
        parse_manifest(text)
    }

    /// Renders the complete manifest text, identity line included.
    pub(crate) fn render(&self) -> String {
        let mut lines = vec![HEADER.to_owned(), format!("identity {}", self.identity)];
        lines.extend(self.body_lines());
        let mut text = lines.join("\n");
        text.push('\n');
        text
    }

    /// Renders every manifest line except the identity line, in canonical
    /// order. The identity is the digest of exactly these bytes.
    fn body_lines(&self) -> Vec<String> {
        let mut lines = vec![match self.kind {
            BackupKind::Full => "kind full".to_owned(),
            BackupKind::Snapshot => "kind snapshot".to_owned(),
            BackupKind::Incremental { base } => format!("kind incremental {}", base.get()),
        }];
        lines.push(format!("capture_kind {}", self.capture_kind.as_str()));
        lines.push(format!("database {}", self.database_id));
        lines.push(format!("partition {}", self.partition_id));
        lines.push(format!("tenant_scope {}", self.tenant_scope));
        lines.push(format!("boundary {}", self.boundary.get()));
        lines.push(format!("boundary_unix_millis {}", self.boundary_unix_millis));
        lines.push(format!("captured_unix_millis {}", self.captured_unix_millis));
        lines.push(format!("retain_until_unix_millis {}", self.retain_until_unix_millis));
        lines.push(format!("logical_checksum {}", self.logical_checksum));
        lines.push(format!("logical_documents {}", self.logical_documents));
        lines.push(format!("documents {}", self.documents));
        lines.push(format!("family_records {}", self.family_records));
        lines.push(format!("deletes {}", self.deletes));
        lines.push(format!("disclosures {}", self.disclosures));
        lines.push(format!("total_bytes {}", self.total_bytes));
        for tenant in &self.tenants {
            // The name is length-prefixed in bytes: `tenant <len>:<name> …`.
            // A tenant identifier may carry interior whitespace, colons, and
            // digits, so the space-separated form was ambiguous and a valid
            // name could not be parsed back.
            lines.push(format!(
                "tenant {}:{} {} {}",
                tenant.tenant().as_str().len(),
                tenant.tenant(),
                tenant.documents(),
                tenant.family_records()
            ));
        }
        for member in &self.members {
            lines.push(format!("member {} {} {}", member.name(), member.length(), member.digest()));
        }
        lines
    }
}

/// Returns the recovery-point directory name for one identity.
pub(crate) fn point_directory(identity: &str) -> String {
    format!("rp-{identity}")
}

/// Parses and validates one manifest text into a [`BackupManifest`].
///
/// The parsed identity must equal the digest recomputed over the parsed
/// body, every member name must be the canonical writer form in strictly
/// increasing order, and every tenant name must be a valid tenant
/// identifier. Anything else is [`BackupError::ManifestInvalid`].
#[allow(clippy::too_many_lines)]
pub(crate) fn parse_manifest(text: &str) -> Result<BackupManifest, BackupError> {
    let invalid = || BackupError::ManifestInvalid;
    if text.len() > MAX_MANIFEST_BYTES || text.lines().any(|line| line.len() > MAX_LINE_BYTES) {
        return Err(invalid());
    }
    if !text.ends_with('\n') {
        return Err(invalid());
    }
    // `ends_with('\n')` guarantees exactly one empty tail after the split.
    let mut lines = text.split('\n');
    if lines.next() != Some(HEADER) {
        return Err(invalid());
    }
    let identity =
        lines.next().ok_or_else(invalid)?.strip_prefix("identity ").ok_or_else(invalid)?;
    validate_identity_hex(identity)?;

    let kind = parse_kind(lines.next().ok_or_else(invalid)?)?;
    let capture_kind = BackupCaptureKind::parse(
        lines.next().ok_or_else(invalid)?.strip_prefix("capture_kind ").ok_or_else(invalid)?,
    )?;
    let database_id = parse_database_id(lines.next().ok_or_else(invalid)?)?;
    let partition_id = parse_partition_id(lines.next().ok_or_else(invalid)?)?;
    let tenant_scope = parse_digest(lines.next().ok_or_else(invalid)?, "tenant_scope")?;
    let boundary = parse_number(lines.next().ok_or_else(invalid)?, "boundary")?;
    let boundary_unix_millis =
        parse_number(lines.next().ok_or_else(invalid)?, "boundary_unix_millis")?;
    let captured_unix_millis =
        parse_number(lines.next().ok_or_else(invalid)?, "captured_unix_millis")?;
    let retain_until_unix_millis =
        parse_number(lines.next().ok_or_else(invalid)?, "retain_until_unix_millis")?;
    let retained_millis =
        retain_until_unix_millis.checked_sub(captured_unix_millis).ok_or_else(invalid)?;
    if ![1_u64, 7, 30, 35].into_iter().any(|days| retained_millis == days * 86_400_000) {
        return Err(invalid());
    }
    if captured_unix_millis < boundary_unix_millis {
        return Err(invalid());
    }
    let logical_checksum = parse_digest(lines.next().ok_or_else(invalid)?, "logical_checksum")?;
    let logical_documents = parse_number(lines.next().ok_or_else(invalid)?, "logical_documents")?;
    let documents = parse_number(lines.next().ok_or_else(invalid)?, "documents")?;
    let family_records = parse_number(lines.next().ok_or_else(invalid)?, "family_records")?;
    let deletes = parse_number(lines.next().ok_or_else(invalid)?, "deletes")?;
    let disclosures = parse_number(lines.next().ok_or_else(invalid)?, "disclosures")?;
    let total_bytes = parse_number(lines.next().ok_or_else(invalid)?, "total_bytes")?;

    let rest: Vec<&str> = lines.collect();
    let Some((trailing, body)) = rest.split_last() else {
        return Err(invalid());
    };
    if !trailing.is_empty() || body.iter().any(|line| line.is_empty()) {
        return Err(invalid());
    }
    let mut tenants = Vec::new();
    let mut members = Vec::new();
    let mut in_members = false;
    for line in body {
        if let Some(rest) = line.strip_prefix("member ") {
            if members.len() >= MAX_MEMBERS {
                return Err(invalid());
            }
            in_members = true;
            members.push(parse_member(rest)?);
        } else if let Some(rest) = line.strip_prefix("tenant ") {
            if in_members {
                return Err(invalid());
            }
            if tenants.len() >= MAX_TENANTS {
                return Err(invalid());
            }
            tenants.push(parse_tenant(rest)?);
        } else {
            return Err(invalid());
        }
    }
    for pair in members.windows(2) {
        if member_ordinal(pair[0].name()) >= member_ordinal(pair[1].name()) {
            return Err(invalid());
        }
    }
    for pair in tenants.windows(2) {
        if pair[0].tenant() >= pair[1].tenant() {
            return Err(invalid());
        }
    }
    let documents_sum = tenants.iter().try_fold(0_u64, |sum, tenant| {
        sum.checked_add(tenant.documents()).ok_or(BackupError::ManifestInvalid)
    })?;
    let family_records_sum = tenants.iter().try_fold(0_u64, |sum, tenant| {
        sum.checked_add(tenant.family_records()).ok_or(BackupError::ManifestInvalid)
    })?;
    if documents_sum != documents || family_records_sum != family_records {
        return Err(invalid());
    }
    if tenant_scope != tenant_scope_digest(&tenants) {
        return Err(invalid());
    }
    if let BackupKind::Incremental { base } = kind
        && base.get() > boundary
    {
        return Err(invalid());
    }
    let member_bytes = members.iter().try_fold(0_u64, |sum, member| {
        sum.checked_add(member.length()).ok_or(BackupError::ManifestInvalid)
    })?;
    if member_bytes != total_bytes {
        return Err(invalid());
    }
    let manifest = BackupManifest {
        database_id,
        partition_id,
        tenant_scope,
        identity: identity.to_owned(),
        kind,
        capture_kind,
        boundary: StorageSequence::new(boundary),
        boundary_unix_millis,
        captured_unix_millis,
        retain_until_unix_millis,
        logical_checksum,
        logical_documents,
        documents,
        family_records,
        deletes,
        disclosures,
        tenants,
        members,
        total_bytes,
    };
    // The identity must bind the parsed body exactly.
    if manifest.identity
        != hex::encode(digest(&SHA256, manifest.body_lines().join("\n").as_bytes()).as_ref())
    {
        return Err(invalid());
    }
    // Identity binds the canonical body, so the serialized input must also
    // be exactly the canonical rendering. Without this comparison, lexical
    // aliases such as `boundary 07` parsed as the canonical value `7` and
    // could retain the identity of bytes that were never actually supplied.
    if manifest.render() != text {
        return Err(invalid());
    }
    Ok(manifest)
}

/// Reads one manifest with a hard byte cap, including across a file-size
/// race. Callers that need the original bytes (artifact bundling) can use
/// this instead of an unbounded `read`.
pub(crate) fn read_manifest_bytes(path: &std::path::Path) -> Result<Vec<u8>, BackupError> {
    let file = std::fs::File::open(path).map_err(|_| BackupError::ManifestInvalid)?;
    let mut bytes = Vec::new();
    file.take((MAX_MANIFEST_BYTES + 1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|_| BackupError::ManifestInvalid)?;
    if bytes.len() > MAX_MANIFEST_BYTES {
        return Err(BackupError::ManifestInvalid);
    }
    Ok(bytes)
}

fn parse_kind(line: &str) -> Result<BackupKind, BackupError> {
    match line {
        "kind full" => Ok(BackupKind::Full),
        "kind snapshot" => Ok(BackupKind::Snapshot),
        other => other
            .strip_prefix("kind incremental ")
            .and_then(|base| base.parse::<u64>().ok())
            .map(|base| BackupKind::Incremental { base: StorageSequence::new(base) })
            .ok_or(BackupError::ManifestInvalid),
    }
}

fn parse_database_id(line: &str) -> Result<String, BackupError> {
    let value = line.strip_prefix("database ").ok_or(BackupError::ManifestInvalid)?;
    let legacy_hex = value.len() == 32
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte));
    if DatabaseUlid::from_text(value).is_err() && !legacy_hex {
        return Err(BackupError::ManifestInvalid);
    }
    Ok(value.to_owned())
}

fn parse_partition_id(line: &str) -> Result<String, BackupError> {
    let value = line.strip_prefix("partition ").ok_or(BackupError::ManifestInvalid)?;
    if value != "partition-000000" {
        return Err(BackupError::ManifestInvalid);
    }
    Ok(value.to_owned())
}

fn parse_digest(line: &str, field: &str) -> Result<String, BackupError> {
    let value = line.strip_prefix(&format!("{field} ")).ok_or(BackupError::ManifestInvalid)?;
    validate_identity_hex(value)?;
    Ok(value.to_owned())
}

fn parse_number(line: &str, field: &str) -> Result<u64, BackupError> {
    line.strip_prefix(field)
        .and_then(|rest| rest.strip_prefix(' '))
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or(BackupError::ManifestInvalid)
}

/// Returns the numeric suffix of a canonical member name for ordering.
fn member_ordinal(name: &str) -> u64 {
    name.strip_prefix("member-").and_then(|digits| digits.parse::<u64>().ok()).unwrap_or(u64::MAX)
}

/// Parses `tenant <name-bytes>:<name> <documents> <family_records>`.
///
/// The length prefix is in bytes and names the name exactly, so a name with
/// interior whitespace, colons, or digits round-trips. The name must still be
/// a valid [`TenantId`], which is what keeps the line representable at all:
/// no control characters, no leading or trailing whitespace, at most 255
/// bytes.
fn parse_tenant(rest: &str) -> Result<BackupTenantCount, BackupError> {
    let invalid = BackupError::ManifestInvalid;
    let (length, after_length) = rest.split_once(':').ok_or(invalid)?;
    let length: usize = length.parse().map_err(|_| invalid)?;
    let bytes = after_length.as_bytes();
    if bytes.len() <= length || bytes[length] != b' ' {
        return Err(invalid);
    }
    let name = std::str::from_utf8(&bytes[..length]).map_err(|_| invalid)?;
    let mut parts = after_length[length + 1..].split(' ');
    let documents = parts.next().and_then(|value| value.parse::<u64>().ok()).ok_or(invalid)?;
    let family_records = parts.next().and_then(|value| value.parse::<u64>().ok()).ok_or(invalid)?;
    if parts.next().is_some() {
        return Err(invalid);
    }
    let tenant = TenantId::try_new(name).map_err(|_| invalid)?;
    Ok(BackupTenantCount::new(tenant, documents, family_records))
}

fn parse_member(rest: &str) -> Result<BackupMember, BackupError> {
    let invalid = BackupError::ManifestInvalid;
    let mut parts = rest.split(' ');
    let name = parts.next().ok_or(invalid)?;
    let length = parts.next().and_then(|value| value.parse::<u64>().ok()).ok_or(invalid)?;
    let digest = parts.next().ok_or(invalid)?;
    if parts.next().is_some() {
        return Err(invalid);
    }
    validate_identity_hex(digest)?;
    if !is_canonical_member_name(name) {
        return Err(invalid);
    }
    Ok(BackupMember::new(name.to_owned(), length, digest.to_owned()))
}

/// A member name is canonical only in the writer's form: the `member-`
/// prefix followed by six or more ASCII digits. The form admits no path
/// separator, dot, or traversal text, so a validated manifest can never
/// name a file outside its recovery-point directory.
fn is_canonical_member_name(name: &str) -> bool {
    let Some(digits) = name.strip_prefix("member-") else {
        return false;
    };
    digits.len() >= 6 && digits.bytes().all(|byte| byte.is_ascii_digit())
}

fn validate_identity_hex(text: &str) -> Result<(), BackupError> {
    if text.len() == 64
        && text.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Ok(());
    }
    Err(BackupError::ManifestInvalid)
}

/// Computes the canonical digest of the ordered tenant scope.
fn tenant_scope_digest(tenants: &[BackupTenantCount]) -> String {
    let mut hasher = Context::new(&SHA256);
    for tenant in tenants {
        hasher.update(tenant.tenant().as_str().len().to_string().as_bytes());
        hasher.update(b":");
        hasher.update(tenant.tenant().as_str().as_bytes());
        hasher.update(b"\n");
    }
    hex::encode(hasher.finish().as_ref())
}

/// Validates a caller-supplied recovery-point identity.
pub(crate) fn validate_identity(text: &str) -> Result<(), BackupError> {
    validate_identity_hex(text)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> BackupManifest {
        BackupManifest::assemble(ManifestFacts {
            database_id: "0123456789abcdef0123456789abcdef".to_owned(),
            partition_id: "partition-000000".to_owned(),
            kind: BackupKind::Full,
            capture_kind: BackupCaptureKind::WholeDatabase,
            boundary: StorageSequence::new(7),
            boundary_unix_millis: 700,
            captured_unix_millis: 800,
            retain_until_unix_millis: 86_400_800,
            logical_checksum: "a".repeat(64),
            logical_documents: 1,
            documents: 1,
            family_records: 0,
            deletes: 0,
            disclosures: 0,
            tenants: vec![BackupTenantCount::new(
                TenantId::try_new("tenant-a").expect("tenant"),
                1,
                0,
            )],
            members: Vec::new(),
        })
    }

    fn rebind(mut text: String) -> String {
        let mut lines: Vec<_> = text.split('\n').map(str::to_owned).collect();
        let body = lines[2..lines.len() - 1].join("\n");
        lines[1] = format!("identity {}", hex::encode(digest(&SHA256, body.as_bytes()).as_ref()));
        text = lines.join("\n");
        text
    }

    #[test]
    fn logical_source_facts_are_bound_to_manifest_identity() {
        let manifest = sample();
        assert_eq!(BackupManifest::decode(&manifest.encode()).unwrap(), manifest);
        for changed in [
            manifest.render().replace(
                &format!("logical_checksum {}", "a".repeat(64)),
                &format!("logical_checksum {}", "b".repeat(64)),
            ),
            manifest.render().replace("logical_documents 1", "logical_documents 2"),
        ] {
            assert_eq!(parse_manifest(&changed), Err(BackupError::ManifestInvalid));
        }
    }

    #[test]
    fn capture_kind_roundtrips_and_changes_authenticated_identity() {
        let whole = sample();
        let mut participant = whole.clone();
        participant.capture_kind = BackupCaptureKind::RoutedShardParticipant;
        participant.identity = String::new();
        participant.identity =
            hex::encode(digest(&SHA256, participant.body_lines().join("\n").as_bytes()).as_ref());
        assert_eq!(
            BackupManifest::decode(&whole.encode()).unwrap().capture_kind(),
            BackupCaptureKind::WholeDatabase
        );
        assert_eq!(
            BackupManifest::decode(&participant.encode()).unwrap().capture_kind(),
            BackupCaptureKind::RoutedShardParticipant
        );
        assert_ne!(whole.identity, participant.identity);
    }

    #[test]
    fn capture_kind_is_required_and_strictly_parsed() {
        let encoded = sample().render();
        for replacement in [
            ("capture_kind whole_database\n", ""),
            ("capture_kind whole_database", "capture_kind unknown"),
            ("capture_kind whole_database", "capture_kind WholeDatabase"),
            ("terrabase-backup-manifest 4", "terrabase-backup-manifest 3"),
        ] {
            let candidate = encoded.replacen(replacement.0, replacement.1, 1);
            assert_eq!(
                parse_manifest(&candidate),
                Err(BackupError::ManifestInvalid),
                "replacement {replacement:?}"
            );
        }
    }

    #[test]
    fn retention_deadline_is_bound_and_cannot_be_rewritten_or_shortened() {
        let manifest = sample();
        assert_eq!(manifest.retain_until_unix_millis(), 86_400_800);
        let changed = manifest
            .render()
            .replace("retain_until_unix_millis 86400800", "retain_until_unix_millis 604800800");
        assert_eq!(parse_manifest(&changed), Err(BackupError::ManifestInvalid));
        let too_short = rebind(
            manifest
                .render()
                .replace("retain_until_unix_millis 86400800", "retain_until_unix_millis 799"),
        );
        assert_eq!(parse_manifest(&too_short), Err(BackupError::ManifestInvalid));
        let unsupported_term = rebind(
            manifest
                .render()
                .replace("retain_until_unix_millis 86400800", "retain_until_unix_millis 172800800"),
        );
        assert_eq!(parse_manifest(&unsupported_term), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn rejects_oversized_manifest_before_collecting_lines() {
        let oversized = format!("{}{}", HEADER, "x".repeat(MAX_MANIFEST_BYTES));
        assert_eq!(parse_manifest(&oversized), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn rejects_duplicate_tenants_and_count_scope_mismatch() {
        let manifest = sample();
        let duplicate = format!("{}tenant 8:tenant-a 1 0\n", manifest.render());
        assert_eq!(parse_manifest(&duplicate), Err(BackupError::ManifestInvalid));

        let forged = rebind(manifest.render().replace("documents 1", "documents 2"));
        assert_eq!(parse_manifest(&forged), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn rejects_incremental_frontier_ahead_of_its_boundary() {
        let mut text = sample().render().replace("kind full", "kind incremental 8");
        text = rebind(text);
        assert_eq!(parse_manifest(&text), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn rejects_noncanonical_bytes_even_when_they_normalize_to_the_bound_body() {
        let text = sample().render().replace("boundary 7", "boundary 07");

        assert_eq!(parse_manifest(&text), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn member_matching_requires_both_length_and_digest() {
        let bytes = b"member payload";
        let member = BackupMember::new(
            "member-000000".to_owned(),
            bytes.len() as u64,
            hex::encode(digest(&SHA256, bytes).as_ref()),
        );

        assert!(member.matches_bytes(bytes));
        assert!(!member.matches_bytes(b"member payloaD"));
        assert!(!member.matches_bytes(b"member payload with trailing bytes"));
    }

    #[test]
    fn every_single_byte_mutation_of_a_manifest_is_rejected() {
        let encoded = sample().encode();
        for index in 0..encoded.len() {
            let mut mutated = encoded.clone();
            mutated[index] ^= 1;
            assert_eq!(
                BackupManifest::decode(&mutated),
                Err(BackupError::ManifestInvalid),
                "byte {index}"
            );
        }
    }
}
