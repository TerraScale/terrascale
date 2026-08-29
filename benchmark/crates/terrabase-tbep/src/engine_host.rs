//! The engine-backed TBEP host (P4): the real durable Engine serving
//! the protocol's document operations.
//!
//! [`EngineHost`] implements [`TbepHost`] over one `terrabase-v2`
//! `Engine` at a durable location: channel binds validate the tenant
//! identifier, document writes commit at `Synced` through the engine's
//! transaction path with the envelope's idempotency key as the
//! operation identity, and document reads serve the published body
//! with its version. This is the serving spine the C# client exercises
//! — `SyncDeliveryHost` remains the sync-session specialist beside it.
//!
//! Honest limits of this landing: the sync-session family
//! (backbone/sessions) and the family surfaces answer the typed refusal
//! naming their landing; the tier-administration and cluster-mode
//! operations land with their phases. The query and schema surfaces are
//! served: V1-compatible query text compiles and executes against the
//! bound tenant's snapshot, and collection schemas declare, validate,
//! and enforce through the recorded policy.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use terrabase_v2::engine::{CommitOutcome, Engine, EngineConfig};
use terrabase_v2::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};

use crate::codec::TbepValue;
use crate::generated::{
    AdminStatusResponse, ChannelBindRequest, ChannelBindResponse, CommitReceipt, DocumentBody,
    DocumentDeleteRequest, DocumentDeleteResponse, DocumentPut, DocumentReadRequest,
    DocumentReadResponse, DocumentWriteRequest, DocumentWriteResponse, ExpectedVersion,
    HealthState, REGISTRY_VERSION, SupervisionHealthResponse, SupervisionShutdownRequest,
    SupervisionShutdownResponse, WriteOutcome,
};
use crate::host::{HostingError, TbepHost};

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

/// The TBEP host backed by one durable Engine.
pub struct EngineHost {
    engine: Engine,
    location: PathBuf,
    instance: u64,
    reads: Mutex<u64>,
    frames_served: Mutex<u64>,
}

impl EngineHost {
    /// Opens the host's engine at a durable location, creating the
    /// directory when absent.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::EngineFailure`] when the location or
    /// engine cannot open.
    pub fn open(location: &Path, instance: u64) -> Result<Self, HostingError> {
        let engine = Engine::open(
            EngineConfig::durable(location)
                .map_err(|_| HostingError::EngineFailure("location refused".into()))?,
        )
        .map_err(|_| HostingError::EngineFailure("engine open refused".into()))?;
        Ok(Self {
            engine,
            location: location.to_path_buf(),
            instance,
            reads: Mutex::new(0),
            frames_served: Mutex::new(0),
        })
    }

    /// Returns the durable location the host's engine serves.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
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
    ) -> Result<Option<terrabase_v2::families::SchemaDefinition>, HostingError> {
        let read = terrabase_v2::families::ValidationRead::try_new(
            collection.clone(),
            None,
            terrabase_v2::types::Deadline::after(std::time::Duration::from_secs(30))
                .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?,
            terrabase_v2::types::Cancellation::default(),
        )
        .map_err(|_| HostingError::Invalid("the validation read is not valid".into()))?;
        let scope = self.engine.scope_for(tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let context = terrabase_v2::families::FamilyReadContext::new(scope, snapshot);
        let request = terrabase_v2::families::FamilyReadRequest::validation(read);
        match self
            .engine
            .families()
            .read(context, request)
            .map_err(|_| HostingError::EngineFailure("the validation read failed".into()))?
        {
            terrabase_v2::families::FamilyReadResult::Validation(result) => {
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
    ) -> Result<Option<Document>, HostingError> {
        let operation = self.next_read_operation()?;
        let scope = self.engine.scope_for(tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let transaction = self
            .engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| HostingError::EngineFailure("the read could not begin".into()))?;
        let found = transaction
            .read(document)
            .map_err(|_| HostingError::EngineFailure("the read failed".into()))?;
        drop(transaction);
        Ok(found)
    }

    /// Commits one mutation through the engine's transaction path and
    /// maps the outcome to the wire's write outcome.
    fn commit(
        &self,
        tenant: &TenantId,
        operation: &str,
        stage: impl FnOnce(&mut terrabase_v2::engine::Transaction<'_>) -> Result<(), HostingError>,
        idempotency_key: &str,
    ) -> Result<WriteOutcome, HostingError> {
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
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(WriteOutcome::Committed(CommitReceipt {
                sequence: receipt.sequence().get(),
                // Replaced by the published version at the call site.
                version: 0,
                operation_id: receipt.operation_id().as_str().to_owned(),
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
        // The tenant identifier validates now: an invalid bind never
        // reaches an engine scope.
        Self::tenant_of(&request.tenant)?;
        Ok(ChannelBindResponse { tenant: request.tenant.clone(), serving_instance: self.instance })
    }

    fn document_read(
        &self,
        tenant: &str,
        request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let document = DocumentId::try_new(request.document.as_str())
            .map_err(|_| HostingError::Invalid("the document identifier is not valid".into()))?;
        let found = self.read_body(&tenant, &document)?;
        let document = found.map(|found| DocumentBody {
            id: found.id().as_str().to_owned(),
            collection: found.collection().as_str().to_owned(),
            version: found.metadata().version(),
            body: to_wire_value(found.value()),
        });
        Ok(DocumentReadResponse { found: document.is_some(), document })
    }

    fn document_write(
        &self,
        tenant: &str,
        request: &DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        let DocumentPut { id, collection, body } = &request.document;
        let (document, collection) = Self::identifiers_of(id, collection)?;
        let expected = to_engine_expected(&request.expected);
        let value = to_engine_value(body);
        let outcome = self.commit(
            &tenant,
            &request.operation_id,
            |transaction| {
                transaction
                    .put(
                        Document::new(document.clone(), collection.clone(), value.clone()),
                        expected,
                    )
                    .map_err(|_| HostingError::Refused("the document was not admissible".into()))
            },
            idempotency_key,
        )?;
        // The wire's receipt carries the document's published version —
        // the engine's own receipt carries the storage sequence.
        let outcome = match outcome {
            WriteOutcome::Committed(mut receipt) => {
                let version = self
                    .read_body(&tenant, &document)?
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
        tenant: &str,
        request: &DocumentDeleteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentDeleteResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        let document = DocumentId::try_new(request.document.as_str())
            .map_err(|_| HostingError::Invalid("the document identifier is not valid".into()))?;
        CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let expected = to_engine_expected(&request.expected);
        let outcome = self.commit(
            &tenant,
            &request.operation_id,
            |transaction| {
                transaction
                    .delete(document.clone(), expected)
                    .map_err(|_| HostingError::Refused("the document was not admissible".into()))
            },
            idempotency_key,
        )?;
        Ok(DocumentDeleteResponse { outcome })
    }

    fn query_execute(
        &self,
        tenant: &str,
        request: &crate::generated::QueryExecuteRequest,
    ) -> Result<crate::generated::QueryExecuteResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        // The wire's text is the V1-compatible query input, bounded at
        // 16 KiB by the registry's own validation.
        let text = terrabase_v2::operations::V1QueryText::try_new(&request.text)
            .map_err(|_| HostingError::Invalid("the query text is not valid".into()))?;
        let input = terrabase_v2::operations::QueryInput::v1_compatible(text);
        let scope = self.engine.scope_for(&tenant);
        let snapshot = self
            .engine
            .read_snapshot(&scope)
            .map_err(|_| HostingError::EngineFailure("the snapshot is unavailable".into()))?;
        let store = self.engine.queries();
        let plan = store
            .compile(input)
            .map_err(|_| HostingError::Invalid("the query did not compile".into()))?;
        let result = store
            .execute(&plan, &snapshot)
            .map_err(|_| HostingError::EngineFailure("the query execution failed".into()))?;
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
            crate::generated::QueryTerminal::Truncated
        } else {
            crate::generated::QueryTerminal::Complete
        };
        Ok(crate::generated::QueryExecuteResponse {
            count: u32::try_from(result.matched()).unwrap_or(u32::MAX),
            documents,
            terminal,
        })
    }

    fn query_cancel(
        &self,
        _tenant: &str,
        _request: &crate::generated::QueryCancelRequest,
    ) -> Result<crate::generated::QueryCancelResponse, HostingError> {
        Err(HostingError::Refused("query_cancel is served by the query-plane landing".into()))
    }

    fn family_read(
        &self,
        _tenant: &str,
        _request: &crate::generated::FamilyReadRequest,
    ) -> Result<crate::generated::FamilyReadResponse, HostingError> {
        Err(HostingError::Refused("family_read is served by the family-plane landing".into()))
    }

    fn family_write(
        &self,
        _tenant: &str,
        _request: &crate::generated::FamilyWriteRequest,
        _idempotency_key: &str,
    ) -> Result<crate::generated::FamilyWriteResponse, HostingError> {
        Err(HostingError::Refused("family_write is served by the family-plane landing".into()))
    }

    fn backbone_subscribe(
        &self,
        _tenant: &str,
        _request: &crate::generated::BackboneSubscribeRequest,
    ) -> Result<
        (
            crate::generated::BackboneSubscribeResponse,
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
        _tenant: &str,
        _request: &crate::generated::BackboneAcknowledgeRequest,
    ) -> Result<crate::generated::BackboneAcknowledgeResponse, HostingError> {
        Err(HostingError::Refused(
            "backbone delivery is served by the sync-delivery host beside this one".into(),
        ))
    }

    fn backbone_unsubscribe(
        &self,
        _tenant: &str,
        _request: &crate::generated::BackboneUnsubscribeRequest,
    ) -> Result<crate::generated::BackboneUnsubscribeResponse, HostingError> {
        Err(HostingError::Refused(
            "backbone delivery is served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_session_setup(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncSessionSetupRequest,
    ) -> Result<crate::generated::SyncSessionSetupResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_delta_pull(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncDeltaPullRequest,
    ) -> Result<crate::generated::SyncDeltaPullResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_snapshot_pull(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncSnapshotPullRequest,
    ) -> Result<crate::generated::SyncSnapshotPullResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_checkpoint_ack(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncCheckpointAckRequest,
    ) -> Result<crate::generated::SyncCheckpointAckResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_mutation_submit(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncMutationSubmitRequest,
        _idempotency_key: &str,
    ) -> Result<crate::generated::SyncMutationSubmitResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn sync_session_close(
        &self,
        _tenant: &str,
        _request: &crate::generated::SyncSessionCloseRequest,
    ) -> Result<crate::generated::SyncSessionCloseResponse, HostingError> {
        Err(HostingError::Refused(
            "sync sessions are served by the sync-delivery host beside this one".into(),
        ))
    }

    fn topology_read(
        &self,
        _tenant: &str,
    ) -> Result<crate::generated::TopologyReadResponse, HostingError> {
        Err(HostingError::Refused(
            "topology_read is served by the cluster-mode landing (P5)".into(),
        ))
    }

    fn schema_declare(
        &self,
        tenant: &str,
        request: &crate::generated::SchemaDeclareRequest,
        idempotency_key: &str,
    ) -> Result<crate::generated::SchemaDeclareResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        // The declared schema value adopts the collection's NEXT version:
        // version evolution is forward-only in the engine, so the host
        // derives it from the recorded policy.
        let current = self.validation_policy(&tenant, &collection)?;
        let next_version = terrabase_v2::families::SchemaVersion::try_new(
            current.as_ref().map_or(1, |policy| policy.version().get() + 1),
        )
        .map_err(|_| HostingError::Invalid("the derived schema version is not valid".into()))?;
        let schema = match &request.schema {
            None => None,
            Some(value) => Some(
                terrabase_v2::families::SchemaDefinition::from_json_schema(
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
        let write = terrabase_v2::families::ValidationWrite::try_new(
            self.engine.scope_for(&tenant),
            collection.clone(),
            schema,
            operation,
            terrabase_v2::types::Deadline::after(std::time::Duration::from_secs(30))
                .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?,
            terrabase_v2::types::Cancellation::default(),
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
                terrabase_v2::families::FamilyWriteRequest::schema_validation_update(write),
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
        Ok(crate::generated::SchemaDeclareResponse {
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
        tenant: &str,
        request: &crate::generated::SchemaValidateRequest,
    ) -> Result<crate::generated::SchemaValidateResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let Some(policy) = self.validation_policy(&tenant, &collection)? else {
            // Schema-free collections carry no rule set: any body is
            // valid, and the empty version says why.
            return Ok(crate::generated::SchemaValidateResponse {
                enforced_version: None,
                valid: true,
                violations: Vec::new(),
            });
        };
        let body = to_engine_value(&request.document);
        let violations = terrabase_v2::families::violations(&policy, &body)
            .into_iter()
            .map(|violation| crate::generated::SchemaViolation {
                field: violation.field().to_owned(),
                kind: match violation.kind() {
                    terrabase_v2::families::ViolationKind::DocumentNotObject => {
                        crate::generated::ViolationKind::DocumentNotObject
                    }
                    terrabase_v2::families::ViolationKind::MissingRequiredField => {
                        crate::generated::ViolationKind::MissingRequiredField
                    }
                    terrabase_v2::families::ViolationKind::TypeMismatch => {
                        crate::generated::ViolationKind::TypeMismatch
                    }
                    terrabase_v2::families::ViolationKind::ValueNotAllowed => {
                        crate::generated::ViolationKind::ValueNotAllowed
                    }
                    terrabase_v2::families::ViolationKind::NumberOutOfRange => {
                        crate::generated::ViolationKind::NumberOutOfRange
                    }
                    terrabase_v2::families::ViolationKind::LengthOutOfRange => {
                        crate::generated::ViolationKind::LengthOutOfRange
                    }
                },
            })
            .collect::<Vec<_>>();
        let enforced_version = Some(policy.version().get());
        let valid = violations.is_empty();
        Ok(crate::generated::SchemaValidateResponse { enforced_version, valid, violations })
    }

    fn schema_status(
        &self,
        tenant: &str,
        request: &crate::generated::SchemaStatusRequest,
    ) -> Result<crate::generated::SchemaStatusResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        let collection = CollectionName::try_new(request.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection identifier is not valid".into()))?;
        let policy = self.validation_policy(&tenant, &collection)?;
        Ok(crate::generated::SchemaStatusResponse {
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
        Ok(SupervisionHealthResponse {
            state: HealthState::Serving,
            sessions: 0,
            subscriptions: 0,
            frames_served: frames,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError> {
        Ok(SupervisionShutdownResponse { state: HealthState::Draining, in_flight: 0 })
    }
}
