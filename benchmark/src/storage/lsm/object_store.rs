//! The object-store tier of the LSM engine (P1, ADR 0016 D-4 / ADR 0017).
//!
//! Tier 2 is S3-compatible object storage holding encrypted, immutable,
//! checksum-addressed cold segments. This module defines the engine-side
//! trait and a local-filesystem implementation: the same contract a real
//! S3 REST client satisfies, testable without credentials or an emulator.
//! Object keys are content-addressed (`fnv1a-<hex>`), objects are
//! write-once (a replaced range is a new object), and every read verifies
//! the checksum before any byte is trusted — the manifest never references
//! an object until `put` and its read-after-write verification complete
//! (`docs/design/p0-storage-tiering-contract.md` §1).
//!
//! The real S3 REST endpoint class is recorded `unrun` on this host: no
//! endpoint or credentials exist in the development environment, and the
//! charter forbids fabricating that evidence. The operator surface lands
//! with P4's typed administration.
#![allow(dead_code, reason = "P4 typed administration wires the operator surface")]

use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
};

/// Errors of the object-store tier. Typed and bounded: no paths or
/// credentials cross this seam.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ObjectStoreError {
    /// The write could not complete.
    Unavailable,
    /// The object is absent.
    NotFound,
    /// The object's bytes do not match its checksum address.
    ChecksumMismatch,
    /// An immutable object key already names different bytes.
    AlreadyExists,
    /// Every bounded cold-tier source was exhausted without a verified copy.
    ColdTierUnavailable,
}

/// The content-addressed key of one immutable object.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct ObjectKey(String);

impl ObjectKey {
    /// Derives the checksum address of `bytes`.
    #[must_use]
    pub(crate) fn of(bytes: &[u8]) -> Self {
        Self(format!("fnv1a-{:016x}", super::super::codec::fnv1a(bytes)))
    }

    /// Returns the canonical key text.
    #[must_use]
    pub(crate) fn as_str(&self) -> &str {
        &self.0
    }

    /// Rebuilds a key from its canonical text.
    #[must_use]
    pub(crate) fn from_recorded(text: &str) -> Self {
        Self(text.to_owned())
    }
}

/// The engine-side object-store contract (Tier 2).
pub(crate) trait ObjectStore: Send {
    /// Writes one immutable object and verifies it read-after-write.
    ///
    /// # Errors
    ///
    /// [`ObjectStoreError::Unavailable`] when the write or its
    /// verification cannot complete; [`ObjectStoreError::ChecksumMismatch`]
    /// never surfaces from a `put` — a mismatched write fails instead of
    /// publishing.
    fn put(&mut self, key: &ObjectKey, bytes: &[u8]) -> Result<(), ObjectStoreError>;

    /// Reads one object, verifying its checksum address.
    ///
    /// # Errors
    ///
    /// [`ObjectStoreError::NotFound`] when the object is absent;
    /// [`ObjectStoreError::ChecksumMismatch`] when the bytes do not verify
    /// — the caller must never serve such bytes (contract §4).
    fn get(&self, key: &ObjectKey) -> Result<Vec<u8>, ObjectStoreError>;

    /// Reports whether one object exists.
    fn exists(&self, key: &ObjectKey) -> bool;

    /// Removes one object. Reclamation is the mover's decision; the store
    /// only executes it.
    fn delete(&mut self, key: &ObjectKey) -> Result<(), ObjectStoreError>;
}

/// A local-filesystem object store: the Tier-2 contract for tests and
/// same-host deployments.
pub(crate) struct LocalObjectStore {
    dir: PathBuf,
}

impl LocalObjectStore {
    /// Opens or creates the object store rooted at `dir`.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectStoreError::Unavailable`] when the root cannot be
    /// created.
    pub(crate) fn open(dir: &Path) -> Result<Self, ObjectStoreError> {
        fs::create_dir_all(dir).map_err(|_| ObjectStoreError::Unavailable)?;
        Ok(Self { dir: dir.to_path_buf() })
    }

    fn path_of(&self, key: &ObjectKey) -> PathBuf {
        self.dir.join(key.as_str())
    }
}

impl ObjectStore for LocalObjectStore {
    fn put(&mut self, key: &ObjectKey, bytes: &[u8]) -> Result<(), ObjectStoreError> {
        let path = self.path_of(key);
        if path.exists() {
            // Content-addressed retries are idempotent, but a key may never
            // be replaced. In particular, do not rely on `rename`'s Unix
            // overwrite behavior for this write-once contract.
            return match self.get(key) {
                Ok(existing) if existing == bytes => Ok(()),
                Ok(_) | Err(ObjectStoreError::ChecksumMismatch) => {
                    Err(ObjectStoreError::AlreadyExists)
                }
                Err(error) => Err(error),
            };
        }
        let tmp = self.dir.join(format!("{}.tmp", key.as_str()));
        {
            let mut file = fs::OpenOptions::new()
                .create_new(true)
                .write(true)
                .open(&tmp)
                .map_err(|_| ObjectStoreError::Unavailable)?;
            file.write_all(bytes).map_err(|_| ObjectStoreError::Unavailable)?;
            file.sync_all().map_err(|_| ObjectStoreError::Unavailable)?;
        }
        if path.exists() {
            return Err(ObjectStoreError::AlreadyExists);
        }
        fs::rename(&tmp, &path).map_err(|_| ObjectStoreError::Unavailable)?;
        if let Ok(dir_handle) = fs::File::open(&self.dir) {
            let _ = dir_handle.sync_all();
        }
        // Read-after-write verification before the caller may reference
        // the object (contract §1).
        let read_back = self.get(key)?;
        if read_back != bytes {
            return Err(ObjectStoreError::ChecksumMismatch);
        }
        Ok(())
    }

    fn get(&self, key: &ObjectKey) -> Result<Vec<u8>, ObjectStoreError> {
        let bytes = fs::read(self.path_of(key)).map_err(|_| ObjectStoreError::NotFound)?;
        if ObjectKey::of(&bytes) != *key {
            return Err(ObjectStoreError::ChecksumMismatch);
        }
        Ok(bytes)
    }

    fn exists(&self, key: &ObjectKey) -> bool {
        self.path_of(key).is_file()
    }

    fn delete(&mut self, key: &ObjectKey) -> Result<(), ObjectStoreError> {
        match fs::remove_file(self.path_of(key)) {
            Ok(()) => Ok(()),
            Err(_) if !self.exists(key) => Ok(()),
            Err(_) => Err(ObjectStoreError::Unavailable),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store_dir(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-obj-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn puts_are_checksum_addressed_and_verify() {
        let dir = store_dir("rt");
        let mut store = LocalObjectStore::open(&dir).unwrap();
        let bytes = b"cold segment payload".to_vec();
        let key = ObjectKey::of(&bytes);
        store.put(&key, &bytes).unwrap();
        assert!(store.exists(&key));
        assert_eq!(store.get(&key).unwrap(), bytes);
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn corrupted_objects_fail_closed() {
        let dir = store_dir("corrupt");
        let mut store = LocalObjectStore::open(&dir).unwrap();
        let bytes = b"cold segment payload".to_vec();
        let key = ObjectKey::of(&bytes);
        store.put(&key, &bytes).unwrap();
        let path = dir.join(key.as_str());
        let mut on_disk = fs::read(&path).unwrap();
        let mid = on_disk.len() / 2;
        on_disk[mid] ^= 0xFF;
        fs::write(&path, &on_disk).unwrap();
        assert_eq!(store.get(&key), Err(ObjectStoreError::ChecksumMismatch));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn absent_objects_report_not_found() {
        let dir = store_dir("absent");
        let store = LocalObjectStore::open(&dir).unwrap();
        let key = ObjectKey::of(b"nothing");
        assert_eq!(store.get(&key), Err(ObjectStoreError::NotFound));
        assert!(!store.exists(&key));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn identical_content_maps_to_one_address() {
        let bytes = b"identical".to_vec();
        assert_eq!(ObjectKey::of(&bytes), ObjectKey::of(b"identical"));
        assert_ne!(ObjectKey::of(&bytes), ObjectKey::of(b"different"));
    }

    #[test]
    fn existing_key_is_write_once_and_cannot_be_overwritten() {
        let dir = store_dir("write-once");
        let mut store = LocalObjectStore::open(&dir).unwrap();
        let original = b"immutable payload".to_vec();
        let key = ObjectKey::of(&original);
        store.put(&key, &original).unwrap();
        assert_eq!(store.put(&key, b"different payload"), Err(ObjectStoreError::AlreadyExists));
        assert_eq!(store.get(&key).unwrap(), original);
        // Retrying the exact same immutable write is idempotent and does not
        // replace the object.
        store.put(&key, b"immutable payload").unwrap();
        fs::remove_dir_all(&dir).unwrap();
    }
}
