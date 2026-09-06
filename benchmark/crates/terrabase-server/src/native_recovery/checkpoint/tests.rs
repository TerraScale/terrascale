use super::*;
use std::cell::Cell;

fn point() -> Checkpoint {
    Checkpoint { recovery_point_id: "rp".into(), source_database_id: "db".into(), journal_cut: 7, captured_at_millis: 11, retained_until_millis: 99, placement: vec!["GRU".into()] }
}
fn facts() -> RecoveryPointFacts {
    RecoveryPointFacts { recovery_point_id: "rp".into(), source_database_id: "db".into(), journal_cut: 7, captured_at_millis: 11, checksum: blake3::hash(b"fixture").to_hex().to_string(), row_count: 0, placement: vec!["GRU".into()], retained_until_millis: 99 }
}
fn state(phase: NativeRecoveryPhase) -> NativeRecoveryOperation {
    NativeRecoveryOperation { operation_id: "op".into(), source_database_id: "db".into(), phase, facts: None, error: None }
}

#[test]
fn checkpoint_round_trips_with_strict_bounds() {
    let original = point();
    assert_eq!(Checkpoint::decode(&original.encode().unwrap()).unwrap(), original);
    assert!(validate(Some(&original), "db", &["GRU".into()]).is_ok());
}

#[test]
fn checkpoint_rejects_source_and_placement_mismatch() {
    let original = point();
    assert_eq!(validate(Some(&original), "other", &["GRU".into()]), Err(NativeRecoveryError::VerificationFailed));
    assert_eq!(validate(Some(&original), "db", &["SFO".into()]), Err(NativeRecoveryError::VerificationFailed));
    assert!(Checkpoint::decode(br"{}").is_err());
}

#[test]
fn accepted_dispatch_captures_persists_measures_and_completes() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let calls = Cell::new(0);
    let mut phases = Vec::new();
    advance(&mut operation, None, &["GRU".into()], || { calls.set(calls.get() + 1); Ok(point()) }, |state, cp| { phases.push((state.phase.clone(), cp.is_some())); Ok(()) }, |_| Ok(facts())).unwrap();
    assert_eq!(calls.get(), 1);
    assert_eq!(phases, vec![(NativeRecoveryPhase::Capturing, false), (NativeRecoveryPhase::Measuring, true), (NativeRecoveryPhase::Complete, true)]);
    assert_eq!(operation.phase, NativeRecoveryPhase::Complete);
    assert_eq!(operation.facts, Some(facts()));
}

#[test]
fn measuring_checkpoint_resumes_without_capture() {
    let mut operation = state(NativeRecoveryPhase::Measuring);
    let captures = Cell::new(0);
    advance(&mut operation, Some(point()), &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| Ok(()), |_| Ok(facts())).unwrap();
    assert_eq!(captures.get(), 0);
    assert_eq!(operation.phase, NativeRecoveryPhase::Complete);
}

#[test]
fn accepted_checkpoint_retry_does_not_capture_and_complete_replay_does_nothing() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let captures = Cell::new(0); let persists = Cell::new(0); let measures = Cell::new(0);
    advance(&mut operation, Some(point()), &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| { persists.set(persists.get() + 1); Ok(()) }, |_| { measures.set(measures.get() + 1); Ok(facts()) }).unwrap();
    operation.phase = NativeRecoveryPhase::Complete;
    advance(&mut operation, None, &["GRU".into()], || panic!("capture on complete"), |_, _| panic!("persist on complete"), |_| panic!("measure on complete")).unwrap();
    assert_eq!((captures.get(), persists.get(), measures.get()), (0, 2, 1));
}

#[test]
fn invalid_phase_or_missing_checkpoint_fails_without_capture() {
    for phase in [NativeRecoveryPhase::Capturing, NativeRecoveryPhase::Measuring, NativeRecoveryPhase::Failed] {
        let mut operation = state(phase);
        let captures = Cell::new(0);
        assert_eq!(advance(&mut operation, None, &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| Ok(()), |_| Ok(facts())), Err(NativeRecoveryError::VerificationFailed));
        assert_eq!(captures.get(), 0);
    }
}

#[test]
fn invalid_checkpoint_is_rejected_before_callbacks() {
    for invalid in [
        Checkpoint { source_database_id: "other".into(), ..point() },
        Checkpoint { placement: vec!["SFO".into()], ..point() },
        Checkpoint { retained_until_millis: 10, ..point() },
    ] {
        let mut operation = state(NativeRecoveryPhase::Accepted); let callbacks = Cell::new(0);
        assert_eq!(advance(&mut operation, Some(invalid), &["GRU".into()], || { callbacks.set(1); Ok(point()) }, |_, _| { callbacks.set(2); Ok(()) }, |_| { callbacks.set(3); Ok(facts()) }), Err(NativeRecoveryError::VerificationFailed));
        assert_eq!(callbacks.get(), 0);
    }
}

#[test]
fn capturing_failure_and_persist_failure_do_not_recapture() {
    let mut operation = state(NativeRecoveryPhase::Accepted); let captures = Cell::new(0);
    assert_eq!(advance(&mut operation, None, &["GRU".into()], || { captures.set(captures.get() + 1); Err(NativeRecoveryError::VerificationFailed) }, |_, _| Ok(()), |_| Ok(facts())), Err(NativeRecoveryError::VerificationFailed));
    assert_eq!(captures.get(), 1); assert_eq!(operation.phase, NativeRecoveryPhase::Capturing);
    assert_eq!(advance(&mut operation, None, &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| Ok(()), |_| Ok(facts())), Err(NativeRecoveryError::VerificationFailed));
    assert_eq!(captures.get(), 1);

    let mut operation = state(NativeRecoveryPhase::Accepted); let persists = Cell::new(0); let measures = Cell::new(0);
    assert_eq!(advance(&mut operation, None, &["GRU".into()], || Ok(point()), |_, cp| { persists.set(persists.get() + 1); if cp.is_some() { Err(NativeRecoveryError::Unavailable) } else { Ok(()) } }, |_| { measures.set(measures.get() + 1); Ok(facts()) }), Err(NativeRecoveryError::Unavailable));
    assert_eq!((persists.get(), measures.get()), (2, 0));
}

#[test]
fn measure_failure_preserves_checkpoint_and_final_persist_failure_is_terminal() {
    let mut operation = state(NativeRecoveryPhase::Accepted); let captures = Cell::new(0);
    assert_eq!(advance(&mut operation, Some(point()), &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| Ok(()), |_| Err(NativeRecoveryError::VerificationFailed)), Err(NativeRecoveryError::VerificationFailed));
    assert_eq!(operation.phase, NativeRecoveryPhase::Measuring); assert_eq!(captures.get(), 0);
    operation.phase = NativeRecoveryPhase::Accepted;
    assert_eq!(advance(&mut operation, Some(point()), &["GRU".into()], || panic!("recapture"), |_, _| Ok(()), |_| Ok(facts())), Ok(()));
    let mut operation = state(NativeRecoveryPhase::Accepted); let captures = Cell::new(0); let persists = Cell::new(0);
    assert_eq!(advance(&mut operation, None, &["GRU".into()], || { captures.set(captures.get() + 1); Ok(point()) }, |_, _| { persists.set(persists.get() + 1); if persists.get() == 3 { Err(NativeRecoveryError::Unavailable) } else { Ok(()) } }, |_| Ok(facts())), Err(NativeRecoveryError::Unavailable));
    assert_eq!((captures.get(), persists.get()), (1, 3));
}
