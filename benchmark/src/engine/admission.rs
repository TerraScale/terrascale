use std::{
    ffi::OsString,
    fs,
    path::{Path, PathBuf},
};

use super::EngineOpenError;

fn target_parent(target: &Path) -> &Path {
    // A single-component relative path has Some("") as its parent, not None.
    target
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."))
}

/// Nonblocking process/system admission for one durable Engine target.
pub(super) struct EngineAdmissionGuard {
    #[cfg(unix)]
    file: fs::File,
    target: PathBuf,
}

impl EngineAdmissionGuard {
    pub(super) fn acquire(target: &Path) -> Result<Self, EngineOpenError> {
        let parent = target_parent(target);
        let basename = target.file_name().ok_or(EngineOpenError::InvalidConfig)?;
        match fs::symlink_metadata(target) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(EngineOpenError::InvalidConfig);
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => return Err(EngineOpenError::StorageUnavailable),
        }
        let parent = parent.canonicalize().map_err(|_| EngineOpenError::StorageUnavailable)?;
        let mut lock_name = OsString::from(".");
        lock_name.push(basename);
        lock_name.push(".engine.lock");
        let path = parent.join(lock_name);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            let file = fs::OpenOptions::new()
                .create(true)
                .truncate(false)
                .read(true)
                .write(true)
                .mode(0o600)
                .open(&path)
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            rustix::fs::flock(&file, rustix::fs::FlockOperation::NonBlockingLockExclusive)
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            Ok(Self { file, target: parent.join(basename) })
        }
        #[cfg(not(unix))]
        {
            let _ = path;
            Err(EngineOpenError::StorageUnavailable)
        }
    }

    pub(super) fn matches(&self, target: &Path) -> bool {
        let parent = target_parent(target);
        let Some(name) = target.file_name() else { return false };
        parent.canonicalize().ok().map(|parent| parent.join(name)) == Some(self.target.clone())
    }
}

#[cfg(unix)]
impl Drop for EngineAdmissionGuard {
    fn drop(&mut self) {
        let _ = rustix::fs::flock(&self.file, rustix::fs::FlockOperation::Unlock);
    }
}
