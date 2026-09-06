use super::*;

fn scope() -> UsageScope {
    scope_for("tenant-a")
}

fn scope_for(tenant: &str) -> UsageScope {
    UsageScope::try_new(
        TenantId::try_new(tenant).expect("tenant"),
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
    )
    .expect("scope")
}

fn definition(kind: MeterKind) -> MeterDefinition {
    let (unit, charge_scope, minting, cadence, correction) = expected_meter(kind);
    MeterDefinition::try_new(kind, 1, unit, charge_scope, minting, cadence, correction)
        .expect("definition")
}

fn segment(id: &str, first: u64, last: u64, total: i128, correction: Option<&str>) -> UsageSegment {
    UsageSegment::try_new(
        SegmentId::try_new(id).expect("segment id"),
        definition(MeterKind::StorageByteSeconds),
        scope(),
        1,
        SequenceInterval::try_new(first, last).expect("interval"),
        MeasurementWindow::interval(42, 43).expect("window"),
        total,
        correction.map(|value| SegmentId::try_new(value).expect("correction id")),
    )
    .expect("segment")
}

#[test]
fn initial_catalog_contains_exactly_the_goal_meters() {
    let catalog = MeterCatalog::initial();
    assert_eq!(catalog.definitions().count(), 2);
    for kind in MeterKind::all() {
        let definition = catalog.get(kind, 1).expect("version one");
        assert_eq!(definition.kind(), kind);
        assert_eq!(definition.correction(), CorrectionProcedure::AppendOnlySegmentReference);
    }
}

#[test]
fn catalog_rejects_wrong_typed_meter_contracts_and_duplicate_versions() {
    let mut catalog = MeterCatalog::initial();
    let invalid = MeterDefinition::try_new(
        MeterKind::DocumentWriteUnits,
        2,
        MeterUnit::RequestUnit,
        ChargeScope::DocumentWrite,
        MintingRule::DocumentWrite,
        MeasurementCadence::PerEvent,
        CorrectionProcedure::AppendOnlySegmentReference,
    );
    assert_eq!(invalid, Err(UsageError::InvalidMeterDefinition));
    let valid = MeterDefinition::try_new(
        MeterKind::DocumentWriteUnits,
        2,
        MeterUnit::WriteUnit,
        ChargeScope::DocumentWrite,
        MintingRule::DocumentWrite,
        MeasurementCadence::PerEvent,
        CorrectionProcedure::AppendOnlySegmentReference,
    )
    .expect("new version");
    catalog.register(valid).expect("first version two");
    assert_eq!(catalog.register(valid), Err(UsageError::DuplicateMeterDefinition));
    assert_eq!(catalog.definitions().count(), 3);
}

#[test]
fn facts_are_integer_scoped_and_match_their_minting_rule() {
    let fact = UsageFact::try_new(
        definition(MeterKind::DocumentWriteUnits),
        scope(),
        MintingIdentity::DocumentWrite(UsageFactId::try_new("request-1").expect("id")),
        MeasurementWindow::instant(10),
        3,
    )
    .expect("fact");
    assert_eq!(fact.delta(), 3);
    assert_eq!(fact.scope().region_id().as_str(), "GRU");
    assert_eq!(
        UsageFact::try_new(
            definition(MeterKind::DocumentWriteUnits),
            scope(),
            MintingIdentity::DocumentWrite(UsageFactId::try_new("request-2").expect("id")),
            MeasurementWindow::instant(10),
            0,
        ),
        Err(UsageError::ZeroDelta)
    );
    assert_eq!(
        UsageFact::try_new(
            definition(MeterKind::DocumentWriteUnits),
            scope(),
            MintingIdentity::DocumentWrite(UsageFactId::try_new("request-3").expect("id")),
            MeasurementWindow::interval(10, 11).expect("interval"),
            1,
        ),
        Err(UsageError::InvalidMeasurementWindow)
    );
}

#[test]
fn durable_fact_encoding_binds_sequence_epoch_and_scope() {
    let fact = UsageFact::try_new(
        definition(MeterKind::StorageByteSeconds),
        scope(),
        MintingIdentity::LogicalStorage(UsageFactId::try_new("commit-1").expect("id")),
        MeasurementWindow::interval(7, 8).expect("interval"),
        9,
    )
    .expect("fact");
    let durable = DurableUsageFact::try_new(fact, 7, 4).expect("durable fact");
    let encoded = durable.encoded();
    assert_eq!(DurableUsageFact::decode(&encoded).expect("decode"), durable);

    let mut damaged = encoded;
    damaged[8] ^= 1;
    assert_eq!(DurableUsageFact::decode(&damaged), Err(UsageError::ChecksumMismatch));
    assert_eq!(
        DurableUsageFact::try_new(durable.fact().clone(), 0, 4),
        Err(UsageError::InvalidDurabilityBinding)
    );
    assert_eq!(
        DurableUsageFact::try_new(durable.fact().clone(), 7, 0),
        Err(UsageError::InvalidDurabilityBinding)
    );
}

#[test]
fn durable_fact_ledger_is_idempotent_and_fences_reuse() {
    let fact = UsageFact::try_new(
        definition(MeterKind::StorageByteSeconds),
        scope(),
        MintingIdentity::LogicalStorage(UsageFactId::try_new("commit-1").expect("id")),
        MeasurementWindow::interval(7, 8).expect("interval"),
        9,
    )
    .expect("fact");
    let durable = DurableUsageFact::try_new(fact.clone(), 7, 4).expect("durable fact");
    let mut ledger = UsageFactLedger::default();
    assert_eq!(ledger.insert(durable.clone()), Ok(true));
    assert_eq!(ledger.insert(durable), Ok(false));
    let different = DurableUsageFact::try_new(
        UsageFact::try_new(
            definition(MeterKind::StorageByteSeconds),
            scope(),
            MintingIdentity::LogicalStorage(UsageFactId::try_new("commit-1").expect("id")),
            MeasurementWindow::interval(7, 8).expect("interval"),
            10,
        )
        .expect("fact"),
        7,
        4,
    )
    .expect("durable fact");
    assert_eq!(ledger.insert(different), Err(UsageError::FactIdentityConflict));
}

#[test]
fn durable_fact_identities_are_scoped_and_do_not_collide_across_tenants() {
    let identity = MintingIdentity::DocumentWrite(UsageFactId::try_new("request-1").expect("id"));
    let fact_for = |tenant: &str| {
        UsageFact::try_new(
            definition(MeterKind::DocumentWriteUnits),
            scope_for(tenant),
            identity.clone(),
            MeasurementWindow::instant(10),
            1,
        )
        .expect("fact")
    };
    let first = DurableUsageFact::try_new(fact_for("tenant-a"), 1, 1).expect("first");
    let second = DurableUsageFact::try_new(fact_for("tenant-b"), 2, 1).expect("second");
    let mut ledger = UsageFactLedger::default();
    assert_eq!(ledger.insert(first), Ok(true));
    assert_eq!(ledger.insert(second), Ok(true));
    assert_eq!(ledger.facts().count(), 2);
    assert!(ledger.get_by_identity(&scope_for("tenant-a"), &identity).is_some());
    assert!(ledger.get_by_identity(&scope_for("tenant-b"), &identity).is_some());
}

#[test]
fn standalone_usage_record_is_closed_bounded_and_tenant_bound() {
    let meter = UsageRecordMeter::DocumentWriteUnits;
    let expected_kind = MeterKind::DocumentWriteUnits;
    let expected_rule = MintingRule::DocumentWrite;
    let request = UsageRecordRequest::try_new(
        meter,
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
        UsageFactId::try_new("fact-1").expect("id"),
        1_788_105_600_000,
        1,
        11,
    )
    .expect("request");
    let fact = request.fact_for(TenantId::try_new("tenant-b").expect("tenant")).expect("fact");
    assert_eq!(fact.meter(), expected_kind);
    assert_eq!(fact.identity().rule(), expected_rule);
    assert_eq!(fact.scope().tenant_id().as_str(), "tenant-b");
    assert_eq!(fact.delta(), 11);
}

#[test]
fn usage_page_shapes_enforce_their_public_bounds() {
    assert_eq!(UsagePageRequest::try_new(None, 0, None), Err(UsageError::InvalidPageSize));
    assert_eq!(
        UsagePageRequest::try_new(None, MAX_USAGE_PAGE_SIZE + 1, None),
        Err(UsageError::InvalidPageSize)
    );
    assert_eq!(
        UsagePageRequest::try_new(Some(MeasurementWindow::instant(10)), 1, None),
        Err(UsageError::InvalidMeasurementWindow)
    );
    assert_eq!(UsageContinuation::try_new(Vec::new()), Err(UsageError::InvalidContinuation));
    assert_eq!(
        UsageContinuation::try_new(vec![0; MAX_USAGE_CONTINUATION_BYTES + 1]),
        Err(UsageError::InvalidContinuation)
    );
    let continuation = UsageContinuation::try_new(vec![1, 2, 3]).expect("continuation");
    let request = UsagePageRequest::try_new(
        Some(MeasurementWindow::interval(10, 20).expect("window")),
        MAX_USAGE_PAGE_SIZE,
        Some(continuation.clone()),
    )
    .expect("page request");
    assert_eq!(request.continuation(), Some(&continuation));
    assert_eq!(request.page_size(), MAX_USAGE_PAGE_SIZE);

    let receipt = UsageRecordReceipt::new(
        UsageFactId::try_new("fact-1").expect("id"),
        StorageSequence::new(9),
        3,
        true,
    );
    assert_eq!(receipt.sequence().get(), 9);
    assert_eq!(receipt.replica_epoch(), 3);
    assert!(receipt.already_recorded());
}

#[test]
fn fact_aggregation_rejects_duplicate_identities_and_integer_overflow() {
    let first = UsageFact::try_new(
        definition(MeterKind::StorageByteSeconds),
        scope(),
        MintingIdentity::LogicalStorage(UsageFactId::try_new("request-max").expect("id")),
        MeasurementWindow::interval(10, 11).expect("window"),
        i128::MAX,
    )
    .expect("fact");
    let duplicate = first.clone();
    assert_eq!(
        UsageSegment::from_facts(
            SegmentId::try_new("duplicate-facts").expect("id"),
            definition(MeterKind::StorageByteSeconds),
            &[first.clone(), duplicate],
            1,
            SequenceInterval::try_new(1, 2).expect("interval"),
            None,
        ),
        Err(UsageError::DuplicateFactIdentity)
    );

    let increment = UsageFact::try_new(
        definition(MeterKind::StorageByteSeconds),
        scope(),
        MintingIdentity::LogicalStorage(UsageFactId::try_new("request-one").expect("id")),
        MeasurementWindow::interval(10, 11).expect("window"),
        1,
    )
    .expect("fact");
    assert_eq!(
        UsageSegment::from_facts(
            SegmentId::try_new("overflow-facts").expect("id"),
            definition(MeterKind::StorageByteSeconds),
            &[first, increment],
            1,
            SequenceInterval::try_new(1, 2).expect("interval"),
            None,
        ),
        Err(UsageError::IntegerOverflow)
    );
}

#[test]
fn segment_encoding_is_canonical_round_trippable_and_checksum_bound() {
    let original = segment("segment-1", 1, 3, 3, None);
    let encoded = original.encoded();
    let decoded = UsageSegment::decode(&encoded).expect("decode");
    assert_eq!(decoded, original);
    assert_eq!(decoded.encoded(), encoded);

    let mut damaged = encoded;
    let checksum_byte = damaged.len() - 9;
    damaged[checksum_byte] ^= 1;
    assert_eq!(UsageSegment::decode(&damaged), Err(UsageError::ChecksumMismatch));
}

#[test]
fn ledger_is_idempotent_and_fences_identity_conflicts_and_overlaps() {
    let first = segment("segment-1", 1, 3, 3, None);
    let mut ledger = UsageSegmentLedger::default();
    assert_eq!(ledger.insert(first.clone()), Ok(SegmentInsertOutcome::Inserted));
    assert_eq!(ledger.insert(first.clone()), Ok(SegmentInsertOutcome::Duplicate));

    let conflicting = segment("segment-1", 1, 3, 4, None);
    assert_eq!(ledger.insert(conflicting), Err(UsageError::SegmentIdentityConflict));
    let overlap = segment("segment-2", 3, 4, 2, None);
    assert_eq!(ledger.insert(overlap), Err(UsageError::SequenceOverlap));
}

#[test]
fn corrections_are_append_only_and_reference_an_existing_matching_base() {
    let base = segment("base", 1, 3, 3, None);
    let correction = segment("correction", 4, 4, -1, Some("base"));
    let mut ledger = UsageSegmentLedger::default();
    ledger.insert(base).expect("base");
    assert_eq!(ledger.insert(correction), Ok(SegmentInsertOutcome::Inserted));

    let missing = segment("missing-correction", 5, 5, -1, Some("unknown"));
    assert_eq!(ledger.insert(missing), Err(UsageError::MissingCorrectionTarget));
}

#[test]
fn d16_write_units_apply_the_region_multiplier_once() {
    assert_eq!(write_units_for_bytes(0), 1);
    assert_eq!(write_units_for_bytes(4_096), 1);
    assert_eq!(write_units_for_bytes(4_097), 2);
    assert_eq!(write_units_for_regions(4_097, 3), Ok(6));
    assert_eq!(billed_write_units(1, 2), Ok(2));
    assert_eq!(write_units_for_regions(1, 0), Err(UsageError::InvalidRegionCount));
}

#[test]
fn group_commit_retries_are_idempotent_and_reconcile_from_journal() {
    let first = UsageFactId::try_new("op-1").expect("id");
    let second = UsageFactId::try_new("op-2").expect("id");
    let mut wave = GroupCommitAccumulator::try_new("db-a", 7, 2).expect("wave");
    assert!(wave.add(first.clone(), 4_096).expect("first"));
    assert!(!wave.add(first.clone(), 4_096).expect("retry"));
    assert_eq!(wave.add(first.clone(), 4_097), Err(UsageError::FactIdentityConflict));
    assert!(wave.add(second.clone(), 4_097).expect("second"));
    let fact = wave.finish().expect("fact");
    assert_eq!(fact.db(), "db-a");
    assert_eq!(fact.seq_hi(), 7);
    assert_eq!(fact.writes(), 6);
    assert_eq!(fact.logical_bytes(), 8_193);

    let row_a = JournalWriteFact::try_new("db-a", 7, first, 4_096, 2).expect("row");
    let row_b = JournalWriteFact::try_new("db-a", 7, second, 4_097, 2).expect("row");
    let reconciled = reconcile_journal(&[row_a.clone(), row_b.clone(), row_a]).expect("reconcile");
    assert_eq!(reconciled, vec![fact]);
}

#[test]
fn storage_byte_seconds_include_current_and_history_retention() {
    let current = [(10_u64, 0_u64, 2_000_u64)];
    let history = [(5_u64, 500_u64, 1_500_u64)];
    assert_eq!(storage_byte_seconds_current_and_history(&current, &history, 2), Ok(50));
    assert_eq!(storage_byte_seconds(10, 2, 0, 2_000), Ok(40));
    assert_eq!(
        storage_byte_seconds_current_and_history(&[(1, 0, 500)], &[], 1),
        Ok(0),
        "sub-second remainder is not billed as a whole byte-second"
    );
    assert_eq!(storage_byte_seconds(1, 0, 0, 1), Err(UsageError::InvalidRegionCount));
}

#[test]
fn storage_rollup_keeps_expired_history_and_applies_regions_once() {
    let current =
        StorageRevision::current(UsageFactId::try_new("current").expect("id"), 10, 1, Some(5_001))
            .expect("current");
    let history = StorageRevision::history(
        UsageFactId::try_new("history").expect("id"),
        5,
        1_001,
        9_001,
        None,
    )
    .expect("history");
    assert_eq!(
        storage_byte_seconds_for_revisions(&[current.clone(), history.clone()], 2, 10_001),
        Ok(280)
    );
    let expired = StorageRevision::current(
        UsageFactId::try_new("expired").expect("id"),
        100,
        5_000,
        Some(5_000),
    )
    .expect("expired");
    assert_eq!(storage_byte_seconds_for_revisions(&[expired], 3, 10_000), Ok(1500));

    let row_a = JournalStorageFact::try_new("db-a", 7, current, 2).expect("row");
    let row_b = JournalStorageFact::try_new("db-a", 8, history, 2).expect("row");
    let result = reconcile_storage_journal(&[row_a.clone(), row_b.clone(), row_a], 10_001)
        .expect("reconcile");
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].seq_hi(), 8);
    assert_eq!(result[0].selected_regions(), 2);
    assert_eq!(result[0].byte_seconds(), 280);
}

#[test]
fn export_cursor_is_checksummed_and_monotonic_across_retries() {
    let mut cursor = UsageExportCursor::initial();
    let first = UsageExportCursor::try_new(3, "db-a").expect("cursor");
    assert!(cursor.advance(&first).expect("advance"));
    assert!(!cursor.advance(&first).expect("retry"));
    let encoded = cursor.encoded();
    assert_eq!(UsageExportCursor::decode(&encoded).expect("decode"), first);
    let old = UsageExportCursor::try_new(2, "db-z").expect("cursor");
    assert_eq!(cursor.advance(&old), Err(UsageError::CursorRegression));
    let mut damaged = encoded;
    damaged[8] ^= 1;
    assert_eq!(UsageExportCursor::decode(&damaged), Err(UsageError::ChecksumMismatch));
}

#[test]
fn reads_and_queries_are_explicitly_unbilled() {
    assert!(UsageActivity::Write.is_billed());
    assert!(!UsageActivity::Write.is_unbilled());
    assert!(!UsageActivity::Read.is_billed());
    assert!(UsageActivity::Read.is_unbilled());
    assert!(!UsageActivity::Query.is_billed());
    assert!(UsageActivity::Query.is_unbilled());
}

#[test]
fn all_serving_and_maintenance_activity_is_unbilled() {
    for activity in [
        UsageActivity::Read,
        UsageActivity::Query,
        UsageActivity::Fanout,
        UsageActivity::Subscriber,
        UsageActivity::Webhook,
        UsageActivity::Replay,
        UsageActivity::CatchUp,
        UsageActivity::TtlExpiry,
        UsageActivity::Egress,
        UsageActivity::Infrastructure,
    ] {
        assert!(activity.is_unbilled());
        assert_eq!(activity.billable_meter(), None);
    }
}

#[test]
// D-16 guard: the deprecated unbilled request meter must stay non-billable.
// Wire-compat use only; new code mints `DocumentWriteUnits`.
#[allow(deprecated)]
fn legacy_request_and_egress_meters_cannot_mint_charges() {
    let request = UsageRecordRequest::try_new(
        UsageRecordMeter::AdmittedRequestUnits,
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
        UsageFactId::try_new("free-request").expect("id"),
        42,
        1,
        1,
    );
    assert_eq!(request, Err(UsageError::NonBillableMeter));
    assert_eq!(MeterKind::from_key("request_units"), Err(UsageError::UnknownMeterDefinition));
    assert_eq!(
        MeterCatalog::initial().get_key("egress_bytes", 1),
        Err(UsageError::UnknownMeterDefinition)
    );
}
