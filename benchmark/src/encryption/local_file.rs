//! Owner-only local development key persistence.

use std::fs::{self, File, OpenOptions};
use std::io::{Read as _, Write as _};
use std::path::{Path, PathBuf};

use aws_lc_rs::rand;

use super::{DatabaseKey, EncryptionError};

const KEY_BYTES: usize = 32;
const KEY_DIRECTORY: &str = "keys";
const KEY_FILE: &str = "local-database.key";

/// Persists one local development database key below an engine location.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalFileKeyProvider {
    path: PathBuf,
}

impl LocalFileKeyProvider {
    /// Resolves the fixed key path below one engine location.
    #[must_use]
    pub fn at_engine_location(engine_location: &Path) -> Self {
        Self { path: engine_location.join(KEY_DIRECTORY).join(KEY_FILE) }
    }

    /// Returns the stable non-production provider identity.
    #[must_use]
    pub const fn identity(&self) -> &'static str {
        "local-file"
    }

    /// Returns the key path without exposing key material.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Loads the existing key or creates and synchronizes it exactly once.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::UnsafeLocalKeyFile`] for a symbolic link,
    /// non-regular file, wrong length, broad Unix permissions, or changed file
    /// identity. I/O and randomness failures return
    /// [`EncryptionError::LocalKeyUnavailable`].
    pub fn load_or_create(&self) -> Result<DatabaseKey, EncryptionError> {
        let parent = self.path.parent().ok_or(EncryptionError::UnsafeLocalKeyFile)?;
        create_private_directory(parent)?;
        match load_existing(&self.path) {
            Ok(key) => Ok(key),
            Err(LoadError::Missing) => self.create(),
            Err(LoadError::Unsafe) => Err(EncryptionError::UnsafeLocalKeyFile),
            Err(LoadError::Io) => Err(EncryptionError::LocalKeyUnavailable),
        }
    }

    fn create(&self) -> Result<DatabaseKey, EncryptionError> {
        let mut bytes = [0u8; KEY_BYTES];
        rand::fill(&mut bytes).map_err(|_| EncryptionError::LocalKeyUnavailable)?;
        let mut file = match private_create(&self.path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                bytes.fill(0);
                return match load_existing(&self.path) {
                    Ok(key) => Ok(key),
                    Err(LoadError::Unsafe) => Err(EncryptionError::UnsafeLocalKeyFile),
                    Err(LoadError::Missing | LoadError::Io) => {
                        Err(EncryptionError::LocalKeyUnavailable)
                    }
                };
            }
            Err(_) => {
                bytes.fill(0);
                return Err(EncryptionError::LocalKeyUnavailable);
            }
        };
        let result = file.write_all(&bytes).and_then(|()| file.sync_all());
        if result.is_err() {
            bytes.fill(0);
            return Err(EncryptionError::LocalKeyUnavailable);
        }
        let key = DatabaseKey::from_bytes(bytes);
        drop(file);
        sync_parent(self.path.parent().ok_or(EncryptionError::UnsafeLocalKeyFile)?)?;
        Ok(key)
    }
}

enum LoadError {
    Missing,
    Unsafe,
    Io,
}

fn load_existing(path: &Path) -> Result<DatabaseKey, LoadError> {
    let before = fs::symlink_metadata(path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            LoadError::Missing
        } else {
            LoadError::Io
        }
    })?;
    if before.file_type().is_symlink() || !before.is_file() || before.len() != KEY_BYTES as u64 {
        return Err(LoadError::Unsafe);
    }
    private_mode(&before)?;
    let mut file = OpenOptions::new().read(true).open(path).map_err(|_| LoadError::Io)?;
    let opened = file.metadata().map_err(|_| LoadError::Io)?;
    if !opened.is_file() || opened.len() != KEY_BYTES as u64 || !same_file(&before, &opened) {
        return Err(LoadError::Unsafe);
    }
    private_mode(&opened)?;
    let mut bytes = [0u8; KEY_BYTES];
    file.read_exact(&mut bytes).map_err(|_| LoadError::Io)?;
    let mut trailing = [0u8; 1];
    if file.read(&mut trailing).map_err(|_| LoadError::Io)? != 0 {
        bytes.fill(0);
        return Err(LoadError::Unsafe);
    }
    Ok(DatabaseKey::from_bytes(bytes))
}

fn create_private_directory(path: &Path) -> Result<(), EncryptionError> {
    fs::create_dir_all(path).map_err(|_| EncryptionError::LocalKeyUnavailable)?;
    let metadata = fs::symlink_metadata(path).map_err(|_| EncryptionError::LocalKeyUnavailable)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(EncryptionError::UnsafeLocalKeyFile);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        fs::set_permissions(path, fs::Permissions::from_mode(0o700))
            .map_err(|_| EncryptionError::LocalKeyUnavailable)?;
    }
    Ok(())
}

fn private_create(path: &Path) -> std::io::Result<File> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    options.open(path)
}

fn private_mode(metadata: &fs::Metadata) -> Result<(), LoadError> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(LoadError::Unsafe);
        }
    }
    Ok(())
}

#[cfg(unix)]
fn same_file(left: &fs::Metadata, right: &fs::Metadata) -> bool {
    use std::os::unix::fs::MetadataExt as _;
    left.dev() == right.dev() && left.ino() == right.ino()
}

#[cfg(not(unix))]
fn same_file(left: &fs::Metadata, right: &fs::Metadata) -> bool {
    left.len() == right.len()
        && left.modified().ok() == right.modified().ok()
        && left.created().ok() == right.created().ok()
}

fn sync_parent(path: &Path) -> Result<(), EncryptionError> {
    File::open(path)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| EncryptionError::LocalKeyUnavailable)
}
