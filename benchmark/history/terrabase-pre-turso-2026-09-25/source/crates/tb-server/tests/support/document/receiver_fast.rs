use crate::receiver_fixture::*;

#[test]
fn concurrent_v1_live_and_v2_tombstone_keep_exact_d1_in_both_arrival_orders() {
    let b = received_event_fixture(ORIGIN_B);
    let b_store = b.ready_store();
    let live = b.commit(
        &b_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"from-b"),
        1,
    );
    let c = received_event_fixture(ORIGIN_C);
    let tombstone = c.v1_event(DOCUMENT, &[], None, 2);
    assert_eq!(
        tb_replication::decode_metadata(&live.metadata, replication_limits())
            .unwrap()
            .header
            .record_format,
        2
    );
    assert_eq!(
        tb_replication::decode_metadata(&tombstone.metadata, replication_limits())
            .unwrap()
            .header
            .record_format,
        1
    );

    for order in [[&live, &tombstone], [&tombstone, &live]] {
        let target = Fixture::new(ORIGIN_A);
        let store = target.ready_store();
        assert!(matches!(
            store.point_read(COLLECTION, DOCUMENT).unwrap().point(),
            tb_document::DocumentPoint::Absent
        ));
        for package in order {
            assert!(matches!(
                target
                    .receive(&store, package)
                    .expect("certified concurrent receive"),
                DocumentApplyOutcome::Applied { sequence: 1 }
            ));
            if package.event == live.event {
                let read = store.point_read(COLLECTION, DOCUMENT).unwrap();
                if let tb_document::DocumentPoint::Value { sibling, .. } = read.point() {
                    assert_eq!(sibling.event, live.event);
                    assert_eq!(
                        tb_format::decode_document(&sibling.document_bytes)
                            .unwrap()
                            .field(1)
                            .unwrap()
                            .as_str(),
                        Some("from-b")
                    );
                } else if order[0].event == live.event {
                    panic!("first received live sibling must be visible as a value");
                }
            } else if order[0].event == tombstone.event {
                let read = store.point_read(COLLECTION, DOCUMENT).unwrap();
                assert!(matches!(
                    read.point(),
                    tb_document::DocumentPoint::Deleted { sibling, .. }
                        if sibling.event == tombstone.event
                ));
            }
        }
        let read = store.point_read(COLLECTION, DOCUMENT).unwrap();
        match read.point() {
            tb_document::DocumentPoint::Conflict { context, siblings } => {
                assert_eq!(context.get(ORIGIN_B), 1);
                assert_eq!(context.get(ORIGIN_C), 1);
                assert_eq!(siblings.len(), 2);
                assert_eq!(siblings[0].event, live.event);
                assert_eq!(siblings[0].kind, DocumentKind::Live);
                assert_eq!(siblings[1].event, tombstone.event);
                assert_eq!(siblings[1].kind, DocumentKind::Tombstone);
                assert!(siblings[1].document_bytes.is_empty());
            }
            _ => panic!("concurrent live and tombstone must remain a conflict"),
        }
        assert_eq!(store.durable_prefix(ORIGIN_B).unwrap().sequence, 1);
        assert_eq!(store.durable_prefix(ORIGIN_C).unwrap().sequence, 1);
        assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 1);
        assert_eq!(store.applied_prefix(ORIGIN_C).unwrap(), 1);
        assert!(matches!(
            store.durable_prefix(ORIGIN_A),
            Err(DocumentError::IncomingConflict)
        ));
        assert!(matches!(
            store.applied_prefix(ORIGIN_A),
            Err(DocumentError::IncomingConflict)
        ));
        let unsigned_origin = tb_core::OriginId {
            replica_id: ORIGIN_B.replica_id,
            incarnation: [0xEE; 16],
        };
        assert!(matches!(
            store.applied_prefix(unsigned_origin),
            Err(DocumentError::IncomingConflict)
        ));
        drop(store);
        let reopened = target.ready_store();
        assert_eq!(reopened.applied_prefix(ORIGIN_B).unwrap(), 1);
        assert_eq!(reopened.applied_prefix(ORIGIN_C).unwrap(), 1);
        assert!(matches!(
            reopened.point_read(COLLECTION, DOCUMENT).unwrap().point(),
            tb_document::DocumentPoint::Conflict { siblings, .. } if siblings.len() == 2
        ));
        drop(reopened);
        target.exact_source_changes(&live);
        target.exact_source_changes(&tombstone);
        let manifest = target.manifest(DOCUMENT);
        let siblings: Vec<_> = manifest
            .siblings()
            .iter()
            .map(|sibling| (sibling.event.origin, sibling.kind))
            .collect();
        assert_eq!(
            siblings,
            vec![
                (ORIGIN_B, DocumentKind::Live),
                (ORIGIN_C, DocumentKind::Tombstone),
            ]
        );
        assert!(target.has_applied(live.event));
        assert!(target.has_applied(tombstone.event));
    }
}

#[test]
fn origin_gap_is_durable_only_then_exact_replay_and_duplicates_are_checked() {
    let source = received_event_fixture(ORIGIN_B);
    let source_store = source.ready_store();
    let first = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"one"),
        7,
    );
    let second = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"two"),
        8,
    );

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target.receive(&store, &second).expect("receive origin gap"),
        DocumentApplyOutcome::DurableOnly { .. }
    ));
    assert_eq!(store.durable_prefix(ORIGIN_B).unwrap().sequence, 0);
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 0);
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(second.event));

    let store = target.ready_store();
    assert_eq!(
        target.receive(&store, &first).expect("receive predecessor"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    assert_eq!(store.durable_prefix(ORIGIN_B).unwrap().sequence, 2);
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 1);
    assert_eq!(
        store
            .replay_next(ORIGIN_B)
            .expect("replay durable successor")
            .expect("gap successor exists"),
        DocumentApplyOutcome::Applied { sequence: 2 }
    );
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 2);
    drop(store);
    target.exact_source_changes(&first);
    target.exact_source_changes(&second);

    let store = target.ready_store();
    assert_eq!(
        target.receive(&store, &first).expect("exact duplicate"),
        DocumentApplyOutcome::Duplicate { sequence: 1 }
    );
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 2);

    let collision_source = received_event_fixture(ORIGIN_B);
    let collision_store = collision_source.ready_store();
    let changed_first = collision_source.commit(
        &collision_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"different bytes for the same origin slot"),
        9,
    );
    assert_eq!(changed_first.event, first.event);
    assert!(matches!(
        target.receive(&store, &changed_first),
        Err(DocumentError::IncomingConflict)
    ));
    drop(store);
    assert!(target.has_applied(first.event));
    assert!(target.has_applied(second.event));
    let store = target.ready_store();
    assert_eq!(
        target
            .receive(&store, &first)
            .expect("healthy exact duplicate after conflict"),
        DocumentApplyOutcome::Duplicate { sequence: 1 }
    );
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 2);
}

#[test]
fn pre_reconciliation_receipt_is_durable_only_until_all_inboxes_are_coupled() {
    let source = Fixture::new(ORIGIN_B);
    let source_store = source.ready_store();
    let event = source.commit(
        &source_store,
        DOCUMENT,
        &CausalContext::new(),
        Some(b"pending"),
        21,
    );
    let successor_document = DocumentId::from_bytes([19; 16]);
    let successor = source.commit(
        &source_store,
        successor_document,
        &CausalContext::new(),
        Some(b"successor"),
        22,
    );

    let target = Fixture::new(ORIGIN_A);
    let store = target.open();
    assert!(matches!(
        store.durable_prefix(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    assert!(matches!(
        store.applied_prefix(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    assert!(matches!(
        store.point_read(COLLECTION, DOCUMENT),
        Err(DocumentError::NeedsRecovery)
    ));
    store
        .install_receivers(target.receivers())
        .expect("install complete signed inbox set");
    assert!(matches!(
        target
            .receive(&store, &successor)
            .expect("durable origin gap before reconciliation"),
        DocumentApplyOutcome::DurableOnly { durable_prefix: 0 }
    ));
    assert!(matches!(
        target
            .receive(&store, &event)
            .expect("durable-only before reconciliation"),
        DocumentApplyOutcome::DurableOnly { durable_prefix: 2 }
    ));
    assert_eq!(store.durable_prefix(ORIGIN_B).unwrap().sequence, 2);
    assert!(matches!(
        store.applied_prefix(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    assert!(matches!(
        store.point_read(COLLECTION, DOCUMENT),
        Err(DocumentError::NeedsRecovery)
    ));
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(event.event));
    assert!(!target.has_applied(successor.event));

    let store = target.open();
    store
        .install_receivers(target.receivers())
        .expect("install complete recovered inbox set");
    assert!(matches!(
        store.replay_next(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));
    assert!(matches!(
        store.applied_prefix(ORIGIN_B),
        Err(DocumentError::NeedsRecovery)
    ));

    store
        .reconcile_inboxes()
        .expect("couple the complete recovered inbox set");
    assert_eq!(store.durable_prefix(ORIGIN_B).unwrap().sequence, 2);
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 0);
    assert_eq!(
        store
            .replay_next(ORIGIN_B)
            .expect("replay after inbox reconciliation")
            .expect("durable pending event"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 1);
    assert_eq!(
        store
            .replay_next(ORIGIN_B)
            .expect("replay recovered successor")
            .expect("durable successor event"),
        DocumentApplyOutcome::Applied { sequence: 2 }
    );
    assert_eq!(store.applied_prefix(ORIGIN_B).unwrap(), 2);
    assert!(matches!(
        store.point_read(COLLECTION, DOCUMENT).unwrap().point(),
        tb_document::DocumentPoint::Value { sibling, .. } if sibling.event == event.event
    ));
    drop(store);
    let reopened = target.ready_store();
    assert_eq!(reopened.applied_prefix(ORIGIN_B).unwrap(), 2);
    assert!(matches!(
        reopened.point_read(COLLECTION, DOCUMENT).unwrap().point(),
        tb_document::DocumentPoint::Value { sibling, .. } if sibling.event == event.event
    ));
    assert!(matches!(
        reopened
            .point_read(COLLECTION, successor_document)
            .unwrap()
            .point(),
        tb_document::DocumentPoint::Value { sibling, .. } if sibling.event == successor.event
    ));
    drop(reopened);
    target.exact_source_changes(&event);
    target.exact_source_changes(&successor);
    assert!(target.has_applied(event.event));
    assert!(target.has_applied(successor.event));
}

#[test]
fn remote_and_local_dependencies_wait_for_their_applied_frontiers() {
    let b = received_event_fixture(ORIGIN_B);
    let b_store = b.ready_store();
    let remote_predecessor = b.commit(&b_store, DOCUMENT, &CausalContext::new(), Some(b"b"), 10);

    let c = received_event_fixture(ORIGIN_C);
    let c_store = c.ready_store();
    c.receive(&c_store, &remote_predecessor)
        .expect("C receives B's durable event");
    let remote_dependent = c.commit(
        &c_store,
        DOCUMENT,
        &context(&[(ORIGIN_B, 1)]),
        Some(b"c after b"),
        11,
    );

    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    assert!(matches!(
        target
            .receive(&store, &remote_dependent)
            .expect("defer remote dependency"),
        DocumentApplyOutcome::DurableOnly { .. }
    ));
    drop(store);
    assert!(!target.has_primary(DOCUMENT));
    assert!(!target.has_applied(remote_dependent.event));
    let store = target.ready_store();
    target
        .receive(&store, &remote_predecessor)
        .expect("apply remote predecessor");
    assert_eq!(
        store
            .replay_next(ORIGIN_C)
            .expect("replay after B D5 becomes contiguous")
            .expect("C event is durable"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(store);
    assert_eq!(target.manifest(DOCUMENT).siblings().len(), 1);
    assert_eq!(
        target.manifest(DOCUMENT).siblings()[0].event.origin,
        ORIGIN_C
    );

    let local_claim = Fixture::new(ORIGIN_B);
    let local_dependent =
        local_claim.v1_event(DOCUMENT, &[(ORIGIN_A, 1)], Some(b"after local"), 12);
    let deferred_target = Fixture::new(ORIGIN_A);
    let deferred_store = deferred_target.ready_store();
    assert!(matches!(
        deferred_target
            .receive(&deferred_store, &local_dependent)
            .expect("defer absent local D4 dependency"),
        DocumentApplyOutcome::DurableOnly { .. }
    ));
    drop(deferred_store);
    assert!(!deferred_target.has_primary(DOCUMENT));
    assert!(!deferred_target.has_applied(local_dependent.event));
    let deferred_store = deferred_target.ready_store();
    deferred_target.commit(
        &deferred_store,
        DocumentId::from_bytes([18; 16]),
        &CausalContext::new(),
        Some(b"local source D4"),
        13,
    );
    assert_eq!(
        deferred_store
            .replay_next(ORIGIN_B)
            .expect("replay after local D4 advances")
            .expect("local-dependent inbox event"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    );
    drop(deferred_store);
    assert!(deferred_target.has_applied(local_dependent.event));
}

#[test]
fn receiver_final_groups_are_skipped_by_local_source_resend() {
    let target = Fixture::new(ORIGIN_A);
    let store = target.ready_store();
    let local_source = target.commit(
        &store,
        DocumentId::from_bytes([20; 16]),
        &CausalContext::new(),
        Some(b"local"),
        18,
    );
    let remote = Fixture::new(ORIGIN_B);
    let remote_source = remote.v1_event(DocumentId::from_bytes([21; 16]), &[], Some(b"remote"), 19);
    target
        .receive(&store, &remote_source)
        .expect("apply remote final");

    let resend = store
        .next_source_after(0)
        .expect("scan source and receiver frames")
        .expect("local source event remains resendable");
    assert_eq!(resend.event, local_source.event);
    assert_eq!(resend.metadata(), local_source.metadata);
    assert_eq!(resend.wal_frame(), local_source.wal_frame);
    assert!(store.next_source_after(1).expect("resend end").is_none());
}

#[test]
fn source_high_water_tracks_logical_events_across_retry_receiver_and_reopen() {
    let target = Fixture::new(ORIGIN_A);
    let store = target.open();
    assert_eq!(store.source_high_water().expect("fresh source cursor"), 0);
    store
        .install_receivers(target.receivers())
        .expect("install signed inboxes");
    store.reconcile_inboxes().expect("reconcile fresh inboxes");
    assert!(store.next_source_after(0).expect("empty resend").is_none());

    let first_document = DocumentId::from_bytes([22; 16]);
    let first = target.commit(
        &store,
        first_document,
        &CausalContext::new(),
        Some(b"first"),
        31,
    );
    assert_eq!(first.event.sequence, 1);
    assert_eq!(store.source_high_water().expect("first source cursor"), 1);
    let retry = target.commit(
        &store,
        first_document,
        &CausalContext::new(),
        Some(b"first"),
        31,
    );
    assert_eq!(retry.event, first.event);
    assert_eq!(retry.metadata, first.metadata);
    assert_eq!(retry.wal_frame, first.wal_frame);
    assert_eq!(store.source_high_water().expect("retry source cursor"), 1);

    let remote = Fixture::new(ORIGIN_B);
    let remote_source = remote.v1_event(DocumentId::from_bytes([23; 16]), &[], Some(b"remote"), 32);
    assert!(matches!(
        target
            .receive(&store, &remote_source)
            .expect("apply receiver final"),
        DocumentApplyOutcome::Applied { sequence: 1 }
    ));
    assert_eq!(
        store.source_high_water().expect("receiver source cursor"),
        1
    );

    let second = target.commit(
        &store,
        DocumentId::from_bytes([24; 16]),
        &CausalContext::new(),
        Some(b"second"),
        33,
    );
    assert_eq!(second.event.sequence, 2);
    assert_eq!(store.source_high_water().expect("second source cursor"), 2);
    assert_eq!(
        store
            .next_source_after(0)
            .expect("first resend")
            .expect("first event")
            .event,
        first.event
    );
    assert_eq!(
        store
            .next_source_after(1)
            .expect("second resend")
            .expect("second event")
            .event,
        second.event
    );
    assert!(store.next_source_after(2).expect("resend end").is_none());
    drop(store);

    assert!(
        target.raw_storage().stats().visible.get() > 2,
        "catalog and receiver commits advance physical storage beyond local source events"
    );
    let reopened = target.open();
    assert!(matches!(
        reopened.source_high_water(),
        Err(DocumentError::NeedsRecovery)
    ));
    reopened
        .install_receivers(target.receivers())
        .expect("install recovered inboxes");
    reopened
        .reconcile_inboxes()
        .expect("reconcile recovered inboxes");
    assert_eq!(
        reopened
            .source_high_water()
            .expect("reopened source cursor"),
        2
    );
    assert!(
        reopened
            .next_source_after(2)
            .expect("reopened end")
            .is_none()
    );
}
