//! Monotonic retry-expiry authority independent of manifest fallback.

use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
use std::path::{Path, PathBuf};
use std::{ffi::OsStr, io::ErrorKind};

use crate::StorageError;

const MAGIC: &[u8; 8] = b"TBFLO001";
const VERSION: u16 = 1;
const RECORD_BYTES: usize = 8 + 2 + 2 + 8 + 8 + 4;
pub(crate) const PENDING_BYTES: usize = RECORD_BYTES;
const GENESIS_MARKER: &str = "genesis.pending";
const GENESIS_TEMPS: [&str; 2] = ["floor.0.genesis.tmp", "floor.1.genesis.tmp"];

#[derive(Clone, Copy)]
struct Record {
    sequence: u64,
    floor_ms: u64,
}

pub(crate) struct FloorAuthority {
    current: Record,
    newer_slot: usize,
}

#[derive(Clone, Copy, Eq, PartialEq)]
enum GenesisState {
    Empty,
    LegacyFirst,
    Marked,
    Established,
}

#[derive(Clone, Copy, Eq, PartialEq)]
enum SlotState {
    Missing,
    ValidZero,
    TornOwnedRegular,
    ValidNonzero,
    Unsafe,
}

fn slot_path(directory: &Path, slot: usize) -> PathBuf {
    directory.join(format!("floor.{slot}"))
}

fn pending_path(directory: &Path) -> PathBuf {
    directory.join("floor.pending")
}

fn genesis_marker_path(directory: &Path) -> PathBuf {
    directory.join(GENESIS_MARKER)
}

fn sync_directory(directory: &Path) -> Result<(), StorageError> {
    File::open(directory)?.sync_all()?;
    Ok(())
}

fn encode(record: Record) -> [u8; RECORD_BYTES] {
    let mut bytes = [0_u8; RECORD_BYTES];
    bytes[..8].copy_from_slice(MAGIC);
    bytes[8..10].copy_from_slice(&VERSION.to_le_bytes());
    bytes[12..20].copy_from_slice(&record.sequence.to_le_bytes());
    bytes[20..28].copy_from_slice(&record.floor_ms.to_le_bytes());
    let crc = crc32c::crc32c(&bytes[..RECORD_BYTES - 4]);
    bytes[RECORD_BYTES - 4..].copy_from_slice(&crc.to_le_bytes());
    bytes
}

fn decode(bytes: &[u8; RECORD_BYTES]) -> Option<Record> {
    if &bytes[..8] != MAGIC
        || u16::from_le_bytes(bytes[8..10].try_into().ok()?) != VERSION
        || bytes[10..12] != [0, 0]
        || crc32c::crc32c(&bytes[..RECORD_BYTES - 4])
            != u32::from_le_bytes(bytes[RECORD_BYTES - 4..].try_into().ok()?)
    {
        return None;
    }
    Some(Record {
        sequence: u64::from_le_bytes(bytes[12..20].try_into().ok()?),
        floor_ms: u64::from_le_bytes(bytes[20..28].try_into().ok()?),
    })
}

fn open_owned_regular(path: &Path) -> Result<File, StorageError> {
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.is_file() || metadata.nlink() != 1 {
        return Err(StorageError::Corrupt(
            "expiry-floor authority type or links".into(),
        ));
    }
    Ok(file)
}

fn read_slot(directory: &Path, slot: usize) -> Result<Record, StorageError> {
    let path = slot_path(directory, slot);
    // Nonblocking open also prevents a substituted FIFO from hanging recovery.
    let mut file = open_owned_regular(&path)?;
    let metadata = file.metadata()?;
    if metadata.len() != RECORD_BYTES as u64 {
        return Err(StorageError::Corrupt(
            "expiry-floor authority length".into(),
        ));
    }
    let mut bytes = [0_u8; RECORD_BYTES];
    file.read_exact(&mut bytes)?;
    decode(&bytes)
        .ok_or_else(|| StorageError::Corrupt("expiry-floor authority checksum or version".into()))
}

fn genesis_state(directory: &Path, local_document: bool) -> Result<GenesisState, StorageError> {
    let mut count = 0;
    let mut first = false;
    let mut marker = false;
    let mut unexpected = false;
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let file_name = entry.file_name();
        let name = file_name.as_os_str();
        if name == OsStr::new("shard.lock") {
            continue;
        }
        // Only the local document entry point passes this flag, after OpenGate
        // has validated the exact marker and rejected pending marker files.
        if local_document && name == OsStr::new(crate::local_mode::MARKER_NAME) {
            continue;
        }
        count += 1;
        if name == OsStr::new("floor.0") {
            first = true;
        } else if name == OsStr::new(GENESIS_MARKER) {
            marker = true;
        } else if name != OsStr::new("floor.1")
            && !GENESIS_TEMPS.iter().any(|temp| name == OsStr::new(temp))
        {
            unexpected = true;
        }
    }
    if marker {
        if unexpected {
            return Err(StorageError::Corrupt(
                "unexpected file during expiry-floor genesis".into(),
            ));
        }
        Ok(GenesisState::Marked)
    } else if count == 0 {
        Ok(GenesisState::Empty)
    } else if count == 1 && first {
        Ok(GenesisState::LegacyFirst)
    } else {
        Ok(GenesisState::Established)
    }
}

fn classify_slot(directory: &Path, slot: usize) -> Result<SlotState, StorageError> {
    let path = slot_path(directory, slot);
    let metadata = match std::fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(SlotState::Missing),
        Err(error) => return Err(error.into()),
    };
    if !metadata.is_file() || metadata.nlink() != 1 {
        return Ok(SlotState::Unsafe);
    }
    let mut file = open_owned_regular(&path)?;
    if file.metadata()?.len() != RECORD_BYTES as u64 {
        return Ok(SlotState::TornOwnedRegular);
    }
    let mut bytes = [0_u8; RECORD_BYTES];
    file.read_exact(&mut bytes)?;
    Ok(match decode(&bytes) {
        Some(Record {
            sequence: 0,
            floor_ms: 0,
        }) => SlotState::ValidZero,
        Some(_) => SlotState::ValidNonzero,
        None => SlotState::TornOwnedRegular,
    })
}

fn validate_genesis_marker(directory: &Path) -> Result<(), StorageError> {
    let path = genesis_marker_path(directory);
    let metadata = std::fs::symlink_metadata(&path)?;
    if !metadata.is_dir() {
        return Err(StorageError::Corrupt(
            "expiry-floor genesis marker type".into(),
        ));
    }
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_DIRECTORY | libc::O_NONBLOCK)
        .open(&path)?;
    if !file.metadata()?.is_dir() || std::fs::read_dir(&path)?.next().transpose()?.is_some() {
        return Err(StorageError::Corrupt(
            "expiry-floor genesis marker content".into(),
        ));
    }
    Ok(())
}

fn owned_genesis_temp(path: &Path) -> Result<bool, StorageError> {
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error.into()),
    };
    if !metadata.is_file() || metadata.nlink() != 1 {
        return Err(StorageError::Corrupt(
            "expiry-floor genesis temp type or links".into(),
        ));
    }
    let file = open_owned_regular(path)?;
    if file.metadata()?.len() != metadata.len() {
        return Err(StorageError::Corrupt(
            "expiry-floor genesis temp changed".into(),
        ));
    }
    Ok(true)
}

fn create_genesis_marker(directory: &Path) -> Result<(), StorageError> {
    let marker = genesis_marker_path(directory);
    std::fs::create_dir(&marker)?;
    crate::crash_at("after_genesis_marker_create");
    sync_directory(&marker)?;
    crate::crash_at("after_genesis_marker_contents_sync");
    sync_directory(directory)?;
    crate::crash_at("after_genesis_marker_sync");
    Ok(())
}

fn write_genesis_slot(
    directory: &Path,
    slot: usize,
    initial: &[u8; RECORD_BYTES],
) -> Result<(), StorageError> {
    const HOOKS: [[&str; 5]; 2] = [
        [
            "after_genesis_slot0_temp_create",
            "after_genesis_slot0_temp_prefix",
            "after_genesis_slot0_temp_sync",
            "after_genesis_slot0_rename",
            "after_genesis_slot0_parent_sync",
        ],
        [
            "after_genesis_slot1_temp_create",
            "after_genesis_slot1_temp_prefix",
            "after_genesis_slot1_temp_sync",
            "after_genesis_slot1_rename",
            "after_genesis_slot1_parent_sync",
        ],
    ];
    let temp = directory.join(GENESIS_TEMPS[slot]);
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(&temp)?;
    crate::crash_at(HOOKS[slot][0]);
    let midpoint = RECORD_BYTES / 2;
    file.write_all(&initial[..midpoint])?;
    crate::crash_at(HOOKS[slot][1]);
    file.write_all(&initial[midpoint..])?;
    file.sync_all()?;
    crate::crash_at(HOOKS[slot][2]);
    std::fs::rename(&temp, slot_path(directory, slot))?;
    crate::crash_at(HOOKS[slot][3]);
    sync_directory(directory)?;
    crate::crash_at(HOOKS[slot][4]);
    Ok(())
}

fn complete_genesis(directory: &Path) -> Result<(), StorageError> {
    validate_genesis_marker(directory)?;
    let states = [classify_slot(directory, 0)?, classify_slot(directory, 1)?];
    if states
        .iter()
        .any(|state| matches!(state, SlotState::Unsafe | SlotState::ValidNonzero))
    {
        return Err(StorageError::Corrupt(
            "unsafe expiry-floor genesis slot".into(),
        ));
    }
    let temps = GENESIS_TEMPS.map(|name| directory.join(name));
    let present = [
        owned_genesis_temp(&temps[0])?,
        owned_genesis_temp(&temps[1])?,
    ];
    for (path, present) in temps.iter().zip(present) {
        if present {
            std::fs::remove_file(path)?;
            sync_directory(directory)?;
        }
    }
    let initial = encode(Record {
        sequence: 0,
        floor_ms: 0,
    });
    for (slot, state) in states.into_iter().enumerate() {
        if state != SlotState::ValidZero {
            write_genesis_slot(directory, slot, &initial)?;
        }
    }
    if classify_slot(directory, 0)? != SlotState::ValidZero
        || classify_slot(directory, 1)? != SlotState::ValidZero
    {
        return Err(StorageError::Corrupt(
            "incomplete expiry-floor genesis".into(),
        ));
    }
    for slot in 0..2 {
        open_owned_regular(&slot_path(directory, slot))?.sync_all()?;
    }
    sync_directory(directory)?;
    crate::crash_at("after_genesis_slots_sync");
    std::fs::remove_dir(genesis_marker_path(directory))?;
    crate::crash_at("after_genesis_marker_remove");
    sync_directory(directory)?;
    crate::crash_at("after_genesis_marker_remove_sync");
    Ok(())
}

impl FloorAuthority {
    pub(crate) fn load_or_init(
        directory: &Path,
        local_document: bool,
    ) -> Result<Self, StorageError> {
        match genesis_state(directory, local_document)? {
            GenesisState::Empty => {
                create_genesis_marker(directory)?;
                complete_genesis(directory)?;
            }
            GenesisState::LegacyFirst => {
                if classify_slot(directory, 0)? != SlotState::ValidZero {
                    return Err(StorageError::Corrupt("expiry-floor initial state".into()));
                }
                // The old first slot must be durable before the marker authorizes repair.
                open_owned_regular(&slot_path(directory, 0))?.sync_all()?;
                create_genesis_marker(directory)?;
                complete_genesis(directory)?;
            }
            GenesisState::Marked => complete_genesis(directory)?,
            GenesisState::Established => {
                for slot in 0..2 {
                    match std::fs::symlink_metadata(slot_path(directory, slot)) {
                        Err(error) if error.kind() == ErrorKind::NotFound => {
                            return Err(StorageError::Corrupt(
                                "missing expiry-floor authority slot".into(),
                            ));
                        }
                        other => {
                            other?;
                        }
                    }
                }
            }
        }
        let slots = [read_slot(directory, 0)?, read_slot(directory, 1)?];
        let newer_slot = if slots[0].sequence == 0 && slots[1].sequence == 0 {
            if slots[0].floor_ms != 0 || slots[1].floor_ms != 0 {
                return Err(StorageError::Corrupt("expiry-floor initial state".into()));
            }
            1
        } else if slots[0].sequence.checked_add(1) == Some(slots[1].sequence)
            && slots[0].floor_ms <= slots[1].floor_ms
        {
            1
        } else if slots[1].sequence.checked_add(1) == Some(slots[0].sequence)
            && slots[1].floor_ms <= slots[0].floor_ms
        {
            0
        } else {
            return Err(StorageError::Corrupt("expiry-floor authority order".into()));
        };
        let pending = pending_path(directory);
        if pending.exists() {
            std::fs::remove_file(pending)?;
            File::open(directory)?.sync_all()?;
        }
        Ok(Self {
            current: slots[newer_slot],
            newer_slot,
        })
    }

    pub(crate) const fn floor_ms(&self) -> u64 {
        self.current.floor_ms
    }

    /// Publish a new high-water mark before any manifest may depend on it.
    pub(crate) fn advance(&mut self, directory: &Path, floor_ms: u64) -> Result<(), StorageError> {
        if floor_ms <= self.current.floor_ms {
            return Ok(());
        }
        let next = Record {
            sequence: self
                .current
                .sequence
                .checked_add(1)
                .ok_or(StorageError::Capacity("expiry-floor sequence exhausted"))?,
            floor_ms,
        };
        let target_slot = 1 - self.newer_slot;
        let pending = pending_path(directory);
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&pending)?;
        file.write_all(&encode(next))?;
        file.sync_all()?;
        std::fs::rename(&pending, slot_path(directory, target_slot))?;
        File::open(directory)?.sync_all()?;
        self.current = next;
        self.newer_slot = target_slot;
        Ok(())
    }
}
