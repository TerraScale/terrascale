//! The engine-backed TBEP host (P4): the real durable Engine serving
//! the protocol's document operations.
//!
//! [`EngineHost`] implements [`TbepHost`] over one `terrabase`
//! `Engine` at a durable location: channel binds validate the tenant
//! identifier, document writes commit at `Synced` through the engine's
//! transaction path with the RPC's idempotency key as the
//! operation identity, and document reads serve the published body
//! with its version. This is the serving spine `terrabase-go` exercises;
//! `SyncDeliveryHost` remains the sync-session specialist beside it.
//!
//! The host also serves queries, collection schemas, acknowledgement and read
//! levels with positions, topology, query cancellation, and Storage Tier Mover
//! administration. Unsupported operations fail with typed errors.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::Mutex;

use terrabase::engine::{CommitOutcome, Engine, EngineConfig};
use terrabase::observability::MetricsRegistry;
use terrabase::regions::RegionCode;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase::usage::{
    MeasurementWindow, MeterKind, MintingRule as EngineMintingRule,
    UsageAdmissionContext as EngineUsageAdmission, UsageCommandError, UsageContinuation,
    UsageFactId, UsagePageRequest, UsageRecordMeter as EngineUsageRecordMeter,
    UsageRecordRequest as EngineUsageRecordRequest,
};

use crate::host::{HostingError, TbepHost};
use crate::registry::{
    AdminStatusResponse, BoundScope, ChannelBindRequest, ChannelBindResponse, CommitReceipt,
    DocumentBody, DocumentDeleteRequest, DocumentDeleteResponse, DocumentPut, DocumentReadRequest,
    DocumentReadResponse, DocumentWriteRequest, DocumentWriteResponse, ExpectedVersion,
    HealthState, REGISTRY_VERSION, SupervisionHealthResponse, SupervisionShutdownRequest,
    SupervisionShutdownResponse, WriteOutcome,
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

/// Converts one engine value into the wire's value model.
fn to_wire_value(value: &DocumentValue) -> TbepValue {
    match value {
        DocumentValue::Null => TbepValue::Null,
        DocumentValue::Boolean(flag) => TbepValue::Boolean(*flag),
        DocumentValue::Integer(number) => TbepValue::Integer(*number),
        DocumentValue::Number(number) => TbepValue::Number(*number),
        DocumentValue::String(text) => TbepValue::String(text.clone()),
        DocumentValue::Array(items) => TbepValue::Array(items.iter().map(to_wire_value).collect()),
        DocumentValue::Object(fields) => TbepValue::Object(
            fields.iter().map(|(name, value)| (name.clone(), to_wire_value(value))).collect(),
        ),
    }
}

/// The expected-version wire form into the engine's.
fn to_engine_expected(expected: &ExpectedVersion) -> EngineVersion {
    match expected {
        ExpectedVersion::Absent => EngineVersion::New,
        ExpectedVersion::Any => EngineVersion::Any,
        ExpectedVersion::Exact(version) => EngineVersion::Exact(*version),
    }
}

const fn engine_region(region: crate::registry::UsageRegion) -> RegionCode {
    match region {
        crate::registry::UsageRegion::Gru => RegionCode::GRU,
        crate::registry::UsageRegion::Sfo => RegionCode::SFO,
        crate::registry::UsageRegion::Fra => RegionCode::FRA,
    }
}

fn wire_region(region: &RegionCode) -> Result<crate::registry::UsageRegion, HostingError> {
    match *region {
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
        UsageCommandError::UnsupportedBackend
        | UsageCommandError::UsageUnavailable
        | UsageCommandError::StorageUnavailable => {
            HostingError::UsageUnavailable(error.to_string())
        }
        UsageCommandError::CorruptStorage => HostingError::UsageCorrupt(error.to_string()),
    }
}

fn engine_usage_admission(
    value: &crate::registry::UsageAdmissionContext,
) -> Result<EngineUsageAdmission, HostingError> {
    let request_id = UsageFactId::try_new(&value.request_id)
        .map_err(|_| HostingError::Invalid("the usage request identity is invalid".into()))?;
    let integer_units = value
        .integer_units
        .parse::<i128>()
        .map_err(|_| HostingError::Invalid("the usage units are invalid".into()))?;
    EngineUsageAdmission::try_new(
        &value.project,
        &value.database,
        &value.partition,
        engine_region(value.origin_region),
        request_id,
        value.accepted_at_millis,
        value.meter_version,
        integer_units,
    )
    .map_err(|_| HostingError::Invalid("the usage admission is invalid".into()))
}

fn wire_usage_fact(
    durable: &terrabase::usage::DurableUsageFact,
) -> Result<crate::registry::UsageFact, HostingError> {
    let fact = durable.fact();
    let (meter, unit, charge_scope, minting_rule) = match (fact.meter(), fact.identity().rule()) {
        (MeterKind::AdmittedRequestUnits, EngineMintingRule::StableAdmittedRequest) => (
            crate::registry::UsageMeter::AdmittedRequestUnits,
            crate::registry::UsageUnit::RequestUnit,
            crate::registry::UsageChargeScope::AdmittedRequest,
            crate::registry::UsageMintingRule::StableAdmittedRequest,
        ),
        (MeterKind::PublicEgressBytes, EngineMintingRule::PublicResponseBytes) => (
            crate::registry::UsageMeter::PublicEgressBytes,
            crate::registry::UsageUnit::Byte,
            crate::registry::UsageChargeScope::PublicEgress,
            crate::registry::UsageMintingRule::PublicResponse,
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
        region: wire_region(scope.region_id())?,
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

/// How many recently finished query identities the host remembers for
/// `already_complete` cancel answers — bounded, drop-oldest.
const COMPLETED_QUERY_HORIZON: usize = 256;

/// Maps one tier administration failure onto the wire's typed refusal.
fn tier_error(error: terrabase::tiers::TierAdminError) -> HostingError {
    match error {
        terrabase::tiers::TierAdminError::InvalidPolicy => {
            HostingError::Invalid("the tier policy is not valid".into())
        }
        terrabase::tiers::TierAdminError::NotConfigured => {
            HostingError::Refused("no tier policy is configured".into())
        }
        terrabase::tiers::TierAdminError::Paused => {
            HostingError::Refused("tier runs are paused".into())
        }
        terrabase::tiers::TierAdminError::UnsupportedBackend => {
            HostingError::Refused("this backend carries no tiers".into())
        }
        terrabase::tiers::TierAdminError::MoveFailed(receipt) => {
            HostingError::EngineFailure(format!(
                "the mover run failed at segment {} (policy {}, manifest {}->{}, decision {:?})",
                receipt.segment_id(),
                receipt.policy_id(),
                receipt.manifest_epoch_before(),
                receipt.manifest_epoch_after(),
                receipt.decision()
            ))
        }
    }
}

/// The standalone engine cannot honestly satisfy any replicated write
/// acknowledgement exposed by TBEP.  Keep this refusal centralized so a
/// caller cannot accidentally turn a local sync into a quorum claim.
fn standalone_ack_refusal(ack: crate::registry::Acknowledgement) -> Result<(), HostingError> {
    let requested = match ack {
        crate::registry::Acknowledgement::RegionalQuorum => "regional_quorum",
        crate::registry::Acknowledgement::GlobalQuorum => "global_quorum",
        crate::registry::Acknowledgement::BackupAck => "backup_ack",
    };
    Err(HostingError::Refused(format!(
        "{requested} acknowledgement is unavailable on the standalone engine host; the protocol needs a local-synced acknowledgement or replication host"
    )))
}

/// Maps one engine tier receipt onto the wire's.
fn wire_receipt(
    receipt: &terrabase::tiers::TierMoveRunReceipt,
) -> crate::registry::TierMoveReceipt {
    crate::registry::TierMoveReceipt {
        policy_id: receipt.policy_id().to_owned(),
        policy_version: receipt.policy_version(),
        decision: match receipt.decision() {
            terrabase::tiers::TierDecision::Promote => crate::registry::TierDecision::Promote,
            terrabase::tiers::TierDecision::Demote => crate::registry::TierDecision::Demote,
            terrabase::tiers::TierDecision::Split => crate::registry::TierDecision::Split,
            terrabase::tiers::TierDecision::None => crate::registry::TierDecision::None,
            terrabase::tiers::TierDecision::Refused => crate::registry::TierDecision::Refused,
        },
        reasons: receipt
            .reasons()
            .iter()
            .map(|reason| crate::registry::TierMoveReason {
                fact: reason.fact().to_owned(),
                observed: reason.observed().to_owned(),
            })
            .collect(),
        segment_id: receipt.segment_id(),
        bytes: receipt.bytes(),
        manifest_before: receipt.manifest_epoch_before(),
        manifest_after: receipt.manifest_epoch_after(),
        dry_run: receipt.dry_run(),
        manifest_accepted: receipt.manifest_accepted(),
    }
}

/// The TBEP host backed by one durable Engine.
pub struct EngineHost {
    engine: Engine,
    location: PathBuf,
    tenant: String,
    project: String,
    database: String,
    instance: u64,
    reads: Mutex<u64>,
    frames_served: Mutex<u64>,
    live_queries: Mutex<std::collections::HashMap<(String, u64), terrabase::types::Cancellation>>,
    completed_queries: Mutex<std::collections::VecDeque<(String, u64)>>,
    health: Mutex<HealthState>,
}

impl EngineHost {
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
            engine,
            location: location.to_path_buf(),
            tenant: tenant.to_owned(),
            project: project.to_owned(),
            database: database.to_owned(),
            instance,
            reads: Mutex::new(0),
            frames_served: Mutex::new(0),
            live_queries: Mutex::new(std::collections::HashMap::new()),
            completed_queries: Mutex::new(std::collections::VecDeque::new()),
            health: Mutex::new(HealthState::Serving),
        })
    }

    /// Returns the durable location the host's engine serves.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
    }

    /// Returns the engine-owned metrics registry shared by storage and every
    /// serving adapter for this location.
    #[must_use]
    pub fn metrics(&self) -> Arc<MetricsRegistry> {
        self.engine.metrics()
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

    fn next_read_operation(&self) -> Result<OperationId, HostingError> {
        let mut reads = self.reads.lock().expect("the read counter is not poisoned");
        *reads += 1;
        OperationId::try_new(&format!("tbep-read-{}", *reads))
            .map_err(|_| HostingError::Invalid("the read identity is not usable".into()))
    }

    fn count_frame(&self) {
        let mut frames = self.frames_served.lock().expect("the frame counter is not poisoned");
        *frames += 1;
    }

    /// Registers one live query's cancellation signal under its id.
    fn register_live_query(
        &self,
        tenant: &str,
        query_id: u64,
        cancellation: &terrabase::types::Cancellation,
    ) -> Result<(), HostingError> {
        // Admission and registration share the health-to-live lock order so
        // shutdown cannot race a query between its admission check and
        // insertion into the cancellation set.
        let health = self.health.lock().expect("the health state is not poisoned");
        if *health != HealthState::Serving {
            return Err(HostingError::Refused("the host is draining".into()));
        }
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        if live.contains_key(&(tenant.to_owned(), query_id)) {
            return Err(HostingError::Refused("the query id is already active".into()));
        }
        live.insert((tenant.to_owned(), query_id), cancellation.clone());
        Ok(())
    }

    /// Retires one query: the id moves to the bounded completed set a
    /// cancel can still name.
    fn retire_live_query(&self, tenant: &str, query_id: u64) {
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        live.remove(&(tenant.to_owned(), query_id));
        let mut completed =
            self.completed_queries.lock().expect("the completed set is not poisoned");
        completed.push_back((tenant.to_owned(), query_id));
        while completed.len() > COMPLETED_QUERY_HORIZON {
            completed.pop_front();
        }
    }

    /// Reports whether a finished query is still inside the horizon.
    fn query_completed(&self, tenant: &str, query_id: u64) -> bool {
        let completed = self.completed_queries.lock().expect("the completed set is not poisoned");
        completed.contains(&(tenant.to_owned(), query_id))
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

    fn identifiers_of(
        id: &str,
        collection: &str,
    ) -> Result<(DocumentId, CollectionName), HostingError> {
        let document = DocumentId::try_new(id)
            .map_err(|_| HostingError::Invalid("the document identifier is not valid".into()))?;
        let collection = CollectionName::try_new(collection)
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        Ok((document, collection))
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

    /// Reads one document through the engine's snapshot path.
    fn read_body(
        &self,
        tenant: &TenantId,
        document: &DocumentId,
    ) -> Result<(Option<Document>, u64), HostingError> {
        let operation = self.next_read_operation()?;
        let scope = self.engine.scope_for(tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let position = snapshot.sequence().get();
        let transaction = self
            .engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| HostingError::EngineFailure("the read could not begin".into()))?;
        let found = transaction
            .read(document)
            .map_err(|_| HostingError::EngineFailure("the read failed".into()))?;
        drop(transaction);
        Ok((found, position))
    }

    /// Commits one mutation through the engine's transaction path and
    /// maps the outcome to the wire's write outcome.
    fn commit(
        &self,
        tenant: &TenantId,
        bound_scope: &BoundScope,
        operation: &str,
        stage: impl FnOnce(&mut terrabase::engine::Transaction<'_>) -> Result<(), HostingError>,
        ack: crate::registry::Acknowledgement,
        idempotency_key: &str,
        usage: Option<&crate::registry::UsageAdmissionContext>,
    ) -> Result<WriteOutcome, HostingError> {
        // This host owns one local Engine only. Its local `Synced` commit
        // cannot prove the protocol's RF=2 regional quorum, a quorum across
        // two regions, or a backup acknowledgement. The registry has no
        // local-synced acknowledgement variant, so reject before opening a
        // transaction rather than returning a false durability claim.
        standalone_ack_refusal(ack)?;

        // The envelope's idempotency key is the operation identity:
        // a retried frame replays the recorded receipt.
        let identity = if idempotency_key.is_empty() { operation } else { idempotency_key };
        let operation_id = OperationId::try_new(identity)
            .map_err(|_| HostingError::Invalid("the operation identity is not valid".into()))?;
        let scope = self.engine.scope_for(tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let mut transaction = self
            .engine
            .begin(&scope, snapshot, operation_id.clone())
            .map_err(|_| HostingError::EngineFailure("the write could not begin".into()))?;
        if let Err(error) = stage(&mut transaction) {
            drop(transaction);
            return Err(error);
        }
        if let Some(usage) = usage {
            if usage.project != bound_scope.project || usage.database != bound_scope.database {
                return Err(HostingError::Invalid(
                    "the usage admission does not match the bound Project and Database".into(),
                ));
            }
            transaction
                .attach_usage_admission(engine_usage_admission(usage)?)
                .map_err(usage_error)?;
        }
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(WriteOutcome::Committed(CommitReceipt {
                sequence: receipt.sequence().get(),
                // Replaced by the published version at the call site.
                version: 0,
                operation_id: receipt.operation_id().as_str().to_owned(),
                // Unreachable on this standalone host because the
                // acknowledgement guard above fails closed; a replicated
                // host must construct its receipt from observed quorum state.
                acknowledgement: Some(ack),
                backup: None,
            })),
            CommitOutcome::Rejected(_) => Ok(WriteOutcome::Conflict),
            // An uncertain commit resolves by identity on retry; the
            // wire's refusal names it.
            CommitOutcome::Unknown(_) => Err(HostingError::EngineFailure(
                "the commit is uncertain; retry the same idempotency key".into(),
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

    fn document_read(
        &self,
        scope: &BoundScope,
        request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let document = DocumentId::try_new(request.document.as_str())
            .map_err(|_| HostingError::Invalid("the document identifier is not valid".into()))?;
        let (found, position) = self.read_body(&tenant, &document)?;
        let document = found.map(|found| DocumentBody {
            id: found.id().as_str().to_owned(),
            collection: found.collection().as_str().to_owned(),
            version: found.metadata().version(),
            body: to_wire_value(found.value()),
        });
        // Every read level observes this member's state directly: the
        // snapshot's sequence is the replication position the client
        // measures lag against.
        Ok(DocumentReadResponse { found: document.is_some(), document, position: Some(position) })
    }

    fn document_write(
        &self,
        scope: &BoundScope,
        request: &DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        let DocumentPut { id, collection, body } = &request.document;
        let (document, collection) = Self::identifiers_of(id, collection)?;
        let expected = to_engine_expected(&request.expected);
        let value = to_engine_value(body);
        let outcome = self.commit(
            &tenant,
            scope,
            &request.operation_id,
            |transaction| {
                transaction
                    .put(
                        Document::new(document.clone(), collection.clone(), value.clone()),
                        expected,
                    )
                    .map_err(|_| HostingError::Refused("the document was not admissible".into()))
            },
            request.ack.unwrap_or(crate::registry::Acknowledgement::RegionalQuorum),
            idempotency_key,
            request.usage.as_ref(),
        )?;
        // The wire's receipt carries the document's published version —
        // the engine's own receipt carries the storage sequence.
        let outcome = match outcome {
            WriteOutcome::Committed(mut receipt) => {
                let version = self
                    .read_body(&tenant, &document)?
                    .0
                    .map_or(1, |found| found.metadata().version());
                receipt.version = version;
                WriteOutcome::Committed(receipt)
            }
            other => other,
        };
        Ok(DocumentWriteResponse { outcome })
    }

    fn document_delete(
        &self,
        scope: &BoundScope,
        request: &DocumentDeleteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentDeleteResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        let document = DocumentId::try_new(request.document.as_str())
            .map_err(|_| HostingError::Invalid("the document identifier is not valid".into()))?;
        CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let expected = to_engine_expected(&request.expected);
        // The version a delete receipt names is the version the document
        // had when it was removed. It is read inside the very transaction
        // that stages the delete, so a concurrent writer cannot move it
        // between the read and the tombstone.
        let mut deleted_version = 0_u64;
        let outcome = self.commit(
            &tenant,
            scope,
            &request.operation_id,
            |transaction| {
                let found = transaction
                    .read(&document)
                    .map_err(|_| HostingError::EngineFailure("the read failed".into()))?;
                deleted_version = found.as_ref().map_or(1, |found| found.metadata().version());
                transaction
                    .delete(document.clone(), expected)
                    .map_err(|_| HostingError::Refused("the document was not admissible".into()))
            },
            request.ack.unwrap_or(crate::registry::Acknowledgement::RegionalQuorum),
            idempotency_key,
            request.usage.as_ref(),
        )?;
        // Same rule as the write path: the wire's receipt carries the
        // document's version, the engine's own receipt carries the
        // storage sequence. A replayed delete whose document is already
        // gone reuses the write path's fallback (version 1); the storage
        // sequence and operation identity stay exact either way.
        let outcome = match outcome {
            WriteOutcome::Committed(mut receipt) => {
                receipt.version = deleted_version;
                WriteOutcome::Committed(receipt)
            }
            other => other,
        };
        Ok(DocumentDeleteResponse { outcome })
    }

    fn document_batch(
        &self,
        scope: &BoundScope,
        request: &crate::registry::DocumentBatchRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::DocumentBatchResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(scope)?;
        if request.items.is_empty() {
            return Err(HostingError::Invalid("the batch carries no items".into()));
        }
        if request.items.len() > crate::registry::MAX_LIST_ITEMS {
            return Err(HostingError::Invalid("the batch exceeds the list bound".into()));
        }
        // One batch, one engine transaction: every item stages before the
        // single commit, so the batch is atomic — a conflicting item
        // rolls the whole frame back and nothing durable records.
        let outcome = self.commit(
            &tenant,
            scope,
            idempotency_key,
            |transaction| {
                for item in &request.items {
                    let (document, collection) = Self::identifiers_of(
                        item.document.id.as_str(),
                        item.document.collection.as_str(),
                    )?;
                    transaction
                        .put(
                            Document::new(
                                document,
                                collection,
                                to_engine_value(&item.document.body),
                            ),
                            to_engine_expected(&item.expected),
                        )
                        .map_err(|_| {
                            HostingError::Refused("a batch item was not admissible".into())
                        })?;
                }
                Ok(())
            },
            request.ack.unwrap_or(crate::registry::Acknowledgement::RegionalQuorum),
            idempotency_key,
            request.usage.as_ref(),
        )?;
        Ok(crate::registry::DocumentBatchResponse { outcome })
    }

    fn query_execute(
        &self,
        bound_scope: &BoundScope,
        request: &crate::registry::QueryExecuteRequest,
    ) -> Result<crate::registry::QueryExecuteResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        let tenant = self.tenant_of_scope(bound_scope)?;
        // The wire's text is bounded TQL input, capped at
        // 16 KiB by the registry's own validation. The query runs with
        // its own cancellation signal — registered under the caller's
        // query id — so a cancel from any channel ends it at a bounded
        // checkpoint.
        let text = terrabase::operations::TqlQueryText::try_new(&request.text)
            .map_err(|_| HostingError::Invalid("the query text is not valid".into()))?;
        let cancellation = terrabase::types::Cancellation::new();
        let deadline = terrabase::types::Deadline::after(std::time::Duration::from_secs(30))
            .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?;
        self.register_live_query(tenant.as_str(), request.query_id, &cancellation)?;
        let input = terrabase::operations::QueryInput::tql_bounded(text, deadline, cancellation);
        let scope = self.engine.scope_for(&tenant);
        let executed = (|| -> Result<
            (
                Result<terrabase::query::QueryResult, terrabase::query::QueryError>,
                u64,
            ),
            HostingError,
        > {
            let snapshot = self
                .engine
                .read_snapshot(&scope)
                .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
            let position = snapshot.sequence().get();
            let store = self.engine.queries();
            let plan = store
                .compile(input)
                .map_err(|_| HostingError::Invalid("the query did not compile".into()))?;
            Ok((store.execute(&plan, &snapshot), position))
        })();
        // Every completion path retires the query from the live set.
        self.retire_live_query(tenant.as_str(), request.query_id);
        let (executed, position) = executed?;
        let result = match executed {
            Ok(result) => result,
            Err(terrabase::query::QueryError::Cancelled) => {
                return Err(HostingError::Refused("the query was cancelled".into()));
            }
            Err(_) => return Err(HostingError::EngineFailure("the query execution failed".into())),
        };
        // The response serves at most the caller's page (bounded by the
        // registry's list bound); `count` reports the query's total
        // matched rows, `terminal` whether the page cut them.
        let rows = result.rows();
        let limit = usize::try_from(request.max_items).unwrap_or(0).min(rows.len());
        let documents: Vec<DocumentBody> = rows[..limit]
            .iter()
            .map(|row| {
                let document = row.document();
                DocumentBody {
                    id: document.id().as_str().to_owned(),
                    collection: document.collection().as_str().to_owned(),
                    version: document.metadata().version(),
                    body: to_wire_value(document.value()),
                }
            })
            .collect();
        let terminal = if limit < rows.len() {
            crate::registry::QueryTerminal::Truncated
        } else {
            crate::registry::QueryTerminal::Complete
        };
        Ok(crate::registry::QueryExecuteResponse {
            count: u32::try_from(result.matched()).unwrap_or(u32::MAX),
            documents,
            terminal,
            // The snapshot the query observed — the read position every
            // level shares on this member.
            position: Some(position),
        })
    }

    fn query_cancel(
        &self,
        scope: &BoundScope,
        request: &crate::registry::QueryCancelRequest,
    ) -> Result<crate::registry::QueryCancelResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        // A live query cancels at its next bounded checkpoint; a
        // finished one reports completion; anything else is unknown.
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        if let Some(cancellation) = live.remove(&(scope.tenant.clone(), request.query_id)) {
            cancellation.cancel();
            return Ok(crate::registry::QueryCancelResponse {
                outcome: crate::registry::CancelOutcome::Cancelled,
            });
        }
        let outcome = if self.query_completed(&scope.tenant, request.query_id) {
            crate::registry::CancelOutcome::AlreadyComplete
        } else {
            crate::registry::CancelOutcome::UnknownQuery
        };
        Ok(crate::registry::QueryCancelResponse { outcome })
    }

    fn family_read(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::FamilyReadRequest,
    ) -> Result<crate::registry::FamilyReadResponse, HostingError> {
        Err(HostingError::Refused("family_read is served by the family-plane landing".into()))
    }

    fn family_write(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::FamilyWriteRequest,
        _idempotency_key: &str,
    ) -> Result<crate::registry::FamilyWriteResponse, HostingError> {
        Err(HostingError::Refused("family_write is served by the family-plane landing".into()))
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

    fn sync_session_setup(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncSessionSetupRequest,
    ) -> Result<crate::registry::SyncSessionSetupResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_delta_pull(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncDeltaPullRequest,
    ) -> Result<crate::registry::SyncDeltaPullResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_snapshot_pull(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncSnapshotPullRequest,
    ) -> Result<crate::registry::SyncSnapshotPullResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_checkpoint_ack(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncCheckpointAckRequest,
    ) -> Result<crate::registry::SyncCheckpointAckResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_mutation_submit(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncMutationSubmitRequest,
        _idempotency_key: &str,
    ) -> Result<crate::registry::SyncMutationSubmitResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_session_close(
        &self,
        _scope: &BoundScope,
        _request: &crate::registry::SyncSessionCloseRequest,
    ) -> Result<crate::registry::SyncSessionCloseResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn tier_configure(
        &self,
        scope: &BoundScope,
        request: &crate::registry::TierConfigureRequest,
        _idempotency_key: &str,
    ) -> Result<crate::registry::TierConfigureResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        let policy = terrabase::tiers::TierPolicy::try_new(
            &request.policy_id,
            request.version,
            request.nvme_budget_bytes,
            request.min_residence_moves,
            request.promotion_threshold,
            request.pinned_segment_ids.clone(),
        )
        .map_err(|_| HostingError::Invalid("the tier policy is not valid".into()))?;
        let tiers = self.engine.tiers();
        let policy = tiers
            .configure(policy)
            .map_err(|_| HostingError::Invalid("the tier policy was refused".into()))?;
        Ok(crate::registry::TierConfigureResponse {
            policy_id: policy.policy_id().to_owned(),
            version: policy.version(),
            // configure is fresh control: never paused afterwards.
            paused: false,
        })
    }

    fn tier_inspect(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierInspectResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        let view = self.engine.tiers().inspect();
        Ok(crate::registry::TierInspectResponse {
            configured: view.configured(),
            paused: view.paused(),
            manifest_epoch: view.manifest_epoch(),
            object_count: u32::try_from(view.object_count()).unwrap_or(u32::MAX),
            nvme_bytes: view.nvme_bytes(),
        })
    }

    fn tier_explain(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierExplainResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        let receipt = self.engine.tiers().dry_run().map_err(tier_error)?;
        Ok(crate::registry::TierExplainResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_dry_run(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierDryRunResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        let receipt = self.engine.tiers().dry_run().map_err(tier_error)?;
        Ok(crate::registry::TierDryRunResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_run(
        &self,
        scope: &BoundScope,
        _idempotency_key: &str,
    ) -> Result<crate::registry::TierRunResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        let receipt = self.engine.tiers().run().map_err(tier_error)?;
        Ok(crate::registry::TierRunResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_pause(
        &self,
        scope: &BoundScope,
        _idempotency_key: &str,
    ) -> Result<crate::registry::TierPauseResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        Ok(crate::registry::TierPauseResponse { paused: self.engine.tiers().pause() })
    }

    fn tier_resume(
        &self,
        scope: &BoundScope,
        _idempotency_key: &str,
    ) -> Result<crate::registry::TierResumeResponse, HostingError> {
        self.count_frame();
        self.ensure_serving()?;
        self.admit_scope(scope)?;
        Ok(crate::registry::TierResumeResponse { paused: self.engine.tiers().resume() })
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
            crate::registry::UsageRecordMeter::AdmittedRequestUnits => {
                EngineUsageRecordMeter::AdmittedRequestUnits
            }
            crate::registry::UsageRecordMeter::PublicEgressBytes => {
                EngineUsageRecordMeter::PublicEgressBytes
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
        match transaction.commit(LocalDurability::Synced) {
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
        let violations = terrabase::families::violations(&policy, &body)
            .into_iter()
            .map(|violation| crate::registry::SchemaViolation {
                field: violation.field().to_owned(),
                kind: match violation.kind() {
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
        let in_flight = self.live_queries.lock().expect("the live set is not poisoned").len();
        Ok(SupervisionHealthResponse {
            state,
            sessions: u32::try_from(in_flight).unwrap_or(u32::MAX),
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
        let live = self.live_queries.lock().expect("the live set is not poisoned");
        for cancellation in live.values() {
            cancellation.cancel();
        }
        Ok(SupervisionShutdownResponse {
            state: *state,
            in_flight: u32::try_from(live.len()).unwrap_or(u32::MAX),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::standalone_ack_refusal;
    use crate::host::HostingError;
    use crate::registry::Acknowledgement;

    #[test]
    fn standalone_write_acknowledgements_fail_closed() {
        for ack in [
            Acknowledgement::RegionalQuorum,
            Acknowledgement::GlobalQuorum,
            Acknowledgement::BackupAck,
        ] {
            let error = standalone_ack_refusal(ack).expect_err("standalone ack must refuse");
            assert!(
                matches!(error, HostingError::Refused(detail) if detail.contains("unavailable on the standalone engine host")),
                "{error:?}"
            );
        }
    }
}
