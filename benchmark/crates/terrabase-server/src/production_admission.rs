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
        let placement = UsagePlacement::try_new(
            TenantId::try_new(&record.tenant_id).map_err(|_| "invalid tenant")?,
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
