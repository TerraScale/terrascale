//! Atomic storage integral, meter fact and frozen stream closure.
use super::*;
use crate::usage::{StorageUsageClosure, UsageFactId, UsageScope};

pub(super) fn initialize(connection: &Connection) -> Result<(), ()> {
    connection
        .execute_batch(
            "CREATE TABLE IF NOT EXISTS storage_usage_closures (
          tenant TEXT NOT NULL, start_millis INTEGER NOT NULL, end_millis INTEGER NOT NULL,
          seq INTEGER NOT NULL, regions INTEGER NOT NULL, byte_seconds TEXT NOT NULL,
          remainder INTEGER NOT NULL, write_units TEXT NOT NULL, fact_count INTEGER NOT NULL,
          checksum BLOB NOT NULL, PRIMARY KEY(tenant, start_millis, end_millis)
        ) WITHOUT ROWID;",
        )
        .map_err(|_| ())
}

pub(super) fn close(
    connection: &Connection,
    scope: UsageScope,
    selected_regions: u32,
    window: MeasurementWindow,
) -> Result<StorageUsageClosure, ()> {
    let start = window.start_millis();
    let end = window.end_millis();
    if start >= end || end > u64::try_from(unix_millis()).map_err(|_| ())? || selected_regions == 0
    {
        return Err(());
    }
    let start_sql = i64::try_from(start).map_err(|_| ())?;
    let end_sql = i64::try_from(end).map_err(|_| ())?;
    let tenant = scope.tenant_id().as_str();
    let prior = connection.query_row(
        "SELECT seq,regions,byte_seconds,write_units,fact_count,checksum FROM storage_usage_closures
         WHERE tenant=?1 AND start_millis=?2 AND end_millis=?3",
        rusqlite::params![tenant, start_sql, end_sql], |row| {
            Ok((row.get::<_,i64>(0)?,row.get::<_,u32>(1)?,row.get::<_,String>(2)?,
                row.get::<_,String>(3)?,row.get::<_,i64>(4)?,row.get::<_,Vec<u8>>(5)?))
        }).optional().map_err(|_| ())?;
    if let Some((sequence, regions, bytes, writes, fact_count, checksum)) = prior {
        if regions != selected_regions {
            return Err(());
        }
        return Ok(StorageUsageClosure {
            scope,
            window,
            sequence: u64::try_from(sequence).map_err(|_| ())?,
            selected_regions,
            byte_seconds: bytes.parse().map_err(|_| ())?,
            write_units: writes.parse().map_err(|_| ())?,
            fact_count: u64::try_from(fact_count).map_err(|_| ())?,
            checksum: checksum.try_into().map_err(|_| ())?,
        });
    }
    let previous = connection
        .query_row(
            "SELECT end_millis,remainder FROM storage_usage_closures WHERE tenant=?1
         ORDER BY end_millis DESC LIMIT 1",
            [tenant],
            |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?)),
        )
        .optional()
        .map_err(|_| ())?;
    if previous.is_some_and(|(previous_end, _)| previous_end != start_sql) {
        return Err(());
    }
    let now =
        super::accounting::begin_change(connection, u64::try_from(unix_millis()).map_err(|_| ())?)?;
    super::accounting::settle(connection, tenant, now)?;
    let observed = super::accounting::byte_millis(connection, tenant, start, end)?;
    let byte_millis = observed
        .checked_add(
            u128::try_from(previous.map_or(0, |(_, remainder)| remainder)).map_err(|_| ())?,
        )
        .ok_or(())?;
    let byte_seconds = byte_millis / 1_000;
    if byte_seconds > 0 {
        let definition = *MeterCatalog::initial().get(MeterKind::StorageByteSeconds, 1).ok_or(())?;
        let identity = UsageFactId::try_new(&format!("storage:{start}:{end}")).map_err(|_| ())?;
        let fact = UsageFact::try_new(
            definition,
            scope.clone(),
            MintingIdentity::LogicalStorage(identity),
            window,
            i128::try_from(byte_seconds).map_err(|_| ())?,
        )
        .map_err(|_| ())?;
        let sequence = durable_watermark(connection)?.checked_add(1).ok_or(())?;
        super::write_usage::insert(connection, &fact, sequence)?;
    }
    let seal = super::usage_seal::seal(connection, &scope, window)?;
    if seal.storage_byte_seconds != i128::try_from(byte_seconds).map_err(|_| ())? {
        return Err(());
    }
    connection.execute(
        "INSERT INTO storage_usage_closures
         (tenant,start_millis,end_millis,seq,regions,byte_seconds,remainder,write_units,fact_count,checksum)
         VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
        rusqlite::params![tenant,start_sql,end_sql,i64::try_from(seal.through_sequence).map_err(|_| ())?,selected_regions,byte_seconds.to_string(),
            (byte_millis % 1_000) as i64,seal.write_units.to_string(),i64::try_from(seal.fact_count).map_err(|_| ())?,seal.checksum.as_slice()]
    ).map_err(|_| ())?;
    Ok(StorageUsageClosure {
        scope,
        window,
        sequence: seal.through_sequence,
        selected_regions,
        byte_seconds,
        write_units: seal.write_units,
        fact_count: seal.fact_count,
        checksum: seal.checksum,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::regions::RegionCode;

    #[test]
    fn atomic_closure_carries_fraction_and_replays_without_duplicate_facts() {
        let mut connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(super::super::SCHEMA).unwrap();
        super::super::accounting::initialize_at(&connection, 1000).unwrap();
        initialize(&connection).unwrap();
        let scope = UsageScope::try_new(
            TenantId::try_new("db").unwrap(),
            "project",
            "database",
            "partition",
            RegionCode::GRU,
        )
        .unwrap();
        connection
            .execute(
                "INSERT INTO storage_accounting_revisions VALUES ('db','items','a',1,3,1000,NULL)",
                [],
            )
            .unwrap();
        let window = MeasurementWindow::interval(1000, 1500).unwrap();
        let receipt = {
            let transaction = connection.transaction().unwrap();
            let receipt = close(&transaction, scope.clone(), 3, window).unwrap();
            transaction.commit().unwrap();
            receipt
        };
        assert_eq!(receipt.byte_seconds(), 1);
        let transaction = connection.transaction().unwrap();
        assert_eq!(close(&transaction, scope.clone(), 3, window).unwrap(), receipt);
        assert!(
            close(&transaction, scope.clone(), 3, MeasurementWindow::interval(1400, 1600).unwrap())
                .is_err()
        );
        let next = close(&transaction, scope, 3, MeasurementWindow::interval(1500, 2000).unwrap())
            .unwrap();
        assert_eq!(next.byte_seconds(), 2);
        transaction.commit().unwrap();
        assert_eq!(
            connection
                .query_row::<i64, _, _>("SELECT COUNT(*) FROM usage_facts", [], |row| row.get(0))
                .unwrap(),
            2
        );
    }

    #[test]
    fn zero_usage_region_still_freezes_a_receipt() {
        let mut connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(super::super::SCHEMA).unwrap();
        super::super::accounting::initialize_at(&connection, 1000).unwrap();
        initialize(&connection).unwrap();
        let scope = UsageScope::try_new(
            TenantId::try_new("db").unwrap(),
            "project",
            "database",
            "partition",
            RegionCode::SFO,
        )
        .unwrap();
        let transaction = connection.transaction().unwrap();
        let result =
            close(&transaction, scope, 3, MeasurementWindow::interval(1000, 2000).unwrap())
                .unwrap();
        assert_eq!(result.byte_seconds(), 0);
        assert_eq!(result.fact_count(), 0);
        transaction.commit().unwrap();
        assert_eq!(
            connection
                .query_row::<i64, _, _>("SELECT COUNT(*) FROM storage_usage_closures", [], |row| {
                    row.get(0)
                })
                .unwrap(),
            1
        );
    }
    #[test]
    fn regional_arrival_and_retention_are_measured_independently() {
        let mut totals = Vec::new();
        for (region, arrived, retained_until) in
            [(RegionCode::GRU, 1000, 3000), (RegionCode::SFO, 1500, 2500)]
        {
            let mut connection = Connection::open_in_memory().unwrap();
            connection.execute_batch(super::super::SCHEMA).unwrap();
            super::super::accounting::initialize_at(&connection, 1000).unwrap();
            initialize(&connection).unwrap();
            connection
                .execute(
                    "INSERT INTO storage_accounting_revisions VALUES ('db','items','a',1,10,?1,?2)",
                    rusqlite::params![arrived, retained_until],
                )
                .unwrap();
            let scope = UsageScope::try_new(
                TenantId::try_new("db").unwrap(),
                "project",
                "database",
                "partition",
                region,
            )
            .unwrap();
            let transaction = connection.transaction().unwrap();
            let receipt =
                close(&transaction, scope, 2, MeasurementWindow::interval(1000, 3000).unwrap())
                    .unwrap();
            totals.push(receipt.byte_seconds());
            transaction.commit().unwrap();
        }
        assert_eq!(totals, vec![20, 10]);
        assert_eq!(totals.iter().sum::<u128>(), 30);
    }
}
