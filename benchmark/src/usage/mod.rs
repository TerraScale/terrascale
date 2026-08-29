//! Native, durable-agnostic usage accounting domain contracts.
//!
//! This module defines the values that a later storage and export layer may
//! persist.  It deliberately has no WAL, transport, pricing, or Dashboard
//! dependency.  Usage values are integers; rating and invoice rounding are
//! outside this domain.

#![allow(clippy::missing_errors_doc)]

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt,
};

use crate::{replication::RegionId, types::TenantId};

/// The canonical version of the usage segment encoding.
pub const USAGE_FORMAT_VERSION: u16 = 1;

/// The checksum domain separator for the canonical usage encoding.
pub const USAGE_CHECKSUM_DOMAIN: &[u8] = b"terrascale.usage.segment.v1\0";

/// The maximum length accepted for one logical usage identifier.
const MAX_IDENTIFIER_BYTES: usize = 255;

/// The maximum canonical encoded size of one segment.
pub const MAX_SEGMENT_BYTES: usize = 64 * 1024;

/// The maximum sequence interval width of one bounded segment.
pub const MAX_SEQUENCE_SPAN: u64 = 1_000_000;

/// Typed failures in the usage domain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UsageError {
    /// An identifier was empty, too long, or contained forbidden text.
    InvalidIdentifier,
    /// A meter version was zero.
    InvalidMeterVersion,
    /// A meter definition does not match its fixed typed contract.
    InvalidMeterDefinition,
    /// A catalog already contains this meter and version.
    DuplicateMeterDefinition,
    /// A requested meter version is absent from a catalog.
    UnknownMeterDefinition,
    /// A sequence interval was empty, reversed, or too large.
    InvalidSequenceInterval,
    /// A measurement window was reversed or otherwise invalid.
    InvalidMeasurementWindow,
    /// A usage fact had no integer work to account for.
    ZeroDelta,
    /// Aggregating integer usage facts overflowed the domain's integer range.
    IntegerOverflow,
    /// One aggregate contains the same minted fact identity more than once.
    DuplicateFactIdentity,
    /// The canonical segment would exceed the bounded segment limit.
    SegmentTooLarge,
    /// The encoded segment is truncated or has an unknown field value.
    MalformedEncoding,
    /// The encoded checksum does not match its canonical body.
    ChecksumMismatch,
    /// The bytes are valid but not the one canonical encoding.
    NonCanonicalEncoding,
    /// A segment identity already names the exact same segment.
    DuplicateSegment,
    /// A segment identity was reused for different bytes/checksum.
    SegmentIdentityConflict,
    /// A new segment overlaps a prior interval in the same accounting scope.
    SequenceOverlap,
    /// A correction names no segment in the ledger.
    MissingCorrectionTarget,
    /// A correction does not match its target's accounting scope.
    CorrectionScopeMismatch,
    /// Corrections may reference only a base segment, never another correction.
    CorrectionChain,
}

impl fmt::Display for UsageError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::InvalidIdentifier => "invalid usage identifier",
            Self::InvalidMeterVersion => "invalid meter version",
            Self::InvalidMeterDefinition => "invalid meter definition",
            Self::DuplicateMeterDefinition => "duplicate meter definition",
            Self::UnknownMeterDefinition => "unknown meter definition",
            Self::InvalidSequenceInterval => "invalid sequence interval",
            Self::InvalidMeasurementWindow => "invalid measurement window",
            Self::ZeroDelta => "usage delta cannot be zero",
            Self::IntegerOverflow => "usage integer total overflow",
            Self::DuplicateFactIdentity => "usage fact identity is duplicated",
            Self::SegmentTooLarge => "usage segment exceeds its bound",
            Self::MalformedEncoding => "malformed usage segment encoding",
            Self::ChecksumMismatch => "usage segment checksum mismatch",
            Self::NonCanonicalEncoding => "non-canonical usage segment encoding",
            Self::DuplicateSegment => "usage segment is an exact duplicate",
            Self::SegmentIdentityConflict => "usage segment identity has different bytes",
            Self::SequenceOverlap => "usage sequence interval overlaps an existing segment",
            Self::MissingCorrectionTarget => "usage correction target is missing",
            Self::CorrectionScopeMismatch => "usage correction scope differs from its target",
            Self::CorrectionChain => "usage correction cannot target another correction",
        })
    }
}

impl std::error::Error for UsageError {}

/// A validated identifier used for Project, Database, and Partition scope.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct UsageScopeId(String);

impl UsageScopeId {
    /// Creates a validated scope identifier.
    pub fn try_new(value: &str) -> Result<Self, UsageError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical identifier text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for UsageScopeId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A validated stable segment identity.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SegmentId(String);

impl SegmentId {
    /// Creates a validated segment identity.
    pub fn try_new(value: &str) -> Result<Self, UsageError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical identity text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for SegmentId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A validated identity for one minted usage fact.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct UsageFactId(String);

impl UsageFactId {
    /// Creates a validated fact identity.
    pub fn try_new(value: &str) -> Result<Self, UsageError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical identity text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// The complete tenant, Project, Database, Partition, and Region scope.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
#[allow(clippy::struct_field_names)]
pub struct UsageScope {
    tenant_id: TenantId,
    project_id: UsageScopeId,
    database_id: UsageScopeId,
    partition_id: UsageScopeId,
    region_id: RegionId,
}

impl UsageScope {
    /// Creates a fully scoped usage identity.
    pub fn try_new(
        tenant_id: TenantId,
        project_id: &str,
        database_id: &str,
        partition_id: &str,
        region_id: RegionId,
    ) -> Result<Self, UsageError> {
        Ok(Self {
            tenant_id,
            project_id: UsageScopeId::try_new(project_id)?,
            database_id: UsageScopeId::try_new(database_id)?,
            partition_id: UsageScopeId::try_new(partition_id)?,
            region_id,
        })
    }

    /// Returns the tenant identity.
    #[must_use]
    pub fn tenant_id(&self) -> &TenantId {
        &self.tenant_id
    }

    /// Returns the Project identity.
    #[must_use]
    pub fn project_id(&self) -> &UsageScopeId {
        &self.project_id
    }

    /// Returns the Database identity.
    #[must_use]
    pub fn database_id(&self) -> &UsageScopeId {
        &self.database_id
    }

    /// Returns the Partition identity.
    #[must_use]
    pub fn partition_id(&self) -> &UsageScopeId {
        &self.partition_id
    }

    /// Returns the Region identity.
    #[must_use]
    pub fn region_id(&self) -> &RegionId {
        &self.region_id
    }
}

/// One initial billable meter from the GOAL catalog.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MeterKind {
    /// Stored bytes multiplied by elapsed hours for each selected Region copy.
    StoredByteHours,
    /// Admitted public request units under a versioned cost table.
    AdmittedRequestUnits,
    /// Public response and change-stream egress bytes.
    PublicEgressBytes,
    /// Active Region seats multiplied by elapsed hours.
    ActiveRegionSeats,
    /// Optional immutable backup bytes multiplied by elapsed hours.
    BackupByteHours,
    /// Explicit restore work.
    RestoreWork,
}

impl MeterKind {
    /// Returns every initial meter in canonical order.
    #[must_use]
    pub const fn all() -> [Self; 6] {
        [
            Self::StoredByteHours,
            Self::AdmittedRequestUnits,
            Self::PublicEgressBytes,
            Self::ActiveRegionSeats,
            Self::BackupByteHours,
            Self::RestoreWork,
        ]
    }
}

/// The integer unit of one meter.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MeterUnit {
    /// Bytes multiplied by hours.
    ByteHour,
    /// One admitted request cost unit.
    RequestUnit,
    /// One public byte.
    Byte,
    /// One active Region seat multiplied by hours.
    SeatHour,
    /// One explicit restore work unit.
    RestoreWork,
}

/// The accounting scope a meter charges.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum ChargeScope {
    /// One selected active Region's copy.
    ActiveRegionCopy,
    /// One admitted request at its origin.
    AdmittedRequest,
    /// One public response or change-stream delivery.
    PublicEgress,
    /// One active Region seat.
    ActiveRegionSeat,
    /// One optional backup copy.
    BackupCopy,
    /// One explicit restore operation.
    ExplicitRestore,
}

/// The rule that mints a unit and identifies excluded internal work.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MintingRule {
    /// Mint once for an accepted origin commit; retries and replicas do not mint.
    AcceptedOriginCommit,
    /// Mint once for a stable admitted request identity.
    StableAdmittedRequest,
    /// Mint public response/change-stream bytes only.
    PublicResponseBytes,
    /// Mint one interval observation for an active Region seat.
    ActiveRegionHour,
    /// Mint one interval observation for an optional backup copy.
    BackupByteHour,
    /// Mint only explicit restore work.
    ExplicitRestoreWork,
}

/// The measurement cadence of a meter.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MeasurementCadence {
    /// The fact is tied to one admitted/public event.
    PerEvent,
    /// The fact covers a non-overlapping interval.
    Interval,
}

/// The engine's correction procedure. Pricing may not mutate this rule.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CorrectionProcedure {
    /// Append a new segment that names the segment being corrected.
    AppendOnlySegmentReference,
}

/// One versioned, typed meter definition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MeterDefinition {
    kind: MeterKind,
    version: u32,
    unit: MeterUnit,
    charge_scope: ChargeScope,
    minting: MintingRule,
    cadence: MeasurementCadence,
    correction: CorrectionProcedure,
}

impl MeterDefinition {
    /// Creates and validates a versioned meter contract.
    pub fn try_new(
        kind: MeterKind,
        version: u32,
        unit: MeterUnit,
        charge_scope: ChargeScope,
        minting: MintingRule,
        cadence: MeasurementCadence,
        correction: CorrectionProcedure,
    ) -> Result<Self, UsageError> {
        if version == 0 {
            return Err(UsageError::InvalidMeterVersion);
        }
        let expected = expected_meter(kind);
        if (unit, charge_scope, minting, cadence, correction) != expected {
            return Err(UsageError::InvalidMeterDefinition);
        }
        Ok(Self { kind, version, unit, charge_scope, minting, cadence, correction })
    }

    /// Returns the meter kind.
    #[must_use]
    pub const fn kind(self) -> MeterKind {
        self.kind
    }

    /// Returns the published version.
    #[must_use]
    pub const fn version(self) -> u32 {
        self.version
    }

    /// Returns the integer unit.
    #[must_use]
    pub const fn unit(self) -> MeterUnit {
        self.unit
    }

    /// Returns the charge scope.
    #[must_use]
    pub const fn charge_scope(self) -> ChargeScope {
        self.charge_scope
    }

    /// Returns the unit-minting rule.
    #[must_use]
    pub const fn minting(self) -> MintingRule {
        self.minting
    }

    /// Returns the measurement cadence.
    #[must_use]
    pub const fn cadence(self) -> MeasurementCadence {
        self.cadence
    }

    /// Returns the append-only correction rule.
    #[must_use]
    pub const fn correction(self) -> CorrectionProcedure {
        self.correction
    }
}

/// The initial versioned meter catalog.
#[derive(Clone, Debug, Default)]
pub struct MeterCatalog {
    definitions: BTreeMap<(MeterKind, u32), MeterDefinition>,
}

impl MeterCatalog {
    /// Builds version one of every initial GOAL meter.
    #[must_use]
    #[allow(clippy::missing_panics_doc)]
    pub fn initial() -> Self {
        let mut catalog = Self::default();
        for kind in MeterKind::all() {
            let definition = MeterDefinition::try_new(
                kind,
                1,
                expected_meter(kind).0,
                expected_meter(kind).1,
                expected_meter(kind).2,
                expected_meter(kind).3,
                expected_meter(kind).4,
            )
            .expect("initial meter definitions are valid");
            catalog.definitions.insert((kind, 1), definition);
        }
        catalog
    }

    /// Registers a new immutable version.
    pub fn register(&mut self, definition: MeterDefinition) -> Result<(), UsageError> {
        let key = (definition.kind, definition.version);
        if self.definitions.contains_key(&key) {
            return Err(UsageError::DuplicateMeterDefinition);
        }
        self.definitions.insert(key, definition);
        Ok(())
    }

    /// Finds one exact meter version.
    #[must_use]
    pub fn get(&self, kind: MeterKind, version: u32) -> Option<&MeterDefinition> {
        self.definitions.get(&(kind, version))
    }

    /// Returns definitions in deterministic meter/version order.
    pub fn definitions(&self) -> impl Iterator<Item = &MeterDefinition> {
        self.definitions.values()
    }
}

/// A stable identity used by one fact's minting rule.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MintingIdentity {
    /// The accepted origin commit identity.
    AcceptedOriginCommit(UsageFactId),
    /// The stable admitted request identity.
    StableAdmittedRequest(UsageFactId),
    /// The public response or change-stream delivery identity.
    PublicResponse(UsageFactId),
    /// The active Region interval identity.
    ActiveRegionHour(UsageFactId),
    /// The optional backup interval identity.
    BackupByteHour(UsageFactId),
    /// The explicit restore identity.
    ExplicitRestore(UsageFactId),
}

impl MintingIdentity {
    fn rule(&self) -> MintingRule {
        match self {
            Self::AcceptedOriginCommit(_) => MintingRule::AcceptedOriginCommit,
            Self::StableAdmittedRequest(_) => MintingRule::StableAdmittedRequest,
            Self::PublicResponse(_) => MintingRule::PublicResponseBytes,
            Self::ActiveRegionHour(_) => MintingRule::ActiveRegionHour,
            Self::BackupByteHour(_) => MintingRule::BackupByteHour,
            Self::ExplicitRestore(_) => MintingRule::ExplicitRestoreWork,
        }
    }

    /// Returns the stable identity text carried by this minting source.
    #[must_use]
    pub fn id(&self) -> &UsageFactId {
        match self {
            Self::AcceptedOriginCommit(id)
            | Self::StableAdmittedRequest(id)
            | Self::PublicResponse(id)
            | Self::ActiveRegionHour(id)
            | Self::BackupByteHour(id)
            | Self::ExplicitRestore(id) => id,
        }
    }
}

/// One non-floating-point usage delta produced by an admitted operation or interval.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageFact {
    scope: UsageScope,
    meter: MeterKind,
    meter_version: u32,
    identity: MintingIdentity,
    window: MeasurementWindow,
    delta: i128,
}

impl UsageFact {
    /// Creates one fact, checking its meter version, minting identity, cadence,
    /// and integer delta.
    pub fn try_new(
        definition: MeterDefinition,
        scope: UsageScope,
        identity: MintingIdentity,
        window: MeasurementWindow,
        delta: i128,
    ) -> Result<Self, UsageError> {
        if identity.rule() != definition.minting || delta == 0 {
            return if delta == 0 {
                Err(UsageError::ZeroDelta)
            } else {
                Err(UsageError::InvalidMeterDefinition)
            };
        }
        window.validate_for(definition.cadence)?;
        Ok(Self {
            scope,
            meter: definition.kind,
            meter_version: definition.version,
            identity,
            window,
            delta,
        })
    }

    /// Returns the full usage scope.
    #[must_use]
    pub fn scope(&self) -> &UsageScope {
        &self.scope
    }

    /// Returns the meter kind.
    #[must_use]
    pub const fn meter(&self) -> MeterKind {
        self.meter
    }

    /// Returns the meter version.
    #[must_use]
    pub const fn meter_version(&self) -> u32 {
        self.meter_version
    }

    /// Returns the stable minting identity.
    #[must_use]
    pub fn identity(&self) -> &MintingIdentity {
        &self.identity
    }

    /// Returns the measurement window.
    #[must_use]
    pub const fn window(&self) -> MeasurementWindow {
        self.window
    }

    /// Returns the signed integer delta.
    #[must_use]
    pub const fn delta(&self) -> i128 {
        self.delta
    }
}

/// One half-open measurement window `[start, end)` in Unix milliseconds.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct MeasurementWindow {
    start_millis: u64,
    end_millis: u64,
}

impl MeasurementWindow {
    /// Creates a non-empty interval window.
    pub const fn interval(start_millis: u64, end_millis: u64) -> Result<Self, UsageError> {
        if end_millis <= start_millis {
            return Err(UsageError::InvalidMeasurementWindow);
        }
        Ok(Self { start_millis, end_millis })
    }

    /// Creates an instant window for event meters.
    #[must_use]
    pub const fn instant(at_millis: u64) -> Self {
        Self { start_millis: at_millis, end_millis: at_millis }
    }

    /// Returns the inclusive start boundary.
    #[must_use]
    pub const fn start_millis(self) -> u64 {
        self.start_millis
    }

    /// Returns the exclusive end boundary.
    #[must_use]
    pub const fn end_millis(self) -> u64 {
        self.end_millis
    }

    fn validate_for(self, cadence: MeasurementCadence) -> Result<(), UsageError> {
        match cadence {
            MeasurementCadence::PerEvent if self.start_millis == self.end_millis => Ok(()),
            MeasurementCadence::PerEvent => Err(UsageError::InvalidMeasurementWindow),
            MeasurementCadence::Interval => self.validate_interval(),
        }
    }

    fn validate_interval(self) -> Result<(), UsageError> {
        (self.end_millis > self.start_millis)
            .then_some(())
            .ok_or(UsageError::InvalidMeasurementWindow)
    }
}

/// A closed sequence interval `[first, last]` carried by one bounded segment.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SequenceInterval {
    first: u64,
    last: u64,
}

impl SequenceInterval {
    /// Creates one non-empty, bounded interval.
    pub const fn try_new(first: u64, last: u64) -> Result<Self, UsageError> {
        if first == 0 || last < first || last - first >= MAX_SEQUENCE_SPAN {
            return Err(UsageError::InvalidSequenceInterval);
        }
        Ok(Self { first, last })
    }

    /// Returns the first sequence.
    #[must_use]
    pub const fn first(self) -> u64 {
        self.first
    }

    /// Returns the last sequence.
    #[must_use]
    pub const fn last(self) -> u64 {
        self.last
    }

    fn overlaps(self, other: Self) -> bool {
        self.first <= other.last && other.first <= self.last
    }
}

/// One immutable checksummed usage segment.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageSegment {
    segment_id: SegmentId,
    meter: MeterKind,
    meter_version: u32,
    meter_scope: ChargeScope,
    scope: UsageScope,
    replica_epoch: u64,
    interval: SequenceInterval,
    measurement_window: MeasurementWindow,
    integer_total: i128,
    correction_of: Option<SegmentId>,
    checksum: [u8; 32],
}

impl UsageSegment {
    /// Creates a bounded immutable segment and computes its canonical checksum.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        segment_id: SegmentId,
        definition: MeterDefinition,
        scope: UsageScope,
        replica_epoch: u64,
        interval: SequenceInterval,
        measurement_window: MeasurementWindow,
        integer_total: i128,
        correction_of: Option<SegmentId>,
    ) -> Result<Self, UsageError> {
        measurement_window.validate_for(definition.cadence)?;
        if integer_total == 0 {
            return Err(UsageError::ZeroDelta);
        }
        if correction_of.is_none() && integer_total < 0 {
            return Err(UsageError::InvalidMeterDefinition);
        }
        let mut segment = Self {
            segment_id,
            meter: definition.kind,
            meter_version: definition.version,
            meter_scope: definition.charge_scope,
            scope,
            replica_epoch,
            interval,
            measurement_window,
            integer_total,
            correction_of,
            checksum: [0; 32],
        };
        segment.checksum = checksum(&segment.canonical_body());
        if segment.encoded().len() > MAX_SEGMENT_BYTES {
            return Err(UsageError::SegmentTooLarge);
        }
        Ok(segment)
    }

    /// Aggregates facts that share one meter, scope, epoch, and window.
    pub fn from_facts(
        segment_id: SegmentId,
        definition: MeterDefinition,
        facts: &[UsageFact],
        replica_epoch: u64,
        interval: SequenceInterval,
        correction_of: Option<SegmentId>,
    ) -> Result<Self, UsageError> {
        let first = facts.first().ok_or(UsageError::ZeroDelta)?;
        let mut identities = BTreeSet::new();
        if facts.iter().any(|fact| !identities.insert(fact.identity.clone())) {
            return Err(UsageError::DuplicateFactIdentity);
        }
        if facts.iter().any(|fact| {
            fact.meter != definition.kind
                || fact.meter_version != definition.version
                || fact.scope != first.scope
                || fact.window != first.window
        }) {
            return Err(UsageError::InvalidMeterDefinition);
        }
        let total = facts
            .iter()
            .try_fold(0_i128, |sum, fact| sum.checked_add(fact.delta))
            .ok_or(UsageError::IntegerOverflow)?;
        Self::try_new(
            segment_id,
            definition,
            first.scope.clone(),
            replica_epoch,
            interval,
            first.window,
            total,
            correction_of,
        )
    }

    /// Decodes and verifies one canonical segment encoding.
    pub fn decode(bytes: &[u8]) -> Result<Self, UsageError> {
        if bytes.len() < 8 + 2 + 32 {
            return Err(UsageError::MalformedEncoding);
        }
        let (body, checksum_bytes) = bytes.split_at(bytes.len() - 32);
        let stored: [u8; 32] =
            checksum_bytes.try_into().map_err(|_| UsageError::MalformedEncoding)?;
        if checksum(body) != stored {
            return Err(UsageError::ChecksumMismatch);
        }
        let mut reader = Reader::new(body);
        if reader.take(8)? != b"TBUSEG01" {
            return Err(UsageError::MalformedEncoding);
        }
        if reader.u16()? != USAGE_FORMAT_VERSION {
            return Err(UsageError::MalformedEncoding);
        }
        let meter = meter_from_u8(reader.u8()?)?;
        let meter_version = reader.u32()?;
        let meter_scope = charge_scope_from_u8(reader.u8()?)?;
        if expected_meter(meter).1 != meter_scope {
            return Err(UsageError::InvalidMeterDefinition);
        }
        let tenant_text = reader.string()?;
        let tenant = TenantId::try_new(&tenant_text).map_err(|_| UsageError::InvalidIdentifier)?;
        let project = reader.string()?;
        let database = reader.string()?;
        let partition = reader.string()?;
        let region_text = reader.string()?;
        let region = RegionId::try_new(&region_text).map_err(|_| UsageError::InvalidIdentifier)?;
        let scope = UsageScope::try_new(tenant, &project, &database, &partition, region)?;
        let replica_epoch = reader.u64()?;
        let interval = SequenceInterval::try_new(reader.u64()?, reader.u64()?)?;
        let window = MeasurementWindow { start_millis: reader.u64()?, end_millis: reader.u64()? };
        let integer_total = reader.i128()?;
        let correction_of = match reader.u8()? {
            0 => None,
            1 => {
                let id = reader.string()?;
                Some(SegmentId::try_new(&id)?)
            }
            _ => return Err(UsageError::MalformedEncoding),
        };
        let segment_text = reader.string()?;
        let segment_id = SegmentId::try_new(&segment_text)?;
        if reader.remaining() != 0 {
            return Err(UsageError::NonCanonicalEncoding);
        }
        let segment = Self {
            segment_id,
            meter,
            meter_version,
            meter_scope,
            scope,
            replica_epoch,
            interval,
            measurement_window: window,
            integer_total,
            correction_of,
            checksum: stored,
        };
        segment.validate_shape()?;
        if segment.canonical_body().len() + 32 > MAX_SEGMENT_BYTES
            || segment.checksum != checksum(&segment.canonical_body())
            || segment.encoded().as_slice() != bytes
        {
            return Err(UsageError::NonCanonicalEncoding);
        }
        Ok(segment)
    }

    /// Returns the stable segment identity.
    #[must_use]
    pub fn segment_id(&self) -> &SegmentId {
        &self.segment_id
    }

    /// Returns the meter kind.
    #[must_use]
    pub const fn meter(&self) -> MeterKind {
        self.meter
    }

    /// Returns the meter version.
    #[must_use]
    pub const fn meter_version(&self) -> u32 {
        self.meter_version
    }

    /// Returns the meter's published charge scope.
    #[must_use]
    pub const fn meter_scope(&self) -> ChargeScope {
        self.meter_scope
    }

    /// Returns the accounting scope.
    #[must_use]
    pub fn scope(&self) -> &UsageScope {
        &self.scope
    }

    /// Returns the replica-set epoch attached to the segment.
    #[must_use]
    pub const fn replica_epoch(&self) -> u64 {
        self.replica_epoch
    }

    /// Returns the exact closed sequence interval.
    #[must_use]
    pub const fn interval(&self) -> SequenceInterval {
        self.interval
    }

    /// Returns the measurement window.
    #[must_use]
    pub const fn measurement_window(&self) -> MeasurementWindow {
        self.measurement_window
    }

    /// Returns the signed integer total.
    #[must_use]
    pub const fn integer_total(&self) -> i128 {
        self.integer_total
    }

    /// Returns the integer total under the schema's plural field name.
    #[must_use]
    pub const fn integer_totals(&self) -> i128 {
        self.integer_total
    }

    /// Returns the segment's sequence interval.
    #[must_use]
    pub const fn sequence_interval(&self) -> SequenceInterval {
        self.interval
    }

    /// Returns the base segment corrected by this segment, if any.
    #[must_use]
    pub fn correction_of(&self) -> Option<&SegmentId> {
        self.correction_of.as_ref()
    }

    /// Returns the stable checksum over the canonical body.
    #[must_use]
    pub const fn checksum(&self) -> [u8; 32] {
        self.checksum
    }

    /// Returns canonical bytes including the trailing checksum.
    #[must_use]
    pub fn encoded(&self) -> Vec<u8> {
        let mut bytes = self.canonical_body();
        bytes.extend_from_slice(&self.checksum);
        bytes
    }

    /// Returns canonical bytes including the trailing checksum.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        self.encoded()
    }

    fn validate_shape(&self) -> Result<(), UsageError> {
        if self.meter_version == 0 {
            return Err(UsageError::InvalidMeterVersion);
        }
        if expected_meter(self.meter).1 != self.meter_scope {
            return Err(UsageError::InvalidMeterDefinition);
        }
        if self.integer_total == 0 {
            return Err(UsageError::ZeroDelta);
        }
        self.measurement_window.validate_for(expected_meter(self.meter).3)?;
        if self.correction_of.is_none() && self.integer_total < 0 {
            return Err(UsageError::InvalidMeterDefinition);
        }
        Ok(())
    }

    fn canonical_body(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(256);
        bytes.extend_from_slice(b"TBUSEG01");
        bytes.extend_from_slice(&USAGE_FORMAT_VERSION.to_le_bytes());
        bytes.push(meter_to_u8(self.meter));
        bytes.extend_from_slice(&self.meter_version.to_le_bytes());
        bytes.push(charge_scope_to_u8(self.meter_scope));
        push_string(&mut bytes, self.scope.tenant_id().as_str());
        push_string(&mut bytes, self.scope.project_id().as_str());
        push_string(&mut bytes, self.scope.database_id().as_str());
        push_string(&mut bytes, self.scope.partition_id().as_str());
        push_string(&mut bytes, self.scope.region_id().as_str());
        bytes.extend_from_slice(&self.replica_epoch.to_le_bytes());
        bytes.extend_from_slice(&self.interval.first.to_le_bytes());
        bytes.extend_from_slice(&self.interval.last.to_le_bytes());
        bytes.extend_from_slice(&self.measurement_window.start_millis.to_le_bytes());
        bytes.extend_from_slice(&self.measurement_window.end_millis.to_le_bytes());
        bytes.extend_from_slice(&self.integer_total.to_le_bytes());
        match &self.correction_of {
            Some(id) => {
                bytes.push(1);
                push_string(&mut bytes, id.as_str());
            }
            None => bytes.push(0),
        }
        push_string(&mut bytes, self.segment_id.as_str());
        bytes
    }
}

/// The outcome of inserting one segment into a bounded in-memory index.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SegmentInsertOutcome {
    /// The segment was newly accepted.
    Inserted,
    /// The exact canonical segment was already present; no state changed.
    Duplicate,
}

/// A pure idempotency and interval index for usage segments.
#[derive(Clone, Debug, Default)]
pub struct UsageSegmentLedger {
    segments: BTreeMap<SegmentId, UsageSegment>,
}

impl UsageSegmentLedger {
    /// Inserts a segment, refusing identity conflicts, overlaps, and invalid corrections.
    pub fn insert(&mut self, segment: UsageSegment) -> Result<SegmentInsertOutcome, UsageError> {
        if let Some(existing) = self.segments.get(segment.segment_id()) {
            return if existing.encoded() == segment.encoded() {
                Ok(SegmentInsertOutcome::Duplicate)
            } else {
                Err(UsageError::SegmentIdentityConflict)
            };
        }
        if let Some(target_id) = segment.correction_of() {
            let target = self.segments.get(target_id).ok_or(UsageError::MissingCorrectionTarget)?;
            if target.correction_of().is_some() {
                return Err(UsageError::CorrectionChain);
            }
            if target.meter() != segment.meter()
                || target.meter_version() != segment.meter_version()
                || target.scope() != segment.scope()
            {
                return Err(UsageError::CorrectionScopeMismatch);
            }
        }
        if self.segments.values().any(|existing| {
            existing.meter() == segment.meter()
                && existing.meter_version() == segment.meter_version()
                && existing.scope() == segment.scope()
                && existing.replica_epoch() == segment.replica_epoch()
                && existing.interval.overlaps(segment.interval)
        }) {
            return Err(UsageError::SequenceOverlap);
        }
        self.segments.insert(segment.segment_id.clone(), segment);
        Ok(SegmentInsertOutcome::Inserted)
    }

    /// Accepts one segment under the same idempotency rules as [`Self::insert`].
    pub fn accept(&mut self, segment: UsageSegment) -> Result<SegmentInsertOutcome, UsageError> {
        self.insert(segment)
    }

    /// Looks up one accepted segment by stable identity.
    #[must_use]
    pub fn get(&self, id: &SegmentId) -> Option<&UsageSegment> {
        self.segments.get(id)
    }

    /// Returns accepted segments in stable identity order.
    pub fn segments(&self) -> impl Iterator<Item = &UsageSegment> {
        self.segments.values()
    }
}

fn validate_identifier(value: &str) -> Result<(), UsageError> {
    if value.is_empty()
        || value.len() > MAX_IDENTIFIER_BYTES
        || value != value.trim()
        || value.chars().any(char::is_control)
    {
        return Err(UsageError::InvalidIdentifier);
    }
    Ok(())
}

const fn expected_meter(
    kind: MeterKind,
) -> (MeterUnit, ChargeScope, MintingRule, MeasurementCadence, CorrectionProcedure) {
    match kind {
        MeterKind::StoredByteHours => (
            MeterUnit::ByteHour,
            ChargeScope::ActiveRegionCopy,
            MintingRule::AcceptedOriginCommit,
            MeasurementCadence::Interval,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
        MeterKind::AdmittedRequestUnits => (
            MeterUnit::RequestUnit,
            ChargeScope::AdmittedRequest,
            MintingRule::StableAdmittedRequest,
            MeasurementCadence::PerEvent,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
        MeterKind::PublicEgressBytes => (
            MeterUnit::Byte,
            ChargeScope::PublicEgress,
            MintingRule::PublicResponseBytes,
            MeasurementCadence::PerEvent,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
        MeterKind::ActiveRegionSeats => (
            MeterUnit::SeatHour,
            ChargeScope::ActiveRegionSeat,
            MintingRule::ActiveRegionHour,
            MeasurementCadence::Interval,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
        MeterKind::BackupByteHours => (
            MeterUnit::ByteHour,
            ChargeScope::BackupCopy,
            MintingRule::BackupByteHour,
            MeasurementCadence::Interval,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
        MeterKind::RestoreWork => (
            MeterUnit::RestoreWork,
            ChargeScope::ExplicitRestore,
            MintingRule::ExplicitRestoreWork,
            MeasurementCadence::PerEvent,
            CorrectionProcedure::AppendOnlySegmentReference,
        ),
    }
}

fn meter_to_u8(meter: MeterKind) -> u8 {
    match meter {
        MeterKind::StoredByteHours => 0,
        MeterKind::AdmittedRequestUnits => 1,
        MeterKind::PublicEgressBytes => 2,
        MeterKind::ActiveRegionSeats => 3,
        MeterKind::BackupByteHours => 4,
        MeterKind::RestoreWork => 5,
    }
}

fn meter_from_u8(value: u8) -> Result<MeterKind, UsageError> {
    Ok(match value {
        0 => MeterKind::StoredByteHours,
        1 => MeterKind::AdmittedRequestUnits,
        2 => MeterKind::PublicEgressBytes,
        3 => MeterKind::ActiveRegionSeats,
        4 => MeterKind::BackupByteHours,
        5 => MeterKind::RestoreWork,
        _ => return Err(UsageError::MalformedEncoding),
    })
}

fn charge_scope_to_u8(scope: ChargeScope) -> u8 {
    match scope {
        ChargeScope::ActiveRegionCopy => 0,
        ChargeScope::AdmittedRequest => 1,
        ChargeScope::PublicEgress => 2,
        ChargeScope::ActiveRegionSeat => 3,
        ChargeScope::BackupCopy => 4,
        ChargeScope::ExplicitRestore => 5,
    }
}

fn charge_scope_from_u8(value: u8) -> Result<ChargeScope, UsageError> {
    Ok(match value {
        0 => ChargeScope::ActiveRegionCopy,
        1 => ChargeScope::AdmittedRequest,
        2 => ChargeScope::PublicEgress,
        3 => ChargeScope::ActiveRegionSeat,
        4 => ChargeScope::BackupCopy,
        5 => ChargeScope::ExplicitRestore,
        _ => return Err(UsageError::MalformedEncoding),
    })
}

fn push_string(bytes: &mut Vec<u8>, value: &str) {
    let length = u16::try_from(value.len()).expect("validated usage identifiers fit u16");
    bytes.extend_from_slice(&length.to_le_bytes());
    bytes.extend_from_slice(value.as_bytes());
}

fn checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(USAGE_CHECKSUM_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], UsageError> {
        let end = self.position.checked_add(length).ok_or(UsageError::MalformedEncoding)?;
        let value = self.bytes.get(self.position..end).ok_or(UsageError::MalformedEncoding)?;
        self.position = end;
        Ok(value)
    }

    fn remaining(&self) -> usize {
        self.bytes.len() - self.position
    }

    fn u8(&mut self) -> Result<u8, UsageError> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, UsageError> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().expect("length checked")))
    }

    fn u32(&mut self) -> Result<u32, UsageError> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().expect("length checked")))
    }

    fn u64(&mut self) -> Result<u64, UsageError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().expect("length checked")))
    }

    fn i128(&mut self) -> Result<i128, UsageError> {
        Ok(i128::from_le_bytes(self.take(16)?.try_into().expect("length checked")))
    }

    fn string(&mut self) -> Result<String, UsageError> {
        let length = usize::from(self.u16()?);
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| UsageError::InvalidIdentifier)
    }
}

#[cfg(test)]
mod tests;
