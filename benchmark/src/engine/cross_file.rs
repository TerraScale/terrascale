//! Database-owned identity and visibility records for cross-file operations.

use std::collections::BTreeMap;

use crate::types::OperationId;
use crate::{
    engine::{CommitError, CommitOutcome, Engine, EngineError},
    types::{DocumentValue, LocalDurability, TenantScope},
};

const MAX_PARTICIPANTS: usize = 64;

/// Local database receipt for a cross-file decision. It carries no replica
/// acknowledgement or cross-region finality claim.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalCrossFileReceipt {
    operation: OperationId,
    sequence: u64,
    digest: [u8; 32],
    durability: LocalDurability,
    phase: CrossFileReceiptPhase,
}

/// Local coordinator phase represented by a receipt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CrossFileReceiptPhase {
    /// Coordinator prepared the decision without committing it.
    Prepare,
    /// Coordinator reached a committed decision.
    Decision,
}

impl LocalCrossFileReceipt {
    /// Logical operation identity.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }
    /// Local publication sequence.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Canonical coordinator record digest.
    #[must_use]
    pub const fn digest(&self) -> [u8; 32] {
        self.digest
    }
    /// Local durability reached by the Engine.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }
    /// Coordinator phase reached by this receipt.
    #[must_use]
    pub const fn phase(&self) -> CrossFileReceiptPhase {
        self.phase
    }
}

/// Immutable identity binding one cross-file operation to its database view.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CrossFileIdentity {
    /// Tenant scope owning the coordinator record.
    pub(crate) tenant: String,
    /// Durable database incarnation.
    pub(crate) incarnation: u128,
    /// Authority epoch used while preparing the operation.
    pub(crate) authority_epoch: u64,
    /// Route generation used while preparing the operation.
    pub(crate) route_generation: u64,
    /// Read frontier at which participants were selected.
    pub(crate) read_frontier: u64,
    /// Exact operation write-set digest.
    pub(crate) write_digest: [u8; 32],
    /// Exact participant name to snapshot sequence binding.
    pub(crate) participants: BTreeMap<String, u64>,
    /// Stable operation identity.
    pub(crate) operation: OperationId,
    /// Monotonic coordinator decision epoch.
    pub(crate) decision_epoch: u64,
}

impl CrossFileIdentity {
    /// Validates bounded identity fields and participant uniqueness.
    pub(crate) fn validate(&self) -> bool {
        !self.tenant.is_empty()
            && self.tenant.len() <= 255
            && self.incarnation != 0
            && self.authority_epoch != 0
            && self.route_generation != u64::MAX
            && self.read_frontier != u64::MAX
            && self.decision_epoch != 0
            && !self.participants.is_empty()
            && self.participants.len() <= MAX_PARTICIPANTS
            && self.participants.iter().all(|(name, sequence)| {
                !name.is_empty() && name.len() <= 255 && i64::try_from(*sequence).is_ok()
            })
    }
}

/// Durable coordinator decision. A committed decision alone is not visible.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CrossFileDecision {
    /// All participants may install the operation.
    Committed,
    /// No participant may install the operation.
    Aborted,
}

/// Durable coordinator record, retaining both identity and terminal state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CrossFileRecord {
    /// Immutable operation identity.
    pub(crate) identity: CrossFileIdentity,
    /// Current durable decision, when terminal.
    pub(crate) decision: Option<CrossFileDecision>,
}

/// One database-wide visible frontier after participant installation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct VisibleFrontier {
    /// Database version made visible to readers.
    pub(crate) version: u64,
    /// Exact participant snapshot sequence map installed for this version.
    pub(crate) participants: BTreeMap<String, u64>,
}

impl VisibleFrontier {
    /// Validates a bounded, nonzero visible frontier.
    pub(crate) fn validate(&self) -> bool {
        self.version != 0
            && !self.participants.is_empty()
            && self.participants.len() <= MAX_PARTICIPANTS
            && self.participants.iter().all(|(name, sequence)| {
                !name.is_empty() && name.len() <= 255 && *sequence != u64::MAX
            })
    }
}

fn key(operation: &OperationId) -> String {
    format!("control:cross-file/v1/{}", operation.as_str())
}
fn visible_key() -> &'static str {
    "control:cross-file-visible/v1"
}
fn maintenance_operation(
    identity: &CrossFileIdentity,
    phase: &str,
) -> Result<OperationId, EngineError> {
    let mut input = b"terrascale.cross-file.maintenance.v1".to_vec();
    input.extend_from_slice(identity.operation.as_str().as_bytes());
    input.extend_from_slice(phase.as_bytes());
    OperationId::try_new(&format!("cross-file-{phase}-{}", blake3::hash(&input).to_hex()))
        .map_err(|_| EngineError::InvalidDocument)
}

fn encode(record: &CrossFileRecord) -> Result<DocumentValue, EngineError> {
    encode_phase(record, "prepare")
}

fn encode_phase(record: &CrossFileRecord, phase: &str) -> Result<DocumentValue, EngineError> {
    let identity = &record.identity;
    if !identity.validate() {
        return Err(EngineError::InvalidDocument);
    }
    let participants: Vec<_> = identity
        .participants
        .iter()
        .map(|(name, sequence)| {
            Ok(DocumentValue::Object(BTreeMap::from([
                ("name".into(), DocumentValue::String(name.clone())),
                (
                    "sequence".into(),
                    DocumentValue::Integer(
                        i64::try_from(*sequence).map_err(|_| EngineError::InvalidDocument)?,
                    ),
                ),
            ])))
        })
        .collect::<Result<_, _>>()?;
    Ok(DocumentValue::Object(BTreeMap::from([
        ("tenant".into(), DocumentValue::String(identity.tenant.clone())),
        ("incarnation".into(), DocumentValue::String(format!("{:032x}", identity.incarnation))),
        (
            "authority_epoch".into(),
            DocumentValue::Integer(
                i64::try_from(identity.authority_epoch)
                    .map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        (
            "route_generation".into(),
            DocumentValue::Integer(
                i64::try_from(identity.route_generation)
                    .map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        (
            "read_frontier".into(),
            DocumentValue::Integer(
                i64::try_from(identity.read_frontier).map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        ("write_digest".into(), DocumentValue::String(hex::encode(identity.write_digest))),
        ("participants".into(), DocumentValue::Array(participants)),
        ("operation".into(), DocumentValue::String(identity.operation.as_str().into())),
        (
            "decision_epoch".into(),
            DocumentValue::Integer(
                i64::try_from(identity.decision_epoch).map_err(|_| EngineError::InvalidDocument)?,
            ),
        ),
        ("phase".into(), DocumentValue::String(phase.into())),
    ])))
}

fn integer(fields: &BTreeMap<String, DocumentValue>, name: &str) -> Option<u64> {
    match fields.get(name) {
        Some(DocumentValue::Integer(value)) if *value >= 0 => (*value).try_into().ok(),
        _ => None,
    }
}

fn decode(value: DocumentValue) -> Result<CrossFileRecord, EngineError> {
    const FIELDS: &[&str] = &[
        "tenant",
        "incarnation",
        "authority_epoch",
        "route_generation",
        "read_frontier",
        "write_digest",
        "participants",
        "operation",
        "decision_epoch",
        "phase",
    ];
    let DocumentValue::Object(fields) = value else {
        return Err(EngineError::InvalidDocument);
    };
    if fields.keys().any(|key| !FIELDS.contains(&key.as_str())) {
        return Err(EngineError::InvalidDocument);
    }
    if !matches!(fields.get("phase"), Some(DocumentValue::String(phase)) if phase == "prepare" || phase == "commit" || phase == "abort")
    {
        return Err(EngineError::InvalidDocument);
    }
    let Some(DocumentValue::String(incarnation)) = fields.get("incarnation") else {
        return Err(EngineError::InvalidDocument);
    };
    let Some(DocumentValue::String(tenant)) = fields.get("tenant") else {
        return Err(EngineError::InvalidDocument);
    };
    let incarnation =
        u128::from_str_radix(incarnation, 16).map_err(|_| EngineError::InvalidDocument)?;
    let Some(DocumentValue::String(digest)) = fields.get("write_digest") else {
        return Err(EngineError::InvalidDocument);
    };
    let digest: [u8; 32] = hex::decode(digest)
        .map_err(|_| EngineError::InvalidDocument)?
        .try_into()
        .map_err(|_| EngineError::InvalidDocument)?;
    let Some(DocumentValue::Array(items)) = fields.get("participants") else {
        return Err(EngineError::InvalidDocument);
    };
    let mut participants = BTreeMap::new();
    for item in items {
        let DocumentValue::Object(item) = item else {
            return Err(EngineError::InvalidDocument);
        };
        let Some(DocumentValue::String(name)) = item.get("name") else {
            return Err(EngineError::InvalidDocument);
        };
        let Some(sequence) = integer(item, "sequence") else {
            return Err(EngineError::InvalidDocument);
        };
        if participants.insert(name.clone(), sequence).is_some() {
            return Err(EngineError::InvalidDocument);
        }
    }
    let Some(DocumentValue::String(operation)) = fields.get("operation") else {
        return Err(EngineError::InvalidDocument);
    };
    let identity = CrossFileIdentity {
        tenant: tenant.clone(),
        incarnation,
        authority_epoch: integer(&fields, "authority_epoch").ok_or(EngineError::InvalidDocument)?,
        route_generation: integer(&fields, "route_generation")
            .ok_or(EngineError::InvalidDocument)?,
        read_frontier: integer(&fields, "read_frontier").ok_or(EngineError::InvalidDocument)?,
        write_digest: digest,
        participants,
        operation: OperationId::try_new(operation).map_err(|_| EngineError::InvalidDocument)?,
        decision_epoch: integer(&fields, "decision_epoch").ok_or(EngineError::InvalidDocument)?,
    };
    let decision = match fields.get("phase") {
        Some(DocumentValue::String(phase)) if phase == "prepare" => None,
        Some(DocumentValue::String(phase)) if phase == "commit" => {
            Some(CrossFileDecision::Committed)
        }
        Some(DocumentValue::String(phase)) if phase == "abort" => Some(CrossFileDecision::Aborted),
        _ => return Err(EngineError::InvalidDocument),
    };
    identity
        .validate()
        .then_some(CrossFileRecord { identity, decision })
        .ok_or(EngineError::InvalidDocument)
}

impl Engine {
    /// Advances the local DB-visible frontier after every named participant
    /// has reported its installed snapshot. This method performs no RF2 or
    /// Global finality claim; those proofs remain cluster-owned.
    pub(crate) fn advance_cross_file_visible(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        installed: BTreeMap<String, u64>,
    ) -> Result<VisibleFrontier, EngineError> {
        if !identity.validate()
            || identity.tenant != scope.tenant().as_str()
            || installed != identity.participants
        {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot(scope)?,
            maintenance_operation(identity, "visible")?,
        )?;
        let record = tx
            .read_private_metadata(&key(&identity.operation))?
            .ok_or(EngineError::Conflict)
            .and_then(decode)?;
        if record.identity != *identity || record.decision != Some(CrossFileDecision::Committed) {
            return Err(EngineError::Conflict);
        }
        let version = identity.decision_epoch;
        let frontier = VisibleFrontier { version, participants: installed };
        if !frontier.validate() {
            return Err(EngineError::InvalidDocument);
        }
        tx.internal_maintenance = true;
        let fields = frontier
            .participants
            .iter()
            .map(|(name, seq)| {
                Ok(DocumentValue::Object(BTreeMap::from([
                    (String::from("name"), DocumentValue::String(name.clone())),
                    (
                        String::from("sequence"),
                        DocumentValue::Integer(
                            i64::try_from(*seq).map_err(|_| EngineError::InvalidDocument)?,
                        ),
                    ),
                ])))
            })
            .collect::<Result<Vec<_>, EngineError>>()?;
        tx.put_private_metadata(
            visible_key(),
            DocumentValue::Object(BTreeMap::from([
                (
                    String::from("version"),
                    DocumentValue::Integer(
                        i64::try_from(version).map_err(|_| EngineError::InvalidDocument)?,
                    ),
                ),
                (String::from("participants"), DocumentValue::Array(fields)),
            ])),
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(frontier),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
    /// Reads a durable cross-file prepare record for one operation.
    pub(crate) fn read_cross_file_decision(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<CrossFileRecord>, EngineError> {
        let read_op = OperationId::try_new(&format!(
            "cross-file-read-{}",
            blake3::hash(operation.as_str().as_bytes()).to_hex()
        ))
        .map_err(|_| EngineError::InvalidDocument)?;
        let tx = self.begin(scope, self.read_snapshot(scope)?, read_op)?;
        tx.read_private_metadata(&key(operation))?.map(decode).transpose()
    }

    /// Creates an immutable prepare record with exact-identity replay.
    pub(crate) fn prepare_cross_file(
        &self,
        scope: &TenantScope,
        identity: CrossFileIdentity,
    ) -> Result<(CrossFileIdentity, LocalCrossFileReceipt), EngineError> {
        if !identity.validate() {
            return Err(EngineError::InvalidDocument);
        }
        if identity.tenant != scope.tenant().as_str() {
            return Err(EngineError::Conflict);
        }
        if identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes().to_owned()) {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot(scope)?,
            maintenance_operation(&identity, "prepare")?,
        )?;
        if let Some(existing) = tx.read_private_metadata(&key(&identity.operation))? {
            let existing = decode(existing)?;
            if existing.identity != identity {
                return Err(EngineError::Conflict);
            }
            let maintenance = maintenance_operation(&identity, "prepare")?;
            let receipt = match self.resolve_commit(scope, maintenance)? {
                super::DurableResolution::Committed(receipt) => LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Prepare,
                },
                super::DurableResolution::Pending => return Err(EngineError::StorageFailure),
                super::DurableResolution::Rejected(_) => return Err(EngineError::Conflict),
            };
            return Ok((identity, receipt));
        }
        tx.internal_maintenance = true;
        tx.put_private_metadata(
            &key(&identity.operation),
            encode(&CrossFileRecord { identity: identity.clone(), decision: None })?,
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok((
                identity.clone(),
                LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Prepare,
                },
            )),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }

    /// Durably decides a prepared operation exactly once.
    pub(crate) fn decide_cross_file(
        &self,
        scope: &TenantScope,
        identity: &CrossFileIdentity,
        decision: CrossFileDecision,
    ) -> Result<(CrossFileRecord, LocalCrossFileReceipt), EngineError> {
        if !identity.validate() {
            return Err(EngineError::InvalidDocument);
        }
        if identity.tenant != scope.tenant().as_str() {
            return Err(EngineError::Conflict);
        }
        if identity.incarnation != u128::from_be_bytes(self.database_id().as_bytes().to_owned()) {
            return Err(EngineError::Conflict);
        }
        let mut tx = self.begin(
            scope,
            self.read_snapshot(scope)?,
            maintenance_operation(identity, "decide")?,
        )?;
        let current =
            tx.read_private_metadata(&key(&identity.operation))?.ok_or(EngineError::Conflict)?;
        let decoded = decode(current.clone())?;
        if decoded.identity != *identity {
            return Err(EngineError::Conflict);
        }
        if let Some(existing) = decoded.decision {
            if existing != decision {
                return Err(EngineError::Conflict);
            }
            let maintenance = maintenance_operation(identity, "decide")?;
            let receipt = match self.resolve_commit(scope, maintenance)? {
                super::DurableResolution::Committed(receipt) => LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Decision,
                },
                super::DurableResolution::Pending => return Err(EngineError::StorageFailure),
                super::DurableResolution::Rejected(_) => return Err(EngineError::Conflict),
            };
            return Ok((decoded, receipt));
        }
        tx.internal_maintenance = true;
        let phase = match decision {
            CrossFileDecision::Committed => "commit",
            CrossFileDecision::Aborted => "abort",
        };
        tx.put_private_metadata(
            &key(&identity.operation),
            encode_phase(
                &CrossFileRecord { identity: identity.clone(), decision: Some(decision) },
                phase,
            )?,
        )?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok((
                CrossFileRecord { identity: identity.clone(), decision: Some(decision) },
                LocalCrossFileReceipt {
                    operation: receipt.operation_id().clone(),
                    sequence: receipt.sequence().get(),
                    digest: identity.write_digest,
                    durability: receipt.durability(),
                    phase: CrossFileReceiptPhase::Decision,
                },
            )),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(EngineError::Conflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(EngineError::StorageFailure)
            }
        }
    }
}
