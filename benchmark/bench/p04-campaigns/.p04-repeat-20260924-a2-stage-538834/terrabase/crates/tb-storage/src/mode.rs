//! Physical-parent gate and local marker for the certified document shard.

use std::fs::{File, OpenOptions};
use std::io::{ErrorKind, Read, Write};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use fs2::FileExt;
use rustix::fs::{AtFlags, Mode, OFlags, RenameFlags};
use tb_core::{DatabaseId, MembershipEpoch, OriginId, ReplicaId, ShardId, TenantId};

use crate::StorageError;
use crate::local::ensure_durable_directory;

const MARKER_NAME: &str = "documents.mode";
const DOCUMENTS_NAME: &str = "documents";
const MAGIC: &[u8; 8] = b"TBDMOD01";
const MARKER_LEN: usize = 148;
static NEXT_TEMP: AtomicU64 = AtomicU64::new(1);

/// The accepted shard and selected local member recorded beside `documents/`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DistributedDocumentMode {
    cluster: [u8; 16],
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    epoch: MembershipEpoch,
    configuration: [u8; 32],
    local_origin: OriginId,
}

impl DistributedDocumentMode {
    /// Bind the signed primitive scope supplied by an accepted authority view.
    #[must_use]
    pub const fn new(
        cluster: [u8; 16],
        tenant: TenantId,
        database: DatabaseId,
        shard: ShardId,
        epoch: MembershipEpoch,
        configuration: [u8; 32],
        local_origin: OriginId,
    ) -> Self {
        Self {
            cluster,
            tenant,
            database,
            shard,
            epoch,
            configuration,
            local_origin,
        }
    }

    #[must_use]
    pub const fn tenant(self) -> TenantId {
        self.tenant
    }

    fn encode(self) -> [u8; MARKER_LEN] {
        let mut bytes = [0_u8; MARKER_LEN];
        bytes[..8].copy_from_slice(MAGIC);
        bytes[8..10].copy_from_slice(&1_u16.to_le_bytes());
        bytes[12..16].copy_from_slice(
            &u32::try_from(MARKER_LEN)
                .expect("fixed marker length")
                .to_le_bytes(),
        );
        bytes[16..32].copy_from_slice(&self.cluster);
        bytes[32..48].copy_from_slice(&self.tenant.to_bytes());
        bytes[48..64].copy_from_slice(&self.database.to_bytes());
        bytes[64..72].copy_from_slice(&self.shard.get().to_le_bytes());
        bytes[72..80].copy_from_slice(&self.epoch.get().to_le_bytes());
        bytes[80..112].copy_from_slice(&self.configuration);
        bytes[112..128].copy_from_slice(&self.local_origin.replica_id.to_bytes());
        bytes[128..144].copy_from_slice(&self.local_origin.incarnation);
        let checksum = crc32c::crc32c(&bytes[..144]);
        bytes[144..].copy_from_slice(&checksum.to_le_bytes());
        bytes
    }

    fn decode(bytes: &[u8]) -> Result<Self, StorageError> {
        if bytes.len() != MARKER_LEN
            || &bytes[..8] != MAGIC
            || bytes[8..10] != 1_u16.to_le_bytes()
            || bytes[10..12] != [0; 2]
            || bytes[12..16]
                != u32::try_from(MARKER_LEN)
                    .expect("fixed marker length")
                    .to_le_bytes()
            || u32::from_le_bytes(bytes[144..148].try_into().expect("fixed marker length"))
                != crc32c::crc32c(&bytes[..144])
        {
            return Err(StorageError::Corrupt("invalid document mode marker".into()));
        }
        Ok(Self {
            cluster: bytes[16..32].try_into().expect("fixed marker length"),
            tenant: TenantId::from_bytes(bytes[32..48].try_into().expect("fixed marker length")),
            database: DatabaseId::from_bytes(
                bytes[48..64].try_into().expect("fixed marker length"),
            ),
            shard: ShardId::new(u64::from_le_bytes(
                bytes[64..72].try_into().expect("fixed marker length"),
            )),
            epoch: MembershipEpoch::new(u64::from_le_bytes(
                bytes[72..80].try_into().expect("fixed marker length"),
            )),
            configuration: bytes[80..112].try_into().expect("fixed marker length"),
            local_origin: OriginId {
                replica_id: ReplicaId::from_bytes(
                    bytes[112..128].try_into().expect("fixed marker length"),
                ),
                incarnation: bytes[128..144].try_into().expect("fixed marker length"),
            },
        })
    }
}

struct LockedParent {
    path: PathBuf,
    file: File,
    device: u64,
    inode: u64,
}

/// Held from mode preflight through shard lock acquisition and WAL recovery.
pub(crate) struct OpenGate {
    requested: PathBuf,
    physical_target: PathBuf,
    target_identity: Option<(u64, u64)>,
    parents: Vec<LockedParent>,
}

impl OpenGate {
    pub(crate) fn ordinary(path: &Path) -> Result<Self, StorageError> {
        let requested = if path.as_os_str().is_empty() {
            Path::new(".")
        } else {
            path
        };
        let requested_parent = nonempty_parent(requested);
        ensure_durable_directory(requested_parent)?;
        let requested_parent = std::fs::canonicalize(requested_parent)?;
        let physical_target = physical_target(requested, &requested_parent)?;
        let physical_parent = physical_target
            .parent()
            .unwrap_or(&physical_target)
            .to_path_buf();
        let target_identity = existing_identity(requested)?;
        let gate = Self::lock(
            requested,
            physical_target,
            target_identity,
            [requested_parent.clone(), physical_parent.clone()],
        )?;
        gate.verify_target()?;
        gate.reject_ordinary_markers()?;
        Ok(gate)
    }

    pub(crate) fn distributed(
        path: &Path,
        mode: DistributedDocumentMode,
    ) -> Result<Self, StorageError> {
        require_normalized_absolute(path)?;
        if path.file_name().is_none_or(|name| name != DOCUMENTS_NAME) {
            return Err(invalid(path, "distributed target must be documents"));
        }
        let parent = nonempty_parent(path);
        require_real_ancestors(parent)?;
        let target_identity = real_target_identity(path)?;
        let physical_parent = std::fs::canonicalize(parent)?;
        if physical_parent != parent {
            return Err(invalid(path, "distributed parent is an alias"));
        }
        let gate = Self::lock(
            path,
            path.to_path_buf(),
            target_identity,
            [physical_parent.clone()],
        )?;
        gate.verify_target()?;
        let marker = physical_parent.join(MARKER_NAME);
        match read_marker(&marker)? {
            Some(record) if record == mode => {}
            Some(_) => return Err(invalid(path, "document mode scope mismatch")),
            None if target_identity.is_some() => {
                return Err(invalid(path, "existing document shard has no mode marker"));
            }
            None => publish_marker(&gate.parents[0].file, mode)?,
        }
        gate.verify_target()?;
        gate.verify_distributed_marker(mode)?;
        Ok(gate)
    }

    fn lock(
        requested: &Path,
        physical_target: PathBuf,
        target_identity: Option<(u64, u64)>,
        parents: impl IntoIterator<Item = PathBuf>,
    ) -> Result<Self, StorageError> {
        let mut paths: Vec<_> = parents.into_iter().collect();
        paths.sort();
        paths.dedup();
        let mut locked = Vec::with_capacity(paths.len());
        for path in paths {
            let file = OpenOptions::new()
                .read(true)
                .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW)
                .open(&path)?;
            let metadata = file.metadata()?;
            if !metadata.is_dir() {
                return Err(invalid(&path, "mode gate parent is not a directory"));
            }
            file.lock_exclusive()?;
            locked.push(LockedParent {
                path,
                file,
                device: metadata.dev(),
                inode: metadata.ino(),
            });
        }
        Ok(Self {
            requested: requested.to_path_buf(),
            physical_target,
            target_identity,
            parents: locked,
        })
    }

    fn verify_parents(&self) -> Result<(), StorageError> {
        for parent in &self.parents {
            let current = std::fs::metadata(&parent.path)?;
            if !current.is_dir()
                || current.dev() != parent.device
                || current.ino() != parent.inode
                || parent.file.metadata()?.dev() != parent.device
                || parent.file.metadata()?.ino() != parent.inode
            {
                return Err(invalid(&self.requested, "mode gate parent changed"));
            }
        }
        Ok(())
    }

    fn verify_target(&self) -> Result<(), StorageError> {
        self.verify_parents()?;
        match std::fs::canonicalize(&self.requested) {
            Ok(current) if current == self.physical_target => {
                let metadata = std::fs::metadata(&self.requested)?;
                if !metadata.is_dir()
                    || self
                        .target_identity
                        .is_some_and(|id| id != (metadata.dev(), metadata.ino()))
                {
                    return Err(invalid(&self.requested, "shard target changed"));
                }
            }
            Err(error) if error.kind() == ErrorKind::NotFound && self.target_identity.is_none() => {
                let parent = std::fs::canonicalize(nonempty_parent(&self.requested))?;
                let name = self
                    .requested
                    .file_name()
                    .ok_or_else(|| invalid(&self.requested, "invalid shard path"))?;
                if parent.join(name) != self.physical_target {
                    return Err(invalid(&self.requested, "shard parent changed"));
                }
            }
            _ => return Err(invalid(&self.requested, "shard target changed")),
        }
        Ok(())
    }

    fn verify_opened(&self) -> Result<(), StorageError> {
        if self.target_identity.is_none() && !self.requested.exists() {
            return Err(invalid(&self.requested, "shard target was not created"));
        }
        self.verify_target()
    }

    fn reject_ordinary_markers(&self) -> Result<(), StorageError> {
        if self
            .requested
            .file_name()
            .is_some_and(|name| name == DOCUMENTS_NAME)
        {
            reject_marker(nonempty_parent(&self.requested), &self.requested)?;
        }
        if self
            .physical_target
            .file_name()
            .is_some_and(|name| name == DOCUMENTS_NAME)
        {
            reject_marker(nonempty_parent(&self.physical_target), &self.requested)?;
        }
        Ok(())
    }

    fn verify_distributed_marker(&self, mode: DistributedDocumentMode) -> Result<(), StorageError> {
        match read_marker(&nonempty_parent(&self.requested).join(MARKER_NAME))? {
            Some(record) if record == mode => Ok(()),
            _ => Err(invalid(&self.requested, "document mode marker changed")),
        }
    }

    /// Recheck target identity and marker absence after ordinary recovery.
    pub(crate) fn verify_opened_ordinary(&self) -> Result<(), StorageError> {
        self.verify_opened()?;
        self.reject_ordinary_markers()
    }

    /// Recheck the real target and mode marker after distributed recovery.
    pub(crate) fn verify_opened_distributed(
        &self,
        mode: DistributedDocumentMode,
    ) -> Result<(), StorageError> {
        self.verify_opened()?;
        if real_target_identity(&self.requested)?.is_none() {
            return Err(invalid(&self.requested, "shard target changed"));
        }
        self.verify_distributed_marker(mode)
    }
}

fn invalid(path: &Path, reason: &'static str) -> StorageError {
    StorageError::InvalidDirectory {
        path: path.to_path_buf(),
        reason,
    }
}

fn nonempty_parent(path: &Path) -> &Path {
    match path.parent() {
        Some(parent) if !parent.as_os_str().is_empty() => parent,
        _ => Path::new("."),
    }
}

fn physical_target(requested: &Path, parent: &Path) -> Result<PathBuf, StorageError> {
    match std::fs::canonicalize(requested) {
        Ok(target) => Ok(target),
        Err(error) if error.kind() == ErrorKind::NotFound => {
            let name = requested
                .file_name()
                .ok_or_else(|| invalid(requested, "invalid shard path"))?;
            Ok(parent.join(name))
        }
        Err(error) => Err(error.into()),
    }
}

fn existing_identity(path: &Path) -> Result<Option<(u64, u64)>, StorageError> {
    match std::fs::metadata(path) {
        Ok(metadata) => Ok(Some((metadata.dev(), metadata.ino()))),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error.into()),
    }
}

fn real_target_identity(path: &Path) -> Result<Option<(u64, u64)>, StorageError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_dir() => Ok(Some((metadata.dev(), metadata.ino()))),
        Ok(_) => Err(invalid(path, "distributed target is not a real directory")),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error.into()),
    }
}

fn reject_marker(parent: &Path, path: &Path) -> Result<(), StorageError> {
    match std::fs::symlink_metadata(parent.join(MARKER_NAME)) {
        Ok(_) => Err(invalid(
            path,
            "distributed document marker requires certified open",
        )),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn require_normalized_absolute(path: &Path) -> Result<(), StorageError> {
    let bytes = path.as_os_str().as_bytes();
    if !path.is_absolute()
        || bytes == b"/"
        || bytes.ends_with(b"/")
        || bytes[1..]
            .split(|byte| *byte == b'/')
            .any(|part| part.is_empty() || part == b"." || part == b"..")
    {
        return Err(invalid(path, "distributed path is not normalized absolute"));
    }
    Ok(())
}

fn require_real_ancestors(path: &Path) -> Result<(), StorageError> {
    let mut current = PathBuf::from("/");
    for component in path.components().skip(1) {
        current.push(component.as_os_str());
        let metadata = std::fs::symlink_metadata(&current)?;
        if !metadata.file_type().is_dir() {
            return Err(invalid(
                &current,
                "distributed path ancestor is not a real directory",
            ));
        }
    }
    Ok(())
}

fn read_marker(path: &Path) -> Result<Option<DistributedDocumentMode>, StorageError> {
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    };
    if !metadata.file_type().is_file()
        || metadata.nlink() != 1
        || metadata.len() != MARKER_LEN as u64
    {
        return Err(invalid(path, "unsafe document mode marker"));
    }
    let mut file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    let opened = file.metadata()?;
    if opened.dev() != metadata.dev() || opened.ino() != metadata.ino() || opened.nlink() != 1 {
        return Err(invalid(path, "document mode marker changed"));
    }
    let mut bytes = [0_u8; MARKER_LEN];
    file.read_exact(&mut bytes)?;
    if file.metadata()?.len() != MARKER_LEN as u64 {
        return Err(invalid(path, "document mode marker changed"));
    }
    Ok(Some(DistributedDocumentMode::decode(&bytes)?))
}

fn create_marker_temp(parent: &File) -> Result<(String, File), StorageError> {
    loop {
        let ordinal = NEXT_TEMP.fetch_add(1, Ordering::Relaxed);
        let temp = format!("documents.mode.pending.{}.{}", std::process::id(), ordinal);
        match rustix::fs::openat(
            parent,
            temp.as_str(),
            OFlags::WRONLY | OFlags::CREATE | OFlags::EXCL | OFlags::NOFOLLOW | OFlags::CLOEXEC,
            Mode::RUSR | Mode::WUSR,
        ) {
            Ok(file) => return Ok((temp, File::from(file))),
            Err(rustix::io::Errno::EXIST) => {}
            Err(error) => return Err(std::io::Error::from(error).into()),
        }
    }
}

fn publish_marker(parent: &File, mode: DistributedDocumentMode) -> Result<(), StorageError> {
    let (temp, mut file) = create_marker_temp(parent)?;
    let result = (|| {
        file.write_all(&mode.encode())?;
        file.sync_all()?;
        crate::crash_at("after_document_mode_temp_sync");
        rustix::fs::renameat_with(
            parent,
            temp.as_str(),
            parent,
            MARKER_NAME,
            RenameFlags::NOREPLACE,
        )
        .map_err(std::io::Error::from)?;
        crate::crash_at("after_document_mode_publish_before_parent_sync");
        parent.sync_all()?;
        crate::crash_at("after_document_mode_parent_sync");
        Ok::<(), StorageError>(())
    })();
    if result.is_err() {
        let _ = rustix::fs::unlinkat(parent, temp.as_str(), AtFlags::empty());
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mode() -> DistributedDocumentMode {
        DistributedDocumentMode::new(
            [1; 16],
            TenantId::from_bytes([2; 16]),
            DatabaseId::from_bytes([3; 16]),
            ShardId::new(0x0102_0304_0506_0708),
            MembershipEpoch::new(0x1112_1314_1516_1718),
            [4; 32],
            OriginId {
                replica_id: ReplicaId::from_bytes([5; 16]),
                incarnation: [6; 16],
            },
        )
    }

    #[test]
    fn marker_has_exact_canonical_fields_and_checksum() {
        let expected = mode();
        let encoded = expected.encode();
        assert_eq!(&encoded[..8], b"TBDMOD01");
        assert_eq!(&encoded[8..10], &1_u16.to_le_bytes());
        assert_eq!(&encoded[10..12], &[0, 0]);
        assert_eq!(&encoded[12..16], &148_u32.to_le_bytes());
        assert_eq!(&encoded[16..32], &[1; 16]);
        assert_eq!(&encoded[32..48], &[2; 16]);
        assert_eq!(&encoded[48..64], &[3; 16]);
        assert_eq!(&encoded[64..72], &0x0102_0304_0506_0708_u64.to_le_bytes());
        assert_eq!(&encoded[72..80], &0x1112_1314_1516_1718_u64.to_le_bytes());
        assert_eq!(&encoded[80..112], &[4; 32]);
        assert_eq!(&encoded[112..128], &[5; 16]);
        assert_eq!(&encoded[128..144], &[6; 16]);
        assert_eq!(
            &encoded[144..148],
            &crc32c::crc32c(&encoded[..144]).to_le_bytes(),
        );
        assert_eq!(DistributedDocumentMode::decode(&encoded).unwrap(), expected);
    }

    #[test]
    fn marker_rejects_length_version_flags_and_crc_changes() {
        let encoded = mode().encode();
        for offset in [0, 8, 10, 12, 64, 144] {
            let mut changed = encoded;
            changed[offset] ^= 1;
            assert!(DistributedDocumentMode::decode(&changed).is_err());
        }
        assert!(DistributedDocumentMode::decode(&encoded[..147]).is_err());
        let mut extended = encoded.to_vec();
        extended.push(0);
        assert!(DistributedDocumentMode::decode(&extended).is_err());
    }
}
