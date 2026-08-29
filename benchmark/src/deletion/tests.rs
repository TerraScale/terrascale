//! Unit and property tests for the deletion workflow domain.
//!
//! Every test drives the state machine with injected times only. The
//! property tests are deterministic loops (per hold kind, per copy class,
//! per resume prefix) — no external property-testing dependency.

use std::collections::BTreeSet;

use super::*;

const DAY: u64 = 24 * 60 * 60 * 1000;
const T0: u64 = 1_800_000_000_000;

fn region(name: &str) -> RegionId {
    RegionId::try_new(name).expect("region")
}

fn selected() -> BTreeSet<RegionId> {
    ["gru", "sfo", "fra"].into_iter().map(region).collect()
}

fn new_workflow() -> DeletionWorkflow {
    DeletionWorkflow::request(
        DeletionRequestId::try_new("del-0001").expect("request id"),
        DeletionScope::try_new(TenantId::try_new("tenant-a").expect("tenant"), "subject-a")
            .expect("scope"),
        selected(),
        T0 - 1,
        T0,
    )
    .expect("request")
}

fn full_plan() -> BTreeSet<CopyTarget> {
    let mut plan = BTreeSet::new();
    for name in ["gru", "sfo", "fra"] {
        plan.insert(
            CopyTarget::try_new_live(region(name), &format!("live-{name}")).expect("live target"),
        );
    }
    plan.insert(CopyTarget::try_new_scoped(CopyClass::Index, "idx-profiles").expect("index"));
    plan.insert(CopyTarget::try_new_scoped(CopyClass::Cache, "cache-subject").expect("cache"));
    plan.insert(CopyTarget::try_new_scoped(CopyClass::DerivedView, "view-profile").expect("view"));
    plan.insert(
        CopyTarget::try_new_scoped(CopyClass::BackupRotation, "backup-cluster").expect("backup"),
    );
    plan
}

fn admitted() -> DeletionWorkflow {
    let mut workflow = new_workflow();
    workflow.admit(GlobalDurableAdmission::new(selected(), T0 + 1), full_plan()).expect("admit");
    workflow
}

fn receipt_for(target: &CopyTarget, at_millis: u64) -> CopyReceipt {
    let outcome = match target.class() {
        CopyClass::BackupRotation => CopyOutcome::RotatedOut,
        _ => CopyOutcome::Removed,
    };
    CopyReceipt::new(target.clone(), outcome, at_millis)
}

fn record_all(workflow: &mut DeletionWorkflow, at_millis: u64) {
    for target in workflow.plan().cloned().collect::<Vec<_>>() {
        workflow.record_receipt(receipt_for(&target, at_millis)).expect("receipt");
    }
}

fn legal_hold(kind: HoldKind, recorded_at: u64, expires_at: u64) -> HoldRecord {
    HoldRecord::try_new(kind, "authority-1", recorded_at, expires_at, &["accounting-id"])
        .expect("hold")
}

#[test]
fn charter_deadline_constants_are_thirty_and_ninety_days() {
    assert_eq!(LIVE_DEADLINE_DAYS, 30);
    assert_eq!(LIVE_DEADLINE_MILLIS, 30 * DAY);
    assert_eq!(BACKUP_DEADLINE_DAYS, 90);
    assert_eq!(BACKUP_DEADLINE_MILLIS, 90 * DAY);
    for class in CopyClass::all() {
        match class {
            CopyClass::LiveRegion
            | CopyClass::Index
            | CopyClass::Cache
            | CopyClass::DerivedView => {
                assert_eq!(class.deadline_millis(), LIVE_DEADLINE_MILLIS);
            }
            CopyClass::BackupRotation => {
                assert_eq!(class.deadline_millis(), BACKUP_DEADLINE_MILLIS);
            }
        }
    }
}

#[test]
fn request_requires_selected_regions_and_a_prior_verification() {
    let id = DeletionRequestId::try_new("del-0002").expect("id");
    let scope = DeletionScope::try_new(TenantId::try_new("tenant-a").expect("tenant"), "subject-a")
        .expect("scope");
    let empty = BTreeSet::new();
    let error = DeletionWorkflow::request(id.clone(), scope.clone(), empty, T0 - 1, T0)
        .expect_err("empty region set");
    assert_eq!(error, DeletionError::EmptyRegionSet);
    let error = DeletionWorkflow::request(id, scope, selected(), T0 + 1, T0)
        .expect_err("future verification");
    assert_eq!(error, DeletionError::InvalidVerification);
}

#[test]
fn happy_path_walks_every_charter_state_in_order() {
    let mut workflow = admitted();
    assert_eq!(workflow.state(), DeletionState::Admitted);
    let first = workflow.plan().next().cloned().expect("first target");
    workflow.record_receipt(receipt_for(&first, T0 + 2)).expect("first receipt");
    assert_eq!(workflow.state(), DeletionState::Executing);
    record_all(&mut workflow, T0 + 3);
    let verification = workflow.verify(T0 + 4).expect("verify");
    assert_eq!(workflow.state(), DeletionState::Verified);
    assert_eq!(verification.receipt_count(), full_plan().len());
    assert_eq!(verification.live_deadline_at_millis(), T0 + LIVE_DEADLINE_MILLIS);
    assert_eq!(verification.backup_deadline_at_millis(), T0 + BACKUP_DEADLINE_MILLIS);
    let completion = workflow.complete(T0 + 5).expect("complete");
    assert_eq!(workflow.state(), DeletionState::Complete);
    assert!(workflow.state().is_terminal());
    assert_eq!(completion.receipt_count(), full_plan().len());
    assert_eq!(completion.request_id().as_str(), "del-0001");
}

#[test]
fn admission_must_be_global_durable_over_every_selected_region() {
    let mut workflow = new_workflow();

    // One selected Region never acknowledged: not GlobalDurable.
    let mut partial = selected();
    partial.remove(&region("fra"));
    let error = workflow
        .admit(GlobalDurableAdmission::new(partial, T0 + 1), full_plan())
        .expect_err("partial admission");
    assert_eq!(error, DeletionError::AdmissionNotGlobalDurable);

    // An acknowledgement from an unselected Region fails closed.
    let mut extra = selected();
    extra.insert(region("syd"));
    let error = workflow
        .admit(GlobalDurableAdmission::new(extra, T0 + 1), full_plan())
        .expect_err("unknown acknowledging region");
    assert_eq!(error, DeletionError::UnknownRegion);

    // Admission before the request exists is incoherent.
    let error = workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 - 1), full_plan())
        .expect_err("admission before request");
    assert_eq!(error, DeletionError::InvalidAdmissionTime);

    // A plan without every selected Region's live copy can miss a Region.
    let mut missing_live = full_plan();
    missing_live.remove(&CopyTarget::try_new_live(region("sfo"), "live-sfo").expect("target"));
    let error = workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 + 1), missing_live)
        .expect_err("plan misses a region");
    assert_eq!(error, DeletionError::PlanMissesRegion);

    // A plan cannot pull an unselected Region's copy in either: adding a
    // Region later never silently extends this workflow's scope.
    let mut extra_live = full_plan();
    extra_live.insert(CopyTarget::try_new_live(region("syd"), "live-syd").expect("target"));
    let error = workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 + 1), extra_live)
        .expect_err("plan names unselected region");
    assert_eq!(error, DeletionError::UnknownRegion);

    // An empty plan admits nothing.
    let error = workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 + 1), BTreeSet::new())
        .expect_err("empty plan");
    assert_eq!(error, DeletionError::InvalidPlan);

    workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 + 1), full_plan())
        .expect("valid admission");
    let error = workflow
        .admit(GlobalDurableAdmission::new(selected(), T0 + 2), full_plan())
        .expect_err("double admission");
    assert_eq!(error, DeletionError::InvalidTransition);
}

#[test]
fn no_execution_precedes_admission() {
    let mut workflow = new_workflow();
    let target = CopyTarget::try_new_live(region("gru"), "live-gru").expect("target");
    let error = workflow
        .record_receipt(receipt_for(&target, T0 + 1))
        .expect_err("receipt before admission");
    assert_eq!(error, DeletionError::NotAdmitted);
    let error = workflow.verify(T0 + 1).expect_err("verify before admission");
    assert_eq!(error, DeletionError::NotAdmitted);
    let error = workflow.complete(T0 + 1).expect_err("complete before anything");
    assert_eq!(error, DeletionError::InvalidTransition);
}

#[test]
fn receipts_are_idempotent_monotone_and_plan_bound() {
    let mut workflow = admitted();
    let target = CopyTarget::try_new_live(region("gru"), "live-gru").expect("target");

    // A receipt that precedes admission is incoherent.
    let error =
        workflow.record_receipt(receipt_for(&target, T0)).expect_err("receipt at admission time");
    assert_eq!(error, DeletionError::InvalidReceiptTime);

    let first = workflow.record_receipt(receipt_for(&target, T0 + 10)).expect("first receipt");
    assert_eq!(first, ReceiptOutcome::Recorded);
    let bytes = workflow.encoded();

    // Exact duplicate changes nothing.
    let duplicate =
        workflow.record_receipt(receipt_for(&target, T0 + 10)).expect("duplicate receipt");
    assert_eq!(duplicate, ReceiptOutcome::Duplicate);
    assert_eq!(workflow.encoded(), bytes);

    // A stale replay of an older receipt changes nothing.
    let stale = workflow.record_receipt(receipt_for(&target, T0 + 5)).expect("stale replay");
    assert_eq!(stale, ReceiptOutcome::Duplicate);
    assert_eq!(workflow.encoded(), bytes);

    // A later receipt supersedes (re-execution after a hold, for example).
    let superseded =
        workflow.record_receipt(receipt_for(&target, T0 + 20)).expect("superseding receipt");
    assert_eq!(superseded, ReceiptOutcome::Superseded);
    assert_ne!(workflow.encoded(), bytes);

    // The superseding receipt replays as an exact duplicate.
    let replay = workflow.record_receipt(receipt_for(&target, T0 + 20)).expect("replay");
    assert_eq!(replay, ReceiptOutcome::Duplicate);

    // Receipts stay inside the admitted plan.
    let unplanned = CopyTarget::try_new_live(region("gru"), "live-elsewhere").expect("target");
    let error =
        workflow.record_receipt(receipt_for(&unplanned, T0 + 20)).expect_err("unplanned target");
    assert_eq!(error, DeletionError::CopyTargetNotPlanned);

    // Live classes remove; only backup rotation rotates out.
    let live = CopyTarget::try_new_scoped(CopyClass::Cache, "cache-subject").expect("target");
    let error = workflow
        .record_receipt(CopyReceipt::new(live.clone(), CopyOutcome::RotatedOut, T0 + 20))
        .expect_err("wrong outcome");
    assert_eq!(error, DeletionError::InvalidReceiptOutcome);
    let backup =
        CopyTarget::try_new_scoped(CopyClass::BackupRotation, "backup-cluster").expect("target");
    let error = workflow
        .record_receipt(CopyReceipt::new(backup, CopyOutcome::Removed, T0 + 20))
        .expect_err("wrong backup outcome");
    assert_eq!(error, DeletionError::InvalidReceiptOutcome);
}

#[test]
fn verification_requires_every_planned_copy() {
    // Plan minus one target: exactly one copy stays unreceipted.
    let mut trimmed = full_plan();
    let missing = trimmed.pop_first().expect("a target");
    let mut other = admitted();
    for target in &trimmed {
        other.record_receipt(receipt_for(target, T0 + 2)).expect("receipt");
    }
    let missing_list: Vec<_> = other.missing_targets().cloned().collect();
    assert_eq!(missing_list, vec![missing.clone()]);
    let error = other.verify(T0 + 3).expect_err("one copy missing");
    assert_eq!(error, DeletionError::MissingReceipts);
    other.record_receipt(receipt_for(&missing, T0 + 3)).expect("final receipt");
    other.verify(T0 + 4).expect("verify after the last copy");
}

#[test]
fn every_hold_kind_blocks_verification_and_ordinary_access() {
    for kind in HoldKind::all() {
        let mut workflow = admitted();
        record_all(&mut workflow, T0 + 2);
        workflow.record_hold(legal_hold(kind, T0 + 1, T0 + 40 * DAY)).expect("hold");
        assert!(workflow.ordinary_access_blocked(T0 + 3));
        let error = workflow.verify(T0 + 3).expect_err("hold blocks verify");
        assert_eq!(error, DeletionError::HoldBlocksCompletion);
        assert_eq!(
            workflow.retained_fields(T0 + 3),
            ["accounting-id"]
                .into_iter()
                .map(|field| RetainedField::try_new(field).expect("field"))
                .collect::<BTreeSet<_>>()
        );
        // Completion is unreachable while the hold applies.
        let error = workflow.complete(T0 + 3).expect_err("no verified state");
        assert_eq!(error, DeletionError::InvalidTransition);
    }
}

#[test]
fn expired_holds_stop_blocking_but_receipts_must_postdate_the_hold() {
    let mut workflow = admitted();
    record_all(&mut workflow, T0 + 2);
    workflow.record_hold(legal_hold(HoldKind::Legal, T0 + 1, T0 + 40 * DAY)).expect("hold");

    // The hold lapses; ordinary access unblocks, but the receipts recorded
    // during the hold cannot prove the retained fields were removed.
    let after_expiry = T0 + 41 * DAY;
    assert!(!workflow.ordinary_access_blocked(after_expiry));
    assert_eq!(workflow.last_hold_end(after_expiry), T0 + 40 * DAY);
    let error = workflow.verify(after_expiry).expect_err("stale receipts");
    assert_eq!(error, DeletionError::ReceiptPredatesHoldEnd);

    // Re-execute after the hold's end: later receipts supersede and verify.
    record_all(&mut workflow, after_expiry + 1);
    let verification = workflow.verify(after_expiry + 2).expect("verify after re-execution");
    assert_eq!(verification.receipt_count(), full_plan().len());
    workflow.complete(after_expiry + 3).expect("complete");
    assert!(!workflow.ordinary_access_blocked(after_expiry + 3));
    assert!(workflow.retained_fields(after_expiry + 3).is_empty());
}

#[test]
fn early_hold_release_ends_at_release_time_and_extends_deadlines() {
    let mut workflow = admitted();
    workflow
        .record_hold(
            HoldRecord::try_new(
                HoldKind::Fraud,
                "authority-1",
                T0,
                T0 + 40 * DAY,
                &["accounting-id"],
            )
            .expect("hold facts"),
        )
        .expect("hold recorded");
    let authority = HoldAuthorityId::try_new("authority-1").expect("authority");
    let outcome = workflow.end_hold(&authority, T0 + 10 * DAY).expect("release");
    assert_eq!(outcome, HoldOutcome::Ended);
    let replay = workflow.end_hold(&authority, T0 + 11 * DAY).expect("idempotent release");
    assert_eq!(replay, HoldOutcome::AlreadyEnded);
    assert_eq!(workflow.last_hold_end(u64::MAX), T0 + 10 * DAY);
    let unknown = HoldAuthorityId::try_new("authority-2").expect("authority");
    let error = workflow.end_hold(&unknown, T0 + 10 * DAY).expect_err("unknown hold");
    assert_eq!(error, DeletionError::UnknownHold);

    // Coverage is exactly the released window, and it extends the live
    // deadline by the same amount: a receipt at day 40 is on day 31 of
    // uncovered time, so it violates without the hold and passes with it.
    assert_eq!(workflow.hold_coverage_millis(u64::MAX, T0 + 40 * DAY), 10 * DAY);
    let mut held = workflow.clone();
    record_all(&mut held, T0 + 40 * DAY);
    held.verify(T0 + 40 * DAY + 1).expect("hold coverage extends the deadline");
    // Without the hold the same receipts are eleven days late.
    let mut unheld = admitted();
    record_all(&mut unheld, T0 + 40 * DAY);
    assert_eq!(unheld.verify(T0 + 40 * DAY + 1).unwrap_err(), DeletionError::DeadlineExceeded);
}

#[test]
fn hold_records_validate_their_window_and_retained_fields() {
    let error = HoldRecord::try_new(HoldKind::Legal, "authority-1", T0, T0, &["field"])
        .expect_err("empty window");
    assert_eq!(error, DeletionError::InvalidHoldWindow);
    let error = HoldRecord::try_new(HoldKind::Tax, "authority-1", T0, T0 + 1, &[])
        .expect_err("no retained fields");
    assert_eq!(error, DeletionError::InvalidRetainedFields);

    let mut workflow = admitted();
    let error = workflow
        .record_hold(legal_hold(HoldKind::Security, T0 - DAY, T0 + DAY))
        .expect_err("recorded before the request");
    assert_eq!(error, DeletionError::InvalidHoldRecordingTime);

    workflow
        .record_hold(legal_hold(HoldKind::Payment, T0 + 1, T0 + DAY))
        .expect("valid payment hold");
    let error = workflow
        .record_hold(legal_hold(HoldKind::Payment, T0 + 1, T0 + 2 * DAY))
        .expect_err("authority reuse with different facts");
    assert_eq!(error, DeletionError::HoldConflict);
    let duplicate = workflow
        .record_hold(legal_hold(HoldKind::Payment, T0 + 1, T0 + DAY))
        .expect("identical hold replay");
    assert_eq!(duplicate, HoldOutcome::Duplicate);
}

#[test]
fn overlapping_holds_count_once_in_coverage() {
    let mut workflow = admitted();
    workflow.record_hold(legal_hold(HoldKind::Legal, T0 + DAY, T0 + 40 * DAY)).expect("first hold");
    let second = HoldRecord::try_new(
        HoldKind::Tax,
        "authority-2",
        T0 + 20 * DAY,
        T0 + 50 * DAY,
        &["tax-basis"],
    )
    .expect("second hold");
    workflow.record_hold(second).expect("second hold recorded");
    let authority_one = HoldAuthorityId::try_new("authority-1").expect("authority");
    let authority_two = HoldAuthorityId::try_new("authority-2").expect("authority");
    workflow.end_hold(&authority_one, u64::MAX).expect("end one");
    workflow.end_hold(&authority_two, u64::MAX).expect("end two");
    // Union of [1d,40d] and [20d,50d] is [1d,50d] = 49 days, not 69.
    assert_eq!(workflow.hold_coverage_millis(u64::MAX, u64::MAX), 49 * DAY);
    let retained = workflow.retained_fields(T0 + 30 * DAY);
    // Both holds ended, so nothing is retained anymore.
    assert!(retained.is_empty());
}

#[test]
fn deadline_enforcement_is_exact_and_terminal_on_violation() {
    // Property: for every copy class, the boundary is the charter deadline
    // to the millisecond, and one millisecond over fails terminally.
    for class in CopyClass::all() {
        let mut workflow = admitted();
        let target = workflow
            .plan()
            .find(|target| target.class() == class)
            .cloned()
            .expect("a target of the class");
        let on_time = T0 + class.deadline_millis();
        let others: Vec<_> =
            workflow.plan().filter(|candidate| *candidate != &target).cloned().collect();
        for other in &others {
            // Every other copy completes early so only this class's
            // boundary is under test.
            workflow.record_receipt(receipt_for(other, T0 + 1)).expect("other receipt");
        }
        workflow.record_receipt(receipt_for(&target, on_time)).expect("on-deadline receipt");
        workflow.verify(on_time + 1).expect("on the deadline is within it");

        let mut late = admitted();
        let one_over = T0 + class.deadline_millis() + 1;
        for other in &others {
            late.record_receipt(receipt_for(other, T0 + 1)).expect("other receipt");
        }
        late.record_receipt(receipt_for(&target, one_over)).expect("late receipt");
        let error = late.verify(one_over + 1).expect_err("crossed deadline");
        assert_eq!(error, DeletionError::DeadlineExceeded);
        assert_eq!(late.state(), DeletionState::Failed(DeletionFailure::DeadlineExceeded));
        assert!(late.state().is_terminal());
        let violations = late.deadline_violations_at(one_over + 1);
        assert_eq!(violations.len(), 1);
        assert_eq!(violations[0].target(), &target);
        assert_eq!(violations[0].deadline_at_millis(), T0 + class.deadline_millis());
        assert_eq!(violations[0].completed_at_millis(), one_over);
    }
}

#[test]
fn terminal_states_refuse_every_mutation() {
    let mut complete = admitted();
    record_all(&mut complete, T0 + 2);
    complete.verify(T0 + 3).expect("verify");
    complete.complete(T0 + 4).expect("complete");
    let target = complete.plan().next().cloned().expect("target");
    assert_eq!(
        complete.record_receipt(receipt_for(&target, T0 + 5)),
        Err(DeletionError::TerminalState)
    );
    assert_eq!(
        complete.record_hold(legal_hold(HoldKind::Legal, T0 + 5, T0 + DAY)),
        Err(DeletionError::TerminalState)
    );
    assert_eq!(
        complete.end_hold(&HoldAuthorityId::try_new("authority-1").expect("a"), T0 + 5),
        Err(DeletionError::TerminalState)
    );
    assert_eq!(
        complete.admit(GlobalDurableAdmission::new(selected(), T0 + 5), full_plan()),
        Err(DeletionError::TerminalState)
    );
    assert_eq!(complete.verify(T0 + 5), Err(DeletionError::TerminalState));
    assert_eq!(complete.complete(T0 + 5), Err(DeletionError::TerminalState));

    let mut failed = admitted();
    let live = failed.plan().next().cloned().expect("target");
    failed.record_receipt(receipt_for(&live, T0 + LIVE_DEADLINE_MILLIS + 1)).expect("late receipt");
    record_all(&mut failed, T0 + LIVE_DEADLINE_MILLIS + 1);
    assert_eq!(failed.verify(T0 + LIVE_DEADLINE_MILLIS + 2), Err(DeletionError::DeadlineExceeded));
    assert!(failed.state().is_terminal());
}

#[test]
fn resume_from_encoded_bytes_at_every_step_matches_a_continuous_run() {
    // Property: decoding the durable bytes at ANY step and continuing from
    // the decoded workflow reaches exactly the same final workflow as the
    // continuous run. A restart at any point loses nothing and duplicates
    // nothing.
    type Step = Box<dyn Fn(&mut DeletionWorkflow)>;
    let steps: Vec<Step> = vec![
        Box::new(|workflow: &mut DeletionWorkflow| {
            workflow
                .admit(GlobalDurableAdmission::new(selected(), T0 + 1), full_plan())
                .expect("admit");
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            workflow.record_hold(legal_hold(HoldKind::Legal, T0 + 2, T0 + 10 * DAY)).expect("hold");
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            record_all(workflow, T0 + 3 * DAY);
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            let error = workflow.verify(T0 + 4 * DAY).expect_err("hold blocks");
            assert_eq!(error, DeletionError::HoldBlocksCompletion);
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            workflow
                .end_hold(&HoldAuthorityId::try_new("authority-1").expect("a"), T0 + 11 * DAY)
                .expect("release");
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            let error = workflow.verify(T0 + 12 * DAY).expect_err("stale receipts");
            assert_eq!(error, DeletionError::ReceiptPredatesHoldEnd);
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            record_all(workflow, T0 + 13 * DAY);
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            workflow.verify(T0 + 14 * DAY).expect("verify");
        }),
        Box::new(|workflow: &mut DeletionWorkflow| {
            workflow.complete(T0 + 15 * DAY).expect("complete");
        }),
    ];

    let mut continuous = new_workflow();
    for step in &steps {
        step(&mut continuous);
    }

    for prefix in 0..=steps.len() {
        let mut resumed = new_workflow();
        for step in &steps[..prefix] {
            step(&mut resumed);
        }
        // Restart: the durable bytes are the only thing that survives.
        let bytes = resumed.encoded();
        let decoded = DeletionWorkflow::decode(&bytes).expect("decode at prefix");
        assert_eq!(decoded, resumed, "decode is identity at prefix {prefix}");
        let mut resumed = decoded;
        // Replay every receipt already recorded: pure idempotency, skipped
        // only where the workflow is already terminal.
        if !resumed.state().is_terminal() {
            for receipt in resumed.receipts().cloned().collect::<Vec<_>>() {
                let outcome = resumed.record_receipt(receipt).expect("replayed receipt");
                assert_eq!(outcome, ReceiptOutcome::Duplicate);
            }
        }
        for step in &steps[prefix..] {
            step(&mut resumed);
        }
        assert_eq!(
            resumed.encoded(),
            continuous.encoded(),
            "resume at prefix {prefix} converges to the continuous run"
        );
    }
}

#[test]
fn encoding_fails_closed_on_corruption() {
    let mut workflow = admitted();
    record_all(&mut workflow, T0 + 2);
    workflow.verify(T0 + 3).expect("verify");
    let bytes = workflow.encoded();
    let decoded = DeletionWorkflow::decode(&bytes).expect("round trip");
    assert_eq!(decoded, workflow);
    assert_eq!(decoded.encoded(), bytes);

    let truncated = &bytes[..bytes.len() - 1];
    assert_eq!(DeletionWorkflow::decode(truncated).unwrap_err(), DeletionError::ChecksumMismatch);
    let short = &bytes[..10];
    assert_eq!(DeletionWorkflow::decode(short).unwrap_err(), DeletionError::MalformedEncoding);
    let mut corrupted = bytes.clone();
    corrupted[0] = b'X';
    assert_eq!(DeletionWorkflow::decode(&corrupted).unwrap_err(), DeletionError::ChecksumMismatch);
    let mut flipped = bytes.clone();
    let middle = flipped.len() / 2;
    flipped[middle] ^= 0xff;
    assert_eq!(DeletionWorkflow::decode(&flipped).unwrap_err(), DeletionError::ChecksumMismatch);
}

#[test]
fn ledger_is_idempotent_by_request_identity() {
    let mut ledger = DeletionLedger::default();
    let first = new_workflow();
    let request_id = first.request_id().clone();
    assert_eq!(ledger.submit(first.clone()), Ok(LedgerOutcome::Inserted));
    assert_eq!(ledger.submit(first), Ok(LedgerOutcome::Duplicate));

    let different = DeletionWorkflow::request(
        request_id.clone(),
        DeletionScope::try_new(TenantId::try_new("tenant-a").expect("tenant"), "subject-b")
            .expect("scope"),
        selected(),
        T0 - 1,
        T0,
    )
    .expect("request");
    assert_eq!(ledger.submit(different), Err(DeletionError::WorkflowConflict));
    assert!(ledger.get(&request_id).is_some());
    assert_eq!(ledger.workflows().count(), 1);
}

#[test]
fn retained_fields_union_across_concurrent_holds() {
    let mut workflow = admitted();
    workflow
        .record_hold(
            HoldRecord::try_new(
                HoldKind::Payment,
                "authority-pay",
                T0 + 1,
                T0 + 20 * DAY,
                &["accounting-id", "invoice-total"],
            )
            .expect("payment hold"),
        )
        .expect("hold");
    workflow
        .record_hold(
            HoldRecord::try_new(
                HoldKind::Security,
                "authority-sec",
                T0 + 2,
                T0 + 30 * DAY,
                &["abuse-signature"],
            )
            .expect("security hold"),
        )
        .expect("hold");
    let expected: BTreeSet<RetainedField> = ["accounting-id", "invoice-total", "abuse-signature"]
        .into_iter()
        .map(|field| RetainedField::try_new(field).expect("field"))
        .collect();
    assert_eq!(workflow.retained_fields(T0 + 3), expected);
    assert_eq!(workflow.unexpired_holds(T0 + 3).len(), 2);
    // After the longer hold lapses, nothing is retained.
    assert!(workflow.retained_fields(T0 + 31 * DAY).is_empty());
}
