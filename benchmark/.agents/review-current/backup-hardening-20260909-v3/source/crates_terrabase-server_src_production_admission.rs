//! Production admission bound to accepted placement, encrypted Member storage, and public routing.
use crate::{
    admission::{AdmissionRecord, StorageAdmission},
    admitted_databases::AdmittedDatabaseRegistry,
};
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use terrabase::{
    encryption::{
        DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultDatabaseKeys,
        OciVaultKeyDelivery,
    },
    identity::{DatabaseUlid, ProjectUlid, TenantUlid},
    member::{DatabaseId, DatabaseScope, DatabaseSpec, Member},
    regions::RegionCode,
    types::TenantId,
    usage::UsagePlacement,
};

/// Placement facts loaded from the authenticated, accepted journal, never from create intent.
pub enum AcceptedAdmission {
    /// Active writer admission with an independently registered billing stream.
    Active {
        /// Canonical partition identity from accepted placement.
        partition: String,
        /// Immutable placement revision used by billing.
        revision: String,
        /// Accepted regional origin.
        origin: RegionCode,
        /// Exact residency spelling accepted by the billing authority.
        residency: String,
        /// Strictly positive accepted writer epoch.
        epoch: u64,
        /// Whether this exact local member is the accepted writer.
        local_is_writer: bool,
    },
    /// Revoked database reopened only for deletion and maintenance.
    Maintenance {
        /// Accepted regional origin.
        origin: RegionCode,
        /// Exact residency spelling accepted by the billing authority.
        residency: String,
        /// Accepted epoch, which is never activated for origin writes.
        epoch: u64,
        /// Whether this exact local member is the accepted writer.
        local_is_writer: bool,
    },
}

/// Loads exact accepted authority; `None` means authenticated registration is pending.
pub type AdmissionAuthority =
    Arc<dyn Fn(&AdmissionRecord) -> Result<Option<AcceptedAdmission>, String> + Send + Sync>;

#[cfg(test)]
struct Writer(u64);

#[cfg(test)]
impl terrabase::member::DatabaseRoleAuthority for Writer {
    fn accepted_role(
        &self,
        _: &terrabase::member::DatabaseId,
    ) -> Option<terrabase::member::AcceptedDatabaseRole> {
        Some(terrabase::member::AcceptedDatabaseRole {
            epoch: self.0,
            role: terrabase::member::DatabaseRole::Writer,
        })
    }
}

fn register_storage(
    member: &Member,
    provider: &OciVaultKeyDelivery,
    customer_source: Option<
        &(
             dyn Fn(
            &AdmissionRecord,
        ) -> Result<Arc<dyn terrabase::encryption::DatabaseKeySource>, String>
                 + Send
                 + Sync
         ),
    >,
    record: &AdmissionRecord,
    per_database: usize,
) -> Result<(RegionCode, TenantId), String> {
    let region = record.region.parse::<RegionCode>().map_err(|_| "invalid origin")?;
    let tenant = TenantUlid::from_text(&record.tenant_id).map_err(|_| "invalid tenant")?;
    let project = ProjectUlid::from_text(&record.project_id).map_err(|_| "invalid project")?;
    let database = DatabaseUlid::from_text(&record.database_id).map_err(|_| "invalid database")?;
    let version =
        DatabaseKeyVersion::from_text(&record.key_version).map_err(|_| "invalid key version")?;
    let scope = DatabaseScope::try_new(&record.environment, tenant, project, database, region)
        .map_err(|_| "invalid owner")?;
    let key_scope = KeyScope::new_with_project(
        &record.environment,
        tenant,
        project,
        database,
        KeyPurpose::Data,
    )
    .map_err(|_| "invalid key scope")?;
    let keys: Arc<dyn terrabase::encryption::DatabaseKeySource> =
        if let Some(resolve) = customer_source {
            resolve(record)?
        } else {
            Arc::new(
                OciVaultDatabaseKeys::new(
                    provider.clone(),
                    KeyVersionMetadata::new(key_scope, version),
                    region,
                )
                .map_err(|_| "key authorization unavailable")?,
            )
        };
    member
        .add_production_database(
            DatabaseSpec::scoped(scope, &record.database_id),
            per_database,
            keys,
        )
        .map_err(|_| "database storage admission refused")?;
    Ok((region, TenantId::try_new(&record.tenant_id).map_err(|_| "invalid tenant")?))
}

/// Constructs the replay and live callback used by the authenticated admission host.
/// Storage is opened only after accepted writer authority and exact OCI key authorization.
#[must_use]
pub fn storage_admission(
    member: Member,
    registry: Arc<AdmittedDatabaseRegistry>,
    provider: OciVaultKeyDelivery,
    authority: AdmissionAuthority,
    per_database: usize,
    instance: u64,
) -> StorageAdmission {
    storage_admission_inner(member, registry, provider, authority, per_database, instance, None)
}

/// Constructs storage admission with a startup-only retained-manifest replay
/// window. The caller closes `replaying` after the first reconciliation commits
/// its lanes, before live admission can use the callback.
pub fn storage_admission_with_replay(
    member: Member,
    registry: Arc<AdmittedDatabaseRegistry>,
    provider: OciVaultKeyDelivery,
    authority: AdmissionAuthority,
    per_database: usize,
    instance: u64,
    replaying: Arc<AtomicBool>,
) -> StorageAdmission {
    storage_admission_inner(
        member,
        registry,
        provider,
        authority,
        per_database,
        instance,
        Some(replaying),
    )
}

/// Constructs storage admission with customer key-source resolution for BYOK records.
pub fn storage_admission_with_replay_and_customer(
    member: Member,
    registry: Arc<AdmittedDatabaseRegistry>,
    provider: OciVaultKeyDelivery,
    authority: AdmissionAuthority,
    per_database: usize,
    instance: u64,
    replaying: Arc<AtomicBool>,
    customer_source: Arc<
        dyn Fn(
                &AdmissionRecord,
            ) -> Result<Arc<dyn terrabase::encryption::DatabaseKeySource>, String>
            + Send
            + Sync,
    >,
) -> StorageAdmission {
    storage_admission_inner_with_customer(
        member,
        registry,
        provider,
        authority,
        per_database,
        instance,
        Some(replaying),
        Some(customer_source),
    )
}

fn storage_admission_inner(
    member: Member,
    registry: Arc<AdmittedDatabaseRegistry>,
    provider: OciVaultKeyDelivery,
    authority: AdmissionAuthority,
    per_database: usize,
    instance: u64,
    replaying: Option<Arc<AtomicBool>>,
) -> StorageAdmission {
    storage_admission_inner_with_customer(
        member,
        registry,
        provider,
        authority,
        per_database,
        instance,
        replaying,
        None,
    )
}

fn storage_admission_inner_with_customer(
    member: Member,
    registry: Arc<AdmittedDatabaseRegistry>,
    provider: OciVaultKeyDelivery,
    authority: AdmissionAuthority,
    per_database: usize,
    instance: u64,
    replaying: Option<Arc<AtomicBool>>,
    customer_source: Option<
        Arc<
            dyn Fn(
                    &AdmissionRecord,
                )
                    -> Result<Arc<dyn terrabase::encryption::DatabaseKeySource>, String>
                + Send
                + Sync,
        >,
    >,
) -> StorageAdmission {
    Arc::new(move |record| {
        let (region, tenant_id) =
            register_storage(&member, &provider, customer_source.as_deref(), record, per_database)?;
        // Registration precedes placement proposal. A pending scope is durable but
        // remains closed and absent from public routing until an authenticated retry.
        let Some(accepted) = authority(record)? else {
            return Ok(());
        };
        match accepted {
            AcceptedAdmission::Maintenance { epoch, origin, residency, local_is_writer } => {
                if epoch == 0 || origin != region || residency != record.residency {
                    return Err("invalid accepted database authority".into());
                }
                if !local_is_writer {
                    return Ok(());
                }
                let id =
                    DatabaseId::try_new(&record.database_id).map_err(|_| "invalid database")?;
                let lease =
                    member.acquire_existing(&id).map_err(|_| "encrypted database unavailable")?;
                if lifecycle_allows_origin_activation(&lease, &tenant_id)? {
                    return Err("maintenance admission is not access-revoked".into());
                }
                publish(
                    &registry,
                    replaying.as_ref(),
                    record.clone(),
                    lease,
                    member.root().join(&record.database_id),
                    instance,
                )
            }
            AcceptedAdmission::Active {
                partition,
                revision,
                origin,
                residency,
                epoch,
                local_is_writer,
            } => {
                if epoch == 0 || origin != region || residency != record.residency {
                    return Err("invalid accepted database authority".into());
                }
                if !local_is_writer {
                    return Ok(());
                }
                let id =
                    DatabaseId::try_new(&record.database_id).map_err(|_| "invalid database")?;
                let lease = member.acquire(&id).map_err(|_| "encrypted database unavailable")?;
                // Preparation and acceptance are durable origin fences. Replay
                // retains revoked encrypted entries for authorized maintenance.
                if !lifecycle_allows_origin_activation(&lease, &tenant_id)? {
                    publish(
                        &registry,
                        replaying.as_ref(),
                        record.clone(),
                        lease,
                        member.root().join(&record.database_id),
                        instance,
                    )?;
                    return Ok(());
                }
                let placement = UsagePlacement::try_new(
                    tenant_id,
                    &record.project_id,
                    &record.database_id,
                    &partition,
                    region,
                    record
                        .regions
                        .iter()
                        .map(|region| region.parse::<RegionCode>())
                        .collect::<Result<Vec<_>, _>>()
                        .map_err(|_| "invalid selected regions")?,
                    &revision,
                )
                .map_err(|_| "invalid usage placement")?;
                lease
                    .configure_usage_placement(placement)
                    .map_err(|_| "usage placement refused")?;
                lease.activate_origin_epoch(epoch).map_err(|_| "writer epoch refused")?;
                publish(
                    &registry,
                    replaying.as_ref(),
                    record.clone(),
                    lease,
                    member.root().join(&record.database_id),
                    instance,
                )
            }
        }
    })
}

fn publish(
    registry: &AdmittedDatabaseRegistry,
    replaying: Option<&Arc<AtomicBool>>,
    record: AdmissionRecord,
    lease: terrabase::member::DatabaseLease,
    location: std::path::PathBuf,
    instance: u64,
) -> Result<(), String> {
    if replaying.is_some_and(|flag| flag.load(Ordering::Acquire)) {
        registry.publish_retained(record, lease, location, instance)
    } else {
        registry.publish(record, lease, location, instance)
    }
}

/// Reads the durable deletion fence before replay can activate a writer epoch.
/// A false result retains the lease for authorized maintenance without
/// reopening origin writes.
pub(crate) fn lifecycle_allows_origin_activation(
    lease: &terrabase::member::DatabaseLease,
    tenant: &TenantId,
) -> Result<bool, String> {
    lease
        .lifecycle_state(&lease.scope_for(tenant))
        .map(|state| !state.access_revoked())
        .map_err(|_| "database lifecycle unavailable".into())
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::Writer;
    use super::{
        AcceptedAdmission, AdmissionAuthority, lifecycle_allows_origin_activation,
        storage_admission,
    };
    use crate::{
        admission::{AdmissionHost, AdmissionRecord},
        admitted_databases::AdmittedDatabaseRegistry,
    };
    use std::{
        collections::BTreeMap,
        fs,
        sync::{
            Arc,
            atomic::{AtomicBool, Ordering},
        },
        time::Duration,
    };
    use terrabase::{
        engine::lifecycle_state::DeletionIntent,
        identity::{DatabaseUlid, ProjectUlid, TenantUlid},
        member::{
            AcceptedDatabaseRole, AdmissionLimits, DatabaseId, DatabaseRole, DatabaseRoleAuthority,
            DatabaseScope, DatabaseSpec, Member,
        },
        regions::RegionCode,
        types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
            OperationId, TenantId,
        },
    };

    fn record(scope: &DatabaseScope, member: &str) -> AdmissionRecord {
        AdmissionRecord {
            created_at: 1,
            environment: "production".into(),
            tenant_id: scope.tenant().to_text(),
            project_id: scope.project().to_text(),
            database_id: scope.database().to_text(),
            parent_database_id: None,
            key_version: "01ARZ3NDEKTSV4RRFFQ69G5FB0".into(),
            name: scope.database().to_text(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: "admission-replay".into(),
            region: "GRU".into(),
            member_id: member.into(),
        }
    }

    fn tenant(scope: &DatabaseScope) -> TenantId {
        TenantId::try_new(&scope.tenant().to_text()).unwrap()
    }

    fn replay_case(accepted: bool) {
        let root = std::env::temp_dir()
            .join(format!("terrabase-admission-host-replay-{}", DatabaseUlid::generate()));
        let member_id = terrabase::identity::MemberUlid::generate();
        let fenced = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .unwrap();
        let unrelated = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .unwrap();
        let specs = [
            DatabaseSpec::scoped(fenced.clone(), fenced.database().to_text()),
            DatabaseSpec::scoped(unrelated.clone(), unrelated.database().to_text()),
        ];
        fs::create_dir_all(&root).unwrap();
        let fenced_id = DatabaseId::from(fenced.database());
        let unrelated_id = DatabaseId::from(unrelated.database());
        let fixture = crate::test_keys::key_delivery();
        let successor_maintenance = Arc::new(AtomicBool::new(false));
        let maintenance_mode = Arc::clone(&successor_maintenance);
        let fenced_text = fenced.database().to_text();
        let open = || {
            let mut keys =
                crate::test_keys::storage_keys_with_provider(&fenced, fixture.provider.clone());
            keys.extend(crate::test_keys::storage_keys_with_provider(
                &unrelated,
                fixture.provider.clone(),
            ));
            let member =
                Member::open_production(&root, specs.clone(), AdmissionLimits::new(4, 2), keys)
                    .unwrap();
            member.set_clock_offset_seconds(0.0);
            member
        };
        let authority: AdmissionAuthority = Arc::new(move |record| {
            if maintenance_mode.load(Ordering::Acquire) && record.database_id == fenced_text {
                return Ok(Some(AcceptedAdmission::Maintenance {
                    origin: RegionCode::GRU,
                    residency: record.residency.clone(),
                    epoch: 2,
                    local_is_writer: true,
                }));
            }
            Ok(Some(AcceptedAdmission::Active {
                partition: format!("partition-{}", record.database_id),
                revision: "revision".into(),
                origin: RegionCode::GRU,
                residency: record.residency.clone(),
                epoch: 1,
                local_is_writer: true,
            }))
        });
        let member = open();
        member.accept_database_role(&fenced_id, &Writer(1)).unwrap();
        member.accept_database_role(&unrelated_id, &Writer(1)).unwrap();
        let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(|| true)));
        let callback = storage_admission(
            member.clone(),
            registry.clone(),
            fixture.provider.clone(),
            authority.clone(),
            4,
            1,
        );
        let host =
            AdmissionHost::open(&root, "production".into(), RegionCode::GRU, member_id, callback)
                .unwrap();
        drop(member.acquire(&fenced_id).expect("initial encrypted writer acquisition"));
        let fenced_record = record(&fenced, &member_id.to_text());
        let unrelated_record = record(&unrelated, &member_id.to_text());
        host.admit(&fenced_record).unwrap();
        host.admit(&unrelated_record).unwrap();
        let fenced_host = registry.resolve(&fenced.database().to_text()).unwrap().unwrap();
        let fenced_tenant = TenantId::try_new(&fenced.tenant().to_text()).unwrap();
        let initial_scope = fenced_host.host.engine().scope_for(&fenced_tenant);
        let prepared = fenced_host
            .host
            .engine()
            .lifecycle_prepare_deletion(&initial_scope, "delete-replay")
            .unwrap();
        if accepted {
            let cutoff = prepared.deletion_preparation.as_ref().unwrap().minimum_cutoff_millis;
            fenced_host
                .host
                .engine()
                .lifecycle_accept_deletion(
                    &initial_scope,
                    terrabase::engine::lifecycle_state::DeletionIntent {
                        operation_id: "delete-replay".into(),
                        usage_stopped_at: cutoff,
                        retain_until: Some(cutoff + 1),
                    },
                )
                .unwrap();
            successor_maintenance.store(true, Ordering::Release);
        }
        assert_eq!(fenced_host.host.engine().origin_epoch().unwrap(), (1, true));
        drop(fenced_host);
        drop(host);
        drop(registry);
        member.close(Duration::from_secs(1)).unwrap();

        replay_after_restart(
            accepted,
            &root,
            member_id,
            &fenced,
            &unrelated,
            &fixture,
            authority,
            fenced_tenant,
            open,
        );
    }

    fn replay_after_restart(
        accepted: bool,
        root: &std::path::Path,
        member_id: terrabase::identity::MemberUlid,
        fenced: &DatabaseScope,
        unrelated: &DatabaseScope,
        fixture: &crate::test_keys::KeyDeliveryFixture,
        authority: AdmissionAuthority,
        fenced_tenant: TenantId,
        open: impl Fn() -> Member,
    ) {
        let reopened = open();
        let fenced_id = DatabaseId::from(fenced.database());
        let unrelated_id = DatabaseId::from(unrelated.database());
        reopened.accept_database_role(&fenced_id, &Writer(if accepted { 2 } else { 1 })).unwrap();
        reopened.accept_database_role(&unrelated_id, &Writer(1)).unwrap();
        let ready = Arc::new(AtomicBool::new(false));
        let registry_ready = Arc::clone(&ready);
        let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(move || {
            registry_ready.load(Ordering::Acquire)
        })));
        let replaying = Arc::new(AtomicBool::new(true));
        let callback = super::storage_admission_with_replay(
            reopened.clone(),
            registry.clone(),
            fixture.provider.clone(),
            authority,
            4,
            2,
            Arc::clone(&replaying),
        );
        let host =
            AdmissionHost::open(root, "production".into(), RegionCode::GRU, member_id, callback)
                .unwrap();
        replaying.store(false, Ordering::Release);
        assert!(registry.resolve_serving(&fenced.database().to_text()).is_err());
        let fenced_host = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == fenced.database().to_text())
            .unwrap()
            .database;
        let scope = fenced_host.host.engine().scope_for(&fenced_tenant);
        let state = fenced_host.host.engine().lifecycle_state(&scope).unwrap();
        assert!(state.access_revoked());
        assert_eq!(fenced_host.host.engine().origin_epoch().unwrap(), (1, true));
        let unrelated_host = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == unrelated.database().to_text())
            .unwrap()
            .database;
        assert_eq!(unrelated_host.host.engine().origin_epoch().unwrap(), (1, false));
        assert!(unrelated_host.host.engine().activate_origin_epoch(1).is_ok());
        let unrelated_scope = unrelated_host
            .host
            .engine()
            .scope_for(&TenantId::try_new(&unrelated.tenant().to_text()).unwrap());
        let snapshot = unrelated_host.host.engine().read_snapshot(&unrelated_scope).unwrap();
        let mut transaction = unrelated_host
            .host
            .engine()
            .begin(&unrelated_scope, snapshot, OperationId::try_new("restart-write").unwrap())
            .unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("restart-doc").unwrap(),
                    CollectionName::try_new("users").unwrap(),
                    DocumentValue::Object(BTreeMap::default()),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            terrabase::engine::CommitOutcome::Committed(_)
        ));
        assert_eq!(
            fenced_host.host.engine().lifecycle_prepare_deletion(&scope, "delete-replay").unwrap(),
            state
        );
        if accepted {
            let cutoff = state.deletion.as_ref().unwrap().usage_stopped_at;
            let retried = fenced_host
                .host
                .engine()
                .lifecycle_accept_deletion(
                    &scope,
                    DeletionIntent {
                        operation_id: "delete-replay".into(),
                        usage_stopped_at: cutoff,
                        retain_until: state.deletion.as_ref().unwrap().retain_until,
                    },
                )
                .unwrap();
            assert!(retried.deletion.is_some());
        }
        drop((fenced_host, unrelated_host, host, registry));
        reopened.close(Duration::from_secs(1)).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn admission_host_restart_retains_preparation_fence_and_unrelated_writer() {
        replay_case(false);
    }

    #[test]
    fn admission_host_restart_retains_accepted_deletion_fence_and_retry() {
        replay_case(true);
    }

    #[test]
    fn prepared_database_replay_stays_fenced_alongside_unrelated_writer() {
        let root = std::env::temp_dir()
            .join(format!("terrabase-admission-replay-{}", DatabaseUlid::generate()));
        let make_scope = || {
            DatabaseScope::new(
                "production",
                TenantUlid::generate(),
                ProjectUlid::generate(),
                DatabaseUlid::generate(),
                RegionCode::GRU,
            )
            .unwrap()
        };
        let fenced = make_scope();
        let unrelated = make_scope();
        let specs = [
            DatabaseSpec::scoped(fenced.clone(), fenced.database().to_text()),
            DatabaseSpec::scoped(unrelated.clone(), unrelated.database().to_text()),
        ];
        let fenced_id = DatabaseId::from(fenced.database());
        let unrelated_id = DatabaseId::from(unrelated.database());
        prepare_fenced_database(
            &root,
            &specs,
            &fenced,
            &unrelated,
            fenced_id.clone(),
            unrelated_id.clone(),
        );

        let mut keys = crate::test_keys::storage_keys(&fenced);
        keys.extend(crate::test_keys::storage_keys(&unrelated));
        let reopened =
            Member::open_production(&root, specs, AdmissionLimits::new(4, 2), keys).unwrap();
        let writer = Writer(1);
        reopened.accept_database_role(&fenced_id, &writer).unwrap();
        reopened.accept_database_role(&unrelated_id, &writer).unwrap();
        let fenced_lease = reopened.acquire(&fenced_id).unwrap();
        let unrelated_lease = reopened.acquire(&unrelated_id).unwrap();
        assert!(!lifecycle_allows_origin_activation(&fenced_lease, &tenant(&fenced),).unwrap());
        assert!(
            lifecycle_allows_origin_activation(&unrelated_lease, &tenant(&unrelated),).unwrap()
        );
        assert!(unrelated_lease.activate_origin_epoch(1).is_ok());
        assert_eq!(fenced_lease.origin_epoch().unwrap(), (1, true));
        let fenced_scope = fenced_lease.scope_for(&tenant(&fenced));
        assert!(fenced_lease.lifecycle_state(&fenced_scope).unwrap().access_revoked());
        assert_eq!(
            fenced_lease.lifecycle_prepare_deletion(&fenced_scope, "delete-replay").unwrap(),
            fenced_lease.lifecycle_state(&fenced_scope).unwrap()
        );
        let prepared = fenced_lease.lifecycle_state(&fenced_scope).unwrap();
        let cutoff = u64::try_from(
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis(),
        )
        .unwrap()
        .max(prepared.deletion_preparation.as_ref().unwrap().minimum_cutoff_millis);
        let accepted = fenced_lease
            .lifecycle_accept_deletion(
                &fenced_scope,
                DeletionIntent {
                    operation_id: "delete-replay".into(),
                    usage_stopped_at: cutoff,
                    retain_until: Some(cutoff + 1),
                },
            )
            .unwrap();
        assert!(accepted.deletion.is_some());
        drop((fenced_lease, unrelated_lease));
        reopened.close(Duration::from_secs(1)).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    fn prepare_fenced_database(
        root: &std::path::Path,
        specs: &[DatabaseSpec; 2],
        fenced: &DatabaseScope,
        unrelated: &DatabaseScope,
        fenced_id: DatabaseId,
        unrelated_id: DatabaseId,
    ) {
        fs::create_dir_all(root).unwrap();
        let mut keys = crate::test_keys::storage_keys(fenced);
        keys.extend(crate::test_keys::storage_keys(unrelated));
        let member =
            Member::open_production(root, specs.clone(), AdmissionLimits::new(4, 2), keys).unwrap();
        let writer = Writer(1);
        member.accept_database_role(&fenced_id, &writer).unwrap();
        member.accept_database_role(&unrelated_id, &writer).unwrap();
        let fenced_lease = member.acquire(&fenced_id).unwrap();
        fenced_lease
            .configure_usage_placement(
                terrabase::usage::UsagePlacement::try_new(
                    tenant(fenced),
                    &fenced.project().to_text(),
                    &fenced.database().to_text(),
                    "partition",
                    RegionCode::GRU,
                    vec![RegionCode::GRU],
                    "revision",
                )
                .unwrap(),
            )
            .unwrap();
        fenced_lease.activate_origin_epoch(1).unwrap();
        fenced_lease
            .lifecycle_prepare_deletion(&fenced_lease.scope_for(&tenant(fenced)), "delete-replay")
            .unwrap();
        drop(fenced_lease);
        member.close(Duration::from_secs(1)).unwrap();
    }
}
