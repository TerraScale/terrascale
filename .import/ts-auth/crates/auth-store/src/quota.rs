//! Per-instance, CAS-serialized admission and plan-cap gate.
//!
//! A seat claim is still the durable owner-to-seat record. This compact ledger
//! counts admitted owners (including grandfathered high-numbered seats) and
//! only keeps unfinished operations, so large plans do not put every user in
//! one document. An uncertain write keeps its owner counted until a fenced
//! repair can prove that neither a user nor a seat claim can appear later.

use std::collections::{BTreeMap, HashSet};

use auth_contracts::models::{Subscription, User};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use super::{AuthStore, Predicate, StoreError, StoreResult, unique_claim_id};

const LEDGER_COLLECTION: &str = "quota_admissions";
const TRANSITION_IDS_COLLECTION: &str = "quota_transition_ids";
const MAX_IN_FLIGHT: usize = 256;
const MAX_CAS_ATTEMPTS: usize = 16;

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct QuotaLedger {
    instance_id: String,
    cap: u32,
    count: u64,
    #[serde(default)]
    in_flight: BTreeMap<String, InFlight>,
    #[serde(default)]
    plan_generation: u64,
    #[serde(default)]
    plan_intent: Option<QuotaCapTransition>,
    #[serde(default)]
    last_plan_transition: Option<QuotaCapTransition>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(tag = "phase", rename_all = "snake_case")]
enum InFlight {
    Reserving { limit: u32 },
    Releasing { seat: u32 },
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuotaCapTransition {
    pub id: String,
    pub generation: u64,
    pub target_cap: u32,
    /// Exact row the billing writer intended to commit. Retain it while an
    /// ambiguous write may still land, even if a later Stripe GET has changed.
    pub expected_subscription: Subscription,
    /// `None` means a create-only write; otherwise this is the ETag pinned
    /// before the billing caller prepared its subscription CAS.
    pub source_version: Option<String>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct QuotaTransitionIdClaim {
    instance_id: String,
    transition_id: String,
    target_cap: u32,
    expected_subscription: Subscription,
    source_version: Option<String>,
}

impl AuthStore {
    /// A plan transition ID remains bound to exactly one candidate even after
    /// later transitions replace the ledger's compact last-transition slot.
    async fn claim_quota_transition_id(
        &self,
        instance_id: &str,
        transition_id: &str,
        target_cap: u32,
        expected_subscription: &Subscription,
        source_version: Option<&str>,
    ) -> StoreResult<bool> {
        let id = unique_claim_id(
            "quota-transition",
            &format!("{instance_id}:{transition_id}"),
        );
        let expected = QuotaTransitionIdClaim {
            instance_id: instance_id.to_owned(),
            transition_id: transition_id.to_owned(),
            target_cap,
            expected_subscription: expected_subscription.clone(),
            source_version: source_version.map(str::to_owned),
        };
        let expected_value = serde_json::to_value(&expected)?;
        if let Some(existing) = self
            .get::<QuotaTransitionIdClaim>(TRANSITION_IDS_COLLECTION, &id)
            .await?
        {
            return if serde_json::to_value(existing)? == expected_value {
                Ok(false)
            } else {
                Err(StoreError::Precondition)
            };
        }
        match self.create(TRANSITION_IDS_COLLECTION, &id, &expected).await {
            Ok(()) => Ok(true),
            Err(error) => {
                let observed = self
                    .get::<QuotaTransitionIdClaim>(TRANSITION_IDS_COLLECTION, &id)
                    .await?;
                if observed
                    .is_some_and(|claim| serde_json::to_value(claim).ok() == Some(expected_value))
                {
                    Ok(false)
                } else if error.is_conflict() {
                    Err(StoreError::Precondition)
                } else {
                    Err(error)
                }
            }
        }
    }

    /// Authoritative admission state, including in-flight reservations and
    /// a pre-lowered plan gate. Initializing the ledger counts existing users
    /// and seat claims before answering the first request.
    pub async fn quota_admission_status(
        &self,
        instance_id: &str,
        initial_cap: u32,
    ) -> StoreResult<(u64, u32)> {
        let (ledger, _) = self
            .read_or_create_quota_ledger(instance_id, initial_cap)
            .await?;
        if ledger.instance_id != instance_id {
            return Err(StoreError::Precondition);
        }
        Ok((ledger.count, ledger.cap))
    }

    async fn quota_owner_union(&self, instance_id: &str) -> StoreResult<HashSet<String>> {
        let mut owners = HashSet::new();
        let users_filter = [Predicate::equal("instanceId", instance_id)?];
        let mut after: Option<String> = None;
        loop {
            let (users, next) = self
                .query_page::<User>("users", &users_filter, 1_000, after.as_deref())
                .await?;
            owners.extend(
                users
                    .into_iter()
                    .filter_map(|(user, _)| (!user.deleted).then_some(user.id)),
            );
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => after = Some(cursor),
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "quota user cursor did not advance".into(),
                    ));
                }
                None => break,
            }
        }

        let claims_filter = [
            Predicate::equal("ownerType", "quota-seat")?,
            Predicate::equal("instanceId", instance_id)?,
        ];
        after = None;
        loop {
            let (claims, next) = self
                .query_page::<Value>("uniques", &claims_filter, 1_000, after.as_deref())
                .await?;
            for (claim, _) in claims {
                let owner = claim
                    .get("ownerId")
                    .and_then(Value::as_str)
                    .filter(|owner| !owner.is_empty())
                    .ok_or_else(|| {
                        StoreError::InvalidConfiguration("quota seat claim has no owner".into())
                    })?;
                owners.insert(owner.to_owned());
            }
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => after = Some(cursor),
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "quota claim cursor did not advance".into(),
                    ));
                }
                None => break,
            }
        }
        Ok(owners)
    }

    pub(super) async fn read_or_create_quota_ledger(
        &self,
        instance_id: &str,
        initial_cap: u32,
    ) -> StoreResult<(QuotaLedger, String)> {
        if instance_id.is_empty() || initial_cap == 0 {
            return Err(StoreError::InvalidConfiguration(
                "invalid quota ledger scope or cap".into(),
            ));
        }
        if let Some((ledger, version)) = self
            .get_versioned::<QuotaLedger>(LEDGER_COLLECTION, instance_id)
            .await?
        {
            return Ok((ledger, version));
        }
        let owners = self.quota_owner_union(instance_id).await?;
        let ledger = QuotaLedger {
            instance_id: instance_id.to_owned(),
            cap: initial_cap,
            count: owners.len().try_into().map_err(|_| {
                StoreError::InvalidConfiguration("quota owner count overflow".into())
            })?,
            in_flight: BTreeMap::new(),
            plan_generation: 0,
            plan_intent: None,
            last_plan_transition: None,
        };
        match self.create(LEDGER_COLLECTION, instance_id, &ledger).await {
            Ok(()) => {}
            Err(error) if error.is_conflict() => {}
            Err(error) => {
                // A failed create can commit after the response. Keep the
                // ambiguity, but accept a confirmed same-scope ledger.
                if let Some(found) = self
                    .get_versioned::<QuotaLedger>(LEDGER_COLLECTION, instance_id)
                    .await?
                {
                    return Ok(found);
                }
                return Err(error);
            }
        }
        self.get_versioned::<QuotaLedger>(LEDGER_COLLECTION, instance_id)
            .await?
            .ok_or(StoreError::Precondition)
    }

    async fn quota_ledger(&self, instance_id: &str) -> StoreResult<(QuotaLedger, String)> {
        self.get_versioned::<QuotaLedger>(LEDGER_COLLECTION, instance_id)
            .await?
            .ok_or(StoreError::Precondition)
    }

    /// Reserve capacity before choosing a seat. The returned limit is pinned
    /// for this owner even if a later plan transition lowers the cap.
    pub(crate) async fn reserve_quota_admission(
        &self,
        instance_id: &str,
        initial_cap: u32,
        owner_id: &str,
    ) -> StoreResult<u32> {
        if owner_id.is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "quota owner is required".into(),
            ));
        }
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self
                .read_or_create_quota_ledger(instance_id, initial_cap)
                .await?;
            if ledger.instance_id != instance_id {
                return Err(StoreError::Precondition);
            }
            match ledger.in_flight.get(owner_id) {
                Some(InFlight::Reserving { limit }) => return Ok(*limit),
                Some(InFlight::Releasing { .. }) => return Err(StoreError::Precondition),
                None => {}
            }
            if ledger.count >= u64::from(ledger.cap) {
                return Err(StoreError::QuotaExceeded);
            }
            if ledger.in_flight.len() >= MAX_IN_FLIGHT {
                return Err(StoreError::Contention);
            }
            let limit = ledger.cap;
            ledger.count = ledger
                .count
                .checked_add(1)
                .ok_or_else(|| StoreError::InvalidConfiguration("quota counter overflow".into()))?;
            ledger
                .in_flight
                .insert(owner_id.to_owned(), InFlight::Reserving { limit });
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(limit),
                Ok(false) => {}
                Err(error) => {
                    if let Ok((current, _)) = self.quota_ledger(instance_id).await
                        && let Some(InFlight::Reserving { limit }) = current.in_flight.get(owner_id)
                    {
                        return Ok(*limit);
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Remove the in-flight marker only after the exact User is durable.
    pub(crate) async fn finalize_quota_admission(
        &self,
        instance_id: &str,
        owner_id: &str,
    ) -> StoreResult<()> {
        let user = self
            .get::<User>("users", owner_id)
            .await?
            .ok_or(StoreError::Precondition)?;
        if user.instance_id != instance_id || user.quota_seat.is_none() {
            return Err(StoreError::Precondition);
        }
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            match ledger.in_flight.get(owner_id) {
                None => return Ok(()),
                Some(InFlight::Releasing { .. }) => return Err(StoreError::Precondition),
                Some(InFlight::Reserving { .. }) => {}
            }
            ledger.in_flight.remove(owner_id);
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(()),
                Ok(false) => {}
                Err(error) => {
                    if self
                        .quota_ledger(instance_id)
                        .await?
                        .0
                        .in_flight
                        .contains_key(owner_id)
                    {
                        return Err(error);
                    }
                    return Ok(());
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Cancel only after every attempted seat create ended in a definitive
    /// conflict, and no user or own claim was observed.
    pub(crate) async fn cancel_quota_admission(
        &self,
        instance_id: &str,
        owner_id: &str,
    ) -> StoreResult<()> {
        if self.get::<User>("users", owner_id).await?.is_some() {
            return Err(StoreError::Precondition);
        }
        let claims = self.quota_claims_for_owner(instance_id, owner_id).await?;
        if !claims.is_empty() {
            return Err(StoreError::Precondition);
        }
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            match ledger.in_flight.get(owner_id) {
                None => return Ok(()),
                Some(InFlight::Releasing { .. }) => return Err(StoreError::Precondition),
                Some(InFlight::Reserving { .. }) => {}
            }
            ledger.in_flight.remove(owner_id);
            ledger.count = ledger
                .count
                .checked_sub(1)
                .ok_or(StoreError::Precondition)?;
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(()),
                Ok(false) => {}
                Err(error) => return Err(error),
            }
        }
        Err(StoreError::Contention)
    }

    pub(super) async fn quota_claims_for_owner(
        &self,
        instance_id: &str,
        owner_id: &str,
    ) -> StoreResult<Vec<Value>> {
        let predicates = [
            Predicate::equal("ownerType", "quota-seat")?,
            Predicate::equal("instanceId", instance_id)?,
            Predicate::equal("ownerId", owner_id)?,
        ];
        self.query_all("uniques", &predicates, 100).await
    }

    /// Mark a release in the gate, conditionally remove the owned seat, then
    /// decrement exactly once. An interrupted release remains counted.
    pub(crate) async fn release_quota_admission(
        &self,
        instance_id: &str,
        seat: u32,
        owner_id: &str,
    ) -> StoreResult<bool> {
        if instance_id.is_empty() || owner_id.is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "quota release scope is required".into(),
            ));
        }
        let user = self.get::<User>("users", owner_id).await?;
        if user
            .as_ref()
            .is_some_and(|user| user.instance_id != instance_id || !user.deleted)
        {
            return Err(StoreError::Precondition);
        }
        let claim_id = unique_claim_id("quota-seat", &format!("{instance_id}:{seat}"));
        let mut marker_ready = false;
        for _ in 0..MAX_CAS_ATTEMPTS {
            // Pin the gate version before observing the seat. If another
            // releaser removes both claim and marker after this read, our CAS
            // must lose; we cannot re-create a marker from a stale claim.
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            let (claim, _) = match self.get_versioned::<Value>("uniques", &claim_id).await? {
                Some(found) => found,
                None => {
                    if matches!(
                        ledger.in_flight.get(owner_id),
                        Some(InFlight::Reserving { .. })
                    ) {
                        self.cancel_quota_admission(instance_id, owner_id).await?;
                        return Ok(true);
                    }
                    if !matches!(ledger.in_flight.get(owner_id), Some(InFlight::Releasing { seat: marked }) if *marked == seat)
                    {
                        return Ok(false);
                    }
                    marker_ready = true;
                    break;
                }
            };
            if claim.get("ownerId").and_then(Value::as_str) != Some(owner_id)
                || claim.get("instanceId").and_then(Value::as_str) != Some(instance_id)
                || claim.get("seat").and_then(Value::as_u64) != Some(u64::from(seat))
            {
                // The old claim may already have been removed and its slot
                // claimed by a new reserved owner while our release marker
                // still counts the tombstoned old owner. Only that existing
                // marker authorizes finishing the interrupted release.
                if matches!(ledger.in_flight.get(owner_id), Some(InFlight::Releasing { seat: marked }) if *marked == seat)
                {
                    marker_ready = true;
                    break;
                }
                return Ok(false);
            }
            match ledger.in_flight.get(owner_id) {
                Some(InFlight::Releasing { seat: marked }) if *marked == seat => {
                    marker_ready = true;
                    break;
                }
                Some(InFlight::Releasing { .. }) => return Err(StoreError::Precondition),
                _ => {}
            }
            if ledger.in_flight.len() >= MAX_IN_FLIGHT && !ledger.in_flight.contains_key(owner_id) {
                return Err(StoreError::Contention);
            }
            ledger
                .in_flight
                .insert(owner_id.to_owned(), InFlight::Releasing { seat });
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => {
                    marker_ready = true;
                    break;
                }
                Ok(false) => {}
                Err(error) => {
                    let (current, _) = self.quota_ledger(instance_id).await?;
                    if !matches!(current.in_flight.get(owner_id), Some(InFlight::Releasing { seat: marked }) if *marked == seat)
                    {
                        return Err(error);
                    }
                    marker_ready = true;
                    break;
                }
            }
        }
        if !marker_ready {
            return Err(StoreError::Contention);
        }
        // The marker now fences duplicate releases. Never delete a seat that
        // changed owner after our first read.
        if let Some((claim, version)) = self.get_versioned::<Value>("uniques", &claim_id).await? {
            if claim.get("ownerType").and_then(Value::as_str) != Some("quota-seat")
                || claim.get("instanceId").and_then(Value::as_str) != Some(instance_id)
                || claim.get("seat").and_then(Value::as_u64) != Some(u64::from(seat))
            {
                return Err(StoreError::Precondition);
            }
            match claim.get("ownerId").and_then(Value::as_str) {
                Some(owner) if owner == owner_id => {
                    let _ = self
                        .compare_and_delete("uniques", &claim_id, &version)
                        .await?;
                }
                Some(owner) if !owner.is_empty() => {
                    // Another admitted owner reused the slot after this
                    // release removed its original claim. Preserve theirs.
                }
                _ => return Err(StoreError::Precondition),
            }
        }
        if let Some(claim) = self.get::<Value>("uniques", &claim_id).await?
            && (claim.get("ownerType").and_then(Value::as_str) != Some("quota-seat")
                || claim.get("instanceId").and_then(Value::as_str) != Some(instance_id)
                || claim.get("seat").and_then(Value::as_u64) != Some(u64::from(seat))
                || claim
                    .get("ownerId")
                    .and_then(Value::as_str)
                    .is_none_or(|owner| owner.is_empty() || owner == owner_id))
        {
            return Err(StoreError::Precondition);
        }
        if self
            .get::<User>("users", owner_id)
            .await?
            .is_some_and(|user| !user.deleted)
        {
            return Err(StoreError::Precondition);
        }
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            if !matches!(ledger.in_flight.get(owner_id), Some(InFlight::Releasing { seat: marked }) if *marked == seat)
            {
                return Ok(false);
            }
            ledger.in_flight.remove(owner_id);
            ledger.count = ledger
                .count
                .checked_sub(1)
                .ok_or(StoreError::Precondition)?;
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(true),
                Ok(false) => {}
                Err(error) => {
                    if !self
                        .quota_ledger(instance_id)
                        .await?
                        .0
                        .in_flight
                        .contains_key(owner_id)
                    {
                        return Ok(true);
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Repair the count from every live user, every quota claim (including
    /// high grandfathered seats), and all unresolved in-flight owners.
    pub(crate) async fn reconcile_quota_admission(&self, instance_id: &str) -> StoreResult<()> {
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            let mut owners = self.quota_owner_union(instance_id).await?;
            owners.extend(ledger.in_flight.keys().cloned());
            let count = owners.len().try_into().map_err(|_| {
                StoreError::InvalidConfiguration("quota owner count overflow".into())
            })?;
            if ledger.count == count {
                return Ok(());
            }
            ledger.count = count;
            if self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await?
            {
                return Ok(());
            }
        }
        Err(StoreError::Contention)
    }

    /// Serialize plan changes. Lower before subscription CAS; raise only after
    /// its durable commit is confirmed. A different active intent fails closed.
    pub async fn begin_quota_cap_transition(
        &self,
        instance_id: &str,
        initial_cap: u32,
        target_cap: u32,
        transition_id: &str,
        expected_subscription: &Subscription,
        source_version: Option<&str>,
    ) -> StoreResult<u64> {
        if target_cap == 0
            || transition_id.is_empty()
            || expected_subscription.instance_id != instance_id
        {
            return Err(StoreError::InvalidConfiguration(
                "invalid quota plan transition".into(),
            ));
        }
        let new_id = self
            .claim_quota_transition_id(
                instance_id,
                transition_id,
                target_cap,
                expected_subscription,
                source_version,
            )
            .await?;
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self
                .read_or_create_quota_ledger(instance_id, initial_cap)
                .await?;
            if let Some(intent) = &ledger.plan_intent {
                return if intent.id == transition_id
                    && intent.target_cap == target_cap
                    && serde_json::to_value(&intent.expected_subscription)?
                        == serde_json::to_value(expected_subscription)?
                    && intent.source_version.as_deref() == source_version
                {
                    Ok(intent.generation)
                } else {
                    Err(StoreError::Contention)
                };
            }
            if let Some(last) = &ledger.last_plan_transition
                && last.id == transition_id
            {
                return if last.target_cap == target_cap
                    && serde_json::to_value(&last.expected_subscription)?
                        == serde_json::to_value(expected_subscription)?
                    && last.source_version.as_deref() == source_version
                {
                    Ok(last.generation)
                } else {
                    Err(StoreError::Precondition)
                };
            }
            // A completed or aborted ID cannot restart a later generation.
            // A create-only claim left before the very first gate CAS can be
            // retried while the ledger has never begun a transition.
            if !new_id && ledger.plan_generation != 0 {
                return Err(StoreError::Precondition);
            }
            let generation = ledger.plan_generation.checked_add(1).ok_or_else(|| {
                StoreError::InvalidConfiguration("quota plan generation exhausted".into())
            })?;
            ledger.plan_generation = generation;
            ledger.cap = ledger.cap.min(target_cap);
            ledger.plan_intent = Some(QuotaCapTransition {
                id: transition_id.to_owned(),
                generation,
                target_cap,
                expected_subscription: expected_subscription.clone(),
                source_version: source_version.map(str::to_owned),
            });
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(generation),
                Ok(false) => {}
                Err(error) => {
                    if let Ok((current, _)) = self.quota_ledger(instance_id).await
                        && let Some(intent) = current.plan_intent
                        && intent.id == transition_id
                        && intent.target_cap == target_cap
                        && serde_json::to_value(&intent.expected_subscription)?
                            == serde_json::to_value(expected_subscription)?
                        && intent.source_version.as_deref() == source_version
                    {
                        return Ok(intent.generation);
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Replace a committed, superseded plan intent with its newer candidate
    /// in one ledger CAS. The gate remains at the lower cap throughout.
    /// The caller must first verify the old candidate's exact durable row.
    #[expect(
        clippy::too_many_arguments,
        reason = "both transition tokens and the exact subscription CAS source fence are required"
    )]
    pub async fn replace_quota_cap_transition(
        &self,
        instance_id: &str,
        old_transition_id: &str,
        old_generation: u64,
        target_cap: u32,
        transition_id: &str,
        expected_subscription: &Subscription,
        source_version: Option<&str>,
    ) -> StoreResult<u64> {
        if target_cap == 0
            || transition_id.is_empty()
            || transition_id == old_transition_id
            || expected_subscription.instance_id != instance_id
        {
            return Err(StoreError::InvalidConfiguration(
                "invalid replacement quota plan transition".into(),
            ));
        }
        let new_id = self
            .claim_quota_transition_id(
                instance_id,
                transition_id,
                target_cap,
                expected_subscription,
                source_version,
            )
            .await?;
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            if let Some(current) = &ledger.plan_intent
                && current.id == transition_id
                && current.target_cap == target_cap
                && serde_json::to_value(&current.expected_subscription)?
                    == serde_json::to_value(expected_subscription)?
                && current.source_version.as_deref() == source_version
            {
                return Ok(current.generation);
            }
            let Some(old) = ledger.plan_intent.clone() else {
                return Err(StoreError::Precondition);
            };
            if old.id != old_transition_id || old.generation != old_generation {
                return Err(StoreError::Precondition);
            }
            if !new_id && ledger.plan_generation != old_generation {
                return Err(StoreError::Precondition);
            }
            let generation = ledger.plan_generation.checked_add(1).ok_or_else(|| {
                StoreError::InvalidConfiguration("quota plan generation exhausted".into())
            })?;
            ledger.plan_generation = generation;
            ledger.cap = ledger.cap.min(target_cap);
            ledger.last_plan_transition = Some(old);
            ledger.plan_intent = Some(QuotaCapTransition {
                id: transition_id.to_owned(),
                generation,
                target_cap,
                expected_subscription: expected_subscription.clone(),
                source_version: source_version.map(str::to_owned),
            });
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(generation),
                Ok(false) => {}
                Err(error) => {
                    let (current, _) = self.quota_ledger(instance_id).await?;
                    if let Some(intent) = current.plan_intent
                        && intent.id == transition_id
                        && intent.target_cap == target_cap
                        && serde_json::to_value(&intent.expected_subscription)?
                            == serde_json::to_value(expected_subscription)?
                        && intent.source_version.as_deref() == source_version
                    {
                        return Ok(intent.generation);
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Reveal an unresolved plan write so the billing caller can compare the
    /// exact expected row with durable storage before completing or aborting.
    pub async fn active_quota_cap_transition(
        &self,
        instance_id: &str,
    ) -> StoreResult<Option<QuotaCapTransition>> {
        Ok(self
            .get::<QuotaLedger>(LEDGER_COLLECTION, instance_id)
            .await?
            .and_then(|ledger| ledger.plan_intent))
    }

    pub async fn complete_quota_cap_transition(
        &self,
        instance_id: &str,
        transition_id: &str,
        generation: u64,
    ) -> StoreResult<()> {
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            let Some(intent) = ledger.plan_intent.clone() else {
                return if ledger
                    .last_plan_transition
                    .as_ref()
                    .is_some_and(|last| last.id == transition_id && last.generation == generation)
                {
                    Ok(())
                } else {
                    Err(StoreError::Precondition)
                };
            };
            if intent.id != transition_id || intent.generation != generation {
                return Err(StoreError::Precondition);
            }
            ledger.cap = intent.target_cap;
            ledger.plan_intent = None;
            ledger.last_plan_transition = Some(intent);
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(()),
                Ok(false) => {}
                Err(error) => {
                    let (current, _) = self.quota_ledger(instance_id).await?;
                    if current.last_plan_transition.as_ref().is_some_and(|last| {
                        last.id == transition_id && last.generation == generation
                    }) {
                        return Ok(());
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }

    /// Only call after a definitive subscription CAS loss. Preserve any
    /// pre-lowered cap until a confirmed plan reconciliation raises it.
    pub async fn abort_quota_cap_transition(
        &self,
        instance_id: &str,
        transition_id: &str,
        generation: u64,
    ) -> StoreResult<()> {
        for _ in 0..MAX_CAS_ATTEMPTS {
            let (mut ledger, version) = self.quota_ledger(instance_id).await?;
            let Some(intent) = ledger.plan_intent.as_ref() else {
                return Ok(());
            };
            if intent.id != transition_id || intent.generation != generation {
                return Err(StoreError::Precondition);
            }
            ledger.plan_intent = None;
            match self
                .compare_and_put(LEDGER_COLLECTION, instance_id, &ledger, &version)
                .await
            {
                Ok(true) => return Ok(()),
                Ok(false) => {}
                Err(error) => {
                    if self
                        .quota_ledger(instance_id)
                        .await?
                        .0
                        .plan_intent
                        .is_none()
                    {
                        return Ok(());
                    }
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }
}
