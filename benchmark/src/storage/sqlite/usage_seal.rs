//! Atomic local usage cuts. Cross-Region completeness belongs to the coordinator.

use rusqlite::{Connection, OptionalExtension, params};

use crate::usage::{DurableUsageFact, MeasurementWindow, MeterKind, UsageFact, UsageScope};

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct UsageStreamSeal {
    pub(crate) through_sequence: u64,
    pub(crate) write_units: i128,
    pub(crate) storage_byte_seconds: i128,
    pub(crate) fact_count: u64,
    pub(crate) checksum: [u8; 32],
}

/// Must run in the transaction inserting a new fact, after exact replay lookup.
pub(crate) fn ensure_unsealed(connection: &Connection, fact: &UsageFact) -> Result<(), ()> {
    let scope = fact.scope();
    let end: Option<i64> = connection.query_row(
        "SELECT MAX(end_millis) FROM usage_stream_seals WHERE tenant=?1 AND project=?2 AND database=?3 AND partition=?4 AND region=?5",
        params![scope.tenant_id().as_str(), scope.project_id().as_str(), scope.database_id().as_str(), scope.partition_id().as_str(), scope.region_id().as_str()],
        |row| row.get(0),
    ).map_err(|_| ())?;
    if end.is_some_and(|end| fact.window().start_millis() < end as u64) {
        return Err(());
    }
    Ok(())
}

/// Runs inside the storage integration transaction, after the final storage fact.
/// The caller must establish the trusted local time/completeness barrier first.
pub(crate) fn seal(
    connection: &Connection,
    scope: &UsageScope,
    window: MeasurementWindow,
) -> Result<UsageStreamSeal, ()> {
    let start = i64::try_from(window.start_millis()).map_err(|_| ())?;
    let end = i64::try_from(window.end_millis()).map_err(|_| ())?;
    if end <= start || connection.is_autocommit() {
        return Err(());
    }
    let identity = params![
        scope.tenant_id().as_str(),
        scope.project_id().as_str(),
        scope.database_id().as_str(),
        scope.partition_id().as_str(),
        scope.region_id().as_str(),
        start,
        end
    ];
    if let Some(receipt) = connection.query_row(
        "SELECT through_sequence,write_units,storage_byte_seconds,fact_count,checksum FROM usage_stream_seals WHERE tenant=?1 AND project=?2 AND database=?3 AND partition=?4 AND region=?5 AND start_millis=?6 AND end_millis=?7",
        identity,
        |row| Ok((row.get::<_,i64>(0)?,row.get::<_,String>(1)?,row.get::<_,String>(2)?,row.get::<_,i64>(3)?,row.get::<_,Vec<u8>>(4)?)),
    ).optional().map_err(|_| ())? {
        return Ok(UsageStreamSeal { through_sequence: u64::try_from(receipt.0).map_err(|_| ())?, write_units: receipt.1.parse().map_err(|_| ())?, storage_byte_seconds: receipt.2.parse().map_err(|_| ())?, fact_count: u64::try_from(receipt.3).map_err(|_| ())?, checksum: receipt.4.try_into().map_err(|_| ())? });
    }
    let prior_end: Option<i64> = connection.query_row("SELECT MAX(end_millis) FROM usage_stream_seals WHERE tenant=?1 AND project=?2 AND database=?3 AND partition=?4 AND region=?5",params![scope.tenant_id().as_str(),scope.project_id().as_str(),scope.database_id().as_str(),scope.partition_id().as_str(),scope.region_id().as_str()],|row| row.get(0)).map_err(|_| ())?;
    if prior_end.is_some_and(|prior| prior != start) {
        return Err(());
    }
    let mut receipt = UsageStreamSeal {
        through_sequence: super::durable_watermark(connection)?,
        write_units: 0,
        storage_byte_seconds: 0,
        fact_count: 0,
        checksum: [0; 32],
    };
    let mut hash = blake3::Hasher::new();
    hash.update(b"terrascale.usage.stream.seal.v1\0");
    for field in [
        scope.tenant_id().as_str(),
        scope.project_id().as_str(),
        scope.database_id().as_str(),
        scope.partition_id().as_str(),
        scope.region_id().as_str(),
    ] {
        hash.update(&(field.len() as u64).to_le_bytes());
        hash.update(field.as_bytes());
    }
    hash.update(&start.to_le_bytes());
    hash.update(&end.to_le_bytes());
    hash.update(&receipt.through_sequence.to_le_bytes());
    let mut statement = connection.prepare("SELECT body FROM usage_facts WHERE tenant=?1 AND project=?2 AND database=?3 AND partition=?4 AND region=?5 AND start_millis < ?7 AND (end_millis > ?6 OR (end_millis=start_millis AND start_millis>=?6)) ORDER BY seq,rule,fact_id").map_err(|_| ())?;
    let rows = statement.query_map(identity, |row| row.get::<_, Vec<u8>>(0)).map_err(|_| ())?;
    for row in rows {
        let bytes = row.map_err(|_| ())?;
        let durable = DurableUsageFact::decode(&bytes).map_err(|_| ())?;
        let fact = durable.fact();
        if fact.scope() != scope
            || fact.window().start_millis() < window.start_millis()
            || fact.window().end_millis() > window.end_millis()
            || durable.sequence() > receipt.through_sequence
        {
            return Err(());
        }
        match fact.meter() {
            MeterKind::DocumentWriteUnits => {
                receipt.write_units = receipt.write_units.checked_add(fact.delta()).ok_or(())?
            }
            MeterKind::StorageByteSeconds => {
                receipt.storage_byte_seconds =
                    receipt.storage_byte_seconds.checked_add(fact.delta()).ok_or(())?
            }
            _ => return Err(()),
        }
        receipt.fact_count = receipt.fact_count.checked_add(1).ok_or(())?;
        hash.update(&(bytes.len() as u64).to_le_bytes());
        hash.update(&bytes);
    }
    receipt.checksum = *hash.finalize().as_bytes();
    connection
        .execute(
            "INSERT INTO usage_stream_seals VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)",
            params![
                scope.tenant_id().as_str(),
                scope.project_id().as_str(),
                scope.database_id().as_str(),
                scope.partition_id().as_str(),
                scope.region_id().as_str(),
                start,
                end,
                i64::try_from(receipt.through_sequence).map_err(|_| ())?,
                receipt.write_units.to_string(),
                receipt.storage_byte_seconds.to_string(),
                i64::try_from(receipt.fact_count).map_err(|_| ())?,
                receipt.checksum.as_slice()
            ],
        )
        .map_err(|_| ())?;
    Ok(receipt)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        regions::RegionCode,
        types::TenantId,
        usage::{MeterCatalog, MintingIdentity, UsageFactId},
    };

    #[test]
    fn zero_stream_seals_replay_and_reject_backdated_facts() {
        let mut connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(super::super::SCHEMA).unwrap();
        let scope = UsageScope::try_new(
            TenantId::try_new("tenant").unwrap(),
            "project",
            "database",
            "partition",
            RegionCode::GRU,
        )
        .unwrap();
        let window = MeasurementWindow::interval(1000, 2000).unwrap();
        let transaction = connection.transaction().unwrap();
        let receipt = seal(&transaction, &scope, window).unwrap();
        assert_eq!(receipt.fact_count, 0);
        transaction.commit().unwrap();
        let transaction = connection.transaction().unwrap();
        assert_eq!(seal(&transaction, &scope, window).unwrap(), receipt);
        assert!(
            seal(&transaction, &scope, MeasurementWindow::interval(1500, 2500).unwrap()).is_err()
        );
        assert!(
            seal(&transaction, &scope, MeasurementWindow::interval(2500, 3000).unwrap()).is_err()
        );
        let definition = *MeterCatalog::initial().get(MeterKind::DocumentWriteUnits, 1).unwrap();
        let fact = |at| {
            UsageFact::try_new(
                definition,
                scope.clone(),
                MintingIdentity::DocumentWrite(UsageFactId::try_new("write").unwrap()),
                MeasurementWindow::instant(at),
                1,
            )
            .unwrap()
        };
        assert!(ensure_unsealed(&transaction, &fact(1999)).is_err());
        assert!(ensure_unsealed(&transaction, &fact(2000)).is_ok());
        transaction.commit().unwrap();
    }

    #[test]
    fn seal_requires_atomic_transaction() {
        let connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(super::super::SCHEMA).unwrap();
        let scope = UsageScope::try_new(
            TenantId::try_new("tenant").unwrap(),
            "project",
            "database",
            "partition",
            RegionCode::GRU,
        )
        .unwrap();
        assert!(seal(&connection, &scope, MeasurementWindow::interval(1, 2).unwrap()).is_err());
    }
}
