//! Immutable, operator-signed authority for one distributed shard.
//!
//! This module is intentionally independent of transport claims. The caller
//! supplies the leaf DER only after the TLS stack has authenticated its chain.

use std::error::Error;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use aws_lc_rs::signature::{ED25519, UnparsedPublicKey};
use fs2::FileExt;
use sha2::{Digest, Sha256};
use tb_core::{
    AdmissionError, Attribution, CollectionId, DatabaseId, MAX_VALUE_BYTES, MembershipEpoch,
    MemoryBudget, MemoryCategory, OriginId, ReplicaId, ShardId, TenantId, WorkReservation,
};
use tb_format::{FormatError, decode_catalog};
use tb_storage::{StorageConfig, StorageError};

use crate::cursor::{CursorError, CursorLimits, CursorScope};
use crate::event::{CollectionBinding, ConfigurationId, EventDigest, EventId};
use crate::journal::{JournalError, OwnedDurableGroup, ReplicaJournal};
use crate::metadata::{VerifiedIncomingGroup, VerifiedSourceChange};

const CERT_MAGIC: &[u8; 8] = b"TBCAU001";
const CERT_MAGIC_V2: &[u8; 8] = b"TBCAU002";
const RECORD_MAGIC: &[u8; 8] = b"TBACR001";
const SIGN_DOMAIN: &[u8] = b"TB-AUTHORITY-FIXED-V1\0";
const SIGN_DOMAIN_V2: &[u8] = b"TB-AUTHORITY-FIXED-V2\0";
const CONFIG_DOMAIN: &[u8] = b"TB-CONFIG-FIXED-V1\0";
const CONFIG_DOMAIN_V2: &[u8] = b"TB-CONFIG-FIXED-V2\0";
const MAX_PAYLOAD: usize = 4 * 1024 * 1024;
const SIGNATURE_LEN: usize = 64;
const CERT_HEADER_LEN: usize = 8 + 4 + 2 + 2 + 16 + 16 + 16 + 8 + 8 + 2 + 2;
const CERT_HEADER_LEN_V2: usize = CERT_HEADER_LEN + 2;
const ACTIVE_ROW_LEN: usize = 16 + 8 + 32;
const RECORD_HEADER_LEN: usize = 8 + 4 + 32 + 32 + 4;
const RECORD_TRAILER_LEN: usize = 4;
static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);
static VIEW_SEQUENCE: AtomicU64 = AtomicU64::new(1);
static RECEIVER_SEQUENCE: AtomicU64 = AtomicU64::new(1);

/// A strictly scoped expected local identity, configured independently of the certificate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LocalAuthorityScope {
    pub cluster: [u8; 16],
    pub tenant: TenantId,
    pub database: DatabaseId,
    pub shard: ShardId,
    pub member: ReplicaId,
}

/// Fixed scope encoded into one operator-signed authority certificate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AuthorityCertificateScope {
    pub cluster: [u8; 16],
    pub tenant: TenantId,
    pub database: DatabaseId,
    pub shard: ShardId,
    pub epoch: MembershipEpoch,
}

/// One member of a fixed authority certificate. The exact TLS leaf DER is
/// hashed into the signed row; it is not stored in the certificate.
pub struct AuthorityMemberInput<'a> {
    pub origin: OriginId,
    pub leaf_der: &'a [u8],
    pub server_name: &'a str,
    pub failure_domain: &'a str,
}

/// One complete catalog definition retained by a fixed authority certificate.
pub struct AuthoritySchemaInput<'a> {
    pub binding: CollectionBinding,
    pub catalog: &'a [u8],
}

/// Validated canonical v1 or v2 bytes awaiting an offline operator signature.
/// The signing key remains entirely outside this crate.
pub struct AuthorityPayload {
    bytes: Vec<u8>,
    configuration: ConfigurationId,
    local_scope: LocalAuthorityScope,
    version: AuthorityVersion,
}

/// Signed authority certificate grammar and configuration domain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthorityVersion {
    V1,
    V2,
}

impl AuthorityVersion {
    const fn signing_domain(self) -> &'static [u8] {
        match self {
            Self::V1 => SIGN_DOMAIN,
            Self::V2 => SIGN_DOMAIN_V2,
        }
    }

    const fn configuration_domain(self) -> &'static [u8] {
        match self {
            Self::V1 => CONFIG_DOMAIN,
            Self::V2 => CONFIG_DOMAIN_V2,
        }
    }
}

/// The fields asserted in Hello. They remain untrusted until checked here.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PeerHelloScope {
    pub cluster: [u8; 16],
    pub tenant: TenantId,
    pub database: DatabaseId,
    pub shard: ShardId,
    pub origin: OriginId,
    pub epoch: MembershipEpoch,
    pub configuration: ConfigurationId,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct AuthorityBinding {
    cluster: [u8; 16],
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    trusted_key_sha256: [u8; 32],
}

struct MemberRow {
    origin: OriginId,
    leaf_sha256: [u8; 32],
    failure_domain: Box<str>,
    server_name: Box<str>,
}

struct SchemaRow {
    binding: CollectionBinding,
    catalog_start: usize,
    catalog_end: usize,
}

/// Read-only signed member details exposed for distributed startup.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CertifiedMemberInfo<'a> {
    pub origin: OriginId,
    pub leaf_sha256: [u8; 32],
    pub failure_domain: &'a str,
    pub server_name: &'a str,
}

/// Recovered, immutable membership and complete catalog definitions.
pub struct CertifiedShardView {
    binding: AuthorityBinding,
    version: AuthorityVersion,
    local_origin: OriginId,
    instance: u64,
    data_dir: PathBuf,
    certificate: Vec<u8>,
    members: Vec<MemberRow>,
    schemas: Vec<SchemaRow>,
    active: Vec<CollectionBinding>,
}

/// Process-lifetime lock and budget ownership for accepted authority.
pub struct AcceptedAuthority {
    view: CertifiedShardView,
    _lock: File,
    _permit: WorkReservation,
}

/// A peer admitted from an authenticated TLS leaf and matching Hello identity.
pub struct CertifiedMember<'v> {
    view: &'v CertifiedShardView,
    origin: OriginId,
}

/// A single semantically verified event admitted by the same authority view.
pub struct CertifiedEvent<'v, 'p, 'b> {
    view: &'v CertifiedShardView,
    proof: &'p VerifiedIncomingGroup<'b>,
    id: EventId,
    digest: EventDigest,
}

/// Read-only replay authority for one exact, contiguous durable inbox group.
/// It cannot enter the live `append_certified` path or issue an ACK.
pub struct CertifiedReplayEvent<'r, 'v, 'g, 'p, 'b> {
    _receiver: &'r ReplicaReceiver,
    _view: &'v CertifiedShardView,
    group: &'g OwnedDurableGroup,
    proof: &'p VerifiedIncomingGroup<'b>,
}

/// The synced contiguous prefix of one origin. This is not an applied or client ACK.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DurablePrefix {
    pub sequence: u64,
    pub epoch: MembershipEpoch,
    pub configuration: ConfigurationId,
    pub origin: OriginId,
}

/// The only public path from a certified event into the private inbox journal.
pub struct ReplicaReceiver {
    view_instance: u64,
    open_token: u64,
    binding: AuthorityBinding,
    scope: CursorScope,
    journal: ReplicaJournal,
}

#[derive(Debug)]
pub enum AuthorityError {
    Invalid(&'static str),
    ScopeMismatch,
    UnauthenticatedMember,
    UnknownSchema,
    ExistingDirectoryWithoutAuthority,
    RecordMismatch,
    LockUnavailable,
    Io(std::io::Error),
    Admission(AdmissionError),
    Format(FormatError),
}

impl fmt::Display for AuthorityError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}
impl Error for AuthorityError {}
impl From<std::io::Error> for AuthorityError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}
impl From<AdmissionError> for AuthorityError {
    fn from(error: AdmissionError) -> Self {
        Self::Admission(error)
    }
}
impl From<FormatError> for AuthorityError {
    fn from(error: FormatError) -> Self {
        Self::Format(error)
    }
}

#[derive(Debug)]
pub enum ReceiveError {
    Authority(AuthorityError),
    Journal(String),
    /// A live append classified after the journal's write-phase state is known.
    Append {
        kind: AppendFailureKind,
        detail: String,
    },
    /// Provenance of a bounded exact read from a recovered durable inbox.
    DurableRead {
        kind: DurableReadKind,
        detail: String,
    },
}

/// Whether a failed exact inbox read indicates damaged state or unavailable
/// resources. No variant asserts an applied or network acknowledgment.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DurableReadKind {
    StoredFault,
    NeedsRecovery,
    Unavailable,
}

/// Provenance of a failed certified append. A journal in `NeedsRecovery` always
/// takes precedence over the underlying error returned by its write phase.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AppendFailureKind {
    IncomingConflict,
    Unavailable,
    StoredFault,
    NeedsRecovery,
}

fn append_failure_kind(error: &JournalError, needs_recovery: bool) -> AppendFailureKind {
    if needs_recovery {
        return AppendFailureKind::NeedsRecovery;
    }
    match error {
        JournalError::ConflictingIdentity
        | JournalError::Cursor(CursorError::ConflictingIdentity) => {
            AppendFailureKind::IncomingConflict
        }
        JournalError::Capacity(_)
        | JournalError::Admission(_)
        | JournalError::Storage(
            StorageError::Capacity(_)
            | StorageError::Admission(_)
            | StorageError::Io(_)
            | StorageError::Format(FormatError::Allocation)
            | StorageError::Core(tb_core::CoreError::CapacityExceeded { .. }),
        )
        | JournalError::Format(FormatError::Allocation)
        | JournalError::Core(tb_core::CoreError::CapacityExceeded { .. })
        | JournalError::Cursor(CursorError::GapBeyondWindow | CursorError::PendingFull) => {
            AppendFailureKind::Unavailable
        }
        JournalError::NeedsRecovery | JournalError::Storage(StorageError::Poisoned) => {
            AppendFailureKind::NeedsRecovery
        }
        _ => AppendFailureKind::StoredFault,
    }
}

fn durable_read_kind(error: &JournalError) -> DurableReadKind {
    match error {
        JournalError::NeedsRecovery | JournalError::Storage(StorageError::Poisoned) => {
            DurableReadKind::NeedsRecovery
        }
        JournalError::Capacity(_)
        | JournalError::Admission(_)
        | JournalError::Storage(
            StorageError::Capacity(_)
            | StorageError::Admission(_)
            | StorageError::Io(_)
            | StorageError::Format(FormatError::Allocation)
            | StorageError::Core(tb_core::CoreError::CapacityExceeded { .. }),
        )
        | JournalError::Format(FormatError::Allocation)
        | JournalError::Core(tb_core::CoreError::CapacityExceeded { .. }) => {
            DurableReadKind::Unavailable
        }
        _ => DurableReadKind::StoredFault,
    }
}
impl fmt::Display for ReceiveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}
impl Error for ReceiveError {}
impl From<AuthorityError> for ReceiveError {
    fn from(error: AuthorityError) -> Self {
        Self::Authority(error)
    }
}
impl From<JournalError> for ReceiveError {
    fn from(error: JournalError) -> Self {
        Self::Journal(error.to_string())
    }
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}

fn configuration_id(
    version: AuthorityVersion,
    payload: &[u8],
) -> Result<ConfigurationId, AuthorityError> {
    let mut configured = Sha256::new();
    configured.update(version.configuration_domain());
    configured.update(payload);
    ConfigurationId::from_bytes(configured.finalize().into())
        .map_err(|_| AuthorityError::Invalid("zero configuration digest"))
}

fn take<'a>(bytes: &'a [u8], cursor: &mut usize, count: usize) -> Result<&'a [u8], AuthorityError> {
    let end = cursor
        .checked_add(count)
        .ok_or(AuthorityError::Invalid("length overflow"))?;
    let value = bytes
        .get(*cursor..end)
        .ok_or(AuthorityError::Invalid("truncated authority"))?;
    *cursor = end;
    Ok(value)
}
fn array<const N: usize>(bytes: &[u8], cursor: &mut usize) -> Result<[u8; N], AuthorityError> {
    Ok(take(bytes, cursor, N)?
        .try_into()
        .expect("checked fixed width"))
}
fn u16_le(bytes: &[u8], cursor: &mut usize) -> Result<u16, AuthorityError> {
    Ok(u16::from_le_bytes(array(bytes, cursor)?))
}
fn u32_le(bytes: &[u8], cursor: &mut usize) -> Result<u32, AuthorityError> {
    Ok(u32::from_le_bytes(array(bytes, cursor)?))
}
fn u64_le(bytes: &[u8], cursor: &mut usize) -> Result<u64, AuthorityError> {
    Ok(u64::from_le_bytes(array(bytes, cursor)?))
}

fn valid_server_name(bytes: &[u8]) -> bool {
    if !(1..=253).contains(&bytes.len()) || bytes.contains(&b'*') {
        return false;
    }
    bytes.split(|&byte| byte == b'.').all(|label| {
        (1..=63).contains(&label.len())
            && (label[0].is_ascii_lowercase() || label[0].is_ascii_digit())
            && (label[label.len() - 1].is_ascii_lowercase()
                || label[label.len() - 1].is_ascii_digit())
            && label
                .iter()
                .all(|&byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
    })
}

fn valid_failure_domain(bytes: &[u8]) -> bool {
    (1..=64).contains(&bytes.len())
        && bytes
            .iter()
            .all(|&byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

impl AuthorityPayload {
    /// Encode a canonical fixed-scope authority payload for offline signing.
    /// Members must be sorted by ID; schemas by `(collection, generation)`.
    /// Catalog bytes are decoded and checked against their stated binding.
    ///
    /// # Errors
    /// Rejects invalid scope, ordering, duplicates, names, catalogs, or bounds.
    #[allow(
        clippy::too_many_lines,
        reason = "canonical field validation and emission share one ordered pass over the fixed certificate grammar"
    )]
    pub fn encode(
        scope: AuthorityCertificateScope,
        members: &[AuthorityMemberInput<'_>],
        schemas: &[AuthoritySchemaInput<'_>],
    ) -> Result<Self, AuthorityError> {
        if scope.cluster == [0; 16]
            || scope.tenant.to_bytes() == [0; 16]
            || scope.database.to_bytes() == [0; 16]
            || scope.epoch.get() == 0
            || !(2..=64).contains(&members.len())
            || !(1..=1024).contains(&schemas.len())
        {
            return Err(AuthorityError::Invalid("certificate scope or counts"));
        }
        let mut payload_len = CERT_HEADER_LEN;
        let mut previous_member = None;
        let mut fingerprints = std::collections::BTreeSet::new();
        let mut server_names = std::collections::BTreeSet::new();
        for member in members {
            if member.leaf_der.is_empty() || member.leaf_der.len() > MAX_VALUE_BYTES {
                return Err(AuthorityError::Invalid("member leaf DER length"));
            }
            let member_id = member.origin.replica_id.to_bytes();
            let fingerprint = sha256(member.leaf_der);
            let name = member.server_name.as_bytes();
            let domain = member.failure_domain.as_bytes();
            if member_id == [0; 16]
                || member.origin.incarnation == [0; 16]
                || fingerprint == [0; 32]
                || previous_member.is_some_and(|old| old >= member_id)
                || !fingerprints.insert(fingerprint)
                || !server_names.insert(name)
                || !valid_server_name(name)
                || !valid_failure_domain(domain)
            {
                return Err(AuthorityError::Invalid("member row"));
            }
            previous_member = Some(member_id);
            payload_len = payload_len
                .checked_add(66 + name.len() + domain.len())
                .ok_or(AuthorityError::Invalid("certificate length"))?;
        }
        let mut previous_schema = None;
        for schema in schemas {
            let binding = schema.binding;
            let collection = binding.collection.to_bytes();
            if collection == [0; 16]
                || binding.generation == 0
                || binding.schema_digest == [0; 32]
                || schema.catalog.is_empty()
                || schema.catalog.len() > MAX_VALUE_BYTES
                || previous_schema.is_some_and(|old| old >= (collection, binding.generation))
            {
                return Err(AuthorityError::Invalid("schema row"));
            }
            let decoded = decode_catalog(schema.catalog)?;
            if decoded.collection != binding.collection
                || decoded.generation != binding.generation
                || decoded.digest != binding.schema_digest
            {
                return Err(AuthorityError::Invalid("catalog binding mismatch"));
            }
            previous_schema = Some((collection, binding.generation));
            payload_len = payload_len
                .checked_add(60)
                .and_then(|len| len.checked_add(schema.catalog.len()))
                .ok_or(AuthorityError::Invalid("certificate length"))?;
        }
        if payload_len > MAX_PAYLOAD {
            return Err(AuthorityError::Invalid("certificate length"));
        }
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(payload_len)
            .map_err(|_| AuthorityError::Invalid("certificate allocation"))?;
        bytes.extend_from_slice(CERT_MAGIC);
        bytes.extend_from_slice(
            &u32::try_from(payload_len)
                .map_err(|_| AuthorityError::Invalid("certificate length"))?
                .to_le_bytes(),
        );
        bytes.extend_from_slice(&1_u16.to_le_bytes());
        bytes.extend_from_slice(&0_u16.to_le_bytes());
        bytes.extend_from_slice(&scope.cluster);
        bytes.extend_from_slice(&scope.tenant.to_bytes());
        bytes.extend_from_slice(&scope.database.to_bytes());
        bytes.extend_from_slice(&scope.shard.get().to_le_bytes());
        bytes.extend_from_slice(&scope.epoch.get().to_le_bytes());
        bytes.extend_from_slice(
            &u16::try_from(members.len())
                .map_err(|_| AuthorityError::Invalid("member count"))?
                .to_le_bytes(),
        );
        bytes.extend_from_slice(
            &u16::try_from(schemas.len())
                .map_err(|_| AuthorityError::Invalid("schema count"))?
                .to_le_bytes(),
        );
        for member in members {
            let name = member.server_name.as_bytes();
            let domain = member.failure_domain.as_bytes();
            bytes.extend_from_slice(&member.origin.replica_id.to_bytes());
            bytes.extend_from_slice(&member.origin.incarnation);
            bytes.extend_from_slice(&sha256(member.leaf_der));
            bytes.push(
                u8::try_from(name.len())
                    .map_err(|_| AuthorityError::Invalid("server name length"))?,
            );
            bytes.extend_from_slice(name);
            bytes.push(
                u8::try_from(domain.len())
                    .map_err(|_| AuthorityError::Invalid("failure domain length"))?,
            );
            bytes.extend_from_slice(domain);
        }
        for schema in schemas {
            bytes.extend_from_slice(&schema.binding.collection.to_bytes());
            bytes.extend_from_slice(&schema.binding.generation.to_le_bytes());
            bytes.extend_from_slice(&schema.binding.schema_digest);
            bytes.extend_from_slice(
                &u32::try_from(schema.catalog.len())
                    .map_err(|_| AuthorityError::Invalid("catalog length"))?
                    .to_le_bytes(),
            );
            bytes.extend_from_slice(schema.catalog);
        }
        debug_assert_eq!(bytes.len(), payload_len);
        let configuration = configuration_id(AuthorityVersion::V1, &bytes)?;
        Ok(Self {
            bytes,
            configuration,
            local_scope: LocalAuthorityScope {
                cluster: scope.cluster,
                tenant: scope.tenant,
                database: scope.database,
                shard: scope.shard,
                member: members[0].origin.replica_id,
            },
            version: AuthorityVersion::V1,
        })
    }

    /// Encode a v2 authority with one signed active catalog binding for every
    /// certified collection. Catalog rows may retain older generations.
    ///
    /// # Errors
    /// Rejects missing, duplicate, unordered, or unmatched active bindings.
    pub fn encode_v2(
        scope: AuthorityCertificateScope,
        members: &[AuthorityMemberInput<'_>],
        schemas: &[AuthoritySchemaInput<'_>],
        active: &[CollectionBinding],
    ) -> Result<Self, AuthorityError> {
        let base = Self::encode(scope, members, schemas)?;
        let mut distinct_collections = 0;
        let mut previous_schema_collection = None;
        for schema in schemas {
            let collection = schema.binding.collection.to_bytes();
            if previous_schema_collection != Some(collection) {
                distinct_collections += 1;
                previous_schema_collection = Some(collection);
            }
        }
        if active.len() != distinct_collections || !(1..=1024).contains(&active.len()) {
            return Err(AuthorityError::Invalid("active binding count"));
        }
        let mut previous_active = None;
        for binding in active {
            let collection = binding.collection.to_bytes();
            if previous_active.is_some_and(|old| old >= collection)
                || !schemas.iter().any(|row| row.binding == *binding)
            {
                return Err(AuthorityError::Invalid("active binding row"));
            }
            previous_active = Some(collection);
        }
        let payload_len = base
            .bytes
            .len()
            .checked_add(2)
            .and_then(|len| len.checked_add(active.len() * ACTIVE_ROW_LEN))
            .ok_or(AuthorityError::Invalid("certificate length"))?;
        if payload_len > MAX_PAYLOAD {
            return Err(AuthorityError::Invalid("certificate length"));
        }
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(payload_len)
            .map_err(|_| AuthorityError::Invalid("certificate allocation"))?;
        bytes.extend_from_slice(&base.bytes[..CERT_HEADER_LEN]);
        bytes.extend_from_slice(
            &u16::try_from(active.len())
                .map_err(|_| AuthorityError::Invalid("active binding count"))?
                .to_le_bytes(),
        );
        bytes.extend_from_slice(&base.bytes[CERT_HEADER_LEN..]);
        for binding in active {
            bytes.extend_from_slice(&binding.collection.to_bytes());
            bytes.extend_from_slice(&binding.generation.to_le_bytes());
            bytes.extend_from_slice(&binding.schema_digest);
        }
        bytes[..8].copy_from_slice(CERT_MAGIC_V2);
        bytes[8..12].copy_from_slice(
            &u32::try_from(payload_len)
                .map_err(|_| AuthorityError::Invalid("certificate length"))?
                .to_le_bytes(),
        );
        bytes[12..14].copy_from_slice(&2_u16.to_le_bytes());
        debug_assert_eq!(bytes.len(), payload_len);
        Ok(Self {
            configuration: configuration_id(AuthorityVersion::V2, &bytes)?,
            bytes,
            local_scope: base.local_scope,
            version: AuthorityVersion::V2,
        })
    }

    /// The configuration ID derived from the canonical unsigned payload.
    #[must_use]
    pub const fn configuration(&self) -> ConfigurationId {
        self.configuration
    }

    /// Exact domain-separated bytes to sign with an offline Ed25519 key.
    ///
    /// # Errors
    /// Returns an error if allocation of the bounded signing input fails.
    pub fn signing_input(&self) -> Result<Vec<u8>, AuthorityError> {
        let domain = self.version.signing_domain();
        let mut signed = Vec::new();
        signed
            .try_reserve_exact(domain.len() + self.bytes.len())
            .map_err(|_| AuthorityError::Invalid("signature allocation"))?;
        signed.extend_from_slice(domain);
        signed.extend_from_slice(&self.bytes);
        Ok(signed)
    }

    /// Attach an offline signature, then validate the complete certificate
    /// through the same decoder used by `AcceptedAuthority::open_or_install`.
    ///
    /// # Errors
    /// Rejects a signature or public key that does not authenticate this payload.
    pub fn finish(
        self,
        signature: &[u8; 64],
        trusted_public_key: &[u8; 32],
    ) -> Result<Vec<u8>, AuthorityError> {
        let mut certificate = self.bytes;
        certificate
            .try_reserve_exact(SIGNATURE_LEN)
            .map_err(|_| AuthorityError::Invalid("certificate allocation"))?;
        certificate.extend_from_slice(signature);
        let view = parse_certificate(
            &certificate,
            trusted_public_key,
            self.local_scope,
            PathBuf::new(),
        )?;
        if view.configuration() != self.configuration {
            return Err(AuthorityError::Invalid("configuration mismatch"));
        }
        Ok(certificate)
    }
}

#[allow(
    clippy::too_many_lines,
    reason = "the signed certificate grammar is parsed in one ordered pass so offsets and canonical checks remain adjacent"
)]
fn parse_certificate(
    certificate: &[u8],
    trusted_key: &[u8; 32],
    expected: LocalAuthorityScope,
    data_dir: PathBuf,
) -> Result<CertifiedShardView, AuthorityError> {
    if certificate.len() < CERT_HEADER_LEN + SIGNATURE_LEN
        || certificate.len() > MAX_PAYLOAD + SIGNATURE_LEN
    {
        return Err(AuthorityError::Invalid("certificate length"));
    }
    let payload_len = certificate.len() - SIGNATURE_LEN;
    let (payload, signature) = certificate.split_at(payload_len);
    let mut cursor = 0;
    let magic = take(payload, &mut cursor, 8)?;
    if usize::try_from(u32_le(payload, &mut cursor)?).ok() != Some(payload_len) {
        return Err(AuthorityError::Invalid("noncanonical payload length"));
    }
    let wire_version = u16_le(payload, &mut cursor)?;
    let version = if magic == CERT_MAGIC && wire_version == 1 {
        AuthorityVersion::V1
    } else if magic == CERT_MAGIC_V2 && wire_version == 2 {
        AuthorityVersion::V2
    } else {
        return Err(AuthorityError::Invalid("certificate magic or version"));
    };
    if u16_le(payload, &mut cursor)? != 0 {
        return Err(AuthorityError::Invalid("certificate version or flags"));
    }
    let cluster: [u8; 16] = array(payload, &mut cursor)?;
    let tenant_bytes: [u8; 16] = array(payload, &mut cursor)?;
    let database_bytes: [u8; 16] = array(payload, &mut cursor)?;
    let shard = ShardId::new(u64_le(payload, &mut cursor)?);
    let epoch = MembershipEpoch::new(u64_le(payload, &mut cursor)?);
    let member_count = usize::from(u16_le(payload, &mut cursor)?);
    let schema_count = usize::from(u16_le(payload, &mut cursor)?);
    let active_count = if version == AuthorityVersion::V2 {
        usize::from(u16_le(payload, &mut cursor)?)
    } else {
        0
    };
    if cluster == [0; 16]
        || tenant_bytes == [0; 16]
        || database_bytes == [0; 16]
        || epoch.get() == 0
        || !(2..=64).contains(&member_count)
        || !(1..=1024).contains(&schema_count)
        || (version == AuthorityVersion::V2 && !(1..=1024).contains(&active_count))
    {
        return Err(AuthorityError::Invalid("certificate scope or counts"));
    }
    let tenant = TenantId::from_bytes(tenant_bytes);
    let database = DatabaseId::from_bytes(database_bytes);
    if expected.cluster != cluster
        || expected.tenant != tenant
        || expected.database != database
        || expected.shard != shard
        || expected.member.to_bytes() == [0; 16]
    {
        return Err(AuthorityError::ScopeMismatch);
    }
    let mut signed = Vec::new();
    let sign_domain = version.signing_domain();
    signed
        .try_reserve_exact(sign_domain.len() + payload_len)
        .map_err(|_| AuthorityError::Invalid("signature allocation"))?;
    signed.extend_from_slice(sign_domain);
    signed.extend_from_slice(payload);
    UnparsedPublicKey::new(&ED25519, trusted_key)
        .verify(&signed, signature)
        .map_err(|_| AuthorityError::Invalid("certificate signature or trust key"))?;
    let configuration = configuration_id(version, payload)?;
    let mut members = Vec::new();
    members
        .try_reserve_exact(member_count)
        .map_err(|_| AuthorityError::Invalid("member allocation"))?;
    let mut previous_member = None;
    let mut fingerprints = std::collections::BTreeSet::new();
    let mut server_names = std::collections::BTreeSet::new();
    let mut local_origin = None;
    for _ in 0..member_count {
        let member_bytes: [u8; 16] = array(payload, &mut cursor)?;
        let incarnation: [u8; 16] = array(payload, &mut cursor)?;
        let fingerprint: [u8; 32] = array(payload, &mut cursor)?;
        let name_len = usize::from(take(payload, &mut cursor, 1)?[0]);
        let server_name = take(payload, &mut cursor, name_len)?;
        let len = usize::from(take(payload, &mut cursor, 1)?[0]);
        let domain = take(payload, &mut cursor, len)?;
        if member_bytes == [0; 16]
            || incarnation == [0; 16]
            || fingerprint == [0; 32]
            || previous_member.is_some_and(|old| old >= member_bytes)
            || !fingerprints.insert(fingerprint)
            || !server_names.insert(server_name)
            || !(1..=64).contains(&len)
            || !valid_server_name(server_name)
            || !valid_failure_domain(domain)
        {
            return Err(AuthorityError::Invalid("member row"));
        }
        previous_member = Some(member_bytes);
        let replica_id = ReplicaId::from_bytes(member_bytes);
        let origin = OriginId {
            replica_id,
            incarnation,
        };
        if replica_id == expected.member {
            local_origin = Some(origin);
        }
        members.push(MemberRow {
            origin,
            leaf_sha256: fingerprint,
            failure_domain: std::str::from_utf8(domain).expect("ASCII checked").into(),
            server_name: std::str::from_utf8(server_name)
                .expect("ASCII checked")
                .into(),
        });
    }
    let local_origin = local_origin.ok_or(AuthorityError::ScopeMismatch)?;
    let mut schemas = Vec::new();
    schemas
        .try_reserve_exact(schema_count)
        .map_err(|_| AuthorityError::Invalid("schema allocation"))?;
    let mut previous_schema = None;
    for _ in 0..schema_count {
        let collection_bytes: [u8; 16] = array(payload, &mut cursor)?;
        let generation = u64_le(payload, &mut cursor)?;
        let digest: [u8; 32] = array(payload, &mut cursor)?;
        let catalog_len = usize::try_from(u32_le(payload, &mut cursor)?)
            .map_err(|_| AuthorityError::Invalid("catalog length"))?;
        if collection_bytes == [0; 16]
            || generation == 0
            || digest == [0; 32]
            || catalog_len == 0
            || catalog_len > MAX_VALUE_BYTES
            || previous_schema.is_some_and(|old| old >= (collection_bytes, generation))
        {
            return Err(AuthorityError::Invalid("schema row"));
        }
        let start = cursor;
        let catalog = take(payload, &mut cursor, catalog_len)?;
        let decoded = decode_catalog(catalog)?;
        let collection = CollectionId::from_bytes(collection_bytes);
        if decoded.collection != collection
            || decoded.generation != generation
            || decoded.digest != digest
        {
            return Err(AuthorityError::Invalid("catalog binding mismatch"));
        }
        previous_schema = Some((collection_bytes, generation));
        schemas.push(SchemaRow {
            binding: CollectionBinding {
                collection,
                generation,
                schema_digest: digest,
            },
            catalog_start: start,
            catalog_end: cursor,
        });
    }
    let mut active = Vec::new();
    active
        .try_reserve_exact(active_count)
        .map_err(|_| AuthorityError::Invalid("active allocation"))?;
    let mut previous_active = None;
    for _ in 0..active_count {
        let collection_bytes: [u8; 16] = array(payload, &mut cursor)?;
        let generation = u64_le(payload, &mut cursor)?;
        let schema_digest: [u8; 32] = array(payload, &mut cursor)?;
        let binding = CollectionBinding {
            collection: CollectionId::from_bytes(collection_bytes),
            generation,
            schema_digest,
        };
        if collection_bytes == [0; 16]
            || generation == 0
            || schema_digest == [0; 32]
            || previous_active.is_some_and(|old| old >= collection_bytes)
            || !schemas.iter().any(|row| row.binding == binding)
        {
            return Err(AuthorityError::Invalid("active binding row"));
        }
        previous_active = Some(collection_bytes);
        active.push(binding);
    }
    if version == AuthorityVersion::V2 {
        let distinct_collections = schemas
            .iter()
            .enumerate()
            .filter(|(index, row)| {
                *index == 0 || schemas[*index - 1].binding.collection != row.binding.collection
            })
            .count();
        if active.len() != distinct_collections {
            return Err(AuthorityError::Invalid("active binding count"));
        }
    }
    if cursor != payload_len {
        return Err(AuthorityError::Invalid("certificate trailing bytes"));
    }
    Ok(CertifiedShardView {
        binding: AuthorityBinding {
            cluster,
            tenant,
            database,
            shard,
            epoch,
            configuration,
            trusted_key_sha256: sha256(trusted_key),
        },
        version,
        local_origin,
        instance: VIEW_SEQUENCE.fetch_add(1, Ordering::Relaxed),
        data_dir,
        certificate: certificate.to_vec(),
        members,
        schemas,
        active,
    })
}

fn encode_record(view: &CertifiedShardView) -> Result<Vec<u8>, AuthorityError> {
    let len = RECORD_HEADER_LEN
        .checked_add(view.certificate.len())
        .and_then(|n| n.checked_add(RECORD_TRAILER_LEN))
        .ok_or(AuthorityError::Invalid("record length overflow"))?;
    let mut record = Vec::new();
    record
        .try_reserve_exact(len)
        .map_err(|_| AuthorityError::Invalid("record allocation"))?;
    record.extend_from_slice(RECORD_MAGIC);
    record.extend_from_slice(
        &u32::try_from(len)
            .map_err(|_| AuthorityError::Invalid("record length"))?
            .to_le_bytes(),
    );
    record.extend_from_slice(&view.binding.trusted_key_sha256);
    record.extend_from_slice(&view.binding.configuration.to_bytes());
    record.extend_from_slice(
        &u32::try_from(view.certificate.len())
            .map_err(|_| AuthorityError::Invalid("certificate length"))?
            .to_le_bytes(),
    );
    record.extend_from_slice(&view.certificate);
    record.extend_from_slice(&crc32c::crc32c(&record).to_le_bytes());
    Ok(record)
}

fn validate_record(bytes: &[u8], expected: &[u8]) -> Result<(), AuthorityError> {
    if bytes.len() < RECORD_HEADER_LEN + CERT_HEADER_LEN + SIGNATURE_LEN + RECORD_TRAILER_LEN
        || bytes.len() > RECORD_HEADER_LEN + MAX_PAYLOAD + SIGNATURE_LEN + RECORD_TRAILER_LEN
    {
        return Err(AuthorityError::Invalid("record length"));
    }
    let mut cursor = 0;
    if take(bytes, &mut cursor, 8)? != RECORD_MAGIC {
        return Err(AuthorityError::Invalid("record magic"));
    }
    if usize::try_from(u32_le(bytes, &mut cursor)?).ok() != Some(bytes.len()) {
        return Err(AuthorityError::Invalid("record total length"));
    }
    take(bytes, &mut cursor, 64)?;
    let certificate_len = usize::try_from(u32_le(bytes, &mut cursor)?)
        .map_err(|_| AuthorityError::Invalid("record certificate length"))?;
    if certificate_len != bytes.len() - RECORD_HEADER_LEN - RECORD_TRAILER_LEN {
        return Err(AuthorityError::Invalid("record certificate length"));
    }
    take(bytes, &mut cursor, certificate_len)?;
    let stored_crc = u32_le(bytes, &mut cursor)?;
    if cursor != bytes.len() || crc32c::crc32c(&bytes[..bytes.len() - 4]) != stored_crc {
        return Err(AuthorityError::Invalid("record checksum"));
    }
    if bytes != expected {
        return Err(AuthorityError::RecordMismatch);
    }
    Ok(())
}

fn read_bounded(path: &Path, maximum: usize) -> Result<Vec<u8>, AuthorityError> {
    let mut file = File::open(path)?;
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take(u64::try_from(maximum + 1).expect("bounded size"))
        .read_to_end(&mut bytes)?;
    if bytes.len() > maximum {
        return Err(AuthorityError::Invalid("record exceeds bound"));
    }
    Ok(bytes)
}

fn require_real_directory(path: &Path) -> Result<(), AuthorityError> {
    for ancestor in path.ancestors() {
        let kind = fs::symlink_metadata(ancestor)?.file_type();
        if !kind.is_dir() || kind.is_symlink() {
            return Err(AuthorityError::Invalid(
                "authority path or ancestor is not a real directory",
            ));
        }
    }
    Ok(())
}

fn create_or_check_directory(path: &Path, parent: &Path) -> Result<(), AuthorityError> {
    match fs::create_dir(path) {
        Ok(()) => File::open(parent)?.sync_all()?,
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
        Err(error) => return Err(error.into()),
    }
    require_real_directory(path)
}

impl AcceptedAuthority {
    /// Install only in a newly created shard directory, or recover its exact record.
    /// The returned lock must remain alive through every distributed read/write.
    ///
    /// # Errors
    /// Returns an error for malformed or untrusted authority, changed/missing
    /// accepted state, admission failure, lock contention, or uncertain I/O.
    #[allow(
        clippy::too_many_lines,
        reason = "fresh installation and recovery preserve one auditable sync/lock ordering"
    )]
    pub fn open_or_install(
        data_dir: impl AsRef<Path>,
        certificate_bytes: &[u8],
        trusted_key: &[u8; 32],
        expected_local_scope: LocalAuthorityScope,
        budget: &MemoryBudget,
        mut attribution: Attribution,
    ) -> Result<Self, AuthorityError> {
        if certificate_bytes.len() < CERT_HEADER_LEN + SIGNATURE_LEN
            || certificate_bytes.len() > MAX_PAYLOAD + SIGNATURE_LEN
        {
            return Err(AuthorityError::Invalid("certificate length"));
        }
        let member_count = usize::from(u16::from_le_bytes([
            certificate_bytes[80],
            certificate_bytes[81],
        ]));
        let schema_count = usize::from(u16::from_le_bytes([
            certificate_bytes[82],
            certificate_bytes[83],
        ]));
        let active_count = if certificate_bytes.starts_with(CERT_MAGIC_V2) {
            if certificate_bytes.len() < CERT_HEADER_LEN_V2 + SIGNATURE_LEN {
                return Err(AuthorityError::Invalid("certificate length"));
            }
            usize::from(u16::from_le_bytes([
                certificate_bytes[84],
                certificate_bytes[85],
            ]))
        } else {
            0
        };
        if !(2..=64).contains(&member_count)
            || !(1..=1024).contains(&schema_count)
            || (certificate_bytes.starts_with(CERT_MAGIC_V2) && !(1..=1024).contains(&active_count))
        {
            return Err(AuthorityError::Invalid("certificate counts"));
        }
        attribution.category = MemoryCategory::Replication;
        let reserved_bytes = certificate_bytes
            .len()
            .checked_mul(6)
            .and_then(|n| n.checked_add(16 * 1024))
            .ok_or(AuthorityError::Invalid("reservation overflow"))?;
        let permit = budget.try_reserve_work(
            attribution,
            reserved_bytes,
            1 + member_count + schema_count + active_count,
        )?;
        let data_dir = std::path::absolute(data_dir.as_ref())?;
        let parent = data_dir
            .parent()
            .ok_or(AuthorityError::Invalid("shard directory parent"))?;
        require_real_directory(parent)?;
        let view = parse_certificate(
            certificate_bytes,
            trusted_key,
            expected_local_scope,
            data_dir.clone(),
        )?;
        let expected_record = encode_record(&view)?;
        let fresh = match fs::create_dir(&data_dir) {
            Ok(()) => {
                File::open(parent)?.sync_all()?;
                true
            }
            Err(error)
                if error.kind() == std::io::ErrorKind::AlreadyExists && data_dir.is_dir() =>
            {
                false
            }
            Err(error) => return Err(error.into()),
        };
        require_real_directory(&data_dir)?;
        let authority_dir = data_dir.join("authority");
        if fresh {
            fs::create_dir(&authority_dir)?;
        } else if !authority_dir.is_dir() {
            return Err(AuthorityError::ExistingDirectoryWithoutAuthority);
        }
        require_real_directory(&authority_dir)?;
        let lock_path = authority_dir.join("lock");
        if let Ok(kind) = fs::symlink_metadata(&lock_path)
            && kind.file_type().is_symlink()
        {
            return Err(AuthorityError::Invalid("authority lock symlink"));
        }
        let lock = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(lock_path)?;
        lock.try_lock_exclusive()
            .map_err(|_| AuthorityError::LockUnavailable)?;
        if fresh {
            lock.sync_all()?;
            File::open(&authority_dir)?.sync_all()?;
        }
        let record_path = authority_dir.join("accepted.tbca");
        if let Ok(kind) = fs::symlink_metadata(&record_path)
            && kind.file_type().is_symlink()
        {
            return Err(AuthorityError::Invalid("authority record symlink"));
        }
        if fresh {
            File::open(&data_dir)?.sync_all()?;
            let nonce = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let temp_path =
                authority_dir.join(format!(".accepted-{}-{nonce}.tmp", std::process::id()));
            let mut temp = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temp_path)?;
            temp.write_all(&expected_record)?;
            temp.sync_all()?;
            fs::rename(&temp_path, &record_path)?;
            File::open(&authority_dir)?.sync_all()?;
        }
        let read = match read_bounded(
            &record_path,
            RECORD_HEADER_LEN + MAX_PAYLOAD + SIGNATURE_LEN + RECORD_TRAILER_LEN,
        ) {
            Ok(read) => read,
            Err(error)
                if !fresh
                    && matches!(&error, AuthorityError::Io(io) if io.kind() == std::io::ErrorKind::NotFound) =>
            {
                return Err(AuthorityError::ExistingDirectoryWithoutAuthority);
            }
            Err(error) => return Err(error),
        };
        validate_record(&read, &expected_record)?;
        // Reparse the exact bytes recovered from disk, not the caller's buffer.
        let recovered = parse_certificate(
            &read[RECORD_HEADER_LEN..read.len() - 4],
            trusted_key,
            expected_local_scope,
            data_dir,
        )?;
        Ok(Self {
            view: recovered,
            _lock: lock,
            _permit: permit,
        })
    }

    #[must_use]
    pub fn view(&self) -> &CertifiedShardView {
        &self.view
    }
}

impl CertifiedShardView {
    /// The signed certificate grammar; only v2 carries an active C0 table.
    #[must_use]
    pub const fn version(&self) -> AuthorityVersion {
        self.version
    }

    #[must_use]
    pub const fn cluster(&self) -> [u8; 16] {
        self.binding.cluster
    }

    #[must_use]
    pub const fn tenant(&self) -> TenantId {
        self.binding.tenant
    }

    /// Full signed local origin selected by the independently expected member ID.
    #[must_use]
    pub const fn local_origin(&self) -> OriginId {
        self.local_origin
    }

    #[must_use]
    pub fn configuration(&self) -> ConfigurationId {
        self.binding.configuration
    }
    #[must_use]
    pub fn epoch(&self) -> MembershipEpoch {
        self.binding.epoch
    }
    #[must_use]
    pub fn database(&self) -> DatabaseId {
        self.binding.database
    }
    #[must_use]
    pub fn shard(&self) -> ShardId {
        self.binding.shard
    }
    #[must_use]
    pub fn catalog(&self, binding: CollectionBinding) -> Option<&[u8]> {
        self.schemas
            .iter()
            .find(|row| row.binding == binding)
            .map(|row| &self.certificate[row.catalog_start..row.catalog_end])
    }

    /// All bounded, signed catalog generations in canonical order.
    #[must_use]
    pub fn catalogs(&self) -> impl ExactSizeIterator<Item = (CollectionBinding, &[u8])> + '_ {
        self.schemas.iter().map(|row| {
            (
                row.binding,
                &self.certificate[row.catalog_start..row.catalog_end],
            )
        })
    }

    /// All bounded, signed members in canonical order.
    #[must_use]
    pub fn members(&self) -> impl ExactSizeIterator<Item = CertifiedMemberInfo<'_>> + '_ {
        self.members.iter().map(|row| CertifiedMemberInfo {
            origin: row.origin,
            leaf_sha256: row.leaf_sha256,
            failure_domain: &row.failure_domain,
            server_name: &row.server_name,
        })
    }

    /// The signed active C0 definition for a new distributed source write.
    /// A v1 authority has no active table and cannot pass this check.
    ///
    /// # Errors
    /// Rejects a collection without a signed active binding and catalog.
    pub fn checked_active_catalog(
        &self,
        collection: CollectionId,
    ) -> Result<(CollectionBinding, &[u8]), AuthorityError> {
        if self.version != AuthorityVersion::V2 {
            return Err(AuthorityError::UnknownSchema);
        }
        let binding = self
            .active
            .iter()
            .find(|binding| binding.collection == collection)
            .copied()
            .ok_or(AuthorityError::UnknownSchema)?;
        let catalog = self.catalog(binding).ok_or(AuthorityError::UnknownSchema)?;
        Ok((binding, catalog))
    }

    /// Authority-bound location for the distributed document shard.
    #[must_use]
    pub fn documents_path(&self) -> PathBuf {
        self.data_dir.join("documents")
    }
    #[must_use]
    pub fn failure_domain(&self, member: ReplicaId) -> Option<&str> {
        self.members
            .iter()
            .find(|row| row.origin.replica_id == member)
            .map(|row| row.failure_domain.as_ref())
    }
    #[must_use]
    pub fn server_name(&self, member: ReplicaId) -> Option<&str> {
        self.members
            .iter()
            .find(|row| row.origin.replica_id == member)
            .map(|row| row.server_name.as_ref())
    }
    #[must_use]
    pub fn member_leaf_sha256(&self, member: ReplicaId) -> Option<[u8; 32]> {
        self.members
            .iter()
            .find(|row| row.origin.replica_id == member)
            .map(|row| row.leaf_sha256)
    }
    /// Storage directory for one certified origin stream, isolated from other members.
    #[must_use]
    pub fn inbox_path(&self, origin: OriginId) -> PathBuf {
        let mut name = String::with_capacity(65);
        for byte in origin
            .replica_id
            .to_bytes()
            .into_iter()
            .chain(origin.incarnation)
        {
            use std::fmt::Write as _;
            write!(&mut name, "{byte:02x}").expect("string write");
        }
        self.data_dir.join("inbox").join(name)
    }
    /// Authorize a Hello only from the leaf DER returned by authenticated mTLS.
    ///
    /// # Errors
    /// Rejects a missing/foreign leaf or any member, scope, epoch, or
    /// configuration claim outside this accepted certificate.
    pub fn certify_peer(
        &self,
        hello: PeerHelloScope,
        tls_leaf_der: &[u8],
    ) -> Result<CertifiedMember<'_>, AuthorityError> {
        if tls_leaf_der.is_empty() {
            return Err(AuthorityError::UnauthenticatedMember);
        }
        if hello.cluster != self.binding.cluster
            || hello.tenant != self.binding.tenant
            || hello.database != self.binding.database
            || hello.shard != self.binding.shard
            || hello.epoch != self.binding.epoch
            || hello.configuration != self.binding.configuration
        {
            return Err(AuthorityError::ScopeMismatch);
        }
        let fingerprint = sha256(tls_leaf_der);
        let row = self
            .members
            .iter()
            .find(|row| row.origin == hello.origin && row.leaf_sha256 == fingerprint)
            .ok_or(AuthorityError::UnauthenticatedMember)?;
        Ok(CertifiedMember {
            view: self,
            origin: row.origin,
        })
    }
    /// Bind one exact semantic proof to the authenticated member and retained catalogs.
    ///
    /// # Errors
    /// Rejects a proof from another view/member/scope or a missing catalog binding.
    pub fn certify_event<'v, 'p, 'b>(
        &'v self,
        member: &CertifiedMember<'v>,
        proof: &'p VerifiedIncomingGroup<'b>,
    ) -> Result<CertifiedEvent<'v, 'p, 'b>, AuthorityError> {
        if !std::ptr::eq(member.view, self) {
            return Err(AuthorityError::ScopeMismatch);
        }
        let event = proof.event();
        let id = event.id();
        if id.database() != self.binding.database
            || id.shard() != self.binding.shard
            || id.origin() != member.origin
            || event.epoch() != self.binding.epoch
            || event.configuration() != self.binding.configuration
        {
            return Err(AuthorityError::ScopeMismatch);
        }
        if event
            .bindings()
            .iter()
            .any(|binding| self.catalog(*binding).is_none())
        {
            return Err(AuthorityError::UnknownSchema);
        }
        Ok(CertifiedEvent {
            view: self,
            proof,
            id,
            digest: event.digest(),
        })
    }
}

impl CertifiedMember<'_> {
    #[must_use]
    pub fn origin(&self) -> OriginId {
        self.origin
    }
}

impl CertifiedEvent<'_, '_, '_> {
    /// Check the exact accepted authority that certified this event before a
    /// document store reads or writes its target and private inbox.
    ///
    /// # Errors
    /// Rejects a capability from another accepted authority instance or
    /// signed binding without changing either store.
    pub fn validate_view(&self, view: &CertifiedShardView) -> Result<(), AuthorityError> {
        if self.view.instance != view.instance || self.view.binding != view.binding {
            return Err(AuthorityError::ScopeMismatch);
        }
        Ok(())
    }

    /// The logical event retained by the authority-checked semantic proof.
    #[must_use]
    pub fn event(&self) -> &crate::event::CommitEvent {
        self.proof.event()
    }

    /// Exact source D1 keys and complete values in logical change order.
    #[must_use]
    pub fn source_changes(&self) -> &[VerifiedSourceChange<'_>] {
        self.proof.source_changes()
    }

    #[must_use]
    pub fn id(&self) -> EventId {
        self.id
    }
    #[must_use]
    pub fn digest(&self) -> EventDigest {
        self.digest
    }

    /// Compare the complete proof-owned canonical group with an existing
    /// durable inbox marker, without appending or allocating journal bytes.
    #[must_use]
    pub fn matches_durable_group(&self, group: &OwnedDurableGroup) -> bool {
        let exact = self.proof.journal_group();
        self.view.instance == group.view_instance
            && self.id == group.id()
            && self.digest == group.digest()
            && exact.id == group.id()
            && exact.epoch == group.epoch()
            && exact.configuration == group.configuration()
            && exact.transaction_id == group.transaction_id()
            && exact.metadata == group.metadata()
            && exact.wal_frame == group.wal_frame()
    }
}

impl CertifiedReplayEvent<'_, '_, '_, '_, '_> {
    #[must_use]
    pub fn event(&self) -> &crate::event::CommitEvent {
        self.proof.event()
    }

    /// Exact source D1 keys and complete values in logical change order.
    #[must_use]
    pub fn source_changes(&self) -> &[VerifiedSourceChange<'_>] {
        self.proof.source_changes()
    }

    /// The exact durable marker and canonical bytes retained through replay.
    #[must_use]
    pub fn durable_group(&self) -> &OwnedDurableGroup {
        self.group
    }
}

impl ReplicaReceiver {
    /// Validate this open receiver against the exact accepted view before it
    /// becomes part of a document store's fixed inbox set. This performs no
    /// journal write or durable read.
    ///
    /// # Errors
    /// Rejects a receiver opened for another view or one needing recovery.
    pub fn validate_view(&self, view: &CertifiedShardView) -> Result<(), ReceiveError> {
        if self.journal.needs_recovery() {
            return Err(JournalError::NeedsRecovery.into());
        }
        if self.view_instance != view.instance
            || self.binding != view.binding
            || self.scope.database != view.database()
            || self.scope.shard != view.shard()
            || self.scope.epoch != view.epoch()
            || self.scope.configuration != view.configuration()
            || self.scope.origin == view.local_origin()
            || !view
                .members()
                .any(|member| member.origin == self.scope.origin)
        {
            return Err(AuthorityError::ScopeMismatch.into());
        }
        Ok(())
    }

    /// Open exactly one origin's journal under the accepted shard directory.
    ///
    /// # Errors
    /// Rejects a foreign scope or unsafe directory and propagates journal
    /// recovery, capacity, and I/O failures.
    pub fn open(
        view: &CertifiedShardView,
        config: StorageConfig,
        budget: MemoryBudget,
        scope: CursorScope,
        cursor_limits: CursorLimits,
    ) -> Result<Self, ReceiveError> {
        if config.path != view.inbox_path(scope.origin)
            || config.tenant != view.binding.tenant
            || scope.database != view.binding.database
            || scope.shard != view.binding.shard
            || scope.epoch != view.binding.epoch
            || scope.configuration != view.binding.configuration
            || !view.members.iter().any(|row| row.origin == scope.origin)
        {
            return Err(AuthorityError::ScopeMismatch.into());
        }
        require_real_directory(&view.data_dir)?;
        let inbox_root = view.data_dir.join("inbox");
        create_or_check_directory(&inbox_root, &view.data_dir)?;
        create_or_check_directory(&config.path, &inbox_root)?;
        let journal = ReplicaJournal::open(config, budget, scope, cursor_limits)?;
        Ok(Self {
            view_instance: view.instance,
            open_token: RECEIVER_SEQUENCE.fetch_add(1, Ordering::Relaxed),
            binding: view.binding,
            scope,
            journal,
        })
    }

    /// Read one exact group from the contiguous recovered durable prefix.
    /// This neither advances a cursor nor acknowledges or applies an event.
    ///
    /// # Errors
    /// Rejects invalid bounds, a missing/corrupt marker within the prefix,
    /// a journal needing reopen, storage failures, or admission failures.
    pub fn read_durable_exact(
        &self,
        sequence: u64,
        max_bytes: usize,
    ) -> Result<Option<OwnedDurableGroup>, ReceiveError> {
        self.journal
            .read_durable_exact(sequence, max_bytes, self.view_instance, self.open_token)
            .map_err(|error| ReceiveError::DurableRead {
                kind: durable_read_kind(&error),
                detail: error.to_string(),
            })
    }

    /// Corrupt the first stored chunk of an already durable group for Slow
    /// integration fault testing. The mutation uses the open journal's normal
    /// synced storage path and is unavailable in ordinary builds.
    ///
    /// # Errors
    /// Rejects an absent sequence and propagates storage or recovery failures.
    #[cfg(feature = "slow-test-hooks")]
    #[doc(hidden)]
    pub fn corrupt_first_durable_chunk_for_slow_test(
        &mut self,
        sequence: u64,
    ) -> Result<(), ReceiveError> {
        self.journal
            .corrupt_first_durable_chunk_for_slow_test(sequence)
            .map_err(ReceiveError::from)
    }

    /// Certify a freshly verified exact inbox read against the persisted
    /// signed member and catalog, without using a live TLS member capability.
    ///
    /// # Errors
    /// Rejects a stale view/receiver, altered bytes or marker identity, a
    /// journal needing reopen, absent member, or unavailable catalog binding.
    #[allow(
        clippy::too_many_lines,
        reason = "all replay authority and exact-byte comparisons are one fail-closed gate"
    )]
    pub fn certify_durable_replay<'r, 'v, 'g, 'p, 'b>(
        &'r self,
        view: &'v CertifiedShardView,
        group: &'g OwnedDurableGroup,
        proof: &'p VerifiedIncomingGroup<'b>,
    ) -> Result<CertifiedReplayEvent<'r, 'v, 'g, 'p, 'b>, ReceiveError> {
        if self.journal.needs_recovery() {
            return Err(JournalError::NeedsRecovery.into());
        }
        let event = proof.event();
        let id = event.id();
        let exact = proof.journal_group();
        if view.instance != self.view_instance
            || view.binding != self.binding
            || group.view_instance != self.view_instance
            || group.receiver_open_token != self.open_token
            || group.id() != id
            || group.digest() != event.digest()
            || group.epoch() != event.epoch()
            || group.configuration() != event.configuration()
            || group.transaction_id() != event.transaction_id()
            || group.metadata() != exact.metadata
            || group.wal_frame() != exact.wal_frame
            || id.database() != self.scope.database
            || id.shard() != self.scope.shard
            || id.origin() != self.scope.origin
            || group.epoch() != self.scope.epoch
            || group.configuration() != self.scope.configuration
            || !view.members.iter().any(|row| row.origin == id.origin())
        {
            return Err(AuthorityError::ScopeMismatch.into());
        }
        if event
            .bindings()
            .iter()
            .any(|binding| view.catalog(*binding).is_none())
        {
            return Err(AuthorityError::UnknownSchema.into());
        }
        Ok(CertifiedReplayEvent {
            _receiver: self,
            _view: view,
            group,
            proof,
        })
    }

    /// Sync the private marker, then return only the contiguous durable prefix.
    ///
    /// # Errors
    /// Rejects a capability from another authority/event or propagates any
    /// journal failure without advancing the returned prefix.
    #[allow(
        clippy::needless_pass_by_value,
        reason = "the certified capability is consumed once at the durable admission boundary"
    )]
    pub fn append_certified(
        &mut self,
        certified: CertifiedEvent<'_, '_, '_>,
    ) -> Result<DurablePrefix, ReceiveError> {
        if certified.view.instance != self.view_instance
            || certified.view.binding != self.binding
            || certified.id.database() != self.scope.database
            || certified.id.shard() != self.scope.shard
            || certified.id.origin() != self.scope.origin
            || certified.proof.event().id() != certified.id
            || certified.proof.event().digest() != certified.digest
        {
            return Err(ReceiveError::Append {
                kind: AppendFailureKind::IncomingConflict,
                detail: AuthorityError::ScopeMismatch.to_string(),
            });
        }
        let sequence = match self.journal.append_verified(certified.proof) {
            Ok(sequence) => sequence,
            Err(error) => {
                return Err(ReceiveError::Append {
                    kind: append_failure_kind(&error, self.journal.needs_recovery()),
                    detail: error.to_string(),
                });
            }
        };
        Ok(DurablePrefix {
            sequence,
            epoch: self.scope.epoch,
            configuration: self.scope.configuration,
            origin: self.scope.origin,
        })
    }
    #[must_use]
    pub fn durable_prefix(&self) -> DurablePrefix {
        DurablePrefix {
            sequence: self.journal.durable_prefix(),
            epoch: self.scope.epoch,
            configuration: self.scope.configuration,
            origin: self.scope.origin,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair};
    use tb_core::{BudgetConfig, TransactionId, WorkerId};
    use tb_format::{
        DocumentBinding, DocumentDot, DocumentEventId, DocumentKind, DocumentSibling, FieldType,
        FormatLimits, SchemaField, StatePointer, WalOperation, encode_catalog_into,
        encode_change_into, encode_change_key, encode_high_water_key, encode_high_water_value,
        encode_marker_key, encode_retry_key, encode_retry_value, encode_schema_into,
        encode_state_into, encode_state_pointer, encode_wal_frame_into,
    };

    use crate::event::{CausalContext, MAX_DISTRIBUTED_METADATA_BYTES, ReplicationLimits};
    use crate::metadata::{
        MetadataChange, MetadataHeader, WalRole, compute_event_digest, encode_metadata_into,
        verify_incoming,
    };

    const CLUSTER: [u8; 16] = [1; 16];
    const TENANT: TenantId = TenantId::from_bytes([2; 16]);
    const DATABASE: DatabaseId = DatabaseId::from_bytes([3; 16]);
    const COLLECTION: CollectionId = CollectionId::from_bytes([4; 16]);

    fn key() -> Ed25519KeyPair {
        Ed25519KeyPair::from_seed_unchecked(&[7; 32]).expect("fixed fixture key")
    }
    fn public_key() -> [u8; 32] {
        key()
            .public_key()
            .as_ref()
            .try_into()
            .expect("Ed25519 key width")
    }
    fn expected() -> LocalAuthorityScope {
        LocalAuthorityScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            member: ReplicaId::from_bytes([5; 16]),
        }
    }
    fn budget() -> MemoryBudget {
        MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("test budget"))
    }
    fn attribution() -> Attribution {
        Attribution {
            tenant: TENANT,
            worker: WorkerId(7),
            category: MemoryCategory::Replication,
        }
    }
    fn catalog_for(collection: CollectionId, generation: u64) -> (Vec<u8>, [u8; 32]) {
        let mut schema = Vec::new();
        encode_schema_into(
            &mut schema,
            &[SchemaField {
                field_id: 1,
                parent_field_id: 0,
                value_type: FieldType::Utf8,
                element_type: None,
                flags: 0,
                name: "title",
            }],
        )
        .expect("schema");
        let mut catalog = Vec::new();
        let digest =
            encode_catalog_into(&mut catalog, collection, generation, 0, &schema).expect("catalog");
        (catalog, digest)
    }

    fn catalog() -> (Vec<u8>, [u8; 32]) {
        catalog_for(COLLECTION, 1)
    }

    fn event_limits() -> ReplicationLimits {
        ReplicationLimits {
            max_origins: 8,
            max_changes: 8,
            max_siblings: 8,
            max_value_bytes: MAX_VALUE_BYTES,
            max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
        }
    }

    fn semantic_primary(
        record_format: u16,
        binding: CollectionBinding,
        document: tb_core::DocumentId,
        event: DocumentEventId,
    ) -> (Vec<u8>, Vec<u8>) {
        if record_format == 1 {
            let mut value = Vec::new();
            encode_state_into(
                &mut value,
                binding.collection,
                document,
                &[DocumentDot {
                    origin: event.origin,
                    sequence: event.sequence,
                }],
                &[DocumentSibling {
                    event,
                    generation: binding.generation,
                    schema_digest: binding.schema_digest,
                    kind: DocumentKind::Tombstone,
                    document_bytes: &[],
                }],
            )
            .unwrap();
            (
                tb_format::encode_primary_key(binding.collection, document).to_vec(),
                value,
            )
        } else {
            let (key, value) = encode_state_pointer(StatePointer {
                collection: binding.collection,
                document,
                manifest_sha256: [22; 32],
            });
            (key.to_vec(), value.to_vec())
        }
    }

    const REPLAY_WAL_ROLES: [WalRole; 5] = [
        WalRole::Marker,
        WalRole::SourceChange,
        WalRole::Primary,
        WalRole::Retry,
        WalRole::HighWater,
    ];

    fn semantic_fixture(
        view: &CertifiedShardView,
        sequence: u64,
        record_format: u16,
        binding: CollectionBinding,
    ) -> (Vec<u8>, Vec<u8>, EventDigest, Vec<u8>) {
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let id = EventId::new(DATABASE, ShardId::new(0), origin, sequence).unwrap();
        let event = DocumentEventId {
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            sequence,
        };
        let document = tb_core::DocumentId::from_bytes([9; 16]);
        let document_binding = DocumentBinding {
            collection: binding.collection,
            document,
            generation: binding.generation,
            schema_digest: binding.schema_digest,
        };
        let observed = if sequence == 1 {
            CausalContext::new()
        } else {
            CausalContext::from_entries([(origin, sequence - 1)], 8).unwrap()
        };
        let header = MetadataHeader {
            id,
            epoch: view.epoch(),
            configuration: view.configuration(),
            transaction_id: TransactionId::new(u64::MAX, [u8::try_from(sequence).unwrap(); 16]),
            record_format,
            merge_policy: 1,
        };
        let change = MetadataChange {
            collection: binding.collection,
            document,
            source_wal_ordinal: 1,
            kind: DocumentKind::Tombstone,
            observed,
        };
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            header,
            &[binding],
            &[change],
            &REPLAY_WAL_ROLES,
            event_limits(),
        )
        .unwrap();
        let mut source_value = Vec::new();
        encode_change_into(
            &mut source_value,
            event,
            document_binding,
            DocumentKind::Tombstone,
            &[],
        )
        .unwrap();
        let primary = semantic_primary(record_format, binding, document, event);
        let operations = [
            (
                encode_marker_key(origin, sequence).unwrap().to_vec(),
                metadata.clone(),
            ),
            (
                encode_change_key(binding.collection, document, origin, sequence, 0)
                    .unwrap()
                    .to_vec(),
                source_value.clone(),
            ),
            primary,
            (
                encode_retry_key(header.transaction_id).to_vec(),
                encode_retry_value(event).unwrap().to_vec(),
            ),
            (
                encode_high_water_key(origin).unwrap().to_vec(),
                encode_high_water_value(sequence).unwrap().to_vec(),
            ),
        ];
        let borrowed = operations
            .iter()
            .map(|(key, value)| WalOperation::Put { key, value })
            .collect::<Vec<_>>();
        let mut wal = Vec::new();
        encode_wal_frame_into(
            &mut wal,
            12 + sequence,
            header.transaction_id,
            &borrowed,
            &FormatLimits::default(),
        )
        .unwrap();
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        (metadata, wal, digest, source_value)
    }

    type OrderedSourceBytes = [(Vec<u8>, Vec<u8>); 2];

    fn ordered_source(
        binding: CollectionBinding,
        event: DocumentEventId,
        document: tb_core::DocumentId,
        logical_ordinal: u16,
    ) -> (Vec<u8>, Vec<u8>) {
        let mut value = Vec::new();
        encode_change_into(
            &mut value,
            event,
            DocumentBinding {
                collection: binding.collection,
                document,
                generation: binding.generation,
                schema_digest: binding.schema_digest,
            },
            DocumentKind::Tombstone,
            &[],
        )
        .unwrap();
        (
            encode_change_key(
                binding.collection,
                document,
                event.origin,
                event.sequence,
                logical_ordinal,
            )
            .unwrap()
            .to_vec(),
            value,
        )
    }

    fn ordered_semantic_fixture(
        view: &CertifiedShardView,
        record_format: u16,
        binding: CollectionBinding,
    ) -> (Vec<u8>, Vec<u8>, EventDigest, OrderedSourceBytes) {
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let id = EventId::new(DATABASE, ShardId::new(0), origin, 1).unwrap();
        let event = DocumentEventId {
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            sequence: 1,
        };
        let header = MetadataHeader {
            id,
            epoch: view.epoch(),
            configuration: view.configuration(),
            transaction_id: TransactionId::new(
                u64::MAX,
                [u8::try_from(record_format).expect("fixture record format"); 16],
            ),
            record_format,
            merge_policy: 1,
        };
        let documents = [
            tb_core::DocumentId::from_bytes([9; 16]),
            tb_core::DocumentId::from_bytes([10; 16]),
        ];
        let changes = documents.map(|document| MetadataChange {
            collection: binding.collection,
            document,
            source_wal_ordinal: if document == documents[0] { 3 } else { 1 },
            kind: DocumentKind::Tombstone,
            observed: CausalContext::new(),
        });
        let roles = [
            WalRole::Marker,
            WalRole::SourceChange,
            WalRole::Primary,
            WalRole::SourceChange,
            WalRole::Primary,
            WalRole::Retry,
            WalRole::HighWater,
        ];
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            header,
            &[binding],
            &changes,
            &roles,
            event_limits(),
        )
        .unwrap();
        let sources = [
            ordered_source(binding, event, documents[0], 0),
            ordered_source(binding, event, documents[1], 1),
        ];
        let operations = [
            (
                encode_marker_key(origin, 1).unwrap().to_vec(),
                metadata.clone(),
            ),
            sources[1].clone(),
            semantic_primary(record_format, binding, documents[1], event),
            sources[0].clone(),
            semantic_primary(record_format, binding, documents[0], event),
            (
                encode_retry_key(header.transaction_id).to_vec(),
                encode_retry_value(event).unwrap().to_vec(),
            ),
            (
                encode_high_water_key(origin).unwrap().to_vec(),
                encode_high_water_value(1).unwrap().to_vec(),
            ),
        ];
        let borrowed = operations
            .iter()
            .map(|(key, value)| WalOperation::Put { key, value })
            .collect::<Vec<_>>();
        let mut wal = Vec::new();
        encode_wal_frame_into(
            &mut wal,
            13,
            header.transaction_id,
            &borrowed,
            &FormatLimits::default(),
        )
        .unwrap();
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        (metadata, wal, digest, sources)
    }

    fn payload() -> AuthorityPayload {
        let (catalog, digest) = catalog();
        AuthorityPayload::encode(
            AuthorityCertificateScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: ShardId::new(0),
                epoch: MembershipEpoch::new(1),
            },
            &[
                AuthorityMemberInput {
                    origin: OriginId {
                        replica_id: ReplicaId::from_bytes([5; 16]),
                        incarnation: [6; 16],
                    },
                    leaf_der: b"leaf-a",
                    server_name: "a.example.test",
                    failure_domain: "zone-a",
                },
                AuthorityMemberInput {
                    origin: OriginId {
                        replica_id: ReplicaId::from_bytes([6; 16]),
                        incarnation: [7; 16],
                    },
                    leaf_der: b"leaf-b",
                    server_name: "b.example.test",
                    failure_domain: "zone-b",
                },
            ],
            &[AuthoritySchemaInput {
                binding: CollectionBinding {
                    collection: COLLECTION,
                    generation: 1,
                    schema_digest: digest,
                },
                catalog: &catalog,
            }],
        )
        .expect("canonical authority payload")
    }
    fn certificate() -> Vec<u8> {
        let payload = payload();
        let signed = payload.signing_input().expect("bounded signing input");
        let signature = key().try_sign(&signed).expect("fixture signing");
        payload
            .finish(
                signature.as_ref().try_into().expect("signature width"),
                &public_key(),
            )
            .expect("verified certificate")
    }

    fn certificate_without_second_member() -> Vec<u8> {
        let (catalog, digest) = catalog();
        let payload = AuthorityPayload::encode(
            AuthorityCertificateScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: ShardId::new(0),
                epoch: MembershipEpoch::new(1),
            },
            &[
                AuthorityMemberInput {
                    origin: OriginId {
                        replica_id: expected().member,
                        incarnation: [6; 16],
                    },
                    leaf_der: b"leaf-a",
                    server_name: "a.example.test",
                    failure_domain: "zone-a",
                },
                AuthorityMemberInput {
                    origin: OriginId {
                        replica_id: ReplicaId::from_bytes([7; 16]),
                        incarnation: [8; 16],
                    },
                    leaf_der: b"leaf-c",
                    server_name: "c.example.test",
                    failure_domain: "zone-c",
                },
            ],
            &[AuthoritySchemaInput {
                binding: CollectionBinding {
                    collection: COLLECTION,
                    generation: 1,
                    schema_digest: digest,
                },
                catalog: &catalog,
            }],
        )
        .unwrap();
        let signature = key().try_sign(&payload.signing_input().unwrap()).unwrap();
        payload
            .finish(signature.as_ref().try_into().unwrap(), &public_key())
            .unwrap()
    }
    fn v2_payload() -> AuthorityPayload {
        let (old_catalog, old_digest) = catalog();
        let (new_catalog, new_digest) = catalog_for(COLLECTION, 2);
        let members = [
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([5; 16]),
                    incarnation: [6; 16],
                },
                leaf_der: b"leaf-a",
                server_name: "a.example.test",
                failure_domain: "zone-a",
            },
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([6; 16]),
                    incarnation: [7; 16],
                },
                leaf_der: b"leaf-b",
                server_name: "b.example.test",
                failure_domain: "zone-b",
            },
        ];
        let old = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: old_digest,
        };
        let new = CollectionBinding {
            collection: COLLECTION,
            generation: 2,
            schema_digest: new_digest,
        };
        AuthorityPayload::encode_v2(
            AuthorityCertificateScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: ShardId::new(0),
                epoch: MembershipEpoch::new(1),
            },
            &members,
            &[
                AuthoritySchemaInput {
                    binding: old,
                    catalog: &old_catalog,
                },
                AuthoritySchemaInput {
                    binding: new,
                    catalog: &new_catalog,
                },
            ],
            &[old],
        )
        .expect("canonical v2 payload")
    }
    fn certificate_v2() -> Vec<u8> {
        let payload = v2_payload();
        let signature = key()
            .try_sign(&payload.signing_input().unwrap())
            .expect("v2 signing");
        payload
            .finish(signature.as_ref().try_into().unwrap(), &public_key())
            .expect("verified v2 certificate")
    }
    fn resign(mut payload: Vec<u8>) -> Vec<u8> {
        let mut signed = SIGN_DOMAIN.to_vec();
        signed.extend_from_slice(&payload);
        payload.extend_from_slice(key().try_sign(&signed).expect("fixture signing").as_ref());
        payload
    }
    fn resign_v2(mut payload: Vec<u8>) -> Vec<u8> {
        let mut signed = SIGN_DOMAIN_V2.to_vec();
        signed.extend_from_slice(&payload);
        payload.extend_from_slice(
            key()
                .try_sign(&signed)
                .expect("v2 fixture signing")
                .as_ref(),
        );
        payload
    }
    fn view(certificate: &[u8]) -> Result<CertifiedShardView, AuthorityError> {
        parse_certificate(
            certificate,
            &public_key(),
            expected(),
            PathBuf::from("unused"),
        )
    }

    #[test]
    fn canonical_payload_signing_input_matches_production_verifier() {
        let canonical_payload = payload();
        let configuration = canonical_payload.configuration();
        let signed = canonical_payload
            .signing_input()
            .expect("bounded signing input");
        assert!(signed.starts_with(SIGN_DOMAIN));
        assert_eq!(
            &signed[SIGN_DOMAIN.len()..SIGN_DOMAIN.len() + CERT_MAGIC.len()],
            CERT_MAGIC
        );
        let signature = key().try_sign(&signed).expect("fixture signing");
        let signature: &[u8; 64] = signature.as_ref().try_into().expect("signature width");
        let certificate = canonical_payload
            .finish(signature, &public_key())
            .expect("verified certificate");
        assert_eq!(view(&certificate).unwrap().configuration(), configuration);
        assert_eq!(
            &certificate[..certificate.len() - SIGNATURE_LEN],
            &signed[SIGN_DOMAIN.len()..]
        );

        let mut wrong_signature = *signature;
        wrong_signature[0] ^= 1;
        assert!(payload().finish(&wrong_signature, &public_key()).is_err());
        let wrong_public: [u8; 32] = Ed25519KeyPair::from_seed_unchecked(&[8; 32])
            .expect("other fixture key")
            .public_key()
            .as_ref()
            .try_into()
            .expect("key width");
        assert!(payload().finish(signature, &wrong_public).is_err());
    }

    #[test]
    fn v2_signed_active_catalog_and_domains_are_exact() {
        let v2 = v2_payload();
        assert_ne!(v2.configuration(), payload().configuration());
        let signed = v2.signing_input().unwrap();
        assert!(signed.starts_with(SIGN_DOMAIN_V2));
        assert_eq!(
            &signed[SIGN_DOMAIN_V2.len()..SIGN_DOMAIN_V2.len() + 8],
            CERT_MAGIC_V2
        );
        let certificate_v2_bytes = certificate_v2();
        let certified = view(&certificate_v2_bytes).unwrap();
        assert_eq!(certified.version(), AuthorityVersion::V2);
        assert_eq!(certified.cluster(), CLUSTER);
        assert_eq!(certified.tenant(), TENANT);
        assert_eq!(
            certified.local_origin(),
            OriginId {
                replica_id: expected().member,
                incarnation: [6; 16],
            }
        );
        let second = parse_certificate(
            &certificate_v2_bytes,
            &public_key(),
            LocalAuthorityScope {
                member: ReplicaId::from_bytes([6; 16]),
                ..expected()
            },
            PathBuf::from("unused"),
        )
        .unwrap();
        assert_eq!(second.local_origin().incarnation, [7; 16]);
        let (old_catalog, old_digest) = catalog();
        let active = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: old_digest,
        };
        assert_eq!(
            certified.checked_active_catalog(COLLECTION).unwrap(),
            (active, old_catalog.as_slice())
        );
        assert_eq!(certified.catalogs().len(), 2);
        assert_eq!(certified.members().len(), 2);
        assert_eq!(
            certified.members().next().unwrap().origin.replica_id,
            expected().member
        );
        assert!(matches!(
            certified.checked_active_catalog(CollectionId::from_bytes([8; 16])),
            Err(AuthorityError::UnknownSchema)
        ));
        assert!(matches!(
            view(&certificate())
                .unwrap()
                .checked_active_catalog(COLLECTION),
            Err(AuthorityError::UnknownSchema)
        ));
        let wrong_scope = LocalAuthorityScope {
            shard: ShardId::new(9),
            ..expected()
        };
        assert!(matches!(
            parse_certificate(
                &certificate_v2_bytes,
                &public_key(),
                wrong_scope,
                PathBuf::from("unused"),
            ),
            Err(AuthorityError::ScopeMismatch)
        ));
    }

    #[test]
    fn v2_reopens_exactly_and_cannot_replace_an_installed_v1() {
        let certificate_v2_bytes = certificate_v2();
        let v2 = v2_payload();
        let active = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: catalog().1,
        };
        let parent = temp_root();
        let data_dir = parent.join("shard");
        let budget = budget();
        let installed = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate_v2_bytes,
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .unwrap();
        assert_eq!(
            installed.view().documents_path(),
            data_dir.join("documents")
        );
        assert_eq!(
            installed
                .view()
                .checked_active_catalog(COLLECTION)
                .unwrap()
                .0,
            active
        );
        drop(installed);
        let reopened = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate_v2_bytes,
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .unwrap();
        assert_eq!(reopened.view().configuration(), v2.configuration());
        drop(reopened);
        assert!(matches!(
            AcceptedAuthority::open_or_install(
                &data_dir,
                &certificate(),
                &public_key(),
                expected(),
                &budget,
                attribution(),
            ),
            Err(AuthorityError::RecordMismatch)
        ));
        let old_dir = parent.join("old-shard");
        let old = AcceptedAuthority::open_or_install(
            &old_dir,
            &certificate(),
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .unwrap();
        drop(old);
        assert!(matches!(
            AcceptedAuthority::open_or_install(
                &old_dir,
                &certificate_v2_bytes,
                &public_key(),
                expected(),
                &budget,
                attribution(),
            ),
            Err(AuthorityError::RecordMismatch)
        ));
        fs::remove_dir_all(parent).unwrap();
    }

    #[test]
    fn v2_encoder_requires_one_sorted_matching_active_binding_per_collection() {
        let other_collection = CollectionId::from_bytes([8; 16]);
        let (first_catalog, first_digest) = catalog();
        let (second_catalog, second_digest) = catalog_for(other_collection, 1);
        let first = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: first_digest,
        };
        let second = CollectionBinding {
            collection: other_collection,
            generation: 1,
            schema_digest: second_digest,
        };
        let members = [
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([5; 16]),
                    incarnation: [6; 16],
                },
                leaf_der: b"leaf-a",
                server_name: "a.example.test",
                failure_domain: "zone-a",
            },
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([6; 16]),
                    incarnation: [7; 16],
                },
                leaf_der: b"leaf-b",
                server_name: "b.example.test",
                failure_domain: "zone-b",
            },
        ];
        let schemas = [
            AuthoritySchemaInput {
                binding: first,
                catalog: &first_catalog,
            },
            AuthoritySchemaInput {
                binding: second,
                catalog: &second_catalog,
            },
        ];
        let scope = AuthorityCertificateScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            epoch: MembershipEpoch::new(1),
        };
        assert!(AuthorityPayload::encode_v2(scope, &members, &schemas, &[first, second]).is_ok());
        assert!(AuthorityPayload::encode_v2(scope, &members, &schemas, &[first]).is_err());
        assert!(AuthorityPayload::encode_v2(scope, &members, &schemas, &[first, first]).is_err());
        assert!(AuthorityPayload::encode_v2(scope, &members, &schemas, &[second, first]).is_err());
        let unmatched = CollectionBinding {
            schema_digest: [9; 32],
            ..second
        };
        assert!(
            AuthorityPayload::encode_v2(scope, &members, &schemas, &[first, unmatched]).is_err()
        );
    }

    #[test]
    fn v2_decoder_rejects_resigned_active_rows_and_signature_tampering() {
        let certificate = certificate_v2();
        let payload = &certificate[..certificate.len() - SIGNATURE_LEN];
        let active_start = payload.len() - ACTIVE_ROW_LEN;

        let mut absent = payload.to_vec();
        absent[84..86].copy_from_slice(&0_u16.to_le_bytes());
        assert!(view(&resign_v2(absent)).is_err());

        let mut duplicate = payload.to_vec();
        duplicate[84..86].copy_from_slice(&2_u16.to_le_bytes());
        duplicate.extend_from_slice(&payload[active_start..]);
        let duplicate_len = u32::try_from(duplicate.len()).unwrap();
        duplicate[8..12].copy_from_slice(&duplicate_len.to_le_bytes());
        assert!(view(&resign_v2(duplicate)).is_err());

        let mut unmatched = payload.to_vec();
        unmatched[active_start + 24] ^= 1;
        assert!(view(&resign_v2(unmatched)).is_err());

        let mut bad_catalog = payload.to_vec();
        bad_catalog[active_start - 1] ^= 1;
        assert!(view(&resign_v2(bad_catalog)).is_err());

        let mut wrong_version = payload.to_vec();
        wrong_version[12..14].copy_from_slice(&1_u16.to_le_bytes());
        assert!(view(&resign_v2(wrong_version)).is_err());

        let mut bad_signature = certificate;
        *bad_signature.last_mut().unwrap() ^= 1;
        assert!(view(&bad_signature).is_err());
    }

    #[test]
    fn canonical_payload_rejects_invalid_inputs_before_signing() {
        let (catalog, digest) = catalog();
        let scope = AuthorityCertificateScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            epoch: MembershipEpoch::new(1),
        };
        let mut members = [
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([5; 16]),
                    incarnation: [6; 16],
                },
                leaf_der: b"leaf-a",
                server_name: "a.example.test",
                failure_domain: "zone-a",
            },
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([6; 16]),
                    incarnation: [7; 16],
                },
                leaf_der: b"leaf-b",
                server_name: "b.example.test",
                failure_domain: "zone-b",
            },
        ];
        let mut schemas = [AuthoritySchemaInput {
            binding: CollectionBinding {
                collection: COLLECTION,
                generation: 1,
                schema_digest: digest,
            },
            catalog: &catalog,
        }];
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_ok());
        members[0].server_name = "Bad.example.test";
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_err());
        members[0].server_name = "a.example.test";
        members[1].server_name = "a.example.test";
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_err());
        members[1].server_name = "b.example.test";
        members[1].leaf_der = b"leaf-a";
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_err());
        members[1].leaf_der = b"leaf-b";
        members.swap(0, 1);
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_err());
        members.swap(0, 1);
        let mut bad_catalog = catalog.clone();
        bad_catalog[0] ^= 1;
        schemas[0].catalog = &bad_catalog;
        assert!(AuthorityPayload::encode(scope, &members, &schemas).is_err());
        schemas[0].catalog = &catalog;
        let wrong_scope = AuthorityCertificateScope {
            epoch: MembershipEpoch::new(0),
            ..scope
        };
        assert!(AuthorityPayload::encode(wrong_scope, &members, &schemas).is_err());
    }

    fn temp_root() -> PathBuf {
        let number = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let parent =
            std::env::temp_dir().join(format!("tbca-test-{}-{number}", std::process::id()));
        fs::create_dir(&parent).expect("unique parent");
        parent
    }

    #[test]
    fn signed_scope_peer_and_catalog_are_exact() {
        let certificate = certificate();
        let certified_view = view(&certificate).expect("signed certificate");
        assert_eq!(certified_view.version(), AuthorityVersion::V1);
        assert_eq!(certified_view.cluster(), CLUSTER);
        assert_eq!(certified_view.tenant(), TENANT);
        assert_eq!(
            certified_view.local_origin(),
            OriginId {
                replica_id: expected().member,
                incarnation: [6; 16],
            }
        );
        let second = parse_certificate(
            &certificate,
            &public_key(),
            LocalAuthorityScope {
                member: ReplicaId::from_bytes([6; 16]),
                ..expected()
            },
            PathBuf::from("unused"),
        )
        .expect("second signed member selected");
        assert_eq!(
            second.local_origin(),
            OriginId {
                replica_id: ReplicaId::from_bytes([6; 16]),
                incarnation: [7; 16],
            }
        );
        // Independently computed from the published byte grammar and CRC32C,
        // rather than from the parser below.
        assert_eq!(
            sha256(&certificate[..certificate.len() - SIGNATURE_LEN]),
            [
                0x36, 0xda, 0x54, 0x54, 0x35, 0xa0, 0xf9, 0xa6, 0xe5, 0xb9, 0x05, 0x6b, 0x0f, 0xe7,
                0x2c, 0xf5, 0x8f, 0xc3, 0x7e, 0x79, 0x99, 0x74, 0xff, 0x64, 0xf0, 0x84, 0xb1, 0xcc,
                0xcd, 0x43, 0xd3, 0x13,
            ]
        );
        assert_eq!(
            certified_view.configuration().to_bytes(),
            [
                0x83, 0x45, 0x1d, 0x4a, 0x84, 0x3b, 0x73, 0xc4, 0x0a, 0x4c, 0x6a, 0x3d, 0xd3, 0x9d,
                0x2e, 0x01, 0x5a, 0x66, 0x1a, 0x09, 0xac, 0x41, 0xba, 0x04, 0x2e, 0x27, 0xbb, 0xf4,
                0xdb, 0x52, 0xd4, 0x2d,
            ]
        );
        let binding = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: catalog().1,
        };
        assert_eq!(
            certified_view.catalog(binding),
            Some(catalog().0.as_slice())
        );
        assert_eq!(
            certified_view.server_name(expected().member),
            Some("a.example.test")
        );
        let hello = PeerHelloScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            origin: OriginId {
                replica_id: expected().member,
                incarnation: [6; 16],
            },
            epoch: MembershipEpoch::new(1),
            configuration: certified_view.configuration(),
        };
        assert_eq!(
            certified_view
                .certify_peer(hello, b"leaf-a")
                .expect("authenticated member")
                .origin(),
            hello.origin
        );
        assert!(matches!(
            certified_view.certify_peer(hello, b"leaf-b"),
            Err(AuthorityError::UnauthenticatedMember)
        ));
        let mut wrong = hello;
        wrong.epoch = MembershipEpoch::new(2);
        assert!(matches!(
            certified_view.certify_peer(wrong, b"leaf-a"),
            Err(AuthorityError::ScopeMismatch)
        ));
        let mut corrupted = certificate;
        corrupted[20] ^= 1;
        assert!(view(&corrupted).is_err());
    }

    #[test]
    fn rejects_re_signed_noncanonical_member_and_schema_rows() {
        let certificate = certificate();
        let payload = &certificate[..certificate.len() - SIGNATURE_LEN];
        let mut wrong_domain = payload.to_vec();
        // First member starts immediately after the fixed header.
        let name_offset = CERT_HEADER_LEN + 16 + 16 + 32 + 1;
        let domain_offset = name_offset + "a.example.test".len() + 1;
        wrong_domain[domain_offset] = b'/';
        assert!(view(&resign(wrong_domain)).is_err());
        let mut duplicate_member = payload.to_vec();
        duplicate_member[CERT_HEADER_LEN + 16 + 16 + 32] = 0;
        assert!(view(&resign(duplicate_member)).is_err());
        let mut wrong_catalog = payload.to_vec();
        let last = wrong_catalog.len() - 1;
        wrong_catalog[last] ^= 1;
        assert!(view(&resign(wrong_catalog)).is_err());
        let mut wrong_version = payload.to_vec();
        wrong_version[12] = 2;
        assert!(view(&resign(wrong_version)).is_err());
        let mut wrong_name = payload.to_vec();
        wrong_name[name_offset] = b'A';
        assert!(view(&resign(wrong_name)).is_err());
        let mut wrong_order = payload.to_vec();
        wrong_order[CERT_HEADER_LEN] = 9;
        assert!(view(&resign(wrong_order)).is_err());
        let wrong_key = Ed25519KeyPair::from_seed_unchecked(&[8; 32]).expect("other fixture key");
        let wrong_public: [u8; 32] = wrong_key
            .public_key()
            .as_ref()
            .try_into()
            .expect("key width");
        assert!(
            parse_certificate(
                &certificate,
                &wrong_public,
                expected(),
                PathBuf::from("unused")
            )
            .is_err()
        );
        assert!(view(&certificate[..certificate.len() - 1]).is_err());
    }

    #[test]
    fn one_time_record_recovers_exactly_and_missing_or_changed_record_fails_closed() {
        let parent = temp_root();
        let data_dir = parent.join("shard");
        let certificate = certificate();
        let key = public_key();
        let budget = budget();
        let accepted = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate,
            &key,
            expected(),
            &budget,
            attribution(),
        )
        .expect("fresh install");
        assert!(matches!(
            AcceptedAuthority::open_or_install(
                &data_dir,
                &certificate,
                &key,
                expected(),
                &budget,
                attribution()
            ),
            Err(AuthorityError::LockUnavailable)
        ));
        drop(accepted);
        let recovered = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate,
            &key,
            expected(),
            &budget,
            attribution(),
        )
        .expect("exact recovery");
        assert_eq!(
            recovered.view().catalog(CollectionBinding {
                collection: COLLECTION,
                generation: 1,
                schema_digest: catalog().1,
            }),
            Some(catalog().0.as_slice())
        );
        drop(recovered);
        let record_path = data_dir.join("authority/accepted.tbca");
        let good_record = fs::read(&record_path).expect("record bytes");
        let mut corrupt = good_record.clone();
        let last = corrupt.len() - 1;
        corrupt[last] ^= 1;
        fs::write(&record_path, &corrupt).expect("corrupt record");
        assert!(
            AcceptedAuthority::open_or_install(
                &data_dir,
                &certificate,
                &key,
                expected(),
                &budget,
                attribution()
            )
            .is_err()
        );
        fs::write(&record_path, &good_record).expect("restore fixture record");
        let mut different = certificate.clone();
        let payload_len = different.len() - SIGNATURE_LEN;
        different.truncate(payload_len);
        different[8 + 4 + 2 + 2 + 16 + 16 + 16 + 8] = 2;
        different = resign(different);
        assert!(matches!(
            AcceptedAuthority::open_or_install(
                &data_dir,
                &different,
                &key,
                expected(),
                &budget,
                attribution()
            ),
            Err(AuthorityError::RecordMismatch)
        ));
        fs::remove_file(&record_path).expect("remove record");
        assert!(matches!(
            AcceptedAuthority::open_or_install(
                &data_dir,
                &certificate,
                &key,
                expected(),
                &budget,
                attribution()
            ),
            Err(AuthorityError::ExistingDirectoryWithoutAuthority)
        ));
        fs::remove_dir_all(parent).expect("remove test root");
    }

    #[test]
    fn two_origin_inboxes_coexist_and_reopen_without_scope_collision() {
        let parent = temp_root();
        let data_dir = parent.join("shard");
        let certificate = certificate();
        let budget = budget();
        {
            let accepted = AcceptedAuthority::open_or_install(
                &data_dir,
                &certificate,
                &public_key(),
                expected(),
                &budget,
                attribution(),
            )
            .expect("accepted authority");
            let view = accepted.view();
            let limits = CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            };
            let scope = |id: u8| CursorScope {
                database: DATABASE,
                shard: ShardId::new(0),
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([id; 16]),
                    incarnation: [id + 1; 16],
                },
                epoch: view.epoch(),
                configuration: view.configuration(),
            };
            let open = |id| {
                let stream = scope(id);
                let path = view.inbox_path(stream.origin);
                ReplicaReceiver::open(
                    view,
                    StorageConfig::local(path, TENANT, WorkerId(7)),
                    budget.clone(),
                    stream,
                    limits,
                )
                .expect("isolated origin inbox")
            };
            let first = open(5);
            let second = open(6);
            assert_ne!(
                view.inbox_path(scope(5).origin),
                view.inbox_path(scope(6).origin)
            );
            assert_eq!(first.durable_prefix().sequence, 0);
            assert_eq!(second.durable_prefix().sequence, 0);
            drop(first);
            drop(second);
            assert_eq!(open(5).durable_prefix().sequence, 0);
            assert_eq!(open(6).durable_prefix().sequence, 0);
        }
        fs::remove_dir_all(parent).expect("remove test root");
    }

    type ReplayFixture = (Vec<u8>, Vec<u8>, EventDigest, Vec<u8>);

    fn replay_member(view: &CertifiedShardView, scope: CursorScope) -> CertifiedMember<'_> {
        view.certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: scope.shard,
                origin: scope.origin,
                epoch: scope.epoch,
                configuration: scope.configuration,
            },
            b"leaf-a",
        )
        .unwrap()
    }

    fn replay_receiver(
        view: &CertifiedShardView,
        budget: &MemoryBudget,
        scope: CursorScope,
    ) -> ReplicaReceiver {
        ReplicaReceiver::open(
            view,
            StorageConfig::local(view.inbox_path(scope.origin), TENANT, WorkerId(7)),
            budget.clone(),
            scope,
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .unwrap()
    }

    fn append_replay_fixture(
        view: &CertifiedShardView,
        member: &CertifiedMember<'_>,
        receiver: &mut ReplicaReceiver,
        fixture: &ReplayFixture,
        sequence: u64,
        budget: &MemoryBudget,
    ) {
        let proof = verify_incoming(
            &fixture.0,
            &fixture.1,
            fixture.2,
            event_limits(),
            budget,
            attribution(),
        )
        .unwrap();
        let live = view.certify_event(member, &proof).unwrap();
        assert_eq!(receiver.append_certified(live).unwrap().sequence, sequence);
    }

    fn replay_proof<'a>(
        group: &'a OwnedDurableGroup,
        budget: &MemoryBudget,
    ) -> VerifiedIncomingGroup<'a> {
        verify_incoming(
            group.metadata(),
            group.wal_frame(),
            group.digest(),
            event_limits(),
            budget,
            attribution(),
        )
        .unwrap()
    }

    #[test]
    fn live_certified_accessors_borrow_exact_signed_v1_and_v2_sources() {
        for (certificate, record_format) in [(certificate(), 1), (certificate_v2(), 2)] {
            let view = view(&certificate).expect("signed fixture view");
            let origin = OriginId {
                replica_id: expected().member,
                incarnation: [6; 16],
            };
            let member = view
                .certify_peer(
                    PeerHelloScope {
                        cluster: CLUSTER,
                        tenant: TENANT,
                        database: DATABASE,
                        shard: ShardId::new(0),
                        origin,
                        epoch: view.epoch(),
                        configuration: view.configuration(),
                    },
                    b"leaf-a",
                )
                .expect("signed member and TLS leaf");
            let binding = CollectionBinding {
                collection: COLLECTION,
                generation: 1,
                schema_digest: catalog().1,
            };
            let (metadata, wal, digest, expected) =
                ordered_semantic_fixture(&view, record_format, binding);
            let budget = budget();
            let before = budget.snapshot();
            let proof = verify_incoming(
                &metadata,
                &wal,
                digest,
                event_limits(),
                &budget,
                attribution(),
            )
            .expect("exact semantic proof");
            let held = budget.snapshot();
            assert!(held.reserved_bytes > before.reserved_bytes);
            assert_eq!(held.reserved_items, before.reserved_items + 1);
            {
                let live = view
                    .certify_event(&member, &proof)
                    .expect("live capability");
                assert!(std::ptr::eq(live.event(), proof.event()));
                assert!(std::ptr::eq(live.source_changes(), proof.source_changes()));
                assert_eq!(live.event().versions().record_format, record_format);
                assert_eq!(live.event().id(), live.id());
                assert_eq!(live.event().digest(), live.digest());
                assert_eq!(live.source_changes().len(), 2);
                for (index, (source, (key, value))) in
                    live.source_changes().iter().zip(&expected).enumerate()
                {
                    assert_eq!(source.logical_ordinal(), u16::try_from(index).unwrap());
                    assert_eq!(source.source_wal_ordinal(), if index == 0 { 3 } else { 1 });
                    assert_eq!(source.key(), key.as_slice());
                    assert_eq!(source.value(), value.as_slice());
                }
                assert_eq!(budget.snapshot().reserved_bytes, held.reserved_bytes);
                assert_eq!(budget.snapshot().reserved_items, held.reserved_items);
            }
            assert_eq!(budget.snapshot().reserved_bytes, held.reserved_bytes);
            assert_eq!(budget.snapshot().reserved_items, held.reserved_items);
            drop(proof);
            assert_eq!(budget.snapshot().reserved_bytes, before.reserved_bytes);
            assert_eq!(budget.snapshot().reserved_items, before.reserved_items);
        }
    }

    #[test]
    fn live_peer_gate_rejects_wrong_leaf_scope_and_excluded_member() {
        let view = view(&certificate_v2()).expect("signed v2 view");
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let hello = PeerHelloScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            epoch: view.epoch(),
            configuration: view.configuration(),
        };
        assert!(matches!(
            view.certify_peer(hello, b"leaf-b"),
            Err(AuthorityError::UnauthenticatedMember)
        ));
        assert!(matches!(
            view.certify_peer(
                PeerHelloScope {
                    shard: ShardId::new(1),
                    ..hello
                },
                b"leaf-a"
            ),
            Err(AuthorityError::ScopeMismatch)
        ));
        let excluded = self::view(&certificate_without_second_member()).unwrap();
        assert!(matches!(
            excluded.certify_peer(
                PeerHelloScope {
                    origin: OriginId {
                        replica_id: ReplicaId::from_bytes([6; 16]),
                        incarnation: [7; 16],
                    },
                    configuration: excluded.configuration(),
                    ..hello
                },
                b"leaf-b"
            ),
            Err(AuthorityError::UnauthenticatedMember)
        ));
    }

    #[test]
    fn live_event_gate_rejects_altered_bytes_foreign_view_and_unsigned_catalog() {
        let signed = certificate_v2();
        let view = view(&signed).expect("signed v2 view");
        let foreign = self::view(&signed).expect("independent signed view");
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let hello = PeerHelloScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            epoch: view.epoch(),
            configuration: view.configuration(),
        };
        let binding = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: catalog().1,
        };
        let (metadata, wal, digest, _) = semantic_fixture(&view, 1, 2, binding);
        let budget = budget();
        let mut altered_metadata = metadata.clone();
        altered_metadata[0] ^= 1;
        assert!(
            verify_incoming(
                &altered_metadata,
                &wal,
                digest,
                event_limits(),
                &budget,
                attribution()
            )
            .is_err()
        );
        let mut altered_wal = wal.clone();
        altered_wal[0] ^= 1;
        assert!(
            verify_incoming(
                &metadata,
                &altered_wal,
                digest,
                event_limits(),
                &budget,
                attribution()
            )
            .is_err()
        );
        let proof = verify_incoming(
            &metadata,
            &wal,
            digest,
            event_limits(),
            &budget,
            attribution(),
        )
        .unwrap();
        let member = view.certify_peer(hello, b"leaf-a").unwrap();
        assert!(matches!(
            foreign.certify_event(&member, &proof),
            Err(AuthorityError::ScopeMismatch)
        ));
        let unknown_binding = CollectionBinding {
            collection: CollectionId::from_bytes([99; 16]),
            generation: 1,
            schema_digest: [88; 32],
        };
        let (unknown_metadata, unknown_wal, unknown_digest, _) =
            semantic_fixture(&view, 1, 2, unknown_binding);
        let unknown_proof = verify_incoming(
            &unknown_metadata,
            &unknown_wal,
            unknown_digest,
            event_limits(),
            &budget,
            attribution(),
        )
        .unwrap();
        assert!(matches!(
            view.certify_event(&member, &unknown_proof),
            Err(AuthorityError::UnknownSchema)
        ));
    }

    #[test]
    fn replay_certifies_exact_v1_and_v2_durable_bytes_only_for_current_receiver() {
        let parent = temp_root();
        let data_dir = parent.join("shard");
        let certificate = certificate();
        let budget = budget();
        let accepted = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate,
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .unwrap();
        let view = accepted.view();
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let scope = CursorScope {
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            epoch: view.epoch(),
            configuration: view.configuration(),
        };
        let binding = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: catalog().1,
        };
        let member = replay_member(view, scope);
        let mut receiver = replay_receiver(view, &budget, scope);
        let first = semantic_fixture(view, 1, 1, binding);
        let second = semantic_fixture(view, 2, 2, binding);
        for (sequence, fixture) in [(1, &first), (2, &second)] {
            append_replay_fixture(view, &member, &mut receiver, fixture, sequence, &budget);
        }
        let max = 8 + MAX_DISTRIBUTED_METADATA_BYTES + tb_core::MAX_WAL_GROUP_BYTES;
        let group_v1 = receiver.read_durable_exact(1, max).unwrap().unwrap();
        let group_v2 = receiver.read_durable_exact(2, max).unwrap().unwrap();
        assert_eq!(group_v1.metadata(), first.0.as_slice());
        assert_eq!(group_v1.wal_frame(), first.1.as_slice());
        assert_eq!(group_v2.metadata(), second.0.as_slice());
        assert_eq!(group_v2.wal_frame(), second.1.as_slice());
        let proof_v1 = replay_proof(&group_v1, &budget);
        let proof_v2 = replay_proof(&group_v2, &budget);
        let replay_v1 = receiver
            .certify_durable_replay(view, &group_v1, &proof_v1)
            .unwrap();
        let replay_v2 = receiver
            .certify_durable_replay(view, &group_v2, &proof_v2)
            .unwrap();
        assert_eq!(replay_v1.event().versions().record_format, 1);
        assert_eq!(replay_v2.event().versions().record_format, 2);
        assert_eq!(replay_v1.source_changes()[0].value(), first.3.as_slice());
        assert_eq!(replay_v2.source_changes()[0].value(), second.3.as_slice());
        assert!(
            receiver
                .certify_durable_replay(view, &group_v1, &proof_v2)
                .is_err()
        );
        let stale_view = parse_certificate(
            &certificate,
            &public_key(),
            expected(),
            PathBuf::from("unused"),
        )
        .unwrap();
        assert!(
            receiver
                .certify_durable_replay(&stale_view, &group_v1, &proof_v1)
                .is_err()
        );
        drop(receiver);
        let reopened = replay_receiver(view, &budget, scope);
        assert!(
            reopened
                .certify_durable_replay(view, &group_v1, &proof_v1)
                .is_err()
        );
        let reread = reopened.read_durable_exact(1, max).unwrap().unwrap();
        let fresh = replay_proof(&reread, &budget);
        assert!(
            reopened
                .certify_durable_replay(view, &reread, &fresh)
                .is_ok()
        );
        drop(reopened);
        drop(accepted);
        fs::remove_dir_all(parent).unwrap();
    }

    #[test]
    fn replay_rejects_missing_signed_catalog_and_member() {
        let parent = temp_root();
        let data_dir = parent.join("shard");
        let certificate = certificate();
        let budget = budget();
        let mut accepted = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate,
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .unwrap();
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let scope = CursorScope {
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            epoch: accepted.view().epoch(),
            configuration: accepted.view().configuration(),
        };
        let mut receiver = ReplicaReceiver::open(
            accepted.view(),
            StorageConfig::local(accepted.view().inbox_path(origin), TENANT, WorkerId(7)),
            budget.clone(),
            scope,
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .unwrap();
        let binding = CollectionBinding {
            collection: CollectionId::from_bytes([99; 16]),
            generation: 1,
            schema_digest: [88; 32],
        };
        let (metadata, wal, digest, _) = semantic_fixture(accepted.view(), 1, 2, binding);
        let proof = verify_incoming(
            &metadata,
            &wal,
            digest,
            event_limits(),
            &budget,
            attribution(),
        )
        .unwrap();
        receiver.journal.append_verified(&proof).unwrap();
        let max = 8 + MAX_DISTRIBUTED_METADATA_BYTES + tb_core::MAX_WAL_GROUP_BYTES;
        let group = receiver.read_durable_exact(1, max).unwrap().unwrap();
        let replay_proof = verify_incoming(
            group.metadata(),
            group.wal_frame(),
            group.digest(),
            event_limits(),
            &budget,
            attribution(),
        )
        .unwrap();
        assert!(matches!(
            receiver.certify_durable_replay(accepted.view(), &group, &replay_proof),
            Err(ReceiveError::Authority(AuthorityError::UnknownSchema))
        ));
        accepted.view.members.clear();
        assert!(matches!(
            receiver.certify_durable_replay(accepted.view(), &group, &replay_proof),
            Err(ReceiveError::Authority(AuthorityError::ScopeMismatch))
        ));
        drop(receiver);
        drop(accepted);
        fs::remove_dir_all(parent).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn symlinked_inbox_root_fails_before_storage_open() {
        use std::os::unix::fs::symlink;

        let parent = temp_root();
        let data_dir = parent.join("shard");
        let budget = budget();
        let accepted = AcceptedAuthority::open_or_install(
            &data_dir,
            &certificate(),
            &public_key(),
            expected(),
            &budget,
            attribution(),
        )
        .expect("accepted authority");
        let view = accepted.view();
        symlink(&parent, data_dir.join("inbox")).expect("link inbox");
        let origin = OriginId {
            replica_id: expected().member,
            incarnation: [6; 16],
        };
        let scope = CursorScope {
            database: DATABASE,
            shard: ShardId::new(0),
            origin,
            epoch: view.epoch(),
            configuration: view.configuration(),
        };
        let result = ReplicaReceiver::open(
            view,
            StorageConfig::local(view.inbox_path(origin), TENANT, WorkerId(7)),
            budget,
            scope,
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        );
        assert!(result.is_err());
        drop(accepted);
        fs::remove_dir_all(parent).expect("remove test root");
    }
}
