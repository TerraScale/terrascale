//! Receipt-backed durability progress shared by the production transport and API.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Mutex;
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
    regions: BTreeMap<String, BTreeMap<String, u64>>,
    fenced: bool,
}

/// Bounded per-database progress derived exclusively from durable peer receipts.
///
/// Process restart clears these observations. Drivers recover them through
/// authenticated checkpoint exchange before new waits can succeed. No pending
/// operation queue is retained here; each request owns its bounded wait.
pub struct ReplicationProgress {
    databases: Mutex<BTreeMap<DatabaseRouteScope, DatabaseProgress>>,
    changed: watch::Sender<u64>,
}

impl Default for ReplicationProgress {
    fn default() -> Self {
        Self { databases: Mutex::new(BTreeMap::new()), changed: watch::channel(0).0 }
    }
}

impl ReplicationProgress {
    /// Registers immutable selected Regions from an accepted assignment.
    ///
    /// # Errors
    /// Rejects an empty/oversized selection and replacement of an assignment.
    pub fn admit(
        &self,
        scope: DatabaseRouteScope,
        selected: BTreeSet<String>,
    ) -> Result<(), ReplicationWaitError> {
        if selected.is_empty() || selected.len() > 20 || selected.iter().any(String::is_empty) {
            return Err(ReplicationWaitError::NotAdmitted);
        }
        let mut databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        if databases.contains_key(&scope) {
            return Err(ReplicationWaitError::NotAdmitted);
        }
        databases.insert(scope, DatabaseProgress {
            selected, regional_sequence: 0, regions: BTreeMap::new(), fenced: false,
        });
        Ok(())
    }

    /// Advances the SQLite boundary covered by a verified local RF=2 receipt.
    /// This is called only after the physical driver proves the entire boundary.
    pub(crate) fn advance_regional(&self, scope: DatabaseRouteScope, sequence: u64) {
        if let Ok(mut databases) = self.databases.lock()
            && let Some(progress) = databases.get_mut(&scope)
            && !progress.fenced
        {
            progress.regional_sequence = progress.regional_sequence.max(sequence);
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
        let mut databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
        let progress = databases.get_mut(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
        if progress.fenced { return Err(ReplicationWaitError::Fenced); }
        if !progress.selected.contains(region)
            || frontier.keys().any(|origin| !progress.selected.contains(origin))
        { return Err(ReplicationWaitError::NotAdmitted); }
        let observed = progress.regions.entry(region.to_owned()).or_default();
        for (origin, sequence) in frontier {
            let previous = observed.entry(origin.clone()).or_default();
            *previous = (*previous).max(*sequence);
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
        Ok(())
    }

    /// Permanently fences all progress and wakes pending requests.
    pub fn fence(&self) {
        if let Ok(mut databases) = self.databases.lock() {
            for progress in databases.values_mut() { progress.fenced = true; }
        }
        self.changed.send_modify(|generation| *generation = generation.wrapping_add(1));
    }

    /// Waits until the regional physical follower covers a SQLite commit sequence.
    ///
    /// # Errors
    /// Returns an admission, fencing, or bounded timeout error.
    pub async fn wait_regional(
        &self, scope: DatabaseRouteScope, sequence: u64, timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait(scope, timeout, |progress| Ok(progress.regional_sequence >= sequence)).await
    }

    /// Waits until every selected Region durably covers an origin sequence.
    ///
    /// # Errors
    /// Returns an admission, selection, fencing, or bounded timeout error.
    pub async fn wait_global(
        &self, scope: DatabaseRouteScope, origin: &str, sequence: u64, timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait_uniform(scope, &BTreeMap::from([(origin.to_owned(), sequence)]), timeout).await
    }

    /// Requires every causal dependency to be uniform before a global commit.
    ///
    /// # Errors
    /// Returns an admission, selection, fencing, or bounded timeout error.
    pub async fn wait_uniform(
        &self, scope: DatabaseRouteScope, frontier: &BTreeMap<String, u64>, timeout: Duration,
    ) -> Result<(), ReplicationWaitError> {
        self.wait(scope, timeout, |progress| {
            if progress.selected.len() < 2 { return Err(ReplicationWaitError::InsufficientRegions); }
            if frontier.keys().any(|origin| !progress.selected.contains(origin)) {
                return Err(ReplicationWaitError::NotAdmitted);
            }
            Ok(progress.selected.iter().all(|region| frontier.iter().all(|(origin, needed)| {
                progress.regions.get(region).and_then(|seen| seen.get(origin)).copied()
                    .unwrap_or(0) >= *needed
            })))
        }).await
    }

    async fn wait(
        &self, scope: DatabaseRouteScope, timeout: Duration,
        satisfied: impl Fn(&DatabaseProgress) -> Result<bool, ReplicationWaitError>,
    ) -> Result<(), ReplicationWaitError> {
        let mut changed = self.changed.subscribe();
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            {
                let databases = self.databases.lock().map_err(|_| ReplicationWaitError::Fenced)?;
                let progress = databases.get(&scope).ok_or(ReplicationWaitError::NotAdmitted)?;
                if progress.fenced { return Err(ReplicationWaitError::Fenced); }
                if satisfied(progress)? { return Ok(()); }
            }
            tokio::time::timeout_at(deadline, changed.changed()).await
                .map_err(|_| ReplicationWaitError::Timeout)?
                .map_err(|_| ReplicationWaitError::Fenced)?;
        }
    }
}
