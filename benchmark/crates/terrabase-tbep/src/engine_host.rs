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
//! naming their landing. Served: V1-compatible queries, collection
//! schemas, acknowledgement and read levels with positions, topology's
//! honest standalone answer, query cancellation, and the Storage Tier
//! Mover's typed administration.

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

/// How many recently finished query identities the host remembers for
/// `already_complete` cancel answers — bounded, drop-oldest.
const COMPLETED_QUERY_HORIZON: usize = 256;

/// Maps one tier administration failure onto the wire's typed refusal.
fn tier_error(error: terrabase_v2::tiers::TierAdminError) -> HostingError {
    match error {
        terrabase_v2::tiers::TierAdminError::InvalidPolicy => {
            HostingError::Invalid("the tier policy is not valid".into())
        }
        terrabase_v2::tiers::TierAdminError::NotConfigured => {
            HostingError::Refused("no tier policy is configured".into())
        }
        terrabase_v2::tiers::TierAdminError::Paused => {
            HostingError::Refused("tier runs are paused".into())
        }
        terrabase_v2::tiers::TierAdminError::UnsupportedBackend => {
            HostingError::Refused("this backend carries no tiers".into())
        }
        terrabase_v2::tiers::TierAdminError::MoveFailed(receipt) => {
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

/// Maps one engine tier receipt onto the wire's.
fn wire_receipt(
    receipt: &terrabase_v2::tiers::TierMoveRunReceipt,
) -> crate::generated::TierMoveReceipt {
    crate::generated::TierMoveReceipt {
        policy_id: receipt.policy_id().to_owned(),
        policy_version: receipt.policy_version(),
        decision: match receipt.decision() {
            terrabase_v2::tiers::TierDecision::Promote => crate::generated::TierDecision::Promote,
            terrabase_v2::tiers::TierDecision::Demote => crate::generated::TierDecision::Demote,
            terrabase_v2::tiers::TierDecision::Split => crate::generated::TierDecision::Split,
            terrabase_v2::tiers::TierDecision::None => crate::generated::TierDecision::None,
            terrabase_v2::tiers::TierDecision::Refused => crate::generated::TierDecision::Refused,
        },
        reasons: receipt
            .reasons()
            .iter()
            .map(|reason| crate::generated::TierMoveReason {
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
    instance: u64,
    reads: Mutex<u64>,
    frames_served: Mutex<u64>,
    live_queries: Mutex<std::collections::HashMap<u64, terrabase_v2::types::Cancellation>>,
    completed_queries: Mutex<std::collections::VecDeque<u64>>,
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
            live_queries: Mutex::new(std::collections::HashMap::new()),
            completed_queries: Mutex::new(std::collections::VecDeque::new()),
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

    /// Registers one live query's cancellation signal under its id.
    fn register_live_query(&self, query_id: u64, cancellation: &terrabase_v2::types::Cancellation) {
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        live.insert(query_id, cancellation.clone());
    }

    /// Retires one query: the id moves to the bounded completed set a
    /// cancel can still name.
    fn retire_live_query(&self, query_id: u64) {
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        live.remove(&query_id);
        let mut completed =
            self.completed_queries.lock().expect("the completed set is not poisoned");
        completed.push_back(query_id);
        while completed.len() > COMPLETED_QUERY_HORIZON {
            completed.pop_front();
        }
    }

    /// Reports whether a finished query is still inside the horizon.
    fn query_completed(&self, query_id: u64) -> bool {
        let completed = self.completed_queries.lock().expect("the completed set is not poisoned");
        completed.contains(&query_id)
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
        operation: &str,
        stage: impl FnOnce(&mut terrabase_v2::engine::Transaction<'_>) -> Result<(), HostingError>,
        ack: crate::generated::Acknowledgement,
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
                // One standalone member IS its region's quorum: the
                // local durable commit satisfies both levels.
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
            request.ack.unwrap_or(crate::generated::Acknowledgement::RegionalQuorum),
            idempotency_key,
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
            request.ack.unwrap_or(crate::generated::Acknowledgement::RegionalQuorum),
            idempotency_key,
        )?;
        Ok(DocumentDeleteResponse { outcome })
    }

    fn document_batch(
        &self,
        tenant: &str,
        request: &crate::generated::DocumentBatchRequest,
        idempotency_key: &str,
    ) -> Result<crate::generated::DocumentBatchResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        if request.items.is_empty() {
            return Err(HostingError::Invalid("the batch carries no items".into()));
        }
        if request.items.len() > crate::generated::MAX_LIST_ITEMS {
            return Err(HostingError::Invalid("the batch exceeds the list bound".into()));
        }
        // One batch, one engine transaction: every item stages before the
        // single commit, so the batch is atomic — a conflicting item
        // rolls the whole frame back and nothing durable records.
        let outcome = self.commit(
            &tenant,
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
            request.ack.unwrap_or(crate::generated::Acknowledgement::RegionalQuorum),
            idempotency_key,
        )?;
        Ok(crate::generated::DocumentBatchResponse { outcome })
    }

    fn query_execute(
        &self,
        tenant: &str,
        request: &crate::generated::QueryExecuteRequest,
    ) -> Result<crate::generated::QueryExecuteResponse, HostingError> {
        self.count_frame();
        let tenant = Self::tenant_of(tenant)?;
        // The wire's text is bounded TQL input, capped at
        // 16 KiB by the registry's own validation. The query runs with
        // its own cancellation signal — registered under the caller's
        // query id — so a cancel from any channel ends it at a bounded
        // checkpoint.
        let text = terrabase_v2::operations::TqlQueryText::try_new(&request.text)
            .map_err(|_| HostingError::Invalid("the query text is not valid".into()))?;
        let cancellation = terrabase_v2::types::Cancellation::new();
        self.register_live_query(request.query_id, &cancellation);
        let deadline = terrabase_v2::types::Deadline::after(std::time::Duration::from_secs(30))
            .map_err(|_| HostingError::EngineFailure("the deadline is invalid".into()))?;
        let input = terrabase_v2::operations::QueryInput::tql_bounded(text, deadline, cancellation);
        let scope = self.engine.scope_for(&tenant);
        let executed = (|| -> Result<
            (
                Result<terrabase_v2::query::QueryResult, terrabase_v2::query::QueryError>,
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
        self.retire_live_query(request.query_id);
        let (executed, position) = executed?;
        let result = match executed {
            Ok(result) => result,
            Err(terrabase_v2::query::QueryError::Cancelled) => {
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
            crate::generated::QueryTerminal::Truncated
        } else {
            crate::generated::QueryTerminal::Complete
        };
        Ok(crate::generated::QueryExecuteResponse {
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
        _tenant: &str,
        request: &crate::generated::QueryCancelRequest,
    ) -> Result<crate::generated::QueryCancelResponse, HostingError> {
        self.count_frame();
        // A live query cancels at its next bounded checkpoint; a
        // finished one reports completion; anything else is unknown.
        let mut live = self.live_queries.lock().expect("the live set is not poisoned");
        if let Some(cancellation) = live.remove(&request.query_id) {
            cancellation.cancel();
            return Ok(crate::generated::QueryCancelResponse {
                outcome: crate::generated::CancelOutcome::Cancelled,
            });
        }
        let outcome = if self.query_completed(request.query_id) {
            crate::generated::CancelOutcome::AlreadyComplete
        } else {
            crate::generated::CancelOutcome::UnknownQuery
        };
        Ok(crate::generated::QueryCancelResponse { outcome })
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

    fn tier_configure(
        &self,
        _tenant: &str,
        request: &crate::generated::TierConfigureRequest,
        _idempotency_key: &str,
    ) -> Result<crate::generated::TierConfigureResponse, HostingError> {
        self.count_frame();
        let policy = terrabase_v2::tiers::TierPolicy::try_new(
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
        Ok(crate::generated::TierConfigureResponse {
            policy_id: policy.policy_id().to_owned(),
            version: policy.version(),
            // configure is fresh control: never paused afterwards.
            paused: false,
        })
    }

    fn tier_inspect(
        &self,
        _tenant: &str,
    ) -> Result<crate::generated::TierInspectResponse, HostingError> {
        self.count_frame();
        let view = self.engine.tiers().inspect();
        Ok(crate::generated::TierInspectResponse {
            configured: view.configured(),
            paused: view.paused(),
            manifest_epoch: view.manifest_epoch(),
            object_count: u32::try_from(view.object_count()).unwrap_or(u32::MAX),
            nvme_bytes: view.nvme_bytes(),
        })
    }

    fn tier_explain(
        &self,
        _tenant: &str,
    ) -> Result<crate::generated::TierExplainResponse, HostingError> {
        self.count_frame();
        let receipt = self.engine.tiers().dry_run().map_err(tier_error)?;
        Ok(crate::generated::TierExplainResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_dry_run(
        &self,
        _tenant: &str,
    ) -> Result<crate::generated::TierDryRunResponse, HostingError> {
        self.count_frame();
        let receipt = self.engine.tiers().dry_run().map_err(tier_error)?;
        Ok(crate::generated::TierDryRunResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_run(
        &self,
        _tenant: &str,
        _idempotency_key: &str,
    ) -> Result<crate::generated::TierRunResponse, HostingError> {
        self.count_frame();
        let receipt = self.engine.tiers().run().map_err(tier_error)?;
        Ok(crate::generated::TierRunResponse { receipt: wire_receipt(&receipt) })
    }

    fn tier_pause(
        &self,
        _tenant: &str,
        _idempotency_key: &str,
    ) -> Result<crate::generated::TierPauseResponse, HostingError> {
        self.count_frame();
        Ok(crate::generated::TierPauseResponse { paused: self.engine.tiers().pause() })
    }

    fn tier_resume(
        &self,
        _tenant: &str,
        _idempotency_key: &str,
    ) -> Result<crate::generated::TierResumeResponse, HostingError> {
        self.count_frame();
        Ok(crate::generated::TierResumeResponse { paused: self.engine.tiers().resume() })
    }

    fn topology_read(
        &self,
        _tenant: &str,
    ) -> Result<crate::generated::TopologyReadResponse, HostingError> {
        self.count_frame();
        // Honest standalone answer: this member serves every subsystem
        // it hosts over one local binding; no cluster topology exists
        // until the cluster-mode landing admits peers.
        Ok(crate::generated::TopologyReadResponse {
            binding: crate::generated::BindingKind::LocalUds,
            subsystems: vec![
                "documents".into(),
                "query".into(),
                "schema".into(),
                "supervision".into(),
                "topology".into(),
            ],
            engine_topology_available: false,
            cluster_mode: Some(crate::generated::ClusterMode::Standalone),
        })
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
