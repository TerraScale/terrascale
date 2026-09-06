//! Public routing over immutable admitted identities and retained Member leases.
use crate::{admission::AdmissionRecord, control::DatabaseRecord};
use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::{Arc, RwLock},
};
use terrabase::member::DatabaseLease;
use terrabase_tbep::member_host::MemberHost;

/// One published database bound to its existing Member storage lease.
pub struct AdmittedDatabase {
    /// Lock-free high-water of fully authorized native requests.
    pub activity: terrabase::engine::lifecycle_state::AuthorizedActivity,
    /// Persisted authenticated immutable ownership and placement.
    pub record: AdmissionRecord,
    /// The same engine lease used by Member admission and replication.
    pub host: Arc<MemberHost>,
}

impl AdmittedDatabase {
    /// Public record addressed exclusively by canonical database identity.
    pub fn public_record(&self) -> DatabaseRecord {
        DatabaseRecord {
            name: self.record.database_id.clone(),
            placement: self
                .record
                .regions
                .iter()
                .filter_map(|region| region.parse().ok())
                .collect(),
            residency: Some(self.record.residency.clone()),
            deleted: false,
            created_at: 0,
            deleted_at: None,
            parent: self.record.parent_database_id.clone(),
        }
    }
}

/// No name aliases or implicit engine opens: unknown identities always refuse.
pub struct AdmittedDatabaseRegistry {
    ready: Arc<dyn Fn() -> bool + Send + Sync>,
    databases: RwLock<BTreeMap<String, Arc<AdmittedDatabase>>>,
}

impl AdmittedDatabaseRegistry {
    /// Binds every serving lookup to the complete Member's current readiness.
    #[must_use]
    pub fn new(ready: Arc<dyn Fn() -> bool + Send + Sync>) -> Self {
        Self { ready, databases: RwLock::new(BTreeMap::new()) }
    }

    /// Publishes only after real Member admission, retaining its resource-accounted lease.
    ///
    /// # Errors
    /// Refuses mismatched owner identity, placement, or conflicting immutable retries.
    pub fn publish(
        &self,
        record: AdmissionRecord,
        lease: DatabaseLease,
        location: PathBuf,
        instance: u64,
    ) -> Result<(), String> {
        let scope = lease.database_owner();
        if scope.tenant().to_string() != record.tenant_id
            || scope.project().to_string() != record.project_id
            || scope.database().to_string() != record.database_id
            || scope.environment().as_str() != record.environment
            || scope.region().as_str() != record.region
            || lease.location() != location
            || !lease.owner_scope_enforced()
            || record.regions.is_empty()
            || record
                .regions
                .iter()
                .any(|region| region.parse::<terrabase::regions::RegionCode>().is_err())
        {
            return Err("admitted database owner mismatch".into());
        }
        let mut databases = self.databases.write().map_err(|_| "admitted registry unavailable")?;
        if let Some(existing) = databases.get(&record.database_id) {
            return if existing.record == record {
                Ok(())
            } else {
                Err("immutable admitted database conflict".into())
            };
        }
        let host = Arc::new(MemberHost::from_admitted(lease, location, instance));
        databases.insert(
            record.database_id.clone(),
            Arc::new(AdmittedDatabase { record, host, activity: Default::default() }),
        );
        Ok(())
    }

    /// Resolves an exact canonical admitted database identity.
    pub fn resolve(&self, database: &str) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        Ok(self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned())
    }

    /// Enumerates admitted databases for bounded maintenance drivers.
    pub fn records(&self) -> Result<Vec<DatabaseRecord>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        Ok(self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .values()
            .map(|database| database.public_record())
            .collect())
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use terrabase::{
        identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid},
        member::{AdmissionLimits, DatabaseScope, DatabaseSpec, Member},
        regions::RegionCode,
    };

    pub(crate) fn fixture() -> (AdmittedDatabaseRegistry, Member, Vec<AdmissionRecord>, PathBuf) {
        let root = std::env::temp_dir().join(format!("admitted-api-{}", DatabaseUlid::generate()));
        std::fs::create_dir_all(&root).unwrap();
        let records: Vec<_> = (0..2)
            .map(|_| AdmissionRecord {
                environment: "production".into(),
                tenant_id: TenantUlid::generate().to_string(),
                project_id: ProjectUlid::generate().to_string(),
                database_id: DatabaseUlid::generate().to_string(),
                parent_database_id: None,
                key_version: terrabase::encryption::DatabaseKeyVersion::generate().to_string(),
                name: "same-name".into(),
                regions: vec!["GRU".into()],
                residency: "GRU".into(),
                operation_id: DatabaseUlid::generate().to_string(),
                region: "GRU".into(),
                member_id: MemberUlid::generate().to_string(),
            })
            .collect();
        let specs: Vec<_> = records
            .iter()
            .map(|record| {
                DatabaseSpec::scoped(
                    DatabaseScope::new(
                        "production",
                        record.tenant_id.parse().unwrap(),
                        record.project_id.parse().unwrap(),
                        record.database_id.parse().unwrap(),
                        RegionCode::GRU,
                    )
                    .unwrap(),
                    &record.database_id,
                )
            })
            .collect();
        let keys = specs
            .iter()
            .flat_map(|spec| crate::test_keys::storage_keys(spec.scope().unwrap()))
            .collect();
        let member =
            Member::open_production(&root, specs, AdmissionLimits::new(8, 4), keys).unwrap();
        let registry = AdmittedDatabaseRegistry::new(Arc::new(|| true));
        for record in &records {
            let id = terrabase::member::DatabaseId::try_new(&record.database_id).unwrap();
            let lease = member.acquire(&id).unwrap();
            registry.publish(record.clone(), lease, root.join(&record.database_id), 1).unwrap();
        }
        (registry, member, records, root)
    }

    #[test]
    fn live_member_fence_denies_previously_published_database() {
        let (mut registry, member, records, root) = fixture();
        let ready = Arc::new(std::sync::atomic::AtomicBool::new(true));
        let gate = ready.clone();
        registry.ready = Arc::new(move || gate.load(std::sync::atomic::Ordering::Acquire));
        assert!(registry.resolve(&records[0].database_id).unwrap().is_some());
        ready.store(false, std::sync::atomic::Ordering::Release);
        assert!(registry.resolve(&records[0].database_id).is_err());
        assert!(registry.records().is_err());
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn same_names_resolve_only_canonical_owners_and_retain_member_engine() {
        let (registry, member, records, root) = fixture();
        assert!(registry.resolve("same-name").unwrap().is_none());
        for record in &records {
            let admitted = registry.resolve(&record.database_id).unwrap().unwrap();
            assert_eq!(admitted.host.tenant(), record.tenant_id);
            let id = terrabase::member::DatabaseId::try_new(&record.database_id).unwrap();
            let lease = member.acquire(&id).unwrap();
            assert!(std::ptr::eq(admitted.host.engine(), &*lease));
        }
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}

impl AdmittedDatabaseRegistry {
    /// Collects actual admitted lifecycle observations without inventing missing scopes.
    pub fn lifecycle_snapshots(
        &self,
        now: u64,
    ) -> Result<Vec<crate::lifecycle_publication::LifecyclePublicationSnapshot>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to publish lifecycle".into());
        }
        let databases: Vec<_> = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .values()
            .cloned()
            .collect();
        let mut snapshots = Vec::with_capacity(databases.len());
        for database in databases {
            // A local observation cannot certify database-wide inactivity while
            // another selected Region may still be serving authorized requests.
            if database.record.regions.len() != 1 {
                return Err("regional lifecycle aggregation unavailable".into());
            }
            let engine = database.host.engine();
            let tenant = terrabase::types::TenantId::try_new(database.host.tenant())
                .map_err(|_| "invalid admitted database")?;
            let scope = engine.scope_for(&tenant);
            let mut state =
                engine.lifecycle_state(&scope).map_err(|_| "lifecycle source unavailable")?;
            if state.deletion.is_none()
                && let Some(activity) = database.activity.pending()
            {
                state = engine
                    .lifecycle_flush_activity(&scope, activity)
                    .map_err(|_| "activity flush unavailable")?;
            }
            snapshots.push(
                crate::lifecycle_publication::LifecyclePublicationSnapshot::from_native(
                    database.record.tenant_id.clone(),
                    database.record.project_id.clone(),
                    database.record.database_id.clone(),
                    now,
                    &state,
                )
                .map_err(|_| "invalid lifecycle observation")?,
            );
        }
        Ok(snapshots)
    }
    /// Executes a protected, durable deletion stop against the retained engine lease.
    /// The exact operation and boundary survive restart in the database SQLite file.
    pub fn stop(
        &self,
        record: &AdmissionRecord,
        request: &crate::admission::StopRequest,
    ) -> Result<(), String> {
        let database = self.resolve(&record.database_id)?.ok_or("database not admitted")?;
        if database.record != *record {
            return Err("immutable stop admission mismatch".into());
        }
        let cutoff = request.cutoff_millis()?;
        let retain_until = request
            .retain_until
            .as_ref()
            .map(|text| {
                let retention = chrono::DateTime::parse_from_rfc3339(text)
                    .map_err(|_| "invalid retention boundary")?;
                if retention.timestamp_subsec_nanos() % 1_000_000 != 0 {
                    return Err("retention boundary requires millisecond precision");
                }
                u64::try_from(retention.timestamp_millis())
                    .map_err(|_| "invalid retention boundary")
            })
            .transpose()?;
        let engine = database.host.engine();
        let tenant = terrabase::types::TenantId::try_new(database.host.tenant())
            .map_err(|_| "invalid admitted tenant")?;
        let scope = engine.scope_for(&tenant);
        let prepared =
            engine.lifecycle_state(&scope).map_err(|_| "native lifecycle state unavailable")?;
        if prepared
            .deletion_preparation
            .as_ref()
            .is_none_or(|intent| intent.operation_id != request.operation_id)
        {
            return Err("native deletion requires matching preparation".into());
        }
        engine
            .lifecycle_accept_deletion(
                &scope,
                terrabase::engine::lifecycle_state::DeletionIntent {
                    operation_id: request.operation_id.clone(),
                    usage_stopped_at: cutoff,
                    retain_until,
                },
            )
            .map_err(|_| "native lifecycle stop refused")?;
        let (epoch, _) = engine.origin_epoch().map_err(|_| "origin fence unavailable")?;
        engine.fence_origin(epoch).map_err(|_| "origin fence failed")?;
        crate::reactive_api::revoke_database(
            &record.tenant_id,
            &record.project_id,
            &record.database_id,
        )
        .map_err(|_| "reactive revocation failed")?;
        Ok(())
    }
}

impl AdmittedDatabaseRegistry {
    /// Atomically protects the deletion operation against subsequent writes/protection changes.
    pub fn prepare_stop(
        &self,
        record: &AdmissionRecord,
        request: &crate::admission::PrepareStopRequest,
    ) -> Result<u64, String> {
        let database = self.resolve(&record.database_id)?.ok_or("database not admitted")?;
        if database.record != *record {
            return Err("immutable prepare admission mismatch".into());
        }
        let engine = database.host.engine();
        let tenant = terrabase::types::TenantId::try_new(database.host.tenant())
            .map_err(|_| "invalid admitted tenant")?;
        let scope = engine.scope_for(&tenant);
        let state = engine
            .lifecycle_prepare_deletion(&scope, &request.operation_id)
            .map_err(|_| "native lifecycle prepare refused")?;
        crate::reactive_api::revoke_database(
            &record.tenant_id,
            &record.project_id,
            &record.database_id,
        )
        .map_err(|_| "reactive revocation failed")?;
        state
            .deletion_preparation
            .map(|value| value.minimum_cutoff_millis)
            .ok_or("missing prepared boundary".into())
    }
}

impl AdmittedDatabaseRegistry {
    /// Reads durable native deletion protection; no activity or policy defaults are synthesized.
    pub fn stop_preflight(&self, record: &AdmissionRecord) -> Result<bool, String> {
        let database = self.resolve(&record.database_id)?.ok_or("database not admitted")?;
        if database.record != *record {
            return Err("immutable preflight admission mismatch".into());
        }
        let engine = database.host.engine();
        let tenant = terrabase::types::TenantId::try_new(database.host.tenant())
            .map_err(|_| "invalid admitted tenant")?;
        let state = engine
            .lifecycle_state(&engine.scope_for(&tenant))
            .map_err(|_| "native lifecycle state unavailable")?;
        Ok(state.deletion_protection)
    }
}
