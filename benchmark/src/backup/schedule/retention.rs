//! Retention policy enforcement (PB-6014).
//!
//! Retention runs as a separate typed operation from backup selection and
//! restore selection, exactly as the parity row requires. The unit of
//! pruning is a **whole chain**: the full that roots a chain and every
//! incremental that builds on it are pruned or kept together, so an
//! expired full can never strand live incrementals and an incremental can
//! never lose its root. The chain that holds the destination's newest
//! recovery point is always kept — that is what "never deletes the last
//! full of a chain" reduces to here — and a chain whose root is younger
//! than the policy's minimum age is kept whatever else is true.
//!
//! Age is a destination fact, not a scheduler fact: a chain's sealed-at
//! time is the modification time of its root full's manifest file, the
//! last byte written before publication. The caller supplies `now`, so a
//! sweep is deterministic under injected times and honest about clock
//! skew: a point whose sealed-at time cannot be read or compared fails
//! the sweep typed rather than being pruned on a guess.
//!
//! Deletion is idempotent: a point directory that is already gone counts
//! as pruned, so a retried sweep after a partial failure completes the
//! same plan. Pruning a point reclaims every artifact of it, so the
//! sweep deletes the point directory **and** the sealed bundle artifact a
//! scheduled run leaves beside it in the destination root — a bundle that
//! outlived its point would keep the pruned data fully recoverable and
//! grow without bound. A failure mid-sweep returns the typed cause
//! together with the receipt of everything already pruned (PB-6014's
//! partial-deletion reporting), and nothing is deleted when discovery
//! itself fails — an invalid sealed manifest fails the whole sweep closed.

use std::path::Path;
use std::time::{Duration, SystemTime};

use crate::backup::manifest::{MANIFEST_FILE, point_directory};
use crate::backup::schedule::codec::bundle_file_name;
use crate::backup::{BackupKind, BackupManifest, discover_points};
use crate::types::StorageSequence;

/// One retention policy: disabled, or a minimum age below which nothing
/// is pruned. There is deliberately no "prune everything regardless of
/// age" form: pruning without an age floor can delete the newest-but-one
/// chain seconds after it seals.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RetentionPolicy {
    enabled: bool,
    min_age: Duration,
}

impl RetentionPolicy {
    /// A policy that never prunes anything.
    #[must_use]
    pub fn disabled() -> Self {
        Self { enabled: false, min_age: Duration::ZERO }
    }

    /// A policy that prunes whole chains whose root full is at least
    /// `min_age` old, always keeping the newest chain.
    #[must_use]
    pub fn min_age(min_age: Duration) -> Self {
        Self { enabled: true, min_age }
    }

    /// Whether the policy prunes at all.
    #[must_use]
    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    /// Returns the minimum age below which chains are kept.
    #[must_use]
    pub fn minimum_age(&self) -> Duration {
        self.min_age
    }
}

/// One pruned recovery point, receipted without any document content.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PrunedPoint {
    identity: String,
    kind: BackupKind,
    boundary: StorageSequence,
}

impl PrunedPoint {
    /// Creates one receipt entry from validated facts.
    fn new(identity: String, kind: BackupKind, boundary: StorageSequence) -> Self {
        Self { identity, kind, boundary }
    }

    /// Returns the pruned point's identity.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the pruned point's kind.
    #[must_use]
    pub fn kind(&self) -> BackupKind {
        self.kind
    }

    /// Returns the pruned point's boundary.
    #[must_use]
    pub fn boundary(&self) -> StorageSequence {
        self.boundary
    }
}

/// The receipt of one retention sweep: exactly what was pruned and which
/// chain roots were kept. It names identities, kinds, and boundaries
/// only — no paths, no archive content, no secrets.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct RetentionReceipt {
    pruned: Vec<PrunedPoint>,
    kept_roots: Vec<String>,
}

impl RetentionReceipt {
    /// Returns the pruned points in pruning order.
    #[must_use]
    pub fn pruned(&self) -> &[PrunedPoint] {
        &self.pruned
    }

    /// Returns the identities of every kept chain's root.
    #[must_use]
    pub fn protected_roots(&self) -> &[String] {
        &self.kept_roots
    }
}

/// Why a sweep failed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RetentionFailure {
    /// A sealed manifest in the destination failed validation; the sweep
    /// refuses to prune anything it cannot fully read.
    ManifestInvalid,
    /// A sealed-at time cannot be read or compared against `now`; nothing
    /// is pruned on a clock guess.
    Clock,
    /// A point directory or its sealed bundle artifact could not be
    /// deleted.
    DestinationUnavailable,
}

/// The typed failure of one sweep, carrying the partial receipt of what
/// was already pruned before the failure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RetentionError {
    cause: RetentionFailure,
    pruned_before_failure: Vec<PrunedPoint>,
}

impl RetentionError {
    /// Returns the typed cause.
    #[must_use]
    pub fn cause(&self) -> RetentionFailure {
        self.cause
    }

    /// Returns what the sweep pruned before failing, in pruning order.
    #[must_use]
    pub fn pruned_before_failure(&self) -> &[PrunedPoint] {
        &self.pruned_before_failure
    }
}

/// Enforces `policy` on `destination` as of `now`.
///
/// The sweep discovers every sealed recovery point, groups them into
/// chains by base links, keeps the newest chain and every chain younger
/// than the minimum age, and deletes the oldest prunable chains first,
/// one whole chain at a time, receipting each pruned point. Each pruned
/// point takes both of its artifacts with it: the `rp-<identity>`
/// directory and the `bundle-<identity>.tbv2b` artifact a scheduled run
/// seals beside it, so pruned data is unrecoverable rather than merely
/// hidden behind a surviving bundle.
///
/// # Errors
///
/// Returns [`RetentionFailure::ManifestInvalid`] when discovery fails
/// closed (nothing is deleted), [`RetentionFailure::Clock`] when a
/// sealed-at time cannot be compared, and
/// [`RetentionFailure::DestinationUnavailable`] when a deletion fails —
/// with the partial receipt attached.
pub fn enforce_retention(
    destination: &Path,
    policy: &RetentionPolicy,
    now: SystemTime,
) -> Result<RetentionReceipt, RetentionError> {
    if !policy.is_enabled() {
        return Ok(RetentionReceipt::default());
    }
    let points = discover_points(destination)
        .map_err(|_| clock_free_failure(RetentionFailure::ManifestInvalid))?;
    if points.is_empty() {
        return Ok(RetentionReceipt::default());
    }
    let groups = group_into_chains(&points);
    // The newest point's chain is protected outright, whatever its age.
    let protected_group = groups[newest_point(&points)];

    let mut prunable: Vec<(usize, u64, String)> = Vec::new();
    for (index, group) in groups.iter().enumerate() {
        if *group != index || *group == protected_group {
            // Not a chain root, or the protected newest chain.
            continue;
        }
        let age = root_age(destination, points[index].identity(), now)?;
        if age >= policy.minimum_age() {
            // Oldest sealed-at first; the identity makes ties — and thus a
            // retried sweep's plan — deterministic.
            prunable.push((index, age.as_secs(), points[index].identity().to_owned()));
        }
    }
    prunable.sort_by(|left, right| right.1.cmp(&left.1).then_with(|| left.2.cmp(&right.2)));

    let mut pruned = Vec::new();
    for (root, _, _) in &prunable {
        let mut members: Vec<usize> = groups
            .iter()
            .enumerate()
            .filter(|(_, group)| **group == *root)
            .map(|(index, _)| index)
            .collect();
        members.sort_by_key(|index| points[*index].identity().to_owned());
        for index in members {
            let point = &points[index];
            let directory = destination.join(point_directory(point.identity()));
            let removed = std::fs::remove_dir_all(&directory);
            if removed.is_err() && directory.exists() {
                return Err(RetentionError {
                    cause: RetentionFailure::DestinationUnavailable,
                    pruned_before_failure: pruned,
                });
            }
            // A scheduled run also seals a bundle artifact for the point in
            // the destination root, so pruning the point reclaims that too —
            // otherwise the pruned data stays fully recoverable and orphaned
            // bundles grow without bound. An already-missing bundle counts
            // as pruned, exactly like its point directory.
            let bundle = destination.join(bundle_file_name(point.identity()));
            let removed = std::fs::remove_file(&bundle);
            if removed.is_err() && bundle.exists() {
                return Err(RetentionError {
                    cause: RetentionFailure::DestinationUnavailable,
                    pruned_before_failure: pruned,
                });
            }
            pruned.push(PrunedPoint::new(
                point.identity().to_owned(),
                point.kind(),
                point.boundary_sequence(),
            ));
        }
    }

    let mut kept_roots: Vec<String> = groups
        .iter()
        .enumerate()
        .filter_map(|(index, group)| {
            (index == *group).then_some(points[index].identity().to_owned())
        })
        .collect();
    kept_roots.sort();
    Ok(RetentionReceipt { pruned, kept_roots })
}

/// A failure that pruned nothing.
fn clock_free_failure(cause: RetentionFailure) -> RetentionError {
    RetentionError { cause, pruned_before_failure: Vec::new() }
}

/// Groups every point into a chain by union-find, returning each point's
/// group-root index. A point links to every point that covers its base, so
/// chain membership is a structural fact and ambiguity protects more,
/// never less.
fn group_into_chains(points: &[BackupManifest]) -> Vec<usize> {
    let mut parent: Vec<usize> = (0..points.len()).collect();
    for (index, point) in points.iter().enumerate() {
        if let Some(base) = point.base_sequence() {
            for (other, candidate) in points.iter().enumerate() {
                if candidate.boundary_sequence() == base {
                    let left = chain_find(&mut parent, index);
                    let right = chain_find(&mut parent, other);
                    if left != right {
                        parent[left.max(right)] = left.min(right);
                    }
                }
            }
        }
    }
    (0..points.len()).map(|index| chain_find(&mut parent, index)).collect()
}

/// Finds a point's chain root, compressing the walked path.
fn chain_find(parent: &mut [usize], index: usize) -> usize {
    let mut root = index;
    while parent[root] != root {
        root = parent[root];
    }
    let mut current = index;
    while parent[current] != root {
        let next = parent[current];
        parent[current] = root;
        current = next;
    }
    root
}

/// Returns the index of the newest point: the highest boundary, an
/// incremental preferred over a root at the same boundary, and the
/// identity as the deterministic tie-break — the same order
/// `verify_latest_chain` uses to pick the head. Identities are unique per
/// point, so the comparison is always decisive.
fn newest_point(points: &[BackupManifest]) -> usize {
    points
        .iter()
        .enumerate()
        .max_by(|(_, left), (_, right)| {
            (
                left.boundary_sequence().get(),
                usize::from(left.base_sequence().is_some()),
                left.identity(),
            )
                .cmp(&(
                    right.boundary_sequence().get(),
                    usize::from(right.base_sequence().is_some()),
                    right.identity(),
                ))
        })
        .map_or(0, |(index, _)| index)
}

/// Reads the sealed-at age of a chain root against the sweep's `now`: the
/// manifest file's mtime. A sealed-at in the future or an unreadable time
/// is a clock failure, never a prune.
fn root_age(
    destination: &Path,
    identity: &str,
    now: SystemTime,
) -> Result<Duration, RetentionError> {
    let manifest = destination.join(point_directory(identity)).join(MANIFEST_FILE);
    let sealed_at = std::fs::metadata(&manifest).ok().and_then(|metadata| metadata.modified().ok());
    let sealed_at = sealed_at.ok_or_else(|| clock_free_failure(RetentionFailure::Clock))?;
    now.duration_since(sealed_at).map_err(|_| clock_free_failure(RetentionFailure::Clock))
}
