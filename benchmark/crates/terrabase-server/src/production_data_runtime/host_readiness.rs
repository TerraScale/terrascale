//! Readiness checks for encrypted locally hosted writer databases.

use std::collections::BTreeMap;

use terrabase::types::{Cancellation, TenantId};
use terrabase_cluster::production::DatabaseRouteScope;

use crate::{admitted_databases::AdmittedDatabaseSnapshot, production_readiness::ExpectedScope};

/// Readiness facts for the locally hosted writer databases.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct WriterHostReadiness {
    /// Every expected local writer has an exact activated retained host.
    pub admitted: bool,
    /// Every matched writer has an enforced owner and valid storage authorization.
    pub encrypted: bool,
    /// Every matched writer has a readable bounded sync journal.
    pub journal_available: bool,
}

pub(super) fn evaluate_writer_hosts(
    expected: &BTreeMap<DatabaseRouteScope, ExpectedScope>,
    snapshots: &[AdmittedDatabaseSnapshot],
) -> WriterHostReadiness {
    if expected.iter().any(|(key, value)| *key != value.scope) {
        return WriterHostReadiness::default();
    }
    let mut result =
        WriterHostReadiness { admitted: true, encrypted: true, journal_available: true };
    let writers = expected.values().filter(|scope| scope.local_is_writer).collect::<Vec<_>>();
    for snapshot in snapshots.iter().filter(|snapshot| snapshot.activated) {
        let Some(snapshot_scope) = snapshot_scope(snapshot) else {
            result.admitted = false;
            continue;
        };
        let Some(scope) = expected.get(&snapshot_scope) else {
            result.admitted = false;
            continue;
        };
        if !scope.local_is_writer || snapshot.database.record.region != scope.local_region {
            result.admitted = false;
        }
    }
    for expected in writers {
        let Some(snapshot) = snapshots.iter().find(|snapshot| {
            snapshot.activated
                && snapshot.database.record.tenant_id == expected.scope.tenant().to_text()
                && snapshot.database.record.project_id == expected.scope.project().to_text()
                && snapshot.database.record.database_id == expected.scope.database().to_text()
                && snapshot.database.record.region == expected.local_region
        }) else {
            result.admitted = false;
            result.encrypted = false;
            result.journal_available = false;
            continue;
        };
        let engine = snapshot.database.host.engine();
        let owner = engine.database_owner();
        if !engine.owner_scope_enforced()
            || owner.environment().as_str() != snapshot.database.record.environment
            || owner.tenant().to_text() != expected.scope.tenant().to_text()
            || owner.project().to_text() != expected.scope.project().to_text()
            || owner.database().to_text() != expected.scope.database().to_text()
            || owner.region().as_str() != expected.local_region
        {
            result.admitted = false;
            result.encrypted = false;
            result.journal_available = false;
            continue;
        }
        if engine.validate_storage_authorization().is_err() {
            result.encrypted = false;
            result.journal_available = false;
            continue;
        }
        let Ok(tenant) = TenantId::try_new(&snapshot.database.record.tenant_id) else {
            result.journal_available = false;
            continue;
        };
        if engine
            .streams()
            .replay_sync_journal(&engine.scope_for(&tenant), None, 1, &Cancellation::default())
            .is_err()
        {
            result.journal_available = false;
        }
    }
    result
}

/// Validates encryption for every activated retained host, including followers.
pub(super) fn all_activated_hosts_encrypted(snapshots: &[AdmittedDatabaseSnapshot]) -> bool {
    snapshots.iter().filter(|snapshot| snapshot.activated).all(|snapshot| {
        let engine = snapshot.database.host.engine();
        let owner = engine.database_owner();
        engine.validate_storage_authorization().is_ok()
            && owner.environment().as_str() == snapshot.database.record.environment
            && owner.tenant().to_text() == snapshot.database.record.tenant_id
            && owner.project().to_text() == snapshot.database.record.project_id
            && owner.database().to_text() == snapshot.database.record.database_id
            && owner.region().as_str() == snapshot.database.record.region
    })
}

fn snapshot_scope(snapshot: &AdmittedDatabaseSnapshot) -> Option<DatabaseRouteScope> {
    DatabaseRouteScope::try_new(
        &snapshot.database.record.tenant_id,
        &snapshot.database.record.project_id,
        &snapshot.database.record.database_id,
    )
    .ok()
}

#[cfg(test)]
pub(super) mod tests {
    use super::*;
    use std::sync::Arc;
    use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};

    fn scope() -> DatabaseRouteScope {
        DatabaseRouteScope::new(
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
        )
    }

    struct Writer;
    impl terrabase::member::DatabaseRoleAuthority for Writer {
        fn accepted_role(
            &self,
            _: &terrabase::member::DatabaseId,
        ) -> Option<terrabase::member::AcceptedDatabaseRole> {
            Some(terrabase::member::AcceptedDatabaseRole {
                epoch: 1,
                role: terrabase::member::DatabaseRole::Writer,
            })
        }
    }

    pub(in crate::production_data_runtime) fn writer_fixture()
    -> (AdmittedDatabaseSnapshot, DatabaseRouteScope, terrabase::member::Member, std::path::PathBuf)
    {
        let root =
            std::env::temp_dir().join(format!("host-readiness-{}", DatabaseUlid::generate()));
        std::fs::create_dir_all(&root).unwrap();
        let scope = scope();
        let database =
            terrabase::identity::DatabaseUlid::from_text(&scope.database().to_text()).unwrap();
        let database_scope = terrabase::member::DatabaseScope::new(
            "production",
            scope.tenant(),
            scope.project(),
            database,
            terrabase::regions::RegionCode::GRU,
        )
        .unwrap();
        let spec = terrabase::member::DatabaseSpec::scoped(database_scope.clone(), "database");
        let member = terrabase::member::Member::open_production(
            &root,
            [spec],
            terrabase::member::AdmissionLimits::new(2, 2),
            crate::test_keys::storage_keys(&database_scope),
        )
        .unwrap();
        let id = terrabase::member::DatabaseId::try_new(scope.database().to_text()).unwrap();
        member.accept_database_role(&id, &Writer).unwrap();
        let lease = member.acquire(&id).unwrap();
        let record = crate::admission::AdmissionRecord {
            created_at: 1,
            environment: "production".into(),
            tenant_id: scope.tenant().to_text(),
            project_id: scope.project().to_text(),
            database_id: scope.database().to_text(),
            parent_database_id: None,
            key_version: "01ARZ3NDEKTSV4RRFFQ69G5FB0".into(),
            name: "database".into(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: "host-readiness".into(),
            region: "GRU".into(),
            member_id: terrabase::identity::MemberUlid::generate().to_text(),
        };
        let host = Arc::new(terrabase_tbep::member_host::MemberHost::from_admitted(
            lease,
            root.join("database"),
            1,
        ));
        let database = Arc::new(crate::admitted_databases::AdmittedDatabase {
            record,
            host,
            activity: terrabase::engine::lifecycle_state::AuthorizedActivity::default(),
        });
        (AdmittedDatabaseSnapshot { database, activated: true }, scope, member, root)
    }

    #[test]
    fn empty_writer_inventory_is_vacuously_ready() {
        assert_eq!(
            evaluate_writer_hosts(&BTreeMap::new(), &[]),
            WriterHostReadiness { admitted: true, encrypted: true, journal_available: true }
        );
    }

    #[test]
    fn missing_expected_writer_fails_closed() {
        let scope = scope();
        let expected = ExpectedScope {
            scope,
            generation: 1,
            epoch: 1,
            local_region: "GRU".into(),
            local_is_writer: true,
            physical_peer: "peer".into(),
            remote_writers: BTreeMap::new(),
        };
        let facts = evaluate_writer_hosts(&BTreeMap::from([(scope, expected)]), &[]);
        assert_eq!(
            facts,
            WriterHostReadiness { admitted: false, encrypted: false, journal_available: false }
        );
    }

    #[test]
    fn expected_scope_map_key_mismatch_fails_closed() {
        let key = scope();
        let value_scope = scope();
        let facts =
            evaluate_writer_hosts(&BTreeMap::from([(key, expected(value_scope, true))]), &[]);
        assert_eq!(facts, WriterHostReadiness::default());
    }

    fn expected(scope: DatabaseRouteScope, writer: bool) -> ExpectedScope {
        ExpectedScope {
            scope,
            generation: 1,
            epoch: 1,
            local_region: "GRU".into(),
            local_is_writer: writer,
            physical_peer: "peer".into(),
            remote_writers: BTreeMap::new(),
        }
    }

    #[test]
    fn activated_writer_has_encrypted_journal_backed_readiness() {
        let (snapshot, scope, member, root) = writer_fixture();
        let facts = evaluate_writer_hosts(
            &BTreeMap::from([(scope, expected(scope, true))]),
            std::slice::from_ref(&snapshot),
        );
        assert_eq!(
            facts,
            WriterHostReadiness { admitted: true, encrypted: true, journal_available: true }
        );
        drop(snapshot);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn inactive_writer_is_missing_but_active_follower_is_rejected() {
        let (mut snapshot, scope, member, root) = writer_fixture();
        snapshot.activated = false;
        let facts = evaluate_writer_hosts(
            &BTreeMap::from([(scope, expected(scope, true))]),
            std::slice::from_ref(&snapshot),
        );
        assert!(!facts.admitted);
        drop(snapshot);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();

        let (snapshot, scope, member, root) = writer_fixture();
        let facts = evaluate_writer_hosts(
            &BTreeMap::from([(scope, expected(scope, false))]),
            std::slice::from_ref(&snapshot),
        );
        assert!(!facts.admitted);
        drop(snapshot);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn environment_mismatch_fails_owner_validation() {
        let (mut snapshot, scope, member, root) = writer_fixture();
        Arc::get_mut(&mut snapshot.database).unwrap().record.environment = "staging".into();
        let facts = evaluate_writer_hosts(
            &BTreeMap::from([(scope, expected(scope, true))]),
            std::slice::from_ref(&snapshot),
        );
        assert!(!facts.admitted && !facts.encrypted && !facts.journal_available);
        drop(snapshot);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}
