use crate::receiver_fixture::*;

#[test]
fn distributed_point_read_output_budget_refusal_is_retryable() {
    const PROCESS_BYTES: usize = 512 * 1024 * 1024;
    const QUERY_BYTES: usize = 16 * 1024 * 1024;
    let memory = MemoryBudget::new(
        BudgetConfig::new(PROCESS_BYTES, 0)
            .expect("point-read pressure budget")
            .with_category_limit_bytes(MemoryCategory::Query, QUERY_BYTES),
    );
    let target = Fixture::new_with_memory_budget(ORIGIN_A, 16, memory);
    let store = target.ready_store();
    let body = vec![b'x'; 900 * 1024];
    for (index, origin) in [ORIGIN_B, ORIGIN_C, ORIGIN_D, ORIGIN_E]
        .into_iter()
        .enumerate()
    {
        let event = Fixture::new(origin).v1_event(
            DOCUMENT,
            &[],
            Some(&body),
            u8::try_from(index + 60).unwrap(),
        );
        assert_eq!(
            target
                .receive(&store, &event)
                .expect("apply signed sibling"),
            DocumentApplyOutcome::Applied { sequence: 1 }
        );
    }

    let before_read = target.memory.snapshot().categories[MemoryCategory::Query as usize];
    let read = store
        .point_read(COLLECTION, DOCUMENT)
        .expect("four-sibling read");
    assert!(matches!(
        read.point(),
        tb_document::DocumentPoint::Conflict { siblings, .. } if siblings.len() == 4
    ));
    let with_read = target.memory.snapshot().categories[MemoryCategory::Query as usize];
    let output_claim = with_read - before_read;
    assert!(output_claim > 3 * tb_core::MAX_VALUE_BYTES);
    drop(read);

    // Leave enough for the D0/D6/D1 validation reads, but one byte less
    // than the complete owned conflict result requires.
    let held_bytes = QUERY_BYTES - before_read - (output_claim - 1);
    let held = target
        .memory
        .try_reserve(
            Attribution {
                tenant: TENANT,
                worker: WorkerId(2),
                category: MemoryCategory::Query,
            },
            held_bytes,
        )
        .expect("hold output budget short by one byte");
    assert!(matches!(
        store.point_read(COLLECTION, DOCUMENT),
        Err(DocumentError::Admission(_))
    ));
    drop(held);
    assert!(matches!(
        store.point_read(COLLECTION, DOCUMENT).unwrap().point(),
        tb_document::DocumentPoint::Conflict { siblings, .. } if siblings.len() == 4
    ));
    for origin in [ORIGIN_B, ORIGIN_C, ORIGIN_D, ORIGIN_E] {
        assert_eq!(store.durable_prefix(origin).unwrap().sequence, 1);
    }
}

#[cfg(feature = "slow-tests")]
#[test]
fn corrupt_durable_chunk_is_reported_as_stored_fault() {
    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let event = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"durable bytes before injected fault"),
        41,
    );

    let target = Fixture::new(ORIGIN_A);
    let view = target.authority.view();
    let member = view
        .certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                origin: ORIGIN_B,
                epoch: view.epoch(),
                configuration: view.configuration(),
            },
            leaf(ORIGIN_B),
        )
        .expect("signed source member");
    let proof = verify_incoming(
        &event.metadata,
        &event.wal_frame,
        event.digest,
        target.config.replication,
        &target.memory,
        Attribution {
            tenant: TENANT,
            worker: WorkerId(2),
            category: MemoryCategory::Replication,
        },
    )
    .expect("verified source bytes");
    let certified = view
        .certify_event(&member, &proof)
        .expect("signed source catalog");
    let mut receivers = target.receivers();
    let receiver = receivers
        .iter_mut()
        .find(|receiver| receiver.durable_prefix().origin == ORIGIN_B)
        .expect("origin B inbox");
    assert_eq!(
        receiver
            .append_certified(certified)
            .expect("synced durable group")
            .sequence,
        1
    );
    receiver
        .corrupt_first_durable_chunk_for_slow_test(1)
        .expect("sync changed chunk through storage");
    assert!(matches!(
        receiver.read_durable_exact(1, tb_core::MAX_WAL_GROUP_BYTES),
        Err(tb_replication::ReceiveError::DurableRead {
            kind: DurableReadKind::StoredFault,
            ..
        })
    ));

    let store = target.open();
    store
        .install_receivers(receivers)
        .expect("install the complete recovered receiver set");
    store
        .reconcile_inboxes()
        .expect("no D5 exists before replay");
    assert!(matches!(
        store.replay_next(ORIGIN_B),
        Err(DocumentError::Receive(
            tb_replication::ReceiveError::DurableRead {
                kind: DurableReadKind::StoredFault,
                ..
            }
        ))
    ));
    assert!(matches!(
        store.replay_next(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    drop(store);
    assert!(!target.has_applied(event.event));
}

#[test]
fn three_origin_observed_write_resolves_only_covered_siblings() {
    let b = received_event_fixture(ORIGIN_B);
    let b_store = b.ready_store();
    let live = b.commit(&b_store, DOCUMENT, &CausalContext::new(), Some(b"b"), 3);
    let c = received_event_fixture(ORIGIN_C);
    let tombstone = c.v1_event(DOCUMENT, &[], None, 4);

    let d = received_event_fixture(ORIGIN_D);
    let observed_write = d.v1_event(DOCUMENT, &[(ORIGIN_B, 1), (ORIGIN_C, 1)], Some(b"d"), 5);
    let e = received_event_fixture(ORIGIN_E);
    let unobserved_tombstone = e.v1_event(DOCUMENT, &[], None, 6);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    target.receive(&store, &live).expect("apply origin B");
    target.receive(&store, &tombstone).expect("apply origin C");
    target
        .receive(&store, &observed_write)
        .expect("apply observing origin D");
    drop(store);
    let after_resolution = target.manifest(DOCUMENT);
    assert_eq!(after_resolution.siblings().len(), 1);
    assert_eq!(after_resolution.siblings()[0].event.origin, ORIGIN_D);
    assert_eq!(after_resolution.context().len(), 3);
    assert!(
        after_resolution
            .context()
            .iter()
            .any(|dot| dot.origin == ORIGIN_B && dot.sequence == 1)
    );
    assert!(
        after_resolution
            .context()
            .iter()
            .any(|dot| dot.origin == ORIGIN_C && dot.sequence == 1)
    );

    let store = target.ready_store();
    target
        .receive(&store, &unobserved_tombstone)
        .expect("apply unobserved tombstone");
    drop(store);
    let with_concurrent_delete = target.manifest(DOCUMENT);
    let siblings: Vec<_> = with_concurrent_delete
        .siblings()
        .iter()
        .map(|sibling| (sibling.event.origin, sibling.kind))
        .collect();
    assert_eq!(
        siblings,
        vec![
            (ORIGIN_D, DocumentKind::Live),
            (ORIGIN_E, DocumentKind::Tombstone),
        ]
    );
}

#[test]
fn pre_and_post_reconciliation_conflicts_leave_exact_duplicates_healthy() {
    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let original = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"original"),
        28,
    );
    let collision_source = Fixture::new(ORIGIN_B);
    let collision_store = collision_source.ready_store();
    let conflicting = collision_source.commit(
        &collision_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"different bytes for the same event identity"),
        29,
    );
    assert_eq!(original.event, conflicting.event);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert_eq!(
        target.receive(&store, &original).expect("apply original"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    assert!(matches!(
        target.receive(&store, &conflicting),
        Err(DocumentError::IncomingConflict)
    ));
    assert_eq!(
        target
            .receive(&store, &original)
            .expect("exact duplicate after post-gate conflict"),
        DocumentApplyOutcome::Duplicate { sequence: 1 }
    );
    drop(store);

    // Reopening recovers D5 into the writer, but the inbox coupling gate is
    // still closed until the complete receiver set is reconciled.
    let store = target.open();
    store
        .install_receivers(target.receivers())
        .expect("install recovered receivers before reconciliation");
    assert!(matches!(
        target.receive(&store, &conflicting),
        Err(DocumentError::IncomingConflict)
    ));
    assert_eq!(
        target
            .receive(&store, &original)
            .expect("exact duplicate after pre-gate conflict"),
        DocumentApplyOutcome::DurableOnly { durable_prefix: 1 }
    );
    assert!(matches!(
        store.replay_next(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    store
        .reconcile_inboxes()
        .expect("couple recovered exact inbox after duplicate checks");
    assert_eq!(
        target
            .receive(&store, &original)
            .expect("exact duplicate after reconciliation"),
        DocumentApplyOutcome::Duplicate { sequence: 1 }
    );
    drop(store);
    assert!(target.has_applied(original.event));
    assert_eq!(target.manifest(DOCUMENT).siblings().len(), 1);
}

#[test]
fn foreign_accepted_view_with_same_scope_cannot_append_to_target_inbox() {
    let foreign = Fixture::new(ORIGIN_B);
    let foreign_store = foreign.ready_store();
    let event = foreign.commit(
        &foreign_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"same signed scope, different accepted view"),
        40,
    );
    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();

    let foreign_view = foreign.authority.view();
    let foreign_member = foreign_view
        .certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                origin: event.event.origin,
                epoch: foreign_view.epoch(),
                configuration: foreign_view.configuration(),
            },
            leaf(event.event.origin),
        )
        .expect("foreign fixture member under the same signed scope");
    let foreign_proof = verify_incoming(
        &event.metadata,
        &event.wal_frame,
        event.digest,
        foreign.config.replication,
        &foreign.memory,
        Attribution {
            tenant: TENANT,
            worker: WorkerId(2),
            category: MemoryCategory::Replication,
        },
    )
    .expect("same-scope foreign proof");
    let foreign_certified = foreign_view
        .certify_event(&foreign_member, &foreign_proof)
        .expect("same-scope foreign accepted view");

    assert!(matches!(
        store.receive_certified(foreign_certified),
        Err(DocumentError::IncomingConflict)
    ));
    assert_eq!(
        target
            .receive(&store, &event)
            .expect("target-certified event is still admissible"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(store);
    assert!(target.has_applied(event.event));
    target.exact_source_changes(&event);
}

#[test]
fn pending_durable_conflict_does_not_poison_original_replay() {
    let b = Fixture::new(ORIGIN_B);
    let b_store = b.ready_store();
    let remote_predecessor = b.commit(&b_store, DOCUMENT, &CausalContext::new(), Some(b"b"), 30);

    let c = Fixture::new(ORIGIN_C);
    let c_store = c.ready_store();
    c.receive(&c_store, &remote_predecessor)
        .expect("C applies B's predecessor");
    let pending = c.commit(
        &c_store,
        DOCUMENT,
        &context(&[(ORIGIN_B, 1)]),
        Some(b"original pending C"),
        31,
    );
    let conflicting = Fixture::new(ORIGIN_C).v1_event(
        DOCUMENT,
        &[(ORIGIN_B, 1)],
        Some(b"conflicting pending C"),
        32,
    );
    assert_eq!(pending.event, conflicting.event);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target
            .receive(&store, &pending)
            .expect("persist C while B dependency is absent"),
        DocumentApplyOutcome::DurableOnly { .. }
    ));
    assert!(matches!(
        target.receive(&store, &conflicting),
        Err(DocumentError::IncomingConflict)
    ));
    target
        .receive(&store, &remote_predecessor)
        .expect("apply B after rejecting conflicting C bytes");
    assert_eq!(
        store
            .replay_next(ORIGIN_C)
            .expect("replay the unchanged durable C group")
            .expect("C's pending group remains durable"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(store);
    assert!(target.has_applied(remote_predecessor.event));
    assert!(target.has_applied(pending.event));
    let manifest = target.manifest(DOCUMENT);
    assert_eq!(manifest.siblings().len(), 1);
    assert_eq!(manifest.siblings()[0].event.origin, ORIGIN_C);
}

#[test]
fn out_of_order_durable_conflict_does_not_poison_gap_replay() {
    let c = Fixture::new(ORIGIN_C);
    let c_store = c.ready_store();
    let first = c.commit(
        &c_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"C sequence one"),
        34,
    );
    let second = c.commit(
        &c_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"C sequence two"),
        35,
    );
    assert_eq!(first.event.sequence, 1);
    assert_eq!(second.event.sequence, 2);

    let collision = Fixture::new(ORIGIN_C);
    let collision_store = collision.ready_store();
    collision.commit(
        &collision_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"different C sequence one"),
        36,
    );
    let conflicting_second = collision.commit(
        &collision_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"conflicting C sequence two"),
        37,
    );
    assert_eq!(conflicting_second.event, second.event);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target
            .receive(&store, &second)
            .expect("persist the out-of-order C event"),
        DocumentApplyOutcome::DurableOnly { durable_prefix: 0 }
    ));
    assert!(matches!(
        target.receive(&store, &conflicting_second),
        Err(DocumentError::IncomingConflict)
    ));
    assert_eq!(
        target
            .receive(&store, &first)
            .expect("apply C sequence one"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    assert_eq!(
        store
            .replay_next(ORIGIN_C)
            .expect("replay unchanged C sequence two")
            .expect("C sequence two remains durable"),
        DocumentApplyOutcome::Applied { sequence: 2 }
    );
    drop(store);
    assert!(target.has_applied(first.event));
    assert!(target.has_applied(second.event));
}

#[test]
fn unavailable_duplicate_inbox_read_is_retryable_after_budget_release() {
    const PROCESS_BYTES: usize = 512 * 1024 * 1024;
    const REPLICATION_BYTES: usize = 1024 * 1024;

    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let event = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"duplicate read under pressure"),
        38,
    );

    let constrained_memory = MemoryBudget::new(
        BudgetConfig::new(PROCESS_BYTES, 0)
            .expect("pressure-test budget")
            .with_category_limit_bytes(MemoryCategory::Replication, REPLICATION_BYTES),
    );
    let target = Fixture::new_with_memory_budget(ORIGIN_A, 16, constrained_memory);
    let store = target.ready_store();
    target
        .receive(&store, &event)
        .expect("apply event before budget pressure");

    let view = target.authority.view();
    let member = view
        .certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                origin: event.event.origin,
                epoch: view.epoch(),
                configuration: view.configuration(),
            },
            leaf(event.event.origin),
        )
        .expect("certified duplicate origin");
    let proof = verify_incoming(
        &event.metadata,
        &event.wal_frame,
        event.digest,
        target.config.replication,
        &target.memory,
        Attribution {
            tenant: TENANT,
            worker: WorkerId(2),
            category: MemoryCategory::Replication,
        },
    )
    .expect("verified duplicate before pressure");
    let certified = view
        .certify_event(&member, &proof)
        .expect("certified duplicate before pressure");

    let replication_used =
        target.memory.snapshot().categories[MemoryCategory::Replication as usize];
    assert!(replication_used <= REPLICATION_BYTES);
    let held_replication = target
        .memory
        .try_reserve(
            Attribution {
                tenant: TENANT,
                worker: WorkerId(2),
                category: MemoryCategory::Replication,
            },
            REPLICATION_BYTES - replication_used,
        )
        .expect("hold remaining replication-category budget");
    assert!(matches!(
        store.receive_certified(certified),
        Err(DocumentError::Receive(
            tb_replication::ReceiveError::DurableRead {
                kind: DurableReadKind::Unavailable,
                ..
            }
        ))
    ));
    drop(held_replication);
    assert_eq!(
        target
            .receive(&store, &event)
            .expect("retry exact duplicate after budget release"),
        DocumentApplyOutcome::Duplicate { sequence: 1 }
    );
}

#[test]
fn out_of_window_append_is_unavailable_and_does_not_block_sequence_one() {
    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let mut first = None;
    let mut far_ahead = None;
    for sequence in 1_u8..=33 {
        let package = source.commit(
            &source_store,
            DocumentId::from_bytes([sequence; 16]),
            &CausalContext::new(),
            Some(b"bounded-window source event"),
            sequence,
        );
        if sequence == 1 {
            first = Some(package);
        } else if sequence == 33 {
            far_ahead = Some(package);
        }
    }
    let first = first.expect("source sequence one");
    let far_ahead = far_ahead.expect("source sequence beyond receive window");
    assert_eq!(far_ahead.event.sequence, 33);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target.receive(&store, &far_ahead),
        Err(DocumentError::Receive(
            tb_replication::ReceiveError::Append {
                kind: AppendFailureKind::Unavailable,
                ..
            }
        ))
    ));
    assert_eq!(
        target
            .receive(&store, &first)
            .expect("valid sequence one remains admissible"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(store);
    assert!(target.has_applied(first.event));
    assert!(!target.has_applied(far_ahead.event));
    assert!(!target.has_primary(DocumentId::from_bytes([33; 16])));
}

#[test]
fn unavailable_pre_gate_append_can_retry_after_budget_release() {
    const PROCESS_BYTES: usize = 512 * 1024 * 1024;
    const REPLICATION_BYTES: usize = 1024 * 1024;

    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let event = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"retryable inbox append"),
        39,
    );
    let constrained_memory = MemoryBudget::new(
        BudgetConfig::new(PROCESS_BYTES, 0)
            .expect("pressure-test budget")
            .with_category_limit_bytes(MemoryCategory::Replication, REPLICATION_BYTES),
    );
    let target = Fixture::new_with_memory_budget(ORIGIN_A, 16, constrained_memory);
    let store = target.open();
    store
        .install_receivers(target.receivers())
        .expect("install inboxes before the reconciliation gate");

    let view = target.authority.view();
    let member = view
        .certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                origin: event.event.origin,
                epoch: view.epoch(),
                configuration: view.configuration(),
            },
            leaf(event.event.origin),
        )
        .expect("certified remote origin");
    let proof = verify_incoming(
        &event.metadata,
        &event.wal_frame,
        event.digest,
        target.config.replication,
        &target.memory,
        Attribution {
            tenant: TENANT,
            worker: WorkerId(2),
            category: MemoryCategory::Replication,
        },
    )
    .expect("verified append before pressure");
    let certified = view
        .certify_event(&member, &proof)
        .expect("certified append before pressure");

    let replication_used =
        target.memory.snapshot().categories[MemoryCategory::Replication as usize];
    assert!(replication_used <= REPLICATION_BYTES);
    let held_replication = target
        .memory
        .try_reserve(
            Attribution {
                tenant: TENANT,
                worker: WorkerId(2),
                category: MemoryCategory::Replication,
            },
            REPLICATION_BYTES - replication_used,
        )
        .expect("hold remaining replication-category budget");
    assert!(matches!(
        store.receive_certified(certified),
        Err(DocumentError::Receive(
            tb_replication::ReceiveError::Append {
                kind: AppendFailureKind::Unavailable,
                ..
            }
        ))
    ));
    drop(held_replication);

    assert!(matches!(
        target
            .receive(&store, &event)
            .expect("retry append after budget release"),
        DocumentApplyOutcome::DurableOnly { durable_prefix: 1 }
    ));
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(event.event));

    let store = target.ready_store();
    assert_eq!(
        store
            .replay_next(ORIGIN_B)
            .expect("replay retryable durable event")
            .expect("event retained in inbox"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(store);
    assert!(target.has_applied(event.event));
}

#[test]
fn refusing_a_second_concurrent_sibling_publishes_no_d0_or_d5() {
    let b = received_event_fixture(ORIGIN_B);
    let b_store = b.ready_store();
    let first = b.commit(&b_store, DOCUMENT, &CausalContext::new(), Some(b"one"), 14);
    let c = received_event_fixture(ORIGIN_C);
    let second = c.v1_event(DOCUMENT, &[], Some(b"two"), 15);

    let target = Fixture::new_with_sibling_limit(ORIGIN_A, 1);
    let store = target.ready_store();
    target.receive(&store, &first).expect("first sibling fits");
    assert!(target.receive(&store, &second).is_err());
    drop(store);
    assert!(!target.has_applied(second.event));
    assert_eq!(target.manifest(DOCUMENT).siblings().len(), 1);
}

#[test]
fn reopen_rejects_receiver_d1_without_d5() {
    let source = received_event_fixture(ORIGIN_B);
    let source_store = source.ready_store();
    let corrupt = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"bytes"),
        16,
    );
    let target = Fixture::new(ORIGIN_A);
    target.seed_source_change(&corrupt);
    assert!(matches!(
        open_distributed(
            Arc::clone(&target.authority),
            target.config.clone(),
            target.memory.clone(),
        ),
        Err(DocumentError::Corrupt("foreign visible D1"))
    ));
}

#[test]
fn reopen_with_d5_but_missing_inbox_marker_cannot_apply_dependents_or_write_source() {
    let source_b = Fixture::new(ORIGIN_B);
    let source_b_store = source_b.ready_store();
    let applied_b = source_b.commit(
        &source_b_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"b predecessor"),
        22,
    );
    let source_c = Fixture::new(ORIGIN_C);
    let dependent = source_c.v1_event(DOCUMENT, &[(ORIGIN_B, 1)], Some(b"c depends on b"), 23);

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    target
        .receive(&store, &applied_b)
        .expect("apply B and persist its D5");
    drop(store);
    assert!(target.has_applied(applied_b.event));

    let missing_inbox = target.authority.view().inbox_path(ORIGIN_B);
    fs::remove_dir_all(&missing_inbox).expect("simulate an omitted recovered inbox");
    let reopened = target.open();
    let blocked_write = reopened.commit_documents(
        TransactionId::new(50_000, [24; 16]),
        &[DocumentWrite {
            binding: DocumentBinding {
                collection: target.binding.collection,
                document: DocumentId::from_bytes([22; 16]),
                generation: target.binding.generation,
                schema_digest: target.binding.schema_digest,
            },
            observed: &CausalContext::new(),
            value: DocumentWriteValue::Tombstone,
        }],
    );
    assert!(matches!(blocked_write, Err(DocumentError::NeedsRecovery)));
    reopened
        .install_receivers(target.receivers())
        .expect("open replacement receiver set");
    assert!(matches!(
        reopened.reconcile_inboxes(),
        Err(DocumentError::Corrupt("recovered D5 ahead of inbox"))
    ));
    assert!(matches!(
        target.receive(&reopened, &dependent),
        Err(DocumentError::NeedsRecovery)
    ));
    let still_blocked = reopened.commit_documents(
        TransactionId::new(50_000, [25; 16]),
        &[DocumentWrite {
            binding: DocumentBinding {
                collection: target.binding.collection,
                document: DocumentId::from_bytes([23; 16]),
                generation: target.binding.generation,
                schema_digest: target.binding.schema_digest,
            },
            observed: &CausalContext::new(),
            value: DocumentWriteValue::Tombstone,
        }],
    );
    assert!(matches!(still_blocked, Err(DocumentError::NeedsRecovery)));
    drop(reopened);
    assert!(!target.has_applied(dependent.event));
    assert_eq!(target.manifest(DOCUMENT).siblings().len(), 1);
}

#[test]
fn duplicate_with_d5_but_missing_inbox_marker_poisons_writer() {
    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let applied = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"applied before inbox loss"),
        33,
    );

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    target
        .receive(&store, &applied)
        .expect("apply source event and persist D5");
    drop(store);

    fs::remove_dir_all(target.authority.view().inbox_path(ORIGIN_B))
        .expect("simulate loss of the recovered inbox");
    let reopened = target.open();
    reopened
        .install_receivers(target.receivers())
        .expect("install replacement inbox set");
    assert!(matches!(
        target.receive(&reopened, &applied),
        Err(DocumentError::Corrupt(
            "applied D5 lacks durable inbox marker"
        ))
    ));
    assert!(matches!(
        target.receive(&reopened, &applied),
        Err(DocumentError::NeedsRecovery)
    ));
    drop(reopened);
    assert!(target.has_applied(applied.event));
}

#[test]
fn unknown_observed_origin_is_rejected_before_any_apply_marker() {
    let source = Fixture::new(ORIGIN_B);
    let invalid_origin = OriginId {
        replica_id: ReplicaId::from_bytes([99; 16]),
        incarnation: [100; 16],
    };
    let package = source.v1_event(DOCUMENT, &[(invalid_origin, 1)], Some(b"unknown"), 20);
    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target.receive(&store, &package),
        Err(DocumentError::Corrupt("unknown observed origin"))
    ));
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(package.event));
}

#[test]
fn valid_source_bytes_with_an_unsigned_schema_never_reach_document_apply() {
    let source = Fixture::new(ORIGIN_B);
    let unsigned_binding = CollectionBinding {
        schema_digest: [0xAA; 32],
        ..source.binding
    };
    let package = source.v1_event_for_binding(
        unsigned_binding,
        DOCUMENT,
        &[],
        Some(b"unsigned schema"),
        27,
    );
    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    let view = target.authority.view();
    let member = view
        .certify_peer(
            PeerHelloScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                origin: package.event.origin,
                epoch: view.epoch(),
                configuration: view.configuration(),
            },
            leaf(package.event.origin),
        )
        .expect("signed remote member");
    let proof = verify_incoming(
        &package.metadata,
        &package.wal_frame,
        package.digest,
        target.config.replication,
        &target.memory,
        Attribution {
            tenant: TENANT,
            worker: WorkerId(2),
            category: MemoryCategory::Replication,
        },
    )
    .expect("source bytes are semantically valid");
    assert!(matches!(
        view.certify_event(&member, &proof),
        Err(tb_replication::AuthorityError::UnknownSchema)
    ));
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(package.event));
}
