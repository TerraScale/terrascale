//! Frozen native transaction identity, canonical effect digest, and resolution.

use crate::client::SharedClient;
use crate::discovery::NativeRequirement;
use crate::documents::{validate_context, DatabaseScope, Event, Observed, ProtocolVersion};
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};
use crate::values::{
    invalid, CollectionId, DecimalU64, Digest, DocumentId, Fields, Hex, Incarnation, ReplicaId,
    Value,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};

pub const MAX_WRITES: usize = 1024;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Writer {
    pub replica: ReplicaId,
    pub incarnation: Incarnation,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "mode", rename_all = "snake_case", deny_unknown_fields)]
pub enum Authority {
    Leaderless {},
    SingleWriter {
        generation: DecimalU64,
        writer: Writer,
    },
}

impl Authority {
    pub fn validate(&self) -> Result<()> {
        if matches!(
            self,
            Self::SingleWriter {
                generation: DecimalU64(0),
                ..
            }
        ) {
            return Err(invalid("Authority generation must be positive."));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Condition {
    None {},
    Absent {},
    Version { version: Digest },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum WriteValue {
    Live { fields: Fields },
    Tombstone {},
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Write {
    pub collection: CollectionId,
    pub document: DocumentId,
    pub schema_generation: DecimalU64,
    pub schema_digest: Digest,
    pub observed: Vec<Observed>,
    pub condition: Condition,
    pub value: WriteValue,
}

impl Write {
    pub fn validate(&self, authority: &Authority) -> Result<()> {
        if self.schema_generation.0 == 0 {
            return Err(invalid("Schema generation must be positive."));
        }
        if matches!(authority, Authority::SingleWriter { .. })
            && matches!(self.condition, Condition::None {})
        {
            return Err(invalid("Unconditional writes require leaderless mode."));
        }
        validate_context(&self.observed)?;
        if let WriteValue::Live { fields } = &self.value {
            fields.validate()?;
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionId {
    pub expires_at_unix_ms: DecimalU64,
    pub nonce: Hex<16>,
}

impl TransactionId {
    /// Supply an explicit expiry and fresh nonce. Retry reuses this entire identity.
    #[must_use]
    pub fn new(expires_at_unix_ms: u64) -> Self {
        Self {
            expires_at_unix_ms: DecimalU64(expires_at_unix_ms),
            nonce: Hex::from_bytes(*uuid::Uuid::now_v7().as_bytes()),
        }
    }
}

#[derive(Serialize)]
struct CommitBody<'a> {
    version: ProtocolVersion,
    transaction_id: &'a TransactionId,
    authority: &'a Authority,
    effect_digest: Digest,
    writes: &'a [Write],
}

/// An immutable operation. Exact retries reuse its identity and exact bytes.
#[derive(Clone)]
pub struct PreparedTransaction {
    scope: DatabaseScope,
    transaction_id: TransactionId,
    authority: Authority,
    effect_digest: Digest,
    body: Vec<u8>,
    requirement: NativeRequirement,
}

impl std::fmt::Debug for PreparedTransaction {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PreparedTransaction")
            .field("scope", &self.scope)
            .field("transaction_id", &self.transaction_id)
            .field("authority", &self.authority)
            .field("effect_digest", &self.effect_digest)
            .field("body_bytes", &self.body.len())
            .finish()
    }
}

impl PreparedTransaction {
    pub fn new(
        scope: DatabaseScope,
        transaction_id: TransactionId,
        authority: Authority,
        mut writes: Vec<Write>,
    ) -> Result<Self> {
        // A region hint or caller-supplied Writer is not a durable signed admission.
        // Keep security writes unavailable until the producer/Atlas fence is admitted.
        if matches!(authority, Authority::SingleWriter { .. }) {
            return Err(Error::new(
                ErrorKind::Unavailable,
                "single_writer_not_admitted",
                "Signed durable single-writer admission and commit fencing are not yet qualified.",
            ));
        }
        let requirement = NativeRequirement::Commit {
            conditional: writes
                .iter()
                .any(|write| !matches!(write.condition, Condition::None {})),
            atomic: writes.len() > 1,
        };
        writes.sort_by_key(|w| (w.collection, w.document));
        let effect_digest = canonical_effect_digest(&scope, &authority, &writes)?;
        let request = Request::json(
            "native.commit",
            Method::Post,
            String::new(),
            &CommitBody {
                version: ProtocolVersion,
                transaction_id: &transaction_id,
                authority: &authority,
                effect_digest,
                writes: &writes,
            },
        )?;
        Ok(Self {
            scope,
            transaction_id,
            authority,
            effect_digest,
            body: request.body.unwrap_or_default(),
            requirement,
        })
    }
    #[must_use]
    pub fn scope(&self) -> &DatabaseScope {
        &self.scope
    }
    #[must_use]
    pub fn transaction_id(&self) -> &TransactionId {
        &self.transaction_id
    }
    #[must_use]
    pub fn authority(&self) -> &Authority {
        &self.authority
    }
    #[must_use]
    pub fn effect_digest(&self) -> Digest {
        self.effect_digest
    }
    #[must_use]
    pub fn body(&self) -> &[u8] {
        &self.body
    }
}

/// Source-backed TB-DOC-EFFECT-v1 grammar. The transaction ID is excluded.
pub fn canonical_effect_digest(
    scope: &DatabaseScope,
    authority: &Authority,
    writes: &[Write],
) -> Result<Digest> {
    authority.validate()?;
    if writes.is_empty() || writes.len() > MAX_WRITES {
        return Err(invalid("A transaction needs 1..1024 writes."));
    }
    let mut hash = Sha256::new();
    hash.update(b"TB-DOC-EFFECT-v1\0");
    hash.update(scope.tenant.bytes());
    hash.update(scope.database.bytes());
    hash.update(scope.shard.0.to_le_bytes());
    match authority {
        Authority::Leaderless {} => hash.update([0]),
        Authority::SingleWriter { generation, writer } => {
            hash.update([1]);
            hash.update(generation.0.to_le_bytes());
            hash.update(writer.replica.bytes());
            hash.update(writer.incarnation.bytes());
        }
    }
    hash.update((writes.len() as u64).to_le_bytes());
    let mut previous = None;
    for write in writes {
        write.validate(authority)?;
        let key = (write.collection, write.document);
        if previous.is_some_and(|p| p >= key) {
            return Err(invalid(
                "Writes must be sorted with no duplicate document binding.",
            ));
        }
        previous = Some(key);
        hash.update(write.collection.bytes());
        hash.update(write.document.bytes());
        hash.update(write.schema_generation.0.to_le_bytes());
        hash.update(write.schema_digest.bytes());
        match &write.condition {
            Condition::None {} => hash.update([0]),
            Condition::Absent {} => hash.update([1]),
            Condition::Version { version } => {
                hash.update([2]);
                hash.update(version.bytes());
            }
        }
        hash.update((write.observed.len() as u64).to_le_bytes());
        for observed in &write.observed {
            hash.update(observed.replica.bytes());
            hash.update(observed.incarnation.bytes());
            hash.update(observed.sequence.0.to_le_bytes());
        }
        match &write.value {
            WriteValue::Tombstone {} => hash.update([0]),
            WriteValue::Live { fields } => {
                hash.update([1]);
                hash.update((fields.len() as u64).to_le_bytes());
                for field in fields.iter() {
                    let (tag, bytes) = native_value(&field.value)?;
                    hash.update(field.id.to_le_bytes());
                    hash.update([tag]);
                    hash.update((bytes.len() as u64).to_le_bytes());
                    hash.update(bytes);
                }
            }
        }
    }
    Ok(Digest::from_bytes(hash.finalize().into()))
}

fn native_value(value: &Value) -> Result<(u8, Vec<u8>)> {
    Ok(match value {
        Value::Null => (1, vec![]),
        Value::Bool(value) => (2, vec![u8::from(*value)]),
        Value::I64(value) => (3, value.to_le_bytes().to_vec()),
        Value::Utf8(value) => (4, value.as_bytes().to_vec()),
        Value::Bytes(value) => (5, value.clone()),
        Value::Map(fields) => {
            let mut bytes = (fields.len() as u16).to_le_bytes().to_vec();
            for field in fields.iter() {
                bytes.extend_from_slice(&field.id.to_le_bytes());
                encode_child(&mut bytes, &field.value)?;
            }
            (6, bytes)
        }
        Value::Array(items) => {
            let mut bytes = (items.len() as u16).to_le_bytes().to_vec();
            for item in items {
                encode_child(&mut bytes, item)?;
            }
            (7, bytes)
        }
    })
}

fn encode_child(container: &mut Vec<u8>, value: &Value) -> Result<()> {
    let (tag, bytes) = native_value(value)?;
    let length =
        u32::try_from(bytes.len()).map_err(|_| invalid("Native child value length overflow."))?;
    container.push(tag);
    container.extend_from_slice(&length.to_le_bytes());
    container.extend_from_slice(&bytes);
    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Durability {
    LocalDurable,
    ObjectDurable,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Receipt {
    pub version: ProtocolVersion,
    pub transaction_id: TransactionId,
    pub authority: Authority,
    pub effect_digest: Digest,
    pub event: Event,
    pub commit_sequence: DecimalU64,
    pub durability: Durability,
}

impl Receipt {
    fn validate_for(&self, operation: &PreparedTransaction) -> Result<()> {
        if self.transaction_id != operation.transaction_id
            || self.authority != operation.authority
            || self.effect_digest != operation.effect_digest
            || self.commit_sequence.0 == 0
        {
            return Err(Error::invalid_response(
                "Receipt does not bind the exact prepared operation.",
            ));
        }
        self.event
            .validate_in(&operation.scope)
            .map_err(|e| Error::invalid_response(e.message))
    }
}

#[derive(Clone, Debug)]
pub struct UncertainCommit {
    pub operation: PreparedTransaction,
    pub cause: Error,
}

#[derive(Clone, Debug)]
pub enum CommitOutcome {
    Committed(Receipt),
    Uncertain(UncertainCommit),
}

#[derive(Serialize)]
struct StatusRequest<'a> {
    version: ProtocolVersion,
    transaction_id: &'a TransactionId,
    effect_digest: Digest,
    authority: &'a Authority,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TransactionState {
    Committed,
    Rejected,
    Pending,
    Unknown,
    Expired,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Rejection {
    pub code: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum StatusOutcome {
    Committed(CommittedStatus),
    Rejected(RejectedStatus),
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CommittedStatus {
    pub receipt: Receipt,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RejectedStatus {
    pub error: Rejection,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TransactionStatus {
    pub version: ProtocolVersion,
    pub state: TransactionState,
    pub transaction_id: TransactionId,
    pub effect_digest: Digest,
    pub authority: Authority,
    #[serde(deserialize_with = "required_outcome")]
    pub outcome: Option<StatusOutcome>,
}

fn required_outcome<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<StatusOutcome>, D::Error> {
    Option::<StatusOutcome>::deserialize(d)
}

impl TransactionStatus {
    fn validate_for(&self, operation: &PreparedTransaction) -> Result<()> {
        if self.transaction_id != operation.transaction_id
            || self.authority != operation.authority
            || self.effect_digest != operation.effect_digest
        {
            return Err(Error::invalid_response(
                "Status does not bind the original operation.",
            ));
        }
        match (&self.state, &self.outcome) {
            (TransactionState::Committed, Some(StatusOutcome::Committed(outcome))) => {
                outcome.receipt.validate_for(operation)
            }
            (TransactionState::Rejected, Some(StatusOutcome::Rejected(outcome)))
                if outcome.error.code == "precondition_failed" =>
            {
                Ok(())
            }
            (
                TransactionState::Pending | TransactionState::Unknown | TransactionState::Expired,
                None,
            ) => Ok(()),
            _ => Err(Error::invalid_response(
                "Transaction status and outcome disagree.",
            )),
        }
    }
    /// Only a retained rejection proves this operation did not commit.
    #[must_use]
    pub fn definitively_rejected(&self) -> bool {
        self.state == TransactionState::Rejected
    }
}

#[derive(Debug, Clone)]
pub struct TransactionsClient {
    shared: SharedClient,
}

impl TransactionsClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn commit(&self, operation: &PreparedTransaction) -> Result<CommitOutcome> {
        let origin = self
            .shared
            .native_origin(&operation.scope, operation.requirement)
            .await?;
        let request = Request {
            route_name: "native.commit".into(),
            method: Method::Post,
            url: format!("{origin}{}/transactions", operation.scope.path()),
            headers: vec![("content-type".into(), "application/json".into())],
            body: Some(operation.body.clone()),
        };
        let result: Result<Receipt> = self
            .shared
            .send_native_typed(&operation.scope, operation.requirement, request)
            .await;
        let result = result.and_then(|receipt| {
            receipt.validate_for(operation)?;
            Ok(receipt)
        });
        match result {
            Ok(receipt) => Ok(CommitOutcome::Committed(receipt)),
            Err(cause)
                if matches!(
                    cause.kind,
                    ErrorKind::Transport
                        | ErrorKind::InvalidResponse
                        | ErrorKind::Unavailable
                        | ErrorKind::Server
                ) || cause.code == "commit_uncertain"
                    || cause.status.is_some_and(|status| {
                        !matches!(status, 400 | 401 | 403 | 404 | 409 | 412)
                    }) =>
            {
                Ok(CommitOutcome::Uncertain(UncertainCommit {
                    operation: operation.clone(),
                    cause,
                }))
            }
            Err(error) => Err(error),
        }
    }

    /// Resolve the original operation; unknown, pending and expired are not aborts.
    pub async fn status(&self, operation: &PreparedTransaction) -> Result<TransactionStatus> {
        let origin = self
            .shared
            .native_origin(&operation.scope, NativeRequirement::TransactionStatus)
            .await?;
        let request = Request::json(
            "native.transaction_status",
            Method::Post,
            format!("{origin}{}/transactions/status", operation.scope.path()),
            &StatusRequest {
                version: ProtocolVersion,
                transaction_id: &operation.transaction_id,
                effect_digest: operation.effect_digest,
                authority: &operation.authority,
            },
        )?;
        let status: TransactionStatus = self
            .shared
            .send_native_typed(
                &operation.scope,
                NativeRequirement::TransactionStatus,
                request,
            )
            .await?;
        status.validate_for(operation)?;
        Ok(status)
    }
}
