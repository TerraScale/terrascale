//! Authenticated retained metadata is separate from the bounded archive cache.
use super::*;

pub(super) fn inventory_path(stream: &Stream) -> PathBuf {
    stream.config.staging.join(".inventory")
}

pub(super) fn inventory_point_limit(stream: &Stream) -> usize {
    // One scheduled point per ten seconds, including one extra root day and
    // bounded explicit snapshots. This is a metadata guard, not a pruning rule.
    (usize::from(stream.config.retention_days) + 1) * 8_640 + 8_640
}

pub(super) fn inventory_points(
    stream: &Stream,
) -> Result<Vec<super::super::BackupManifest>, FleetError> {
    let mut points = BTreeMap::new();
    for directory in [inventory_path(stream), stream.config.staging.clone()] {
        if !directory.exists() {
            continue;
        }
        point_paths(&directory, inventory_point_limit(stream))?;
        for point in super::super::restore::discover_encrypted_points(
            &directory,
            stream.config.keys.as_ref(),
        )
        .map_err(|_| FleetError::Capture)?
        {
            points.insert(point.identity().to_owned(), point);
            if points.len() > inventory_point_limit(stream) {
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

fn cleanup_interrupted(directory: &Path) -> Result<(), FleetError> {
    for entry in std::fs::read_dir(directory).map_err(|_| FleetError::Capture)? {
        let entry = entry.map_err(|_| FleetError::Capture)?;
        if entry
            .file_name()
            .to_str()
            .is_some_and(|name| name.starts_with(".evict-rp-") || name.starts_with(".pending-"))
        {
            std::fs::remove_dir_all(entry.path()).map_err(|_| FleetError::Capture)?;
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
