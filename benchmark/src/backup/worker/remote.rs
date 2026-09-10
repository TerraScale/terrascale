//! Remote metadata discovery and bounded retained-point migration.
use super::{
    BackupKeys, FleetError, FleetLimits, ObjectDestination, Provider, Stream, inventory_path,
    inventory_point_limit, inventory_points, staged_bytes,
};
use std::sync::Arc;

pub(super) async fn remote_points(
    database: &str,
    stream: &Stream,
    destination: &ObjectDestination,
) -> Result<Vec<super::super::BackupManifest>, FleetError> {
    remote_points_with_keys(
        database,
        stream.config.keys.clone(),
        inventory_point_limit(stream),
        destination,
    )
    .await
}

pub(super) async fn remote_points_with_keys(
    database: &str,
    keys: Arc<dyn BackupKeys>,
    point_limit: usize,
    destination: &ObjectDestination,
) -> Result<Vec<super::super::BackupManifest>, FleetError> {
    let prefixes =
        destination.list_points(database, point_limit).await.map_err(|_| FleetError::Upload)?;
    let mut points = Vec::with_capacity(prefixes.len());
    for prefix in prefixes {
        let envelope = destination
            .get_member(&format!("{prefix}/{}", super::super::manifest::ENCRYPTED_MANIFEST_FILE))
            .await
            .map_err(|_| FleetError::Upload)?;
        let bytes = keys.open_backup_envelope(&envelope).map_err(|_| FleetError::Capture)?;
        let manifest =
            super::super::BackupManifest::decode(&bytes).map_err(|_| FleetError::Capture)?;
        if prefix != format!("{database}/rp-{}", manifest.identity())
            || manifest.database_id() != database
        {
            return Err(FleetError::Capture);
        }
        points.push(manifest);
    }
    Ok(points)
}

pub(super) async fn migrate_points(
    database: &str,
    stream: &Stream,
    limits: FleetLimits,
    destinations: &[(Provider, Arc<ObjectDestination>)],
) -> Result<(), FleetError> {
    let points = inventory_points(stream)?;
    if points.is_empty() {
        return Err(FleetError::Capture);
    }
    // A policy transition may retain the only readable copy on a retired
    // destination. Probe the admitted active and historical set, once each,
    // before declaring migration unavailable.
    let mut source_destinations = Vec::new();
    let mut seen = std::collections::BTreeSet::new();
    for (_, destination) in
        stream.config.destinations.iter().chain(stream.config.historical_destinations.iter())
    {
        if seen.insert(destination.identity().to_owned()) {
            source_destinations.push(Arc::clone(destination));
        }
    }
    for manifest in points {
        let name = format!("rp-{}", manifest.identity());
        let prefix = format!("{database}/{name}");
        let local = stream.config.staging.join(&name);
        let temporary =
            stream.config.staging.join(format!("migration-{}", crate::identity::generate()));
        let result = async {
            let source = if local.is_dir() {
                local
            } else {
                std::fs::create_dir(&temporary).map_err(|_| FleetError::Capture)?;
                let target = temporary.join(&name);
                let remaining = limits
                    .staging_bytes_per_stream
                    .checked_sub(staged_bytes(&stream.config.staging)?)
                    .ok_or(FleetError::StagingFull)?;
                let mut downloaded = false;
                for destination in &source_destinations {
                    if destination
                        .download_point_with_byte_limit(&prefix, &target, remaining)
                        .await
                        .is_ok()
                    {
                        downloaded = true;
                        break;
                    }
                }
                if !downloaded {
                    return Err(FleetError::Upload);
                }
                target
            };
            let sealed_manifest = inventory_path(stream)
                .join(&name)
                .join(super::super::manifest::ENCRYPTED_MANIFEST_FILE);
            // Authenticate the retained metadata and all bytes before copying them.
            for member in manifest.members() {
                let path = source.join(member.name());
                if std::fs::metadata(&path).map_err(|_| FleetError::Capture)?.len()
                    != member.length()
                    || super::super::sha256::digest_file(&path).map_err(|_| FleetError::Capture)?
                        != member.digest()
                {
                    return Err(FleetError::Capture);
                }
            }
            if std::fs::read(source.join(super::super::manifest::ENCRYPTED_MANIFEST_FILE))
                .map_err(|_| FleetError::Capture)?
                != std::fs::read(&sealed_manifest).map_err(|_| FleetError::Capture)?
            {
                return Err(FleetError::Capture);
            }
            for (_, destination) in destinations {
                destination.upload_point(&source, &prefix).await.map_err(|_| FleetError::Upload)?;
                destination
                    .verify_manifest(&prefix, &sealed_manifest, &manifest)
                    .await
                    .map_err(|_| FleetError::Upload)?;
            }
            Ok(())
        }
        .await;
        let _ = std::fs::remove_dir_all(temporary);
        result?;
    }
    Ok(())
}
