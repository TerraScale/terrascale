//! Durable, fail-closed deletion of expired routed recovery artifacts.
//!
//! A single protected journal binds exact immutable keys to destination identities.
//! It survives remote marker deletion; provider acknowledgements are fsynced before
//! local eviction, and the journal is retired last. Discovery never deletes.
use super::{
    BackupEnvelope, FleetError, FleetLimits, ObjectDestination, Provider, Stream, inventory,
    open_staging_temporary, participant_namespace,
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::path::Path;
use std::sync::Arc;

const JOURNAL: &str = ".envelope-retention";
const VERSION: u32 = 2;
const MAX_JOURNAL_BYTES: u64 = 8 * 1024 * 1024;
const MAX_INVENTORY_ENTRIES: usize = 65_536;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Target {
    provider: String,
    destination: String,
    key: String,
    acknowledged: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Work {
    // Only derived, validated relative paths are used for local cache eviction.
    relative: String,
    envelope: Option<Vec<u8>>,
    manifest: Option<Vec<u8>>,
    targets: Vec<Target>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Journal {
    version: u32,
    database: String,
    operation_id: String,
    work: Vec<Work>,
}

impl Journal {
    fn identity(&self) -> Result<String, FleetError> {
        let mut canonical = self.clone();
        canonical.operation_id.clear();
        for work in &mut canonical.work {
            for target in &mut work.targets {
                target.acknowledged = false;
            }
        }
        let bytes = serde_json::to_vec(&canonical).map_err(|_| FleetError::Capture)?;
        Ok(blake3::hash(&bytes).to_hex().to_string())
    }
}

fn read_private(path: &Path, max: u64) -> Result<Vec<u8>, FleetError> {
    let metadata = std::fs::symlink_metadata(path).map_err(|_| FleetError::Capture)?;
    if !metadata.is_file() || metadata.len() > max {
        return Err(FleetError::Capture);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(target_os = "linux")]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(0o400_000); // O_NOFOLLOW
    }
    let mut bytes = Vec::new();
    options
        .open(path)
        .map_err(|_| FleetError::Capture)?
        .take(max + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| FleetError::Capture)?;
    if bytes.len() as u64 > max {
        return Err(FleetError::Capture);
    }
    Ok(bytes)
}

fn journal_bound(limits: FleetLimits) -> u64 {
    limits.inventory_bytes_per_stream.min(MAX_JOURNAL_BYTES)
}

fn persist(path: &Path, journal: &Journal, limits: FleetLimits) -> Result<(), FleetError> {
    let bytes = serde_json::to_vec(journal).map_err(|_| FleetError::Capture)?;
    if bytes.len() as u64 > journal_bound(limits) {
        return Err(FleetError::StagingFull);
    }
    let temporary = path.with_extension("pending");
    let mut file = open_staging_temporary(&temporary)?;
    file.write_all(&bytes).map_err(|_| FleetError::Capture)?;
    file.sync_all().map_err(|_| FleetError::Capture)?;
    std::fs::rename(&temporary, path).map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(path.parent().ok_or(FleetError::Capture)?)
        .map_err(|_| FleetError::Capture)
}

fn load(path: &Path, limits: FleetLimits) -> Result<Option<Journal>, FleetError> {
    match std::fs::symlink_metadata(path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(FleetError::Capture),
        Ok(_) => {}
    }
    let journal: Journal = serde_json::from_slice(&read_private(path, journal_bound(limits))?)
        .map_err(|_| FleetError::Capture)?;
    if journal.version != VERSION || journal.operation_id != journal.identity()? {
        return Err(FleetError::Capture);
    }
    Ok(Some(journal))
}

fn destinations(stream: &Stream) -> Result<Vec<(Provider, Arc<ObjectDestination>)>, FleetError> {
    let mut result = Vec::new();
    let mut seen = BTreeMap::new();
    for (provider, destination) in
        stream.config.destinations.iter().chain(stream.config.historical_destinations.iter())
    {
        if let Some(previous) = seen.insert(destination.identity().to_owned(), *provider) {
            if previous != *provider {
                return Err(FleetError::Configuration);
            }
        } else {
            result.push((*provider, Arc::clone(destination)));
        }
    }
    result.sort_by(|left, right| left.1.identity().cmp(right.1.identity()));
    Ok(result)
}

fn point_relative(prefix: &str) -> Result<&str, FleetError> {
    let parts = prefix.split('/').collect::<Vec<_>>();
    if parts.len() != 5
        || parts[0] != "participants"
        || ![(parts[1], 32), (parts[2], 16), (parts[3], 32)].iter().all(|(part, len)| {
            part.len() == *len && part.bytes().all(|byte| byte.is_ascii_hexdigit())
        })
    {
        return Err(FleetError::Capture);
    }
    let id = parts[4].strip_prefix("rp-").ok_or(FleetError::Capture)?;
    super::super::manifest::validate_identity(id).map_err(|_| FleetError::Capture)?;
    Ok(id)
}

fn decode_manifest(
    bytes: &[u8],
    database: &str,
    relative: &str,
) -> Result<super::super::BackupManifest, FleetError> {
    let manifest = super::super::BackupManifest::decode(bytes).map_err(|_| FleetError::Capture)?;
    if manifest.database_id() != database || point_relative(relative)? != manifest.identity() {
        return Err(FleetError::Capture);
    }
    let incarnation =
        u128::from_str_radix(relative.split('/').nth(1).ok_or(FleetError::Capture)?, 16)
            .map_err(|_| FleetError::Capture)?;
    if crate::identity::DatabaseUlid::from_wire_bytes(incarnation.to_be_bytes()).to_text()
        != database
    {
        return Err(FleetError::Capture);
    }
    Ok(manifest)
}

fn work_keys(work: &Work, database: &str) -> Result<Vec<String>, FleetError> {
    match (&work.envelope, &work.manifest) {
        (Some(bytes), None) => {
            let envelope = BackupEnvelope::decode(bytes).map_err(|_| FleetError::Capture)?;
            if envelope.database_id() != database
                || work.relative != format!("envelopes/env-{}", envelope.digest())
            {
                return Err(FleetError::Capture);
            }
            Ok(vec![format!("{database}/{}/envelope.tbv1", work.relative)])
        }
        (None, Some(bytes)) => {
            let manifest = decode_manifest(bytes, database, &work.relative)?;
            let mut names = vec![
                "transfer-v1.index".to_owned(),
                super::super::manifest::ENCRYPTED_MANIFEST_FILE.to_owned(),
            ];
            names.extend(manifest.members().iter().map(|member| member.name().to_owned()));
            Ok(names
                .into_iter()
                .map(|name| format!("{database}/{}/{name}", work.relative))
                .collect())
        }
        _ => Err(FleetError::Capture),
    }
}

fn validate_journal(
    journal: &Journal,
    database: &str,
    now: u64,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    max: usize,
) -> Result<(), FleetError> {
    if journal.database != database || journal.work.len() > max {
        return Err(FleetError::Capture);
    }
    let mut seen_work = BTreeSet::new();
    let mut total = 0_usize;
    for work in &journal.work {
        if !seen_work.insert(&work.relative) {
            return Err(FleetError::Capture);
        }
        let keys = work_keys(work, database)?.into_iter().collect::<BTreeSet<_>>();
        let deadline = match (&work.envelope, &work.manifest) {
            (Some(bytes), None) => BackupEnvelope::decode(bytes)
                .map_err(|_| FleetError::Capture)?
                .retain_until_unix_millis(),
            (None, Some(bytes)) => {
                decode_manifest(bytes, database, &work.relative)?.retain_until_unix_millis()
            }
            _ => return Err(FleetError::Capture),
        };
        if deadline > now {
            return Err(FleetError::Capture);
        }
        let expected = admitted
            .iter()
            .flat_map(|(provider, destination)| {
                keys.iter()
                    .map(move |key| (provider.as_str(), destination.identity(), key.as_str()))
            })
            .collect::<BTreeSet<_>>();
        let actual = work
            .targets
            .iter()
            .map(|target| {
                (target.provider.as_str(), target.destination.as_str(), target.key.as_str())
            })
            .collect::<BTreeSet<_>>();
        if actual != expected || actual.len() != work.targets.len() {
            return Err(FleetError::Configuration);
        }
        total = total.checked_add(actual.len()).ok_or(FleetError::StagingFull)?;
        if total > max {
            return Err(FleetError::StagingFull);
        }
    }
    Ok(())
}

fn targets(
    work: &Work,
    database: &str,
    admitted: &[(Provider, Arc<ObjectDestination>)],
) -> Result<Vec<Target>, FleetError> {
    let keys = work_keys(work, database)?;
    Ok(admitted
        .iter()
        .flat_map(|(provider, destination)| {
            keys.iter().map(move |key| Target {
                provider: provider.as_str().to_owned(),
                destination: destination.identity().to_owned(),
                key: key.clone(),
                acknowledged: false,
            })
        })
        .collect())
}

#[derive(Default)]
struct Budget {
    entries: usize,
    bytes: u64,
}
impl Budget {
    fn add(&mut self, bytes: u64, max: usize, limit: u64) -> Result<(), FleetError> {
        self.entries = self.entries.checked_add(1).ok_or(FleetError::StagingFull)?;
        self.bytes = self.bytes.checked_add(bytes).ok_or(FleetError::StagingFull)?;
        if self.entries > max || self.bytes > limit {
            return Err(FleetError::StagingFull);
        }
        Ok(())
    }
}

// Walk a fixed-depth archive layout; symlinks and arbitrary nested trees fail
// before any deletion. Even unfinished capture files consume the same budget.
fn local_points(
    staging: &Path,
    max: usize,
    byte_limit: u64,
    budget: &mut Budget,
) -> Result<BTreeMap<String, Vec<u8>>, FleetError> {
    fn walk(
        staging: &Path,
        path: &Path,
        depth: usize,
        max: usize,
        byte_limit: u64,
        budget: &mut Budget,
        points: &mut BTreeMap<String, Vec<u8>>,
    ) -> Result<(), FleetError> {
        for entry in std::fs::read_dir(path).map_err(|_| FleetError::Capture)? {
            let entry = entry.map_err(|_| FleetError::Capture)?;
            let metadata =
                std::fs::symlink_metadata(entry.path()).map_err(|_| FleetError::Capture)?;
            if metadata.file_type().is_symlink() {
                return Err(FleetError::Capture);
            }
            budget.add(if metadata.is_file() { metadata.len() } else { 0 }, max, byte_limit)?;
            if metadata.is_dir() {
                if depth >= 4 {
                    return Err(FleetError::Capture);
                }
                walk(staging, &entry.path(), depth + 1, max, byte_limit, budget, points)?;
            } else if metadata.is_file()
                && depth == 4
                && entry.file_name() == super::super::manifest::ENCRYPTED_MANIFEST_FILE
            {
                let parent = entry.path().parent().ok_or(FleetError::Capture)?.to_path_buf();
                let relative = parent
                    .strip_prefix(staging)
                    .map_err(|_| FleetError::Capture)?
                    .to_str()
                    .ok_or(FleetError::Capture)?
                    .to_owned();
                if parent
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.starts_with(".evict-rp-"))
                {
                    continue;
                }
                point_relative(&relative)?;
                points.insert(
                    relative,
                    read_private(
                        &entry.path(),
                        (super::super::manifest::MAX_MANIFEST_BYTES + 4096) as u64,
                    )?,
                );
            } else if !metadata.is_file() {
                return Err(FleetError::Capture);
            }
        }
        Ok(())
    }
    let root = staging.join("participants");
    let mut points = BTreeMap::new();
    match std::fs::symlink_metadata(&root) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(points),
        Err(_) => return Err(FleetError::Capture),
        Ok(metadata) if !metadata.is_dir() || metadata.file_type().is_symlink() => {
            return Err(FleetError::Capture);
        }
        Ok(_) => {}
    }
    walk(staging, &root, 0, max, byte_limit, budget, &mut points)?;
    Ok(points)
}

pub(super) async fn resume_outstanding(
    database: &str,
    stream: &Stream,
    limits: FleetLimits,
) -> Result<(), FleetError> {
    match std::fs::symlink_metadata(stream.config.staging.join(JOURNAL)) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(_) => return Err(FleetError::Capture),
        Ok(_) => {}
    }
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|_| FleetError::Capture)?
        .as_millis();
    enforce(database, stream, limits, u64::try_from(now).map_err(|_| FleetError::Capture)?).await
}

#[derive(Default)]
struct RetentionInventory {
    budget: Budget,
    physical: BTreeMap<String, super::super::BackupManifest>,
    physical_objects: BTreeMap<String, Vec<(String, u64)>>,
    envelopes: BTreeMap<String, BackupEnvelope>,
    protected: BTreeSet<String>,
}

async fn scan_physical(
    database: &str,
    stream: &Stream,
    limits: FleetLimits,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    max: usize,
    inventory: &mut RetentionInventory,
) -> Result<(), FleetError> {
    let RetentionInventory { budget, physical, physical_objects, .. } = inventory;
    for (relative, sealed) in
        local_points(&stream.config.staging, max, limits.staging_bytes_per_stream, budget)?
    {
        let bytes =
            stream.config.keys.open_backup_envelope(&sealed).map_err(|_| FleetError::Capture)?;
        physical.insert(relative.clone(), decode_manifest(&bytes, database, &relative)?);
    }
    // Scan all active AND retained historical destinations. No discovery or
    // failed read can be interpreted as an empty provider.
    for (_, destination) in admitted {
        let objects = destination
            .retention_participant_objects(
                database,
                max.saturating_sub(budget.entries),
                limits.staging_bytes_per_stream.saturating_sub(budget.bytes),
            )
            .await
            .map_err(|_| FleetError::Upload)?;
        for (key, bytes) in objects {
            budget.add(bytes, max, limits.staging_bytes_per_stream)?;
            let (prefix, member) = key.rsplit_once('/').ok_or(FleetError::Capture)?;
            let relative =
                prefix.strip_prefix(&format!("{database}/")).ok_or(FleetError::Capture)?;
            physical_objects
                .entry(relative.to_owned())
                .or_default()
                .push((member.to_owned(), bytes));
            if member == super::super::manifest::ENCRYPTED_MANIFEST_FILE {
                let sealed = destination.get_member(&key).await.map_err(|_| FleetError::Upload)?;
                let plain = stream
                    .config
                    .keys
                    .open_backup_envelope(&sealed)
                    .map_err(|_| FleetError::Capture)?;
                let manifest = decode_manifest(&plain, database, relative)?;
                if physical.get(relative).is_some_and(|previous| previous != &manifest) {
                    return Err(FleetError::Capture);
                }
                physical.insert(relative.to_owned(), manifest);
            }
        }
    }
    Ok(())
}

async fn scan_remote_envelopes(
    database: &str,
    limits: FleetLimits,
    now: u64,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    max: usize,
    outstanding: Option<&Journal>,
    inventory: &mut RetentionInventory,
) -> Result<(), FleetError> {
    let RetentionInventory { budget, physical, envelopes, protected, .. } = inventory;
    for (_, destination) in admitted {
        let prefixes = destination
            .list_envelope_prefixes(database, limits.points_per_stream)
            .await
            .map_err(|_| FleetError::Upload)?;
        for prefix in prefixes {
            let bytes =
                destination.download_envelope(&prefix).await.map_err(|_| FleetError::Upload)?;
            budget.add(bytes.len() as u64, max, limits.staging_bytes_per_stream)?;
            let envelope = BackupEnvelope::decode(&bytes).map_err(|_| FleetError::Capture)?;
            if envelope.database_id() != database
                || prefix != format!("{database}/envelopes/env-{}", envelope.digest())
            {
                return Err(FleetError::Capture);
            }
            // Previously authenticated journal bytes remain authority during a
            // partial delete whose participant manifests are already gone.
            let resuming = outstanding.is_some_and(|journal| {
                journal.work.iter().any(|work| work.envelope.as_ref() == Some(&bytes))
            });
            for participant in envelope.participants() {
                let relative = format!(
                    "{}/rp-{}",
                    participant_namespace(participant.file_identity),
                    participant.manifest.identity()
                );
                if !resuming && physical.get(&relative) != Some(&participant.manifest) {
                    return Err(FleetError::Capture);
                }
                if envelope.retain_until_unix_millis() > now {
                    protected.insert(relative);
                }
            }
            if envelopes.get(envelope.digest()).is_some_and(|previous| previous != &envelope) {
                return Err(FleetError::Capture);
            }
            envelopes.insert(envelope.digest().to_owned(), envelope);
            if envelopes.len() > limits.points_per_stream {
                return Err(FleetError::StagingFull);
            }
        }
    }
    Ok(())
}

fn protect_staged_envelopes(
    database: &str,
    stream: &Stream,
    limits: FleetLimits,
    max: usize,
    inventory: &mut RetentionInventory,
) -> Result<(), FleetError> {
    let RetentionInventory { budget, protected, .. } = inventory;
    // A staged-but-unpublished envelope is still a capture reference. Account
    // for it with the same bounds; never turn canceled publication into deletion.
    let catalog = stream.config.staging.join("envelopes");
    if catalog.exists() {
        if !std::fs::symlink_metadata(&catalog).map_err(|_| FleetError::Capture)?.is_dir() {
            return Err(FleetError::Capture);
        }
        for entry in std::fs::read_dir(&catalog).map_err(|_| FleetError::Capture)? {
            let entry = entry.map_err(|_| FleetError::Capture)?;
            let name = entry.file_name().to_str().ok_or(FleetError::Capture)?.to_owned();
            let metadata =
                std::fs::symlink_metadata(entry.path()).map_err(|_| FleetError::Capture)?;
            if !metadata.is_dir() || metadata.file_type().is_symlink() {
                return Err(FleetError::Capture);
            }
            budget.add(0, max, limits.staging_bytes_per_stream)?;
            let marker = entry.path().join("envelope.tbv1");
            for member in std::fs::read_dir(entry.path()).map_err(|_| FleetError::Capture)? {
                let member = member.map_err(|_| FleetError::Capture)?;
                let metadata =
                    std::fs::symlink_metadata(member.path()).map_err(|_| FleetError::Capture)?;
                if !metadata.is_file() || member.file_name() != "envelope.tbv1" {
                    return Err(FleetError::Capture);
                }
                budget.add(metadata.len(), max, limits.staging_bytes_per_stream)?;
            }
            if name.starts_with(".env-") && name.ends_with(".staging") {
                let bytes = read_private(&marker, 256 * 1024)?;
                let envelope = BackupEnvelope::decode(&bytes).map_err(|_| FleetError::Capture)?;
                if envelope.database_id() != database {
                    return Err(FleetError::Capture);
                }
                for participant in envelope.participants() {
                    protected.insert(format!(
                        "{}/rp-{}",
                        participant_namespace(participant.file_identity),
                        participant.manifest.identity()
                    ));
                }
            }
        }
    }
    Ok(())
}

fn plan_retention(
    database: &str,
    now: u64,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    inventory: &RetentionInventory,
) -> Result<Journal, FleetError> {
    let RetentionInventory { physical, physical_objects, envelopes, protected, .. } = inventory;
    let mut work = Vec::new();
    for envelope in envelopes.values().filter(|envelope| envelope.retain_until_unix_millis() <= now)
    {
        work.push(Work {
            relative: format!("envelopes/env-{}", envelope.digest()),
            envelope: Some(envelope.encode().map_err(|_| FleetError::Capture)?),
            manifest: None,
            targets: Vec::new(),
        });
    }
    for (relative, manifest) in physical {
        // Independent history and unfinished/unexpired captures are protected.
        if protected.contains(relative)
            || manifest.kind() != super::super::BackupKind::Snapshot
            || manifest.base_sequence().is_some()
            || manifest.retain_until_unix_millis() > now
        {
            continue;
        }
        for (name, length) in physical_objects.get(relative).into_iter().flatten() {
            if name == "transfer-v1.index"
                || name == super::super::manifest::ENCRYPTED_MANIFEST_FILE
            {
                continue;
            }
            if !manifest
                .members()
                .iter()
                .any(|member| member.name() == name && member.length() == *length)
            {
                return Err(FleetError::Capture);
            }
        }
        work.push(Work {
            relative: relative.clone(),
            envelope: None,
            manifest: Some(manifest.encode()),
            targets: Vec::new(),
        });
    }
    for item in &mut work {
        item.targets = targets(item, database, admitted)?;
    }
    let mut journal = Journal {
        version: VERSION,
        database: database.to_owned(),
        operation_id: String::new(),
        work,
    };
    journal.operation_id = journal.identity()?;
    Ok(journal)
}

fn revalidate_retention(
    journal: &Journal,
    database: &str,
    now: u64,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    max: usize,
    inventory: &RetentionInventory,
) -> Result<(), FleetError> {
    let RetentionInventory { physical, protected, .. } = inventory;
    validate_journal(journal, database, now, admitted, max)?;
    // Revalidate every stale point target against the fresh cross-provider
    // retained-reference inventory before resuming ANY deletion.
    for work in &journal.work {
        if work.manifest.is_some() && protected.contains(&work.relative) {
            return Err(FleetError::Capture);
        }
        if let Some(bytes) = &work.manifest {
            let saved = decode_manifest(bytes, database, &work.relative)?;
            if physical.get(&work.relative).is_some_and(|current| current != &saved) {
                return Err(FleetError::Capture);
            }
        }
    }
    Ok(())
}

async fn delete_journal_targets(
    journal_path: &Path,
    journal: &mut Journal,
    database: &str,
    admitted: &[(Provider, Arc<ObjectDestination>)],
    limits: FleetLimits,
) -> Result<(), FleetError> {
    persist(journal_path, journal, limits)?;
    // Envelope markers precede orphan objects. Exact target names never expand
    // during retry, even when a provider's publication/index is already absent.
    for work_index in 0..journal.work.len() {
        for target_index in 0..journal.work[work_index].targets.len() {
            let target = &journal.work[work_index].targets[target_index];
            if target.acknowledged {
                continue;
            }
            let destination = admitted
                .iter()
                .find(|(provider, destination)| {
                    provider.as_str() == target.provider
                        && destination.identity() == target.destination
                })
                .ok_or(FleetError::Configuration)?;
            destination
                .1
                .delete_retention_object(database, &target.key)
                .await
                .map_err(|_| FleetError::Upload)?;
            journal.work[work_index].targets[target_index].acknowledged = true;
            persist(journal_path, journal, limits)?;
        }
    }
    Ok(())
}

fn evict_journal(
    journal_path: &Path,
    journal: &Journal,
    stream: &Stream,
) -> Result<(), FleetError> {
    // No await between the last durable ack and eviction. A crash is still
    // resumable because all work remains in the journal until eviction fsyncs.
    for work in &journal.work {
        let path = stream.config.staging.join(&work.relative);
        if let Some(parent) = path.parent()
            && parent.exists()
        {
            let tombstone = parent.join(format!(
                ".evict-{}",
                path.file_name().and_then(|name| name.to_str()).ok_or(FleetError::Capture)?
            ));
            if tombstone.exists() {
                if !std::fs::symlink_metadata(&tombstone).map_err(|_| FleetError::Capture)?.is_dir()
                {
                    return Err(FleetError::Capture);
                }
                std::fs::remove_dir_all(&tombstone).map_err(|_| FleetError::Capture)?;
                super::super::sync_directory(parent).map_err(|_| FleetError::Capture)?;
            }
            inventory::remove_point(&path)?;
            super::super::sync_directory(parent).map_err(|_| FleetError::Capture)?;
        }
    }
    std::fs::remove_file(journal_path).map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)
}

/// Called by the scheduled worker with exclusive fleet ownership. New captures
/// generate immutable point IDs. A canceled blocking capture is unsealed or has
/// its own future retention deadline; neither can be an expired orphan here.
pub(super) async fn enforce(
    database: &str,
    stream: &Stream,
    limits: FleetLimits,
    now: u64,
) -> Result<(), FleetError> {
    let journal_path = stream.config.staging.join(JOURNAL);
    let outstanding = load(&journal_path, limits)?;
    let admitted = destinations(stream)?;
    let max = limits.points_per_stream.saturating_mul(256).clamp(1, MAX_INVENTORY_ENTRIES);
    if let Some(journal) = &outstanding {
        validate_journal(journal, database, now, &admitted, max)?;
    }
    let mut inventory = RetentionInventory::default();
    scan_physical(database, stream, limits, &admitted, max, &mut inventory).await?;
    scan_remote_envelopes(
        database,
        limits,
        now,
        &admitted,
        max,
        outstanding.as_ref(),
        &mut inventory,
    )
    .await?;
    protect_staged_envelopes(database, stream, limits, max, &mut inventory)?;
    let mut journal = match outstanding {
        Some(journal) => journal,
        None => plan_retention(database, now, &admitted, &inventory)?,
    };
    if journal.work.is_empty() {
        return Ok(());
    }
    revalidate_retention(&journal, database, now, &admitted, max, &inventory)?;
    delete_journal_targets(&journal_path, &mut journal, database, &admitted, limits).await?;
    evict_journal(&journal_path, &journal, stream)
}
