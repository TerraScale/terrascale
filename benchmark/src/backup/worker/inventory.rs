//! Authenticated retained metadata is separate from the bounded archive cache.
use super::{
    BackupReader, FleetError, FleetLimits, Provider, Stream, persist_upload_journal, point_paths,
    staged_bytes,
};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub(super) fn inventory_path(stream: &Stream) -> PathBuf {
    stream.config.staging.join(".inventory")
}

pub(super) fn inventory_point_limit(stream: &Stream) -> usize {
    scheduled_point_limit(stream.config.retention_days)
}

fn scheduled_point_limit(retention_days: u8) -> usize {
    // A rotation tick can seal both a pinned root and its covering incremental.
    // Include an extra root day and bounded explicit snapshots. This metadata
    // guard never authorizes pruning an unexpired point.
    (usize::from(retention_days) + 1) * 8_640 * 2 + 8_640
}

pub(super) fn inventory_points(
    stream: &Stream,
) -> Result<Vec<super::super::BackupManifest>, FleetError> {
    inventory_points_for(
        &stream.config.staging,
        stream.config.keys.as_ref(),
        stream.config.retention_days,
    )
}

pub(super) fn inventory_points_for(
    staging: &Path,
    keys: &dyn BackupReader,
    retention_days: u8,
) -> Result<Vec<super::super::BackupManifest>, FleetError> {
    let mut points = BTreeMap::new();
    for directory in [staging.join(".inventory"), staging.to_path_buf()] {
        if !directory.exists() {
            continue;
        }
        point_paths(&directory, scheduled_point_limit(retention_days))?;
        for point in super::super::restore::discover_encrypted_points(&directory, keys)
            .map_err(|_| FleetError::Capture)?
        {
            points.insert(point.identity().to_owned(), point);
            if points.len() > scheduled_point_limit(retention_days) {
                return Err(FleetError::StagingFull);
            }
        }
    }
    Ok(points.into_values().collect())
}

pub(super) fn persist_inventory(stream: &Stream, limits: FleetLimits) -> Result<(), FleetError> {
    let root = inventory_path(stream);
    std::fs::create_dir_all(&root).map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)?;
    cleanup_unsealed_points(&stream.config.staging)?;
    cleanup_interrupted(&stream.config.staging)?;
    cleanup_interrupted(&root)?;
    for point in super::super::restore::discover_encrypted_points(
        &stream.config.staging,
        stream.config.keys.as_ref(),
    )
    .map_err(|_| FleetError::Capture)?
    {
        let name = format!("rp-{}", point.identity());
        let destination = root.join(&name);
        let manifest = super::super::manifest::ENCRYPTED_MANIFEST_FILE;
        if destination.join(manifest).exists() {
            let stored = destination.join(manifest);
            let source = stream.config.staging.join(&name).join(manifest);
            for path in [&stored, &source] {
                let metadata = std::fs::symlink_metadata(path).map_err(|_| FleetError::Capture)?;
                if !metadata.file_type().is_file()
                    || metadata.len() > (super::super::manifest::MAX_MANIFEST_BYTES + 4096) as u64
                {
                    return Err(FleetError::Capture);
                }
            }
            if std::fs::read(stored).map_err(|_| FleetError::Capture)?
                != std::fs::read(source).map_err(|_| FleetError::Capture)?
            {
                return Err(FleetError::Capture);
            }
            continue;
        }
        let source = stream.config.staging.join(&name).join(manifest);
        let additional = std::fs::metadata(&source).map_err(|_| FleetError::Capture)?.len();
        if staged_bytes(&root)?.saturating_add(additional) > limits.inventory_bytes_per_stream {
            return Err(FleetError::StagingFull);
        }
        let temporary = root.join(format!(".pending-{}", point.identity()));
        std::fs::create_dir_all(&temporary).map_err(|_| FleetError::Capture)?;
        std::fs::copy(stream.config.staging.join(&name).join(manifest), temporary.join(manifest))
            .map_err(|_| FleetError::Capture)?;
        std::fs::File::open(temporary.join(manifest))
            .and_then(|file| file.sync_all())
            .map_err(|_| FleetError::Capture)?;
        super::super::sync_directory(&temporary).map_err(|_| FleetError::Capture)?;
        std::fs::rename(&temporary, &destination).map_err(|_| FleetError::Capture)?;
        super::super::sync_directory(&root).map_err(|_| FleetError::Capture)?;
    }
    Ok(())
}

/// Removes a valid-identity point directory left before its manifest commit.
///
/// A production retry cannot recover the capture clock from member files, so
/// retaining such a directory would leave an unrepairable orphan beside the
/// next capture. A directory with either manifest form is sealed (and is
/// therefore validated by discovery instead of being removed here).
fn cleanup_unsealed_points(directory: &Path) -> Result<(), FleetError> {
    for entry in std::fs::read_dir(directory).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        let filename = entry.file_name();
        let Some(identity) =
            filename.to_str().and_then(|name| name.strip_prefix("rp-")).map(str::to_owned)
        else {
            continue;
        };
        if super::super::manifest::validate_identity(&identity).is_err() {
            continue;
        }
        let path = entry.path();
        if !entry.file_type().map_err(|_| FleetError::Capture)?.is_dir() {
            return Err(FleetError::Capture);
        }
        if path.join(super::super::manifest::MANIFEST_FILE).is_file()
            || path.join(super::super::manifest::ENCRYPTED_MANIFEST_FILE).is_file()
        {
            continue;
        }
        remove_point(&path)?;
    }
    Ok(())
}

pub(super) fn evict_retired_local_chains(
    stream: &Stream,
    limits: FleetLimits,
) -> Result<(), FleetError> {
    if super::rotation::read(stream)?.is_some() {
        return Ok(());
    }
    let chain = super::super::verify_latest_chain_encrypted(
        &stream.config.staging,
        stream.config.keys.as_ref(),
    )
    .map_err(|_| FleetError::Capture)?;
    // A replacement root alone has not yet archived post-base transactions.
    // Keep the old local chain until the covering incremental is verified.
    if chain.last().is_some_and(|point| point.kind() == super::super::BackupKind::Snapshot) {
        return Ok(());
    }
    let retained = chain
        .iter()
        .map(|point| format!("rp-{}", point.identity()))
        .collect::<std::collections::BTreeSet<_>>();
    for point in point_paths(&stream.config.staging, limits.points_per_stream)? {
        let name = point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
        if retained.contains(name) {
            continue;
        }
        // Called only after every destination has verified all staged points.
        // Metadata is fsynced first; provider copies retain the promised window.
        if !inventory_path(stream).join(name).exists() {
            return Err(FleetError::Capture);
        }
        remove_point(&point)?;
    }
    // Journals track cache transfer work only; retained provider metadata lives
    // in the independent inventory and never consumes this bounded queue.
    for (provider, _) in &stream.config.destinations {
        let journal = stream.config.staging.join(match provider {
            Provider::Oci => "uploaded-oci",
            Provider::Tigris => "uploaded-tigris",
            Provider::CustomerS3 => "uploaded-customer-s3",
        });
        persist_upload_journal(&journal, &stream.config.policy_revision, &retained)?;
    }
    Ok(())
}

/// Hide a complete point atomically before unlinking its member files. Restart
/// discovery never observes a half-deleted rp directory.
pub(super) fn remove_point(point: &Path) -> Result<(), FleetError> {
    if !point.exists() {
        return Ok(());
    }
    let parent = point.parent().ok_or(FleetError::Capture)?;
    let name = point.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?;
    let tombstone = parent.join(format!(".evict-{name}"));
    std::fs::rename(point, &tombstone).map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(parent).map_err(|_| FleetError::Capture)?;
    std::fs::remove_dir_all(tombstone).map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(parent).map_err(|_| FleetError::Capture)
}

pub(super) fn cleanup_interrupted(directory: &Path) -> Result<(), FleetError> {
    for entry in std::fs::read_dir(directory).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
            continue;
        };
        if owned_ephemeral(&name) {
            let file_type = entry.file_type().map_err(|_| FleetError::Capture)?;
            if name == "rotation-pending.tmp" {
                if !file_type.is_file() {
                    return Err(FleetError::Capture);
                }
                std::fs::remove_file(entry.path()).map_err(|_| FleetError::Capture)?;
            } else {
                if !file_type.is_dir() {
                    return Err(FleetError::Capture);
                }
                std::fs::remove_dir_all(entry.path()).map_err(|_| FleetError::Capture)?;
            }
        }
    }
    super::super::sync_directory(directory).map_err(|_| FleetError::Capture)
}

pub(super) fn has_capacity(
    stream: &Stream,
    limits: FleetLimits,
    additional: usize,
) -> Result<bool, FleetError> {
    let count = point_paths(&inventory_path(stream), inventory_point_limit(stream))?.len();
    let bytes = staged_bytes(&inventory_path(stream))?;
    let reserve = (additional as u64)
        .saturating_mul((super::super::manifest::MAX_MANIFEST_BYTES + 4096) as u64);
    Ok(count.saturating_add(additional) <= inventory_point_limit(stream)
        && bytes.saturating_add(reserve) <= limits.inventory_bytes_per_stream)
}

fn owned_ephemeral(name: &str) -> bool {
    if name == "rotation-pending.tmp" {
        return true;
    }
    if let Some(identity) =
        name.strip_prefix(".env-").and_then(|name| name.strip_suffix(".staging"))
    {
        return super::super::manifest::validate_identity(identity).is_ok();
    }
    if let Some(identity) =
        name.strip_prefix(".evict-rp-").or_else(|| name.strip_prefix(".pending-"))
    {
        return super::super::manifest::validate_identity(identity).is_ok();
    }
    if let Some(identity) =
        name.strip_prefix("restore-").or_else(|| name.strip_prefix("migration-"))
    {
        return identity.parse::<::ulid::Ulid>().is_ok();
    }
    name.strip_prefix(".staging-").and_then(|parts| parts.split_once('-')).is_some_and(
        |(pid, suffix)| {
            if pid.parse::<u32>().is_err() {
                return false;
            }
            let mut fields = suffix.split('-');
            let Some(index) = fields.next() else {
                return false;
            };
            if index.parse::<u64>().is_err() {
                return false;
            }
            match (fields.next(), fields.next()) {
                // Older workers used PID plus the process-local counter.
                (None, None) => true,
                // Current workers add a random suffix so restart cannot
                // collide with a crash-left directory.
                (Some(ulid), None) => ulid.parse::<::ulid::Ulid>().is_ok(),
                _ => false,
            }
        },
    )
}

#[cfg(test)]
mod tests {
    use super::scheduled_point_limit;

    #[test]
    fn unsealed_recovery_point_orphan_is_removed_during_inventory_reconcile() {
        let directory = crate::backup::tests::location("backup-inventory-orphan");
        std::fs::remove_dir_all(&directory).ok();
        std::fs::create_dir_all(directory.join(format!("rp-{}", "a".repeat(64)))).unwrap();
        std::fs::write(directory.join(format!("rp-{}/member-000001", "a".repeat(64))), b"staged")
            .unwrap();

        super::cleanup_unsealed_points(&directory).unwrap();

        assert!(!directory.join(format!("rp-{}", "a".repeat(64))).exists());
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn interrupted_cleanup_refuses_symlinks_and_preserves_owned_journals() {
        let directory = crate::backup::tests::location("backup-cleanup-symlink");
        let outside = crate::backup::tests::location("backup-cleanup-outside");
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        std::fs::write(outside.join("sentinel"), b"retained").unwrap();
        for name in ["rotation-pending", "uploaded-oci", "policy-active", "delete-oci"] {
            std::fs::write(directory.join(name), b"retained").unwrap();
        }
        let point = directory.join(format!("rp-{}", "a".repeat(64)));
        std::fs::create_dir(&point).unwrap();
        std::os::unix::fs::symlink(&outside, directory.join(".staging-1-1")).unwrap();
        assert_eq!(super::cleanup_interrupted(&directory), Err(super::FleetError::Capture));
        assert!(outside.join("sentinel").exists());
        assert!(point.exists());
        for name in ["rotation-pending", "uploaded-oci", "policy-active", "delete-oci"] {
            assert_eq!(std::fs::read(directory.join(name)).unwrap(), b"retained");
        }
        std::fs::remove_dir_all(directory).unwrap();
        std::fs::remove_dir_all(outside).unwrap();
    }

    #[test]
    fn interrupted_cleanup_reclaims_restart_left_staging_names() {
        let directory = crate::backup::tests::location("backup-cleanup-restart-staging");
        std::fs::create_dir_all(&directory).unwrap();
        let stale = directory.join(format!(
            ".staging-{}-0-{}",
            std::process::id(),
            crate::identity::generate()
        ));
        std::fs::create_dir(&stale).unwrap();
        std::fs::write(stale.join("partial-member"), b"crash leftover").unwrap();

        super::cleanup_interrupted(&directory).unwrap();

        assert!(!stale.exists());
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn interrupted_cleanup_reclaims_envelope_and_rotation_temporaries() {
        let directory = crate::backup::tests::location("backup-cleanup-temporaries");
        std::fs::remove_dir_all(&directory).ok();
        std::fs::create_dir_all(&directory).unwrap();
        let envelope = directory.join(format!(".env-{}.staging", "a".repeat(64)));
        std::fs::create_dir(&envelope).unwrap();
        std::fs::write(directory.join("rotation-pending.tmp"), b"0 1\n").unwrap();

        super::cleanup_interrupted(&directory).unwrap();

        assert!(!envelope.exists());
        assert!(!directory.join("rotation-pending.tmp").exists());
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn interrupted_cleanup_refuses_temporary_type_mismatches() {
        let directory = crate::backup::tests::location("backup-cleanup-temporary-types");
        std::fs::remove_dir_all(&directory).ok();
        std::fs::create_dir_all(&directory).unwrap();
        let rotation = directory.join("rotation-pending.tmp");
        std::fs::create_dir(&rotation).unwrap();
        assert_eq!(super::cleanup_interrupted(&directory), Err(super::FleetError::Capture));
        assert!(rotation.is_dir());
        std::fs::remove_dir(&rotation).unwrap();

        let envelope = directory.join(format!(".env-{}.staging", "b".repeat(64)));
        std::fs::write(&envelope, b"wrong type").unwrap();
        assert_eq!(super::cleanup_interrupted(&directory), Err(super::FleetError::Capture));
        assert!(envelope.is_file());
        std::fs::remove_file(envelope).unwrap();
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn interrupted_cleanup_refuses_temporary_symlinks() {
        let directory = crate::backup::tests::location("backup-cleanup-temporary-symlinks");
        let outside = crate::backup::tests::location("backup-cleanup-temporary-symlinks-outside");
        std::fs::remove_dir_all(&directory).ok();
        std::fs::remove_dir_all(&outside).ok();
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        std::fs::write(outside.join("sentinel"), b"retained").unwrap();
        std::os::unix::fs::symlink(&outside, directory.join("rotation-pending.tmp")).unwrap();
        assert_eq!(super::cleanup_interrupted(&directory), Err(super::FleetError::Capture));
        assert!(outside.join("sentinel").exists());
        std::fs::remove_file(directory.join("rotation-pending.tmp")).unwrap();

        std::os::unix::fs::symlink(
            &outside,
            directory.join(format!(".env-{}.staging", "c".repeat(64))),
        )
        .unwrap();
        assert_eq!(super::cleanup_interrupted(&directory), Err(super::FleetError::Capture));
        assert!(outside.join("sentinel").exists());
        std::fs::remove_file(directory.join(format!(".env-{}.staging", "c".repeat(64)))).unwrap();
        std::fs::remove_dir_all(directory).unwrap();
        std::fs::remove_dir_all(outside).unwrap();
    }

    #[test]
    fn small_cache_rotation_fits_each_plan_window_and_provider_listing_bound() {
        for days in [1_u8, 7, 30, 35] {
            let two_points_every_tick = usize::from(days) * 24 * 60 * 6 * 2;
            let limit = scheduled_point_limit(days);
            assert!(limit > two_points_every_tick);
            assert!(limit <= 1_000_000);
        }
    }
}
