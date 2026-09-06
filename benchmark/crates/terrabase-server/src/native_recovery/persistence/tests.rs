use super::*;
use crate::native_recovery::{NativeRecoveryPhase, RecoveryPointFacts};
use std::path::PathBuf;
use std::{fs, os::unix::fs::PermissionsExt};

struct Database {
    dir: PathBuf,
    path: PathBuf,
}
impl Database {
    fn open(&self) -> Connection {
        let connection = Connection::open(&self.path).unwrap();
        connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;").unwrap();
        connection
    }
}
impl Drop for Database {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.dir);
    }
}
fn database() -> Database {
    let dir = std::env::temp_dir().join(format!(
        "terrascale-recovery-{}-{}",
        std::process::id(),
        blake3::hash(format!("{:?}", std::time::SystemTime::now()).as_bytes()).to_hex()
    ));
    fs::create_dir(&dir).unwrap();
    fs::set_permissions(&dir, fs::Permissions::from_mode(0o700)).unwrap();
    let path = dir.join("recovery.sqlite");
    let connection = Connection::open(&path).unwrap();
    connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;").unwrap();
    connection.execute_batch("CREATE TABLE native_recovery_operations(operation_id TEXT PRIMARY KEY NOT NULL, source_database_id TEXT NOT NULL, phase TEXT NOT NULL DEFAULT 'accepted', record BLOB NOT NULL CHECK(length(record)<=262144), checkpoint BLOB CHECK(checkpoint IS NULL OR length(checkpoint)<=262144)) WITHOUT ROWID;").unwrap();
    drop(connection);
    Database { dir, path }
}
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
fn operation(phase: NativeRecoveryPhase) -> NativeRecoveryOperation {
    NativeRecoveryOperation {
        operation_id: "op".into(),
        source_database_id: "db".into(),
        phase,
        facts: None,
        error: None,
    }
}
fn complete() -> NativeRecoveryOperation {
    NativeRecoveryOperation {
        phase: NativeRecoveryPhase::Complete,
        facts: Some(RecoveryPointFacts {
            recovery_point_id: "rp".into(),
            source_database_id: "db".into(),
            journal_cut: 7,
            captured_at_millis: 11,
            checksum: blake3::hash(b"fixture").to_hex().to_string(),
            row_count: 0,
            placement: vec!["GRU".into()],
            retained_until_millis: 99,
        }),
        ..operation(NativeRecoveryPhase::Complete)
    }
}
fn insert(c: &Connection, state: &NativeRecoveryOperation) {
    c.execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,?3,?4)", rusqlite::params![state.operation_id, state.source_database_id, serde_json::to_string(&state.phase).unwrap().trim_matches('"'), serde_json::to_vec(state).unwrap()]).unwrap();
}

#[test]
fn save_and_load_checkpoint_survive_close_and_reopen() {
    let database = database();
    let c = database.open();
    let mut state = operation(NativeRecoveryPhase::Capturing);
    insert(&c, &state);
    save(&c, &state, None).unwrap();
    state.phase = NativeRecoveryPhase::Measuring;
    save(&c, &state, Some(&point())).unwrap();
    drop(c);
    let reopened = database.open();
    assert_eq!(load_checkpoint(&reopened, "db", "op").unwrap(), Some(point()));
    let (phase, record) = reopened
        .query_row(
            "SELECT phase, record FROM native_recovery_operations WHERE operation_id='op'",
            [],
            |r| Ok((r.get::<_, String>(0)?, r.get::<_, Vec<u8>>(1)?)),
        )
        .unwrap();
    assert_eq!(phase, "measuring");
    assert!(serde_json::from_slice::<NativeRecoveryOperation>(&record).unwrap().facts.is_none());
    drop(reopened);
}

#[test]
fn complete_save_round_trips_facts_and_checkpoint() {
    let database = database();
    let c = database.open();
    let state = complete();
    insert(&c, &operation(NativeRecoveryPhase::Accepted));
    save(&c, &state, Some(&point())).unwrap();
    let record: Vec<u8> = c
        .query_row(
            "SELECT record FROM native_recovery_operations WHERE operation_id='op'",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(serde_json::from_slice::<NativeRecoveryOperation>(&record).unwrap(), state);
    assert_eq!(load_checkpoint(&c, "db", "op").unwrap(), Some(point()));
}

#[test]
fn update_abort_preserves_phase_record_and_checkpoint_atomically() {
    let database = database();
    let c = database.open();
    let state = operation(NativeRecoveryPhase::Capturing);
    insert(&c, &state);
    let prior: (String, Vec<u8>, Option<Vec<u8>>) = c
        .query_row("SELECT phase,record,checkpoint FROM native_recovery_operations", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?))
        })
        .unwrap();
    c.execute_batch("CREATE TRIGGER reject_measure BEFORE UPDATE ON native_recovery_operations WHEN NEW.phase='measuring' BEGIN SELECT RAISE(ABORT, 'blocked'); END;").unwrap();
    let mut next = operation(NativeRecoveryPhase::Measuring);
    assert_eq!(save(&c, &next, Some(&point())), Err(NativeRecoveryError::Unavailable));
    let after: (String, Vec<u8>, Option<Vec<u8>>) = c
        .query_row("SELECT phase,record,checkpoint FROM native_recovery_operations", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?))
        })
        .unwrap();
    assert_eq!(after, prior);
}

#[test]
fn wrong_source_or_operation_refuses_without_mutation() {
    let database = database();
    let c = database.open();
    let state = operation(NativeRecoveryPhase::Capturing);
    insert(&c, &state);
    let before: Vec<u8> =
        c.query_row("SELECT record FROM native_recovery_operations", [], |r| r.get(0)).unwrap();
    let mut wrong = operation(NativeRecoveryPhase::Measuring);
    wrong.source_database_id = "other".into();
    assert_eq!(save(&c, &wrong, Some(&point())), Err(NativeRecoveryError::Unavailable));
    wrong.source_database_id = "db".into();
    wrong.operation_id = "other".into();
    assert_eq!(save(&c, &wrong, Some(&point())), Err(NativeRecoveryError::Unavailable));
    assert_eq!(
        c.query_row("SELECT record FROM native_recovery_operations", [], |r| r
            .get::<_, Vec<u8>>(0))
            .unwrap(),
        before
    );
}

#[test]
fn record_only_preserves_malformed_checkpoint_and_load_distinguishes_null_missing() {
    let database = database();
    let c = database.open();
    let mut failed = operation(NativeRecoveryPhase::Failed);
    failed.error = Some("safe".into());
    insert(&c, &failed);
    c.execute(
        "UPDATE native_recovery_operations SET checkpoint=?2 WHERE operation_id=?1",
        rusqlite::params!["op", b"bad"],
    )
    .unwrap();
    let replacement = failed.clone();
    save_record_only(&c, &replacement).unwrap();
    assert_eq!(load_checkpoint(&c, "db", "op"), Err(NativeRecoveryError::VerificationFailed));
    c.execute("UPDATE native_recovery_operations SET checkpoint=NULL", []).unwrap();
    assert_eq!(load_checkpoint(&c, "db", "op").unwrap(), None);
    assert_eq!(load_checkpoint(&c, "db", "missing"), Err(NativeRecoveryError::Unavailable));
}

#[test]
fn oversized_checkpoint_is_rejected_on_load() {
    let database = database();
    let c = database.open();
    let state = operation(NativeRecoveryPhase::Capturing);
    insert(&c, &state);
    c.execute_batch("PRAGMA ignore_check_constraints=ON;").unwrap();
    c.execute(
        "UPDATE native_recovery_operations SET checkpoint=?2 WHERE operation_id=?1",
        rusqlite::params!["op", vec![b'x'; 262145]],
    )
    .unwrap();
    assert_eq!(load_checkpoint(&c, "db", "op"), Err(NativeRecoveryError::VerificationFailed));
}
