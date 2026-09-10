//! Bounded private full-intent codec. Never an inbound API or allocation verifier.
use super::{
    CommitIntent, StagedWrite,
    participant::{
        ParticipantReplicationPublication, ParticipantWrite, decode_prepare_payload,
        encode_prepare_payload,
    },
};
use crate::types::{OperationId, StorageSequence, TenantId};
use serde::{Deserialize, Serialize};

const MAX_BYTES: usize = 64 * 1024 * 1024;
type Admission = (String, String, String, String, String, u64, u32, String);
type Conflict = (String, String, String, u64, String, bool, String, u64, String, String, String);

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Wire {
    generated_primary_file_identity: Option<String>,
    dependencies: Option<Vec<u8>>,
    billing: Option<(String, String, String, u64)>,
    version: u8,
    tenant: String,
    operation: String,
    digest: u64,
    snapshot: u64,
    durability: u8,
    origin_epoch: Option<u64>,
    uniform: Option<Vec<u8>>,
    admission: Option<Admission>,
    allocation: Option<Vec<u8>>,
    maintenance: bool,
    shard_copy: bool,
    fingerprint: Option<[u8; 32]>,
    hlc: u64,
    payload: Vec<u8>,
    ttl_guards: Vec<Option<(String, i64)>>,
    conflict: Option<Conflict>,
}

pub(super) fn encode(intent: &CommitIntent) -> Result<Vec<u8>, ()> {
    intent.validated_generated_identity()?;
    if intent.cross_file_control.is_some() {
        return Err(());
    }
    let writes = intent.writes.iter().map(encode_write).collect::<Result<Vec<_>, _>>()?;
    let replication = intent.replication_publication.as_ref().map(encode_replication);
    let wire = Wire {
        generated_primary_file_identity: intent.generated_primary_file_identity.clone(),
        dependencies: intent.cross_file_dependencies.clone(),
        billing: intent.cross_file_billing.as_ref().map(|b| {
            (
                b.operation.to_string(),
                b.metering_participant.clone(),
                b.participant.clone(),
                b.total_write_units,
            )
        }),
        version: 3,
        tenant: intent.tenant.to_string(),
        operation: intent.operation.to_string(),
        digest: intent.digest,
        snapshot: intent.snapshot_sequence.get(),
        durability: super::codec::durability_code(intent.durability),
        origin_epoch: intent.expected_origin_epoch,
        uniform: intent.expected_uniform_context.clone(),
        admission: intent.usage_admission.as_ref().map(|a| {
            (
                a.project_id().to_string(),
                a.database_id().to_string(),
                a.partition_id().to_string(),
                a.origin_region().to_string(),
                a.request_id().to_string(),
                a.accepted_at_millis(),
                a.meter_version(),
                a.integer_units().to_string(),
            )
        }),
        allocation: intent
            .budget_allocation
            .as_ref()
            .map(crate::usage::VerifiedAllocation::encode_prepared)
            .transpose()?,
        maintenance: intent.internal_maintenance,
        shard_copy: intent.shard_copy,
        fingerprint: intent.replication_fingerprint,
        hlc: intent.hlc,
        payload: encode_prepare_payload(
            &writes,
            &intent.metadata,
            &intent.schema_envelopes,
            replication.as_ref(),
        )?,
        ttl_guards: intent
            .writes
            .iter()
            .map(|w| match w {
                StagedWrite::Delete { ttl_guard, .. } => {
                    ttl_guard.as_ref().map(|g| (g.revision.clone(), g.expires_at))
                }
                _ => None,
            })
            .collect(),
        conflict: intent.merge_conflict.as_ref().map(|(c, id, x)| {
            (
                c.to_string(),
                id.to_string(),
                x.loser_commit_ulid.clone(),
                x.loser_hlc,
                x.loser_origin.clone(),
                x.loser_deleted,
                x.winner_commit_ulid.clone(),
                x.winner_hlc,
                x.winner_origin.clone(),
                x.policy.clone(),
                x.explanation.clone(),
            )
        }),
    };
    let bytes = serde_json::to_vec(&wire).map_err(|_| ())?;
    (bytes.len() <= MAX_BYTES).then_some(bytes).ok_or(())
}

fn encode_write(write: &StagedWrite) -> Result<ParticipantWrite, ()> {
    match write {
        StagedWrite::Delete { collection, id, expected, .. } => Ok(ParticipantWrite::Delete {
            collection: collection.clone(),
            id: id.clone(),
            expected: *expected,
        }),
        other => ParticipantWrite::from_staged(other).ok_or(()),
    }
}

fn encode_replication(
    publication: &super::intent::ReplicationPublicationIntent,
) -> ParticipantReplicationPublication {
    ParticipantReplicationPublication {
        origin: publication.origin.clone(),
        origin_sequence: publication.origin_sequence,
        hlc: publication.hlc,
        commit_ulid: publication.commit_ulid.clone(),
        context: publication.context.clone(),
        records: publication.records.clone(),
        private_payload: publication.private_payload.clone(),
        observations: publication.observations.clone(),
        schema_observations: publication.schema_observations.clone(),
        conflicts: publication.conflicts.clone(),
    }
}

fn decode_write(
    (write, guard): (ParticipantWrite, Option<(String, i64)>),
) -> Result<StagedWrite, ()> {
    if guard.is_some() && !matches!(write, ParticipantWrite::Delete { .. }) {
        return Err(());
    }
    Ok(match write {
        ParticipantWrite::Put { document, expected } => StagedWrite::Put { document, expected },
        ParticipantWrite::Delete { collection, id, expected } => StagedWrite::Delete {
            collection,
            id,
            expected,
            ttl_guard: guard
                .map(|(revision, expires_at)| super::intent::TtlGuard { revision, expires_at }),
        },
        ParticipantWrite::ReplicatedPut {
            document,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        } => StagedWrite::ReplicatedPut {
            document,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        },
        ParticipantWrite::ReplicatedDelete {
            collection,
            id,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        } => StagedWrite::ReplicatedDelete {
            collection,
            id,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        },
    })
}

pub(super) fn decode(bytes: &[u8]) -> Result<CommitIntent, ()> {
    if bytes.len() > MAX_BYTES {
        return Err(());
    }
    let w: Wire = serde_json::from_slice(bytes).map_err(|_| ())?;
    if w.version != 3 {
        return Err(());
    }
    let (writes, metadata, schema_envelopes, replication) = decode_prepare_payload(&w.payload)?;
    if writes.len() != w.ttl_guards.len() {
        return Err(());
    }
    let writes =
        writes.into_iter().zip(w.ttl_guards).map(decode_write).collect::<Result<Vec<_>, ()>>()?;
    let usage_admission = w.admission.map(decode_admission).transpose()?;
    let merge_conflict = w
        .conflict
        .map(
            |(
                c,
                id,
                loser_commit_ulid,
                loser_hlc,
                loser_origin,
                loser_deleted,
                winner_commit_ulid,
                winner_hlc,
                winner_origin,
                policy,
                explanation,
            )| {
                Ok::<_, ()>((
                    crate::types::CollectionName::try_new(&c).map_err(|_| ())?,
                    crate::types::DocumentId::try_new(&id).map_err(|_| ())?,
                    super::MergeConflictEntry {
                        loser_commit_ulid,
                        loser_hlc,
                        loser_origin,
                        loser_deleted,
                        winner_commit_ulid,
                        winner_hlc,
                        winner_origin,
                        policy,
                        explanation,
                    },
                ))
            },
        )
        .transpose()?;
    let intent = CommitIntent {
        generated_primary_file_identity: w.generated_primary_file_identity,
        cross_file_billing: w
            .billing
            .map(|(op, metering_participant, participant, total_write_units)| {
                Ok::<_, ()>(super::CrossFileBilling {
                    operation: OperationId::try_new(&op).map_err(|_| ())?,
                    metering_participant,
                    participant,
                    total_write_units,
                })
            })
            .transpose()?,
        cross_file_control: None,
        cross_file_dependencies: w.dependencies,
        tenant: TenantId::try_new(&w.tenant).map_err(|_| ())?,
        operation: OperationId::try_new(&w.operation).map_err(|_| ())?,
        digest: w.digest,
        snapshot_sequence: StorageSequence::new(w.snapshot),
        durability: super::codec::durability_of(w.durability).ok_or(())?,
        expected_origin_epoch: w.origin_epoch,
        expected_uniform_context: w.uniform,
        usage_admission,
        budget_allocation: w
            .allocation
            .as_deref()
            .map(crate::usage::VerifiedAllocation::decode_prepared)
            .transpose()?,
        internal_maintenance: w.maintenance,
        shard_copy: w.shard_copy,
        replication_fingerprint: w.fingerprint,
        replication_publication: replication.map(|p| super::intent::ReplicationPublicationIntent {
            origin: p.origin,
            origin_sequence: p.origin_sequence,
            hlc: p.hlc,
            commit_ulid: p.commit_ulid,
            context: p.context,
            records: p.records,
            private_payload: p.private_payload,
            observations: p.observations,
            schema_observations: p.schema_observations,
            conflicts: p.conflicts,
        }),
        merge_conflict,
        hlc: w.hlc,
        writes,
        metadata,
        schema_envelopes,
    };
    intent.validated_generated_identity()?;
    Ok(intent)
}

fn decode_admission(
    (project, db, partition, region, request, time, meter, units): Admission,
) -> Result<crate::usage::UsageAdmissionContext, ()> {
    crate::usage::UsageAdmissionContext::try_new(
        &project,
        &db,
        &partition,
        crate::regions::RegionCode::from_text(&region).map_err(|_| ())?,
        crate::usage::UsageFactId::try_new(&request).map_err(|_| ())?,
        time,
        meter,
        units.parse().map_err(|_| ())?,
    )
    .map_err(|_| ())
}
