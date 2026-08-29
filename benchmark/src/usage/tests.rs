use super::*;

fn scope() -> UsageScope {
    UsageScope::try_new(
        TenantId::try_new("tenant-a").expect("tenant"),
        "project-a",
        "database-a",
        "partition-a",
        RegionId::try_new("gru").expect("region"),
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
        definition(MeterKind::AdmittedRequestUnits),
        scope(),
        1,
        SequenceInterval::try_new(first, last).expect("interval"),
        MeasurementWindow::instant(42),
        total,
        correction.map(|value| SegmentId::try_new(value).expect("correction id")),
    )
    .expect("segment")
}

#[test]
fn initial_catalog_contains_exactly_the_goal_meters() {
    let catalog = MeterCatalog::initial();
    assert_eq!(catalog.definitions().count(), 6);
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
        MeterKind::PublicEgressBytes,
        2,
        MeterUnit::RequestUnit,
        ChargeScope::PublicEgress,
        MintingRule::PublicResponseBytes,
        MeasurementCadence::PerEvent,
        CorrectionProcedure::AppendOnlySegmentReference,
    );
    assert_eq!(invalid, Err(UsageError::InvalidMeterDefinition));
    let valid = MeterDefinition::try_new(
        MeterKind::PublicEgressBytes,
        2,
        MeterUnit::Byte,
        ChargeScope::PublicEgress,
        MintingRule::PublicResponseBytes,
        MeasurementCadence::PerEvent,
        CorrectionProcedure::AppendOnlySegmentReference,
    )
    .expect("new version");
    catalog.register(valid).expect("first version two");
    assert_eq!(catalog.register(valid), Err(UsageError::DuplicateMeterDefinition));
    assert_eq!(catalog.definitions().count(), 7);
}

#[test]
fn facts_are_integer_scoped_and_match_their_minting_rule() {
    let fact = UsageFact::try_new(
        definition(MeterKind::AdmittedRequestUnits),
        scope(),
        MintingIdentity::StableAdmittedRequest(UsageFactId::try_new("request-1").expect("id")),
        MeasurementWindow::instant(10),
        3,
    )
    .expect("fact");
    assert_eq!(fact.delta(), 3);
    assert_eq!(fact.scope().region_id().as_str(), "gru");
    assert_eq!(
        UsageFact::try_new(
            definition(MeterKind::AdmittedRequestUnits),
            scope(),
            MintingIdentity::StableAdmittedRequest(UsageFactId::try_new("request-2").expect("id")),
            MeasurementWindow::instant(10),
            0,
        ),
        Err(UsageError::ZeroDelta)
    );
    assert_eq!(
        UsageFact::try_new(
            definition(MeterKind::AdmittedRequestUnits),
            scope(),
            MintingIdentity::StableAdmittedRequest(UsageFactId::try_new("request-3").expect("id")),
            MeasurementWindow::interval(10, 11).expect("interval"),
            1,
        ),
        Err(UsageError::InvalidMeasurementWindow)
    );
}

#[test]
fn durable_fact_encoding_binds_sequence_epoch_and_scope() {
    let fact = UsageFact::try_new(
        definition(MeterKind::StoredByteHours),
        scope(),
        MintingIdentity::AcceptedOriginCommit(UsageFactId::try_new("commit-1").expect("id")),
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
        definition(MeterKind::StoredByteHours),
        scope(),
        MintingIdentity::AcceptedOriginCommit(UsageFactId::try_new("commit-1").expect("id")),
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
            definition(MeterKind::StoredByteHours),
            scope(),
            MintingIdentity::AcceptedOriginCommit(UsageFactId::try_new("commit-1").expect("id")),
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
fn fact_aggregation_rejects_duplicate_identities_and_integer_overflow() {
    let first = UsageFact::try_new(
        definition(MeterKind::AdmittedRequestUnits),
        scope(),
        MintingIdentity::StableAdmittedRequest(UsageFactId::try_new("request-max").expect("id")),
        MeasurementWindow::instant(10),
        i128::MAX,
    )
    .expect("fact");
    let duplicate = first.clone();
    assert_eq!(
        UsageSegment::from_facts(
            SegmentId::try_new("duplicate-facts").expect("id"),
            definition(MeterKind::AdmittedRequestUnits),
            &[first.clone(), duplicate],
            1,
            SequenceInterval::try_new(1, 2).expect("interval"),
            None,
        ),
        Err(UsageError::DuplicateFactIdentity)
    );

    let increment = UsageFact::try_new(
        definition(MeterKind::AdmittedRequestUnits),
        scope(),
        MintingIdentity::StableAdmittedRequest(UsageFactId::try_new("request-one").expect("id")),
        MeasurementWindow::instant(10),
        1,
    )
    .expect("fact");
    assert_eq!(
        UsageSegment::from_facts(
            SegmentId::try_new("overflow-facts").expect("id"),
            definition(MeterKind::AdmittedRequestUnits),
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
