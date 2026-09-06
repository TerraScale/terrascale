//! Durable logical storage accounting. The document table contains both current
//! and retained revisions; indexes, replication copies, and SQLite pages do not
//! enter this ledger. Triggers record only changed revisions. TTL changes live
//! visibility; a retained revision continues accruing storage until pruning.

use rusqlite::{Connection, OptionalExtension};

const SCHEMA: &str = r#"
CREATE TABLE IF NOT EXISTS storage_accounting_clock (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1), millis INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS storage_accounting_coverage (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1), start_millis INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS storage_accounting_stops (
    tenant TEXT PRIMARY KEY, millis INTEGER NOT NULL
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS storage_accounting_revisions (
    tenant TEXT NOT NULL, collection TEXT NOT NULL, id TEXT NOT NULL,
    sequence INTEGER NOT NULL, bytes INTEGER NOT NULL CHECK(bytes >= 0),
    start_millis INTEGER NOT NULL, end_millis INTEGER,
    PRIMARY KEY(tenant, collection, id, sequence)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS storage_accounting_window
    ON storage_accounting_revisions(tenant, start_millis);
CREATE TABLE IF NOT EXISTS storage_accounting_events (
    tenant TEXT NOT NULL, millis INTEGER NOT NULL, delta INTEGER NOT NULL,
    PRIMARY KEY(tenant, millis)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS storage_accounting_gauges (
    tenant TEXT PRIMARY KEY, millis INTEGER NOT NULL,
    bytes INTEGER NOT NULL CHECK(bytes >= 0), byte_millis TEXT NOT NULL
) WITHOUT ROWID;
CREATE TRIGGER IF NOT EXISTS storage_accounting_insert AFTER INSERT ON documents
WHEN NEW.deleted = 0 AND NOT EXISTS (
    SELECT 1 FROM storage_accounting_stops WHERE tenant = NEW.tenant
)
BEGIN
    INSERT INTO storage_accounting_revisions
    SELECT NEW.tenant, NEW.collection, NEW.id, NEW.commit_seq, length(NEW.body), millis,
           NULL
      FROM storage_accounting_clock WHERE singleton = 1;
    INSERT INTO storage_accounting_events
    SELECT NEW.tenant, millis, length(NEW.body) FROM storage_accounting_clock
     WHERE singleton = 1
    ON CONFLICT(tenant, millis) DO UPDATE SET delta = delta + excluded.delta;
END;
CREATE TRIGGER IF NOT EXISTS storage_accounting_delete AFTER DELETE ON documents
WHEN OLD.deleted = 0
BEGIN
    INSERT INTO storage_accounting_events
    SELECT r.tenant, c.millis, -r.bytes
      FROM storage_accounting_revisions r, storage_accounting_clock c
     WHERE r.tenant = OLD.tenant AND r.collection = OLD.collection AND r.id = OLD.id
       AND r.sequence = OLD.commit_seq AND c.singleton = 1
       AND (r.end_millis IS NULL OR r.end_millis > c.millis)
    ON CONFLICT(tenant, millis) DO UPDATE SET delta = delta + excluded.delta;
    UPDATE storage_accounting_revisions
       SET end_millis = MIN(COALESCE(end_millis, (SELECT millis FROM storage_accounting_clock)),
                            (SELECT millis FROM storage_accounting_clock))
     WHERE tenant = OLD.tenant AND collection = OLD.collection AND id = OLD.id
       AND sequence = OLD.commit_seq;
END;
"#;

/// Initialize once, after document migrations. Existing data begins accruing at
/// admission to this accounting format: unavailable historical timing is never
/// invented. Subsequent opens preserve the gauge and revision cuts.
pub(super) fn initialize(connection: &Connection) -> Result<(), ()> {
    let transaction = connection.unchecked_transaction().map_err(|_| ())?;
    initialize_at(&transaction, super::unix_millis().max(0) as u64)?;
    transaction.commit().map_err(|_| ())
}

pub(super) fn initialize_at(connection: &Connection, now: u64) -> Result<(), ()> {
    connection.execute_batch(SCHEMA).map_err(|_| ())?;
    connection
        .execute(
            "INSERT OR IGNORE INTO storage_accounting_coverage VALUES (1, ?1)",
            [i64::try_from(now).map_err(|_| ())?],
        )
        .map_err(|_| ())?;
    let first = connection
        .execute(
            "INSERT OR IGNORE INTO storage_accounting_clock VALUES (1, ?1)",
            [i64::try_from(now).map_err(|_| ())?],
        )
        .map_err(|_| ())?;
    if first != 0 {
        connection
            .execute(
                "INSERT INTO storage_accounting_revisions
             SELECT tenant, collection, id, commit_seq, length(body), ?1,
                    NULL
               FROM documents WHERE deleted = 0",
                [now as i64],
            )
            .map_err(|_| ())?;
        connection
            .execute(
                "INSERT INTO storage_accounting_events
             SELECT tenant, ?1, SUM(bytes) FROM storage_accounting_revisions
              WHERE end_millis IS NULL OR end_millis > ?1 GROUP BY tenant",
                [now as i64],
            )
            .map_err(|_| ())?;
    }
    Ok(())
}

/// Set the mutation clock in the caller's write transaction. Persisted clamping
/// prevents clock rollback, restart, or a repeated close from adding elapsed
/// time twice. Use actual local storage time rather than remote commit HLCs.
pub(super) fn begin_change(connection: &Connection, now: u64) -> Result<u64, ()> {
    let now = i64::try_from(now).map_err(|_| ())?;
    connection
        .execute(
            "UPDATE storage_accounting_clock SET millis = MAX(millis, ?1) WHERE singleton = 1",
            [now],
        )
        .map_err(|_| ())?;
    connection
        .query_row("SELECT millis FROM storage_accounting_clock WHERE singleton = 1", [], |row| {
            row.get::<_, i64>(0)
        })
        .map_err(|_| ())
        .and_then(|value| u64::try_from(value).map_err(|_| ()))
}

/// Advance one database's durable current-byte gauge and cumulative byte-time.
/// Only its pending mutation transitions are visited, never documents.
pub(super) fn settle(connection: &Connection, tenant: &str, now: u64) -> Result<(), ()> {
    let now = i64::try_from(now).map_err(|_| ())?;
    let prior = connection
        .query_row(
            "SELECT millis, bytes, byte_millis FROM storage_accounting_gauges WHERE tenant = ?1",
            [tenant],
            |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?, row.get::<_, String>(2)?)),
        )
        .optional()
        .map_err(|_| ())?;
    let (mut time, mut bytes, mut total) = match prior {
        Some((time, bytes, total)) => (time, bytes, total.parse::<u128>().map_err(|_| ())?),
        None => (0, 0, 0),
    };
    let now = now.max(time);
    let mut statement = connection
        .prepare(
            "SELECT millis, delta FROM storage_accounting_events
         WHERE tenant = ?1 AND millis <= ?2 ORDER BY millis",
        )
        .map_err(|_| ())?;
    let events = statement
        .query_map(rusqlite::params![tenant, now], |row| {
            Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?))
        })
        .map_err(|_| ())?;
    for event in events {
        let (at, delta) = event.map_err(|_| ())?;
        if at < time {
            return Err(());
        }
        total = add_elapsed(total, bytes, at - time)?;
        bytes = bytes.checked_add(delta).filter(|value| *value >= 0).ok_or(())?;
        time = at;
    }
    total = add_elapsed(total, bytes, now - time)?;
    drop(statement);
    connection
        .execute(
            "INSERT INTO storage_accounting_gauges VALUES (?1, ?2, ?3, ?4)
         ON CONFLICT(tenant) DO UPDATE SET millis = excluded.millis,
             bytes = excluded.bytes, byte_millis = excluded.byte_millis",
            rusqlite::params![tenant, now, bytes, total.to_string()],
        )
        .map_err(|_| ())?;
    connection
        .execute(
            "DELETE FROM storage_accounting_events WHERE tenant = ?1 AND millis <= ?2",
            rusqlite::params![tenant, now],
        )
        .map_err(|_| ())?;
    Ok(())
}

fn add_elapsed(total: u128, bytes: i64, millis: i64) -> Result<u128, ()> {
    let bytes = u128::try_from(bytes).map_err(|_| ())?;
    let millis = u128::try_from(millis).map_err(|_| ())?;
    total.checked_add(bytes.checked_mul(millis).ok_or(())?).ok_or(())
}

/// Settle pending mutation and pruning transitions during periodic maintenance.
pub(super) fn settle_due(connection: &Connection, now: u64) -> Result<(), ()> {
    let mut statement = connection
        .prepare(
            "SELECT tenant FROM storage_accounting_events WHERE millis <= ?1
                  UNION SELECT tenant FROM storage_accounting_gauges",
        )
        .map_err(|_| ())?;
    let tenants = statement
        .query_map([i64::try_from(now).map_err(|_| ())?], |row| row.get::<_, String>(0))
        .map_err(|_| ())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ())?;
    drop(statement);
    for tenant in tenants {
        settle(connection, &tenant, now)?;
    }
    Ok(())
}

/// Exact historical cycle integration. The monthly close reads retained usage
/// timelines, not mutable documents. Milliseconds are retained until the caller
/// applies the trusted Region policy and rounds once for the complete fact.
pub(super) fn byte_millis(
    connection: &Connection,
    tenant: &str,
    start: u64,
    end: u64,
) -> Result<u128, ()> {
    if start >= end {
        return Err(());
    }
    let start = i64::try_from(start).map_err(|_| ())?;
    let end = i64::try_from(end).map_err(|_| ())?;
    let coverage: i64 = connection
        .query_row(
            "SELECT start_millis FROM storage_accounting_coverage WHERE singleton = 1",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if start < coverage {
        return Err(());
    }
    let clock: i64 = connection
        .query_row("SELECT millis FROM storage_accounting_clock WHERE singleton = 1", [], |row| {
            row.get(0)
        })
        .map_err(|_| ())?;
    if end > clock {
        return Err(());
    }
    let mut statement = connection
        .prepare(
            "SELECT bytes, MAX(start_millis, ?2), MIN(COALESCE(end_millis, ?3), ?3)
           FROM storage_accounting_revisions
          WHERE tenant = ?1 AND start_millis < ?3 AND (end_millis IS NULL OR end_millis > ?2)",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map(rusqlite::params![tenant, start, end], |row| {
            Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?, row.get::<_, i64>(2)?))
        })
        .map_err(|_| ())?;
    let mut total = 0;
    for row in rows {
        let (bytes, from, to) = row.map_err(|_| ())?;
        total = add_elapsed(total, bytes, (to - from).max(0))?;
    }
    Ok(total)
}

/// Stop charges at accepted database deletion, even while physical erasure or
/// backup cleanup continues. Caller commits this marker with lifecycle state.
pub(super) fn stop(connection: &Connection, tenant: &str, at: u64) -> Result<(), ()> {
    let accepted = i64::try_from(at).map_err(|_| ())?;
    let prior: Option<i64> = connection
        .query_row(
            "SELECT millis FROM storage_accounting_stops WHERE tenant = ?1",
            [tenant],
            |row| row.get(0),
        )
        .optional()
        .map_err(|_| ())?;
    if let Some(prior) = prior {
        return if prior == accepted { Ok(()) } else { Err(()) };
    }
    let clock: i64 = connection
        .query_row("SELECT millis FROM storage_accounting_clock WHERE singleton = 1", [], |row| {
            row.get(0)
        })
        .map_err(|_| ())?;
    if accepted < clock {
        return Err(());
    }
    let at = begin_change(connection, at)?;
    settle(connection, tenant, at)?;
    let inserted = connection
        .execute(
            "INSERT OR IGNORE INTO storage_accounting_stops VALUES (?1, ?2)",
            rusqlite::params![tenant, at as i64],
        )
        .map_err(|_| ())?;
    if inserted == 0 {
        return Ok(());
    }
    connection
        .execute(
            "UPDATE storage_accounting_revisions SET end_millis = MIN(COALESCE(end_millis, ?2), ?2)
          WHERE tenant = ?1",
            rusqlite::params![tenant, at as i64],
        )
        .map_err(|_| ())?;
    connection
        .execute("DELETE FROM storage_accounting_events WHERE tenant = ?1", [tenant])
        .map_err(|_| ())?;
    connection
        .execute("UPDATE storage_accounting_gauges SET bytes = 0 WHERE tenant = ?1", [tenant])
        .map_err(|_| ())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn database() -> Connection {
        let connection = Connection::open_in_memory().unwrap();
        connection
            .execute_batch(
                "CREATE TABLE documents(tenant TEXT, collection TEXT, id TEXT,
             commit_seq INTEGER, body BLOB, deleted INTEGER, expires_at INTEGER);",
            )
            .unwrap();
        initialize_at(&connection, 1_000).unwrap();
        connection
    }

    fn put(connection: &Connection, seq: i64, ttl: Option<i64>) {
        connection
            .execute(
                "INSERT INTO documents VALUES ('db', 'c', 'id', ?1, X'010203', 0, ?2)",
                rusqlite::params![seq, ttl],
            )
            .unwrap();
    }

    #[test]
    fn incomplete_historical_coverage_is_refused_even_for_empty_database() {
        let connection = database();
        begin_change(&connection, 3_000).unwrap();
        assert_eq!(byte_millis(&connection, "db", 999, 3_000), Err(()));
        assert_eq!(byte_millis(&connection, "db", 1_000, 3_000), Ok(0));
        initialize_at(&connection, 4_000).unwrap();
        assert_eq!(byte_millis(&connection, "db", 999, 3_000), Err(()));
        assert_eq!(byte_millis(&connection, "db", 1_000, 3_000), Ok(0));
    }

    #[test]
    fn ttl_retains_billable_history_until_actual_pruning() {
        let connection = database();
        put(&connection, 1, None);
        settle(&connection, "db", 1_000).unwrap();
        begin_change(&connection, 2_000).unwrap();
        put(&connection, 2, Some(4_000));
        settle(&connection, "db", 2_000).unwrap();
        begin_change(&connection, 3_000).unwrap();
        connection.execute("DELETE FROM documents WHERE commit_seq = 1", []).unwrap();
        settle_due(&connection, 3_000).unwrap();
        begin_change(&connection, 5_000).unwrap();
        settle_due(&connection, 5_000).unwrap();
        assert_eq!(byte_millis(&connection, "db", 1_000, 5_000), Ok(15_000));
        assert_eq!(byte_millis(&connection, "db", 2_500, 3_500), Ok(4_500));
        let gauge: (i64, String) = connection
            .query_row(
                "SELECT bytes, byte_millis FROM storage_accounting_gauges WHERE tenant = 'db'",
                [],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .unwrap();
        assert_eq!(gauge, (3, "15000".to_owned()));
    }

    #[test]
    fn rollback_restart_repeated_close_and_clock_rollback_preserve_totals() {
        let mut connection = database();
        {
            let transaction = connection.transaction().unwrap();
            put(&transaction, 1, None);
            settle(&transaction, "db", 1_000).unwrap();
        }
        assert_eq!(
            connection
                .query_row::<i64, _, _>(
                    "SELECT COUNT(*) FROM storage_accounting_revisions",
                    [],
                    |row| row.get(0),
                )
                .unwrap(),
            0
        );
        put(&connection, 1, None);
        settle(&connection, "db", 1_000).unwrap();
        begin_change(&connection, 3_000).unwrap();
        settle(&connection, "db", 3_000).unwrap();
        initialize_at(&connection, 2_000).unwrap();
        assert_eq!(begin_change(&connection, 2_000), Ok(3_000));
        settle(&connection, "db", 3_000).unwrap();
        assert_eq!(byte_millis(&connection, "db", 1_000, 3_000), Ok(6_000));
        assert_eq!(byte_millis(&connection, "db", 1_000, 3_000), Ok(6_000));
        stop(&connection, "db", 3_000).unwrap();
        stop(&connection, "db", 3_000).unwrap();
        assert_eq!(stop(&connection, "db", 4_000), Err(()));
        put(&connection, 2, None);
        begin_change(&connection, 5_000).unwrap();
        assert_eq!(byte_millis(&connection, "db", 1_000, 5_000), Ok(6_000));
    }

    #[test]
    fn durable_reopen_retains_pruned_revision_evidence() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-storage-accounting-{}-{}.sqlite3",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        {
            let mut connection = Connection::open(&path).unwrap();
            connection
                .execute_batch(
                    "CREATE TABLE documents(tenant TEXT, collection TEXT, id TEXT,
                 commit_seq INTEGER, body BLOB, deleted INTEGER, expires_at INTEGER);",
                )
                .unwrap();
            let transaction = connection.transaction().unwrap();
            initialize_at(&transaction, 1_000).unwrap();
            put(&transaction, 1, Some(4_000));
            settle(&transaction, "db", 1_000).unwrap();
            transaction.commit().unwrap();
        }
        {
            let mut connection = Connection::open(&path).unwrap();
            let transaction = connection.transaction().unwrap();
            initialize_at(&transaction, 2_000).unwrap();
            begin_change(&transaction, 3_000).unwrap();
            transaction.execute("DELETE FROM documents", []).unwrap();
            settle_due(&transaction, 3_000).unwrap();
            transaction.commit().unwrap();
        }
        {
            let mut connection = Connection::open(&path).unwrap();
            let transaction = connection.transaction().unwrap();
            initialize_at(&transaction, 5_000).unwrap();
            begin_change(&transaction, 5_000).unwrap();
            settle_due(&transaction, 5_000).unwrap();
            assert_eq!(byte_millis(&transaction, "db", 1_000, 5_000), Ok(6_000));
            let total: String = transaction
                .query_row(
                    "SELECT byte_millis FROM storage_accounting_gauges WHERE tenant = 'db'",
                    [],
                    |row| row.get(0),
                )
                .unwrap();
            assert_eq!(total, "6000");
            transaction.commit().unwrap();
        }
        std::fs::remove_file(path).unwrap();
    }
}
