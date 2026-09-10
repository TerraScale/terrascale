//! Production composition for the native backup owner.
//!
//! This boundary turns the already authenticated, serving database inputs into
//! one bounded fleet. Provider clients and key leases are supplied by the
//! caller; this module never invents credentials, destinations, or engines.

use crate::config::{ProductionBackupDestination, ProductionByokConfig};
use crate::native_backup_runtime::{
    NativeBackupInput, NativeBackupRuntime, NativeBackupRuntimeError,
};
use std::fs::{self, File};
use std::io::Read;
use std::path::Path;
use std::sync::Arc;
use terrabase::backup::object_store::{
    Credentials, DestinationConfig, ObjectDestination, WorkloadIdentityProvider,
};
use terrabase::backup::worker::FleetLimits;
use terrabase::regions::RegionCode;

const MAX_CREDENTIAL_BYTES: u64 = 4096;

/// Redacted failures while resolving operator-provided backup credentials.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionBackupFactoryError {
    /// A credential reference was missing, unsafe, or unreadable.
    Credential,
    /// The object destination failed closed during admission.
    Destination,
}

/// Resolves one configured destination without placing secret bytes in the
/// typed configuration, journals, or diagnostics.
pub fn destination_from_config(
    config: &ProductionBackupDestination,
    database_region: RegionCode,
) -> Result<Arc<ObjectDestination>, ProductionBackupFactoryError> {
    let access = read_secret_reference(&config.access_key_file)?;
    let secret = read_secret_reference(&config.secret_key_file)?;
    let destination = ObjectDestination::new(DestinationConfig {
        admitted_region: database_region,
        provider: config.provider,
        endpoint: config.endpoint.clone(),
        signing_region: config.signing_region.clone(),
        database_region: match config.provider {
            terrabase::backup::object_store::Provider::Oci => config.signing_region.clone(),
            terrabase::backup::object_store::Provider::Tigris => {
                database_region.as_str().to_owned()
            }
            terrabase::backup::object_store::Provider::CustomerS3 => config.signing_region.clone(),
        },
        bucket: config.bucket.clone(),
        verified_geography: config.verified_geography.clone(),
        permitted_geographies: config.permitted_geographies.clone(),
        placement_revision: config.placement_revision.clone(),
        credentials: Credentials::new(access, secret),
        max_artifact_bytes: config.max_artifact_bytes,
        max_point_bytes: config.max_point_bytes,
    })
    .map_err(|_| ProductionBackupFactoryError::Destination)?;
    Ok(Arc::new(destination))
}

/// Resolves one destination through the caller-supplied workload identity.
///
/// This is the production path for customer-owned S3. The provider is called
/// lazily by the object store for each request, allowing expiry and revocation
/// to take effect without rebuilding the destination.
pub fn destination_from_config_with_workload_identity(
    config: &ProductionBackupDestination,
    database_region: RegionCode,
    provider: Arc<dyn WorkloadIdentityProvider>,
) -> Result<Arc<ObjectDestination>, ProductionBackupFactoryError> {
    let destination = ObjectDestination::new_with_workload_identity(
        &DestinationConfig {
            admitted_region: database_region,
            provider: config.provider,
            endpoint: config.endpoint.clone(),
            signing_region: config.signing_region.clone(),
            database_region: match config.provider {
                terrabase::backup::object_store::Provider::Oci => config.signing_region.clone(),
                terrabase::backup::object_store::Provider::Tigris => {
                    database_region.as_str().to_owned()
                }
                terrabase::backup::object_store::Provider::CustomerS3 => {
                    config.signing_region.clone()
                }
            },
            bucket: config.bucket.clone(),
            verified_geography: config.verified_geography.clone(),
            permitted_geographies: config.permitted_geographies.clone(),
            placement_revision: config.placement_revision.clone(),
            credentials: Credentials::new(String::new(), String::new()),
            max_artifact_bytes: config.max_artifact_bytes,
            max_point_bytes: config.max_point_bytes,
        },
        provider,
    )
    .map_err(|_| ProductionBackupFactoryError::Destination)?;
    Ok(Arc::new(destination))
}

/// Resolves all destinations through one explicit workload identity authority.
pub fn destinations_from_config_with_workload_identity(
    configs: &[ProductionBackupDestination],
    database_region: RegionCode,
    provider: Arc<dyn WorkloadIdentityProvider>,
) -> Result<
    Vec<(terrabase::backup::object_store::Provider, Arc<ObjectDestination>)>,
    ProductionBackupFactoryError,
> {
    configs
        .iter()
        .map(|config| {
            Ok((
                config.provider,
                destination_from_config_with_workload_identity(
                    config,
                    database_region,
                    Arc::clone(&provider),
                )?,
            ))
        })
        .collect()
}

/// Resolves the single customer-owned destination and binds every credential
/// request to its durable role, endpoint, bucket, region, and placement.
pub fn customer_destination_from_config(
    byok: &ProductionByokConfig,
    policy: &ProductionBackupDestination,
    database_region: RegionCode,
    provider: Arc<dyn WorkloadIdentityProvider>,
) -> Result<
    (terrabase::backup::object_store::Provider, Arc<ObjectDestination>),
    ProductionBackupFactoryError,
> {
    let config = ProductionBackupDestination {
        provider: terrabase::backup::object_store::Provider::CustomerS3,
        endpoint: byok.backup_endpoint.clone(),
        signing_region: byok.backup_region.clone(),
        bucket: byok.backup_bucket.clone(),
        verified_geography: policy.verified_geography.clone(),
        permitted_geographies: policy.permitted_geographies.clone(),
        placement_revision: policy.placement_revision.clone(),
        access_key_file: std::path::PathBuf::new(),
        secret_key_file: std::path::PathBuf::new(),
        max_artifact_bytes: policy.max_artifact_bytes,
        max_point_bytes: policy.max_point_bytes,
    };
    Ok((
        config.provider,
        destination_from_config_with_workload_identity(&config, database_region, provider)?,
    ))
}

/// Resolves every selected destination in configuration order. A failure
/// discards all already-created clients, so callers cannot publish a partial
/// provider policy.
pub fn destinations_from_config(
    configs: &[ProductionBackupDestination],
    database_region: RegionCode,
) -> Result<
    Vec<(terrabase::backup::object_store::Provider, Arc<ObjectDestination>)>,
    ProductionBackupFactoryError,
> {
    let mut destinations = Vec::with_capacity(configs.len());
    for config in configs {
        destinations.push((config.provider, destination_from_config(config, database_region)?));
    }
    Ok(destinations)
}

fn read_secret_reference(path: &Path) -> Result<String, ProductionBackupFactoryError> {
    let metadata =
        fs::symlink_metadata(path).map_err(|_| ProductionBackupFactoryError::Credential)?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_CREDENTIAL_BYTES {
        return Err(ProductionBackupFactoryError::Credential);
    }
    let mut file = File::open(path).map_err(|_| ProductionBackupFactoryError::Credential)?;
    let mut bytes = Vec::new();
    file.take(MAX_CREDENTIAL_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| ProductionBackupFactoryError::Credential)?;
    if bytes.len() as u64 > MAX_CREDENTIAL_BYTES {
        return Err(ProductionBackupFactoryError::Credential);
    }
    let value = String::from_utf8(bytes).map_err(|_| ProductionBackupFactoryError::Credential)?;
    let value = value.trim();
    if value.is_empty() || value.chars().any(char::is_control) {
        return Err(ProductionBackupFactoryError::Credential);
    }
    Ok(value.to_owned())
}

/// Builds the one native backup fleet for a production Member.
///
/// Inputs are ordered by canonical database identity before admission. The
/// runtime constructor is all-or-nothing, so no partially configured owner is
/// returned when a stream, destination, or backup key is refused.
pub fn compose_native_backup(
    limits: FleetLimits,
    mut inputs: Vec<NativeBackupInput>,
) -> Result<NativeBackupRuntime, NativeBackupRuntimeError> {
    inputs.sort_by_key(|input| input.engine.engine().database_id());
    NativeBackupRuntime::new(limits, inputs)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn invalid_limits_fail_before_an_owner_is_published() {
        let result = compose_native_backup(
            FleetLimits {
                streams: 0,
                staging_bytes_per_stream: 0,
                inventory_bytes_per_stream: 0,
                points_per_stream: 0,
            },
            Vec::new(),
        );
        assert!(matches!(
            result,
            Err(NativeBackupRuntimeError::Fleet(
                terrabase::backup::worker::FleetError::Configuration
            ))
        ));
    }

    #[test]
    fn credential_references_are_trimmed_and_never_debuggable_as_plaintext() {
        let path = std::env::temp_dir()
            .join(format!("terrabase-backup-credential-{}", std::process::id()));
        fs::write(&path, b" access-key\n").expect("credential fixture");
        assert_eq!(read_secret_reference(&path).expect("credential"), "access-key");
        let error = ProductionBackupFactoryError::Credential;
        assert_eq!(format!("{error:?}"), "Credential");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn missing_credential_reference_fails_closed() {
        let path =
            std::env::temp_dir().join(format!("terrabase-backup-missing-{}", std::process::id()));
        assert_eq!(read_secret_reference(&path), Err(ProductionBackupFactoryError::Credential));
    }
}
