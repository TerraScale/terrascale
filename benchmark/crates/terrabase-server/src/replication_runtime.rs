//! Receipt-backed durability progress shared by the production transport and API.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::{
    Mutex,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

use terrabase_cluster::production::DatabaseRouteScope;
use tokio::sync::watch;

/// A bounded durability wait failed without changing the committed outcome.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicationWaitError {
    /// No accepted replication assignment exists for the database.
    NotAdmitted,
    /// Global durability requires at least two selected Regions.
    InsufficientRegions,
    /// The requested durable boundary was not observed before the deadline.
    Timeout,
    /// The accepted assignment was fenced or shutdown began.
    Fenced,
}

struct DatabaseProgress {
    selected: BTreeSet<String>,
    regional_sequence: u64,
    regional_observed: bool,
    regions: BTreeMap<String, BTreeMap<String, u64>>,
    /// Receipt evidence split by authenticated peer so one lost session can
    /// be discarded without revoking surviving local RF2 evidence.
    regional_by_peer: BTreeMap<String, (u64, bool)>,
    regions_by_peer: BTreeMap<String, BTreeMap<String, BTreeMap<String, u64>>>,
    fenced: bool,
}

/// Bounded per-database progress derived exclusively from durable peer receipts.
///
/// Process restart clears these observations. Drivers recover them through
/// authenticated checkpoint exchange before new waits can succeed. No pending
/// operation queue is retained here; each request owns its bounded wait.
pub struct ReplicationProgress {
    fenced: AtomicBool,
    databases: Mutex<BTreeMap<DatabaseRouteScope, DatabaseProgress>>,
    changed: watch::Sender<u64>,
}

/// Conservative receipt evidence captured after the transport drain barrier.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationBoundary {
    /// `SQLite` sequence verified on the physical follower.
    pub regional_sequence: u64,
    /// Distinguishes an observed empty physical prefix from default zero state.
    pub regional_observed: bool,
    /// Contiguous origin frontiers verified in each selected Region.
    pub regions: BTreeMap<String, BTreeMap<String, u64>>,
}

impl Default for ReplicationProgress {
    fn default() -> Self {
        Self {
            fenced: AtomicBool::new(false),
            databases: Mutex::new(BTreeMap::new()),
            changed: watch::channel(0).0,
        }
    }
}

impl ReplicationProgress {
    /// Returns whether this member's replication evidence has been fenced.
    /// A fenced member cannot admit new databases, though already admitted
    /// local leases may continue serving reads while the control plane heals.
    #[must_use]
    pub fn is_fenced(&self) -> bool {
        self.fenced.load(Ordering::Acquire)
    }

    /// Returns the immutable accepted residency set while its assignment is live.
    ///
    /// # Errors
    /// Refuses unknown or fenced assignments.
    pub fn selected_regions(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<BTreeSet<String>, ReplicationWaitError> {
        let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced {
            return Err(ReplicationWaitError::Fenced);
        }
        Ok(progress.selected.clone())
    }

    /// Executes a bounded signing operation while the evidence fence is held.
    /// The caller must not reenter this progress ledger from the closure.
    ///
    /// # Errors
    /// Refuses unknown or fenced assignments and poisoned evidence.
    pub fn with_active_boundary<T>(
        &self,
        scope: DatabaseRouteScope,
        operation: impl FnOnce(&ReplicationBoundary) -> T,
    ) -> Result<T, ReplicationWaitError> {
        let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced {
            return Err(ReplicationWaitError::Fenced);
        }
        let boundary = ReplicationBoundary {
            regional_sequence: progress.regional_sequence,
            regional_observed: progress.regional_observed,
            regions: progress.regions.clone(),
        };
        let result = operation(&boundary);
        drop(databases);
        Ok(result)
    }

    /// Reads receipt evidence for a transition signer after workers have drained.
    /// This value alone does not prove fencing: the signer must durably fence
    /// the epoch and stop all old assignment workers before issuing a grant.
    ///
    /// # Errors
    /// Refuses poisoned evidence; no missing database is invented.
    pub fn boundaries(
        &self,
    ) -> Result<BTreeMap<DatabaseRouteScope, ReplicationBoundary>, ReplicationWaitError> {
        let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        Ok(databases
            .iter()
            .map(|(scope, progress)| {
                (
                    *scope,
                    ReplicationBoundary {
                        regional_sequence: progress.regional_sequence,
                        regional_observed: progress.regional_observed,
                        regions: progress.regions.clone(),
                    },
                )
            })
            .collect())
    }

    /// Returns live measured evidence, excluding fenced assignments.
    ///
    /// # Errors
    /// Refuses poisoned state rather than reporting healthy progress.
    pub fn active_boundaries(
        &self,
    ) -> Result<BTreeMap<DatabaseRouteScope, ReplicationBoundary>, ReplicationWaitError> {
        let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        Ok(databases
            .iter()
            .filter(|(_, progress)| !progress.fenced)
            .map(|(scope, progress)| {
                (
                    *scope,
                    ReplicationBoundary {
                        regional_sequence: progress.regional_sequence,
                        regional_observed: progress.regional_observed,
                        regions: progress.regions.clone(),
                    },
                )
            })
            .collect())
    }
    /// Registers immutable selected Regions from an accepted assignment.
    ///
    /// # Errors
    /// Rejects an empty/oversized selection and replacement of an assignment.
    /// Identical active registration is idempotent and preserves all receipts.
    pub fn admit(
        &self,
        scope: DatabaseRouteScope,
        selected: BTreeSet<String>,
    ) -> Result<(), ReplicationWaitError> {
        self.admit_many(std::iter::once((scope, selected)).collect())
    }

    /// Atomically registers a batch of immutable selected-region assignments.
    /// Validation completes before any ledger mutation.
    ///
    /// # Errors
    /// Returns `NotAdmitted` for invalid, conflicting, oversized, or fenced
    /// assignments, or when the database cardinality bound is exceeded.
    pub fn admit_many(
        &self,
        entries: BTreeMap<DatabaseRouteScope, BTreeSet<String>>,
    ) -> Result<(), ReplicationWaitError> {
        for selected in entries.values() {
            if selected.is_empty()
                || selected.len() > 20
                || selected.iter().any(|region| {
                    region.is_empty()
                        || region.len() > 32
                        || !region.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
                })
            {
                return Err(ReplicationWaitError::NotAdmitted);
            }
        }
        let mut databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        if self.fenced.load(Ordering::Acquire) {
            return Err(ReplicationWaitError::NotAdmitted);
        }
        let new_count = entries.keys().filter(|scope| !databases.contains_key(scope)).count();
        if databases.len().saturating_add(new_count) > terrabase::member::MAX_DATABASES {
            return Err(ReplicationWaitError::NotAdmitted);
        }
        for (scope, selected) in &entries {
            if let Some(existing) = databases.get(scope)
                && (existing.fenced || existing.selected != *selected)
            {
                return Err(ReplicationWaitError::NotAdmitted);
            }
        }
        for (scope, selected) in entries {
            databases.entry(scope).or_insert_with(|| DatabaseProgress {
                selected,
                regional_sequence: 0,
                regional_observed: false,
                regions: BTreeMap::new(),
                regional_by_peer: BTreeMap::new(),
                regions_by_peer: BTreeMap::new(),
                fenced: false,
            });
        }
        Ok(())
    }

    /// Advances the `SQLite` boundary covered by a verified local RF=2 receipt.
    /// This is called only after the physical driver proves the entire boundary.
    pub(crate) fn advance_regional(&self, scope: DatabaseRouteScope, sequence: u64) {
        self.advance_regional_from_peer(scope, "__local__", sequence);
    }

    pub(crate) fn advance_regional_from_peer(
        &self,
        scope: DatabaseRouteScope,
        peer: &str,
        sequence: u64,
    ) {
        if let Ok(mut databases) = self.databases.lock()
            && let Some(progress) = databases.get_mut(&scope)
            && !progress.fenced
        {
            let observed = progress.regional_by_peer.entry(peer.to_owned()).or_default();
            observed.0 = observed.0.max(sequence);
            observed.1 = true;
            progress.regional_sequence =
                progress.regional_by_peer.values().map(|entry| entry.0).max().unwrap_or(0);
            progress.regional_observed = progress.regional_by_peer.values().any(|entry| entry.1);
            self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
        }
    }

    /// Records an authenticated Region's RF=2-durable contiguous origin frontier.
    /// The transport must validate scope, placement epoch, and exact peer identity.
    pub(crate) fn advance_region(
        &self,
        scope: DatabaseRouteScope,
        region: &str,
        frontier: &BTreeMap<String, u64>,
    ) -> Result<(), ReplicationWaitError> {
        self.advance_region_from_peer(scope, "__local__", region, frontier)
    }

    pub(crate) fn advance_region_from_peer(
        &self,
        scope: DatabaseRouteScope,
        peer: &str,
        region: &str,
        frontier: &BTreeMap<String, u64>,
    ) -> Result<(), ReplicationWaitError> {
        let mut databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get_mut(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced {
            return Err(ReplicationWaitError::Fenced);
        }
        if !progress.selected.contains(region)
            || frontier.keys().any(|origin| !progress.selected.contains(origin))
        {
            return Err(ReplicationWaitError::NotAdmitted);
        }
        let observed = progress
            .regions_by_peer
            .entry(peer.to_owned())
            .or_default()
            .entry(region.to_owned())
            .or_default();
        for (origin, sequence) in frontier {
            let previous = observed.entry(origin.clone()).or_default();
            *previous = (*previous).max(*sequence);
        }
        progress.regions.clear();
        for peer_regions in progress.regions_by_peer.values() {
            for (region, frontier) in peer_regions {
                let merged = progress.regions.entry(region.clone()).or_default();
                for (origin, sequence) in frontier {
                    let previous = merged.entry(origin.clone()).or_default();
                    *previous = (*previous).max(*sequence);
                }
            }
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
        Ok(())
    }

    /// Drops all receipt evidence sourced from one authenticated peer while
    /// retaining the assignment and evidence from every surviving peer.
    pub(crate) fn fence_peer(&self, peer: &str) {
        if let Ok(mut databases) = self.databases.lock() {
            for progress in databases.values_mut() {
                progress.regional_by_peer.remove(peer);
                progress.regions_by_peer.remove(peer);
                progress.regional_sequence =
                    progress.regional_by_peer.values().map(|entry| entry.0).max().unwrap_or(0);
                progress.regional_observed =
                    progress.regional_by_peer.values().any(|entry| entry.1);
                progress.regions.clear();
                for peer_regions in progress.regions_by_peer.values() {
                    for (region, frontier) in peer_regions {
                        let merged = progress.regions.entry(region.clone()).or_default();
                        for (origin, sequence) in frontier {
                            let previous = merged.entry(origin.clone()).or_default();
                            *previous = (*previous).max(*sequence);
                        }
                    }
                }
            }
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
    }

    /// Permanently fences all progress and wakes pending requests.
    pub fn fence(&self) {
        // Publish before acquiring the ledger lock. Admissions already holding
        // that lock are fenced below; later admissions cannot create live entries.
        self.fenced.store(true, Ordering::Release);
        if let Ok(mut databases) = self.databases.lock() {
            for progress in databases.values_mut() {
                progress.fenced = true;
            }
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
    }

    /// Discards observations from replaced files while retaining accepted identity.
    pub(crate) fn reset_database_evidence(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<(), ReplicationWaitError> {
        let mut databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get_mut(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced || self.fenced.load(Ordering::Acquire) {
            return Err(ReplicationWaitError::Fenced);
        }
        progress.regional_sequence = 0;
        progress.regional_observed = false;
        progress.regions.clear();
        progress.regional_by_peer.clear();
        progress.regions_by_peer.clear();
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
        Ok(())
    }

    /// Permanently revokes one database assignment and wakes its pending waits.
    pub fn fence_database(&self, scope: DatabaseRouteScope) {
        if let Ok(mut databases) = self.databases.lock()
            && let Some(progress) = databases.get_mut(&scope)
        {
            progress.fenced = true;
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
    }

    /// Waits until the regional physical follower covers a `SQLite` commit sequence.
    ///
    /// # Errors
    /// Returns an admission, fencing, or bounded timeout error.
    pub async fn wait_regional(
        &self,
        scope: DatabaseRouteScope,
        sequence: u64,
        timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait(scope, timeout, |progress| Ok(progress.regional_sequence >= sequence)).await
    }

    /// Tests a pending transport receipt without blocking another database lane.
    pub(crate) fn regional_covers(
        &self,
        scope: DatabaseRouteScope,
        sequence: u64,
    ) -> Result<bool, ReplicationWaitError> {
        let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced {
            return Err(ReplicationWaitError::Fenced);
        }
        Ok(progress.regional_sequence >= sequence)
    }

    /// Waits until every selected Region durably covers an origin sequence.
    ///
    /// # Errors
    /// Returns an admission, selection, fencing, or bounded timeout error.
    pub async fn wait_global(
        &self,
        scope: DatabaseRouteScope,
        origin: &str,
        sequence: u64,
        timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait_uniform(scope, &BTreeMap::from([(origin.to_owned(), sequence)]), timeout).await
    }

    /// Requires every causal dependency to be uniform before a global commit.
    ///
    /// # Errors
    /// Returns an admission, selection, fencing, or bounded timeout error.
    pub async fn wait_uniform(
        &self,
        scope: DatabaseRouteScope,
        frontier: &BTreeMap<String, u64>,
        timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait(scope, timeout, |progress| {
            if progress.selected.len() < 2 {
                return Err(ReplicationWaitError::InsufficientRegions);
            }
            if frontier.keys().any(|origin| !progress.selected.contains(origin)) {
                return Err(ReplicationWaitError::NotAdmitted);
            }
            Ok(progress.selected.iter().all(|region| {
                frontier.iter().all(|(origin, needed)| {
                    progress
                        .regions
                        .get(region)
                        .and_then(|seen| seen.get(origin))
                        .copied()
                        .unwrap_or(0)
                        >= *needed
                })
            }))
        })
        .await
    }

    async fn wait(
        &self,
        scope: DatabaseRouteScope,
        timeout: Duration,
        satisfied: impl Fn(&DatabaseProgress) -> Result<bool, ReplicationWaitError>,
    ) -> Result<(), ReplicationWaitError> {
        let mut changed = self.changed.subscribe();
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            {
                let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
                let progress = databases.get(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
                if progress.fenced {
                    return Err(ReplicationWaitError::Fenced);
                }
                if satisfied(progress)? {
                    return Ok(());
                }
            }
            tokio::time::timeout_at(deadline, changed.changed())
                .await
                .map_err(|_| ReplicationWaitError::Timeout)?
                .map_err(|_| ReplicationWaitError::Fenced)?;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    fn scope() -> DatabaseRouteScope {
        DatabaseRouteScope::try_new(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
        )
        .unwrap()
    }

    #[tokio::test]
    async fn global_wait_requires_every_selected_region_and_dependency() {
        let progress = Arc::new(ReplicationProgress::default());
        progress.admit(scope(), BTreeSet::from(["gru".into(), "sfo".into()])).unwrap();
        let required = BTreeMap::from([("gru".into(), 3), ("sfo".into(), 2)]);
        progress.advance_region(scope(), "gru", &required).unwrap();
        progress.advance_region(scope(), "sfo", &BTreeMap::from([("gru".into(), 3)])).unwrap();
        assert_eq!(
            progress.wait_uniform(scope(), &required, Duration::ZERO).await,
            Err(ReplicationWaitError::Timeout)
        );
        let waiter = {
            let progress = progress.clone();
            let required = required.clone();
            tokio::spawn(async move {
                progress.wait_uniform(scope(), &required, Duration::from_secs(1)).await
            })
        };
        progress.advance_region(scope(), "sfo", &BTreeMap::from([("sfo".into(), 2)])).unwrap();
        assert_eq!(waiter.await.unwrap(), Ok(()));
    }

    #[tokio::test]
    async fn fencing_is_permanent_and_wakes_waiters() {
        let progress = Arc::new(ReplicationProgress::default());
        progress.admit(scope(), BTreeSet::from(["gru".into()])).unwrap();
        let waiter = {
            let progress = progress.clone();
            tokio::spawn(
                async move { progress.wait_regional(scope(), 5, Duration::from_secs(1)).await },
            )
        };
        progress.fence();
        progress.advance_regional(scope(), 100);
        assert_eq!(waiter.await.unwrap(), Err(ReplicationWaitError::Fenced));
        assert_eq!(
            progress.admit(scope(), BTreeSet::from(["gru".into()])),
            Err(ReplicationWaitError::NotAdmitted)
        );
    }

    #[test]
    fn global_fence_rejects_new_assignments_even_from_an_empty_ledger() {
        for initially_admitted in [false, true] {
            let progress = ReplicationProgress::default();
            if initially_admitted {
                progress.admit(scope(), BTreeSet::from(["GRU".into()])).unwrap();
            }
            progress.fence();
            progress.fence();
            let other = DatabaseRouteScope::new(
                terrabase::identity::TenantUlid::generate(),
                terrabase::identity::ProjectUlid::generate(),
                terrabase::identity::DatabaseUlid::generate(),
            );
            assert_eq!(
                progress.admit(other, BTreeSet::from(["GRU".into()])),
                Err(ReplicationWaitError::NotAdmitted)
            );
            assert!(!progress.boundaries().unwrap().contains_key(&other));
            assert!(progress.active_boundaries().unwrap().is_empty());
        }
    }

    #[tokio::test]
    async fn restart_does_not_reconstruct_receipts_from_configuration() {
        let progress = ReplicationProgress::default();
        progress.admit(scope(), BTreeSet::from(["gru".into()])).unwrap();
        assert_eq!(
            progress.wait_regional(scope(), 1, Duration::ZERO).await,
            Err(ReplicationWaitError::Timeout)
        );
        assert_eq!(
            progress.wait_uniform(scope(), &BTreeMap::new(), Duration::ZERO).await,
            Err(ReplicationWaitError::InsufficientRegions)
        );
        assert_eq!(
            progress.advance_region(scope(), "fra", &BTreeMap::new()),
            Err(ReplicationWaitError::NotAdmitted)
        );
    }

    #[test]
    fn batch_rejects_later_invalid_entry_without_partial_progress() {
        let progress = ReplicationProgress::default();
        let other = DatabaseRouteScope::try_new(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
        )
        .unwrap();
        assert!(
            progress
                .admit_many(BTreeMap::from([
                    (scope(), BTreeSet::from(["GRU".into()])),
                    (other, BTreeSet::new()),
                ]))
                .is_err()
        );
        assert_eq!(progress.selected_regions(scope()), Err(ReplicationWaitError::NotAdmitted));
    }

    #[test]
    fn identical_batch_registration_preserves_existing_progress() {
        let progress = ReplicationProgress::default();
        progress.admit(scope(), BTreeSet::from(["GRU".into()])).unwrap();
        progress.advance_regional(scope(), 7);
        progress.admit_many(BTreeMap::from([(scope(), BTreeSet::from(["GRU".into()]))])).unwrap();
        assert_eq!(progress.boundaries().unwrap()[&scope()].regional_sequence, 7);
    }

    #[test]
    fn fencing_one_peer_preserves_surviving_peer_evidence() {
        let progress = ReplicationProgress::default();
        progress.admit(scope(), BTreeSet::from(["GRU".into(), "SFO".into()])).unwrap();
        progress.advance_regional_from_peer(scope(), "local-rf2", 9);
        progress
            .advance_region_from_peer(
                scope(),
                "remote-a",
                "GRU",
                &BTreeMap::from([("GRU".into(), 4)]),
            )
            .unwrap();
        progress
            .advance_region_from_peer(
                scope(),
                "remote-b",
                "SFO",
                &BTreeMap::from([("GRU".into(), 4)]),
            )
            .unwrap();
        progress.fence_peer("remote-a");
        assert_eq!(progress.boundaries().unwrap()[&scope()].regional_sequence, 9);
        assert!(progress.boundaries().unwrap()[&scope()].regions.contains_key("SFO"));
        assert!(!progress.boundaries().unwrap()[&scope()].regions.contains_key("GRU"));
    }

    #[test]
    fn batch_rejects_conflicting_and_fenced_existing_assignments() {
        let progress = ReplicationProgress::default();
        progress.admit(scope(), BTreeSet::from(["GRU".into()])).unwrap();
        assert!(
            progress
                .admit_many(BTreeMap::from([(scope(), BTreeSet::from(["SFO".into()]))]))
                .is_err()
        );
        progress.fence_database(scope());
        assert!(
            progress
                .admit_many(BTreeMap::from([(scope(), BTreeSet::from(["GRU".into()]))]))
                .is_err()
        );
    }

    #[test]
    fn batch_rejects_new_scope_capacity_overflow_atomically() {
        let progress = ReplicationProgress::default();
        let mut entries = BTreeMap::new();
        for _ in 0..=terrabase::member::MAX_DATABASES {
            let scope = DatabaseRouteScope::new(
                terrabase::identity::TenantUlid::generate(),
                terrabase::identity::ProjectUlid::generate(),
                terrabase::identity::DatabaseUlid::generate(),
            );
            entries.insert(scope, BTreeSet::from(["GRU".into()]));
        }
        assert!(progress.admit_many(entries).is_err());
        assert!(progress.boundaries().unwrap().is_empty());
    }
}
