//! Durable ownership identity for one engine location.
//!
//! The owner-controlled metadata file binds a location to one environment and
//! typed database scope. Existing locations may acquire the record once
//! through the explicitly local-development open path; a scoped production
//! open can never adopt a local record or a foreign location.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
};

#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;

use crate::{
    identity::{DatabaseUlid, ProjectUlid, TenantUlid},
    regions::RegionCode,
};

use super::sqlite::DATABASE_FILE;

const MAGIC: &[u8; 8] = b"TBOwner1";
const VERSION: u8 = 1;
const MODE_LOCAL: u8 = 0;
const MODE_PRODUCTION: u8 = 1;
const MAX_RECORD_BYTES: usize = 512;
const MAX_RECORD_BYTES_U64: u64 = 512;
const MAX_ENVIRONMENT_BYTES: usize = 96;
const FILE_NAME: &str = "owner.tbv1";

/// A bounded environment identity used in the durable owner tuple.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct EnvironmentName(String);

impl EnvironmentName {
    /// Creates a strict environment identity. It is metadata, never a path.
    ///
    /// # Errors
    ///
    /// Returns [`OwnershipError::InvalidEnvironment`] for empty, oversized,
    /// or unsafe text.
    pub fn try_new(value: impl AsRef<str>) -> Result<Self, OwnershipError> {
        let value = value.as_ref();
        if value.is_empty() || value.len() > MAX_ENVIRONMENT_BYTES {
            return Err(OwnershipError::InvalidEnvironment);
        }
        if !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        {
            return Err(OwnershipError::InvalidEnvironment);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical environment text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for EnvironmentName {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// The canonical durable owner tuple for an engine location.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseOwner {
    environment: EnvironmentName,
    tenant: TenantUlid,
    project: ProjectUlid,
    database: DatabaseUlid,
    region: RegionCode,
}

impl DatabaseOwner {
    /// Builds a typed owner tuple from its canonical environment text.
    ///
    /// # Errors
    ///
    /// Returns [`OwnershipError::InvalidEnvironment`] for invalid text.
    pub fn try_new(
        environment: impl AsRef<str>,
        tenant: TenantUlid,
        project: ProjectUlid,
        database: DatabaseUlid,
        region: RegionCode,
    ) -> Result<Self, OwnershipError> {
        Ok(Self::new(EnvironmentName::try_new(environment)?, tenant, project, database, region))
    }

    /// Builds a typed owner tuple.
    #[must_use]
    pub const fn new(
        environment: EnvironmentName,
        tenant: TenantUlid,
        project: ProjectUlid,
        database: DatabaseUlid,
        region: RegionCode,
    ) -> Self {
        Self { environment, tenant, project, database, region }
    }

    /// Mints the explicit non-production owner used by compatibility opens.
    ///
    /// # Panics
    ///
    /// Never panics; the built-in environment string is a compile-time-valid
    /// value.
    #[must_use]
    pub fn local_development() -> Self {
        Self::new(
            EnvironmentName::try_new("local-development").expect("constant environment"),
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
    }

    /// Returns the environment identity.
    #[must_use]
    pub fn environment(&self) -> &EnvironmentName {
        &self.environment
    }

    /// Returns the owning tenant identity.
    #[must_use]
    pub const fn tenant(&self) -> TenantUlid {
        self.tenant
    }

    /// Returns the owning Project identity.
    #[must_use]
    pub const fn project(&self) -> ProjectUlid {
        self.project
    }

    /// Returns the owning database identity.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.database
    }

    /// Returns the canonical home Region.
    #[must_use]
    pub const fn region(&self) -> RegionCode {
        self.region
    }
}

/// Whether an engine open is local-development or scoped production.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum OwnershipMode {
    LocalDevelopment,
    Production,
}

/// Why durable owner metadata could not be opened or validated.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OwnershipError {
    /// The environment contains invalid or unbounded identity text.
    InvalidEnvironment,
    /// The location or owner metadata path is not a safe regular location.
    InvalidLocation,
    /// The directory contains bytes that are not a `TerraBase` location.
    ForeignLocation,
    /// The owner record is truncated, oversized, malformed, or improperly protected.
    Corrupt,
    /// The supplied owner differs from the durable owner tuple.
    Mismatch,
    /// The local and production ownership modes differ.
    ModeMismatch,
    /// An owner metadata operation could not complete.
    Unavailable,
}

/// Loads the location owner, creating it exactly once for a local open.
pub(crate) fn load_or_create(
    location: &Path,
    expected: Option<&DatabaseOwner>,
    mode: OwnershipMode,
) -> Result<DatabaseOwner, OwnershipError> {
    validate_location(location)?;
    let path = location.join(FILE_NAME);
    if path.symlink_metadata().is_ok_and(|metadata| metadata.file_type().is_symlink()) {
        return Err(OwnershipError::InvalidLocation);
    }
    if path.exists() {
        let (owner, stored_mode) = read_record(&path)?;
        if stored_mode != mode {
            return Err(OwnershipError::ModeMismatch);
        }
        if expected.is_some_and(|wanted| wanted != &owner) {
            return Err(OwnershipError::Mismatch);
        }
        return Ok(owner);
    }

    let owner = expected.cloned().unwrap_or_else(DatabaseOwner::local_development);
    let temp = unique_temp_path(location);
    let mut file = create_private(&temp)?;
    let record = encode_record(&owner, mode);
    file.write_all(&record).map_err(|_| OwnershipError::Unavailable)?;
    file.sync_data().map_err(|_| OwnershipError::Unavailable)?;
    drop(file);
    match fs::rename(&temp, &path) {
        Ok(()) => {
            fs::File::open(location)
                .and_then(|directory| directory.sync_data())
                .map_err(|_| OwnershipError::Unavailable)?;
        }
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
            let _ = fs::remove_file(&temp);
            let (actual, stored_mode) = read_record(&path)?;
            if stored_mode != mode {
                return Err(OwnershipError::ModeMismatch);
            }
            if actual != owner {
                return Err(OwnershipError::Mismatch);
            }
            return Ok(actual);
        }
        Err(_) => {
            let _ = fs::remove_file(&temp);
            return Err(OwnershipError::Unavailable);
        }
    }
    Ok(owner)
}

/// Loads an existing location owner without creating directories or metadata.
pub(crate) fn load_existing(
    location: &Path,
    expected: Option<&DatabaseOwner>,
    mode: OwnershipMode,
) -> Result<DatabaseOwner, OwnershipError> {
    validate_location(location)?;
    let path = location.join(FILE_NAME);
    let (owner, stored_mode) = read_record(&path)?;
    if stored_mode != mode {
        return Err(OwnershipError::ModeMismatch);
    }
    if expected.is_some_and(|wanted| wanted != &owner) {
        return Err(OwnershipError::Mismatch);
    }
    Ok(owner)
}

/// Returns the stable binding carried by the owner record, when one exists.
///
/// `SQLite` stores this value alongside its schema so replacing the database
/// file cannot silently turn into a new database under the old owner. The
/// canonical record bytes are hashed rather than exposing the owner tuple to
/// the storage module.
pub(crate) fn database_binding(location: &Path) -> Result<Option<String>, OwnershipError> {
    let path = location.join(FILE_NAME);
    match fs::symlink_metadata(&path) {
        Ok(metadata) if metadata.file_type().is_symlink() => {
            return Err(OwnershipError::InvalidLocation);
        }
        Ok(metadata) if !metadata.is_file() => {
            return Err(OwnershipError::ForeignLocation);
        }
        Ok(_) => {}
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(OwnershipError::Unavailable),
    }
    let (owner, mode) = read_record(&path)?;
    let record = encode_record(&owner, mode);
    Ok(Some(format!("owner-{:016x}", checksum(&record))))
}

/// Verifies the source record for the local-stack physical backup importer.
pub(crate) fn validate_local_restore_source(source: &Path) -> Result<(), OwnershipError> {
    let metadata = fs::symlink_metadata(source).map_err(|_| OwnershipError::InvalidLocation)?;
    if !metadata.is_dir() || metadata.file_type().is_symlink() {
        return Err(OwnershipError::InvalidLocation);
    }
    // A running Litestream process may keep scratch files beside the database.
    // Reading its owner record does not adopt or open those files.
    let _ = database_binding(source)?.ok_or(OwnershipError::ForeignLocation)?;
    let (_, mode) = read_record(&source.join(FILE_NAME))?;
    if mode != OwnershipMode::LocalDevelopment {
        return Err(OwnershipError::ModeMismatch);
    }
    Ok(())
}

fn validate_location(location: &Path) -> Result<(), OwnershipError> {
    let metadata = fs::symlink_metadata(location).map_err(|_| OwnershipError::InvalidLocation)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(OwnershipError::InvalidLocation);
    }
    // A location consists only of its durable owner and SQLite's files. The
    // owner is the TerraBase identity; SQLite's own header identifies the
    // database. Unknown files and orphaned SQLite files are never adopted.
    let entries = fs::read_dir(location).map_err(|_| OwnershipError::Unavailable)?;
    let mut has_owner = false;
    let mut has_database = false;
    let mut has_companion = false;
    for entry in entries {
        let entry = entry.map_err(|_| OwnershipError::Unavailable)?;
        let name = entry.file_name();
        let entry_type = entry.file_type().map_err(|_| OwnershipError::Unavailable)?;
        if entry_type.is_symlink() {
            if name == FILE_NAME {
                return Err(OwnershipError::InvalidLocation);
            }
            return Err(OwnershipError::ForeignLocation);
        }
        if name == FILE_NAME {
            if !entry_type.is_file() {
                return Err(OwnershipError::ForeignLocation);
            }
            has_owner = true;
            continue;
        }
        if name.to_str().is_some_and(|name| name.starts_with("owner.tbv1.tmp-")) {
            if !entry_type.is_file() {
                return Err(OwnershipError::ForeignLocation);
            }
            continue;
        }
        if name == DATABASE_FILE {
            if !entry_type.is_file() {
                return Err(OwnershipError::ForeignLocation);
            }
            has_database = true;
            continue;
        }
        if name
            .to_str()
            .and_then(|name| name.strip_prefix(DATABASE_FILE))
            .is_some_and(|suffix| matches!(suffix, "-wal" | "-shm" | "-journal"))
        {
            if !entry_type.is_file() {
                return Err(OwnershipError::ForeignLocation);
            }
            has_companion = true;
            continue;
        }
        // Encrypted SQLite keeps its authenticated companion, generation
        // files, and per-artifact lock stubs beside the logical database.
        // They are all regular files owned by the encrypted VFS and must
        // survive a Member restart so the owner record can be revalidated.
        let encrypted_artifact = name.to_str().is_some_and(encrypted_artifact_name);
        if encrypted_artifact {
            if !entry_type.is_file() {
                return Err(OwnershipError::ForeignLocation);
            }
            has_companion = true;
            continue;
        }
        return Err(OwnershipError::ForeignLocation);
    }
    if (has_database || has_companion) && !has_owner {
        return Err(OwnershipError::ForeignLocation);
    }
    if has_companion && !has_database {
        return Err(OwnershipError::ForeignLocation);
    }
    Ok(())
}

fn encrypted_artifact_name(name: &str) -> bool {
    [DATABASE_FILE, "terrabase.sqlite3-wal", "terrabase.sqlite3-journal"].iter().any(|base| {
        let Some(suffix) = name.strip_prefix(base) else {
            return false;
        };
        if matches!(suffix, ".tbe" | ".tbe.lock" | ".tbe.manifest-next") {
            return true;
        }
        let Some(generation) = suffix.strip_prefix(".tbe.gen-") else {
            return false;
        };
        generation.len() == 64
            && generation.bytes().all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
    })
}

fn unique_temp_path(location: &Path) -> PathBuf {
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let count = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    location.join(format!("{FILE_NAME}.tmp-{}-{count}", std::process::id()))
}

fn create_private(path: &Path) -> Result<File, OwnershipError> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    std::os::unix::fs::OpenOptionsExt::mode(&mut options, 0o600);
    options.open(path).map_err(|_| OwnershipError::Unavailable)
}

fn encode_record(owner: &DatabaseOwner, mode: OwnershipMode) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(128);
    bytes.extend_from_slice(MAGIC);
    bytes.push(VERSION);
    bytes.push(match mode {
        OwnershipMode::LocalDevelopment => MODE_LOCAL,
        OwnershipMode::Production => MODE_PRODUCTION,
    });
    push_string(&mut bytes, owner.environment.as_str());
    bytes.extend_from_slice(&owner.tenant.as_bytes());
    bytes.extend_from_slice(&owner.project.as_bytes());
    bytes.extend_from_slice(&owner.database.as_bytes());
    bytes.extend_from_slice(&owner.region.as_bytes());
    let checksum = checksum(&bytes);
    bytes.extend_from_slice(&checksum.to_le_bytes());
    bytes
}

fn checksum(bytes: &[u8]) -> u64 {
    bytes.iter().fold(0xcbf2_9ce4_8422_2325_u64, |hash, byte| {
        hash.wrapping_mul(0x0100_0000_01b3).wrapping_add(u64::from(*byte))
    })
}

fn push_string(bytes: &mut Vec<u8>, value: &str) {
    bytes.push(u8::try_from(value.len()).expect("environment bound fits u8"));
    bytes.extend_from_slice(value.as_bytes());
}

fn read_record(path: &Path) -> Result<(DatabaseOwner, OwnershipMode), OwnershipError> {
    let metadata = fs::symlink_metadata(path).map_err(|_| OwnershipError::Unavailable)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > MAX_RECORD_BYTES_U64
    {
        return Err(OwnershipError::Corrupt);
    }
    #[cfg(unix)]
    if metadata.permissions().mode() & 0o077 != 0 {
        return Err(OwnershipError::Corrupt);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(target_os = "linux")]
    std::os::unix::fs::OpenOptionsExt::custom_flags(&mut options, 0x20000);
    let mut file = options.open(path).map_err(|_| OwnershipError::Unavailable)?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes).map_err(|_| OwnershipError::Unavailable)?;
    if bytes.len() > MAX_RECORD_BYTES {
        return Err(OwnershipError::Corrupt);
    }
    if bytes.len() < 8 {
        return Err(OwnershipError::Corrupt);
    }
    let body_len = bytes.len() - 8;
    let expected = u64::from_le_bytes(bytes[body_len..].try_into().expect("fixed checksum"));
    if checksum(&bytes[..body_len]) != expected {
        return Err(OwnershipError::Corrupt);
    }
    let mut reader = RecordReader { bytes: &bytes[..body_len], offset: 0 };
    if reader.take(MAGIC.len()) != Some(MAGIC.as_slice()) || reader.byte() != Some(VERSION) {
        return Err(OwnershipError::Corrupt);
    }
    let mode = match reader.byte() {
        Some(MODE_LOCAL) => OwnershipMode::LocalDevelopment,
        Some(MODE_PRODUCTION) => OwnershipMode::Production,
        _ => return Err(OwnershipError::Corrupt),
    };
    let environment =
        EnvironmentName::try_new(reader.string()?).map_err(|_| OwnershipError::Corrupt)?;
    let tenant = TenantUlid::from_binary(reader.take(16).ok_or(OwnershipError::Corrupt)?)
        .map_err(|_| OwnershipError::Corrupt)?;
    let project = ProjectUlid::from_binary(reader.take(16).ok_or(OwnershipError::Corrupt)?)
        .map_err(|_| OwnershipError::Corrupt)?;
    let database = DatabaseUlid::from_binary(reader.take(16).ok_or(OwnershipError::Corrupt)?)
        .map_err(|_| OwnershipError::Corrupt)?;
    let region = RegionCode::from_wire_bytes(
        reader.take(3).ok_or(OwnershipError::Corrupt)?.try_into().expect("fixed width"),
    )
    .map_err(|_| OwnershipError::Corrupt)?;
    if reader.remaining() != 0 {
        return Err(OwnershipError::Corrupt);
    }
    Ok((DatabaseOwner::new(environment, tenant, project, database, region), mode))
}

struct RecordReader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> RecordReader<'a> {
    fn byte(&mut self) -> Option<u8> {
        let byte = *self.bytes.get(self.offset)?;
        self.offset += 1;
        Some(byte)
    }

    fn take(&mut self, length: usize) -> Option<&'a [u8]> {
        let end = self.offset.checked_add(length)?;
        let value = self.bytes.get(self.offset..end)?;
        self.offset = end;
        Some(value)
    }

    fn string(&mut self) -> Result<&'a str, OwnershipError> {
        let length = usize::from(self.byte().ok_or(OwnershipError::Corrupt)?);
        std::str::from_utf8(self.take(length).ok_or(OwnershipError::Corrupt)?)
            .map_err(|_| OwnershipError::Corrupt)
    }

    fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.offset)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn location(label: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "terrabase-owner-{label}-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("location");
        path
    }

    #[test]
    fn local_owner_is_created_once_and_recovered() {
        let path = location("restart");
        let first = load_or_create(&path, None, OwnershipMode::LocalDevelopment).expect("create");
        fs::write(path.join(DATABASE_FILE), b"sqlite-owned-by-test").expect("database fixture");
        let second = load_or_create(&path, None, OwnershipMode::LocalDevelopment).expect("read");
        assert_eq!(first, second);
        assert!(!path.join("format").exists(), "ownership needs no backend selector");
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn sqlite_files_without_an_owner_are_never_adopted() {
        let path = location("orphan");
        fs::write(path.join(DATABASE_FILE), b"orphaned").expect("database fixture");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::ForeignLocation)
        );
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn owner_mismatch_and_mode_mismatch_fail_closed() {
        let path = location("mismatch");
        let owner = DatabaseOwner::local_development();
        load_or_create(&path, Some(&owner), OwnershipMode::Production).expect("create");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::ModeMismatch)
        );
        let other = DatabaseOwner::local_development();
        assert_eq!(
            load_or_create(&path, Some(&other), OwnershipMode::Production),
            Err(OwnershipError::Mismatch)
        );
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn malformed_and_oversized_owner_records_fail_closed() {
        let path = location("corrupt");
        let owner_path = path.join(FILE_NAME);
        fs::write(&owner_path, vec![0_u8; MAX_RECORD_BYTES + 1]).expect("write");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::Corrupt)
        );
        fs::write(&owner_path, b"bad").expect("truncate");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::Corrupt)
        );
        let _ = fs::remove_dir_all(path);
    }

    #[cfg(unix)]
    #[test]
    fn owner_symlink_is_rejected() {
        let path = location("symlink");
        let target = path.join("missing-target");
        std::os::unix::fs::symlink(&target, path.join(FILE_NAME)).expect("symlink");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::InvalidLocation)
        );
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn encrypted_vfs_artifacts_are_owned_and_strictly_named() {
        let path = location("encrypted-artifacts");
        load_or_create(&path, None, OwnershipMode::LocalDevelopment).expect("owner");
        let generation = "a".repeat(64);
        for name in [
            "terrabase.sqlite3.tbe",
            "terrabase.sqlite3.tbe.lock",
            "terrabase.sqlite3.tbe.manifest-next",
            "terrabase.sqlite3-wal.tbe",
            "terrabase.sqlite3-wal.tbe.lock",
            "terrabase.sqlite3-wal.tbe.manifest-next",
            "terrabase.sqlite3-journal.tbe",
            "terrabase.sqlite3-journal.tbe.lock",
            "terrabase.sqlite3-journal.tbe.manifest-next",
        ] {
            fs::write(path.join(name), b"artifact").expect("artifact");
        }
        fs::write(path.join(format!("terrabase.sqlite3.tbe.gen-{generation}")), b"artifact")
            .expect("generation");
        for base in ["terrabase.sqlite3-wal", "terrabase.sqlite3-journal"] {
            fs::write(path.join(format!("{base}.tbe.gen-{generation}")), b"artifact")
                .expect("generation");
        }
        fs::write(path.join(DATABASE_FILE), b"sqlite-owned-by-test").expect("database");
        load_or_create(&path, None, OwnershipMode::LocalDevelopment).expect("reopen");

        for name in [
            "terrabase.sqlite3.tbe.gen-A",
            "terrabase.sqlite3.tbe.gen-abc",
            "terrabase.sqlite3-wal.tbe.gen-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.extra",
            "terrabase.sqlite3.other",
        ] {
            fs::write(path.join(name), b"foreign").expect("foreign");
            assert_eq!(
                load_or_create(&path, None, OwnershipMode::LocalDevelopment),
                Err(OwnershipError::ForeignLocation)
            );
            fs::remove_file(path.join(name)).expect("remove foreign");
        }
        let symlink = path.join("terrabase.sqlite3-wal.tbe");
        fs::remove_file(&symlink).expect("remove artifact");
        fs::create_dir(&symlink).expect("directory");
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::ForeignLocation)
        );
        fs::remove_dir(&symlink).expect("remove directory");
        #[cfg(unix)]
        std::os::unix::fs::symlink(path.join("missing"), &symlink).expect("symlink");
        #[cfg(unix)]
        assert_eq!(
            load_or_create(&path, None, OwnershipMode::LocalDevelopment),
            Err(OwnershipError::ForeignLocation)
        );
        let _ = fs::remove_dir_all(path);
    }
}
