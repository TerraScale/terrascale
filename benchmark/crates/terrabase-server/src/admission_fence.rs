//! Durable native admission fences used by project deletion.

use crate::admission::AdmissionRecord;
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::Mutex,
};

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub(crate) struct FenceRecord {
    pub(crate) record: AdmissionRecord,
    pub(crate) deletion_operation_id: String,
    pub(crate) project_fence_revision: u64,
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub(crate) enum FenceOutcome {
    NoInstall,
    StopRequired,
    Unknown,
}

pub(crate) struct AdmissionFenceStore {
    root: PathBuf,
    _lock: fs::File,
    fences: Mutex<BTreeMap<String, FenceRecord>>,
}

impl AdmissionFenceStore {
    pub(crate) fn open(root: &Path) -> Result<Self, String> {
        fs::create_dir_all(root).map_err(|_| "admission root unavailable")?;
        let lock_path = root.join(".native-admission.lock");
        let lock = fs::OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .open(lock_path)
            .map_err(|_| "native admission lock unavailable")?;
        rustix::fs::flock(&lock, rustix::fs::FlockOperation::NonBlockingLockExclusive)
            .map_err(|_| "native admission lock is held")?;
        let admissions = root.join("admissions");
        fs::create_dir_all(admissions.join("fences")).map_err(|_| "fence directory unavailable")?;
        let store = Self { root: admissions, _lock: lock, fences: Mutex::default() };
        let loaded = store.load_all()?;
        *store.fences.lock().map_err(|_| "fence lock poisoned")? = loaded;
        Ok(store)
    }

    pub(crate) fn load_all(&self) -> Result<BTreeMap<String, FenceRecord>, String> {
        let dir = self.root.join("fences");
        let mut result = BTreeMap::new();
        for entry in fs::read_dir(dir).map_err(|_| "fence directory unavailable")? {
            let entry = entry.map_err(|_| "fence directory unavailable")?;
            let metadata = fs::symlink_metadata(entry.path()).map_err(|_| "unsafe fence")?;
            if !metadata.is_file() || metadata.file_type().is_symlink() {
                return Err("unsafe fence".into());
            }
            let record: FenceRecord =
                serde_json::from_slice(&fs::read(entry.path()).map_err(|_| "fence unavailable")?)
                    .map_err(|_| "invalid fence")?;
            result.insert(record.record.database_id.clone(), record);
        }
        Ok(result)
    }

    pub(crate) fn get(&self, database: &str) -> Result<Option<FenceRecord>, String> {
        self.fences
            .lock()
            .map(|fences| fences.get(database).cloned())
            .map_err(|_| "fence lock poisoned".into())
    }

    pub(crate) fn fence_unadmitted(
        &self,
        record: &AdmissionRecord,
        deletion_operation_id: &str,
        project_fence_revision: u64,
    ) -> Result<FenceOutcome, String> {
        if deletion_operation_id.is_empty() {
            return Err("invalid deletion operation".into());
        }
        let mut fences = self.fences.lock().map_err(|_| "fence lock poisoned")?;
        if let Some(existing) = fences.get(&record.database_id) {
            if existing.record != *record || existing.deletion_operation_id != deletion_operation_id
            {
                return Err("fence identity conflict".into());
            }
            if project_fence_revision < existing.project_fence_revision {
                return Err("stale project fence revision".into());
            }
            return Ok(if has_disk_intent(&self.root, &record.database_id) {
                FenceOutcome::StopRequired
            } else {
                FenceOutcome::NoInstall
            });
        }
        let outcome = if has_disk_intent(&self.root, &record.database_id) {
            FenceOutcome::StopRequired
        } else {
            FenceOutcome::NoInstall
        };
        let fenced = FenceRecord {
            record: record.clone(),
            deletion_operation_id: deletion_operation_id.to_owned(),
            project_fence_revision,
        };
        let path = self.root.join("fences").join(format!("{}.json", record.database_id));
        let tmp = self.root.join("fences").join(format!(".{}.pending", record.database_id));
        let bytes = serde_json::to_vec(&fenced).map_err(|_| "fence encoding failed")?;
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp)
            .map_err(|_| "fence persistence uncertain")?;
        file.write_all(&bytes)
            .and_then(|()| file.sync_all())
            .map_err(|_| "fence persistence uncertain")?;
        fs::rename(&tmp, &path).map_err(|_| "fence persistence uncertain")?;
        fs::File::open(self.root.join("fences"))
            .and_then(|f| f.sync_all())
            .map_err(|_| "fence persistence uncertain")?;
        fences.insert(record.database_id.clone(), fenced);
        Ok(outcome)
    }
}

fn has_disk_intent(root: &Path, database: &str) -> bool {
    root.join(format!("{database}.json")).is_file()
        || root.join(format!("{database}.pending")).is_file()
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
        let store = AdmissionFenceStore::open(&root).unwrap();
        let record = record();
        assert_eq!(
            store.fence_unadmitted(&record, "delete-1", 4).unwrap(),
            FenceOutcome::NoInstall
        );
        assert_eq!(
            store.fence_unadmitted(&record, "delete-1", 4).unwrap(),
            FenceOutcome::NoInstall
        );
        assert!(store.fence_unadmitted(&record, "delete-2", 4).is_err());
        let intent = store.root.join(format!("{}.pending", record.database_id));
        fs::write(intent, b"pending").unwrap();
        assert_eq!(
            store.fence_unadmitted(&record, "delete-1", 5).unwrap(),
            FenceOutcome::StopRequired
        );
        drop(store);
        let reopened = AdmissionFenceStore::open(&root).unwrap();
        assert!(reopened.get(&record.database_id).unwrap().is_some());
        assert_eq!(
            reopened.fence_unadmitted(&record, "delete-1", 5).unwrap(),
            FenceOutcome::StopRequired
        );
        drop(reopened);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn native_lifetime_lock_is_exclusive_until_owner_drops() {
        let root =
            std::env::temp_dir().join(format!("native-fence-lock-{}", DatabaseUlid::generate()));
        let first = AdmissionFenceStore::open(&root).unwrap();
        assert!(AdmissionFenceStore::open(&root).is_err());
        drop(first);
        let second = AdmissionFenceStore::open(&root).unwrap();
        drop(second);
        fs::remove_dir_all(root).unwrap();
    }
}
