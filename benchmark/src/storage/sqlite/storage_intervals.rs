//! Exact frozen local storage levels and bounded retirement of source evidence.
use crate::usage::StorageUsageInterval;
use rusqlite::{Connection, params};

pub(super) fn initialize(connection: &Connection) -> Result<(), ()> {
    connection.execute_batch("CREATE TABLE IF NOT EXISTS storage_usage_intervals (
      tenant TEXT NOT NULL, window_start INTEGER NOT NULL, window_end INTEGER NOT NULL,
      ordinal INTEGER NOT NULL, start_millis INTEGER NOT NULL, end_millis INTEGER NOT NULL,
      logical_bytes TEXT NOT NULL, PRIMARY KEY(tenant,window_start,window_end,ordinal)
    ) WITHOUT ROWID;
    CREATE INDEX IF NOT EXISTS storage_accounting_retired ON storage_accounting_revisions(tenant,end_millis)
      WHERE end_millis IS NOT NULL;").map_err(|_| ())
}

pub(super) fn observe(
    connection: &Connection,
    tenant: &str,
    start: i64,
    end: i64,
) -> Result<Vec<StorageUsageInterval>, ()> {
    let mut statement = connection
        .prepare(
            "SELECT at,SUM(delta) FROM (
      SELECT MAX(start_millis,?2) AS at,bytes AS delta FROM storage_accounting_revisions
       WHERE tenant=?1 AND start_millis<?3 AND (end_millis IS NULL OR end_millis>?2)
      UNION ALL
      SELECT MIN(COALESCE(end_millis,?3),?3) AS at,-bytes AS delta FROM storage_accounting_revisions
       WHERE tenant=?1 AND start_millis<?3 AND (end_millis IS NULL OR end_millis>?2)
      ) GROUP BY at ORDER BY at",
        )
        .map_err(|_| ())?;
    let rows = statement
        .query_map(params![tenant, start, end], |row| {
            Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?))
        })
        .map_err(|_| ())?;
    let mut intervals = Vec::new();
    let mut cursor = start;
    let mut bytes = 0_i128;
    for row in rows {
        let (at, delta) = row.map_err(|_| ())?;
        if at > cursor {
            append(&mut intervals, cursor, at, bytes)?;
        }
        bytes = bytes.checked_add(i128::from(delta)).ok_or(())?;
        if bytes < 0 {
            return Err(());
        }
        cursor = at;
    }
    if cursor < end {
        append(&mut intervals, cursor, end, bytes)?;
    }
    Ok(intervals)
}
fn append(
    intervals: &mut Vec<StorageUsageInterval>,
    start: i64,
    end: i64,
    bytes: i128,
) -> Result<(), ()> {
    let logical_bytes = u128::try_from(bytes).map_err(|_| ())?;
    if let Some(last) = intervals.last_mut() {
        if last.logical_bytes == logical_bytes && last.end_millis == start as u64 {
            last.end_millis = u64::try_from(end).map_err(|_| ())?;
            return Ok(());
        }
    }
    if intervals.len() >= 65_536 {
        return Err(());
    }
    intervals.push(StorageUsageInterval {
        start_millis: u64::try_from(start).map_err(|_| ())?,
        end_millis: u64::try_from(end).map_err(|_| ())?,
        logical_bytes,
    });
    Ok(())
}
pub(super) fn load(
    connection: &Connection,
    tenant: &str,
    start: i64,
    end: i64,
) -> Result<Vec<StorageUsageInterval>, ()> {
    let mut statement=connection.prepare("SELECT start_millis,end_millis,logical_bytes FROM storage_usage_intervals WHERE tenant=?1 AND window_start=?2 AND window_end=?3 ORDER BY ordinal").map_err(|_| ())?;
    let rows = statement
        .query_map(params![tenant, start, end], |row| {
            Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?, row.get::<_, String>(2)?))
        })
        .map_err(|_| ())?;
    let mut result = Vec::new();
    for row in rows {
        let (from, to, bytes) = row.map_err(|_| ())?;
        if result.len() >= 65_536 {
            return Err(());
        }
        result.push(StorageUsageInterval {
            start_millis: u64::try_from(from).map_err(|_| ())?,
            end_millis: u64::try_from(to).map_err(|_| ())?,
            logical_bytes: bytes.parse().map_err(|_| ())?,
        });
    }
    if result.first().map(|v| v.start_millis) != Some(start as u64)
        || result.last().map(|v| v.end_millis) != Some(end as u64)
    {
        return Err(());
    }
    Ok(result)
}
pub(super) fn persist(
    connection: &Connection,
    tenant: &str,
    start: i64,
    end: i64,
    intervals: &[StorageUsageInterval],
    seal_hash: &[u8; 32],
) -> Result<[u8; 32], ()> {
    let mut hash = blake3::Hasher::new();
    hash.update(b"terrascale.storage.timeline.closure.v1\0");
    hash.update(seal_hash);
    for (ordinal, interval) in intervals.iter().enumerate() {
        hash.update(&interval.start_millis.to_le_bytes());
        hash.update(&interval.end_millis.to_le_bytes());
        hash.update(&interval.logical_bytes.to_le_bytes());
        connection
            .execute(
                "INSERT INTO storage_usage_intervals VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![
                    tenant,
                    start,
                    end,
                    ordinal as i64,
                    interval.start_millis as i64,
                    interval.end_millis as i64,
                    interval.logical_bytes.to_string()
                ],
            )
            .map_err(|_| ())?;
    }
    // Each closure retires bounded source rows. Open revisions remain available;
    // exact historical replay reads the immutable intervals and seal instead.
    connection.execute("DELETE FROM storage_accounting_revisions WHERE (tenant,collection,id,sequence) IN
      (SELECT tenant,collection,id,sequence FROM storage_accounting_revisions WHERE tenant=?1 AND end_millis<=?2 ORDER BY end_millis LIMIT 4096)",params![tenant,end]).map_err(|_| ())?;
    Ok(*hash.finalize().as_bytes())
}

/// Maintenance drains closed historical source evidence in bounded batches.
pub(super) fn prune_retired(connection: &Connection) -> Result<(), ()> {
    connection
        .execute(
            "DELETE FROM storage_accounting_revisions WHERE (tenant,collection,id,sequence) IN
      (SELECT r.tenant,r.collection,r.id,r.sequence FROM storage_accounting_revisions r
       WHERE r.end_millis IS NOT NULL AND r.end_millis <=
        (SELECT MAX(c.end_millis) FROM storage_usage_closures c WHERE c.tenant=r.tenant)
       LIMIT 4096)",
            [],
        )
        .map_err(|_| ())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn complete_levels_preserve_midcycle_overage_and_cleanup_replay() {
        let mut connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(super::super::SCHEMA).unwrap();
        super::super::accounting::initialize_at(&connection, 1000).unwrap();
        super::super::storage_closure::initialize(&connection).unwrap();
        initialize(&connection).unwrap();
        connection.execute("INSERT INTO storage_accounting_revisions VALUES ('db','items','a',1,200,2000,3000)",[]).unwrap();
        connection
            .execute(
                "INSERT INTO storage_accounting_revisions VALUES ('db','items','b',2,10,3000,NULL)",
                [],
            )
            .unwrap();
        let transaction = connection.transaction().unwrap();
        let levels = observe(&transaction, "db", 1000, 3000).unwrap();
        assert_eq!(
            levels
                .iter()
                .map(|v| (v.start_millis(), v.end_millis(), v.logical_bytes()))
                .collect::<Vec<_>>(),
            vec![(1000, 2000, 0), (2000, 3000, 200)]
        );
        let overage: u128 = levels
            .iter()
            .map(|v| {
                v.logical_bytes().saturating_sub(100)
                    * u128::from(v.end_millis() - v.start_millis())
            })
            .sum();
        assert_eq!(overage, 100_000);
        persist(&transaction, "db", 1000, 3000, &levels, &[7; 32]).unwrap();
        assert_eq!(load(&transaction, "db", 1000, 3000).unwrap(), levels);
        assert_eq!(
            transaction
                .query_row::<i64, _, _>(
                    "SELECT COUNT(*) FROM storage_accounting_revisions",
                    [],
                    |row| row.get(0)
                )
                .unwrap(),
            1
        );
        transaction.commit().unwrap();
        assert_eq!(load(&connection, "db", 1000, 3000).unwrap(), levels);
    }
}
