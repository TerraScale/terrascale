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

use crate::backup::sha256::{Sha256, to_hex};
use crate::backup::{BackupError, BackupKind};
use crate::types::{StorageSequence, TenantId};

/// The manifest file name inside one recovery-point directory.
pub(crate) const MANIFEST_FILE: &str = "manifest.tbv2m";

/// The manifest format header and version.
///
/// Version 2 length-prefixes the tenant name on the `tenant` line. Version 1
/// rendered the name space-separated, so a valid tenant identifier with
/// interior whitespace (`team a` is committable) produced a manifest that
/// could never be parsed back; version 2 encodes the name's byte length so
/// the line is unambiguous for every representable tenant name.
pub(crate) const HEADER: &str = "terrabase-backup-manifest 2";

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
    /// The backup kind.
    pub(crate) kind: BackupKind,
    /// The storage-sequence boundary the recovery point covers.
    pub(crate) boundary: StorageSequence,
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
    identity: String,
    kind: BackupKind,
    boundary: StorageSequence,
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
            kind,
            boundary,
            documents,
            family_records,
            deletes,
            disclosures,
            tenants,
            members,
        } = facts;
        let total_bytes = members.iter().map(BackupMember::length).sum();
        let mut manifest = Self {
            identity: String::new(),
            kind,
            boundary,
            documents,
            family_records,
            deletes,
            disclosures,
            tenants,
            members,
            total_bytes,
        };
        let mut hasher = Sha256::new();
        hasher.update(&manifest.body_lines().join("\n").into_bytes());
        manifest.identity = to_hex(&hasher.finish());
        manifest
    }

    /// Returns the recovery-point identity: the SHA-256 digest of the
    /// manifest body, as lowercase hex.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
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

    /// Returns the base boundary an incremental manifest builds on, or
    /// `None` for a full or snapshot manifest.
    #[must_use]
    pub fn base_sequence(&self) -> Option<StorageSequence> {
        match self.kind {
            BackupKind::Incremental { base } => Some(base),
            BackupKind::Full | BackupKind::Snapshot => None,
        }
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
        lines.push(format!("boundary {}", self.boundary.get()));
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
pub(crate) fn parse_manifest(text: &str) -> Result<BackupManifest, BackupError> {
    let invalid = || BackupError::ManifestInvalid;
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
    let boundary = parse_number(lines.next().ok_or_else(invalid)?, "boundary")?;
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
            in_members = true;
            members.push(parse_member(rest)?);
        } else if let Some(rest) = line.strip_prefix("tenant ") {
            if in_members {
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
    if members.iter().map(BackupMember::length).sum::<u64>() != total_bytes {
        return Err(invalid());
    }
    let manifest = BackupManifest {
        identity: identity.to_owned(),
        kind,
        boundary: StorageSequence::new(boundary),
        documents,
        family_records,
        deletes,
        disclosures,
        tenants,
        members,
        total_bytes,
    };
    // The identity must bind the parsed body exactly.
    let mut hasher = Sha256::new();
    hasher.update(&manifest.body_lines().join("\n").into_bytes());
    if manifest.identity != to_hex(&hasher.finish()) {
        return Err(invalid());
    }
    Ok(manifest)
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

/// Validates a caller-supplied recovery-point identity.
pub(crate) fn validate_identity(text: &str) -> Result<(), BackupError> {
    validate_identity_hex(text)
}
