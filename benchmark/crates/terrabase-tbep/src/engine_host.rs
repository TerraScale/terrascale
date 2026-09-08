//! The engine-backed TBEP host (P4): the real durable Engine serving
//! the protocol's control surface.
//!
//! [`EngineHost`] implements [`TbepHost`] over one `terrabase`
//! `Engine` at a durable location: channel binds validate the tenant
//! identifier and the engine accessor hands the public API router
//! (`S-03`) the same storage truth. The document/query/family/sync-session
//! data-plane RPCs were deleted with the S-03 TBEP reduction; the host now
//! serves collection schemas, topology, usage, and supervision. Unsupported
//! operations fail with typed errors.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use terrabase::engine::{CommitOutcome, Engine, EngineConfig};
use terrabase::observability::MetricsRegistry;
use terrabase::regions::RegionCode;
use terrabase::types::{CollectionName, DocumentValue, OperationId, TenantId, TenantScope};
use terrabase::usage::{
    MeasurementWindow, MeterKind, MintingRule as EngineMintingRule, UsageCommandError,
    UsageContinuation, UsageFactId, UsagePageRequest, UsageRecordMeter as EngineUsageRecordMeter,
    UsageRecordRequest as EngineUsageRecordRequest,
};
use terrabase_replication_core::anti_entropy::{
    JournalRowId, RangeSummaryTable, RowSummary, row_hash,
};

use crate::anti_entropy::{DurableJournalRow, JournalRowSource, JournalSummary};
use crate::host::{HostingError, TbepHost};
use crate::registry::{
    AdminStatusResponse, BoundScope, ChannelBindRequest, ChannelBindResponse, HealthState,
    REGISTRY_VERSION, SupervisionHealthResponse, SupervisionShutdownRequest,
    SupervisionShutdownResponse,
};
use crate::value::TbepValue;

/// Converts one wire value into the engine's value model.
fn to_engine_value(value: &TbepValue) -> DocumentValue {
    match value {
        TbepValue::Null => DocumentValue::Null,
        TbepValue::Boolean(flag) => DocumentValue::Boolean(*flag),
        TbepValue::Integer(number) => DocumentValue::Integer(*number),
        TbepValue::Number(number) => DocumentValue::Number(*number),
        TbepValue::String(text) => DocumentValue::String(text.clone()),
        TbepValue::Array(items) => {
            DocumentValue::Array(items.iter().map(to_engine_value).collect())
        }
        // The wire's object is a name-sorted field list; the engine's is
        // the sorted map of the same fields.
        TbepValue::Object(fields) => DocumentValue::Object(
            fields.iter().map(|(name, value)| (name.clone(), to_engine_value(value))).collect(),
        ),
    }
}

/// Evaluates the public schema subset at the TBEP boundary. The family
/// schema evaluator is intentionally crate-private; keeping this small
/// adapter-side projection lets the protocol return the same closed
/// violation vocabulary without exposing an evaluator as a new API seam.
fn schema_violations(
    schema: &terrabase::families::SchemaDefinition,
    value: &DocumentValue,
) -> Vec<(String, terrabase::families::ViolationKind)> {
    use terrabase::families::{SchemaFieldType, SchemaRule, ViolationKind};
    let DocumentValue::Object(fields) = value else {
        return if schema.rules().is_empty() {
            Vec::new()
        } else {
            vec![(String::new(), ViolationKind::DocumentNotObject)]
        };
    };
    let mut result = Vec::new();
    for rule in schema.rules() {
        let field = rule.field();
        let present = fields.get(field);
        match rule {
            SchemaRule::Required { .. } if present.is_none() => {
                result.push((field.to_owned(), ViolationKind::MissingRequiredField));
            }
            SchemaRule::FieldType { kind, .. }
                if present.is_some_and(|value| {
                    !matches!(
                        (kind, value),
                        (SchemaFieldType::Boolean, DocumentValue::Boolean(_))
                            | (
                                SchemaFieldType::Integer | SchemaFieldType::Number,
                                DocumentValue::Integer(_)
                            )
                            | (SchemaFieldType::Number, DocumentValue::Number(_))
                            | (SchemaFieldType::Text, DocumentValue::String(_))
                            | (SchemaFieldType::Object, DocumentValue::Object(_))
                            | (SchemaFieldType::Array, DocumentValue::Array(_))
                    )
                }) =>
            {
                result.push((field.to_owned(), ViolationKind::TypeMismatch));
            }
            SchemaRule::EnumValues { values, .. } => {
                if let Some(value) = present
                    && !matches!(value, DocumentValue::String(text) if values.contains(text))
                {
                    result.push((field.to_owned(), ViolationKind::ValueNotAllowed));
                }
            }
            SchemaRule::NumberRange { min, max, .. } => {
                let out_of_range = match present {
                    Some(DocumentValue::Integer(number)) => {
                        min.is_some_and(|bound| *number < bound)
                            || max.is_some_and(|bound| *number > bound)
                    }
                    Some(DocumentValue::Number(number)) => {
                        !number.is_finite()
                            || min.is_some_and(|bound| {
                                compare_number_to_integer(*number, bound)
                                    == Some(std::cmp::Ordering::Less)
                            })
                            || max.is_some_and(|bound| {
                                compare_number_to_integer(*number, bound)
                                    == Some(std::cmp::Ordering::Greater)
                            })
                    }
                    _ => false,
                };
                if out_of_range {
                    result.push((field.to_owned(), ViolationKind::NumberOutOfRange));
                }
            }
            SchemaRule::TextLength { min, max, .. } => {
                let out_of_range = match present {
                    Some(DocumentValue::String(text)) => {
                        let length = text.chars().count() as u64;
                        min.is_some_and(|bound| length < bound)
                            || max.is_some_and(|bound| length > bound)
                    }
                    _ => false,
                };
                if out_of_range {
                    result.push((field.to_owned(), ViolationKind::LengthOutOfRange));
                }
            }
            _ => {}
        }
    }
    result
}

const fn engine_region(region: crate::registry::UsageRegion) -> RegionCode {
    match region {
        crate::registry::UsageRegion::Gru => RegionCode::GRU,
        crate::registry::UsageRegion::Sfo => RegionCode::SFO,
        crate::registry::UsageRegion::Fra => RegionCode::FRA,
    }
}

fn wire_region(region: RegionCode) -> Result<crate::registry::UsageRegion, HostingError> {
    match region {
        RegionCode::GRU => Ok(crate::registry::UsageRegion::Gru),
        RegionCode::SFO => Ok(crate::registry::UsageRegion::Sfo),
        RegionCode::FRA => Ok(crate::registry::UsageRegion::Fra),
        _ => Err(HostingError::UsageCorrupt(
            "a durable usage fact carries an unsupported Region".into(),
        )),
    }
}

fn usage_error(error: UsageCommandError) -> HostingError {
    match error {
        UsageCommandError::InvalidRequest
        | UsageCommandError::ScopeMismatch
        | UsageCommandError::IdentityMismatch => HostingError::Invalid(error.to_string()),
        UsageCommandError::IdentityConflict | UsageCommandError::RequiresSynced => {
            HostingError::Refused(error.to_string())
        }
        UsageCommandError::UsageUnavailable | UsageCommandError::StorageUnavailable => {
            HostingError::UsageUnavailable(error.to_string())
        }
        UsageCommandError::CorruptStorage => HostingError::UsageCorrupt(error.to_string()),
    }
}

fn wire_usage_fact(
    durable: &terrabase::usage::DurableUsageFact,
) -> Result<crate::registry::UsageFact, HostingError> {
    let fact = durable.fact();
    let (meter, unit, charge_scope, minting_rule) = match (fact.meter(), fact.identity().rule()) {
        (MeterKind::DocumentWriteUnits, EngineMintingRule::AcceptedOriginCommit) => (
            crate::registry::UsageMeter::DocumentWriteUnits,
            crate::registry::UsageUnit::WriteUnit,
            crate::registry::UsageChargeScope::DocumentWrite,
            crate::registry::UsageMintingRule::AcceptedOriginCommit,
        ),
        (MeterKind::StorageByteSeconds, EngineMintingRule::StableAdmittedRequest) => (
            crate::registry::UsageMeter::StorageByteSeconds,
            crate::registry::UsageUnit::ByteSecond,
            crate::registry::UsageChargeScope::LogicalStorage,
            crate::registry::UsageMintingRule::LogicalStorage,
        ),
        _ => {
            return Err(HostingError::UsageCorrupt(
                "a durable usage fact carries an unsupported meter contract".into(),
            ));
        }
    };
    let canonical = durable.encoded();
    let checksum = canonical
        .get(canonical.len().saturating_sub(32)..)
        .ok_or_else(|| HostingError::UsageCorrupt("a durable usage checksum is absent".into()))?
        .to_vec();
    let scope = fact.scope();
    let window = fact.window();
    Ok(crate::registry::UsageFact {
        fact_id: fact.identity().id().as_str().to_owned(),
        meter,
        meter_version: fact.meter_version(),
        unit,
        charge_scope,
        minting_rule,
        tenant: scope.tenant_id().as_str().to_owned(),
        project: scope.project_id().as_str().to_owned(),
        database: scope.database_id().as_str().to_owned(),
        partition: scope.partition_id().as_str().to_owned(),
        region: wire_region(*scope.region_id())?,
        measurement_start_millis: window.start_millis(),
        measurement_end_millis: window.end_millis(),
        integer_delta: fact.delta().to_string(),
        sequence: durable.sequence(),
        replica_epoch: durable.replica_epoch(),
        format_version: u32::from(terrabase::usage::USAGE_FORMAT_VERSION),
        canonical,
        checksum,
    })
}

/// The TBEP host backed by one durable Engine.
enum HostEngine {
    Local(Box<Engine>),
    Admitted(Arc<terrabase::member::DatabaseLease>),
}

impl std::ops::Deref for HostEngine {
    type Target = Engine;
    fn deref(&self) -> &Engine {
        match self {
            Self::Local(engine) => engine,
            Self::Admitted(lease) => lease,
        }
    }
}

/// TBEP adapter retaining either a local engine or its actual Member admission lease.
pub struct EngineHost {
    engine: HostEngine,
    location: PathBuf,
    tenant: String,
    project: String,
    database: String,
    instance: u64,
    frames_served: Mutex<u64>,
    health: Mutex<HealthState>,
}

impl EngineHost {
    /// Returns whether this host owns a Member admission lease.
    #[must_use]
    pub fn is_admitted(&self) -> bool {
        matches!(self.engine, HostEngine::Admitted(_))
    }
    /// Consumes an admitted host and releases its exclusive Member lease.
    pub fn into_admitted_lease(self) -> Result<terrabase::member::DatabaseLease, HostingError> {
        match self.engine {
            HostEngine::Admitted(lease) => Arc::try_unwrap(lease)
                .map_err(|_| HostingError::Refused("engine lease is still shared".into())),
            HostEngine::Local(_) => {
                Err(HostingError::Refused("local engine has no admitted Member lease".into()))
            }
        }
    }
    /// Opens the host's engine at a durable location, creating the
    /// directory when absent.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::EngineFailure`] when the location or
    /// engine cannot open.
    pub fn open(
        location: &Path,
        instance: u64,
        tenant: &str,
        project: &str,
        database: &str,
    ) -> Result<Self, HostingError> {
        let bound = BoundScope {
            principal: "spiffe://local/engine-host".into(),
            tenant: tenant.to_owned(),
            project: project.to_owned(),
            database: database.to_owned(),
        };
        bound
            .validate()
            .map_err(|_| HostingError::Invalid("the engine database scope is invalid".into()))?;
        let engine = Engine::open(
            EngineConfig::durable(location)
                .map_err(|_| HostingError::EngineFailure("location refused".into()))?,
        )
        .map_err(|_| HostingError::EngineFailure("engine open refused".into()))?;
        Ok(Self {
            engine: HostEngine::Local(Box::new(engine)),
            location: location.to_path_buf(),
            tenant: tenant.to_owned(),
            project: project.to_owned(),
            database: database.to_owned(),
            instance,
            frames_served: Mutex::new(0),
            health: Mutex::new(HealthState::Serving),
        })
    }

    /// Composes the exact Member engine while retaining its admission lease.
    #[must_use]
    pub fn from_admitted(
        lease: terrabase::member::DatabaseLease,
        location: PathBuf,
        instance: u64,
    ) -> Self {
        let scope = lease.database_owner().clone();
        Self {
            tenant: scope.tenant().to_string(),
            project: scope.project().to_string(),
            database: scope.database().to_string(),
            engine: HostEngine::Admitted(Arc::new(lease)),
            location,
            instance,
            frames_served: Mutex::new(0),
            health: Mutex::new(HealthState::Serving),
        }
    }

    /// Logical tenant bound to this adapter's admitted engine.
    #[must_use]
    pub fn tenant(&self) -> &str {
        &self.tenant
    }

    /// Returns the durable location the host's engine serves.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
    }

    /// Returns the host's engine — the public API router (`S-03`) serves
    /// documents through the same engine the TBEP surface does, so one
    /// database has one storage truth per member.
    #[must_use]
    pub fn engine(&self) -> &Engine {
        &self.engine
    }

    /// Begins a named document write through the authoritative serving
    /// router and binds its generation to the Engine transaction.
    pub fn begin_routed_transaction(
        &self,
        scope: &TenantScope,
        router: &terrabase::shard_split::ShardRouter,
        key: &[u8],
        operation: OperationId,
    ) -> Result<terrabase::engine::Transaction<'_>, terrabase::engine::EngineError> {
        let token = self.engine.shard_admission_for_key(scope, router, key)?;
        let mut transaction =
            self.engine.begin(scope, self.engine.read_snapshot(scope)?, operation)?;
        transaction.bind_shard_admission(token)?;
        Ok(transaction)
    }

    /// Takes a read snapshot for a validated engine scope.
    pub(crate) fn read_snapshot(
        &self,
        scope: &TenantScope,
    ) -> Result<terrabase::types::ReadSnapshot, terrabase::engine::EngineError> {
        self.engine.read_snapshot(scope)
    }

    /// Returns the engine-owned metrics registry shared by storage and every
    /// serving adapter for this location.
    #[must_use]
    pub fn metrics(&self) -> Arc<MetricsRegistry> {
        self.engine.metrics()
    }

    /// Pulls one bounded page from this host's engine publication journal for
    /// the opt-in `MemberHost` sync materializer.
    pub(crate) fn replay_sync_journal(
        &self,
        scope: &BoundScope,
        after: Option<terrabase::streaming::ChangeCheckpoint>,
    ) -> Result<terrabase::streaming::SyncJournalBatch, HostingError> {
        let tenant = self.tenant_of_scope(scope)?;
        let engine_scope = self.engine.scope_for(&tenant);
        self.engine
            .streams()
            .replay_sync_journal(
                &engine_scope,
                after,
                terrabase::streaming::MAX_STREAM_BATCH_ITEMS,
                &terrabase::types::Cancellation::default(),
            )
            .map_err(|error| {
                HostingError::EngineFailure(format!(
                    "the durable sync journal could not be replayed: {error:?}"
                ))
            })
    }

    fn admit_scope(&self, scope: &BoundScope) -> Result<(), HostingError> {
        if scope.tenant != self.tenant
            || scope.project != self.project
            || scope.database != self.database
        {
            return Err(HostingError::Refused(
                "the engine location serves another tenant, Project, or Database".into(),
            ));
        }
        Ok(())
    }

    fn tenant_of_scope(&self, scope: &BoundScope) -> Result<TenantId, HostingError> {
        self.admit_scope(scope)?;
        Self::tenant_of(&scope.tenant)
    }

    /// The engine-side scope of one bound scope: the engine's tenant
    /// binding for the same tenant, project, and database.
    pub(crate) fn engine_scope_of(
        &self,
        scope: &BoundScope,
    ) -> Result<terrabase::types::TenantScope, HostingError> {
        let tenant = self.tenant_of_scope(scope)?;
        Ok(self.engine.scope_for(&tenant))
    }

    fn count_frame(&self) {
        let mut frames = self.frames_served.lock().expect("the frame counter is not poisoned");
        *frames += 1;
    }

    fn ensure_serving(&self) -> Result<(), HostingError> {
        match *self.health.lock().expect("the health state is not poisoned") {
            HealthState::Serving => Ok(()),
            HealthState::Starting => Err(HostingError::Refused("the host is starting".into())),
            HealthState::Draining => Err(HostingError::Refused("the host is draining".into())),
            HealthState::Stopped => Err(HostingError::Refused("the host is stopped".into())),
        }
    }

    fn tenant_of(tenant: &str) -> Result<TenantId, HostingError> {
        TenantId::try_new(tenant)
            .map_err(|_| HostingError::Invalid("the tenant identifier is not valid".into()))
    }

    /// Reads the collection's recorded validation policy.
    fn validation_policy(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
    ) -> Result<Option<terrabase::families::SchemaDefinition>, HostingError> {
        let read = terrabase::families::ValidationRead::try_new(
            collection.clone(),
            None,
            terrabase::types::Deadline::after(std::time::Duration::from_secs(30))
                .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?,
            terrabase::types::Cancellation::default(),
        )
        .map_err(|_| HostingError::Invalid("the validation read is not valid".into()))?;
        let scope = self.engine.scope_for(tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let context = terrabase::families::FamilyReadContext::new(scope, snapshot);
        let request = terrabase::families::FamilyReadRequest::validation(read);
        match self
            .engine
            .families()
            .read(context, request)
            .map_err(|_| HostingError::EngineFailure("the validation read failed".into()))?
        {
            terrabase::families::FamilyReadResult::Validation(result) => {
                Ok(result.policy().cloned())
            }
            _ => Err(HostingError::EngineFailure(
                "the validation read answered another family".into(),
            )),
        }
    }
}

impl TbepHost for EngineHost {
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<ChannelBindResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.tenant_of_scope(&request.scope)?;
        Ok(ChannelBindResponse { scope: request.scope.clone(), serving_instance: self.instance })
    }

    fn backbone_subscribe(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::BackboneSubscribeRequest,
    ) -> Result<
        (
            crate::registry::BackboneSubscribeResponse,
            std::sync::mpsc::Receiver<crate::host::TbepNotification>,
        ),
        HostingError,
    > {
        Err(HostingError::Refused(
            "backbone delivery is served by the sync-delivery host beside this one".into(),
        ))
    }

    fn backbone_acknowledge(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::BackboneAcknowledgeRequest,
    ) -> Result<crate::registry::BackboneAcknowledgeResponse, HostingError> {
        Err(HostingError::Refused(
            "backbone delivery is served by the sync-delivery host beside this one".into(),
        ))
    }

    fn backbone_unsubscribe(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::BackboneUnsubscribeRequest,
    ) -> Result<crate::registry::BackboneUnsubscribeResponse, HostingError> {
        Err(HostingError::Refused(
            "backbone delivery is served by the sync-delivery host beside this one".into(),
        ))
    }

    fn topology_read(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TopologyReadResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        // Honest standalone answer: this member serves every subsystem
        // it hosts over one local binding; no cluster topology exists
        // until the cluster-mode landing admits peers.
        Ok(crate::registry::TopologyReadResponse {
            binding: crate::registry::BindingKind::LocalUds,
            subsystems: vec![
                "documents".into(),
                "query".into(),
                "schema".into(),
                "supervision".into(),
                "topology".into(),
            ],
            engine_topology_available: false,
            cluster_mode: Some(crate::registry::ClusterMode::Standalone),
        })
    }

    fn usage_record(
        &self,
        scope: &BoundScope,
        request: &crate::registry::UsageRecordRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::UsageRecordResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        request
            .validate_for_key(idempotency_key)
            .map_err(|_| HostingError::Invalid("the usage record is invalid".into()))?;
        let tenant = self.tenant_of_scope(scope)?;
        if request.project != scope.project || request.database != scope.database {
            return Err(HostingError::Invalid(
                "the usage record does not match the bound Project and Database".into(),
            ));
        }
        let fact_id = UsageFactId::try_new(&request.fact_id)
            .map_err(|_| HostingError::Invalid("the usage fact identity is invalid".into()))?;
        let integer_units = request
            .integer_units
            .parse::<i128>()
            .map_err(|_| HostingError::Invalid("the usage units are invalid".into()))?;
        let meter = match request.meter {
            crate::registry::UsageRecordMeter::DocumentWriteUnits => {
                EngineUsageRecordMeter::DocumentWriteUnits
            }
            crate::registry::UsageRecordMeter::StorageByteSeconds => {
                EngineUsageRecordMeter::StorageByteSeconds
            }
            #[allow(deprecated)]
            crate::registry::UsageRecordMeter::AdmittedRequestUnits
            | crate::registry::UsageRecordMeter::PublicEgressBytes
            | crate::registry::UsageRecordMeter::ActiveRegionSeats
            | crate::registry::UsageRecordMeter::BackupByteHours
            | crate::registry::UsageRecordMeter::RestoreWork => {
                return Err(HostingError::Invalid("the usage meter is not billable".into()));
            }
        };
        let command = EngineUsageRecordRequest::try_new(
            meter,
            &request.project,
            &request.database,
            &request.partition,
            engine_region(request.origin_region),
            fact_id,
            request.recorded_at_millis,
            request.meter_version,
            integer_units,
        )
        .map_err(|_| HostingError::Invalid("the usage record is invalid".into()))?;
        let scope = self.engine.scope_for(&tenant);
        let receipt = self.engine.record_usage(&scope, command).map_err(usage_error)?;
        if receipt.fact_id().as_str() != request.fact_id {
            return Err(HostingError::UsageCorrupt(
                "the durable usage receipt carries another fact identity".into(),
            ));
        }
        Ok(crate::registry::UsageRecordResponse {
            sequence: receipt.sequence().get(),
            replica_epoch: receipt.replica_epoch(),
            already_recorded: receipt.already_recorded(),
        })
    }

    fn usage_export(
        &self,
        scope: &BoundScope,
        request: &crate::registry::UsageExportRequest,
    ) -> Result<crate::registry::UsageExportResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        request
            .validate()
            .map_err(|_| HostingError::Invalid("the usage export request is invalid".into()))?;
        let tenant = self.tenant_of_scope(scope)?;
        let window = match (request.window_start_millis, request.window_end_millis) {
            (None, None) => None,
            (Some(start), Some(end)) => Some(
                MeasurementWindow::interval(start, end)
                    .map_err(|_| HostingError::Invalid("the usage window is invalid".into()))?,
            ),
            _ => return Err(HostingError::Invalid("the usage window is invalid".into())),
        };
        let continuation =
            if request.continuation.is_empty() {
                None
            } else {
                Some(UsageContinuation::try_new(request.continuation.clone()).map_err(|_| {
                    HostingError::Invalid("the usage continuation is invalid".into())
                })?)
            };
        let command = UsagePageRequest::try_new(window, request.page_size, continuation)
            .map_err(|_| HostingError::Invalid("the usage page request is invalid".into()))?;
        let scope = self.engine.scope_for(&tenant);
        let page = self.engine.usage_page(&scope, command).map_err(usage_error)?;
        let facts = page.facts().iter().map(wire_usage_fact).collect::<Result<Vec<_>, _>>()?;
        Ok(crate::registry::UsageExportResponse {
            facts,
            continuation: page
                .continuation()
                .map_or_else(Vec::new, |value| value.as_bytes().to_vec()),
            snapshot_sequence: page.snapshot_sequence().get(),
        })
    }

    fn schema_declare(
        &self,
        scope: &BoundScope,
        request: &crate::registry::SchemaDeclareRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::SchemaDeclareResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        // The declared schema value adopts the collection's NEXT version:
        // version evolution is forward-only in the engine, so the host
        // derives it from the recorded policy.
        let current = self.validation_policy(&tenant, &collection)?;
        let next_version = terrabase::families::SchemaVersion::try_new(
            current.as_ref().map_or(1, |policy| policy.version().get() + 1),
        )
        .map_err(|_| HostingError::Invalid("the derived schema version is not valid".into()))?;
        let schema = match &request.schema {
            None => None,
            Some(value) => Some(
                terrabase::families::SchemaDefinition::from_json_schema(
                    next_version,
                    &to_engine_value(value),
                )
                .map_err(|_| {
                    HostingError::Invalid("the schema value is not a valid definition".into())
                })?,
            ),
        };
        let identity = if idempotency_key.is_empty() {
            request.operation_id.as_str()
        } else {
            idempotency_key
        };
        let operation = OperationId::try_new(identity)
            .map_err(|_| HostingError::Invalid("the operation identity is not valid".into()))?;
        let write = terrabase::families::ValidationWrite::try_new(
            self.engine.scope_for(&tenant),
            collection.clone(),
            schema,
            operation,
            terrabase::types::Deadline::after(std::time::Duration::from_secs(30))
                .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?,
            terrabase::types::Cancellation::default(),
        )
        .map_err(|_| HostingError::Invalid("the schema declaration is not valid".into()))?;
        let scope = self.engine.scope_for(&tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let mut transaction = self
            .engine
            .begin(
                &scope,
                snapshot,
                OperationId::try_new(&request.operation_id).map_err(|_| {
                    HostingError::Invalid("the operation identity is not valid".into())
                })?,
            )
            .map_err(|_| HostingError::EngineFailure("the declaration could not begin".into()))?;
        self.engine
            .families()
            .stage(
                &mut transaction,
                terrabase::families::FamilyWriteRequest::schema_validation_update(write),
            )
            .map_err(|_| HostingError::Refused("the schema declaration was refused".into()))?;
        match transaction.commit(terrabase::types::LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {}
            CommitOutcome::Rejected(_) => {
                return Err(HostingError::Refused("the schema declaration conflicted".into()));
            }
            CommitOutcome::Unknown(_) => {
                return Err(HostingError::EngineFailure(
                    "the declaration is uncertain; retry the same idempotency key".into(),
                ));
            }
        }
        let policy = self.validation_policy(&tenant, &collection)?;
        Ok(crate::registry::SchemaDeclareResponse {
            enforced_version: policy.as_ref().map(|policy| policy.version().get()),
            // The rule set is bounded well below the wire's u32; the
            // saturating conversion can never lose a rule.
            rule_count: policy
                .as_ref()
                .map_or(0, |policy| u32::try_from(policy.rules().len()).unwrap_or(u32::MAX)),
        })
    }

    fn schema_validate(
        &self,
        scope: &BoundScope,
        request: &crate::registry::SchemaValidateRequest,
    ) -> Result<crate::registry::SchemaValidateResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let Some(policy) = self.validation_policy(&tenant, &collection)? else {
            // Schema-free collections carry no rule set: any body is
            // valid, and the empty version says why.
            return Ok(crate::registry::SchemaValidateResponse {
                enforced_version: None,
                valid: true,
                violations: Vec::new(),
            });
        };
        let body = to_engine_value(&request.document);
        let violations = schema_violations(&policy, &body)
            .into_iter()
            .map(|violation| crate::registry::SchemaViolation {
                field: violation.0,
                kind: match violation.1 {
                    terrabase::families::ViolationKind::DocumentNotObject => {
                        crate::registry::ViolationKind::DocumentNotObject
                    }
                    terrabase::families::ViolationKind::MissingRequiredField => {
                        crate::registry::ViolationKind::MissingRequiredField
                    }
                    terrabase::families::ViolationKind::TypeMismatch => {
                        crate::registry::ViolationKind::TypeMismatch
                    }
                    terrabase::families::ViolationKind::ValueNotAllowed => {
                        crate::registry::ViolationKind::ValueNotAllowed
                    }
                    terrabase::families::ViolationKind::NumberOutOfRange => {
                        crate::registry::ViolationKind::NumberOutOfRange
                    }
                    terrabase::families::ViolationKind::LengthOutOfRange => {
                        crate::registry::ViolationKind::LengthOutOfRange
                    }
                },
            })
            .collect::<Vec<_>>();
        let enforced_version = Some(policy.version().get());
        let valid = violations.is_empty();
        Ok(crate::registry::SchemaValidateResponse { enforced_version, valid, violations })
    }

    fn schema_status(
        &self,
        scope: &BoundScope,
        request: &crate::registry::SchemaStatusRequest,
    ) -> Result<crate::registry::SchemaStatusResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let policy = self.validation_policy(&tenant, &collection)?;
        Ok(crate::registry::SchemaStatusResponse {
            enforced_version: policy.as_ref().map(|policy| policy.version().get()),
            // The rule set is bounded well below the wire's u32; the
            // saturating conversion can never lose a rule.
            rule_count: policy
                .as_ref()
                .map_or(0, |policy| u32::try_from(policy.rules().len()).unwrap_or(u32::MAX)),
        })
    }

    fn admin_status(&self) -> Result<AdminStatusResponse, HostingError> {
        let frames = *self.frames_served.lock().expect("the frame counter is not poisoned");
        Ok(AdminStatusResponse {
            registry_version: REGISTRY_VERSION.into(),
            frames_served: frames,
            sessions: 0,
            subscriptions: 0,
            scopes: 0,
        })
    }

    fn health(&self) -> Result<SupervisionHealthResponse, HostingError> {
        let frames = *self.frames_served.lock().expect("the frame counter is not poisoned");
        let state = *self.health.lock().expect("the health state is not poisoned");
        Ok(SupervisionHealthResponse {
            state,
            sessions: 0,
            subscriptions: 0,
            frames_served: frames,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError> {
        let mut state = self.health.lock().expect("the health state is not poisoned");
        if *state == HealthState::Serving {
            *state = HealthState::Draining;
        }
        Ok(SupervisionShutdownResponse { state: *state, in_flight: 0 })
    }
}

impl JournalRowSource for EngineHost {
    fn durable_journal_rows(
        &self,
        scope: &BoundScope,
    ) -> Result<Vec<DurableJournalRow>, HostingError> {
        const PAGE_SIZE: u32 = 256;
        let tenant = self.tenant_of_scope(scope)?;
        let engine_scope = self.engine.scope_for(&tenant);
        let mut after = 0_u64;
        let mut rows = Vec::new();
        loop {
            let page =
                self.engine.journal_rows(&engine_scope, after, PAGE_SIZE).map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the durable anti-entropy journal could not be read: {error:?}"
                    ))
                })?;
            let Some(last) = page.last() else {
                let published = self.engine.published_sequence().map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the anti-entropy journal boundary could not be verified: {error:?}"
                    ))
                })?;
                if published.get() > after {
                    return Err(HostingError::EngineFailure(
                        "the anti-entropy journal read ended before its durable boundary".into(),
                    ));
                }
                break;
            };
            let last_sequence = last.commit_sequence;
            for row in page {
                if row.origin.is_empty() || row.origin_sequence == 0 {
                    return Err(HostingError::EngineFailure(
                        "the durable journal row has no stable source origin sequence".into(),
                    ));
                }
                let id = JournalRowId::new(row.origin, row.origin_sequence).map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the durable journal row identity is invalid: {error}"
                    ))
                })?;
                rows.push(DurableJournalRow::new(id, row_hash(&row.canonical)));
            }
            if last_sequence <= after {
                return Err(HostingError::EngineFailure(
                    "the durable anti-entropy journal cursor did not advance".into(),
                ));
            }
            after = last_sequence;
        }
        Ok(rows)
    }

    fn durable_journal_summary(
        &self,
        scope: &BoundScope,
        max_rows: usize,
    ) -> Result<JournalSummary, HostingError> {
        EngineHost::durable_journal_summary(self, scope, max_rows)
    }
}

impl EngineHost {
    /// Builds the range summaries directly from bounded journal pages.  The
    /// repair path must not first materialize every retained row in a Vec:
    /// only the current storage page and the capped summary table are live.
    pub(crate) fn durable_journal_summary(
        &self,
        scope: &BoundScope,
        max_rows: usize,
    ) -> Result<JournalSummary, HostingError> {
        const PAGE_SIZE: u32 = 256;
        if max_rows == 0 {
            return Err(HostingError::Invalid("the journal row budget must be nonzero".into()));
        }
        let tenant = self.tenant_of_scope(scope)?;
        let engine_scope = self.engine.scope_for(&tenant);
        let mut after = 0_u64;
        let mut table = RangeSummaryTable::new();
        let mut inspected = 0_usize;
        loop {
            let page =
                self.engine.journal_rows(&engine_scope, after, PAGE_SIZE).map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the durable anti-entropy journal could not be read: {error:?}"
                    ))
                })?;
            if page.is_empty() {
                // An empty page is a valid end only when the storage cursor is
                // also at the published boundary.  Treat a missing page in
                // front of that boundary as an unavailable/partial read.
                let published = self.engine.published_sequence().map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the anti-entropy journal boundary could not be verified: {error:?}"
                    ))
                })?;
                if published.get() > after {
                    return Err(HostingError::EngineFailure(
                        "the anti-entropy journal read ended before its durable boundary".into(),
                    ));
                }
                return Ok(JournalSummary { table, complete: true });
            }
            if inspected.saturating_add(page.len()) > max_rows {
                return Ok(JournalSummary { table, complete: false });
            }
            let Some(last) = page.last() else {
                unreachable!("the nonempty journal page has a last row");
            };
            let last_sequence = last.commit_sequence;
            for row in page {
                if row.origin.is_empty() || row.origin_sequence == 0 {
                    return Err(HostingError::EngineFailure(
                        "the durable journal row has no stable source origin sequence".into(),
                    ));
                }
                let id = JournalRowId::new(row.origin, row.origin_sequence).map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the durable journal row identity is invalid: {error}"
                    ))
                })?;
                let summary = RowSummary::new(id, row_hash(&row.canonical));
                if table.insert(&summary).is_some() {
                    return Err(HostingError::EngineFailure(
                        "the durable anti-entropy journal contains a duplicate row identity".into(),
                    ));
                }
                inspected = inspected.saturating_add(1);
            }
            if last_sequence <= after {
                return Err(HostingError::EngineFailure(
                    "the durable anti-entropy journal cursor did not advance".into(),
                ));
            }
            after = last_sequence;
        }
    }
}

// Compare without rounding an integer bound to f64. The cast truncates only
// after the range checks prove that the finite value fits in i64.
#[allow(
    clippy::cast_possible_truncation,
    reason = "range checked truncation is used with the fractional remainder"
)]
fn compare_number_to_integer(number: f64, bound: i64) -> Option<std::cmp::Ordering> {
    use std::cmp::Ordering;
    const I64_UPPER_EXCLUSIVE: f64 = 9_223_372_036_854_775_808.0;
    if number.is_nan() {
        return None;
    }
    if number >= I64_UPPER_EXCLUSIVE {
        return Some(Ordering::Greater);
    }
    if number < -I64_UPPER_EXCLUSIVE {
        return Some(Ordering::Less);
    }
    match (number as i64).cmp(&bound) {
        Ordering::Equal => number.fract().partial_cmp(&0.0),
        ordering => Some(ordering),
    }
}

#[cfg(test)]
mod numeric_bound_tests {
    use super::compare_number_to_integer;
    use std::cmp::Ordering::{Equal, Greater, Less};

    #[test]
    fn floating_point_bounds_preserve_integer_precision() {
        for (number, bound, expected) in [
            (9_007_199_254_740_992.0, 9_007_199_254_740_993, Less),
            (-9_007_199_254_740_992.0, -9_007_199_254_740_993, Greater),
            (9_223_372_036_854_775_808.0, i64::MAX, Greater),
            (-9_223_372_036_854_775_808.0, i64::MIN, Equal),
            (-9_223_372_036_854_775_808.0, i64::MIN + 1, Less),
            (0.5, 0, Greater),
            (-0.5, 0, Less),
            (-0.0, 0, Equal),
            (42.0, 42, Equal),
        ] {
            assert_eq!(
                compare_number_to_integer(number, bound),
                Some(expected),
                "{number:?}, {bound}"
            );
        }
        assert_eq!(compare_number_to_integer(f64::NAN, 0), None);
    }

    #[test]
    fn schema_range_rejects_rounded_bounds_and_nonfinite_values() {
        use terrabase::families::{SchemaDefinition, SchemaRule, SchemaVersion, ViolationKind};
        use terrabase::types::DocumentValue;

        let schema = SchemaDefinition::try_new(
            SchemaVersion::try_new(1).unwrap(),
            vec![SchemaRule::NumberRange {
                field: "amount".into(),
                min: Some(9_007_199_254_740_993),
                max: Some(i64::MAX),
            }],
        )
        .unwrap();
        for number in
            [9_007_199_254_740_992.0, 9_223_372_036_854_775_808.0, f64::NAN, f64::INFINITY]
        {
            let value = DocumentValue::Object(std::collections::BTreeMap::from([(
                "amount".into(),
                DocumentValue::Number(number),
            )]));
            assert_eq!(
                super::schema_violations(&schema, &value),
                vec![("amount".into(), ViolationKind::NumberOutOfRange),]
            );
        }
    }
}
