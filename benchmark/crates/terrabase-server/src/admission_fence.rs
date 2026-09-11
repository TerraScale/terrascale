//! Durable native admission fences used by project deletion.

use crate::admission::AdmissionRecord;
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    io::{Read, Write},
    os::unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
    sync::Mutex,
};

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct FenceRecord {
    pub(crate) record: AdmissionRecord,
    pub(crate) deletion_operation_id: String,
    pub(crate) project_fence_revision: u64,
    pub(crate) outcome: FenceOutcome,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub(crate) enum FenceOutcome {
    NoInstall,
    StopRequired,
    Unknown,
}

pub(crate) struct AdmissionFenceStore {
    root: PathBuf,
    _lock: fs::File,
    fences: Mutex<BTreeMap<String, FenceRecord>>,
    uncertain: Mutex<BTreeMap<String, Vec<PathBuf>>>,
    poisoned: Mutex<bool>,
    #[cfg(test)]
    fault: std::sync::atomic::AtomicU8,
}

impl AdmissionFenceStore {
    #[allow(clippy::cast_possible_wrap, reason = "OFlags bits are the platform open(2) flag set")]
    pub(crate) fn open(
        root: &Path,
        validate: impl Fn(&AdmissionRecord) -> Result<(), String>,
    ) -> Result<Self, String> {
        if !root.is_absolute()
            || root.components().any(|part| matches!(part, std::path::Component::ParentDir))
        {
            return Err("unsafe native admission root components".into());
        }
        for ancestor in root.ancestors() {
            match fs::symlink_metadata(ancestor) {
                Ok(metadata) if metadata.is_dir() && !metadata.file_type().is_symlink() => {}
                Err(error) if error.kind() == std::io::ErrorKind::NotFound && ancestor == root => {}
                _ => return Err("unsafe native admission root".into()),
            }
        }
        protected_directory(root)?;
        let lock_path = root.join(".native-admission.lock");
        let lock = fs::OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .mode(0o600)
            .custom_flags(rustix::fs::OFlags::NOFOLLOW.bits() as i32)
            .open(lock_path)
            .map_err(|_| "native admission lock unavailable")?;
        let metadata = lock.metadata().map_err(|_| "native admission lock unavailable")?;
        if !metadata.is_file()
            || metadata.nlink() != 1
            || metadata.uid() != rustix::process::geteuid().as_raw()
            || metadata.mode() & 0o022 != 0
        {
            return Err("unsafe native admission lock".into());
        }
        rustix::fs::flock(&lock, rustix::fs::FlockOperation::NonBlockingLockExclusive)
            .map_err(|_| "native admission lock is held")?;
        lock.sync_all().map_err(|_| "native admission lock sync failed")?;
        sync_directory(root)?;
        let admissions = root.join("admissions");
        protected_directory(&admissions)?;
        protected_directory(&admissions.join("fences"))?;
        let store = Self {
            root: admissions,
            _lock: lock,
            fences: Mutex::default(),
            uncertain: Mutex::default(),
            poisoned: Mutex::default(),
            #[cfg(test)]
            fault: std::sync::atomic::AtomicU8::new(0),
        };
        let loaded = store.load_all(&validate)?;
        *store.fences.lock().map_err(|_| "fence lock poisoned")? = loaded;
        store.recover_pending(&validate)?;
        Ok(store)
    }

    fn recover_pending(
        &self,
        validate: &impl Fn(&AdmissionRecord) -> Result<(), String>,
    ) -> Result<(), String> {
        let dir = self.root.join("fences");
        let mut grouped = BTreeMap::<String, Vec<PathBuf>>::new();
        let mut count = 0;
        for entry in fs::read_dir(&dir).map_err(|_| "fence directory unavailable")? {
            let entry = entry.map_err(|_| "fence directory unavailable")?;
            if entry.file_name().to_string_lossy().starts_with('.') {
                let (database, _) = pending_identity(&entry.path())?;
                count += 1;
                if count > 4096 {
                    return Err("too many pending fences".into());
                }
                grouped.entry(database).or_default().push(entry.path());
            }
        }
        let mut fences = self.fences.lock().map_err(|_| "fence lock poisoned")?;
        let mut uncertain = self.uncertain.lock().map_err(|_| "fence lock poisoned")?;
        for (database, paths) in grouped {
            // Never pick an arbitrary winner among conflicting pending candidates.
            if paths.len() != 1 {
                uncertain.insert(database, paths);
                continue;
            }
            let path = &paths[0];
            let candidate = read_fence(path, true).and_then(|fence| {
                validate(&fence.record)?;
                let disk_intent = has_disk_intent(&self.root, &fence.record)?;
                if fence.outcome == FenceOutcome::NoInstall && disk_intent {
                    return Err("no-install fence conflicts with admission intent".into());
                }
                Ok(fence)
            });
            let Ok(candidate) = candidate else {
                uncertain.insert(database, paths);
                continue;
            };
            if fences.get(&database).is_some_and(|published| published != &candidate) {
                uncertain.insert(database, paths);
                continue;
            }
            self.publish_pending(path, &candidate, fences.contains_key(&database))?;
            fences.insert(database, candidate);
        }
        Ok(())
    }

    fn load_all(
        &self,
        validate: &impl Fn(&AdmissionRecord) -> Result<(), String>,
    ) -> Result<BTreeMap<String, FenceRecord>, String> {
        let dir = self.root.join("fences");
        let mut result = BTreeMap::new();
        for entry in fs::read_dir(dir).map_err(|_| "fence directory unavailable")? {
            let entry = entry.map_err(|_| "fence directory unavailable")?;
            if entry.file_name().to_string_lossy().starts_with('.') {
                // Grammar is checked in recovery. Unknown names cannot be scoped.
                continue;
            }
            let record = read_fence(&entry.path(), false)?;
            validate(&record.record)?;
            if record.outcome == FenceOutcome::NoInstall
                && has_disk_intent(&self.root, &record.record)?
            {
                return Err("no-install fence conflicts with admission intent".into());
            }
            result.insert(record.record.database_id.clone(), record);
        }
        Ok(result)
    }

    pub(crate) fn get(&self, database: &str) -> Result<Option<FenceRecord>, String> {
        self.ensure_database_available(database)?;
        self.published(database)
    }

    pub(crate) fn published(&self, database: &str) -> Result<Option<FenceRecord>, String> {
        self.ensure_available()?;
        self.fences
            .lock()
            .map(|fences| fences.get(database).cloned())
            .map_err(|_| "fence lock poisoned".into())
    }

    pub(crate) fn is_uncertain(&self, database: &str) -> Result<bool, String> {
        self.ensure_available()?;
        self.uncertain
            .lock()
            .map(|pending| pending.contains_key(database))
            .map_err(|_| "fence lock poisoned".into())
    }

    pub(crate) fn ensure_database_available(&self, database: &str) -> Result<(), String> {
        if self.is_uncertain(database)? {
            return Err("database fence is uncertain".into());
        }
        Ok(())
    }

    pub(crate) fn poison(&self) {
        if let Ok(mut poisoned) = self.poisoned.lock() {
            *poisoned = true;
        }
    }

    fn ensure_available(&self) -> Result<(), String> {
        if *self.poisoned.lock().map_err(|_| "fence lock poisoned")? {
            return Err("fence persistence unavailable".into());
        }
        Ok(())
    }

    #[cfg(test)]
    pub(crate) fn fail_at(&self, stage: u8) {
        self.fault.store(stage, std::sync::atomic::Ordering::SeqCst);
    }

    #[allow(clippy::unused_self, reason = "test fault injection reads the store handle")]
    #[allow(
        clippy::unnecessary_wraps,
        reason = "test fault injection returns the injected failure"
    )]
    fn checkpoint(&self, stage: u8) -> std::io::Result<()> {
        #[cfg(test)]
        if self.fault.load(std::sync::atomic::Ordering::SeqCst) == stage {
            return Err(std::io::Error::other("injected fence persistence failure"));
        }
        let _ = stage;
        Ok(())
    }

    #[allow(clippy::too_many_lines, reason = "one fence pass over pending and retained evidence")]
    #[allow(clippy::cast_possible_wrap, reason = "OFlags bits are the platform open(2) flag set")]
    #[allow(clippy::similar_names, reason = "fences indexes the store, fenced is the built record")]
    pub(crate) fn fence_unadmitted(
        &self,
        record: &AdmissionRecord,
        deletion_operation_id: &str,
        project_fence_revision: u64,
        retained_admission: bool,
    ) -> Result<FenceRecord, String> {
        self.ensure_available()?;
        if deletion_operation_id.is_empty()
            || deletion_operation_id.len() > 128
            || deletion_operation_id.chars().any(char::is_control)
            || project_fence_revision == 0
        {
            return Err("invalid deletion operation".into());
        }
        let mut fences = self.fences.lock().map_err(|_| "fence lock poisoned")?;
        let mut uncertain = self.uncertain.lock().map_err(|_| "fence lock poisoned")?;
        if let Some(paths) = uncertain.get(&record.database_id) {
            if paths.len() != 1 {
                return Err("conflicting pending fences".into());
            }
            let path = &paths[0];
            let (_, expected_digest) = pending_identity(path)?;
            let disk_intent = has_disk_intent(&self.root, record)?;
            let mut matched = None;
            for outcome in [FenceOutcome::NoInstall, FenceOutcome::StopRequired] {
                let candidate = FenceRecord {
                    record: record.clone(),
                    deletion_operation_id: deletion_operation_id.to_owned(),
                    project_fence_revision,
                    outcome,
                };
                let bytes = canonical_bytes(&candidate)?;
                if digest(&bytes) == expected_digest {
                    if outcome == FenceOutcome::NoInstall && (retained_admission || disk_intent) {
                        return Err("no-install fence conflicts with admission intent".into());
                    }
                    matched = Some((candidate, bytes));
                }
            }
            let (candidate, bytes) = matched.ok_or("pending fence identity conflict")?;
            if fences.get(&record.database_id).is_some_and(|published| published != &candidate) {
                return Err("pending fence conflicts with published fence".into());
            }
            // The authenticated retry supplies the missing canonical suffix, not
            // permission to replace inconsistent evidence or select a new outcome.
            let mut file = fs::OpenOptions::new()
                .read(true)
                .append(true)
                .custom_flags(
                    (rustix::fs::OFlags::NOFOLLOW | rustix::fs::OFlags::NONBLOCK).bits() as i32
                )
                .open(path)
                .map_err(|_| "pending fence unavailable")?;
            let existing = bounded_read(&mut file)?;
            if !bytes.starts_with(&existing) {
                return Err("pending fence is not the canonical prefix".into());
            }
            file.write_all(&bytes[existing.len()..]).and_then(|()| file.sync_all()).map_err(
                |_| {
                    self.poison();
                    "fence persistence uncertain"
                },
            )?;
            self.publish_pending(path, &candidate, fences.contains_key(&record.database_id))
                .inspect_err(|_error| {
                    self.poison();
                })?;
            fences.insert(record.database_id.clone(), candidate.clone());
            uncertain.remove(&record.database_id);
            return Ok(candidate);
        }
        if let Some(existing) = fences.get(&record.database_id) {
            if existing.record != *record || existing.deletion_operation_id != deletion_operation_id
            {
                return Err("fence identity conflict".into());
            }
            if project_fence_revision < existing.project_fence_revision {
                return Err("stale project fence revision".into());
            }
            if project_fence_revision != existing.project_fence_revision {
                return Err("project fence revision conflict".into());
            }
            return Ok(existing.clone());
        }
        let disk_intent = has_disk_intent(&self.root, record)?;
        let outcome = if retained_admission || disk_intent {
            FenceOutcome::StopRequired
        } else {
            FenceOutcome::NoInstall
        };
        let fenced = FenceRecord {
            record: record.clone(),
            deletion_operation_id: deletion_operation_id.to_owned(),
            project_fence_revision,
            outcome,
        };
        let path = self.root.join("fences").join(format!("{}.json", record.database_id));
        let bytes = canonical_bytes(&fenced)?;
        let tmp = self.root.join("fences").join(pending_name(&fenced)?);
        let mut file =
            fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&tmp).map_err(
                |_| {
                    self.poison();
                    "fence persistence uncertain"
                },
            )?;
        self.checkpoint(1)
            .and_then(|()| file.write_all(&bytes))
            .and_then(|()| self.checkpoint(2))
            .and_then(|()| file.sync_all())
            .and_then(|()| self.checkpoint(3))
            .and_then(|()| fs::File::open(self.root.join("fences"))?.sync_all())
            .map_err(|_| {
                self.poison();
                "fence persistence uncertain"
            })?;
        self.checkpoint(4).and_then(|()| fs::rename(&tmp, &path)).map_err(|_| {
            self.poison();
            "fence persistence uncertain"
        })?;
        self.checkpoint(5)
            .and_then(|()| fs::File::open(self.root.join("fences"))?.sync_all())
            .map_err(|_| {
                self.poison();
                "fence persistence uncertain"
            })?;
        fences.insert(record.database_id.clone(), fenced.clone());
        Ok(fenced)
    }

    fn publish_pending(
        &self,
        pending: &Path,
        fence: &FenceRecord,
        final_exists: bool,
    ) -> Result<(), String> {
        let dir = self.root.join("fences");
        let final_path = dir.join(format!("{}.json", fence.record.database_id));
        if final_exists {
            // Even equal pending evidence cannot replace the published inode.
            fs::File::open(&final_path)
                .and_then(|file| file.sync_all())
                .map_err(|_| "fence recovery unavailable")?;
            sync_directory(&dir)?;
            fs::remove_file(pending).map_err(|_| "fence recovery unavailable")?;
        } else {
            fs::File::open(pending)
                .and_then(|file| file.sync_all())
                .map_err(|_| "fence recovery unavailable")?;
            sync_directory(&dir)?;
            fs::rename(pending, final_path).map_err(|_| "fence recovery unavailable")?;
        }
        sync_directory(&dir)
    }
}

fn has_disk_intent(root: &Path, record: &AdmissionRecord) -> Result<bool, String> {
    let mut found = false;
    for suffix in ["json", "pending"] {
        let path = root.join(format!("{}.{}", record.database_id, suffix));
        let metadata = match fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(_) => return Err("admission intent unavailable".into()),
        };
        if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() > 16384 {
            return Err("unsafe admission intent".into());
        }
        let stored: AdmissionRecord =
            serde_json::from_slice(&fs::read(&path).map_err(|_| "admission intent unavailable")?)
                .map_err(|_| "invalid admission intent")?;
        if stored != *record {
            return Err("admission intent identity conflict".into());
        }
        found = true;
    }
    Ok(found)
}

fn protected_directory(path: &Path) -> Result<(), String> {
    match fs::symlink_metadata(path) {
        Ok(metadata)
            if metadata.is_dir()
                && !metadata.file_type().is_symlink()
                && metadata.uid() == rustix::process::geteuid().as_raw()
                && metadata.mode() & 0o022 == 0 => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            fs::DirBuilder::new()
                .mode(0o700)
                .create(path)
                .map_err(|_| "admission directory unavailable")?;
        }
        _ => return Err("unsafe admission directory".into()),
    }
    sync_directory(path)?;
    sync_directory(path.parent().ok_or("admission directory has no parent")?)
}

fn sync_directory(path: &Path) -> Result<(), String> {
    fs::File::open(path)
        .and_then(|f| f.sync_all())
        .map_err(|_| "admission directory sync failed".into())
}

#[allow(clippy::cast_possible_wrap, reason = "OFlags bits are the platform open(2) flag set")]
fn read_fence(path: &Path, pending: bool) -> Result<FenceRecord, String> {
    let mut file = fs::OpenOptions::new()
        .read(true)
        .custom_flags((rustix::fs::OFlags::NOFOLLOW | rustix::fs::OFlags::NONBLOCK).bits() as i32)
        .open(path)
        .map_err(|_| "fence unavailable")?;
    let bytes = bounded_read(&mut file)?;
    let fence: FenceRecord = serde_json::from_slice(&bytes).map_err(|_| "invalid fence")?;
    terrabase::identity::DatabaseUlid::from_text(&fence.record.database_id)
        .map_err(|_| "invalid fence database")?;
    let expected = if pending {
        pending_identity(path)?;
        if canonical_bytes(&fence)? != bytes {
            return Err("noncanonical pending fence".into());
        }
        pending_name(&fence)?
    } else {
        format!("{}.json", fence.record.database_id)
    };
    if path.file_name().and_then(|name| name.to_str()) != Some(&expected)
        || fence.deletion_operation_id.is_empty()
        || fence.deletion_operation_id.len() > 128
        || fence.deletion_operation_id.chars().any(char::is_control)
        || fence.project_fence_revision == 0
        || fence.outcome == FenceOutcome::Unknown
    {
        return Err("fence identity mismatch".into());
    }
    Ok(fence)
}

fn bounded_read(file: &mut fs::File) -> Result<Vec<u8>, String> {
    let metadata = file.metadata().map_err(|_| "fence unavailable")?;
    if !metadata.is_file() || metadata.len() > 32768 {
        return Err("unsafe fence".into());
    }
    let mut bytes = Vec::new();
    file.take(32769).read_to_end(&mut bytes).map_err(|_| "fence unavailable")?;
    if bytes.len() > 32768 {
        return Err("unsafe fence".into());
    }
    Ok(bytes)
}

fn canonical_bytes(fence: &FenceRecord) -> Result<Vec<u8>, String> {
    serde_json::to_vec(fence).map_err(|_| "fence encoding failed".into())
}

fn digest(bytes: &[u8]) -> String {
    hex::encode(aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, bytes).as_ref())
}

pub(crate) fn pending_name(fence: &FenceRecord) -> Result<String, String> {
    Ok(format!(".{}.{}.pending", fence.record.database_id, digest(&canonical_bytes(fence)?)))
}

fn pending_identity(path: &Path) -> Result<(String, String), String> {
    let name =
        path.file_name().and_then(|name| name.to_str()).ok_or("invalid pending fence name")?;
    let parts = name.split('.').collect::<Vec<_>>();
    if parts.len() != 4
        || !parts[0].is_empty()
        || parts[3] != "pending"
        || parts[2].len() != 64
        || !parts[2].bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err("invalid pending fence name".into());
    }
    let database = terrabase::identity::DatabaseUlid::from_text(parts[1])
        .map_err(|_| "invalid pending fence database")?;
    if database.to_string() != parts[1] {
        return Err("noncanonical pending fence database".into());
    }
    Ok((parts[1].to_owned(), parts[2].to_owned()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::encryption::DatabaseKeyVersion;
    use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};

    fn record() -> AdmissionRecord {
        AdmissionRecord {
            created_at: 1,
            environment: "production".into(),
            tenant_id: TenantUlid::generate().to_string(),
            project_id: ProjectUlid::generate().to_string(),
            database_id: DatabaseUlid::generate().to_string(),
            parent_database_id: None,
            key_version: DatabaseKeyVersion::generate().to_string(),
            name: "database".into(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: "create".into(),
            region: "GRU".into(),
            member_id: MemberUlid::generate().to_string(),
        }
    }

    #[test]
    fn fence_is_durable_idempotent_and_detects_intent() {
        let root = std::env::temp_dir().join(format!("native-fence-{}", DatabaseUlid::generate()));
        let store = AdmissionFenceStore::open(&root, |_| Ok(())).unwrap();
        let record = record();
        assert_eq!(
            store.fence_unadmitted(&record, "delete-1", 4, false).unwrap().outcome,
            FenceOutcome::NoInstall
        );
        assert_eq!(
            store.fence_unadmitted(&record, "delete-1", 4, false).unwrap().outcome,
            FenceOutcome::NoInstall
        );
        assert!(store.fence_unadmitted(&record, "delete-2", 4, false).is_err());
        assert!(store.fence_unadmitted(&record, "delete-1", 5, false).is_err());
        drop(store);
        let reopened = AdmissionFenceStore::open(&root, |_| Ok(())).unwrap();
        assert!(reopened.get(&record.database_id).unwrap().is_some());
        assert_eq!(
            reopened.fence_unadmitted(&record, "delete-1", 4, false).unwrap().outcome,
            FenceOutcome::NoInstall
        );
        drop(reopened);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn native_lifetime_lock_is_exclusive_until_owner_drops() {
        let root =
            std::env::temp_dir().join(format!("native-fence-lock-{}", DatabaseUlid::generate()));
        let first = AdmissionFenceStore::open(&root, |_| Ok(())).unwrap();
        assert!(AdmissionFenceStore::open(&root, |_| Ok(())).is_err());
        drop(first);
        let second = AdmissionFenceStore::open(&root, |_| Ok(())).unwrap();
        drop(second);
        fs::remove_dir_all(root).unwrap();
    }
}
