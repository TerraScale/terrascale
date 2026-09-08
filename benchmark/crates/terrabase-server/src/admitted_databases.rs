//! Public routing over immutable admitted identities and retained Member leases.
use crate::{admission::AdmissionRecord, control::DatabaseRecord};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::PathBuf,
    sync::{
        Arc, RwLock,
        atomic::{AtomicBool, Ordering},
    },
};
use terrabase::member::DatabaseLease;
use terrabase_tbep::member_host::MemberHost;

/// One published database bound to its existing Member storage lease.
pub struct AdmittedDatabase {
    /// Lock-free high-water of fully authorized native requests.
    pub activity: Arc<terrabase::engine::lifecycle_state::AuthorizedActivity>,
    /// Persisted authenticated immutable ownership and placement.
    pub record: AdmissionRecord,
    /// The same engine lease used by Member admission and replication.
    pub host: Arc<MemberHost>,
}

impl AdmittedDatabase {
    /// Public record addressed exclusively by canonical database identity.
    ///
    /// # Errors
    /// Returns an error when the admitted identity or lifecycle state cannot be read.
    pub fn public_record(&self) -> Result<DatabaseRecord, String> {
        let tenant = terrabase::types::TenantId::try_new(&self.record.tenant_id)
            .map_err(|_| "invalid admitted tenant")?;
        let state = self
            .host
            .engine()
            .lifecycle_state(&self.host.engine().scope_for(&tenant))
            .map_err(|_| "lifecycle source unavailable")?;
        Ok(DatabaseRecord {
            name: self.record.database_id.clone(),
            placement: self
                .record
                .regions
                .iter()
                .filter_map(|region| region.parse().ok())
                .collect(),
            residency: Some(self.record.residency.clone()),
            deleted: state.deletion.is_some(),
            created_at: self.record.created_at,
            deleted_at: state
                .deletion
                .as_ref()
                .map(|deletion| i64::try_from(deletion.usage_stopped_at))
                .transpose()
                .map_err(|_| "deletion timestamp exceeds public representation")?,
            parent: self.record.parent_database_id.clone(),
        })
    }
}

/// A retained host and its lane-activation state captured under the registry locks.
/// This observation retains the existing lease; it does not grant serving authority.
pub struct AdmittedDatabaseSnapshot {
    /// The already opened host and immutable accepted owner record.
    pub database: Arc<AdmittedDatabase>,
    /// Whether replication lane publication has activated this host.
    pub activated: bool,
}

/// Exclusive restore reservation after the admitted host and lease have been
/// fully released. The reservation carries only immutable admission facts;
/// no engine handle remains alive while restore bytes are installed.
pub struct RestoreReservation {
    record: AdmissionRecord,
    location: PathBuf,
    activated: bool,
    rollback: Option<(terrabase::member::Member, Arc<AdmittedDatabaseRegistry>)>,
    permit: Option<terrabase::member::RestorePermit>,
    path_guard: Arc<std::sync::Mutex<BTreeSet<PathBuf>>>,
}

impl RestoreReservation {
    /// Restores registry admission when the async restore future is cancelled.
    pub fn rollback_on_drop(
        mut self,
        member: terrabase::member::Member,
        registry: Arc<AdmittedDatabaseRegistry>,
    ) -> Self {
        self.rollback = Some((member, registry));
        self
    }

    /// Returns the admitted record being restored.
    #[must_use]
    pub fn record(&self) -> &AdmissionRecord {
        &self.record
    }

    /// Returns the exclusive database location.
    #[must_use]
    pub fn location(&self) -> &std::path::Path {
        &self.location
    }

    /// Reacquires the target through the owning Member while retaining the
    /// exclusion. Only this reservation may cross the restore boundary.
    pub fn reacquire(&self, member: &terrabase::member::Member) -> Result<DatabaseLease, String> {
        let id = terrabase::member::DatabaseId::try_new(&self.record.database_id)
            .map_err(|_| "restore target identity is invalid")?;
        member
            .acquire_existing_for_restore(
                &id,
                self.permit.as_ref().ok_or("restore permit released")?,
            )
            .map_err(|_| "restore target reacquisition refused".to_owned())
    }

    /// Reinstalls the same admitted identity after a successful restore.
    pub fn complete(
        mut self,
        registry: &AdmittedDatabaseRegistry,
        lease: DatabaseLease,
        instance: u64,
    ) -> Result<(), String> {
        let activated = self.activated;
        let location = self.location.clone();
        let database_id = self.record.database_id.clone();
        registry.publish(self.record.clone(), lease, location.clone(), instance)?;
        self.rollback = None;
        if activated {
            if let Err(error) = registry.activate(&[database_id]) {
                return Err(error);
            }
        }
        // Dropping the owned permit releases the exclusion only after the
        // admitted host has been republished (and activated when required).
        if let Ok(mut paths) = self.path_guard.lock() {
            paths.remove(&location);
        }
        drop(self.permit.take());
        Ok(())
    }
}

impl Drop for RestoreReservation {
    fn drop(&mut self) {
        if let Some((member, registry)) = self.rollback.take() {
            if let Ok(lease) = self.reacquire(&member) {
                // Publication remains fenced until this drop releases the permit.
                let _ = registry.publish(self.record.clone(), lease, self.location.clone(), 1);
            }
        }
        if let Ok(mut paths) = self.path_guard.lock() {
            paths.remove(&self.location);
        }
    }
}

/// No name aliases or implicit engine opens: unknown identities always refuse.
pub struct AdmittedDatabaseRegistry {
    ready: Arc<dyn Fn() -> bool + Send + Sync>,
    databases: RwLock<BTreeMap<String, Arc<AdmittedDatabase>>>,
    serving: RwLock<BTreeSet<String>>,
    restore_paths: Arc<std::sync::Mutex<BTreeSet<PathBuf>>>,
    draining: AtomicBool,
}

impl AdmittedDatabaseRegistry {
    /// Fences one paused database and transfers its host lease exclusively.
    /// Restore callers must complete the reservation with a freshly acquired
    /// lease after installing bytes; no open Engine survives this boundary.
    pub fn reserve_restore(&self, database: &str) -> Result<RestoreReservation, String> {
        let mut serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        let mut databases = self.databases.write().map_err(|_| "admitted registry unavailable")?;
        if serving.contains(database) {
            return Err("restore target is still serving".into());
        }
        let entry =
            databases.get(database).ok_or_else(|| "restore target is not admitted".to_owned())?;
        let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
            .map_err(|_| "restore target tenant is invalid".to_owned())?;
        let lifecycle = entry
            .host
            .engine()
            .lifecycle_state(&entry.host.engine().scope_for(&tenant))
            .map_err(|_| "restore target lifecycle is unavailable".to_owned())?;
        if !lifecycle.paused || lifecycle.access_revoked() {
            return Err("restore target must be paused and active".into());
        }
        if Arc::strong_count(entry) != 1
            || Arc::strong_count(&entry.host) != 1
            || !entry.host.is_admitted()
        {
            return Err("restore target engine is still referenced".to_owned());
        }
        let location = entry.host.location().to_path_buf();
        let mut paths = self.restore_paths.lock().map_err(|_| "restore reservation unavailable")?;
        if !paths.insert(location.clone()) {
            return Err("restore target is already reserved".into());
        }
        let activated = serving.remove(database);
        let entry = databases.remove(database).expect("entry remains under registry write lock");
        let database_obj = match Arc::try_unwrap(entry) {
            Ok(database_obj) => database_obj,
            Err(entry) => {
                databases.insert(database.to_owned(), entry);
                if activated {
                    serving.insert(database.to_owned());
                }
                paths.remove(&location);
                return Err("restore target admission is still referenced".into());
            }
        };
        let AdmittedDatabase { activity, record, host } = database_obj;
        let host = match Arc::try_unwrap(host) {
            Ok(host) => host,
            Err(host) => {
                databases.insert(
                    database.to_owned(),
                    Arc::new(AdmittedDatabase { activity, record, host }),
                );
                if activated {
                    serving.insert(database.to_owned());
                }
                paths.remove(&location);
                return Err("restore target engine is still referenced".into());
            }
        };
        let lease =
            host.into_admitted_lease().map_err(|_| "restore target lease is not exclusive")?;
        let permit = match lease.reserve_restore() {
            Ok(permit) => permit,
            Err((lease, _)) => {
                let host = Arc::new(MemberHost::from_admitted(lease, location.clone(), 1));
                databases.insert(
                    database.to_owned(),
                    Arc::new(AdmittedDatabase { activity, record, host }),
                );
                if activated {
                    serving.insert(database.to_owned());
                }
                paths.remove(&location);
                return Err("restore target lease is not exclusive".into());
            }
        };
        let result = RestoreReservation {
            record,
            location,
            activated,
            rollback: None,
            permit: Some(permit),
            path_guard: Arc::clone(&self.restore_paths),
        };
        drop(databases);
        drop(serving);
        Ok(result)
    }
    /// Durably pauses one admitted database while retaining its host and data.
    pub fn pause(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        let mut serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        let entry = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned()
            .ok_or_else(|| "database is not admitted".to_owned())?;
        let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
            .map_err(|_| "invalid admitted tenant")?;
        let outcome = entry
            .host
            .engine()
            .lifecycle_pause(&entry.host.engine().scope_for(&tenant), operation_id)
            .map_err(|_| "database pause failed")?;
        if outcome.state.paused {
            serving.remove(database);
        }
        Ok(outcome)
    }

    /// Resumes one paused admitted database without replacing its identity.
    pub fn resume(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        let mut serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        let entry = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned()
            .ok_or_else(|| "database is not admitted".to_owned())?;
        let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
            .map_err(|_| "invalid admitted tenant")?;
        let outcome = entry
            .host
            .engine()
            .lifecycle_resume(&entry.host.engine().scope_for(&tenant), operation_id)
            .map_err(|_| "database resume failed")?;
        if !outcome.state.paused && !outcome.state.access_revoked() {
            serving.insert(database.to_owned());
        }
        Ok(outcome)
    }

    /// Binds every serving lookup to the complete Member's current readiness.
    #[must_use]
    pub fn new(ready: Arc<dyn Fn() -> bool + Send + Sync>) -> Self {
        Self {
            ready,
            databases: RwLock::new(BTreeMap::new()),
            serving: RwLock::new(BTreeSet::new()),
            draining: AtomicBool::new(false),
            restore_paths: Arc::new(std::sync::Mutex::new(BTreeSet::new())),
        }
    }

    /// Returns the live Member admission predicate used by publication and
    /// readiness-gated serving lookups.
    ///
    /// Degraded serving intentionally does not call this method: retained
    /// admitted databases may continue serving reads while new database
    /// publication is fenced.  The public regional health surface uses this
    /// predicate so discovery and admission report the same authority.
    #[must_use]
    pub fn admission_ready(&self) -> bool {
        !(self.draining.load(Ordering::Acquire)) && (self.ready)()
    }

    /// Fences publication, activation, and serving lookups while retaining
    /// admitted hosts for shutdown observations.
    ///
    /// # Errors
    /// Refuses unavailable activation state.
    pub fn begin_drain(&self) -> Result<(), String> {
        let _serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        self.draining.store(true, Ordering::Release);
        Ok(())
    }

    /// Releases retained hosts after the registry has been fenced.
    ///
    /// Host drops occur after both registry locks are released so Member
    /// shutdown cannot re-enter registry code while a lock is held.
    ///
    /// # Errors
    /// Refuses release before fencing or when registry state is unavailable.
    pub fn release_drained_hosts(&self) -> Result<(), String> {
        let mut serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        if !self.draining.load(Ordering::Acquire) {
            return Err("admitted registry must be fenced before releasing hosts".into());
        }
        let hosts = {
            let mut databases =
                self.databases.write().map_err(|_| "admitted registry unavailable")?;
            serving.clear();
            std::mem::take(&mut *databases)
        };
        drop(serving);
        drop(hosts);
        Ok(())
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
        // Keep the owner independent of the lease before moving the lease
        // into the registry entry.
        let scope = lease.database_owner().clone();
        if !self.admission_ready() {
            return Err("Member is not ready to admit databases".into());
        }
        self.publish_inner(record, lease, location, instance, scope)
    }

    /// Publishes a retained admission while startup is replaying durable
    /// manifests. The readiness gate still protects all public lookups; this
    /// path only restores the retained host needed for final readiness.
    pub(crate) fn publish_retained(
        &self,
        record: AdmissionRecord,
        lease: DatabaseLease,
        location: PathBuf,
        instance: u64,
    ) -> Result<(), String> {
        let scope = lease.database_owner().clone();
        self.publish_inner(record, lease, location, instance, scope)
    }

    fn publish_inner(
        &self,
        record: AdmissionRecord,
        lease: DatabaseLease,
        location: PathBuf,
        instance: u64,
        scope: terrabase::storage::DatabaseOwner,
    ) -> Result<(), String> {
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
        let _serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("admitted registry is draining".into());
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
            Arc::new(AdmittedDatabase {
                record,
                host,
                activity: Arc::new(
                    terrabase::engine::lifecycle_state::AuthorizedActivity::default(),
                ),
            }),
        );
        Ok(())
    }

    /// Inspects retained hosts without consulting the Member readiness callback.
    /// Readiness and metrics composition use this path to avoid recursively
    /// invoking the readiness decision they are currently evaluating.
    /// Public serving must continue through the readiness-gated lookup methods.
    ///
    /// # Errors
    /// Refuses poisoned registry or activation state rather than returning a
    /// partial inventory that could omit a required host.
    pub fn retained_snapshot(&self) -> Result<Vec<AdmittedDatabaseSnapshot>, String> {
        // Use the same lock order as activation and serving lookup.
        let serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        let retained = self.databases.read().map_err(|_| "admitted registry unavailable")?;
        Ok(retained
            .iter()
            .map(|(id, database)| AdmittedDatabaseSnapshot {
                database: Arc::clone(database),
                activated: serving.contains(id),
            })
            .collect())
    }

    /// Resolves an exact canonical admitted database identity.
    ///
    /// # Errors
    /// Returns an error when the Member is not ready or the registry lock is unavailable.
    pub fn resolve(&self, database: &str) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        let _serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        let entry = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned();
        if let Some(entry) = &entry {
            let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
                .map_err(|_| "invalid admitted tenant")?;
            let state = entry
                .host
                .engine()
                .lifecycle_state(&entry.host.engine().scope_for(&tenant))
                .map_err(|_| "lifecycle source unavailable")?;
            if state.paused {
                return Ok(None);
            }
        }
        Ok(entry)
    }

    /// Publishes a validated batch only after replication lanes are committed.
    /// Missing entries refuse the entire batch; identical retries are harmless.
    ///
    /// # Errors
    /// Returns an error when an entry is not retained or the registry is unavailable.
    pub fn activate(&self, databases: &[String]) -> Result<(), String> {
        let mut serving = self.serving.write().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("admitted registry is draining".into());
        }
        let retained = self.databases.read().map_err(|_| "admitted registry unavailable")?;
        if databases.iter().any(|id| !retained.contains_key(id)) {
            return Err("activation database is not retained".into());
        }
        serving.extend(databases.iter().cloned());
        Ok(())
    }

    /// Public lookup excludes retained entries whose lanes are still pending.
    ///
    /// # Errors
    /// Returns an error when the Member is not ready or the registry is unavailable.
    pub fn resolve_serving(&self, database: &str) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        let serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        if !serving.contains(database) {
            return Ok(None);
        }
        let mut databases = self.databases.write().map_err(|_| "admitted registry unavailable")?;
        if let Some(entry) = databases.get(database) {
            if let Some(host) = entry
                .host
                .refreshed_serving_host()
                .map_err(|_| "shard serving refresh unavailable")?
            {
                let replacement = Arc::new(AdmittedDatabase {
                    activity: entry.activity.clone(),
                    record: entry.record.clone(),
                    host: Arc::new(host),
                });
                databases.insert(database.to_owned(), replacement);
            }
        }
        let entry = databases.get(database).cloned();
        drop(databases);
        if let Some(entry) = &entry {
            let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
                .map_err(|_| "invalid admitted tenant")?;
            let state = entry
                .host
                .engine()
                .lifecycle_state(&entry.host.engine().scope_for(&tenant))
                .map_err(|_| "lifecycle source unavailable")?;
            if state.paused || state.access_revoked() {
                return Ok(None);
            }
        }
        Ok(entry)
    }

    /// Resolves an already activated database while the control-plane
    /// readiness gate is degraded. This preserves local reads for retained
    /// leases; publication and new admission still require `ready`.
    pub fn resolve_degraded_serving(
        &self,
        database: &str,
    ) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        let serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) || !serving.contains(database) {
            return Ok(None);
        }
        let entry = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned();
        if let Some(entry) = &entry {
            let tenant = terrabase::types::TenantId::try_new(&entry.record.tenant_id)
                .map_err(|_| "invalid admitted tenant")?;
            let state = entry
                .host
                .engine()
                .lifecycle_state(&entry.host.engine().scope_for(&tenant))
                .map_err(|_| "lifecycle source unavailable")?;
            if state.paused || state.access_revoked() {
                return Ok(None);
            }
        }
        Ok(entry)
    }

    /// Metadata can describe a terminal deletion without reopening document access.
    ///
    /// # Errors
    /// Returns an error when readiness, registry state, or lifecycle metadata is unavailable.
    pub fn resolve_metadata(
        &self,
        database: &str,
    ) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        let serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        let retained = self
            .databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .get(database)
            .cloned();
        match retained {
            Some(entry) if serving.contains(database) || entry.public_record()?.deleted => {
                Ok(Some(entry))
            }
            _ => Ok(None),
        }
    }

    /// Resolves a retained database for lifecycle authority checks, including
    /// paused entries that are intentionally absent from serving resolution.
    pub fn resolve_lifecycle(
        &self,
        database: &str,
    ) -> Result<Option<Arc<AdmittedDatabase>>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        self.databases
            .read()
            .map_err(|_| "admitted registry unavailable".into())
            .map(|databases| databases.get(database).cloned())
    }

    /// Public metadata excludes pending entries but retains terminal deletion records.
    ///
    /// # Errors
    /// Returns an error when the Member is not ready or an admitted lifecycle state cannot be read.
    pub fn public_records(&self) -> Result<Vec<DatabaseRecord>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        let serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        self.databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .iter()
            .filter_map(|(id, database)| match database.public_record() {
                Ok(record) if serving.contains(id) || record.deleted => Some(Ok(record)),
                Ok(_) => None,
                Err(error) => Some(Err(error)),
            })
            .collect()
    }

    /// Reads at most sixteen registry keys for operation discovery. The cursor
    /// advances over foreign scopes without exposing their identities.
    pub(crate) fn discovery_page(
        &self,
        tenant: &str,
        project: &str,
        after: &str,
    ) -> Result<(Vec<Arc<AdmittedDatabase>>, Option<String>), String> {
        if !(self.ready)() || self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        let databases = self.databases.read().map_err(|_| "admitted registry unavailable")?;
        let mut entries = databases
            .range((std::ops::Bound::Excluded(after.to_owned()), std::ops::Bound::Unbounded));
        let mut result = Vec::new();
        let mut last = None;
        for (id, database) in entries.by_ref().take(16) {
            last = Some(id.clone());
            if database.record.tenant_id == tenant && database.record.project_id == project {
                result.push(database.clone());
            }
        }
        let cursor = entries.next().and(last);
        Ok((result, cursor))
    }

    /// Enumerates admitted databases for bounded maintenance drivers.
    ///
    /// # Errors
    /// Returns an error when the Member is not ready, the registry is unavailable, or metadata is invalid.
    pub fn records(&self) -> Result<Vec<DatabaseRecord>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to serve".into());
        }
        let _serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
            return Err("Member is not ready to serve".into());
        }
        self.databases
            .read()
            .map_err(|_| "admitted registry unavailable")?
            .values()
            .map(|database| database.public_record())
            .collect::<Result<Vec<_>, _>>()
    }
}

impl AdmittedDatabaseRegistry {
    /// Collects actual admitted lifecycle observations without inventing missing scopes.
    ///
    /// # Errors
    /// Returns an error when the Member is not ready or draining, a registry
    /// lock is poisoned or lifecycle state cannot be read. Multi-region records
    /// are omitted because this local member cannot certify their global state.
    pub fn lifecycle_snapshots(
        &self,
        now: u64,
    ) -> Result<Vec<crate::lifecycle_publication::LifecyclePublicationSnapshot>, String> {
        if !(self.ready)() {
            return Err("Member is not ready to publish lifecycle".into());
        }
        let _serving = self.serving.read().map_err(|_| "admitted activation unavailable")?;
        if self.draining.load(Ordering::Acquire) {
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
            // A local observation cannot certify database-wide state while
            // another selected Region may still be serving authorized requests.
            if database.record.regions.len() != 1 {
                continue;
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
    /// The exact operation and boundary survive restart in the database `SQLite` file.
    ///
    /// # Errors
    /// Returns an error when the record is not admitted, preparation or cutoff boundaries do not match, native fencing fails, or reactive revocation fails.
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
    /// Atomically protects the deletion operation against subsequent writes/protection changes.
    ///
    /// # Errors
    /// Returns an error when the record is not admitted, native preparation fails, or reactive revocation fails.
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
    /// Reads durable native deletion protection; no activity or policy defaults are synthesized.
    ///
    /// # Errors
    /// Returns an error when the record is not admitted, the Member is not ready or draining, a registry lock is poisoned, or lifecycle storage cannot be read.
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

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use terrabase::{
        identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid},
        member::{
            AcceptedDatabaseRole, AdmissionLimits, DatabaseRole, DatabaseRoleAuthority,
            DatabaseScope, DatabaseSpec, Member,
        },
        regions::RegionCode,
    };

    struct Writer;
    impl DatabaseRoleAuthority for Writer {
        fn accepted_role(&self, _: &terrabase::member::DatabaseId) -> Option<AcceptedDatabaseRole> {
            Some(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer })
        }
    }

    pub(crate) fn fixture() -> (AdmittedDatabaseRegistry, Member, Vec<AdmissionRecord>, PathBuf) {
        let root = std::env::temp_dir().join(format!("admitted-api-{}", DatabaseUlid::generate()));
        std::fs::create_dir_all(&root).unwrap();
        let records: Vec<_> = (0..2)
            .map(|_| AdmissionRecord {
                created_at: 1,
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
            member.accept_database_role(&id, &Writer).unwrap();
            let lease = member.acquire(&id).unwrap();
            registry.publish(record.clone(), lease, root.join(&record.database_id), 1).unwrap();
        }
        registry
            .activate(&records.iter().map(|record| record.database_id.clone()).collect::<Vec<_>>())
            .unwrap();
        (registry, member, records, root)
    }

    #[test]
    fn cancelled_restore_republishes_paused_target_for_retry() {
        let (registry, member, records, root) = fixture();
        let registry = Arc::new(registry);
        let database = &records[0].database_id;
        registry.pause(database, "restore-pause").unwrap();
        let reservation = registry
            .reserve_restore(database)
            .unwrap()
            .rollback_on_drop(member.clone(), Arc::clone(&registry));
        let id = terrabase::member::DatabaseId::try_new(database).unwrap();
        member.close_database(&id).expect("release resident engine before installation");
        drop(reservation);
        assert!(registry.resolve_serving(database).unwrap().is_none());
        let retry = registry
            .reserve_restore(database)
            .expect("cancelled target remains admitted")
            .rollback_on_drop(member.clone(), Arc::clone(&registry));
        drop(retry);
        registry.resume(database, "resume-after-cancel").unwrap();
        assert!(registry.resolve_serving(database).unwrap().is_some());
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn restore_reservation_is_unique_and_republishes_after_guarded_reopen() {
        let (registry, member, records, root) = fixture();
        let database = &records[0].database_id;
        registry.pause(database, "restore-pause").unwrap();
        let reservation = registry.reserve_restore(database).unwrap();
        assert!(registry.reserve_restore(database).is_err());
        let id = terrabase::member::DatabaseId::try_new(database).unwrap();
        assert!(matches!(
            member.acquire_existing(&id),
            Err(terrabase::member::MemberError::DatabaseBusy(_))
        ));
        let lease = reservation.reacquire(&member).unwrap();
        reservation.complete(&registry, lease, 2).unwrap();
        registry.activate(&[database.clone()]).unwrap();
        // Republication retains the durable pause until the lifecycle owner
        // explicitly resumes the restored target.
        registry.resume(database, "restore-resume").unwrap();
        assert!(registry.resolve_serving(database).unwrap().is_some());
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn publication_compares_record_against_the_lease_owner() {
        let (registry, member, records, root) = fixture();
        let database = &records[0].database_id;
        registry.pause(database, "owner-mismatch-pause").unwrap();
        let reservation = registry.reserve_restore(database).unwrap();

        let mut mismatched = records[0].clone();
        mismatched.environment = "staging".into();
        let lease = reservation.reacquire(&member).unwrap();
        assert_eq!(
            registry.publish(mismatched, lease, root.join(database), 2),
            Err("admitted database owner mismatch".into())
        );

        let lease = reservation.reacquire(&member).unwrap();
        reservation.complete(&registry, lease, 2).unwrap();
        registry.activate(std::slice::from_ref(&records[0].database_id)).unwrap();
        registry.resume(database, "owner-mismatch-resume").unwrap();
        assert!(registry.resolve_serving(database).unwrap().is_some());
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn public_activation_requires_the_entire_retained_batch() {
        let (registry, member, records, root) = fixture();
        registry.serving.write().unwrap().clear();
        assert!(registry.resolve(&records[0].database_id).unwrap().is_some());
        assert!(registry.resolve_serving(&records[0].database_id).unwrap().is_none());
        assert!(registry.resolve_metadata(&records[0].database_id).unwrap().is_none());
        assert!(registry.public_records().unwrap().is_empty());
        assert!(
            registry
                .activate(&[records[0].database_id.clone(), DatabaseUlid::generate().to_text()])
                .is_err()
        );
        assert!(registry.resolve_serving(&records[0].database_id).unwrap().is_none());
        let batch = records.iter().map(|record| record.database_id.clone()).collect::<Vec<_>>();
        registry.activate(&batch).unwrap();
        registry.activate(&batch).unwrap();
        assert_eq!(registry.public_records().unwrap().len(), 2);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn drain_fences_publication_and_serving_but_retains_hosts_until_release() {
        let (registry, member, records, root) = fixture();
        let snapshot = registry.retained_snapshot().unwrap();
        assert_eq!(snapshot.len(), records.len());
        assert!(registry.release_drained_hosts().is_err());
        registry.begin_drain().unwrap();
        registry.begin_drain().unwrap();
        assert!(registry.resolve(&records[0].database_id).is_err());
        assert!(registry.resolve_serving(&records[0].database_id).is_err());
        assert!(registry.resolve_metadata(&records[0].database_id).is_err());
        assert!(registry.records().is_err());
        assert!(registry.public_records().is_err());
        assert!(registry.activate(&[records[0].database_id.clone()]).is_err());
        let id = terrabase::member::DatabaseId::try_new(&records[0].database_id).unwrap();
        let lease = member.acquire(&id).unwrap();
        assert!(
            registry
                .publish(records[0].clone(), lease, root.join(&records[0].database_id), 2)
                .is_err()
        );
        assert!(member.close(std::time::Duration::from_millis(50)).is_err());
        registry.release_drained_hosts().unwrap();
        registry.release_drained_hosts().unwrap();
        assert!(registry.retained_snapshot().unwrap().is_empty());
        assert!(member.close(std::time::Duration::ZERO).is_err());
        drop(snapshot);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn live_member_fence_denies_previously_published_database() {
        let (mut registry, member, records, root) = fixture();
        let ready = Arc::new(std::sync::atomic::AtomicBool::new(true));
        let gate = ready.clone();
        registry.ready = Arc::new(move || gate.load(std::sync::atomic::Ordering::Acquire));
        assert!(registry.resolve(&records[0].database_id).unwrap().is_some());
        ready.store(false, std::sync::atomic::Ordering::Release);
        assert!(!registry.admission_ready());
        assert!(registry.resolve(&records[0].database_id).is_err());
        let id = terrabase::member::DatabaseId::try_new(&records[0].database_id).unwrap();
        let lease = member.acquire(&id).unwrap();
        assert!(
            registry
                .publish(records[0].clone(), lease, root.join(&records[0].database_id), 2)
                .is_err(),
            "degraded readiness refuses new admission"
        );
        assert!(registry.records().is_err());
        registry.serving.write().unwrap().remove(&records[0].database_id);
        let snapshot = registry.retained_snapshot().unwrap();
        assert_eq!(snapshot.len(), records.len());
        for entry in &snapshot {
            assert_eq!(
                entry.activated,
                entry.database.record.database_id != records[0].database_id
            );
            assert!(Arc::ptr_eq(
                &entry.database,
                registry.databases.read().unwrap().get(&entry.database.record.database_id).unwrap()
            ));
        }
        drop(snapshot);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn readiness_can_inspect_hosts_without_registry_locks_held() {
        let (mut registry, member, records, root) = fixture();
        let registry = Arc::new_cyclic(|weak: &std::sync::Weak<AdmittedDatabaseRegistry>| {
            let weak = weak.clone();
            registry.ready = Arc::new(move || {
                let registry = weak.upgrade().unwrap();
                drop(
                    registry
                        .serving
                        .try_write()
                        .expect("readiness called while activation lock held"),
                );
                drop(
                    registry.databases.try_write().expect("readiness called while host lock held"),
                );
                registry.retained_snapshot().unwrap().len() == 2
            });
            registry
        });
        for record in &records {
            assert!(registry.resolve_serving(&record.database_id).unwrap().is_some());
            assert!(registry.resolve_metadata(&record.database_id).unwrap().is_some());
        }
        assert_eq!(registry.public_records().unwrap().len(), 2);
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
            let lease_ptr = std::ptr::from_ref::<terrabase::engine::Engine>(&*lease);
            assert!(std::ptr::eq(admitted.host.engine(), lease_ptr));
        }
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn pause_hides_serving_database_and_resume_retains_identity() {
        let (registry, member, records, root) = fixture();
        let database = &records[0].database_id;
        let admitted = registry.resolve(database).unwrap().unwrap();
        let engine = admitted.host.engine();
        let tenant = terrabase::types::TenantId::try_new(&records[0].tenant_id).unwrap();
        let scope = engine.scope_for(&tenant);

        registry.pause(database, "pause-1").unwrap();
        assert!(engine.lifecycle_state(&scope).unwrap().paused);
        assert!(registry.resolve(database).unwrap().is_none());
        assert!(registry.resolve_serving(database).unwrap().is_none());
        assert!(registry.resolve_metadata(database).unwrap().is_some());

        registry.resume(database, "resume-1").unwrap();
        assert!(!engine.lifecycle_state(&scope).unwrap().paused);
        let resumed = registry.resolve(database).unwrap().unwrap();
        assert!(Arc::ptr_eq(&admitted, &resumed));

        engine.lifecycle_prepare_deletion(&scope, "delete-after-pause").unwrap();
        assert!(registry.resolve(database).unwrap().is_some());
        assert!(registry.resolve_serving(database).unwrap().is_none());
        assert!(registry.pause(database, "pause-2").is_err());
        assert!(registry.resume(database, "resume-2").is_err());

        drop(admitted);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn public_records_reflect_durable_deletion_and_retain_maintenance_lease() {
        let (registry, member, records, root) = fixture();
        let first = registry.resolve(&records[0].database_id).unwrap().unwrap();
        let engine = first.host.engine();
        let tenant = terrabase::types::TenantId::try_new(&records[0].tenant_id).unwrap();
        let scope = engine.scope_for(&tenant);

        let before = first.public_record().unwrap();
        assert!(!before.deleted);
        assert_eq!(before.deleted_at, None);
        assert_eq!(before.created_at, records[0].created_at);

        let prepared_state = engine.lifecycle_prepare_deletion(&scope, "delete-1").unwrap();
        let cutoff = prepared_state.deletion_preparation.unwrap().minimum_cutoff_millis;
        let prepared = first.public_record().unwrap();
        assert!(!prepared.deleted);
        assert!(registry.resolve(&records[0].database_id).unwrap().is_some());

        engine
            .lifecycle_accept_deletion(
                &scope,
                terrabase::engine::lifecycle_state::DeletionIntent {
                    operation_id: "delete-1".into(),
                    usage_stopped_at: cutoff,
                    retain_until: Some(cutoff + 100),
                },
            )
            .unwrap();
        let deleted = first.public_record().unwrap();
        assert!(deleted.deleted);
        assert_eq!(deleted.deleted_at, Some(i64::try_from(cutoff).unwrap()));
        registry.serving.write().unwrap().remove(&records[0].database_id);
        assert!(registry.resolve_metadata(&records[0].database_id).unwrap().is_some());
        assert!(registry.resolve_serving(&records[0].database_id).unwrap().is_none());
        assert!(
            registry
                .public_records()
                .unwrap()
                .iter()
                .any(|record| record.name == records[0].database_id && record.deleted)
        );
        assert!(registry.resolve(&records[0].database_id).unwrap().is_some());

        let unrelated =
            registry.resolve(&records[1].database_id).unwrap().unwrap().public_record().unwrap();
        assert!(!unrelated.deleted);
        assert_eq!(unrelated.deleted_at, None);

        drop(first);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn lifecycle_snapshots_skip_multi_region_and_publish_healthy_records() {
        let (registry, member, records, root) = fixture();
        {
            let mut databases = registry.databases.write().unwrap();
            Arc::get_mut(databases.get_mut(&records[1].database_id).unwrap())
                .unwrap()
                .record
                .regions = vec!["GRU".into(), "SFO".into()];
        }
        let snapshots = registry.lifecycle_snapshots(1).unwrap();
        assert_eq!(snapshots.len(), 1, "unsupported scope must not fabricate a global fact");
        assert_eq!(snapshots[0].database_id, records[0].database_id);

        {
            let mut databases = registry.databases.write().unwrap();
            Arc::get_mut(databases.get_mut(&records[1].database_id).unwrap())
                .unwrap()
                .record
                .regions = vec!["GRU".into()];
        }
        let snapshots = registry.lifecycle_snapshots(1).unwrap();
        assert_eq!(snapshots.len(), 2, "healthy records remain publishable");

        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}
