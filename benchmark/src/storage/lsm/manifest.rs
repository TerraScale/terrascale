//! The segment manifest of the LSM engine (P1).
//!
//! The manifest names the immutable segments that make up the durable
//! database, the next segment identity, and the WAL checkpoint — the
//! sequence through which every entry lives in a named segment. Publication
//! is the crash-safe pointer pattern the W3-1 root store proved: write a
//! complete temporary file, fsync it, atomically rename it over the live
//! manifest, then fsync the directory. A torn publication can only ever
//! leave the previous epoch's file in place.
//!
//! Activated recovery treats the manifest as an authority. Missing or corrupt
//! manifests and named segments fail closed after WAL reclamation; rebuilding
//! from whatever files happen to remain can silently omit an acknowledged
//! segment.

use std::{
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
};

use super::super::{codec, root};
use super::{segment::SegmentMeta, wal_frontier::StreamAnchor};
use crate::storage::faults::{FaultPlan, FaultPoint};
use crate::{
    encryption::{
        ArtifactCipher, ArtifactKind, Compression, DatabaseKeyVersion, EncryptionContext,
        KeyPurpose, KeyScope, LocalFileKeyProvider, ProviderMode, envelope_length_prefix,
    },
    storage::DatabaseOwner,
};

const MANIFEST_MAGIC: &[u8; 8] = b"TBLSMMF1";
const ACTIVE_MANIFEST_MAGIC: &[u8; 8] = b"TBLSMMF2";
const ACTIVE_MANIFEST_SCHEMA: u16 = 1;
const ACTIVE_MANIFEST_DOMAIN: &[u8] = b"terrabase.lsm.manifest.v2\0";
const LEGACY_MANIFEST_FILE: &str = "MANIFEST";
const LEGACY_MANIFEST_TEMP: &str = "MANIFEST.tmp";
const ACTIVE_MANIFEST_FILE: &str = "MANIFEST.active";
const ACTIVE_MANIFEST_TEMP: &str = "MANIFEST.active.tmp";
const MANIFEST_ORDINAL_MAGIC: &[u8; 8] = b"TBMFNOR1";
const MANIFEST_ORDINAL_BYTES: usize = 8 + 16 + 32;
const ENCRYPTED_MAGIC: &[u8; 4] = b"TBE1";

/// Local artifact-key inputs retained by the LSM owner for manifest I/O.
/// The provider reloads the key for each operation, so this configuration
/// never retains raw key bytes.
#[derive(Clone)]
pub(super) struct ManifestEncryption {
    owner: DatabaseOwner,
    provider: LocalFileKeyProvider,
    key_version: DatabaseKeyVersion,
}

impl ManifestEncryption {
    pub(super) fn new(
        owner: DatabaseOwner,
        provider: LocalFileKeyProvider,
        key_version: DatabaseKeyVersion,
    ) -> Self {
        Self { owner, provider, key_version }
    }

    fn cipher(&self, dir: &Path, path: &Path) -> io::Result<(ArtifactCipher, PathBuf)> {
        let key = self.provider.load_or_create().map_err(encryption_io)?;
        let scope = KeyScope::new_with_project(
            self.owner.environment().as_str(),
            self.owner.tenant(),
            self.owner.project(),
            self.owner.database(),
            KeyPurpose::Data,
        )
        .map_err(encryption_io)?;
        let context = EncryptionContext::for_artifact(
            &scope,
            ArtifactKind::Manifest,
            self.owner.database().value(),
            self.key_version,
            1,
            Compression::None,
        )
        .map_err(encryption_io)?;
        let ordinal_path = dir.join("manifest.ordinal");
        let next = load_or_create_ordinal(&ordinal_path, ordinal_path.exists())?;
        let cipher = ArtifactCipher::with_next_ordinal(ProviderMode::LocalFile, key, context, next)
            .map_err(encryption_io)?;
        let _ = path;
        Ok((cipher, ordinal_path))
    }
}

/// The prior WAL prefix an activated manifest has replaced with segments.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct WalCheckpoint {
    pub(super) anchor: StreamAnchor,
    pub(super) next_epoch: u64,
}

/// The activated WAL epoch and optional proof of the preceding checkpoint.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct ActiveWalManifest {
    pub(super) live_epoch: u64,
    pub(super) checkpoint: Option<WalCheckpoint>,
}

/// The published manifest state.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(super) struct SegmentManifest {
    /// Monotonic publication epoch.
    pub(super) epoch: u64,
    /// The next segment identity to allocate.
    pub(super) next_segment_id: u64,
    /// The sequence through which every entry lives in a named segment.
    pub(super) wal_checkpoint: u64,
    /// Present only after the coordinated LSM frontier activation. Legacy
    /// manifests keep their byte encoding unchanged.
    pub(super) active_wal: Option<ActiveWalManifest>,
    /// The named segments, in no particular order.
    pub(super) segments: Vec<SegmentMeta>,
}

impl SegmentManifest {
    /// Allocates the next segment identity.
    pub(super) fn allocate_segment_id(&mut self) -> u64 {
        let id = self.next_segment_id;
        self.next_segment_id += 1;
        id
    }

    /// Encodes the manifest body.
    fn encode(&self) -> Vec<u8> {
        if self.active_wal.is_some() {
            return self.encode_active();
        }
        self.encode_legacy()
    }

    fn encode_legacy(&self) -> Vec<u8> {
        let mut body = Vec::new();
        body.extend_from_slice(MANIFEST_MAGIC);
        codec::push_u64(&mut body, self.epoch);
        codec::push_u64(&mut body, self.next_segment_id);
        codec::push_u64(&mut body, self.wal_checkpoint);
        #[allow(clippy::cast_possible_truncation)]
        let count = self.segments.len() as u32;
        codec::push_u32(&mut body, count);
        for segment in &self.segments {
            codec::push_u64(&mut body, segment.id);
            codec::push_u16(&mut body, segment.level);
            codec::push_u16(&mut body, 0);
            codec::push_u64(&mut body, segment.entry_count);
            codec::push_u64(&mut body, segment.byte_len);
            #[allow(clippy::cast_possible_truncation)]
            let min_len = segment.min_key.len() as u32;
            codec::push_u32(&mut body, min_len);
            body.extend_from_slice(&segment.min_key);
            #[allow(clippy::cast_possible_truncation)]
            let max_len = segment.max_key.len() as u32;
            codec::push_u32(&mut body, max_len);
            body.extend_from_slice(&segment.max_key);
        }
        let mut out = body.clone();
        codec::push_u64(&mut out, codec::fnv1a(&body));
        out
    }

    fn encode_active(&self) -> Vec<u8> {
        let active = self.active_wal.expect("active encoding has WAL state");
        let mut body = Vec::new();
        body.extend_from_slice(ACTIVE_MANIFEST_MAGIC);
        codec::push_u16(&mut body, ACTIVE_MANIFEST_SCHEMA);
        body.extend_from_slice(&[0_u8; 6]);
        codec::push_u64(&mut body, self.epoch);
        codec::push_u64(&mut body, self.next_segment_id);
        codec::push_u64(&mut body, self.wal_checkpoint);
        codec::push_u64(&mut body, active.live_epoch);
        if let Some(checkpoint) = active.checkpoint {
            codec::push_u8(&mut body, 1);
            body.extend_from_slice(&[0_u8; 7]);
            codec::push_u64(&mut body, checkpoint.anchor.epoch);
            codec::push_u64(&mut body, checkpoint.anchor.bytes);
            codec::push_u64(&mut body, checkpoint.anchor.frame_count);
            body.extend_from_slice(&checkpoint.anchor.chain_head);
            codec::push_u64(&mut body, checkpoint.next_epoch);
        } else {
            codec::push_u8(&mut body, 0);
            body.extend_from_slice(&[0_u8; 7]);
        }
        encode_segments(&mut body, &self.segments);
        let checksum = active_manifest_checksum(&body);
        body.extend_from_slice(&checksum);
        body
    }

    /// Decodes and verifies one manifest body.
    fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.starts_with(ACTIVE_MANIFEST_MAGIC) {
            return Self::decode_active(bytes);
        }
        Self::decode_legacy(bytes)
    }

    fn decode_legacy(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 8 + 8 + 8 + 8 + 8 || &bytes[..8] != MANIFEST_MAGIC {
            return None;
        }
        let body_end = bytes.len() - 8;
        let expected = codec::fnv1a(&bytes[..body_end]);
        let stored = u64::from_le_bytes(bytes[body_end..].try_into().expect("eight bytes"));
        if expected != stored {
            return None;
        }
        let mut reader = codec::Reader::new(&bytes[8..body_end]);
        let epoch = reader.read_u64()?;
        let next_segment_id = reader.read_u64()?;
        let wal_checkpoint = reader.read_u64()?;
        let segments = decode_segments(&mut reader)?;
        if reader.remaining() != 0 {
            return None;
        }
        validate_manifest(epoch, next_segment_id, &segments)?;
        Some(Self { epoch, next_segment_id, wal_checkpoint, active_wal: None, segments })
    }

    fn decode_active(bytes: &[u8]) -> Option<Self> {
        const MINIMUM_BYTES: usize = 8 + 2 + 6 + 8 + 8 + 8 + 8 + 8 + 4 + 32;
        if bytes.len() < MINIMUM_BYTES || &bytes[..8] != ACTIVE_MANIFEST_MAGIC {
            return None;
        }
        let checksum_start = bytes.len().checked_sub(32)?;
        if bytes[checksum_start..] != active_manifest_checksum(&bytes[..checksum_start]) {
            return None;
        }
        let mut reader = codec::Reader::new(&bytes[8..checksum_start]);
        if reader.read_u16()? != ACTIVE_MANIFEST_SCHEMA || reader.take(6)? != [0_u8; 6] {
            return None;
        }
        let epoch = reader.read_u64()?;
        let next_segment_id = reader.read_u64()?;
        let wal_checkpoint = reader.read_u64()?;
        let live_epoch = reader.read_u64()?;
        if live_epoch == 0 {
            return None;
        }
        let checkpoint = match reader.read_u8()? {
            0 => {
                if reader.take(7)? != [0_u8; 7] {
                    return None;
                }
                None
            }
            1 => {
                if reader.take(7)? != [0_u8; 7] {
                    return None;
                }
                let prior_epoch = reader.read_u64()?;
                let prior_bytes = reader.read_u64()?;
                let prior_frames = reader.read_u64()?;
                let mut prior_head = [0_u8; 32];
                prior_head.copy_from_slice(reader.take(32)?);
                let next_epoch = reader.read_u64()?;
                if prior_epoch.checked_add(1) != Some(next_epoch) || next_epoch != live_epoch {
                    return None;
                }
                Some(WalCheckpoint {
                    anchor: StreamAnchor {
                        epoch: prior_epoch,
                        bytes: prior_bytes,
                        frame_count: prior_frames,
                        chain_head: prior_head,
                    },
                    next_epoch,
                })
            }
            _ => return None,
        };
        let segments = decode_segments(&mut reader)?;
        if reader.remaining() != 0 {
            return None;
        }
        validate_manifest(epoch, next_segment_id, &segments)?;
        Some(Self {
            epoch,
            next_segment_id,
            wal_checkpoint,
            active_wal: Some(ActiveWalManifest { live_epoch, checkpoint }),
            segments,
        })
    }

    /// Publishes the manifest atomically. Legacy manifests retain their
    /// byte-exact `MANIFEST` authority; activated manifests use a sidecar so
    /// an N-1 reader never mistakes an expand-stage manifest for absence.
    pub(super) fn publish(&self, dir: &Path) -> std::io::Result<()> {
        self.publish_with_encryption(dir, None)
    }

    /// Publishes the manifest, encrypting the complete native encoding when
    /// an artifact configuration is supplied.
    pub(super) fn publish_with_encryption(
        &self,
        dir: &Path,
        encryption: Option<&ManifestEncryption>,
    ) -> std::io::Result<()> {
        let encoded = self.encode();
        let encoded = if let Some(encryption) = encryption {
            let (mut cipher, ordinal_path) = encryption.cipher(dir, dir)?;
            let reservation = cipher.reserve_ordinal().map_err(encryption_io)?;
            persist_ordinal(&ordinal_path, reservation.next_ordinal())?;
            cipher.seal_reserved(reservation, &encoded).map_err(encryption_io)?
        } else {
            encoded
        };
        let (temp_name, live_name) = if self.active_wal.is_some() {
            (ACTIVE_MANIFEST_TEMP, ACTIVE_MANIFEST_FILE)
        } else {
            (LEGACY_MANIFEST_TEMP, LEGACY_MANIFEST_FILE)
        };
        let tmp: PathBuf = dir.join(temp_name);
        let live: PathBuf = dir.join(live_name);
        {
            let mut file = fs::File::create(&tmp)?;
            file.write_all(&encoded)?;
            file.sync_all()?;
        }
        fs::rename(&tmp, &live)?;
        root::sync_dir(dir)
    }

    /// Publishes the expand-stage active sidecar while exposing every
    /// physical crash boundary to the activation fault matrix.
    pub(super) fn publish_activation(
        &self,
        dir: &Path,
        faults: &mut FaultPlan,
    ) -> std::io::Result<()> {
        self.publish_activation_with_encryption(dir, faults, None)
    }

    /// Publishes the expand-stage active sidecar with authenticated bytes.
    pub(super) fn publish_activation_with_encryption(
        &self,
        dir: &Path,
        faults: &mut FaultPlan,
        encryption: Option<&ManifestEncryption>,
    ) -> std::io::Result<()> {
        if self.active_wal.is_none() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "activation manifest lacks active WAL state",
            ));
        }
        let encoded = self.encode();
        let encoded = if let Some(encryption) = encryption {
            let (mut cipher, ordinal_path) = encryption.cipher(dir, dir)?;
            let reservation = cipher.reserve_ordinal().map_err(encryption_io)?;
            persist_ordinal(&ordinal_path, reservation.next_ordinal())?;
            cipher.seal_reserved(reservation, &encoded).map_err(encryption_io)?
        } else {
            encoded
        };
        let tmp = dir.join(ACTIVE_MANIFEST_TEMP);
        let live = dir.join(ACTIVE_MANIFEST_FILE);
        let mut file = fs::File::create(&tmp)?;
        fire_activation_fault(faults, FaultPoint::ActivationManifestTempCreated)?;
        file.write_all(&encoded)?;
        fire_activation_fault(faults, FaultPoint::ActivationManifestWritten)?;
        file.sync_all()?;
        fire_activation_fault(faults, FaultPoint::ActivationManifestSynced)?;
        fs::rename(&tmp, &live)?;
        fire_activation_fault(faults, FaultPoint::ActivationManifestRenamed)?;
        root::sync_dir(dir)?;
        fire_activation_fault(faults, FaultPoint::ActivationManifestDirectorySynced)
    }

    /// Reads and verifies the manifest at `dir/MANIFEST`, if one exists and
    /// verifies.
    pub(super) fn read(dir: &Path) -> std::io::Result<Option<Self>> {
        Self::read_path(&dir.join(LEGACY_MANIFEST_FILE))
    }

    /// Reads and authenticates the legacy manifest path.
    pub(super) fn read_with_encryption(
        dir: &Path,
        encryption: &ManifestEncryption,
    ) -> std::io::Result<Option<Self>> {
        Self::read_path_with_encryption(&dir.join(LEGACY_MANIFEST_FILE), encryption)
    }

    /// Reads the active sidecar. The fallback accepts locations written by
    /// the short-lived pre-sidecar implementation, which stored `TBLSMMF2`
    /// directly in `MANIFEST`.
    pub(super) fn read_active(dir: &Path) -> std::io::Result<Option<Self>> {
        match Self::read_path(&dir.join(ACTIVE_MANIFEST_FILE))? {
            Some(manifest) => Ok(Some(manifest)),
            None => Self::read_path(&dir.join(LEGACY_MANIFEST_FILE)),
        }
    }

    /// Reads and authenticates the active sidecar, falling back to the
    /// pre-sidecar active manifest path when needed.
    pub(super) fn read_active_with_encryption(
        dir: &Path,
        encryption: &ManifestEncryption,
    ) -> std::io::Result<Option<Self>> {
        match Self::read_path_with_encryption(&dir.join(ACTIVE_MANIFEST_FILE), encryption)? {
            Some(manifest) => Ok(Some(manifest)),
            None => Self::read_path_with_encryption(&dir.join(LEGACY_MANIFEST_FILE), encryption),
        }
    }

    /// Reports whether the active or legacy manifest path starts with an
    /// encryption envelope. This is only a format probe; authentication is
    /// performed by [`Self::read_active_with_encryption`].
    pub(super) fn has_encrypted_bytes(dir: &Path, active: bool) -> io::Result<bool> {
        let primary = if active { ACTIVE_MANIFEST_FILE } else { LEGACY_MANIFEST_FILE };
        let bytes = match fs::read(dir.join(primary)) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound && active => {
                match fs::read(dir.join(LEGACY_MANIFEST_FILE)) {
                    Ok(bytes) => bytes,
                    Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
                    Err(error) => return Err(error),
                }
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
            Err(error) => return Err(error),
        };
        Ok(bytes.starts_with(ENCRYPTED_MAGIC))
    }

    fn read_path(path: &Path) -> std::io::Result<Option<Self>> {
        let bytes = match fs::read(path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        Self::decode(&bytes)
            .map(Some)
            .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::InvalidData, "manifest damage"))
    }

    fn read_path_with_encryption(
        path: &Path,
        encryption: &ManifestEncryption,
    ) -> std::io::Result<Option<Self>> {
        let bytes = match fs::read(path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        let envelope_len = envelope_length_prefix(&bytes).map_err(encryption_io)?;
        if envelope_len != bytes.len() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "manifest envelope trailing bytes",
            ));
        }
        let (cipher, _) = encryption.cipher(
            path.parent().ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidData, "manifest has no parent")
            })?,
            path,
        )?;
        let plaintext = cipher.open(&bytes).map_err(encryption_io)?;
        SegmentManifest::decode(&plaintext)
            .map(Some)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "manifest damage"))
    }
}

fn encryption_io(error: crate::encryption::EncryptionError) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

fn ordinal_checksum(body: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrabase.lsm.manifest.ordinal.v1\0");
    hasher.update(body);
    *hasher.finalize().as_bytes()
}

fn load_or_create_ordinal(path: &Path, required: bool) -> io::Result<u128> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink()
                || !metadata.is_file()
                || metadata.len() != MANIFEST_ORDINAL_BYTES as u64
            {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "unsafe manifest ordinal"));
            }
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt as _;
                if metadata.permissions().mode() & 0o077 != 0 {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "unsafe manifest ordinal permissions",
                    ));
                }
            }
            let mut options = fs::OpenOptions::new();
            options.read(true);
            #[cfg(any(target_os = "android", target_os = "linux"))]
            {
                use std::os::unix::fs::OpenOptionsExt as _;
                options.custom_flags(0x20000);
            }
            let mut file = options.open(path)?;
            let opened = file.metadata()?;
            #[cfg(unix)]
            if !same_file(&metadata, &opened) {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "manifest ordinal changed"));
            }
            let mut bytes = [0_u8; MANIFEST_ORDINAL_BYTES];
            file.read_exact(&mut bytes)?;
            let mut trailing = [0_u8; 1];
            if file.read(&mut trailing)? != 0 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "manifest ordinal trailing",
                ));
            }
            if bytes[..8] != *MANIFEST_ORDINAL_MAGIC
                || bytes[24..] != ordinal_checksum(&bytes[..24])
            {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "corrupt manifest ordinal"));
            }
            Ok(u128::from_be_bytes(bytes[8..24].try_into().expect("ordinal bytes")))
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound && !required => {
            persist_ordinal(path, 0)?;
            Ok(0)
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            Err(io::Error::new(io::ErrorKind::InvalidData, "missing manifest ordinal"))
        }
        Err(error) => Err(error),
    }
}

fn persist_ordinal(path: &Path, ordinal: u128) -> io::Result<()> {
    let mut body = Vec::with_capacity(MANIFEST_ORDINAL_BYTES);
    body.extend_from_slice(MANIFEST_ORDINAL_MAGIC);
    body.extend_from_slice(&ordinal.to_be_bytes());
    body.extend_from_slice(&ordinal_checksum(&body));
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let temp = path.with_file_name(format!(
        "{}.tmp-{}-{}",
        path.file_name().and_then(|name| name.to_str()).unwrap_or("manifest.ordinal"),
        std::process::id(),
        COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    ));
    let mut options = fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    let mut file = options.open(&temp)?;
    if let Err(error) = file.write_all(&body).and_then(|()| file.sync_all()) {
        let _ = fs::remove_file(&temp);
        return Err(error);
    }
    drop(file);
    if let Err(error) = fs::rename(&temp, path) {
        let _ = fs::remove_file(&temp);
        return Err(error);
    }
    if let Some(parent) = path.parent() {
        fs::File::open(parent)?.sync_data()?;
    }
    Ok(())
}

#[cfg(unix)]
fn same_file(left: &fs::Metadata, right: &fs::Metadata) -> bool {
    use std::os::unix::fs::MetadataExt as _;
    left.dev() == right.dev() && left.ino() == right.ino()
}

fn fire_activation_fault(faults: &mut FaultPlan, point: FaultPoint) -> std::io::Result<()> {
    faults.fire(point).map_err(|_| std::io::Error::other("injected activation publication failure"))
}

fn encode_segments(body: &mut Vec<u8>, segments: &[SegmentMeta]) {
    #[allow(clippy::cast_possible_truncation)]
    let count = segments.len() as u32;
    codec::push_u32(body, count);
    for segment in segments {
        codec::push_u64(body, segment.id);
        codec::push_u16(body, segment.level);
        codec::push_u16(body, 0);
        codec::push_u64(body, segment.entry_count);
        codec::push_u64(body, segment.byte_len);
        #[allow(clippy::cast_possible_truncation)]
        let min_len = segment.min_key.len() as u32;
        codec::push_u32(body, min_len);
        body.extend_from_slice(&segment.min_key);
        #[allow(clippy::cast_possible_truncation)]
        let max_len = segment.max_key.len() as u32;
        codec::push_u32(body, max_len);
        body.extend_from_slice(&segment.max_key);
    }
}

fn decode_segments(reader: &mut codec::Reader<'_>) -> Option<Vec<SegmentMeta>> {
    let count = reader.read_u32()? as usize;
    if count > 1_000_000 {
        return None;
    }
    let mut segments = Vec::with_capacity(count.min(1024));
    for _ in 0..count {
        let id = reader.read_u64()?;
        let level = reader.read_u16()?;
        if reader.read_u16()? != 0 {
            return None;
        }
        let entry_count = reader.read_u64()?;
        let byte_len = reader.read_u64()?;
        let min_len = reader.read_u32()? as usize;
        let min_key = reader.take(min_len)?.to_owned();
        let max_len = reader.read_u32()? as usize;
        let max_key = reader.take(max_len)?.to_owned();
        if min_key > max_key || min_key.len() > 4096 || max_key.len() > 4096 {
            return None;
        }
        segments.push(SegmentMeta { id, level, entry_count, byte_len, min_key, max_key });
    }
    Some(segments)
}

fn validate_manifest(epoch: u64, next_segment_id: u64, segments: &[SegmentMeta]) -> Option<()> {
    let mut ids = std::collections::BTreeSet::new();
    if segments.iter().any(|segment| {
        !ids.insert(segment.id)
            || segment.id >= next_segment_id
            || segment.entry_count == 0
            || segment.byte_len == 0
    }) || (epoch == 0 && !segments.is_empty())
    {
        return None;
    }
    Some(())
}

fn active_manifest_checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(ACTIVE_MANIFEST_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(bytes: &[u8]) -> String {
        use std::fmt::Write as _;

        let mut output = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            write!(output, "{byte:02x}").expect("writing to a String cannot fail");
        }
        output
    }

    fn meta(id: u64, level: u16, min: &[u8], max: &[u8]) -> SegmentMeta {
        SegmentMeta {
            id,
            level,
            entry_count: id * 10,
            byte_len: id * 100,
            min_key: min.to_vec(),
            max_key: max.to_vec(),
        }
    }

    fn dir(tag: &str) -> std::path::PathBuf {
        let dir =
            std::env::temp_dir().join(format!("terrabase-lsm-mf-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn publish_and_read_round_trip() {
        let dir = dir("rt");
        let manifest = SegmentManifest {
            epoch: 3,
            next_segment_id: 9,
            wal_checkpoint: 4242,
            active_wal: None,
            segments: vec![meta(1, 0, b"a", b"m"), meta(7, 1, b"n", b"z")],
        };
        manifest.publish(&dir).unwrap();
        let read = SegmentManifest::read(&dir).unwrap();
        assert_eq!(read, Some(manifest));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn active_publish_uses_the_sidecar_and_preserves_the_legacy_manifest() {
        let dir = dir("active-sidecar");
        let legacy = SegmentManifest {
            epoch: 3,
            next_segment_id: 9,
            wal_checkpoint: 4242,
            active_wal: None,
            segments: vec![meta(1, 0, b"a", b"m")],
        };
        legacy.publish(&dir).unwrap();
        let legacy_bytes = std::fs::read(dir.join(LEGACY_MANIFEST_FILE)).unwrap();
        let mut active = legacy.clone();
        active.epoch += 1;
        active.active_wal = Some(ActiveWalManifest { live_epoch: 1, checkpoint: None });
        active.publish(&dir).unwrap();
        assert_eq!(std::fs::read(dir.join(LEGACY_MANIFEST_FILE)).unwrap(), legacy_bytes);
        assert_eq!(SegmentManifest::read(&dir).unwrap(), Some(legacy));
        assert_eq!(SegmentManifest::read_active(&dir).unwrap(), Some(active));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn corruption_is_an_integrity_error() {
        let dir = dir("corrupt");
        let manifest = SegmentManifest {
            epoch: 1,
            next_segment_id: 2,
            wal_checkpoint: 5,
            active_wal: None,
            segments: vec![],
        };
        manifest.publish(&dir).unwrap();
        let path = dir.join("MANIFEST");
        let mut bytes = std::fs::read(&path).unwrap();
        let mid = bytes.len() / 2;
        bytes[mid] ^= 0xFF;
        std::fs::write(&path, &bytes).unwrap();
        assert_eq!(
            SegmentManifest::read(&dir).expect_err("damage is not absence").kind(),
            std::io::ErrorKind::InvalidData
        );
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn segment_ids_allocate_monotonically() {
        let mut manifest = SegmentManifest::default();
        assert_eq!(manifest.allocate_segment_id(), 0);
        assert_eq!(manifest.allocate_segment_id(), 1);
        assert_eq!(manifest.next_segment_id, 2);
    }

    #[test]
    fn legacy_and_active_manifest_encodings_match_the_pinned_golden_vectors() {
        let segments = vec![meta(1, 0, b"a", b"m"), meta(7, 1, b"n", b"z")];
        let legacy = SegmentManifest {
            epoch: 3,
            next_segment_id: 9,
            wal_checkpoint: 42,
            active_wal: None,
            segments: segments.clone(),
        };
        assert_eq!(
            hex(&legacy.encode()),
            "54424c534d4d4631030000000000000009000000000000002a00000000000000020000000100000000000000000000000a0000000000000064000000000000000100000061010000006d0700000000000000010000004600000000000000bc02000000000000010000006e010000007a1cc7b5c5b2beeed8"
        );

        let active = SegmentManifest {
            active_wal: Some(ActiveWalManifest {
                live_epoch: 7,
                checkpoint: Some(WalCheckpoint {
                    anchor: StreamAnchor {
                        epoch: 6,
                        bytes: 1_234,
                        frame_count: 5,
                        chain_head: [0xab; 32],
                    },
                    next_epoch: 7,
                }),
            }),
            ..legacy
        };
        assert_eq!(
            hex(&active.encode()),
            "54424c534d4d46320100000000000000030000000000000009000000000000002a00000000000000070000000000000001000000000000000600000000000000d2040000000000000500000000000000abababababababababababababababababababababababababababababababab0700000000000000020000000100000000000000000000000a0000000000000064000000000000000100000061010000006d0700000000000000010000004600000000000000bc02000000000000010000006e010000007a032e10026603d17ea66f0aad7d7db2c1b5d97845a0e1913786083a5e65f1c1b4"
        );
    }
}
