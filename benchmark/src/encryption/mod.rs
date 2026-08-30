//! A small, non-production encryption-at-rest foundation.
//!
//! The module deliberately owns only the cryptographic format primitives. It
//! does not persist keys, select Vault configuration, or claim production or
//! OCI/FIPS qualification. Production mode fails closed until the approved
//! FIPS provider is wired into this crate.

use std::fmt;

use aws_lc_rs::{aead, digest, hkdf, rand};
use zeroize::Zeroize;

const AES_TAG_LEN: usize = 16;
const NONCE_LEN: usize = 12;
const MAX_IDENTITY_BYTES: usize = 128;
const MAX_AAD_BYTES: usize = 2_048;
/// Maximum plaintext accepted by this bounded foundation.
pub const MAX_PLAINTEXT_BYTES: usize = 64 * 1024 * 1024;
const ENVELOPE_MAGIC: [u8; 4] = *b"TBE1";
const ENVELOPE_VERSION: u8 = 1;
const AAD_MAGIC: [u8; 4] = *b"TBA1";
const AAD_VERSION: u8 = 1;
const MAX_ORDINAL: u128 = (1u128 << 96) - 1;

/// The compression marker authenticated in each artifact's AAD.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum Compression {
    /// Plain, uncompressed bytes.
    None = 0,
    /// Zstandard-compressed bytes.
    Zstd = 1,
    /// LZ4-compressed bytes.
    Lz4 = 2,
}

/// Selects the cryptographic provider mode.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProviderMode {
    /// Local development mode backed by the non-FIPS AWS-LC build.
    LocalNonFips,
    /// Production mode, which requires a separately selected FIPS provider.
    ProductionFips,
}

impl ProviderMode {
    /// Returns the stable identity included in diagnostics and evidence.
    #[must_use]
    pub const fn identity(self) -> &'static str {
        match self {
            Self::LocalNonFips => "aws-lc-rs:local-non-fips",
            Self::ProductionFips => "aws-lc-rs:production-fips",
        }
    }

    /// Returns whether this mode represents production FIPS operation.
    #[must_use]
    pub const fn is_production(self) -> bool {
        matches!(self, Self::ProductionFips)
    }
}

/// Errors returned by the encryption foundation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EncryptionError {
    /// The requested production provider is not compiled and selected.
    ProductionProviderUnavailable,
    /// An identity or context field is empty, too long, or otherwise invalid.
    InvalidContext,
    /// The plaintext or envelope exceeds a configured bound.
    SizeLimit,
    /// The 96-bit ordinal nonce space has been exhausted.
    OrdinalExhausted,
    /// The envelope or AAD has an invalid version, shape, or length.
    MalformedEnvelope,
    /// The provider rejected the encryption or decryption operation.
    CryptographicFailure,
    /// Authentication failed, including a wrong key or wrong AAD.
    AuthenticationFailed,
    /// An already-opened ordinal was presented again.
    Replay,
    /// Authenticated plaintext did not match its SHA-256 digest in the AAD.
    DigestMismatch,
}

impl fmt::Display for EncryptionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::ProductionProviderUnavailable => "production FIPS provider unavailable",
            Self::InvalidContext => "invalid encryption context",
            Self::SizeLimit => "encryption input exceeds size limit",
            Self::OrdinalExhausted => "96-bit encryption ordinal exhausted",
            Self::MalformedEnvelope => "malformed encryption envelope",
            Self::CryptographicFailure => "cryptographic provider failure",
            Self::AuthenticationFailed => "encryption authentication failed",
            Self::Replay => "encryption envelope replay refused",
            Self::DigestMismatch => "plaintext digest mismatch",
        };
        f.write_str(message)
    }
}

impl std::error::Error for EncryptionError {}

/// A random 256-bit database key held in zeroizing memory.
pub struct DatabaseKey {
    bytes: [u8; 32],
}

impl DatabaseKey {
    /// Generates a local database key with the provider's OS CSPRNG.
    pub fn generate() -> Result<Self, EncryptionError> {
        let mut bytes = [0u8; 32];
        rand::fill(&mut bytes).map_err(|_| EncryptionError::CryptographicFailure)?;
        Ok(Self { bytes })
    }

    /// Creates a key from exactly 256 bits of caller-owned key material.
    ///
    /// Callers should clear any other copy of the input after construction.
    #[must_use]
    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self { bytes }
    }
}

impl Drop for DatabaseKey {
    fn drop(&mut self) {
        self.bytes.zeroize();
    }
}

impl fmt::Debug for DatabaseKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("DatabaseKey(REDACTED)")
    }
}

/// Identity and format metadata authenticated with every encrypted unit.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EncryptionContext {
    environment: String,
    tenant: String,
    database: String,
    artifact_kind: String,
    artifact_id: String,
    key_version: String,
    format_major: u16,
    compression: Compression,
}

impl EncryptionContext {
    /// Builds a bounded context from canonical textual identities.
    pub fn new(
        environment: impl Into<String>,
        tenant: impl Into<String>,
        database: impl Into<String>,
        artifact_kind: impl Into<String>,
        artifact_id: impl Into<String>,
        key_version: impl Into<String>,
        format_major: u16,
        compression: Compression,
    ) -> Result<Self, EncryptionError> {
        let context = Self {
            environment: environment.into(),
            tenant: tenant.into(),
            database: database.into(),
            artifact_kind: artifact_kind.into(),
            artifact_id: artifact_id.into(),
            key_version: key_version.into(),
            format_major,
            compression,
        };
        context.validate()?;
        Ok(context)
    }

    /// Returns the environment identity.
    #[must_use]
    pub fn environment(&self) -> &str {
        &self.environment
    }

    /// Returns the format major version.
    #[must_use]
    pub const fn format_major(&self) -> u16 {
        self.format_major
    }

    /// Returns the configured compression marker.
    #[must_use]
    pub const fn compression(&self) -> Compression {
        self.compression
    }

    fn validate(&self) -> Result<(), EncryptionError> {
        if self.format_major == 0
            || !valid_identity(&self.environment)
            || !valid_identity(&self.tenant)
            || !valid_identity(&self.database)
            || !valid_identity(&self.artifact_kind)
            || !valid_identity(&self.artifact_id)
            || !valid_identity(&self.key_version)
        {
            return Err(EncryptionError::InvalidContext);
        }
        Ok(())
    }

    fn derivation_info(&self) -> Vec<u8> {
        let mut info = Vec::with_capacity(256);
        info.extend_from_slice(b"terrabase/artifact-key/v1\0");
        append_string(&mut info, &self.artifact_kind);
        append_string(&mut info, &self.artifact_id);
        info.extend_from_slice(&self.format_major.to_be_bytes());
        append_string(&mut info, &self.key_version);
        info
    }

    fn canonical_aad(&self, ordinal: u128, logical_length: u64, digest: &[u8; 32]) -> Vec<u8> {
        let mut aad = Vec::with_capacity(256);
        aad.extend_from_slice(&AAD_MAGIC);
        aad.push(AAD_VERSION);
        append_string(&mut aad, &self.environment);
        append_string(&mut aad, &self.tenant);
        append_string(&mut aad, &self.database);
        append_string(&mut aad, &self.artifact_kind);
        append_string(&mut aad, &self.artifact_id);
        append_string(&mut aad, &self.key_version);
        aad.extend_from_slice(&self.format_major.to_be_bytes());
        aad.extend_from_slice(&ordinal.to_be_bytes()[4..]);
        aad.extend_from_slice(&logical_length.to_be_bytes());
        aad.push(self.compression as u8);
        aad.extend_from_slice(digest);
        aad
    }
}

/// An encryptor/decryptor for one artifact key and its monotonic ordinal.
pub struct ArtifactCipher {
    key: aead::LessSafeKey,
    context: EncryptionContext,
    mode: ProviderMode,
    next_ordinal: u128,
    last_opened_ordinal: Option<u128>,
}

impl fmt::Debug for ArtifactCipher {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ArtifactCipher")
            .field("provider", &self.mode.identity())
            .field("next_ordinal", &self.next_ordinal)
            .finish_non_exhaustive()
    }
}

impl ArtifactCipher {
    /// Creates a local non-FIPS artifact cipher starting at ordinal zero.
    pub fn new(
        mode: ProviderMode,
        database_key: DatabaseKey,
        context: EncryptionContext,
    ) -> Result<Self, EncryptionError> {
        Self::with_next_ordinal(mode, database_key, context, 0)
    }

    /// Creates an artifact cipher with a recovered next ordinal.
    pub fn with_next_ordinal(
        mode: ProviderMode,
        database_key: DatabaseKey,
        context: EncryptionContext,
        next_ordinal: u128,
    ) -> Result<Self, EncryptionError> {
        context.validate()?;
        if mode.is_production() {
            return Err(EncryptionError::ProductionProviderUnavailable);
        }
        if next_ordinal > MAX_ORDINAL {
            return Err(EncryptionError::OrdinalExhausted);
        }

        let salt = hkdf::Salt::new(hkdf::HKDF_SHA256, b"terrabase/database-key/v1");
        let prk = salt.extract(&database_key.bytes);
        let info = context.derivation_info();
        let info_parts = [info.as_slice()];
        let okm = prk
            .expand(&info_parts, &aead::AES_256_GCM)
            .map_err(|_| EncryptionError::CryptographicFailure)?;
        let unbound = aead::UnboundKey::from(okm);
        Ok(Self {
            key: aead::LessSafeKey::new(unbound),
            context,
            mode,
            next_ordinal,
            last_opened_ordinal: None,
        })
    }

    /// Returns the selected provider mode.
    #[must_use]
    pub const fn mode(&self) -> ProviderMode {
        self.mode
    }

    /// Returns the next ordinal that will be allocated for sealing.
    #[must_use]
    pub const fn next_ordinal(&self) -> u128 {
        self.next_ordinal
    }

    /// Encrypts one bounded plaintext into a versioned envelope.
    pub fn seal(&mut self, plaintext: &[u8]) -> Result<Vec<u8>, EncryptionError> {
        if plaintext.len() > MAX_PLAINTEXT_BYTES {
            return Err(EncryptionError::SizeLimit);
        }
        let ordinal = self.next_ordinal;
        if ordinal > MAX_ORDINAL {
            return Err(EncryptionError::OrdinalExhausted);
        }
        let next = ordinal.checked_add(1).ok_or(EncryptionError::OrdinalExhausted)?;
        let digest = sha256(plaintext);
        let aad = self.context.canonical_aad(ordinal, plaintext.len() as u64, &digest);
        if aad.len() > MAX_AAD_BYTES {
            return Err(EncryptionError::SizeLimit);
        }
        let nonce = nonce_for(ordinal);
        let mut ciphertext = plaintext.to_vec();
        self.key
            .seal_in_place_append_tag(nonce, aead::Aad::from(aad.as_slice()), &mut ciphertext)
            .map_err(|_| EncryptionError::CryptographicFailure)?;

        let aad_len = u32::try_from(aad.len()).map_err(|_| EncryptionError::SizeLimit)?;
        let ciphertext_len =
            u64::try_from(ciphertext.len()).map_err(|_| EncryptionError::SizeLimit)?;
        let mut envelope = Vec::with_capacity(29 + aad.len() + ciphertext.len());
        envelope.extend_from_slice(&ENVELOPE_MAGIC);
        envelope.push(ENVELOPE_VERSION);
        envelope.extend_from_slice(&ordinal.to_be_bytes()[4..]);
        envelope.extend_from_slice(&aad_len.to_be_bytes());
        envelope.extend_from_slice(&ciphertext_len.to_be_bytes());
        envelope.extend_from_slice(&aad);
        envelope.extend_from_slice(&ciphertext);
        self.next_ordinal = next;
        Ok(envelope)
    }

    /// Authenticates and decrypts one envelope, refusing ordinal replays.
    pub fn open(&mut self, envelope: &[u8]) -> Result<Vec<u8>, EncryptionError> {
        let (ordinal, aad, mut ciphertext) = parse_envelope(envelope)?;
        if self.last_opened_ordinal.is_some_and(|last| ordinal <= last) {
            return Err(EncryptionError::Replay);
        }
        let parsed = parse_aad(&aad)?;
        if parsed.ordinal != ordinal
            || parsed.environment != self.context.environment.as_bytes()
            || parsed.tenant != self.context.tenant.as_bytes()
            || parsed.database != self.context.database.as_bytes()
            || parsed.artifact_kind != self.context.artifact_kind.as_bytes()
            || parsed.artifact_id != self.context.artifact_id.as_bytes()
            || parsed.key_version != self.context.key_version.as_bytes()
            || parsed.format_major != self.context.format_major
            || parsed.compression != self.context.compression as u8
        {
            return Err(EncryptionError::AuthenticationFailed);
        }
        let plaintext_len =
            usize::try_from(parsed.logical_length).map_err(|_| EncryptionError::SizeLimit)?;
        if plaintext_len > MAX_PLAINTEXT_BYTES || ciphertext.len() != plaintext_len + AES_TAG_LEN {
            return Err(EncryptionError::MalformedEnvelope);
        }
        let nonce = nonce_for(ordinal);
        let plaintext = self
            .key
            .open_in_place(nonce, aead::Aad::from(aad), &mut ciphertext)
            .map_err(|_| EncryptionError::AuthenticationFailed)?;
        let digest = sha256(plaintext);
        if digest != parsed.digest {
            return Err(EncryptionError::DigestMismatch);
        }
        self.last_opened_ordinal = Some(ordinal);
        Ok(plaintext.to_vec())
    }
}

fn valid_identity(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= MAX_IDENTITY_BYTES
        && !value.as_bytes().contains(&0)
        && value.is_char_boundary(value.len())
}

fn append_string(out: &mut Vec<u8>, value: &str) {
    out.extend_from_slice(&(value.len() as u16).to_be_bytes());
    out.extend_from_slice(value.as_bytes());
}

fn nonce_for(ordinal: u128) -> aead::Nonce {
    let mut nonce = [0u8; NONCE_LEN];
    nonce.copy_from_slice(&ordinal.to_be_bytes()[4..]);
    aead::Nonce::assume_unique_for_key(nonce)
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    let digest = digest::digest(&digest::SHA256, bytes);
    let mut result = [0u8; 32];
    result.copy_from_slice(digest.as_ref());
    result
}

struct ParsedAad<'a> {
    environment: &'a [u8],
    tenant: &'a [u8],
    database: &'a [u8],
    artifact_kind: &'a [u8],
    artifact_id: &'a [u8],
    key_version: &'a [u8],
    format_major: u16,
    ordinal: u128,
    logical_length: u64,
    compression: u8,
    digest: [u8; 32],
}

fn parse_aad(aad: &[u8]) -> Result<ParsedAad<'_>, EncryptionError> {
    if aad.len() > MAX_AAD_BYTES || aad.len() < 5 || aad[..4] != AAD_MAGIC || aad[4] != AAD_VERSION
    {
        return Err(EncryptionError::MalformedEnvelope);
    }
    let mut offset = 5;
    let environment = read_string(aad, &mut offset)?;
    let tenant = read_string(aad, &mut offset)?;
    let database = read_string(aad, &mut offset)?;
    let artifact_kind = read_string(aad, &mut offset)?;
    let artifact_id = read_string(aad, &mut offset)?;
    let key_version = read_string(aad, &mut offset)?;
    let format_major = read_u16(aad, &mut offset)?;
    let ordinal_bytes = read_bytes(aad, &mut offset, NONCE_LEN)?;
    let mut full_ordinal = [0u8; 16];
    full_ordinal[4..].copy_from_slice(ordinal_bytes);
    let ordinal = u128::from_be_bytes(full_ordinal);
    let logical_length = read_u64(aad, &mut offset)?;
    let compression =
        *read_bytes(aad, &mut offset, 1)?.first().ok_or(EncryptionError::MalformedEnvelope)?;
    let digest_bytes = read_bytes(aad, &mut offset, 32)?;
    let mut digest = [0u8; 32];
    digest.copy_from_slice(digest_bytes);
    if offset != aad.len() {
        return Err(EncryptionError::MalformedEnvelope);
    }
    Ok(ParsedAad {
        environment,
        tenant,
        database,
        artifact_kind,
        artifact_id,
        key_version,
        format_major,
        ordinal,
        logical_length,
        compression,
        digest,
    })
}

fn read_string<'a>(bytes: &'a [u8], offset: &mut usize) -> Result<&'a [u8], EncryptionError> {
    let length = usize::from(read_u16(bytes, offset)?);
    let value = read_bytes(bytes, offset, length)?;
    if value.is_empty() || value.len() > MAX_IDENTITY_BYTES || value.contains(&0) {
        return Err(EncryptionError::MalformedEnvelope);
    }
    Ok(value)
}

fn read_u16(bytes: &[u8], offset: &mut usize) -> Result<u16, EncryptionError> {
    let value = read_bytes(bytes, offset, 2)?;
    Ok(u16::from_be_bytes([value[0], value[1]]))
}

fn read_u64(bytes: &[u8], offset: &mut usize) -> Result<u64, EncryptionError> {
    let value = read_bytes(bytes, offset, 8)?;
    Ok(u64::from_be_bytes(value.try_into().map_err(|_| EncryptionError::MalformedEnvelope)?))
}

fn read_bytes<'a>(
    bytes: &'a [u8],
    offset: &mut usize,
    length: usize,
) -> Result<&'a [u8], EncryptionError> {
    let end = offset.checked_add(length).ok_or(EncryptionError::MalformedEnvelope)?;
    let value = bytes.get(*offset..end).ok_or(EncryptionError::MalformedEnvelope)?;
    *offset = end;
    Ok(value)
}

fn parse_envelope(envelope: &[u8]) -> Result<(u128, &[u8], Vec<u8>), EncryptionError> {
    const HEADER_LEN: usize = 29;
    if envelope.len() < HEADER_LEN
        || envelope[..4] != ENVELOPE_MAGIC
        || envelope[4] != ENVELOPE_VERSION
    {
        return Err(EncryptionError::MalformedEnvelope);
    }
    let mut ordinal_bytes = [0u8; 16];
    ordinal_bytes[4..].copy_from_slice(&envelope[5..17]);
    let ordinal = u128::from_be_bytes(ordinal_bytes);
    let aad_len = usize::try_from(u32::from_be_bytes(
        envelope[17..21].try_into().map_err(|_| EncryptionError::MalformedEnvelope)?,
    ))
    .map_err(|_| EncryptionError::MalformedEnvelope)?;
    let ciphertext_len = usize::try_from(u64::from_be_bytes(
        envelope[21..29].try_into().map_err(|_| EncryptionError::MalformedEnvelope)?,
    ))
    .map_err(|_| EncryptionError::SizeLimit)?;
    if aad_len > MAX_AAD_BYTES
        || ciphertext_len < AES_TAG_LEN
        || ciphertext_len > MAX_PLAINTEXT_BYTES + AES_TAG_LEN
        || HEADER_LEN.checked_add(aad_len).and_then(|n| n.checked_add(ciphertext_len))
            != Some(envelope.len())
    {
        return Err(EncryptionError::MalformedEnvelope);
    }
    let aad_end = HEADER_LEN + aad_len;
    Ok((ordinal, &envelope[HEADER_LEN..aad_end], envelope[aad_end..].to_vec()))
}
