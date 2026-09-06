//! Production admission bound to accepted placement, encrypted Member storage, and public routing.
use crate::{
    admission::{AdmissionRecord, StorageAdmission},
    admitted_databases::AdmittedDatabaseRegistry,
};
use std::sync::Arc;
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
pub struct AcceptedAdmission {
    /// Canonical partition identity from accepted placement.
    pub partition: String,
    /// Immutable placement revision used by billing.
    pub revision: String,
    /// Accepted regional origin.
    pub origin: RegionCode,
    /// Strictly positive accepted writer epoch.
    pub epoch: u64,
    /// Whether this exact local member is the accepted writer.
    pub local_is_writer: bool,
}

/// Loads exact accepted authority; `None` means authenticated registration is pending.
pub type AdmissionAuthority =
    Arc<dyn Fn(&AdmissionRecord) -> Result<Option<AcceptedAdmission>, String> + Send + Sync>;

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
    Arc::new(move |record| {
        let region = record.region.parse::<RegionCode>().map_err(|_| "invalid origin")?;
        let tenant = TenantUlid::from_text(&record.tenant_id).map_err(|_| "invalid tenant")?;
        let project = ProjectUlid::from_text(&record.project_id).map_err(|_| "invalid project")?;
        let database =
            DatabaseUlid::from_text(&record.database_id).map_err(|_| "invalid database")?;
        let version = DatabaseKeyVersion::from_text(&record.key_version)
            .map_err(|_| "invalid key version")?;
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
        let keys = Arc::new(
            OciVaultDatabaseKeys::new(
                provider.clone(),
                KeyVersionMetadata::new(key_scope, version),
                region,
            )
            .map_err(|_| "key authorization unavailable")?,
        );
        let specification = DatabaseSpec::scoped(scope, &record.database_id);
        member
            .add_production_database(specification, per_database, keys)
            .map_err(|_| "database storage admission refused")?;
        // Registration precedes placement proposal. A pending scope is durable but
        // remains closed and absent from public routing until an authenticated retry.
        let Some(accepted) = authority(record)? else {
            return Ok(());
        };
        if accepted.epoch == 0 || accepted.origin != region {
            return Err("invalid accepted database authority".into());
        }
        if !accepted.local_is_writer {
            return Ok(());
        }
        let id = DatabaseId::try_new(&record.database_id).map_err(|_| "invalid database")?;
        let lease = member.acquire(&id).map_err(|_| "encrypted database unavailable")?;
        // Preparation and acceptance are durable origin fences.  Replay must
        // retain the encrypted entry for an authorized deletion retry, while
        // leaving origin writes fenced so it cannot make the whole Member fail
        // to start.
        let tenant_id = TenantId::try_new(&record.tenant_id).map_err(|_| "invalid tenant")?;
        if !lifecycle_allows_origin_activation(&lease, &tenant_id)? {
            registry.publish(
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
            &accepted.partition,
            region,
            record
                .regions
                .iter()
                .map(|region| region.parse::<RegionCode>())
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| "invalid selected regions")?,
            &accepted.revision,
        )
        .map_err(|_| "invalid usage placement")?;
        lease.configure_usage_placement(placement).map_err(|_| "usage placement refused")?;
        lease.activate_origin_epoch(accepted.epoch).map_err(|_| "writer epoch refused")?;
        registry.publish(record.clone(), lease, member.root().join(&record.database_id), instance)
    })
}

/// Reads the durable deletion fence before replay can activate a writer epoch.
/// A false result retains the lease for authorized maintenance without
/// reopening origin writes.
fn lifecycle_allows_origin_activation(
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
    use super::{AcceptedAdmission, lifecycle_allows_origin_activation, storage_admission};
    use crate::{
        admission::{AdmissionHost, AdmissionRecord},
        admitted_databases::AdmittedDatabaseRegistry,
    };
    use std::{fs, sync::Arc, time::Duration};
    use terrabase::{
        engine::lifecycle_state::DeletionIntent,
        identity::{DatabaseUlid, ProjectUlid, TenantUlid},
        member::{
            AcceptedDatabaseRole, AdmissionLimits, DatabaseId, DatabaseRole, DatabaseRoleAuthority,
            DatabaseScope, DatabaseSpec, Member,
        },
        regions::RegionCode,
        types::TenantId,
    };

    fn record(scope: &DatabaseScope, member: &str) -> AdmissionRecord {
        AdmissionRecord {
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
            DatabaseSpec::scoped(fenced.clone(), &fenced.database().to_text()),
            DatabaseSpec::scoped(unrelated.clone(), &unrelated.database().to_text()),
        ];
        fs::create_dir_all(&root).unwrap();
        let fenced_id = DatabaseId::from(fenced.database());
        let unrelated_id = DatabaseId::from(unrelated.database());
        let fixture = crate::test_keys::key_delivery();
        let open = || {
            let mut keys = crate::test_keys::storage_keys_with_provider(
                &fenced,
                fixture.provider.clone(),
            );
            keys.extend(crate::test_keys::storage_keys_with_provider(
                &unrelated,
                fixture.provider.clone(),
            ));
            Member::open_production(&root, specs.clone(), AdmissionLimits::new(4, 2), keys).unwrap()
        };
        let authority: Arc<
            dyn Fn(&AdmissionRecord) -> Result<Option<AcceptedAdmission>, String> + Send + Sync,
        > = Arc::new(|record| {
            Ok(Some(AcceptedAdmission {
                partition: format!("partition-{}", record.database_id),
                revision: "revision".into(),
                origin: RegionCode::GRU,
                epoch: 1,
                local_is_writer: true,
            }))
        });
        let member = open();
        member.accept_database_role(&fenced_id, &Writer).unwrap();
        member.accept_database_role(&unrelated_id, &Writer).unwrap();
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
        assert!(member.acquire(&fenced_id).is_ok());
        host.admit(record(&fenced, &member_id.to_text())).unwrap();
        host.admit(record(&unrelated, &member_id.to_text())).unwrap();
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
        }
        assert_eq!(fenced_host.host.engine().origin_epoch().unwrap().0, 1);
        drop(fenced_host);
        drop(host);
        drop(registry);
        member.close(Duration::from_secs(1)).unwrap();

        let reopened = open();
        reopened.accept_database_role(&fenced_id, &Writer).unwrap();
        reopened.accept_database_role(&unrelated_id, &Writer).unwrap();
        let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(|| true)));
        let callback = storage_admission(
            reopened.clone(),
            registry.clone(),
            fixture.provider.clone(),
            authority,
            4,
            2,
        );
        let host =
            AdmissionHost::open(&root, "production".into(), RegionCode::GRU, member_id, callback)
                .unwrap();
        let fenced_host = registry.resolve(&fenced.database().to_text()).unwrap().unwrap();
        let scope = fenced_host.host.engine().scope_for(&fenced_tenant);
        let state = fenced_host.host.engine().lifecycle_state(&scope).unwrap();
        assert!(state.access_revoked());
        assert_eq!(fenced_host.host.engine().origin_epoch().unwrap().0, 1);
        let unrelated_host = registry.resolve(&unrelated.database().to_text()).unwrap().unwrap();
        assert_eq!(unrelated_host.host.engine().origin_epoch().unwrap().0, 1);
        assert!(unrelated_host.host.engine().activate_origin_epoch(1).is_ok());
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
                    terrabase::engine::lifecycle_state::DeletionIntent {
                        operation_id: "delete-replay".into(),
                        usage_stopped_at: cutoff,
                        retain_until: state.deletion.as_ref().unwrap().retain_until,
                    },
                )
                .unwrap();
            assert!(retried.deletion.is_some());
        }
        drop(fenced_host);
        drop(unrelated_host);
        drop(host);
        drop(registry);
        drop(fixture);
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

    struct Writer;
    impl DatabaseRoleAuthority for Writer {
        fn accepted_role(&self, _: &DatabaseId) -> Option<AcceptedDatabaseRole> {
            Some(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer })
        }
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
        fs::create_dir_all(&root).unwrap();
        let specs = [
            DatabaseSpec::scoped(fenced.clone(), &fenced.database().to_text()),
            DatabaseSpec::scoped(unrelated.clone(), &unrelated.database().to_text()),
        ];
        let mut keys = crate::test_keys::storage_keys(&fenced);
        keys.extend(crate::test_keys::storage_keys(&unrelated));
        let member =
            Member::open_production(&root, specs.clone(), AdmissionLimits::new(4, 2), keys)
                .unwrap();
        let writer = Writer;
        let fenced_id = DatabaseId::from(fenced.database());
        let unrelated_id = DatabaseId::from(unrelated.database());
        member.accept_database_role(&fenced_id, &writer).unwrap();
        member.accept_database_role(&unrelated_id, &writer).unwrap();
        let fenced_lease = member.acquire(&fenced_id).unwrap();
        fenced_lease
            .lifecycle_prepare_deletion(
                &fenced_lease.scope_for(&TenantId::try_new(&fenced.tenant().to_text()).unwrap()),
                "delete-replay",
            )
            .unwrap();
        drop(fenced_lease);
        member.close(Duration::from_secs(1)).unwrap();

        let mut keys = crate::test_keys::storage_keys(&fenced);
        keys.extend(crate::test_keys::storage_keys(&unrelated));
        let reopened =
            Member::open_production(&root, specs, AdmissionLimits::new(4, 2), keys).unwrap();
        reopened.accept_database_role(&fenced_id, &writer).unwrap();
        reopened.accept_database_role(&unrelated_id, &writer).unwrap();
        let fenced_lease = reopened.acquire(&fenced_id).unwrap();
        let unrelated_lease = reopened.acquire(&unrelated_id).unwrap();
        assert!(
            !lifecycle_allows_origin_activation(
                &fenced_lease,
                &TenantId::try_new(&fenced.tenant().to_text()).unwrap(),
            )
            .unwrap()
        );
        assert!(
            lifecycle_allows_origin_activation(
                &unrelated_lease,
                &TenantId::try_new(&unrelated.tenant().to_text()).unwrap(),
            )
            .unwrap()
        );
        assert!(unrelated_lease.activate_origin_epoch(1).is_ok());
        assert_eq!(fenced_lease.origin_epoch().unwrap().0, 0);
        let fenced_scope =
            fenced_lease.scope_for(&TenantId::try_new(&fenced.tenant().to_text()).unwrap());
        assert!(fenced_lease.lifecycle_state(&fenced_scope).unwrap().access_revoked());
        assert_eq!(
            fenced_lease.lifecycle_prepare_deletion(&fenced_scope, "delete-replay").unwrap(),
            fenced_lease.lifecycle_state(&fenced_scope).unwrap()
        );
        let prepared = fenced_lease.lifecycle_state(&fenced_scope).unwrap();
        let cutoff = prepared.deletion_preparation.as_ref().unwrap().minimum_cutoff_millis;
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
}
