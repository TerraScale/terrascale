//! Identity marker for an explicitly bootstrapped local document shard.

use std::fs::File;
use std::io::{Read, Write};
use std::os::unix::fs::MetadataExt;
use std::sync::atomic::{AtomicU64, Ordering};

use rustix::fs::{AtFlags, Mode, OFlags, RenameFlags};
use tb_core::{DatabaseId, MembershipEpoch, OriginId, ReplicaId, ShardId, TenantId};

use crate::StorageError;

pub(crate) const MARKER_NAME: &str = "local-document.mode";
pub(crate) const PENDING_PREFIX: &str = "local-document.mode.pending.";
const MAGIC: &[u8; 8] = b"TBLMOD01";
const MARKER_LEN: usize = 132;
static NEXT_TEMP: AtomicU64 = AtomicU64::new(1);

/// The exact identity of one explicitly selected local document shard.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LocalDocumentMode {
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    epoch: MembershipEpoch,
    configuration: [u8; 32],
}

impl LocalDocumentMode {
    /// Bind the local shard to its origin and configuration generation.
    #[must_use]
    pub const fn new(
        tenant: TenantId,
        database: DatabaseId,
        shard: ShardId,
        origin: OriginId,
        epoch: MembershipEpoch,
        configuration: [u8; 32],
    ) -> Self {
        Self {
            tenant,
            database,
            shard,
            origin,
            epoch,
            configuration,
        }
    }

    #[must_use]
    pub const fn tenant(self) -> TenantId {
        self.tenant
    }

    pub(crate) fn valid(self) -> bool {
        self.epoch.get() != 0 && self.origin.incarnation != [0; 16] && self.configuration != [0; 32]
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
        bytes[16..32].copy_from_slice(&self.tenant.to_bytes());
        bytes[32..48].copy_from_slice(&self.database.to_bytes());
        bytes[48..56].copy_from_slice(&self.shard.get().to_le_bytes());
        bytes[56..72].copy_from_slice(&self.origin.replica_id.to_bytes());
        bytes[72..88].copy_from_slice(&self.origin.incarnation);
        bytes[88..96].copy_from_slice(&self.epoch.get().to_le_bytes());
        bytes[96..128].copy_from_slice(&self.configuration);
        let checksum = crc32c::crc32c(&bytes[..128]).to_le_bytes();
        bytes[128..132].copy_from_slice(&checksum);
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
            || bytes[128..132] != crc32c::crc32c(&bytes[..128]).to_le_bytes()
        {
            return Err(StorageError::Corrupt(
                "invalid local document mode marker".into(),
            ));
        }
        let result = Self {
            tenant: TenantId::from_bytes(bytes[16..32].try_into().expect("fixed marker length")),
            database: DatabaseId::from_bytes(
                bytes[32..48].try_into().expect("fixed marker length"),
            ),
            shard: ShardId::new(u64::from_le_bytes(
                bytes[48..56].try_into().expect("fixed marker length"),
            )),
            origin: OriginId {
                replica_id: ReplicaId::from_bytes(
                    bytes[56..72].try_into().expect("fixed marker length"),
                ),
                incarnation: bytes[72..88].try_into().expect("fixed marker length"),
            },
            epoch: MembershipEpoch::new(u64::from_le_bytes(
                bytes[88..96].try_into().expect("fixed marker length"),
            )),
            configuration: bytes[96..128].try_into().expect("fixed marker length"),
        };
        if !result.valid() {
            return Err(StorageError::Corrupt(
                "invalid local document mode identity".into(),
            ));
        }
        Ok(result)
    }
}

fn read_named(target: &File, name: &str) -> Result<Option<LocalDocumentMode>, StorageError> {
    let descriptor = match rustix::fs::openat(
        target,
        name,
        OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::NONBLOCK | OFlags::CLOEXEC,
        Mode::empty(),
    ) {
        Ok(file) => file,
        Err(rustix::io::Errno::NOENT) => return Ok(None),
        Err(error) => return Err(std::io::Error::from(error).into()),
    };
    let mut file = File::from(descriptor);
    let metadata = file.metadata()?;
    if !metadata.is_file() || metadata.nlink() != 1 || metadata.len() != MARKER_LEN as u64 {
        return Err(StorageError::Corrupt(
            "unsafe local document mode marker".into(),
        ));
    }
    let mut bytes = [0_u8; MARKER_LEN];
    file.read_exact(&mut bytes)?;
    if file.metadata()?.len() != MARKER_LEN as u64 {
        return Err(StorageError::Corrupt(
            "local document mode marker changed".into(),
        ));
    }
    let mode = LocalDocumentMode::decode(&bytes)?;
    if name.starts_with(PENDING_PREFIX) {
        // A crashed writer may have left readable bytes before its file sync.
        // Make the resumed bytes durable before the no-replace publish.
        file.sync_all()?;
    }
    Ok(Some(mode))
}

pub(crate) fn read_marker(target: &File) -> Result<Option<LocalDocumentMode>, StorageError> {
    read_named(target, MARKER_NAME)
}

pub(crate) fn read_pending(
    target: &File,
    name: &str,
) -> Result<Option<LocalDocumentMode>, StorageError> {
    if !name.starts_with(PENDING_PREFIX) {
        return Err(StorageError::Corrupt(
            "invalid local document pending name".into(),
        ));
    }
    read_named(target, name)
}

fn create_temp(target: &File) -> Result<(String, File), StorageError> {
    loop {
        let ordinal = NEXT_TEMP.fetch_add(1, Ordering::Relaxed);
        let name = format!("{PENDING_PREFIX}{}.{}", std::process::id(), ordinal);
        match rustix::fs::openat(
            target,
            name.as_str(),
            OFlags::WRONLY | OFlags::CREATE | OFlags::EXCL | OFlags::NOFOLLOW | OFlags::CLOEXEC,
            Mode::RUSR | Mode::WUSR,
        ) {
            Ok(file) => return Ok((name, File::from(file))),
            Err(rustix::io::Errno::EXIST) => {}
            Err(error) => return Err(std::io::Error::from(error).into()),
        }
    }
}

pub(crate) fn publish_marker(
    target: &File,
    mode: LocalDocumentMode,
    pending: Option<&str>,
) -> Result<(), StorageError> {
    let created = if pending.is_none() {
        Some(create_temp(target)?)
    } else {
        None
    };
    let name = pending.unwrap_or_else(|| &created.as_ref().expect("created temp").0);
    let result = (|| {
        if let Some((_, file)) = &created {
            let mut file = file;
            file.write_all(&mode.encode())?;
            file.sync_all()?;
        } else if read_pending(target, name)? != Some(mode) {
            return Err(StorageError::Corrupt(
                "local document pending mode changed".into(),
            ));
        }
        crate::crash_at("after_local_document_mode_temp_sync");
        rustix::fs::renameat_with(target, name, target, MARKER_NAME, RenameFlags::NOREPLACE)
            .map_err(std::io::Error::from)?;
        crate::crash_at("after_local_document_mode_publish_before_target_sync");
        target.sync_all()?;
        crate::crash_at("after_local_document_mode_target_sync");
        Ok::<(), StorageError>(())
    })();
    if result.is_err() && created.is_some() {
        let _ = rustix::fs::unlinkat(target, name, AtFlags::empty());
    }
    result
}
