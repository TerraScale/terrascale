//! Public routing over immutable admitted identities and retained Member leases.
use std::{collections::BTreeMap, path::PathBuf, sync::{Arc, RwLock}};
use terrabase_cluster::production::ProductionDatabaseLease;
use terrabase_tbep::member_host::MemberHost;
use crate::{admission::AdmissionRecord, control::DatabaseRecord};

/// One published database bound to its existing Member storage lease.
pub struct AdmittedDatabase {
    /// Persisted authenticated immutable ownership and placement.
    pub record: AdmissionRecord,
    /// The same engine lease used by Member admission and replication.
    pub host: Arc<MemberHost>,
}

impl AdmittedDatabase {
    /// Public record addressed exclusively by canonical database identity.
    pub fn public_record(&self) -> DatabaseRecord {
        DatabaseRecord { name: self.record.database_id.clone(),
            placement: self.record.regions.iter().filter_map(|region| region.parse().ok()).collect(),
            residency: Some(self.record.residency.clone()), deleted: false, created_at: 0,
            deleted_at: None, parent: None }
    }
}

/// No name aliases or implicit engine opens: unknown identities always refuse.
#[derive(Default)]
pub struct AdmittedDatabaseRegistry {
    databases: RwLock<BTreeMap<String, Arc<AdmittedDatabase>>>,
}

impl AdmittedDatabaseRegistry {
    /// Publishes only after real Member admission, retaining its resource-accounted lease.
    ///
    /// # Errors
    /// Refuses mismatched owner identity, placement, or conflicting immutable retries.
    pub fn publish(&self, record: AdmissionRecord, lease: ProductionDatabaseLease,
        location: PathBuf, instance: u64) -> Result<(), String> {
        let scope = lease.scope();
        let owner = lease.database_owner();
        if scope.tenant().to_string() != record.tenant_id
            || scope.project().to_string() != record.project_id
            || scope.database().to_string() != record.database_id
            || owner.tenant() != scope.tenant() || owner.project() != scope.project()
            || owner.database() != scope.database() || record.regions.is_empty()
            || record.regions.iter().any(|region| region.parse::<terrabase::regions::RegionCode>().is_err()) {
            return Err("admitted database owner mismatch".into());
        }
        let mut databases = self.databases.write().map_err(|_| "admitted registry unavailable")?;
        if let Some(existing) = databases.get(&record.database_id) {
            return if existing.record == record { Ok(()) } else { Err("immutable admitted database conflict".into()) };
        }
        let host = Arc::new(MemberHost::from_admitted(lease, location, instance));
        databases.insert(record.database_id.clone(), Arc::new(AdmittedDatabase { record, host }));
        Ok(())
    }

    /// Resolves an exact canonical admitted database identity.
    pub fn resolve(&self, database: &str) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        Ok(self.databases.read().map_err(|_| "admitted registry unavailable")?.get(database).cloned())
    }

    /// Enumerates admitted databases for bounded maintenance drivers.
    pub fn records(&self) -> Result<Vec<DatabaseRecord>, String> {
        Ok(self.databases.read().map_err(|_| "admitted registry unavailable")?.values().map(|database| database.public_record()).collect())
    }
}
