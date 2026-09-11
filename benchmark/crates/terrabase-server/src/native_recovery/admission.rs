use super::{
    NativeRecoveryError, NativeRecoveryLimits, NativeRecoveryOperation, NativeRecoveryPhase,
};
use rusqlite::{Connection, OptionalExtension, params};

const MAX_RECORD_BYTES: i64 = 262_144;

fn persisted_record_size(size: i64) -> Result<usize, NativeRecoveryError> {
    let size = usize::try_from(size).map_err(|_| NativeRecoveryError::VerificationFailed)?;
    let max =
        usize::try_from(MAX_RECORD_BYTES).map_err(|_| NativeRecoveryError::VerificationFailed)?;
    if size > max {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    Ok(size)
}

pub(crate) fn admit_operation(
    connection: &mut Connection,
    limits: NativeRecoveryLimits,
    proposed: NativeRecoveryOperation,
) -> Result<NativeRecoveryOperation, NativeRecoveryError> {
    let limits = limits.validate()?;
    if proposed.phase != NativeRecoveryPhase::Accepted
        || proposed.facts.is_some()
        || proposed.error.is_some()
    {
        return Err(NativeRecoveryError::InvalidRequest);
    }
    let bytes = serde_json::to_vec(&proposed).map_err(|_| NativeRecoveryError::Unavailable)?;
    let max_record_bytes =
        usize::try_from(MAX_RECORD_BYTES).map_err(|_| NativeRecoveryError::Unavailable)?;
    let pending_limit = i64::try_from(limits.pending_operations)
        .map_err(|_| NativeRecoveryError::InvalidRequest)?;
    let retained_limit = i64::try_from(limits.retained_operations)
        .map_err(|_| NativeRecoveryError::InvalidRequest)?;
    if bytes.len() > max_record_bytes
        || proposed.source_database_id.is_empty()
        || proposed.operation_id.is_empty()
    {
        return Err(NativeRecoveryError::InvalidRequest);
    }
    let tx = connection.transaction().map_err(|_| NativeRecoveryError::Unavailable)?;
    let existing = tx.query_row(
        "SELECT source_database_id, phase, length(record), record FROM native_recovery_operations WHERE operation_id=?1",
        params![proposed.operation_id],
        |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?, row.get::<_, Vec<u8>>(3)?)),
    ).optional().map_err(|_| NativeRecoveryError::Unavailable)?;
    if let Some((source, phase, size, stored)) = existing {
        if source != proposed.source_database_id || persisted_record_size(size).is_err() {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let state: NativeRecoveryOperation =
            serde_json::from_slice(&stored).map_err(|_| NativeRecoveryError::VerificationFailed)?;
        let expected_phase = serde_json::to_string(&state.phase)
            .map_err(|_| NativeRecoveryError::VerificationFailed)?
            .trim_matches('"')
            .to_owned();
        if state.operation_id != proposed.operation_id
            || state.source_database_id != source
            || phase != expected_phase
            || (matches!(state.phase, NativeRecoveryPhase::Complete)
                && (state.facts.is_none() || state.error.is_some()))
            || (!matches!(state.phase, NativeRecoveryPhase::Complete) && state.facts.is_some())
        {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        if state.facts.as_ref().is_some_and(|facts| {
            facts.source_database_id != source
                || facts.recovery_point_id.is_empty()
                || facts.recovery_point_id.len() > 256
                || blake3::Hash::from_hex(&facts.checksum).is_err()
                || !(1..=20).contains(&facts.placement.len())
                || facts
                    .placement
                    .iter()
                    .any(|region| region.is_empty() || region.chars().any(char::is_control))
                || facts.retained_until_millis < facts.captured_at_millis
        }) {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        if matches!(state.phase, NativeRecoveryPhase::Failed) {
            let pending: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations WHERE phase IN ('accepted','capturing','measuring')", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
            if pending < 0 || pending >= pending_limit {
                return Err(NativeRecoveryError::Unavailable);
            }
            tx.execute("UPDATE native_recovery_operations SET phase='accepted', record=?2 WHERE operation_id=?1", params![proposed.operation_id, bytes]).map_err(|_| NativeRecoveryError::Unavailable)?;
            tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
            return Ok(proposed);
        }
        tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
        return Ok(state);
    }
    let pending: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations WHERE phase IN ('accepted','capturing','measuring')", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
    if pending < 0 || pending >= pending_limit {
        return Err(NativeRecoveryError::Unavailable);
    }
    let total: i64 = tx
        .query_row("SELECT count(*) FROM native_recovery_operations", [], |row| row.get(0))
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    if total < 0 {
        return Err(NativeRecoveryError::Unavailable);
    }
    if total >= retained_limit {
        tx.execute("DELETE FROM native_recovery_operations WHERE operation_id=(SELECT operation_id FROM native_recovery_operations WHERE phase IN ('complete','failed') ORDER BY operation_id LIMIT 1)", []).map_err(|_| NativeRecoveryError::Unavailable)?;
    }
    let total_after: i64 = tx
        .query_row("SELECT count(*) FROM native_recovery_operations", [], |row| row.get(0))
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    if total_after < 0 || total_after >= retained_limit {
        return Err(NativeRecoveryError::Unavailable);
    }
    tx.execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,'accepted',?3)", params![proposed.operation_id, proposed.source_database_id, bytes]).map_err(|_| NativeRecoveryError::Unavailable)?;
    tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
    Ok(proposed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::native_recovery::RecoveryPointFacts;
    fn db() -> Connection {
        let connection = Connection::open_in_memory().unwrap();
        connection.execute_batch("CREATE TABLE native_recovery_operations(operation_id TEXT PRIMARY KEY, source_database_id TEXT NOT NULL, phase TEXT NOT NULL, record BLOB NOT NULL CHECK(length(record)<=262144));").unwrap();
        connection
    }
    fn op(id: &str, source: &str, phase: NativeRecoveryPhase) -> NativeRecoveryOperation {
        NativeRecoveryOperation {
            operation_id: id.into(),
            source_database_id: source.into(),
            phase,
            facts: None,
            error: None,
        }
    }
    fn complete(id: &str, source: &str) -> NativeRecoveryOperation {
        NativeRecoveryOperation {
            operation_id: id.into(),
            source_database_id: source.into(),
            phase: NativeRecoveryPhase::Complete,
            facts: Some(RecoveryPointFacts {
                recovery_point_id: format!("rp-{id}"),
                source_database_id: source.into(),
                journal_cut: 7,
                captured_at_millis: 100,
                checksum: blake3::hash(id.as_bytes()).to_hex().to_string(),
                row_count: 2,
                placement: vec!["GRU".into()],
                retained_until_millis: 200,
            }),
            error: None,
        }
    }
    fn store_as(
        c: &Connection,
        row_id: &str,
        state: &NativeRecoveryOperation,
        source: &str,
        phase: &str,
        record: Option<Vec<u8>>,
    ) {
        let bytes = record.unwrap_or_else(|| serde_json::to_vec(state).unwrap());
        c.execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,?3,?4)", params![row_id, source, phase, bytes]).unwrap();
    }
    fn store(
        c: &Connection,
        state: &NativeRecoveryOperation,
        source: &str,
        phase: &str,
        record: Option<Vec<u8>>,
    ) {
        store_as(c, &state.operation_id, state, source, phase, record);
    }
    fn ids(c: &Connection) -> Vec<String> {
        c.prepare("SELECT operation_id FROM native_recovery_operations ORDER BY operation_id")
            .unwrap()
            .query_map([], |r| r.get(0))
            .unwrap()
            .map(Result::unwrap)
            .collect()
    }
    fn limits(pending: usize, total: usize) -> NativeRecoveryLimits {
        NativeRecoveryLimits { pending_operations: pending, retained_operations: total }
    }
    #[test]
    fn pending_full_rejects_without_mutation() {
        let mut c = db();
        store(&c, &op("a", "db", NativeRecoveryPhase::Accepted), "db", "accepted", None);
        store(&c, &complete("t", "db"), "db", "complete", None);
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("b", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::Unavailable)
        );
        assert_eq!(ids(&c), vec!["a", "t"]);
    }
    #[test]
    fn total_bound_prunes_oldest_terminal_only() {
        let mut c = db();
        store(&c, &complete("a", "db"), "db", "complete", None);
        store(&c, &complete("b", "db"), "db", "complete", None);
        store(&c, &op("p", "db", NativeRecoveryPhase::Accepted), "db", "accepted", None);
        admit_operation(&mut c, limits(2, 3), op("n", "db", NativeRecoveryPhase::Accepted))
            .unwrap();
        assert_eq!(ids(&c), vec!["b", "n", "p"]);
    }
    #[test]
    fn complete_replay_when_full_has_no_eviction_or_new_effect() {
        let mut c = db();
        let state = complete("a", "db");
        store(&c, &state, "db", "complete", None);
        store(&c, &complete("b", "db"), "db", "complete", None);
        store(&c, &op("p", "db", NativeRecoveryPhase::Accepted), "db", "accepted", None);
        assert_eq!(
            admit_operation(&mut c, limits(1, 3), op("a", "db", NativeRecoveryPhase::Accepted))
                .unwrap(),
            state
        );
        assert_eq!(ids(&c), vec!["a", "b", "p"]);
    }
    #[test]
    fn failed_retry_accepts_and_full_pending_retry_stays_failed() {
        let mut c = db();
        let mut failed = op("a", "db", NativeRecoveryPhase::Failed);
        failed.error = Some("failure".into());
        store(&c, &failed, "db", "failed", None);
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("a", "db", NativeRecoveryPhase::Accepted))
                .unwrap()
                .phase,
            NativeRecoveryPhase::Accepted
        );
        let (phase, record) = c
            .query_row(
                "SELECT phase, record FROM native_recovery_operations WHERE operation_id='a'",
                [],
                |r| Ok((r.get::<_, String>(0)?, r.get::<_, Vec<u8>>(1)?)),
            )
            .unwrap();
        assert_eq!(phase, "accepted");
        assert_eq!(
            serde_json::from_slice::<NativeRecoveryOperation>(&record).unwrap(),
            op("a", "db", NativeRecoveryPhase::Accepted)
        );
        let mut c = db();
        let mut failed = op("b", "db", NativeRecoveryPhase::Failed);
        failed.error = Some("failure".into());
        store(&c, &failed, "db", "failed", None);
        store(&c, &op("p", "db", NativeRecoveryPhase::Accepted), "db", "accepted", None);
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("b", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::Unavailable)
        );
        assert_eq!(
            c.query_row(
                "SELECT phase FROM native_recovery_operations WHERE operation_id='b'",
                [],
                |r| r.get::<_, String>(0)
            )
            .unwrap(),
            "failed"
        );
        let record: Vec<u8> = c
            .query_row(
                "SELECT record FROM native_recovery_operations WHERE operation_id='b'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(serde_json::from_slice::<NativeRecoveryOperation>(&record).unwrap(), failed);
    }
    #[test]
    fn source_record_operation_and_phase_mismatches_are_rejected() {
        for (state, source, phase) in [
            (op("a", "other", NativeRecoveryPhase::Accepted), "db", "accepted"),
            (op("other", "db", NativeRecoveryPhase::Accepted), "db", "accepted"),
            (op("a", "db", NativeRecoveryPhase::Accepted), "db", "failed"),
        ] {
            let mut c = db();
            if state.operation_id == "other" {
                store_as(&c, "a", &state, source, phase, None);
            } else {
                store(&c, &state, source, phase, None);
            }
            assert_eq!(
                admit_operation(&mut c, limits(1, 2), op("a", "db", NativeRecoveryPhase::Accepted)),
                Err(NativeRecoveryError::VerificationFailed)
            );
        }
    }
    #[test]
    fn complete_facts_source_mismatch_is_rejected() {
        let mut c = db();
        let mut state = complete("a", "db");
        state.facts.as_mut().unwrap().source_database_id = "other".into();
        store(&c, &state, "db", "complete", None);
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("a", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::VerificationFailed)
        );
    }
    #[test]
    fn malformed_and_oversized_records_are_rejected() {
        let mut c = db();
        store(
            &c,
            &op("a", "db", NativeRecoveryPhase::Accepted),
            "db",
            "accepted",
            Some(b"not-json".to_vec()),
        );
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("a", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::VerificationFailed)
        );
        let mut c = db();
        c.execute_batch("PRAGMA ignore_check_constraints=ON;").unwrap();
        store(
            &c,
            &op("a", "db", NativeRecoveryPhase::Accepted),
            "db",
            "accepted",
            Some(vec![b'x'; usize::try_from(MAX_RECORD_BYTES).unwrap() + 1]),
        );
        assert_eq!(
            admit_operation(&mut c, limits(1, 2), op("a", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::VerificationFailed)
        );
    }
    #[test]
    fn failed_insert_rolls_back_terminal_pruning() {
        let mut c = db();
        store(&c, &complete("a", "db"), "db", "complete", None);
        store(&c, &op("p", "db", NativeRecoveryPhase::Accepted), "db", "accepted", None);
        c.execute_batch("CREATE TRIGGER reject_new BEFORE INSERT ON native_recovery_operations WHEN NEW.operation_id='n' BEGIN SELECT RAISE(ABORT, 'blocked'); END;").unwrap();
        assert_eq!(
            admit_operation(&mut c, limits(2, 2), op("n", "db", NativeRecoveryPhase::Accepted)),
            Err(NativeRecoveryError::Unavailable)
        );
        assert_eq!(ids(&c), vec!["a", "p"]);
    }
    #[test]
    fn invalid_proposed_phase_facts_error_and_limits_have_no_effect() {
        for proposed in [
            op("n", "db", NativeRecoveryPhase::Capturing),
            {
                let mut x = op("n", "db", NativeRecoveryPhase::Accepted);
                x.facts = Some(complete("x", "db").facts.unwrap());
                x
            },
            {
                let mut x = op("n", "db", NativeRecoveryPhase::Accepted);
                x.error = Some("bad".into());
                x
            },
        ] {
            let mut c = db();
            assert_eq!(
                admit_operation(&mut c, limits(1, 2), proposed),
                Err(NativeRecoveryError::InvalidRequest)
            );
            assert!(ids(&c).is_empty());
        }
        for bound in [limits(0, 1), limits(2, 1), limits(1, 10_001)] {
            let mut c = db();
            assert_eq!(
                admit_operation(&mut c, bound, op("n", "db", NativeRecoveryPhase::Accepted)),
                Err(NativeRecoveryError::InvalidRequest)
            );
            assert!(ids(&c).is_empty());
        }
    }

    #[test]
    fn persisted_size_and_limits_reject_signed_or_unrepresentable_values() {
        assert_eq!(persisted_record_size(-1), Err(NativeRecoveryError::VerificationFailed));
        assert_eq!(persisted_record_size(i64::MAX), Err(NativeRecoveryError::VerificationFailed));
        assert_eq!(
            persisted_record_size(MAX_RECORD_BYTES),
            Ok(usize::try_from(MAX_RECORD_BYTES).unwrap())
        );

        #[cfg(target_pointer_width = "64")]
        assert!(i64::try_from(usize::MAX).is_err());
        #[cfg(target_pointer_width = "32")]
        assert!(i64::try_from(usize::MAX).is_ok());
    }
}
