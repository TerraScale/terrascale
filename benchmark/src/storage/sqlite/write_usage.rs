//! Origin write metering derived from the committed write set, in its transaction.

use super::*;
use crate::usage::{UsagePlacement, UsageScope};

pub(super) fn binding_for(
    connection: &rusqlite::Connection,
    tenant: &TenantId,
) -> Result<Option<UsagePlacement>, ()> {
    use rusqlite::OptionalExtension;
    let row: Option<(String, String, String, String, String, String, String)> = connection
        .query_row(
            "SELECT tenant,project,database,partition,region,selected_regions,revision FROM usage_placement WHERE singleton=1",
            [], |row| Ok((row.get(0)?,row.get(1)?,row.get(2)?,row.get(3)?,row.get(4)?,row.get(5)?,row.get(6)?)),
        ).optional().map_err(|_| ())?;
    let Some((bound_tenant, project, database, partition, region, selected, revision)) = row else {
        return Ok(None);
    };
    if bound_tenant != tenant.as_str() {
        return Err(());
    }
    UsagePlacement::try_new(
        tenant.clone(),
        &project,
        &database,
        &partition,
        crate::regions::RegionCode::from_text(&region).map_err(|_| ())?,
        selected
            .split(',')
            .map(crate::regions::RegionCode::from_text)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?,
        &revision,
    )
    .map(Some)
    .map_err(|_| ())
}

pub(super) fn configure(
    connection: &mut rusqlite::Connection,
    placement: &UsagePlacement,
) -> Result<(), ()> {
    // An accepted binding is immutable. Consult SQLite on every call, but an
    // exact repeat needs no write reservation. First configuration still
    // rechecks under the immediate transaction before claiming coverage.
    if let Some(existing) = binding_for(connection, placement.scope.tenant_id())? {
        return if existing == *placement { Ok(()) } else { Err(()) };
    }
    let transaction = connection
        .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)
        .map_err(|_| ())?;
    if let Some(existing) = binding_for(&transaction, placement.scope.tenant_id())? {
        return if existing == *placement { Ok(()) } else { Err(()) };
    }
    // Binding after customer writes would falsely claim historical coverage.
    let existing: bool = transaction
        .query_row(
            "SELECT EXISTS(SELECT 1 FROM operation_facts WHERE committed=1 AND writes>0)",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if existing {
        return Err(());
    }
    let scope = &placement.scope;
    let selected = placement
        .selected_regions
        .iter()
        .map(crate::regions::RegionCode::as_str)
        .collect::<Vec<_>>()
        .join(",");
    transaction.execute(
        "INSERT INTO usage_placement(singleton,tenant,project,database,partition,region,selected_regions,revision) VALUES(1,?1,?2,?3,?4,?5,?6,?7)",
        rusqlite::params![scope.tenant_id().as_str(),scope.project_id().as_str(),scope.database_id().as_str(),scope.partition_id().as_str(),scope.region_id().as_str(),selected,placement.revision],
    ).map_err(|_| ())?;
    transaction.commit().map_err(|_| ())
}

pub(super) fn mint(
    transaction: &rusqlite::Transaction<'_>,
    intent: &CommitIntent,
    sequence: u64,
    billed_writes: u64,
    provenance: &JournalProvenance,
) -> Result<(), ()> {
    if intent.internal_maintenance || provenance.kind() != JournalKind::Origin {
        return Ok(());
    }
    // This context is accepted only by the exact durable cross-file path.
    // Other participants still publish ordinary origin/storage effects.
    if intent.cross_file_billing.as_ref().is_some_and(|b| b.participant != b.metering_participant) {
        return Ok(());
    }
    let billed_writes =
        intent.cross_file_billing.as_ref().map_or(billed_writes, |b| b.total_write_units);
    if billed_writes == 0 {
        return Ok(());
    }
    let Some(binding) = binding_for(transaction, &intent.tenant)? else {
        return Ok(());
    };
    let stopped: bool = transaction
        .query_row(
            "SELECT EXISTS(SELECT 1 FROM storage_accounting_stops WHERE tenant=?1)",
            [intent.tenant.as_str()],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if stopped {
        return Err(());
    }

    if let Some(admission) = &intent.usage_admission {
        // stage_intent already minted the verified admission fact inside this
        // transaction. The admission bills its envelope-aware upper-bound
        // estimate, so the durable envelope can only be smaller or equal —
        // never larger. An admission that fails to cover the actual local
        // write set still fails closed.
        let expected = i128::from(billed_writes)
            .checked_mul(i128::try_from(binding.selected_regions.len()).map_err(|_| ())?)
            .ok_or(())?;
        return if admission.integer_units() >= expected { Ok(()) } else { Err(()) };
    }
    let at = provenance.hlc() >> 16;
    if at == 0 {
        return Err(());
    }
    let units = i128::from(billed_writes)
        .checked_mul(i128::try_from(binding.selected_regions.len()).map_err(|_| ())?)
        .ok_or(())?;
    let definition = crate::usage::MeterCatalog::initial()
        .get(crate::usage::MeterKind::DocumentWriteUnits, 1)
        .copied()
        .ok_or(())?;
    let fact = crate::usage::UsageFact::try_new(
        definition,
        binding.scope,
        crate::usage::MintingIdentity::AcceptedOriginCommit(
            crate::usage::UsageFactId::try_new(intent.operation.as_str()).map_err(|_| ())?,
        ),
        crate::usage::MeasurementWindow::instant(at),
        units,
    )
    .map_err(|_| ())?;
    insert(transaction, &fact, sequence)
}

pub(super) fn insert(
    connection: &rusqlite::Connection,
    fact: &crate::usage::UsageFact,
    sequence: u64,
) -> Result<(), ()> {
    super::usage_seal::ensure_unsealed(connection, fact)?;
    let replica_epoch = super::current_usage_epoch(connection)?;
    let durable =
        DurableUsageFact::try_new(fact.clone(), sequence, replica_epoch).map_err(|_| ())?;
    let scope: &UsageScope = fact.scope();
    connection.execute(
        "INSERT INTO usage_facts(tenant,project,database,partition,region,rule,fact_id,seq,start_millis,end_millis,body) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
        rusqlite::params![scope.tenant_id().as_str(),scope.project_id().as_str(),scope.database_id().as_str(),scope.partition_id().as_str(),scope.region_id().as_str(),minting_rule_code(fact.identity().rule()),fact.identity().id().as_str(),i64::try_from(sequence).map_err(|_| ())?,i64::try_from(fact.window().start_millis()).map_err(|_| ())?,i64::try_from(fact.window().end_millis()).map_err(|_| ())?,durable.encoded()],
    ).map_err(|_| ())?;
    Ok(())
}

#[cfg(test)]
mod placement_read_tests {
    use super::*;

    fn placement() -> UsagePlacement {
        UsagePlacement::try_new(
            TenantId::try_new("tenant").unwrap(),
            "project",
            "database",
            "partition",
            crate::regions::RegionCode::GRU,
            vec![crate::regions::RegionCode::GRU],
            "revision-one",
        )
        .unwrap()
    }

    #[test]
    fn placement_repeat_reads_committed_binding_without_writer_reservation() {
        let path = std::env::temp_dir()
            .join(format!("terrabase-placement-read-{}", crate::identity::generate()));
        let mut connection = rusqlite::Connection::open(&path).unwrap();
        connection.execute_batch("PRAGMA journal_mode=WAL").unwrap();
        connection.execute_batch(SCHEMA).unwrap();
        connection.busy_timeout(std::time::Duration::ZERO).unwrap();
        let placement = placement();
        configure(&mut connection, &placement).unwrap();
        let mut other = rusqlite::Connection::open(&path).unwrap();
        let tx = other.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate).unwrap();
        assert!(configure(&mut connection, &placement).is_ok());
        let mut changed = placement.clone();
        changed.revision = "revision-two".into();
        assert!(configure(&mut connection, &changed).is_err());
        assert_eq!(binding_for(&connection, placement.scope.tenant_id()).unwrap(), Some(placement));
        drop(tx);
        drop(other);
        drop(connection);
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn placement_first_configuration_refuses_preexisting_writes() {
        let mut connection = rusqlite::Connection::open_in_memory().unwrap();
        connection.execute_batch(SCHEMA).unwrap();
        connection.execute(
            "INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,writes,bytes)
             VALUES('tenant','prior',0,1,1,1,1)",
            [],
        ).unwrap();
        let placement = placement();
        assert!(configure(&mut connection, &placement).is_err());
        assert!(binding_for(&connection, placement.scope.tenant_id()).unwrap().is_none());
    }
}
