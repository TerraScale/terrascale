use std::error::Error;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};

#[derive(Debug)]
pub enum RunDirectoryError {
    Io(io::Error),
    Invalid(String),
    AlreadyExists(PathBuf),
}

impl fmt::Display for RunDirectoryError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "run directory I/O error: {error}"),
            Self::Invalid(error) => write!(formatter, "invalid run directory: {error}"),
            Self::AlreadyExists(path) => {
                write!(
                    formatter,
                    "run-owned path already exists: {}",
                    path.display()
                )
            }
        }
    }
}

impl Error for RunDirectoryError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Invalid(_) | Self::AlreadyExists(_) => None,
        }
    }
}

impl From<io::Error> for RunDirectoryError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

/// A newly created, private, uniquely named directory owned by one builder run.
#[derive(Debug)]
pub struct RunDirectory {
    path: PathBuf,
}

/// A create-new temporary file inside a run directory that becomes visible
/// under its final name only after the caller verifies its contents.
#[derive(Debug)]
pub struct StagedFile {
    file: Option<File>,
    temporary: PathBuf,
    destination: PathBuf,
    directory: PathBuf,
    committed: bool,
}

impl StagedFile {
    /// Sync and atomically install the verified temporary file without
    /// replacing any existing directory entry.
    ///
    /// # Errors
    ///
    /// Returns an error if syncing or installing the staged file fails, or if
    /// its final name already exists.
    pub fn commit(mut self) -> Result<PathBuf, RunDirectoryError> {
        self.file
            .take()
            .ok_or_else(|| RunDirectoryError::Invalid("staged file is already closed".to_owned()))?
            .sync_all()?;
        fs::hard_link(&self.temporary, &self.destination).map_err(|error| {
            if error.kind() == io::ErrorKind::AlreadyExists {
                RunDirectoryError::AlreadyExists(self.destination.clone())
            } else {
                RunDirectoryError::Io(error)
            }
        })?;
        fs::remove_file(&self.temporary)?;
        File::open(&self.directory)?.sync_all()?;
        self.committed = true;
        Ok(self.destination.clone())
    }
}

impl Write for StagedFile {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.file
            .as_mut()
            .ok_or_else(|| io::Error::other("staged file is closed"))?
            .write(bytes)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.file
            .as_mut()
            .ok_or_else(|| io::Error::other("staged file is closed"))?
            .flush()
    }
}

impl Drop for StagedFile {
    fn drop(&mut self) {
        if !self.committed {
            self.file.take();
            let _ = fs::remove_file(&self.temporary);
        }
    }
}

impl RunDirectory {
    /// Create a unique run directory below an explicit existing cache root.
    ///
    /// `run_id` is restricted to filename-safe ASCII and the final directory
    /// is created exclusively with owner-only permissions.
    ///
    /// # Errors
    ///
    /// Returns an error if the cache root is unsafe, the run ID is invalid, or
    /// the unique run directory cannot be created.
    pub fn create(cache_root: &Path, run_id: &str) -> Result<Self, RunDirectoryError> {
        if run_id.is_empty()
            || run_id.len() > 96
            || !run_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
        {
            return Err(RunDirectoryError::Invalid(
                "run id must contain 1-96 ASCII letters, digits, '-' or '_'".to_owned(),
            ));
        }
        let metadata = fs::symlink_metadata(cache_root)?;
        if metadata.file_type().is_symlink() || !metadata.is_dir() {
            return Err(RunDirectoryError::Invalid(
                "cache root must be an existing non-symlink directory".to_owned(),
            ));
        }
        let root = fs::canonicalize(cache_root)?;
        let path = root.join(run_id);
        match fs::create_dir(&path) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
                return Err(RunDirectoryError::AlreadyExists(path));
            }
            Err(error) => return Err(RunDirectoryError::Io(error)),
        }
        #[cfg(unix)]
        fs::set_permissions(&path, fs::Permissions::from_mode(0o700))?;
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() || !metadata.is_dir() {
            return Err(RunDirectoryError::Invalid(
                "created run path is no longer a regular directory".to_owned(),
            ));
        }
        let canonical = fs::canonicalize(&path)?;
        if canonical.parent() != Some(root.as_path()) {
            return Err(RunDirectoryError::Invalid(
                "created run directory escaped the cache root".to_owned(),
            ));
        }
        Ok(Self { path: canonical })
    }

    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Create a new staging file reserved for one safe final filename.
    ///
    /// # Errors
    ///
    /// Returns an error if the filename is unsafe, already exists, or cannot
    /// be created in this run directory.
    pub fn create_staged_file(&self, name: &str) -> Result<StagedFile, RunDirectoryError> {
        validate_file_name(name)?;
        let destination = self.path.join(name);
        let temporary = self.path.join(format!(".{name}.part"));
        if fs::symlink_metadata(&destination).is_ok() {
            return Err(RunDirectoryError::AlreadyExists(destination));
        }
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        options.mode(0o600);
        let file = options.open(&temporary)?;
        Ok(StagedFile {
            file: Some(file),
            temporary,
            destination,
            directory: self.path.clone(),
            committed: false,
        })
    }

    /// Atomically install one new regular file in this private run directory.
    ///
    /// The name must be a single normal path component. Existing names are
    /// rejected, including symlinks. The file and directory are synced before
    /// success is returned.
    ///
    /// # Errors
    ///
    /// Returns an error if the name is unsafe, already exists, or writing and
    /// syncing the new file fails.
    pub fn write_new(&self, name: &str, bytes: &[u8]) -> Result<PathBuf, RunDirectoryError> {
        validate_file_name(name)?;
        let destination = self.path.join(name);
        if fs::symlink_metadata(&destination).is_ok() {
            return Err(RunDirectoryError::AlreadyExists(destination));
        }
        let mut staged = self.create_staged_file(name)?;
        staged.write_all(bytes)?;
        staged.commit()
    }
}

fn validate_file_name(name: &str) -> Result<(), RunDirectoryError> {
    let path = Path::new(name);
    if name.is_empty()
        || path.is_absolute()
        || path.components().count() != 1
        || !matches!(path.components().next(), Some(Component::Normal(_)))
        || name == "."
        || name == ".."
    {
        return Err(RunDirectoryError::Invalid(
            "run output name must be one normal path component".to_owned(),
        ));
    }
    Ok(())
}

#[cfg(unix)]
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};

#[cfg(test)]
mod tests {
    use super::{RunDirectory, RunDirectoryError};
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn test_root(label: &str) -> PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock is after Unix epoch")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "fc-image-builder-{label}-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&path).expect("test root creates");
        path
    }

    #[test]
    fn run_directory_is_private_unique_and_atomically_writes_new_files() {
        let root = test_root("unique");
        let run = RunDirectory::create(&root, "run_001").expect("run directory creates");
        let canonical_root = fs::canonicalize(&root).expect("test root canonicalizes");
        assert_eq!(run.path().parent(), Some(canonical_root.as_path()));
        assert_eq!(
            fs::read(
                run.write_new("input.bin", b"verified")
                    .expect("file writes")
            )
            .expect("file reads"),
            b"verified"
        );
        assert!(matches!(
            run.write_new("input.bin", b"replace"),
            Err(RunDirectoryError::AlreadyExists(_))
        ));
        assert!(matches!(
            RunDirectory::create(&root, "run_001"),
            Err(RunDirectoryError::AlreadyExists(_))
        ));
        fs::remove_dir_all(root).expect("test root removes");
    }

    #[test]
    fn run_directory_rejects_unsafe_ids_names_and_symlink_roots() {
        let root = test_root("unsafe");
        assert!(matches!(
            RunDirectory::create(&root, "../escape"),
            Err(RunDirectoryError::Invalid(_))
        ));
        let run = RunDirectory::create(&root, "valid").expect("run directory creates");
        assert!(matches!(
            run.write_new("../escape", b"x"),
            Err(RunDirectoryError::Invalid(_))
        ));
        let symlink = root.with_extension("link");
        std::os::unix::fs::symlink(&root, &symlink).expect("test symlink creates");
        assert!(matches!(
            RunDirectory::create(&symlink, "run"),
            Err(RunDirectoryError::Invalid(_))
        ));
        fs::remove_dir_all(root).expect("test root removes");
        fs::remove_file(symlink).expect("test symlink removes");
    }
}
