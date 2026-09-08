use super::*;
use rusqlite::Connection;
use std::cell::{Cell, RefCell};

fn point() -> Checkpoint {
    Checkpoint {
        recovery_point_id: "rp".into(),
        source_database_id: "db".into(),
        journal_cut: 7,
        captured_at_millis: 11,
        retained_until_millis: 99,
        placement: vec!["GRU".into()],
    }
}
fn facts() -> RecoveryPointFacts {
    RecoveryPointFacts {
        recovery_point_id: "rp".into(),
        source_database_id: "db".into(),
        journal_cut: 7,
        captured_at_millis: 11,
        checksum: blake3::hash(b"fixture").to_hex().to_string(),
        row_count: 0,
        placement: vec!["GRU".into()],
        retained_until_millis: 99,
    }
}
fn state(phase: NativeRecoveryPhase) -> NativeRecoveryOperation {
    NativeRecoveryOperation {
        operation_id: "op".into(),
        source_database_id: "db".into(),
        phase,
        facts: None,
        error: None,
    }
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
    assert_eq!(
        validate(Some(&original), "other", &["GRU".into()]),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert_eq!(
        validate(Some(&original), "db", &["SFO".into()]),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert!(Checkpoint::decode(br"{}").is_err());
}

#[test]
fn accepted_dispatch_captures_persists_measures_and_completes() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let calls = Cell::new(0);
    let mut phases = Vec::new();
    advance(
        &mut operation,
        None,
        &["GRU".into()],
        || {
            calls.set(calls.get() + 1);
            Ok(point())
        },
        |state, cp| {
            phases.push((state.phase.clone(), cp.is_some()));
            Ok(())
        },
        |_| Ok(facts()),
    )
    .unwrap();
    assert_eq!(calls.get(), 1);
    assert_eq!(
        phases,
        vec![
            (NativeRecoveryPhase::Capturing, false),
            (NativeRecoveryPhase::Measuring, true),
            (NativeRecoveryPhase::Complete, true)
        ]
    );
    assert_eq!(operation.phase, NativeRecoveryPhase::Complete);
    assert_eq!(operation.facts, Some(facts()));
}

#[test]
fn measuring_checkpoint_resumes_without_capture() {
    let mut operation = state(NativeRecoveryPhase::Measuring);
    let captures = Cell::new(0);
    advance(
        &mut operation,
        Some(point()),
        &["GRU".into()],
        || {
            captures.set(captures.get() + 1);
            Ok(point())
        },
        |_, _| Ok(()),
        |_| Ok(facts()),
    )
    .unwrap();
    assert_eq!(captures.get(), 0);
    assert_eq!(operation.phase, NativeRecoveryPhase::Complete);
}

#[test]
fn capturing_checkpoint_retry_enters_measuring_without_stale_error() {
    let mut operation = state(NativeRecoveryPhase::Capturing);
    operation.error = Some("transient failure".into());
    advance(
        &mut operation,
        Some(point()),
        &["GRU".into()],
        || panic!("capture after durable checkpoint"),
        |state, checkpoint| {
            assert!(checkpoint.is_some());
            match state.phase {
                NativeRecoveryPhase::Measuring => assert!(state.error.is_none()),
                NativeRecoveryPhase::Complete => assert_eq!(state.facts, Some(facts())),
                _ => panic!("unexpected phase persisted during retry"),
            }
            Ok(())
        },
        |_| Ok(facts()),
    )
    .unwrap();
}

#[test]
fn mismatched_measurement_facts_cannot_complete() {
    let mut operation = state(NativeRecoveryPhase::Measuring);
    let mut mismatched = facts();
    mismatched.journal_cut += 1;
    assert_eq!(
        advance(
            &mut operation,
            Some(point()),
            &["GRU".into()],
            || panic!("capture after durable checkpoint"),
            |_, _| Ok(()),
            |_| Ok(mismatched),
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert_eq!(operation.phase, NativeRecoveryPhase::Measuring);
    assert!(operation.facts.is_none());
}

#[test]
fn accepted_checkpoint_retry_does_not_capture_and_complete_replay_does_nothing() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let captures = Cell::new(0);
    let persists = Cell::new(0);
    let measures = Cell::new(0);
    advance(
        &mut operation,
        Some(point()),
        &["GRU".into()],
        || {
            captures.set(captures.get() + 1);
            Ok(point())
        },
        |_, _| {
            persists.set(persists.get() + 1);
            Ok(())
        },
        |_| {
            measures.set(measures.get() + 1);
            Ok(facts())
        },
    )
    .unwrap();
    operation.phase = NativeRecoveryPhase::Complete;
    advance(
        &mut operation,
        Some(point()),
        &["GRU".into()],
        || panic!("capture on complete"),
        |_, _| panic!("persist on complete"),
        |_| panic!("measure on complete"),
    )
    .unwrap();
    assert_eq!((captures.get(), persists.get(), measures.get()), (0, 2, 1));
}

#[test]
fn complete_replay_rejects_missing_or_mismatched_checkpoint() {
    let mut operation = state(NativeRecoveryPhase::Complete);
    operation.facts = Some(facts());
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || panic!("capture on complete"),
            |_, _| panic!("persist on complete"),
            |_| panic!("measure on complete"),
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
    let mut mismatched = point();
    mismatched.journal_cut += 1;
    assert_eq!(
        advance(
            &mut operation,
            Some(mismatched),
            &["GRU".into()],
            || panic!("capture on complete"),
            |_, _| panic!("persist on complete"),
            |_| panic!("measure on complete"),
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
}

#[test]
fn sqlite_reopen_resumes_measuring_checkpoint_without_capture() {
    let path = std::env::temp_dir().join(format!(
        "terrabase-native-recovery-checkpoint-{}-{}.sqlite",
        std::process::id(),
        std::thread::current().name().unwrap_or("test")
    ));
    let _ = std::fs::remove_file(&path);
    let connection = Connection::open(&path).unwrap();
    connection
        .execute_batch(
            "CREATE TABLE native_recovery_operations(
                operation_id TEXT PRIMARY KEY NOT NULL,
                source_database_id TEXT NOT NULL,
                phase TEXT NOT NULL,
                record BLOB NOT NULL,
                checkpoint BLOB
            );",
        )
        .unwrap();
    let mut operation = state(NativeRecoveryPhase::Capturing);
    connection
        .execute(
            "INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,'capturing',?3)",
            rusqlite::params![operation.operation_id, operation.source_database_id, serde_json::to_vec(&operation).unwrap()],
        )
        .unwrap();
    operation.phase = NativeRecoveryPhase::Measuring;
    crate::native_recovery::persistence::save(&connection, &operation, Some(&point())).unwrap();
    drop(connection);

    let reopened = Connection::open(&path).unwrap();
    let checkpoint = crate::native_recovery::persistence::load_checkpoint(&reopened, "db", "op")
        .unwrap()
        .unwrap();
    let bytes: Vec<u8> = reopened
        .query_row(
            "SELECT record FROM native_recovery_operations WHERE source_database_id='db' AND operation_id='op'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    let mut reloaded: NativeRecoveryOperation = serde_json::from_slice(&bytes).unwrap();
    let captures = Cell::new(0);
    advance(
        &mut reloaded,
        Some(checkpoint),
        &["GRU".into()],
        || {
            captures.set(captures.get() + 1);
            panic!("durable measuring checkpoint must not recapture")
        },
        |_, _| Ok(()),
        |_| Ok(facts()),
    )
    .unwrap();
    assert_eq!(captures.get(), 0);
    assert_eq!(reloaded.phase, NativeRecoveryPhase::Complete);
    let _ = std::fs::remove_file(&path);
}

#[test]
fn invalid_phase_or_missing_checkpoint_fails_without_capture() {
    for phase in [
        NativeRecoveryPhase::Capturing,
        NativeRecoveryPhase::Measuring,
        NativeRecoveryPhase::Failed,
    ] {
        let mut operation = state(phase);
        let captures = Cell::new(0);
        assert_eq!(
            advance(
                &mut operation,
                None,
                &["GRU".into()],
                || {
                    captures.set(captures.get() + 1);
                    Ok(point())
                },
                |_, _| Ok(()),
                |_| Ok(facts())
            ),
            Err(NativeRecoveryError::VerificationFailed)
        );
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
        let mut operation = state(NativeRecoveryPhase::Accepted);
        let callbacks = Cell::new(0);
        assert_eq!(
            advance(
                &mut operation,
                Some(invalid),
                &["GRU".into()],
                || {
                    callbacks.set(1);
                    Ok(point())
                },
                |_, _| {
                    callbacks.set(2);
                    Ok(())
                },
                |_| {
                    callbacks.set(3);
                    Ok(facts())
                }
            ),
            Err(NativeRecoveryError::VerificationFailed)
        );
        assert_eq!(callbacks.get(), 0);
    }
}

#[test]
fn capturing_failure_and_persist_failure_do_not_recapture() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let captures = Cell::new(0);
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                Err(NativeRecoveryError::VerificationFailed)
            },
            |_, _| Ok(()),
            |_| Ok(facts())
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert_eq!(captures.get(), 1);
    assert_eq!(operation.phase, NativeRecoveryPhase::Capturing);
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                Ok(point())
            },
            |_, _| Ok(()),
            |_| Ok(facts())
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert_eq!(captures.get(), 1);

    let mut operation = state(NativeRecoveryPhase::Accepted);
    let persists = Cell::new(0);
    let measures = Cell::new(0);
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || Ok(point()),
            |_, cp| {
                persists.set(persists.get() + 1);
                if cp.is_some() { Err(NativeRecoveryError::Unavailable) } else { Ok(()) }
            },
            |_| {
                measures.set(measures.get() + 1);
                Ok(facts())
            }
        ),
        Err(NativeRecoveryError::Unavailable)
    );
    assert_eq!((persists.get(), measures.get()), (2, 0));
}

#[test]
fn failures_reload_last_durable_state_without_recapture() {
    let mut operation = state(NativeRecoveryPhase::Accepted);
    let captures = Cell::new(0);
    let persisted = RefCell::new(None);
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                Ok(point())
            },
            |state, cp| {
                if cp.is_some() {
                    return Err(NativeRecoveryError::Unavailable);
                }
                *persisted.borrow_mut() = Some((state.clone(), cp.cloned()));
                Ok(())
            },
            |_| panic!("measure after failed checkpoint persist")
        ),
        Err(NativeRecoveryError::Unavailable)
    );
    let (mut reloaded, durable_cp) = persisted.into_inner().unwrap();
    assert_eq!(reloaded.phase, NativeRecoveryPhase::Capturing);
    assert!(durable_cp.is_none());
    assert_eq!(
        advance(
            &mut reloaded,
            durable_cp,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                Ok(point())
            },
            |_, _| Ok(()),
            |_| Ok(facts())
        ),
        Err(NativeRecoveryError::VerificationFailed)
    );
    assert_eq!(captures.get(), 1);

    let mut operation = state(NativeRecoveryPhase::Accepted);
    let captures = Cell::new(0);
    let persisted = RefCell::new(None);
    let fail_final = Cell::new(true);
    assert_eq!(
        advance(
            &mut operation,
            None,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                Ok(point())
            },
            |state, cp| {
                if fail_final.get() && state.phase == NativeRecoveryPhase::Complete {
                    fail_final.set(false);
                    return Err(NativeRecoveryError::Unavailable);
                }
                *persisted.borrow_mut() = Some((state.clone(), cp.cloned()));
                Ok(())
            },
            |_| Ok(facts())
        ),
        Err(NativeRecoveryError::Unavailable)
    );
    let (mut reloaded, durable_cp) = persisted.into_inner().unwrap();
    assert_eq!(reloaded.phase, NativeRecoveryPhase::Measuring);
    assert_eq!(durable_cp, Some(point()));
    assert_eq!(
        advance(
            &mut reloaded,
            durable_cp,
            &["GRU".into()],
            || {
                captures.set(captures.get() + 1);
                panic!("recapture after final persist failure")
            },
            |state, cp| {
                assert_eq!(state.phase, NativeRecoveryPhase::Complete);
                assert!(cp.is_some());
                Ok(())
            },
            |_| Ok(facts())
        ),
        Ok(())
    );
    assert_eq!(captures.get(), 1);
}

#[test]
fn measurement_failure_keeps_durable_checkpoint_and_measuring_phase() {
    let mut operation = state(NativeRecoveryPhase::Measuring);
    let persisted = RefCell::new(vec![(state(NativeRecoveryPhase::Measuring), Some(point()))]);
    assert_eq!(
        advance(
            &mut operation,
            Some(point()),
            &["GRU".into()],
            || panic!("capture after durable checkpoint"),
            |state, checkpoint| {
                persisted.borrow_mut().push((state.clone(), checkpoint.cloned()));
                Ok(())
            },
            |_| Err(NativeRecoveryError::Unavailable),
        ),
        Err(NativeRecoveryError::Unavailable)
    );
    assert_eq!(operation.phase, NativeRecoveryPhase::Measuring);
    assert!(operation.facts.is_none());
    assert_eq!(
        persisted.into_inner(),
        vec![(state(NativeRecoveryPhase::Measuring), Some(point()))]
    );
}
