use std::{
    ffi::OsString,
    fs,
    path::{Path, PathBuf},
};

use super::EngineOpenError;

#[cfg(unix)]
struct DirectoryAdmission {
    file: Option<fs::File>,
    locked: bool,
}

#[cfg(unix)]
fn open_directory(target: &Path) -> Result<fs::File, EngineOpenError> {
    rustix::fs::open(
        target,
        rustix::fs::OFlags::RDONLY
            | rustix::fs::OFlags::DIRECTORY
            | rustix::fs::OFlags::NOFOLLOW
            | rustix::fs::OFlags::CLOEXEC,
        rustix::fs::Mode::empty(),
    )
    .map(fs::File::from)
    .map_err(|_| EngineOpenError::StorageUnavailable)
}

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
    #[cfg(unix)]
    directory: std::sync::Mutex<DirectoryAdmission>,
    target: PathBuf,
}

impl EngineAdmissionGuard {
    pub(super) fn acquire(target: &Path) -> Result<Self, EngineOpenError> {
        let exists = match fs::symlink_metadata(target) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(EngineOpenError::InvalidConfig);
            }
            Ok(_) => true,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => false,
            Err(_) => return Err(EngineOpenError::StorageUnavailable),
        };
        #[cfg(unix)]
        let directory = if exists { Some(open_directory(target)?) } else { None };
        // This is only the pathname reservation. Data I/O keeps the caller's
        // original path, including any owned descriptor path used by restore.
        let resolved = if exists {
            target.canonicalize().map_err(|_| EngineOpenError::StorageUnavailable)?
        } else {
            target.to_path_buf()
        };
        let parent = target_parent(&resolved);
        let basename = resolved.file_name().ok_or(EngineOpenError::InvalidConfig)?;
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
            Ok(Self {
                file,
                directory: std::sync::Mutex::new(DirectoryAdmission {
                    file: directory,
                    locked: false,
                }),
                target: parent.join(basename),
            })
        }
        #[cfg(not(unix))]
        {
            let _ = path;
            Err(EngineOpenError::StorageUnavailable)
        }
    }

    #[cfg(test)]
    pub(super) fn matches(&self, target: &Path) -> bool {
        #[cfg(unix)]
        {
            let Ok(file) = open_directory(target) else { return false };
            let Ok(directory) = self.directory.lock() else { return false };
            self.matches_directory(target, &file, &directory)
        }
        #[cfg(not(unix))]
        false
    }

    #[cfg(unix)]
    fn matches_directory(
        &self,
        target: &Path,
        file: &fs::File,
        directory: &DirectoryAdmission,
    ) -> bool {
        use std::os::unix::fs::MetadataExt;
        if let Some(reserved) = &directory.file {
            let (Ok(original), Ok(candidate)) = (reserved.metadata(), file.metadata()) else {
                return false;
            };
            if original.dev() == candidate.dev() && original.ino() == candidate.ino() {
                return true;
            }
            // Branch restore removes its empty reservation directory before
            // publishing a prepared tree. Only an unbound, unlinked directory
            // may be replaced under the still-held pathname reservation.
            if directory.locked || original.nlink() != 0 {
                return false;
            }
        }
        let parent = target_parent(target);
        let Some(name) = target.file_name() else { return false };
        parent.canonicalize().ok().map(|parent| parent.join(name)) == Some(self.target.clone())
    }

    /// Binds exclusive inode ownership before owner metadata or storage opens.
    /// Unlike the sibling reservation, this lock survives directory renames.
    pub(super) fn bind_directory(&self, target: &Path) -> Result<(), EngineOpenError> {
        #[cfg(unix)]
        {
            let file = open_directory(target)?;
            let mut directory =
                self.directory.lock().map_err(|_| EngineOpenError::StorageUnavailable)?;
            if !self.matches_directory(target, &file, &directory) {
                return Err(EngineOpenError::InvalidConfig);
            }
            if !directory.locked {
                rustix::fs::flock(&file, rustix::fs::FlockOperation::NonBlockingLockExclusive)
                    .map_err(|_| EngineOpenError::StorageUnavailable)?;
                directory.file = Some(file);
                directory.locked = true;
            }
            Ok(())
        }
        #[cfg(not(unix))]
        Err(EngineOpenError::StorageUnavailable)
    }
}

#[cfg(unix)]
impl Drop for EngineAdmissionGuard {
    fn drop(&mut self) {
        let _ = rustix::fs::flock(&self.file, rustix::fs::FlockOperation::Unlock);
    }
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    use std::os::{fd::AsRawFd, unix::fs::symlink};

    #[test]
    fn directory_aliases_remain_exclusive_after_rename() {
        let root =
            std::env::temp_dir().join(format!("engine-inode-{}", crate::identity::generate()));
        let target = root.join("target");
        let retired = root.join("retired");
        let outside = root.join("outside");
        fs::create_dir_all(&target).unwrap();
        fs::create_dir(&outside).unwrap();
        let descriptor = open_directory(&target).unwrap();
        let alias = PathBuf::from(format!("/proc/self/fd/{}/.", descriptor.as_raw_fd()));
        let owner = EngineAdmissionGuard::acquire(&target).unwrap();
        owner.bind_directory(&target).unwrap();
        assert!(EngineAdmissionGuard::acquire(&alias).is_err());

        fs::rename(&target, &retired).unwrap();
        symlink(&outside, &target).unwrap();
        assert!(owner.matches(&alias));
        owner.bind_directory(&alias).unwrap(); // Reentrant guard must not relock itself.
        for path in [&retired, &alias] {
            let competitor = EngineAdmissionGuard::acquire(path).unwrap();
            assert_eq!(competitor.bind_directory(path), Err(EngineOpenError::StorageUnavailable));
        }
        assert!(matches!(
            EngineAdmissionGuard::acquire(&target),
            Err(EngineOpenError::InvalidConfig)
        ));
        let other = EngineAdmissionGuard::acquire(&outside).unwrap();
        other.bind_directory(&outside).unwrap();
        drop(other);
        drop(owner);

        let alias_owner = EngineAdmissionGuard::acquire(&alias).unwrap();
        alias_owner.bind_directory(&alias).unwrap();
        assert!(EngineAdmissionGuard::acquire(&retired).is_err());
        drop(alias_owner);
        let reopened = EngineAdmissionGuard::acquire(&retired).unwrap();
        reopened.bind_directory(&retired).unwrap();
        drop(reopened);
        drop(descriptor);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn directory_binding_waits_for_published_target() {
        for initially_exists in [false, true] {
            let root = std::env::temp_dir()
                .join(format!("engine-publish-{}", crate::identity::generate()));
            fs::create_dir(&root).unwrap();
            let target = root.join("target");
            if initially_exists {
                fs::create_dir(&target).unwrap();
            }
            let owner = EngineAdmissionGuard::acquire(&target).unwrap();
            if initially_exists {
                fs::remove_dir(&target).unwrap();
            }
            fs::create_dir(&target).unwrap();
            owner.bind_directory(&target).unwrap();
            owner.bind_directory(&target).unwrap();
            drop(owner);
            fs::remove_dir_all(root).unwrap();
        }
    }
}
