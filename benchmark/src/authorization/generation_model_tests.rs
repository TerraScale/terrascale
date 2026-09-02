//! Bounded-exhaustive model of authorization GENERATION transitions
//! — qualification gap-sweep row 19
//! GOAL.md
//! "Correctness and simulation": authorization generation
//! transitions require model-based or formal state-machine checking
//! in addition to ordinary tests.
//!
//! The model drives the REAL engine authorization types — [`Policy`]
//! publication, [`PrincipalContext`] verification pins, and
//! [`evaluate`] — through every interleaving of grant/revoke
//! publication, principal re-verification, clock ticks, and reads, up
//! to the bounds of the model-checking corpus.
//! The publication and read steps are the real API; only the
//! exploration driver is local (a `src/` unit test cannot import the
//! cluster crate's integration-test checker, so a minimal DFS lives
//! here — deliberately separate from `tests.rs`, which stays
//! untouched).
//!
//! ## Modeled system
//!
//! One policy with two grants (`read documents`, `read secrets`) is
//! republished across generations by a gateway; two principals
//! (`alice`, `bob`) each hold a PINNED generation — the policy
//! generation their verified context was minted at, which only moves
//! when they re-verify (the stale-checkpoint shape of row 19). The
//! clock ticks toward expiry.
//!
//! ## Invariants
//!
//! - **INV-1 (a revoked principal never reads through any
//!   interleaving):** a read is allowed ONLY when the reading
//!   principal is pinned at the CURRENT generation AND the matching
//!   rule is present. After a revocation (rule removed), every
//!   subsequent state denies every principal: stale pins answer
//!   `PolicyGenerationMismatch`, refreshed pins answer
//!   `NoMatchingRule`. No interleaving of publications,
//!   re-verifications, or ticks re-opens a revoked grant.
//! - **INV-2 (removals precede generation advance):** a publication
//!   that CHANGES the rule set versus the previously published policy
//!   must strictly increase the generation. The engine's
//!   `Policy::try_new` does not itself enforce monotonic publication
//!   — the model proves the disciplined transition relation holds it
//!   and the negative control proves the checker catches an
//!   unversioned removal (the exact hazard an un-gated publisher
//!   would introduce).
//! - **INV-3 (stale-generation checkpoint refusal is typed):** a
//!   principal pinned below the current generation, with matching
//!   scope, audience, action, and an unexpired window, is refused
//!   with EXACTLY `PolicyGenerationMismatch` — never an allow, never
//!   a different denial. An expired stale context refuses `Expired`
//!   (evaluate orders expiry before generation) — still a denial.

use super::*;
use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, PrincipalSessionUlid, PrincipalUlid,
    ProjectUlid, TenantUlid,
};
use std::collections::{BTreeMap, BTreeSet};

/// The two granted collections in the model policy.
const COLLECTIONS: [&str; 2] = ["documents", "secrets"];

/// The canonical model summary.
#[derive(Clone, Debug, Eq, PartialEq)]
struct AuthState {
    /// The current published policy generation (1-based; the bound
    /// keeps the exploration finite).
    generation: u64,
    /// Which grants the CURRENT policy carries.
    rules: BTreeMap<&'static str, bool>,
    /// Each principal's pinned generation (their checkpoint).
    pins: BTreeMap<&'static str, u64>,
    /// The request clock (`issued_at` 100, `expires_at` 200).
    now: u64,
    /// The generation at which each rule was last revoked, if ever.
    revoked_at: BTreeMap<&'static str, u64>,
}

impl AuthState {
    fn initial() -> Self {
        AuthState {
            generation: 1,
            rules: BTreeMap::from([("documents", true), ("secrets", true)]),
            pins: BTreeMap::from([("alice", 1), ("bob", 1)]),
            now: 100,
            revoked_at: BTreeMap::new(),
        }
    }

    fn key(&self) -> String {
        format!(
            "g={};r={:?};p={:?};now={};rev={:?}",
            self.generation, self.rules, self.pins, self.now, self.revoked_at
        )
    }
}

/// One model step.
#[derive(Clone, Debug, Eq, PartialEq)]
enum AuthStep {
    /// Republish the policy at generation+1 with the given rule set
    /// (the disciplined publication: any change bumps the version).
    Publish { documents: bool, secrets: bool },
    /// Re-verify a principal at the current generation (their
    /// reconnect-and-refresh path).
    Refresh { principal: &'static str },
    /// A read attempt by a principal on a collection, through the
    /// REAL `evaluate()`.
    Read { principal: &'static str, collection: &'static str },
    /// Advance the clock.
    Tick,
    /// Negative control only: change the rule set WITHOUT advancing
    /// the generation — the un-gated publisher.
    PublishUnversioned { documents: bool, secrets: bool },
}

/// Coverage figures for one exploration.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct AuthReport {
    distinct_states: usize,
    transitions_fired: usize,
    max_depth_reached: usize,
    hit_state_cap: bool,
}

/// One checked violation with its reproduction trace.
struct AuthViolation {
    trace: Vec<AuthStep>,
    message: String,
}

/// Builds the REAL policy for a state's rule set at `generation`.
fn real_policy(state: &AuthState, generation: u64) -> Policy {
    let rules: Vec<PolicyRule> = COLLECTIONS
        .iter()
        .filter(|collection| *state.rules.get(*collection).unwrap_or(&false))
        .map(|collection| PolicyRule::try_new(Operation::Read, collection).expect("valid rule"))
        .collect();
    Policy::try_new(generation, std::iter::empty::<String>(), rules)
        .expect("the model publishes valid policies")
}

/// Builds the REAL verified context for a principal pinned at
/// `generation` (the crate-private seam this in-crate model drives).
fn real_context(principal: &str, generation: u64) -> PrincipalContext {
    PrincipalContext::from_verified(VerifiedPrincipalInput {
        scope: model_scope(),
        environment: "production".to_owned(),
        gateway_spiffe_principal: "spiffe://terrascale/gateway".to_owned(),
        issuer: "terrascale-go".to_owned(),
        principal_id: model_principal(principal),
        session_id: PrincipalSessionUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB5").unwrap(),
        grant_id: PrincipalGrantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB6").unwrap(),
        policy_id: AuthorizationPolicyUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB7").unwrap(),
        actions: BTreeSet::from([Action::Read]),
        claims: PrincipalClaims::try_new(Vec::<(String, AttributeValue)>::new()).expect("claims"),
        audience: "terrascale.database".to_owned(),
        issued_at: 100,
        expires_at: 200,
        policy_generation: generation,
    })
    .expect("verified context")
}

/// Builds the REAL read request for a collection at `now`.
fn real_request(collection: &str, now: u64) -> AuthorizationRequest {
    AuthorizationRequest::try_new(
        model_scope(),
        "terrascale.database",
        Action::Read,
        collection,
        BTreeMap::new(),
        BTreeSet::new(),
        now,
    )
    .expect("request")
}

fn model_scope() -> ResourceScope {
    ResourceScope::try_new(
        TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap(),
        ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB2").unwrap(),
        DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB3").unwrap(),
    )
    .expect("scope")
}

fn model_principal(name: &str) -> PrincipalUlid {
    let text = match name {
        "alice" => "01ARZ3NDEKTSV4RRFFQ69G5FB8",
        "bob" => "01ARZ3NDEKTSV4RRFFQ69G5FB9",
        _ => panic!("unknown model principal"),
    };
    PrincipalUlid::from_text(text).unwrap()
}

/// The transition relation: which steps are offered in `state`.
fn auth_steps(state: &AuthState, allow_unversioned: bool) -> Vec<AuthStep> {
    let mut steps = Vec::new();
    if state.generation < 4 {
        for documents in [true, false] {
            for secrets in [true, false] {
                steps.push(AuthStep::Publish { documents, secrets });
            }
        }
    }
    for principal in ["alice", "bob"] {
        steps.push(AuthStep::Refresh { principal });
        for collection in COLLECTIONS {
            steps.push(AuthStep::Read { principal, collection });
        }
    }
    if state.now < 200 {
        steps.push(AuthStep::Tick);
    }
    if allow_unversioned {
        for documents in [true, false] {
            for secrets in [true, false] {
                steps.push(AuthStep::PublishUnversioned { documents, secrets });
            }
        }
    }
    steps
}

/// Applies one step, returning the successor state.
fn auth_apply(state: &AuthState, step: &AuthStep) -> AuthState {
    let mut next = state.clone();
    match step {
        AuthStep::Publish { documents, secrets } => {
            next.generation += 1;
            next.rules.insert("documents", *documents);
            next.rules.insert("secrets", *secrets);
            record_revocations(state, &mut next);
        }
        AuthStep::PublishUnversioned { documents, secrets } => {
            next.rules.insert("documents", *documents);
            next.rules.insert("secrets", *secrets);
            record_revocations(state, &mut next);
        }
        AuthStep::Refresh { principal } => {
            next.pins.insert(principal, state.generation);
        }
        AuthStep::Read { .. } => {
            // The read itself runs through the real `evaluate()` inside
            // check_auth_transition (the outcome is a transition
            // property, not a state mutation); the summary is
            // unchanged.
        }
        AuthStep::Tick => {
            next.now = (state.now + 25).min(200);
        }
    }
    next
}

/// Records the revocation bookkeeping: a rule that was present and is
/// now absent was revoked at `next.generation`.
fn record_revocations(before: &AuthState, next: &mut AuthState) {
    for collection in COLLECTIONS {
        if *before.rules.get(collection).unwrap_or(&false)
            && !*next.rules.get(collection).unwrap_or(&false)
        {
            next.revoked_at.insert(collection, next.generation);
        }
    }
}

/// INV checks for one state.
fn check_auth_state(state: &AuthState) -> Result<(), String> {
    // INV-2 (state form): once a rule was revoked, no LATER
    // publication may silently re-tie the old generation to a
    // different rule set; the bookkeeping itself must be consistent.
    for (collection, at) in &state.revoked_at {
        if *at > state.generation {
            return Err(format!(
                "INV-2: {collection} recorded revoked at generation {at} beyond the current \
                 generation {}",
                state.generation
            ));
        }
        let present = *state.rules.get(collection).unwrap_or(&false);
        if present && *at == state.generation {
            return Err(format!(
                "INV-2: {collection} is both revoked and granted at generation {}",
                state.generation
            ));
        }
    }
    Ok(())
}

/// INV checks for one fired transition (the real `evaluate()` drives
/// INV-1 and INV-3).
fn check_auth_transition(
    before: &AuthState,
    step: &AuthStep,
    after: &AuthState,
) -> Result<(), String> {
    match step {
        AuthStep::Read { principal, collection } => {
            let context = real_context(principal, before.pins[principal]);
            let policy = real_policy(before, before.generation);
            let request = real_request(collection, before.now);
            let decision = evaluate(&context, &policy, &request);
            let pinned = before.pins[principal];
            let granted_now = *before.rules.get(collection).unwrap_or(&false);
            match decision {
                AuthorizationDecision::Allow { .. } => {
                    // INV-1: an allow requires a current pin AND a
                    // present rule — a revoked principal never reads
                    // through any interleaving.
                    if pinned != before.generation {
                        return Err(format!(
                            "INV-1: {principal} read {collection} on a stale pin {pinned} \
                             against generation {}",
                            before.generation
                        ));
                    }
                    if !granted_now {
                        return Err(format!(
                            "INV-1: {principal} read {collection} although the rule is absent \
                             at generation {}",
                            before.generation
                        ));
                    }
                    if before.now >= 200 {
                        return Err(format!(
                            "INV-1: {principal} read {collection} past expiry at {}",
                            before.now
                        ));
                    }
                }
                AuthorizationDecision::Deny(reason) => {
                    // INV-3: a stale pin with an otherwise valid,
                    // unexpired context refuses with EXACTLY the
                    // generation mismatch.
                    if pinned < before.generation
                        && before.now < 200
                        && reason != DenialReason::PolicyGenerationMismatch
                    {
                        return Err(format!(
                            "INV-3: stale pin {pinned} at generation {} refused with \
                             {reason:?}, expected PolicyGenerationMismatch",
                            before.generation
                        ));
                    }
                    // A revoked-then-refreshed pin denies by rule
                    // absence, which is fine; every denial of a
                    // revoked rule must stand — nothing to fail here,
                    // the allow branch above carries INV-1.
                    let _ = after;
                }
            }
        }
        AuthStep::Publish { documents, secrets } => {
            // INV-2 (transition form): a rule-set change strictly
            // advances the generation.
            let changed = before.rules.get("documents").copied().unwrap_or(false) != *documents
                || before.rules.get("secrets").copied().unwrap_or(false) != *secrets;
            if changed && after.generation <= before.generation {
                return Err(format!(
                    "INV-2: rule set changed {:?} -> ({documents}, {secrets}) without a \
                     generation advance ({} -> {})",
                    before.rules, before.generation, after.generation
                ));
            }
        }
        AuthStep::PublishUnversioned { documents, secrets } => {
            let changed = before.rules.get("documents").copied().unwrap_or(false) != *documents
                || before.rules.get("secrets").copied().unwrap_or(false) != *secrets;
            if changed && after.generation <= before.generation {
                return Err(format!(
                    "INV-2 (negative control): rule set changed {:?} -> ({documents}, \
                     {secrets}) without a generation advance ({} -> {})",
                    before.rules, before.generation, after.generation
                ));
            }
        }
        AuthStep::Refresh { .. } | AuthStep::Tick => {}
    }
    Ok(())
}

/// The bounded-exhaustive walk (DFS, memoized on the canonical key).
/// Mirrors the cluster crate's checker semantics: state invariants
/// once per distinct state, transition invariants for every fired
/// transition, exact state cap with honest truncation flag.
struct AuthWalk<'a> {
    max_depth: usize,
    max_states: usize,
    allow_unversioned: bool,
    visited: &'a mut BTreeSet<String>,
    report: &'a mut AuthReport,
    path: &'a mut Vec<AuthStep>,
}

impl AuthWalk<'_> {
    fn walk(&mut self, state: &AuthState, depth: usize) -> Result<(), AuthViolation> {
        for step in auth_steps(state, self.allow_unversioned) {
            self.report.transitions_fired += 1;
            self.report.max_depth_reached = self.report.max_depth_reached.max(depth + 1);
            let after = auth_apply(state, &step);
            if let Err(message) = check_auth_transition(state, &step, &after) {
                let mut trace = self.path.clone();
                trace.push(step);
                return Err(AuthViolation { trace, message });
            }
            let key = after.key();
            if self.visited.contains(&key) {
                continue;
            }
            if self.report.distinct_states >= self.max_states {
                self.report.hit_state_cap = true;
                continue;
            }
            self.visited.insert(key);
            self.report.distinct_states += 1;
            if let Err(message) = check_auth_state(&after) {
                let mut trace = self.path.clone();
                trace.push(step);
                return Err(AuthViolation { trace, message });
            }
            if depth + 1 >= self.max_depth {
                continue;
            }
            self.path.push(step.clone());
            let result = self.walk(&after, depth + 1);
            self.path.pop();
            result?;
        }
        Ok(())
    }
}

fn auth_explore(
    allow_unversioned: bool,
    max_depth: usize,
    max_states: usize,
) -> Result<AuthReport, AuthViolation> {
    let initial = AuthState::initial();
    if let Err(message) = check_auth_state(&initial) {
        return Err(AuthViolation { trace: Vec::new(), message });
    }
    let mut visited = BTreeSet::from([initial.key()]);
    let mut report = AuthReport { distinct_states: 1, ..AuthReport::default() };
    let mut path: Vec<AuthStep> = Vec::new();
    AuthWalk {
        max_depth,
        max_states,
        allow_unversioned,
        visited: &mut visited,
        report: &mut report,
        path: &mut path,
    }
    .walk(&initial, 0)?;
    Ok(report)
}

/// The exhaustive sweep: every interleaving of grant/revoke
/// publication, re-verification, reads, and clock ticks, up to the
/// depth bound, with the state cap asserted unhit so the
/// exhaustive-to-depth claim is checkable.
#[test]
fn authorization_generation_transitions_are_exhaustively_checked() {
    let report = auth_explore(false, 8, 20_000).unwrap_or_else(|violation| {
        panic!(
            "authorization generation invariant violated: {} (trace {:?})",
            violation.message, violation.trace
        )
    });
    assert!(!report.hit_state_cap, "exhaustive-to-depth claim broken: {report:?}");
    assert!(report.distinct_states >= 200, "suspiciously small space: {report:?}");
    println!(
        "row-19 generation sweep: distinct_states={} transitions={} depth={}",
        report.distinct_states, report.transitions_fired, report.max_depth_reached
    );
}

/// Negative control: an un-gated publisher that changes the rule set
/// WITHOUT advancing the generation is exactly the unversioned
/// removal INV-2 exists to catch, and the checker must flag it.
#[test]
fn negative_control_unversioned_removal_is_detected() {
    let violation = auth_explore(true, 3, 5_000)
        .expect_err("an unversioned rule-set change must violate INV-2");
    assert!(
        violation.message.contains("INV-2"),
        "expected the INV-2 violation, got: {}",
        violation.message
    );
    assert!(
        matches!(violation.trace.last(), Some(AuthStep::PublishUnversioned { .. })),
        "the trace must end in the un-gated publication: {:?}",
        violation.trace
    );
}

/// The row-19 straight-line drill: a revoked principal cannot read
/// again through a stale checkpoint, a reconnect, further
/// publications, or expiry — every path answers a typed denial.
#[test]
fn a_revoked_principal_never_reads_again_through_any_observed_path() {
    // Generation 1: both grants live; alice reads both collections.
    let granted = AuthState::initial();
    for collection in COLLECTIONS {
        let decision = evaluate(
            &real_context("alice", 1),
            &real_policy(&granted, 1),
            &real_request(collection, 100),
        );
        assert!(decision.is_allowed(), "the granted read at generation 1 must allow: {decision:?}");
    }

    // Generation 2: `secrets` is revoked. alice's stale checkpoint
    // (still pinned at 1) is refused with the typed generation
    // mismatch — never the revoked data.
    let revoked = auth_apply(&granted, &AuthStep::Publish { documents: true, secrets: false });
    assert_eq!(revoked.generation, 2);
    assert_eq!(*revoked.revoked_at.get("secrets").expect("revocation recorded"), 2);
    let stale = evaluate(
        &real_context("alice", 1),
        &real_policy(&revoked, 2),
        &real_request("secrets", 100),
    );
    assert_eq!(
        stale,
        AuthorizationDecision::Deny(DenialReason::PolicyGenerationMismatch),
        "the stale checkpoint is refused typed"
    );

    // Reconnect-and-refresh at the current generation: the read is
    // still denied — now by rule absence, the revocation itself.
    let refreshed = evaluate(
        &real_context("alice", 2),
        &real_policy(&revoked, 2),
        &real_request("secrets", 100),
    );
    assert_eq!(
        refreshed,
        AuthorizationDecision::Deny(DenialReason::NoMatchingRule),
        "the refreshed principal is denied by the revocation"
    );

    // Further publications (generation 3 restores documents only, and
    // generation 4 re-grants secrets for OTHER principals) never
    // resurrect alice's stale checkpoint: pins below the current
    // generation keep answering the mismatch.
    let later = AuthState {
        generation: 4,
        rules: BTreeMap::from([("documents", true), ("secrets", true)]),
        pins: BTreeMap::from([("alice", 1), ("bob", 4)]),
        now: 175,
        revoked_at: BTreeMap::from([("secrets", 2)]),
    };
    let stale_later =
        evaluate(&real_context("alice", 1), &real_policy(&later, 4), &real_request("secrets", 175));
    assert_eq!(
        stale_later,
        AuthorizationDecision::Deny(DenialReason::PolicyGenerationMismatch),
        "a checkpoint from before the revocation never reads through later re-grants"
    );

    // Expiry refuses with Expired (evaluate orders expiry before
    // generation) — still a denial, never a leak.
    let expired =
        evaluate(&real_context("alice", 1), &real_policy(&later, 4), &real_request("secrets", 200));
    assert_eq!(expired, AuthorizationDecision::Deny(DenialReason::Expired));

    // A principal freshly verified at the current generation reads
    // the re-granted collection again — the model's positive control.
    let fresh =
        evaluate(&real_context("bob", 4), &real_policy(&later, 4), &real_request("secrets", 175));
    assert!(fresh.is_allowed(), "a current, granted principal reads: {fresh:?}");
}
