//! Logical physical replication I/O. Encrypted production files never pass
//! through the operating system's plaintext `SQLite` stub.

use super::sqlite::{encrypted_file::EncryptedFile, encrypted_vfs::EncryptedVfs};
use std::{
    fs::{File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::Path,
    sync::{Arc, Mutex},
};

/// Keeps an authenticated database VFS available for offline follower or restore
/// I/O. Production callers supply the same owner-bound source as `EngineConfig`.
pub struct EncryptedStorage {
    registration: Arc<EncryptedVfs>,
}

impl EncryptedStorage {
    /// Registers an encrypted location before opening physical database files.
    ///
    /// # Errors
    /// Fails on invalid locations or conflicting live key sources.
    pub fn register(
        location: &Path,
        keys: Arc<dyn crate::encryption::DatabaseKeySource>,
    ) -> io::Result<Self> {
        std::fs::create_dir_all(location)?;
        Ok(Self {
            registration: EncryptedVfs::register(&location.join("terrabase.sqlite3"), keys)?,
        })
    }
}

/// A seekable logical SQLite/WAL file, encrypted when its location has an
/// admitted VFS. Plaintext files are permitted only for local engine locations.
pub struct PhysicalFile {
    inner: Backend,
    position: u64,
}

enum Backend {
    Plain(File),
    Encrypted { file: Arc<Mutex<EncryptedFile>>, _guard: Arc<EncryptedVfs> },
}

/// Opens an existing physical file with authenticated logical reads and writes.
///
/// # Errors
/// Refuses encrypted companions whose key registration is absent.
pub fn open_physical_file(path: &Path) -> io::Result<PhysicalFile> {
    open(path, false)
}

/// Creates or opens a physical file without truncation. An admitted encrypted
/// registration is mandatory for production destinations.
///
/// # Errors
/// Refuses unavailable paths, keys and encrypted companions without a VFS.
pub fn create_physical_file(path: &Path) -> io::Result<PhysicalFile> {
    open(path, true)
}

fn open(path: &Path, create: bool) -> io::Result<PhysicalFile> {
    let inner = if let Some(guard) = super::sqlite::encrypted_vfs::lookup(path) {
        if !create && !super::sqlite::encrypted_file::companion(path).exists() {
            return Err(io::ErrorKind::NotFound.into());
        }
        let companion_exists = super::sqlite::encrypted_file::companion(path).exists();
        if let Ok(metadata) = std::fs::symlink_metadata(path) {
            if !metadata.is_file()
                || metadata.len() > 0
                || (!path.as_os_str().as_encoded_bytes().ends_with(b"-wal")
                    && !path.as_os_str().as_encoded_bytes().ends_with(b"-journal")
                    && !companion_exists)
            {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "invalid encrypted SQLite stub",
                ));
            }
        } else if create {
            File::create_new(path)?;
        }
        Backend::Encrypted { file: guard.shared_file(path)?, _guard: guard }
    } else {
        if super::sqlite::encrypted_file::companion(path).exists() {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "encrypted storage keys unavailable",
            ));
        }
        Backend::Plain(
            OpenOptions::new().read(true).write(true).create(create).truncate(false).open(path)?,
        )
    };
    Ok(PhysicalFile { inner, position: 0 })
}

impl PhysicalFile {
    /// Returns the logical `SQLite` byte length.
    ///
    /// # Errors
    /// Fails if the backing handle is unavailable.
    pub fn len(&self) -> io::Result<u64> {
        match &self.inner {
            Backend::Plain(file) => file.metadata().map(|metadata| metadata.len()),
            Backend::Encrypted { file, .. } => {
                Ok(file.lock().map_err(|_| io::Error::other("encrypted file poisoned"))?.size())
            }
        }
    }

    /// Reports an empty logical file.
    ///
    /// # Errors
    /// Fails when length cannot be obtained.
    pub fn is_empty(&self) -> io::Result<bool> {
        self.len().map(|length| length == 0)
    }

    /// Truncates or zero-extends the logical file.
    ///
    /// # Errors
    /// Fails on I/O or authorization loss.
    pub fn set_len(&self, length: u64) -> io::Result<()> {
        match &self.inner {
            Backend::Plain(file) => file.set_len(length),
            Backend::Encrypted { file, .. } => file
                .lock()
                .map_err(|_| io::Error::other("encrypted file poisoned"))?
                .set_len(length),
        }
    }

    /// Synchronizes file content and encrypted replacement directory metadata.
    ///
    /// # Errors
    /// Fails on I/O or authorization loss.
    pub fn sync_all(&self) -> io::Result<()> {
        match &self.inner {
            Backend::Plain(file) => file.sync_all(),
            Backend::Encrypted { file, .. } => {
                file.lock().map_err(|_| io::Error::other("encrypted file poisoned"))?.sync()
            }
        }
    }
}

impl Read for PhysicalFile {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        let remaining = self.len()?.saturating_sub(self.position);
        let count = match usize::try_from(remaining) {
            Ok(remaining) => buffer.len().min(remaining),
            Err(_) => buffer.len(),
        };
        if count == 0 {
            return Ok(0);
        }
        match &mut self.inner {
            Backend::Plain(file) => {
                file.seek(SeekFrom::Start(self.position))?;
                file.read_exact(&mut buffer[..count])?;
            }
            Backend::Encrypted { file, .. } => file
                .lock()
                .map_err(|_| io::Error::other("encrypted file poisoned"))?
                .read_exact_at(&mut buffer[..count], self.position)?,
        }
        self.position += count as u64;
        Ok(count)
    }
}

impl Write for PhysicalFile {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        match &mut self.inner {
            Backend::Plain(file) => {
                file.seek(SeekFrom::Start(self.position))?;
                file.write_all(buffer)?;
            }
            Backend::Encrypted { file, .. } => file
                .lock()
                .map_err(|_| io::Error::other("encrypted file poisoned"))?
                .write_all_at(buffer, self.position)?,
        }
        self.position = self
            .position
            .checked_add(buffer.len() as u64)
            .ok_or_else(|| io::Error::other("file offset overflow"))?;
        Ok(buffer.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        self.sync_all()
    }
}

impl Seek for PhysicalFile {
    fn seek(&mut self, seek: SeekFrom) -> io::Result<u64> {
        let position = match seek {
            SeekFrom::Start(position) => i128::from(position),
            SeekFrom::Current(delta) => i128::from(self.position) + i128::from(delta),
            SeekFrom::End(delta) => i128::from(self.len()?) + i128::from(delta),
        };
        self.position = u64::try_from(position)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid file offset"))?;
        Ok(self.position)
    }
}

/// Opens a mainline `SQLite` connection whose database and journals use the
/// authenticated VFS. The connection's file handles retain the registration.
/// Callers must bind the supplied source to their authorized database scope.
///
/// # Errors
/// Fails closed on unavailable keys, conflicting registrations or plaintext
/// files at the requested location.
pub fn open_encrypted_sqlite(
    path: &Path,
    keys: Arc<dyn crate::encryption::DatabaseKeySource>,
) -> Result<rusqlite::Connection, rusqlite::Error> {
    let guard = EncryptedVfs::register(path, keys)
        .map_err(|_| rusqlite::Error::InvalidPath(path.to_owned()))?;
    let connection = rusqlite::Connection::open_with_flags_and_vfs(
        path,
        rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE | rusqlite::OpenFlags::SQLITE_OPEN_CREATE,
        guard.name(),
    )?;
    connection
        .execute_batch("PRAGMA temp_store=MEMORY; PRAGMA mmap_size=0; PRAGMA synchronous=FULL;")?;
    Ok(connection)
}

impl EncryptedStorage {
    /// Registers a related staging/follower location under the source's
    /// authorized key scope. Local sources need no encrypted registration.
    ///
    /// # Errors
    /// Fails when source encryption has no live keys or destination conflicts.
    pub fn for_related_location(source: &Path, destination: &Path) -> io::Result<Option<Self>> {
        let source_file = source.join("terrabase.sqlite3");
        if let Some(guard) = super::sqlite::encrypted_vfs::lookup(&source_file) {
            Self::register(destination, guard.keys()).map(Some)
        } else if super::sqlite::encrypted_file::companion(&source_file).exists() {
            Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "encrypted storage keys unavailable",
            ))
        } else {
            Ok(None)
        }
    }
}

/// Opens `SQLite` using an already-admitted physical storage registration.
///
/// # Errors
/// Refuses encrypted companions without a live key source.
pub fn open_physical_sqlite(
    path: &Path,
    flags: rusqlite::OpenFlags,
) -> Result<rusqlite::Connection, rusqlite::Error> {
    let connection = if let Some(guard) = super::sqlite::encrypted_vfs::lookup(path) {
        rusqlite::Connection::open_with_flags_and_vfs(path, flags, guard.name())?
    } else {
        if super::sqlite::encrypted_file::companion(path).exists() {
            return Err(rusqlite::Error::InvalidPath(path.to_owned()));
        }
        rusqlite::Connection::open_with_flags(path, flags)?
    };
    connection.execute_batch("PRAGMA temp_store=MEMORY; PRAGMA mmap_size=0;")?;
    Ok(connection)
}

impl EncryptedStorage {
    /// Rewrites one logical database or journal file under the source's active
    /// key version. The caller must quiesce `SQLite` writes/checkpoints first.
    /// Each file publishes an atomic replacement, so interrupted multi-file
    /// rotation can resume while historical leases remain available.
    ///
    /// # Errors
    /// Fails for out-of-scope paths, unavailable historical keys or I/O errors.
    pub fn reencrypt_file(&self, path: &Path) -> io::Result<()> {
        self.registration
            .shared_file(path)?
            .lock()
            .map_err(|_| io::Error::other("encrypted file poisoned"))?
            .reencrypt()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn physical_handles_encrypt_seek_reopen_and_refuse_missing_keys() {
        let root = std::env::temp_dir()
            .join(format!("terrabase-physical-encrypted-{}", crate::identity::generate()));
        let owner = crate::storage::DatabaseOwner::local_development();
        let keys = crate::engine::test_storage_keys(&owner, 111);
        let guard = EncryptedStorage::register(&root, keys.clone()).unwrap();
        let path = root.join("terrabase.sqlite3");
        let mut file = create_physical_file(&path).unwrap();
        file.write_all(b"physical-encrypted-secret").unwrap();
        file.sync_all().unwrap();
        file.seek(SeekFrom::Start(9)).unwrap();
        let mut bytes = [0; 9];
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"encrypted");
        assert_eq!(std::fs::metadata(&path).unwrap().len(), 0);
        drop(file);
        drop(guard);
        assert!(open_physical_file(&path).is_err());
        let guard = EncryptedStorage::register(&root, keys).unwrap();
        let mut file = open_physical_file(&path).unwrap();
        let mut value = String::new();
        file.read_to_string(&mut value).unwrap();
        assert_eq!(value, "physical-encrypted-secret");
        drop(file);
        drop(guard);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn physical_create_refuses_plaintext_adoption_and_missing_main_companion() {
        let root = std::env::temp_dir()
            .join(format!("terrabase-physical-refusal-{}", crate::identity::generate()));
        let owner = crate::storage::DatabaseOwner::local_development();
        let keys = crate::engine::test_storage_keys(&owner, 112);
        let guard = EncryptedStorage::register(&root, keys).unwrap();
        let path = root.join("terrabase.sqlite3");
        std::fs::write(&path, b"plaintext-existing").unwrap();
        assert!(create_physical_file(&path).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), b"plaintext-existing");
        std::fs::write(&path, []).unwrap();
        assert!(create_physical_file(&path).is_err());
        drop(guard);
        std::fs::remove_dir_all(root).unwrap();
    }
}
