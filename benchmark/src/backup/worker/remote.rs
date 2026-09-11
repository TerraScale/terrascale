//! Remote metadata discovery and bounded retained-point migration.
use super::{
    BackupKeys, FleetError, FleetLimits, ObjectDestination, Provider, Stream, inventory_path,
    inventory_point_limit, inventory_points, staged_bytes,
};
use std::fs::File;
use std::io::{self, Read};
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
            || manifest.capture_kind() != super::super::manifest::BackupCaptureKind::WholeDatabase
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
            let source = match std::fs::symlink_metadata(&local) {
                Ok(metadata) if metadata.file_type().is_symlink() => {
                    return Err(FleetError::Capture);
                }
                Ok(metadata) if metadata.is_dir() => local,
                Ok(_) | Err(_) => {
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
                }
            };
            let sealed_manifest = inventory_path(stream)
                .join(&name)
                .join(super::super::manifest::ENCRYPTED_MANIFEST_FILE);
            // Authenticate the retained metadata and all bytes before copying
            // them. This is deliberately before the first upload call.
            validate_migration_source(&source, &sealed_manifest, &manifest)?;
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

fn validate_migration_source(
    source: &std::path::Path,
    sealed_manifest: &std::path::Path,
    manifest: &super::super::BackupManifest,
) -> Result<(), FleetError> {
    // The source is either a local retained point or a freshly downloaded
    // point. In both cases, refuse a symlink before any member pathname is
    // resolved.
    validate_migration_point(source)?;
    for member in manifest.members() {
        let path = source.join(member.name());
        let (length, digest) = digest_regular_file(&path).map_err(|_| FleetError::Capture)?;
        if length != member.length() || digest != member.digest() {
            return Err(FleetError::Capture);
        }
    }
    if read_regular_file(&source.join(super::super::manifest::ENCRYPTED_MANIFEST_FILE))
        .map_err(|_| FleetError::Capture)?
        != read_regular_file(sealed_manifest).map_err(|_| FleetError::Capture)?
    {
        return Err(FleetError::Capture);
    }
    Ok(())
}

fn validate_migration_point(source: &std::path::Path) -> Result<(), FleetError> {
    let metadata = std::fs::symlink_metadata(source).map_err(|_| FleetError::Capture)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(FleetError::Capture);
    }
    Ok(())
}

/// Reads a regular local artifact through a no-follow descriptor. Migration
/// must never hash bytes reached through a destination-controlled symlink.
fn digest_regular_file(path: &std::path::Path) -> io::Result<(u64, String)> {
    let file = open_regular_file(path)?;
    let length = file.metadata()?.len();
    let mut hasher = aws_lc_rs::digest::Context::new(&aws_lc_rs::digest::SHA256);
    let mut buffer = vec![0_u8; 64 * 1024];
    let mut read_length = 0_u64;
    let mut file = file;
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        read_length = read_length
            .checked_add(read as u64)
            .ok_or_else(|| io::Error::other("file length overflow"))?;
        hasher.update(&buffer[..read]);
    }
    if read_length != length {
        return Err(io::Error::other("file changed while reading"));
    }
    Ok((length, hex::encode(hasher.finish().as_ref())))
}

fn read_regular_file(path: &std::path::Path) -> io::Result<Vec<u8>> {
    let mut file = open_regular_file(path)?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    Ok(bytes)
}

fn open_regular_file(path: &std::path::Path) -> io::Result<File> {
    let descriptor = rustix::fs::open(
        path,
        rustix::fs::OFlags::RDONLY | rustix::fs::OFlags::NOFOLLOW | rustix::fs::OFlags::CLOEXEC,
        rustix::fs::Mode::empty(),
    )?;
    let file = File::from(descriptor);
    if !file.metadata()?.is_file() {
        return Err(io::Error::other("not a regular file"));
    }
    Ok(file)
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;

    fn test_path(name: &str) -> std::path::PathBuf {
        std::env::temp_dir()
            .join(format!("terrabase-remote-{name}-{}", crate::identity::generate()))
    }

    #[test]
    fn migration_rejects_symlinked_point_before_upload() {
        let root = test_path("point-symlink");
        let outside = test_path("point-symlink-outside");
        std::fs::create_dir_all(&outside).unwrap();
        std::os::unix::fs::symlink(&outside, &root).unwrap();

        assert_eq!(validate_migration_point(&root), Err(FleetError::Capture));

        std::fs::remove_file(&root).unwrap();
        std::fs::remove_dir(&outside).unwrap();
    }

    #[test]
    fn migration_rejects_symlinked_member_before_upload() {
        let root = test_path("member-symlink");
        let outside = test_path("member-symlink-outside");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(&outside, b"attacker bytes").unwrap();
        std::os::unix::fs::symlink(&outside, root.join("member")).expect("member symlink creates");

        assert!(digest_regular_file(&root.join("member")).is_err());

        std::fs::remove_dir_all(&root).unwrap();
        std::fs::remove_file(&outside).unwrap();
    }
}
