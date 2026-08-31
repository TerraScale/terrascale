//! The Storage Tier Mover's typed administration (P4).
//!
//! This module is the public seam the frozen storage-tiering contract's
//! operator controls ride: configure, explain, dry-run, run, pause,
//! resume, inspect — every control typed, every run receipted. No raw
//! object-store credentials and no generic byte escape hatch cross this
//! seam; the mover's object store is engine-owned (the location's local
//! content-addressed store today, the remote cold tier when that
//! landing ships).
//!
//! The mover itself is one bounded pass: it demotes the coldest segment
//! that exceeds the policy's `NVMe` budget, never a pinned one, verifies
//! the object read-after-write, publishes the next tier-manifest epoch,
//! and only then reclaims the `NVMe` copy. There is no background mover
//! loop yet — `run` executes one pass on the caller's thread, and
//! pause/resume gate it; the continuous scheduler lands with the
//! cold-tier qualification and owns the `cancel` control.

use std::sync::{Arc, Mutex};

use crate::engine::{Engine, StorageState};
use crate::storage::lsm::tier::{self, MoveDecision, MoverOutcome, TierMovePolicy};

/// One configured mover policy — the exact bytes a run's receipt names.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TierPolicy {
    policy_id: String,
    version: u32,
    nvme_budget_bytes: u64,
    min_residence_moves: u32,
    promotion_threshold: u32,
    pinned_segment_ids: Vec<u64>,
}

impl TierPolicy {
    /// Creates a policy after checking its bounded fields.
    ///
    /// # Errors
    ///
    /// Returns [`TierAdminError::InvalidPolicy`] for an empty identity,
    /// a zero budget, or a version of zero.
    pub fn try_new(
        policy_id: &str,
        version: u32,
        nvme_budget_bytes: u64,
        min_residence_moves: u32,
        promotion_threshold: u32,
        pinned_segment_ids: Vec<u64>,
    ) -> Result<Self, TierAdminError> {
        if policy_id.trim().is_empty()
            || policy_id.len() > 128
            || policy_id.chars().any(char::is_control)
        {
            return Err(TierAdminError::InvalidPolicy);
        }
        if version == 0 || nvme_budget_bytes == 0 {
            return Err(TierAdminError::InvalidPolicy);
        }
        Ok(Self {
            policy_id: policy_id.to_owned(),
            version,
            nvme_budget_bytes,
            min_residence_moves,
            promotion_threshold,
            pinned_segment_ids,
        })
    }

    /// Returns the policy's stable identity.
    #[must_use]
    pub fn policy_id(&self) -> &str {
        &self.policy_id
    }

    /// Returns the policy's version.
    #[must_use]
    pub const fn version(&self) -> u32 {
        self.version
    }

    /// Returns the `NVMe` byte budget demotion starts beyond.
    #[must_use]
    pub const fn nvme_budget_bytes(&self) -> u64 {
        self.nvme_budget_bytes
    }

    /// Returns the minimum residence move count used by the policy's
    /// hysteresis rule.
    #[must_use]
    pub const fn min_residence_moves(&self) -> u32 {
        self.min_residence_moves
    }

    /// Returns the promotion threshold used by the policy.
    #[must_use]
    pub const fn promotion_threshold(&self) -> u32 {
        self.promotion_threshold
    }

    /// Returns the pinned segments that never demote.
    #[must_use]
    pub fn pinned_segment_ids(&self) -> &[u64] {
        &self.pinned_segment_ids
    }

    fn as_move_policy(&self, dry_run: bool) -> TierMovePolicy {
        TierMovePolicy {
            policy_id: self.policy_id.clone(),
            version: self.version,
            nvme_budget_bytes: self.nvme_budget_bytes,
            min_residence_moves: self.min_residence_moves,
            promotion_threshold: self.promotion_threshold,
            pinned_segment_ids: self.pinned_segment_ids.clone(),
            dry_run,
        }
    }
}

/// The decision one mover run made — the frozen vocabulary of the
/// storage-tiering contract §3.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TierDecision {
    /// The segment moved from the object-store tier back to `NVMe`.
    Promote,
    /// The segment moved to the object-store tier.
    Demote,
    /// The pass split one range across tiers.
    Split,
    /// Nothing moved.
    None,
    /// The policy refused the move (residence, pin, or budget).
    Refused,
}

/// One machine-readable reason a decision weighed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TierMoveReason {
    fact: String,
    observed: String,
}

impl TierMoveReason {
    /// The fact the policy weighed.
    #[must_use]
    pub fn fact(&self) -> &str {
        &self.fact
    }

    /// The value observed for that fact.
    #[must_use]
    pub fn observed(&self) -> &str {
        &self.observed
    }
}

/// One tier-move run's receipt — the exact policy, the decision, the
/// ordered reasons, and the manifest epochs around the run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TierMoveRunReceipt {
    policy_id: String,
    policy_version: u32,
    decision: TierDecision,
    reasons: Vec<TierMoveReason>,
    segment_id: u64,
    bytes: u64,
    manifest_before: u64,
    manifest_after: u64,
    dry_run: bool,
    manifest_accepted: bool,
}

impl TierMoveRunReceipt {
    /// Returns the policy identity the run executed.
    #[must_use]
    pub fn policy_id(&self) -> &str {
        &self.policy_id
    }

    /// Returns the policy version the run executed.
    #[must_use]
    pub const fn policy_version(&self) -> u32 {
        self.policy_version
    }

    /// Returns the run's decision.
    #[must_use]
    pub const fn decision(&self) -> TierDecision {
        self.decision
    }

    /// Returns the ordered reasons the policy weighed.
    #[must_use]
    pub fn reasons(&self) -> &[TierMoveReason] {
        &self.reasons
    }

    /// Returns the segment the run considered.
    #[must_use]
    pub const fn segment_id(&self) -> u64 {
        self.segment_id
    }

    /// Returns the bytes the run transferred (zero for dry runs and
    /// refusals).
    #[must_use]
    pub const fn bytes(&self) -> u64 {
        self.bytes
    }

    /// Returns whether the run was a dry run.
    #[must_use]
    pub const fn dry_run(&self) -> bool {
        self.dry_run
    }

    /// Returns the manifest epoch before the run.
    #[must_use]
    pub const fn manifest_epoch_before(&self) -> u64 {
        self.manifest_before
    }

    /// Returns the manifest epoch after the run.
    #[must_use]
    pub const fn manifest_epoch_after(&self) -> u64 {
        self.manifest_after
    }

    /// Returns whether the published manifest was accepted.
    #[must_use]
    pub const fn manifest_accepted(&self) -> bool {
        self.manifest_accepted
    }
}

/// The tier administration's view of one engine's tier state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TierStateView {
    configured: bool,
    paused: bool,
    manifest_epoch: u64,
    object_count: usize,
    nvme_bytes: u64,
}

impl TierStateView {
    /// Whether a policy is configured.
    #[must_use]
    pub const fn configured(&self) -> bool {
        self.configured
    }

    /// Whether runs are paused.
    #[must_use]
    pub const fn paused(&self) -> bool {
        self.paused
    }

    /// The published tier-manifest epoch.
    #[must_use]
    pub const fn manifest_epoch(&self) -> u64 {
        self.manifest_epoch
    }

    /// The verified objects the manifest names.
    #[must_use]
    pub const fn object_count(&self) -> usize {
        self.object_count
    }

    /// The bytes resident on `NVMe`.
    #[must_use]
    pub const fn nvme_bytes(&self) -> u64 {
        self.nvme_bytes
    }
}

/// The typed failures of tier administration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TierAdminError {
    /// The policy fields were invalid.
    InvalidPolicy,
    /// No policy is configured.
    NotConfigured,
    /// Runs are paused; resume first.
    Paused,
    /// This backend carries no tiers.
    UnsupportedBackend,
    /// The mover run failed; the receipt names the failed stage (the
    /// candidate, the decision, the untouched manifest epochs).
    MoveFailed(TierMoveRunReceipt),
}

/// The engine-level tier control state.
#[derive(Clone, Debug, Eq, PartialEq, Default)]
pub(crate) struct TierControlState {
    policy: Option<TierPolicy>,
    paused: bool,
}

/// The typed administration handle of one Engine's Storage Tier Mover.
pub struct TierAdministration {
    storage: Arc<Mutex<StorageState>>,
    control: Arc<Mutex<TierControlState>>,
    home_region: String,
}

impl TierAdministration {
    pub(crate) fn new(engine: &Engine) -> Self {
        Self {
            storage: Arc::clone(&engine.storage),
            control: Arc::clone(&engine.tier_control),
            // The single-member engine is its own home region until the
            // cluster-mode landing names peers.
            home_region: "local".to_owned(),
        }
    }

    /// Configures the mover's policy, replacing any prior one and
    /// clearing the pause (a fresh policy is fresh control).
    ///
    /// # Errors
    ///
    /// Returns [`TierAdminError::InvalidPolicy`] for invalid fields.
    pub fn configure(&self, policy: TierPolicy) -> Result<TierPolicy, TierAdminError> {
        TierPolicy::try_new(
            policy.policy_id(),
            policy.version(),
            policy.nvme_budget_bytes(),
            policy.min_residence_moves(),
            policy.promotion_threshold(),
            policy.pinned_segment_ids().to_vec(),
        )?;
        let mut control = self.lock_control();
        control.paused = false;
        control.policy = Some(policy.clone());
        Ok(policy)
    }

    /// Returns the configured policy, if any.
    #[must_use]
    pub fn policy(&self) -> Option<TierPolicy> {
        self.lock_control().policy.clone()
    }

    /// Pauses mover runs. Returns the new paused state.
    #[must_use]
    pub fn pause(&self) -> bool {
        let mut control = self.lock_control();
        control.paused = true;
        control.paused
    }

    /// Resumes mover runs. Returns the new paused state.
    #[must_use]
    pub fn resume(&self) -> bool {
        let mut control = self.lock_control();
        control.paused = false;
        control.paused
    }

    /// Explains the next run without transferring anything: the receipt
    /// of a dry pass, decision and ordered reasons included. A dry pass
    /// is read-only and runs even while paused.
    ///
    /// # Errors
    ///
    /// Returns [`TierAdminError::NotConfigured`] with no policy and
    /// [`TierAdminError::UnsupportedBackend`] on a tierless backend.
    pub fn dry_run(&self) -> Result<TierMoveRunReceipt, TierAdminError> {
        let control = self.lock_control();
        let Some(policy) = control.policy.clone() else {
            return Err(TierAdminError::NotConfigured);
        };
        drop(control);
        self.pass(&policy, true)
    }

    /// Runs one bounded mover pass under the configured policy. A
    /// paused control refuses.
    ///
    /// # Errors
    ///
    /// Returns [`TierAdminError::NotConfigured`], [`TierAdminError::Paused`],
    /// [`TierAdminError::UnsupportedBackend`], or
    /// [`TierAdminError::MoveFailed`] when the pass failed — carrying
    /// the receipt that names the failed stage.
    pub fn run(&self) -> Result<TierMoveRunReceipt, TierAdminError> {
        let control = self.lock_control();
        let Some(policy) = control.policy.clone() else {
            return Err(TierAdminError::NotConfigured);
        };
        if control.paused {
            return Err(TierAdminError::Paused);
        }
        drop(control);
        self.pass(&policy, false)
    }

    /// Inspects the tier state: the control state plus the backend's
    /// tier snapshot (`0` epoch and no objects on a tierless backend).
    #[must_use]
    pub fn inspect(&self) -> TierStateView {
        let control = self.lock_control();
        let configured = control.policy.is_some();
        let paused = control.paused;
        drop(control);
        let snapshot =
            self.storage.lock().map(|storage| storage.port.tier_snapshot()).ok().flatten();
        TierStateView {
            configured,
            paused,
            manifest_epoch: snapshot.map_or(0, |snapshot| snapshot.manifest_epoch),
            object_count: snapshot.map_or(0, |snapshot| snapshot.object_count),
            nvme_bytes: snapshot.map_or(0, |snapshot| snapshot.nvme_bytes),
        }
    }

    fn lock_control(&self) -> std::sync::MutexGuard<'_, TierControlState> {
        self.control.lock().expect("the tier control state is not poisoned")
    }

    fn pass(
        &self,
        policy: &TierPolicy,
        dry_run: bool,
    ) -> Result<TierMoveRunReceipt, TierAdminError> {
        let move_policy = policy.as_move_policy(dry_run);
        let outcome = {
            let mut storage =
                self.storage.lock().map_err(|_| TierAdminError::UnsupportedBackend)?;
            storage
                .port
                .mover_pass(&move_policy, &self.home_region)
                .map_err(|_| TierAdminError::UnsupportedBackend)?
        };
        match outcome {
            MoverOutcome::Completed(receipt) => Ok(public_receipt(&receipt)),
            MoverOutcome::Failed(receipt, _error) => {
                Err(TierAdminError::MoveFailed(public_receipt(&receipt)))
            }
        }
    }
}

/// Maps one internal receipt onto its public mirror.
fn public_receipt(receipt: &tier::TierMoveReceipt) -> TierMoveRunReceipt {
    TierMoveRunReceipt {
        policy_id: receipt.policy_id.clone(),
        policy_version: receipt.policy_version,
        decision: match receipt.decision {
            MoveDecision::Promote => TierDecision::Promote,
            MoveDecision::Demote => TierDecision::Demote,
            MoveDecision::None => TierDecision::None,
            MoveDecision::Refused => TierDecision::Refused,
        },
        reasons: receipt
            .reasons
            .iter()
            .map(|reason| TierMoveReason {
                fact: reason.fact.clone(),
                observed: reason.observed.clone(),
            })
            .collect(),
        segment_id: receipt.segment_id,
        bytes: receipt.bytes,
        manifest_before: receipt.manifest_before,
        manifest_after: receipt.manifest_after,
        dry_run: receipt.dry_run,
        manifest_accepted: receipt.manifest_accepted,
    }
}

#[cfg(test)]
mod tests {
    use super::{TierAdminError, TierDecision, TierMoveReason, TierPolicy};
    use crate::engine::{Engine, EngineConfig};

    fn engine(tag: &str) -> Engine {
        let dir = std::env::temp_dir().join(format!(
            "terrabase-tiers-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        // The tier mover is the LSM engine's cold-segment demotion surface
        // (TB-21 deletes it with that engine); the `SQLite` backend has no
        // tiers and answers the typed refusal.
        Engine::open(
            EngineConfig::durable(&dir)
                .expect("durable")
                .with_storage_backend(crate::engine::StorageBackend::Lsm),
        )
        .expect("opens")
    }

    fn policy() -> TierPolicy {
        TierPolicy::try_new("prod", 3, 64 << 20, 1, 2, Vec::new()).expect("valid policy")
    }

    #[test]
    fn configure_records_the_policy_and_clears_the_pause() {
        let engine = engine("configure");
        let tiers = engine.tiers();
        assert!(tiers.policy().is_none());
        assert!(tiers.pause());
        assert!(tiers.inspect().paused());

        let configured = tiers.configure(policy()).expect("configures");
        assert_eq!(configured.policy_id(), "prod");
        assert_eq!(configured.version(), 3);
        assert_eq!(configured.min_residence_moves(), 1);
        assert_eq!(configured.promotion_threshold(), 2);
        assert!(!tiers.inspect().paused(), "configure is fresh control");
        assert_eq!(tiers.policy(), Some(policy()));
    }

    #[test]
    fn invalid_policies_are_refused_typed() {
        assert_eq!(
            TierPolicy::try_new("", 1, 1024, 1, 2, Vec::new()).err(),
            Some(TierAdminError::InvalidPolicy)
        );
        assert_eq!(
            TierPolicy::try_new("   ", 1, 1024, 1, 2, Vec::new()).err(),
            Some(TierAdminError::InvalidPolicy)
        );
        assert_eq!(
            TierPolicy::try_new("prod\nreceipt", 1, 1024, 1, 2, Vec::new()).err(),
            Some(TierAdminError::InvalidPolicy)
        );
        assert_eq!(
            TierPolicy::try_new("x", 0, 1024, 1, 2, Vec::new()).err(),
            Some(TierAdminError::InvalidPolicy)
        );
        assert_eq!(
            TierPolicy::try_new("x", 1, 0, 1, 2, Vec::new()).err(),
            Some(TierAdminError::InvalidPolicy)
        );
    }

    #[test]
    fn unconfigured_and_paused_runs_refuse_typed() {
        let engine = engine("gates");
        let tiers = engine.tiers();
        assert_eq!(tiers.dry_run().err(), Some(TierAdminError::NotConfigured));
        assert_eq!(tiers.run().err(), Some(TierAdminError::NotConfigured));

        tiers.configure(policy()).expect("configures");
        assert!(tiers.dry_run().expect("dry runs").dry_run());

        assert!(tiers.pause());
        assert_eq!(tiers.run().err(), Some(TierAdminError::Paused));
        // A dry pass is read-only: it runs even while paused.
        assert!(tiers.dry_run().expect("dry pass while paused").dry_run());

        assert!(!tiers.resume());
        // A fresh engine sits below the budget: the resumed run receipts
        // a no-op decision rather than a refusal.
        assert_eq!(tiers.run().expect("runs again").decision(), TierDecision::None);
    }

    #[test]
    fn a_dry_pass_receipts_its_reasons_and_the_decision() {
        let engine = engine("receipt");
        let tiers = engine.tiers();
        tiers.configure(policy()).expect("configures");
        let receipt = tiers.dry_run().expect("the dry pass receipts");
        // A fresh engine sits far below the 64 MiB budget: nothing moves.
        assert_eq!(receipt.decision(), TierDecision::None);
        assert_eq!(receipt.bytes(), 0);
        assert!(!receipt.manifest_accepted());
        let facts: Vec<&str> = receipt.reasons().iter().map(TierMoveReason::fact).collect();
        assert!(facts.contains(&"nvme_resident_bytes"));
        assert!(facts.contains(&"nvme_budget_bytes"));
        // The inspect view agrees with the receipt's facts.
        let view = tiers.inspect();
        assert!(view.configured());
        assert!(!view.paused());
        assert_eq!(view.manifest_epoch(), receipt.manifest_epoch_after());
    }
}
