//! The schedule-owned artifact codec: compression, authenticated
//! encryption, and the sealed bundle container (PB-6013).
//!
//! The workspace constraint stack forbids external dependencies, so both
//! transforms are implemented in-crate on top of the backup family's own
//! SHA-256. Compression is a deterministic LZSS (4 KiB window, 4..=259
//! byte matches); by itself it is not an integrity boundary — integrity is
//! bound by the SHA-256 member digests the container re-verifies and, for
//! encrypted bundles, by the authentication tag over the whole container.
//!
//! Encryption is an encrypt-then-MAC construction: HKDF (extract-then-
//! expand, HMAC-SHA256) derives domain-separated subkeys from the
//! caller-supplied key material; the frame stream is compressed first,
//! then masked with a SHA-256 counter-mode keystream by bitwise
//! exclusive-or
//! `SHA256(enc_key || nonce || counter)`; an HMAC-SHA256 tag over the
//! complete header and ciphertext is stored beside the ciphertext. A wrong
//! key and a tampered byte produce the same single typed failure — the
//! open path cannot tell them apart and never discloses which check
//! failed. The construction is an in-crate composition of standard
//! primitives over the in-crate SHA-256, not a claim of a vetted cipher:
//! no performance or cryptographic-strength claim is made anywhere in this
//! module.
//!
//! # Key handling
//!
//! Key material is supplied by the caller for the duration of one
//! operation ([`EncryptionKey`]). It is never written to a destination,
//! never carried in status, receipt, or Debug output, and the buffer is
//! zeroed on drop. Only HKDF-derived subkeys ever touch the container, so
//! the raw material cannot appear in an artifact's bytes.
//!
//! # The container
//!
//! A bundle `bundle-<identity>.tbv2b` holds one sealed recovery point's
//! manifest text plus every archive member, framed with exact lengths and
//! SHA-256 digests. Layout (little-endian): the 7-byte magic `TBV2EB1`,
//! a `u16` version, one byte each naming the encryption and compression
//! algorithms, a 16-byte nonce (zeros when unencrypted), the identity
//! length and bytes, a 32-byte tag (zeros when unencrypted), and the
//! transformed frame stream. Sealing writes a temporary file and renames
//! it into place; extraction writes the manifest and members back as a
//! complete, loadable recovery-point directory — with the right key. The
//! whole point is materialized while sealing or extracting; that memory
//! bound is the same honest bound the BK-01 archive writer discloses for
//! state runs.

use std::collections::{HashMap, VecDeque};
use std::fmt;
use std::path::Path;

use crate::backup::manifest::{MANIFEST_FILE, parse_manifest, point_directory, validate_identity};
use crate::backup::sha256::{Sha256, to_hex};
use crate::backup::{BackupMember, load_recovery_point};

/// The compression algorithm a bundle names (`lzss-v1`).
pub const BUNDLE_COMPRESSION_ALGORITHM: &str = "lzss-v1";

/// The encryption construction a bundle names
/// (`hkdf-sha256-hmac/sha256-ctr-v1`).
pub const BUNDLE_ENCRYPTION_ALGORITHM: &str = "hkdf-sha256-hmac/sha256-ctr-v1";

/// The minimum acceptable key material, in bytes.
const MIN_KEY_MATERIAL: usize = 16;

/// The generated key size, in bytes.
const GENERATED_KEY_BYTES: usize = 32;

/// The container magic and format version.
const MAGIC: &[u8; 7] = b"TBV2EB1";
const VERSION: u16 = 1;

/// Algorithm tag bytes for the container header.
const ENC_NONE: u8 = 0;
const ENC_HKDF_HMAC_CTR: u8 = 1;
const CMP_NONE: u8 = 0;
const CMP_LZSS: u8 = 1;

/// Fixed header lengths: magic, version, tags, nonce, identity length.
const HEADER_FIXED: usize = MAGIC.len() + 2 + 1 + 1 + 16 + 1;

/// The authentication tag length.
const TAG_LEN: usize = 32;

// ---------------------------------------------------------------------
// LZSS compression
// ---------------------------------------------------------------------

/// The maximum distance a match may reach back (`4` KiB).
const WINDOW: usize = 4096;

/// The shortest encodable match.
const MIN_MATCH: usize = 4;

/// The longest encodable match (`4 + 255`).
const MAX_MATCH: usize = MIN_MATCH + u8::MAX as usize;

/// The hash-chain length cap per 3-byte key.
const CHAIN_CAP: usize = 64;

/// Why one compressed stream cannot be decoded.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompressionError {
    /// The stream references a match outside the decoded window, claims a
    /// match token without its bytes, or otherwise violates the format.
    Malformed,
}

/// Compresses `input` with the deterministic LZSS codec. Identical input
/// always yields identical output; the empty input yields the empty
/// stream.
#[must_use]
pub fn compress(input: &[u8]) -> Vec<u8> {
    let mut output = Vec::with_capacity(input.len() / 2 + 64);
    let mut chains: HashMap<u32, VecDeque<usize>> = HashMap::new();
    let mut position = 0;
    while position < input.len() {
        let control_index = output.len();
        output.push(0);
        let mut control = 0u8;
        for bit in 0..8 {
            if position >= input.len() {
                break;
            }
            let (offset, length) = find_match(input, position, &chains);
            if length >= MIN_MATCH {
                control |= 1 << bit;
                output.extend_from_slice(&offset.to_le_bytes());
                output.push(u8::try_from(length - MIN_MATCH).unwrap_or(u8::MAX));
                for covered in position..position + length {
                    index_position(&mut chains, input, covered);
                }
                position += length;
            } else {
                output.push(input[position]);
                index_position(&mut chains, input, position);
                position += 1;
            }
        }
        output[control_index] = control;
    }
    output
}

/// Decompresses an LZSS stream produced by [`compress`].
///
/// # Errors
///
/// Returns [`CompressionError::Malformed`] when the stream references a
/// match before the start of the decoded output, claims a match token
/// without its three bytes, or sets control bits past the stream's end.
pub fn decompress(input: &[u8]) -> Result<Vec<u8>, CompressionError> {
    if input.is_empty() {
        return Ok(Vec::new());
    }
    let mut output = Vec::with_capacity(input.len() * 2);
    let mut cursor = 0;
    while cursor < input.len() {
        let control = input[cursor];
        cursor += 1;
        for bit in 0..8 {
            if cursor >= input.len() {
                // The final group may end early; its unused control bits
                // must be clear, and a set bit without bytes is malformed.
                if control >> bit & 1 == 1 {
                    return Err(CompressionError::Malformed);
                }
                continue;
            }
            if control >> bit & 1 == 1 {
                if cursor + 3 > input.len() {
                    return Err(CompressionError::Malformed);
                }
                let offset = u16::from_le_bytes([input[cursor], input[cursor + 1]]) as usize;
                let length = input[cursor + 2] as usize + MIN_MATCH;
                cursor += 3;
                if offset == 0 || offset > output.len() {
                    return Err(CompressionError::Malformed);
                }
                let start = output.len() - offset;
                // Byte-by-byte copy so an overlapping match repeats, exactly
                // as the encoder intended.
                for index in 0..length {
                    let byte = output[start + index];
                    output.push(byte);
                }
            } else {
                output.push(input[cursor]);
                cursor += 1;
            }
        }
    }
    Ok(output)
}

/// Finds the longest match for `position`, returning `(offset, length)`
/// with `length < MIN_MATCH` when none exists. The offset is the distance
/// back into the decoded stream, always within the window and therefore
/// always encodable.
fn find_match(
    input: &[u8],
    position: usize,
    chains: &HashMap<u32, VecDeque<usize>>,
) -> (u16, usize) {
    if position + 3 > input.len() {
        return (0, 0);
    }
    let key = three_byte_key(&input[position..position + 3]);
    let Some(candidates) = chains.get(&key) else {
        return (0, 0);
    };
    let mut best_offset = 0u16;
    let mut best_length = 0;
    for candidate in candidates {
        let distance = position - candidate;
        if distance > WINDOW {
            break;
        }
        let limit = MAX_MATCH.min(input.len() - position);
        let mut length = 0;
        while length < limit && input[candidate + length] == input[position + length] {
            length += 1;
        }
        if length > best_length {
            best_length = length;
            best_offset = u16::try_from(distance).expect("the window bounds the distance");
            if length == MAX_MATCH {
                break;
            }
        }
    }
    (best_offset, best_length)
}

/// Records `position` in the match chains under its 3-byte key.
fn index_position(chains: &mut HashMap<u32, VecDeque<usize>>, input: &[u8], position: usize) {
    if position + 3 > input.len() {
        return;
    }
    let key = three_byte_key(&input[position..position + 3]);
    let chain = chains.entry(key).or_default();
    chain.push_front(position);
    chain.truncate(CHAIN_CAP);
}

/// The exact 3-byte key of a match position.
fn three_byte_key(bytes: &[u8]) -> u32 {
    (u32::from(bytes[0]) << 16) | (u32::from(bytes[1]) << 8) | u32::from(bytes[2])
}

// ---------------------------------------------------------------------
// HMAC-SHA256, HKDF, and the keystream
// ---------------------------------------------------------------------

/// Streaming HMAC-SHA256 over a caller-chosen key.
struct Hmac {
    inner: Sha256,
    opad_block: [u8; 64],
}

impl Hmac {
    /// Creates the MAC over `key`, hashing or padding it to the block size
    /// as HMAC requires.
    fn new(key: &[u8]) -> Self {
        let mut block = [0u8; 64];
        if key.len() > 64 {
            let mut hasher = Sha256::new();
            hasher.update(key);
            block.copy_from_slice(&hasher.finish());
        } else {
            block[..key.len()].copy_from_slice(key);
        }
        let mut ipad = [0u8; 64];
        let mut opad_block = [0u8; 64];
        for (block_byte, (ipad_byte, opad_byte)) in
            block.iter().zip(ipad.iter_mut().zip(opad_block.iter_mut()))
        {
            *ipad_byte = block_byte ^ 0x36;
            *opad_byte = block_byte ^ 0x5c;
        }
        let mut inner = Sha256::new();
        inner.update(&ipad);
        Self { inner, opad_block }
    }

    /// Absorbs one message part.
    fn update(&mut self, part: &[u8]) {
        self.inner.update(part);
    }

    /// Finishes and returns the tag.
    fn finish(self) -> [u8; 32] {
        let inner_digest = self.inner.finish();
        let mut outer = Sha256::new();
        outer.update(&self.opad_block);
        outer.update(&inner_digest);
        outer.finish()
    }
}

/// HKDF-Extract: derives the pseudorandom key from salt and input material.
fn hkdf_extract(salt: &[u8], input: &[u8]) -> [u8; 32] {
    let mut mac = Hmac::new(salt);
    mac.update(input);
    mac.finish()
}

/// HKDF-Expand into exactly two 32-byte outputs.
fn hkdf_expand_two(prk: &[u8; 32], first_info: &[u8], second_info: &[u8]) -> [[u8; 32]; 2] {
    let mut first = Hmac::new(prk);
    first.update(first_info);
    first.update(&[1]);
    let first_out = first.finish();
    let mut second = Hmac::new(prk);
    second.update(&first_out);
    second.update(second_info);
    second.update(&[2]);
    let second_out = second.finish();
    [first_out, second_out]
}

/// The domain-separated HKDF salt and subkey infos.
const HKDF_SALT: &[u8] = b"terrabase-backup-encryption-v1";
const ENC_KEY_INFO: &[u8] = b"terrabase-backup-encryption/enc";
const MAC_KEY_INFO: &[u8] = b"terrabase-backup-encryption/mac";

/// XORs `data` in place with the SHA-256 counter-mode keystream.
fn xor_keystream(enc_key: &[u8; 32], nonce: &[u8; 16], data: &mut [u8]) {
    for (counter, chunk) in (0u64..).zip(data.chunks_mut(32)) {
        let mut hasher = Sha256::new();
        hasher.update(enc_key);
        hasher.update(nonce);
        hasher.update(&counter.to_be_bytes());
        let block = hasher.finish();
        for (byte, mask) in chunk.iter_mut().zip(block) {
            *byte ^= mask;
        }
    }
}

/// Compares two tags in fixed time.
fn tags_equal(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut difference = 0u8;
    for index in 0..left.len() {
        difference |= left[index] ^ right[index];
    }
    difference == 0
}

// ---------------------------------------------------------------------
// The caller-supplied key
// ---------------------------------------------------------------------

/// Why one encryption operation failed. Every failure is fail-closed: no
/// variant discloses how much of the container verified.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EncryptionError {
    /// The supplied key material is shorter than the minimum.
    KeyMaterialTooShort,
    /// An encrypted bundle was opened without any key.
    KeyRequired,
    /// The OS randomness source could not be read for a generated key.
    RandomSourceUnavailable,
    /// The authentication tag did not verify: wrong key or tampered bytes.
    AuthenticationFailed,
    /// The container's magic, version, or algorithm tags are unknown.
    UnsupportedFormat,
    /// The container is structurally invalid or disagrees with its facts.
    Malformed,
    /// A framed member's length or digest did not match the manifest.
    ChecksumMismatch,
    /// The extraction target already holds that recovery point.
    TargetExists,
    /// A destination file could not be read or written.
    DestinationUnavailable,
    /// The decompressed stream was malformed.
    Compression(CompressionError),
}

/// Key material supplied by the caller for one operation.
///
/// The engine never stores, persists, or logs key material; the buffer is
/// zeroed on drop and Debug prints only a redaction marker. Only
/// HKDF-derived subkeys are used to transform bytes, so the raw material
/// cannot appear in an artifact.
pub struct EncryptionKey {
    material: Vec<u8>,
}

impl EncryptionKey {
    /// Wraps caller-owned key material of at least 16 bytes.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::KeyMaterialTooShort`] for shorter
    /// material.
    pub fn from_material(material: &[u8]) -> Result<Self, EncryptionError> {
        if material.len() < MIN_KEY_MATERIAL {
            return Err(EncryptionError::KeyMaterialTooShort);
        }
        Ok(Self { material: material.to_vec() })
    }

    /// Generates a fresh 32-byte key from the operating system's
    /// randomness source.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::RandomSourceUnavailable`] when the
    /// source cannot be read. On non-Unix targets the source is a hash of
    /// per-process random state and a counter — weaker, and disclosed
    /// here rather than hidden.
    pub fn generate() -> Result<Self, EncryptionError> {
        Ok(Self { material: os_random_bytes(GENERATED_KEY_BYTES)? })
    }

    /// Derives the domain-separated encryption and MAC subkeys.
    fn derived(&self) -> [[u8; 32]; 2] {
        let prk = hkdf_extract(HKDF_SALT, &self.material);
        hkdf_expand_two(&prk, ENC_KEY_INFO, MAC_KEY_INFO)
    }
}

impl Drop for EncryptionKey {
    fn drop(&mut self) {
        self.material.fill(0);
    }
}

impl fmt::Debug for EncryptionKey {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("EncryptionKey(redacted)")
    }
}

/// Reads `length` fresh bytes from the OS randomness source.
fn os_random_bytes(length: usize) -> Result<Vec<u8>, EncryptionError> {
    #[cfg(unix)]
    {
        use std::io::Read;
        let mut buffer = vec![0u8; length];
        let mut source = std::fs::File::open("/dev/urandom")
            .map_err(|_| EncryptionError::RandomSourceUnavailable)?;
        source.read_exact(&mut buffer).map_err(|_| EncryptionError::RandomSourceUnavailable)?;
        Ok(buffer)
    }
    #[cfg(not(unix))]
    {
        use std::hash::{BuildHasher, RandomState};
        let mut seed = RandomState::new().build_hasher().finish();
        let mut buffer = Vec::with_capacity(length);
        for _ in 0..length {
            seed = seed
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            buffer.push(u8::try_from(seed >> 56).expect("the top byte fits"));
        }
        Ok(buffer)
    }
}

// ---------------------------------------------------------------------
// The bundle container
// ---------------------------------------------------------------------

/// Which transforms a sealed bundle applies to its frame stream.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BundleOptions {
    compression: bool,
}

impl BundleOptions {
    /// Frames stored verbatim.
    #[must_use]
    pub fn plaintext() -> Self {
        Self { compression: false }
    }

    /// Frames stored through the LZSS codec.
    #[must_use]
    pub fn compressed() -> Self {
        Self { compression: true }
    }

    /// Whether compression is on.
    #[must_use]
    pub fn compression(self) -> bool {
        self.compression
    }
}

/// The typed facts of one sealed bundle: the recovery-point identity and
/// the archive members it carries. Like every artifact fact set it is a
/// value, not a handle — it carries no destination path and no key
/// material.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ArtifactBundle {
    identity: String,
    members: Vec<BackupMember>,
}

impl ArtifactBundle {
    /// Returns the recovery-point identity the bundle carries.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the ordered archive members the bundle carries.
    #[must_use]
    pub fn members(&self) -> &[BackupMember] {
        &self.members
    }
}

/// The bundle file name of one identity.
fn bundle_file_name(identity: &str) -> String {
    format!("bundle-{identity}.tbv2b")
}

/// One decoded frame: its name and bytes.
struct Frame {
    name: String,
    data: Vec<u8>,
}

/// Seals the recovery point `identity` in `destination` into a bundle
/// artifact, transformed per `options` and encrypted when a `key` is
/// supplied.
///
/// The bundle is written through a temporary file and renamed into place,
/// so a bundle is either entirely absent or entirely sealed. Sealing with
/// a key draws a fresh nonce, so two seals of the same point differ in
/// bytes and both open with the same key.
///
/// # Errors
///
/// Returns the typed failure for an unsealable identity, an unreadable
/// point or destination, or a randomness source that cannot yield a nonce.
pub fn seal_artifact_bundle(
    destination: &Path,
    identity: &str,
    options: BundleOptions,
    key: Option<&EncryptionKey>,
) -> Result<ArtifactBundle, EncryptionError> {
    validate_identity(identity).map_err(|_| EncryptionError::Malformed)?;
    let manifest =
        load_recovery_point(destination, identity).map_err(|_| EncryptionError::Malformed)?;
    let point = destination.join(point_directory(identity));

    let mut stream = Vec::new();
    let manifest_bytes = std::fs::read(point.join(MANIFEST_FILE))
        .map_err(|_| EncryptionError::DestinationUnavailable)?;
    append_frame(&mut stream, MANIFEST_FILE, &manifest_bytes);
    let mut members = Vec::new();
    for member in manifest.members() {
        let data = std::fs::read(point.join(member.name()))
            .map_err(|_| EncryptionError::DestinationUnavailable)?;
        append_frame(&mut stream, member.name(), &data);
        members.push(BackupMember::new(
            member.name().to_owned(),
            member.length(),
            member.digest().to_owned(),
        ));
    }

    let mut nonce = [0u8; 16];
    let mut ciphertext = if options.compression { compress(&stream) } else { stream };
    let mut tag = [0u8; TAG_LEN];
    let mut header = Vec::with_capacity(HEADER_FIXED + identity.len());
    header.extend_from_slice(MAGIC);
    header.extend_from_slice(&VERSION.to_le_bytes());
    header.push(if key.is_some() { ENC_HKDF_HMAC_CTR } else { ENC_NONE });
    header.push(if options.compression { CMP_LZSS } else { CMP_NONE });
    if let Some(key) = key {
        let [enc_key, mac_key] = key.derived();
        nonce.copy_from_slice(&os_random_bytes(16)?);
        xor_keystream(&enc_key, &nonce, &mut ciphertext);
        header.extend_from_slice(&nonce);
        header.push(u8::try_from(identity.len()).map_err(|_| EncryptionError::Malformed)?);
        header.extend_from_slice(identity.as_bytes());
        let mut mac = Hmac::new(&mac_key);
        mac.update(&header);
        mac.update(&ciphertext);
        tag = mac.finish();
    } else {
        header.extend_from_slice(&nonce);
        header.push(u8::try_from(identity.len()).map_err(|_| EncryptionError::Malformed)?);
        header.extend_from_slice(identity.as_bytes());
    }

    let mut file_bytes = header;
    file_bytes.extend_from_slice(&tag);
    file_bytes.extend_from_slice(&ciphertext);
    let bundle_path = destination.join(bundle_file_name(identity));
    let staging = destination.join(format!(".bundle-{identity}.tmp-{}", std::process::id()));
    let write = std::fs::write(&staging, &file_bytes)
        .and_then(|()| {
            let file = std::fs::File::open(&staging)?;
            file.sync_all()
        })
        .and_then(|()| std::fs::rename(&staging, &bundle_path));
    write.map_err(|_| EncryptionError::DestinationUnavailable)?;
    Ok(ArtifactBundle { identity: identity.to_owned(), members })
}

/// Opens and fully verifies the bundle of `identity` in `destination`,
/// returning its facts. The frame stream is never written anywhere.
///
/// Verification order is fail-closed: structure, then authentication, then
/// identity binding, then decryption, then decompression, then every
/// framed member's length and digest against the manifest.
///
/// # Errors
///
/// Returns the typed failure for an unreadable, foreign, tampered, or
/// wrongly keyed container.
pub fn open_artifact_bundle(
    destination: &Path,
    identity: &str,
    key: Option<&EncryptionKey>,
) -> Result<ArtifactBundle, EncryptionError> {
    decode_bundle(destination, identity, key).map(|(bundle, _frames)| bundle)
}

/// Extracts the bundle of `identity` into `target` as a complete,
/// loadable recovery-point directory.
///
/// Every byte is authenticated and digest-verified before the first file
/// is written, so a denied or tampered open leaves no partial extraction.
///
/// # Errors
///
/// Returns the typed failure of [`open_artifact_bundle`], plus
/// [`EncryptionError::TargetExists`] when the point directory already
/// exists under `target`.
pub fn extract_artifact_bundle(
    destination: &Path,
    identity: &str,
    key: Option<&EncryptionKey>,
    target: &Path,
) -> Result<ArtifactBundle, EncryptionError> {
    let (bundle, frames) = decode_bundle(destination, identity, key)?;
    let point = target.join(point_directory(identity));
    if point.exists() {
        return Err(EncryptionError::TargetExists);
    }
    std::fs::create_dir_all(target).map_err(|_| EncryptionError::DestinationUnavailable)?;
    std::fs::create_dir(&point).map_err(|_| EncryptionError::DestinationUnavailable)?;
    for frame in &frames {
        let write = std::fs::write(point.join(&frame.name), &frame.data).and_then(|()| {
            let file = std::fs::File::open(point.join(&frame.name))?;
            file.sync_all()
        });
        write.map_err(|_| EncryptionError::DestinationUnavailable)?;
    }
    Ok(bundle)
}

/// Reads, authenticates, decrypts, decompresses, and digest-verifies one
/// bundle file, returning its facts and frames.
fn decode_bundle(
    destination: &Path,
    identity: &str,
    key: Option<&EncryptionKey>,
) -> Result<(ArtifactBundle, Vec<Frame>), EncryptionError> {
    validate_identity(identity).map_err(|_| EncryptionError::Malformed)?;
    let bytes = std::fs::read(destination.join(bundle_file_name(identity)))
        .map_err(|_| EncryptionError::DestinationUnavailable)?;
    if bytes.len() < HEADER_FIXED || &bytes[0..MAGIC.len()] != MAGIC {
        return Err(EncryptionError::UnsupportedFormat);
    }
    let version = u16::from_le_bytes([bytes[7], bytes[8]]);
    if version != VERSION {
        return Err(EncryptionError::UnsupportedFormat);
    }
    let enc = bytes[9];
    let cmp = bytes[10];
    if (enc != ENC_NONE && enc != ENC_HKDF_HMAC_CTR) || (cmp != CMP_NONE && cmp != CMP_LZSS) {
        return Err(EncryptionError::UnsupportedFormat);
    }
    let identity_len = bytes[27] as usize;
    let identity_end = 28 + identity_len;
    let tag_start = identity_end;
    let ciphertext_start = tag_start + TAG_LEN;
    if bytes.len() < ciphertext_start || identity_len != identity.len() {
        return Err(EncryptionError::Malformed);
    }
    let payload = if enc == ENC_HKDF_HMAC_CTR {
        let key = key.ok_or(EncryptionError::KeyRequired)?;
        let [enc_key, mac_key] = key.derived();
        let mut mac = Hmac::new(&mac_key);
        mac.update(&bytes[..tag_start]);
        mac.update(&bytes[ciphertext_start..]);
        if !tags_equal(&mac.finish(), &bytes[tag_start..ciphertext_start]) {
            return Err(EncryptionError::AuthenticationFailed);
        }
        let mut plaintext = bytes[ciphertext_start..].to_vec();
        let mut nonce = [0u8; 16];
        nonce.copy_from_slice(&bytes[11..27]);
        xor_keystream(&enc_key, &nonce, &mut plaintext);
        if cmp == CMP_LZSS {
            decompress(&plaintext).map_err(EncryptionError::Compression)?
        } else {
            plaintext
        }
    } else if cmp == CMP_LZSS {
        decompress(&bytes[ciphertext_start..]).map_err(EncryptionError::Compression)?
    } else {
        bytes[ciphertext_start..].to_vec()
    };
    verify_frames(identity, &payload, &bytes[28..identity_end])
}

/// Parses the frame stream and binds every frame to the manifest's facts.
fn verify_frames(
    identity: &str,
    payload: &[u8],
    encoded_identity: &[u8],
) -> Result<(ArtifactBundle, Vec<Frame>), EncryptionError> {
    if encoded_identity != identity.as_bytes() {
        return Err(EncryptionError::Malformed);
    }
    let mut cursor = 0;
    let mut frames = Vec::new();
    while cursor < payload.len() {
        let name_len = payload[cursor] as usize;
        cursor += 1;
        if cursor + name_len + 8 + TAG_LEN > payload.len() {
            return Err(EncryptionError::Malformed);
        }
        let name = String::from_utf8(payload[cursor..cursor + name_len].to_vec())
            .map_err(|_| EncryptionError::Malformed)?;
        cursor += name_len;
        let length = u64::from_le_bytes(
            payload[cursor..cursor + 8].try_into().map_err(|_| EncryptionError::Malformed)?,
        );
        cursor += 8;
        let stored_digest: [u8; 32] =
            payload[cursor..cursor + 32].try_into().map_err(|_| EncryptionError::Malformed)?;
        cursor += 32;
        let end = cursor
            .checked_add(usize::try_from(length).map_err(|_| EncryptionError::Malformed)?)
            .ok_or(EncryptionError::Malformed)?;
        if end > payload.len() {
            return Err(EncryptionError::Malformed);
        }
        let data = payload[cursor..end].to_vec();
        let mut hasher = Sha256::new();
        hasher.update(&data);
        if hasher.finish() != stored_digest {
            return Err(EncryptionError::ChecksumMismatch);
        }
        frames.push(Frame { name, data });
        cursor = end;
    }
    let Some((manifest_frame, member_frames)) = frames.split_first() else {
        return Err(EncryptionError::Malformed);
    };
    if manifest_frame.name != MANIFEST_FILE {
        return Err(EncryptionError::Malformed);
    }
    let manifest_text =
        std::str::from_utf8(&manifest_frame.data).map_err(|_| EncryptionError::Malformed)?;
    let manifest = parse_manifest(manifest_text).map_err(|_| EncryptionError::Malformed)?;
    if manifest.identity() != identity {
        return Err(EncryptionError::Malformed);
    }
    if member_frames.len() != manifest.members().len() {
        return Err(EncryptionError::Malformed);
    }
    let mut members = Vec::new();
    for (frame, member) in member_frames.iter().zip(manifest.members()) {
        let length = u64::try_from(frame.data.len()).map_err(|_| EncryptionError::Malformed)?;
        if frame.name != member.name()
            || length != member.length()
            || to_hex(&sha256(&frame.data)) != member.digest()
        {
            return Err(EncryptionError::ChecksumMismatch);
        }
        members.push(BackupMember::new(
            member.name().to_owned(),
            member.length(),
            member.digest().to_owned(),
        ));
    }
    Ok((ArtifactBundle { identity: identity.to_owned(), members }, frames))
}

/// Appends one frame to the stream: name, length, digest, data.
fn append_frame(stream: &mut Vec<u8>, name: &str, data: &[u8]) {
    stream.push(u8::try_from(name.len()).expect("bounded member names"));
    stream.extend_from_slice(name.as_bytes());
    stream.extend_from_slice(&u64::try_from(data.len()).expect("bounded members").to_le_bytes());
    stream.extend_from_slice(&sha256(data));
    stream.extend_from_slice(data);
}

/// Digests `data` in one step.
fn sha256(data: &[u8]) -> [u8; 32] {
    let mut hasher = Sha256::new();
    hasher.update(data);
    hasher.finish()
}
