use tb_core::{CollectionId, DatabaseId, DocumentId, OriginId, ReplicaId, TransactionId};

use crate::{
    CausalContext, CollectionBinding, CommitEvent, ConfigurationId, CursorError, CursorLimits,
    CursorScope, DocumentChange, DocumentState, DocumentValue, EncodedEventSizes, EventDigest,
    EventHeader, EventId, EventVersions, MAX_BATCH_BYTES, MAX_DISTRIBUTED_METADATA_BYTES,
    MAX_WAL_GROUP_BYTES, MembershipEpoch, OfferOutcome, OriginCursor, ReplicationError,
    ReplicationLimits, ShardId, TransferEnvelopeSizes,
};

const DATABASE: DatabaseId = DatabaseId::from_bytes([10; 16]);
const DOCUMENT: DocumentId = DocumentId::from_bytes([20; 16]);
const COLLECTION: CollectionId = CollectionId::from_bytes([21; 16]);
const SHARD: ShardId = ShardId::new(0);
const EPOCH: MembershipEpoch = MembershipEpoch::new(1);
const VERSIONS: EventVersions = EventVersions {
    record_format: 1,
    event_format: 1,
    merge_policy: 1,
};

fn configuration() -> ConfigurationId {
    ConfigurationId::from_bytes([7; 32]).expect("nonzero configuration")
}

fn scope() -> CursorScope {
    CursorScope {
        database: DATABASE,
        shard: SHARD,
        origin: origin(1),
        epoch: EPOCH,
        configuration: configuration(),
    }
}

fn limits() -> ReplicationLimits {
    ReplicationLimits {
        max_origins: 8,
        max_changes: 4,
        max_siblings: 4,
        max_value_bytes: 16,
        max_payload_bytes: 64,
    }
}

fn origin(seed: u8) -> OriginId {
    OriginId {
        replica_id: ReplicaId::from_bytes([seed; 16]),
        incarnation: [seed; 16],
    }
}

fn event(
    writer: OriginId,
    sequence: u64,
    observed: &[(OriginId, u64)],
    value: DocumentValue,
    digest_seed: u8,
    transaction_expiry: u64,
) -> CommitEvent {
    let context = CausalContext::from_entries(observed.iter().copied(), limits().max_origins)
        .expect("test context is valid");
    CommitEvent::new(
        EventHeader {
            id: EventId::new(DATABASE, SHARD, writer, sequence).expect("nonzero sequence"),
            epoch: EPOCH,
            configuration: configuration(),
            versions: VERSIONS,
            transaction_id: TransactionId::new(transaction_expiry, [digest_seed; 16]),
            digest: EventDigest::from_bytes([digest_seed; 32]),
        },
        EncodedEventSizes {
            wal_group_bytes: 64,
            distributed_metadata_bytes: 16,
        },
        vec![DocumentChange {
            binding: CollectionBinding {
                collection: COLLECTION,
                generation: 1,
                schema_digest: [3; 32],
            },
            document: DOCUMENT,
            observed: context,
            value,
        }],
        limits(),
    )
    .expect("test event is valid")
}

fn bytes(value: u8) -> DocumentValue {
    DocumentValue::Bytes(vec![value].into_boxed_slice())
}

fn state(event: &CommitEvent) -> DocumentState {
    DocumentState::new(DATABASE, SHARD, COLLECTION, DOCUMENT)
        .apply(event, limits())
        .expect("event applies")
}

#[test]
fn concurrent_join_is_associative_commutative_and_idempotent() {
    let a = state(&event(origin(1), 1, &[], bytes(1), 1, 1));
    let b = state(&event(origin(2), 1, &[], bytes(2), 2, u64::MAX));
    let c = state(&event(origin(3), 1, &[], bytes(3), 3, 0));

    let ab = a.join(&b, limits()).expect("join");
    assert_eq!(ab, b.join(&a, limits()).expect("commutative"));
    assert_eq!(ab, ab.join(&ab, limits()).expect("idempotent"));
    assert_eq!(
        ab.join(&c, limits()).expect("left grouping"),
        a.join(&b.join(&c, limits()).expect("right inner"), limits())
            .expect("right grouping")
    );
    assert_eq!(
        ab.join(&c, limits())
            .expect("three siblings")
            .versions()
            .len(),
        3
    );
}

#[test]
fn later_causal_write_removes_observed_siblings_but_keeps_concurrent_one() {
    let a = state(&event(origin(1), 1, &[], bytes(1), 1, 2));
    let b = state(&event(origin(2), 1, &[], bytes(2), 2, 2));
    let c = state(&event(origin(3), 1, &[], bytes(3), 3, 2));
    let later = state(&event(
        origin(1),
        2,
        &[(origin(1), 1), (origin(2), 1)],
        bytes(4),
        4,
        2,
    ));
    let merged = a
        .join(&b, limits())
        .expect("join")
        .join(&later, limits())
        .expect("causal overwrite")
        .join(&c, limits())
        .expect("concurrent join");
    let values: Vec<_> = merged
        .versions()
        .map(|version| version.value.clone())
        .collect();
    assert_eq!(values, vec![bytes(4), bytes(3)]);
}

#[test]
fn causal_delete_prevents_late_resurrection_and_exposes_concurrent_put() {
    let original = state(&event(origin(1), 1, &[], bytes(1), 1, 0));
    let deleted = state(&event(
        origin(1),
        2,
        &[(origin(1), 1)],
        DocumentValue::Tombstone,
        2,
        0,
    ));
    let late = deleted.join(&original, limits()).expect("late old put");
    assert_eq!(
        late.versions()
            .map(|version| version.value)
            .collect::<Vec<_>>(),
        vec![&DocumentValue::Tombstone]
    );

    let concurrent = state(&event(origin(2), 1, &[], bytes(2), 3, 0));
    let conflicted = late.join(&concurrent, limits()).expect("concurrent put");
    assert_eq!(conflicted.versions().len(), 2);
}

#[test]
fn same_dot_with_different_bytes_or_digest_is_corruption() {
    let first = state(&event(origin(1), 1, &[], bytes(1), 1, 0));
    let altered_value = state(&event(origin(1), 1, &[], bytes(2), 2, 0));
    assert_eq!(
        first.join(&altered_value, limits()),
        Err(ReplicationError::ConflictingVersion)
    );
    let altered_digest = state(&event(origin(1), 1, &[], bytes(1), 2, 0));
    assert_eq!(
        first.join(&altered_digest, limits()),
        Err(ReplicationError::ConflictingVersion)
    );
}

#[test]
fn same_document_id_in_two_collections_is_independent() {
    let first = event(origin(1), 1, &[], bytes(1), 1, 0);
    let mut changes = first.changes().to_vec();
    let mut second = changes[0].clone();
    second.binding.collection = CollectionId::from_bytes([22; 16]);
    second.binding.generation = 7;
    second.binding.schema_digest = [7; 32];
    second.value = bytes(2);
    changes.push(second);
    let group = CommitEvent::new(
        first.header(),
        first.encoded_sizes(),
        changes.clone(),
        limits(),
    )
    .expect("two collections may share a document identity");
    assert_eq!(group.bindings().len(), 2);
    let state_a = DocumentState::new(DATABASE, SHARD, COLLECTION, DOCUMENT)
        .apply(&group, limits())
        .expect("first collection applies");
    let state_b = DocumentState::new(DATABASE, SHARD, changes[1].binding.collection, DOCUMENT)
        .apply(&group, limits())
        .expect("second collection applies");
    assert_eq!(state_a.versions().next().unwrap().binding.generation, 1);
    assert_eq!(state_b.versions().next().unwrap().binding.generation, 7);
    assert_eq!(
        state_a.join(&state_b, limits()),
        Err(ReplicationError::ScopeMismatch)
    );

    let mut conflicting = changes;
    conflicting[1].binding.collection = COLLECTION;
    assert_eq!(
        CommitEvent::new(first.header(), first.encoded_sizes(), conflicting, limits()),
        Err(ReplicationError::ConflictingBinding)
    );
}

#[test]
fn sibling_limit_rejects_without_mutating_inputs() {
    let a = state(&event(origin(1), 1, &[], bytes(1), 1, 0));
    let b = state(&event(origin(2), 1, &[], bytes(2), 2, 0));
    let before = a.clone();
    let mut small = limits();
    small.max_siblings = 1;
    assert_eq!(
        a.join(&b, small),
        Err(ReplicationError::LimitExceeded("document siblings"))
    );
    assert_eq!(a, before);
}

fn record_synced(cursor: &mut OriginCursor, event: &CommitEvent) -> Result<u64, CursorError> {
    cursor.record_synced(
        event.id(),
        event.epoch(),
        event.configuration(),
        event.digest(),
    )
}

fn record_applied(cursor: &mut OriginCursor, event: &CommitEvent) -> Result<u64, CursorError> {
    cursor.record_applied(event.id(), event.epoch(), event.configuration())
}

fn cursor() -> OriginCursor {
    OriginCursor::new(
        scope(),
        CursorLimits {
            max_pending: 3,
            max_sequence_distance: 3,
        },
    )
    .expect("valid cursor")
}

#[test]
fn reordered_receipt_and_sync_only_advance_contiguous_prefix() {
    let mut cursor = cursor();
    let one = event(origin(1), 1, &[], bytes(1), 1, 0);
    let two = event(origin(1), 2, &[(origin(1), 1)], bytes(2), 2, 0);
    let three = event(origin(1), 3, &[(origin(1), 2)], bytes(3), 3, 0);
    assert_eq!(cursor.offer(&three), Ok(OfferOutcome::Accepted));
    assert_eq!(cursor.received_prefix(), 0);
    assert_eq!(record_synced(&mut cursor, &three), Ok(0));
    assert_eq!(cursor.offer(&one), Ok(OfferOutcome::Accepted));
    assert_eq!(cursor.received_prefix(), 1);
    assert_eq!(cursor.durable_prefix(), 0);
    assert_eq!(
        record_applied(&mut cursor, &one),
        Err(CursorError::EventNotDurable)
    );
    assert_eq!(record_synced(&mut cursor, &one), Ok(1));
    assert_eq!(cursor.offer(&two), Ok(OfferOutcome::Accepted));
    assert_eq!(cursor.received_prefix(), 3);
    assert_eq!(record_synced(&mut cursor, &two), Ok(3));
    assert_eq!(
        record_applied(&mut cursor, &three),
        Err(CursorError::ApplyGap)
    );
    assert_eq!(record_applied(&mut cursor, &one), Ok(1));
    assert_eq!(record_applied(&mut cursor, &two), Ok(2));
    assert_eq!(record_applied(&mut cursor, &three), Ok(3));
    assert_eq!(
        cursor.offer(&one),
        Ok(OfferOutcome::RequiresStoredIdentityCheck)
    );
}

#[test]
fn duplicate_collision_epoch_and_gap_limits_do_not_advance_durable() {
    let mut cursor = cursor();
    let one = event(origin(1), 1, &[], bytes(1), 1, 0);
    let altered = event(origin(1), 1, &[], bytes(9), 9, 0);
    assert_eq!(cursor.offer(&one), Ok(OfferOutcome::Accepted));
    assert_eq!(cursor.offer(&one), Ok(OfferOutcome::DuplicatePending));
    assert_eq!(
        cursor.offer(&altered),
        Err(CursorError::ConflictingIdentity)
    );
    assert_eq!(cursor.durable_prefix(), 0);

    let four = event(origin(1), 4, &[(origin(1), 3)], bytes(4), 4, 0);
    assert_eq!(cursor.offer(&four), Err(CursorError::GapBeyondWindow));
    let stale = CommitEvent::new(
        EventHeader {
            epoch: MembershipEpoch::new(2),
            ..one.header()
        },
        one.encoded_sizes(),
        one.changes().to_vec(),
        limits(),
    )
    .expect("event in another epoch is structurally valid");
    assert_eq!(cursor.offer(&stale), Err(CursorError::EpochMismatch));
    let divergent = CommitEvent::new(
        EventHeader {
            configuration: ConfigurationId::from_bytes([8; 32]).expect("nonzero configuration"),
            ..one.header()
        },
        one.encoded_sizes(),
        one.changes().to_vec(),
        limits(),
    )
    .expect("same epoch can carry another configuration identity");
    assert_eq!(
        cursor.offer(&divergent),
        Err(CursorError::ConfigurationMismatch)
    );
    assert_eq!(cursor.pending_len(), 1);
}

#[test]
fn unsupported_merge_policy_is_rejected_before_document_apply() {
    let reference = event(origin(1), 1, &[], bytes(1), 1, 0);
    let mut versions = reference.versions();
    versions.merge_policy = 2;
    assert_eq!(
        CommitEvent::new(
            EventHeader {
                versions,
                ..reference.header()
            },
            reference.encoded_sizes(),
            reference.changes().to_vec(),
            limits(),
        ),
        Err(ReplicationError::UnsupportedMergePolicy)
    );
}

#[test]
fn transaction_expiry_is_neither_a_causal_clock_nor_merge_order() {
    let early_expiry = state(&event(origin(1), 1, &[], bytes(1), 1, 1));
    let late_expiry = state(&event(origin(2), 1, &[], bytes(2), 2, u64::MAX));
    assert_eq!(
        early_expiry.join(&late_expiry, limits()),
        late_expiry.join(&early_expiry, limits())
    );
    assert_eq!(
        early_expiry
            .join(&late_expiry, limits())
            .expect("merge")
            .versions()
            .len(),
        2
    );
}

#[test]
fn recovery_accepts_only_applied_prefix_within_durable_prefix() {
    assert!(
        OriginCursor::from_recovered(
            scope(),
            3,
            2,
            CursorLimits {
                max_pending: 3,
                max_sequence_distance: 3
            }
        )
        .is_ok()
    );
    assert_eq!(
        OriginCursor::from_recovered(
            scope(),
            2,
            3,
            CursorLimits {
                max_pending: 3,
                max_sequence_distance: 3
            }
        ),
        Err(CursorError::InvalidCheckpoint)
    );
}

#[test]
fn complete_group_over_common_admission_cap_is_rejected() {
    let reference = event(origin(1), 1, &[], bytes(1), 1, 0);
    assert_eq!(
        CommitEvent::new(
            reference.header(),
            EncodedEventSizes {
                wal_group_bytes: MAX_WAL_GROUP_BYTES + 1,
                distributed_metadata_bytes: 16,
            },
            reference.changes().to_vec(),
            limits(),
        ),
        Err(ReplicationError::LimitExceeded("encoded event sizes"))
    );
    assert_eq!(
        CommitEvent::new(
            reference.header(),
            EncodedEventSizes {
                wal_group_bytes: MAX_WAL_GROUP_BYTES,
                distributed_metadata_bytes: MAX_DISTRIBUTED_METADATA_BYTES + 1,
            },
            reference.changes().to_vec(),
            limits(),
        ),
        Err(ReplicationError::LimitExceeded("encoded event sizes"))
    );
}

#[test]
fn wrapper_can_exceed_small_wal_but_full_transfer_obeys_own_cap() {
    let reference = event(origin(1), 1, &[], bytes(1), 1, 0);
    let event = CommitEvent::new(
        reference.header(),
        EncodedEventSizes {
            wal_group_bytes: 100,
            distributed_metadata_bytes: 120,
        },
        reference.changes().to_vec(),
        limits(),
    )
    .expect("wrapper is independently bounded");
    assert_eq!(
        event.validate_transfer_sizes(TransferEnvelopeSizes {
            encoded_bytes: 160,
            decoded_bytes: 124,
        }),
        Ok(())
    );
    assert_eq!(
        event.validate_transfer_sizes(TransferEnvelopeSizes {
            encoded_bytes: 160,
            decoded_bytes: 120,
        }),
        Err(ReplicationError::LimitExceeded("application batch bytes"))
    );
    assert_eq!(
        event.validate_transfer_sizes(TransferEnvelopeSizes {
            encoded_bytes: MAX_BATCH_BYTES + 1,
            decoded_bytes: 224,
        }),
        Err(ReplicationError::LimitExceeded("application batch bytes"))
    );
}

#[test]
fn pending_count_limit_rejects_reordered_backlog_without_advancing() {
    let mut cursor = OriginCursor::new(
        scope(),
        CursorLimits {
            max_pending: 1,
            max_sequence_distance: 3,
        },
    )
    .expect("valid cursor");
    let two = event(origin(1), 2, &[(origin(1), 1)], bytes(2), 2, 0);
    let three = event(origin(1), 3, &[(origin(1), 2)], bytes(3), 3, 0);
    assert_eq!(cursor.offer(&two), Ok(OfferOutcome::Accepted));
    assert_eq!(cursor.offer(&three), Err(CursorError::PendingFull));
    assert_eq!(cursor.received_prefix(), 0);
    assert_eq!(cursor.durable_prefix(), 0);
}

#[test]
fn cursor_hard_caps_reject_unbounded_gap_configuration() {
    assert_eq!(
        OriginCursor::new(
            scope(),
            CursorLimits {
                max_pending: crate::cursor::MAX_CURSOR_PENDING + 1,
                max_sequence_distance: 1,
            },
        ),
        Err(CursorError::InvalidLimits)
    );
    assert_eq!(
        OriginCursor::new(
            scope(),
            CursorLimits {
                max_pending: 1,
                max_sequence_distance: crate::cursor::MAX_CURSOR_SEQUENCE_DISTANCE + 1,
            },
        ),
        Err(CursorError::InvalidLimits)
    );
}
