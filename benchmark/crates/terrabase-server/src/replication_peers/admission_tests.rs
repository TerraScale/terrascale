use super::*;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::time::Duration;
use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};

fn scope(seed: u8) -> DatabaseRouteScope {
    let value = match seed {
        1 => "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        2 => "01ARZ3NDEKTSV4RRFFQ69G5FAW",
        3 => "01ARZ3NDEKTSV4RRFFQ69G5FAX",
        4 => "01ARZ3NDEKTSV4RRFFQ69G5FAY",
        5 => "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
        6 => "01ARZ3NDEKTSV4RRFFQ69G5FB0",
        _ => "01ARZ3NDEKTSV4RRFFQ69G5FB1",
    };
    DatabaseRouteScope::new(
        TenantUlid::from_text(value).unwrap(),
        ProjectUlid::from_text(value).unwrap(),
        DatabaseUlid::from_text(value).unwrap(),
    )
}

fn lane(scope: DatabaseRouteScope) -> PeerDatabase {
    PeerDatabase { scope, physical: None, causal: None }
}

struct Synthetic {
    peers: ReplicationPeers,
    receivers: BTreeMap<String, PeerInbox>,
    progress: Arc<ReplicationProgress>,
    observations: PeerObservations,
}

fn synthetic() -> Synthetic {
    let mut admissions = BTreeMap::new();
    let mut receivers = BTreeMap::new();
    let progress = Arc::new(ReplicationProgress::default());
    let observations: PeerObservations = Arc::default();
    let live_workers = Arc::new(std::sync::Mutex::new(
        ["peer-a".to_owned(), "peer-b".to_owned()].into_iter().collect(),
    ));
    for peer in ["peer-a", "peer-b"] {
        let (sender, receiver) = mpsc::channel(1);
        admissions.insert(peer.to_owned(), sender);
        receivers.insert(
            peer.to_owned(),
            PeerInbox {
                receiver: Some(receiver),
                peer: peer.to_owned(),
                worker_id: 1,
                live_workers: live_workers.clone(),
                progress: progress.clone(),
                observations: observations.clone(),
                retirement: None,
                suspended: BTreeSet::default(),
            },
        );
    }
    let (stop, _) = watch::channel(false);
    let join = tokio::spawn(async { Ok(()) });
    Synthetic {
        peers: ReplicationPeers {
            retention: Arc::new(ReplicationRetentionFrontier::default()),
            cuts: Arc::new(ReplicationCuts::default()),
            progress: progress.clone(),
            admissions,
            suspensions: std::sync::Mutex::default(),
            suspension_operation: tokio::sync::Mutex::default(),
            admitted: std::sync::Mutex::new(std::collections::BTreeSet::new()),
            observations: observations.clone(),
            live_workers,
            stop,
            draining: std::sync::atomic::AtomicBool::new(false),
            worker_status: join.abort_handle(),
            join: tokio::sync::Mutex::new(Some(join)),
            drain_result: std::sync::Mutex::new(None),
            retiring: std::sync::atomic::AtomicBool::new(false),
            retirement_result: std::sync::Mutex::new(None),
        },
        receivers,
        progress,
        observations,
    }
}

fn request(a: DatabaseRouteScope, b: DatabaseRouteScope) -> BTreeMap<String, Vec<PeerDatabase>> {
    BTreeMap::from([("peer-a".into(), vec![lane(a)]), ("peer-b".into(), vec![lane(b)])])
}

pub(crate) fn failed_peer_owner(error: &'static str) -> ReplicationPeers {
    let mut synthetic = synthetic();
    let join = tokio::spawn(async move { Err(error.to_owned()) });
    synthetic.peers.worker_status = join.abort_handle();
    synthetic.peers.join = tokio::sync::Mutex::new(Some(join));
    synthetic.peers
}

fn batch(lanes: BTreeMap<String, Vec<PeerDatabase>>) -> PeerDatabaseBatch {
    let selected_regions = lanes
        .values()
        .flat_map(|lanes| {
            lanes.iter().map(|lane| (lane.scope, std::collections::BTreeSet::from(["GRU".into()])))
        })
        .collect();
    PeerDatabaseBatch { lanes, selected_regions }
}

#[test]
fn peer_lane_validation_borrows_assignments_before_they_are_moved() {
    use crate::production::tests::{opened_bootstrap, transport_pair};

    let (bootstrap, root, _) = opened_bootstrap("replication-peer-validation");
    let parts = bootstrap.into_parts();
    let placement = parts.placement.clone();
    let (transport, remote) = transport_pair(&placement);
    let peer = transport.config().peers().next().unwrap().identity().member().clone();
    let runtime = Arc::new(
        ProductionMemberRuntime::try_new(placement, parts.routes, parts.member, transport).unwrap(),
    );
    let scope = scope(1);
    let peers = BTreeMap::from([(peer.as_str().to_owned(), vec![lane(scope)])]);

    validate_peer_lanes(&runtime, &peers).unwrap();
    assert_eq!(peers[peer.as_str()].len(), 1);
    assert_eq!(peers[peer.as_str()][0].scope, scope);

    drop(runtime);
    drop(remote);
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test]
async fn incomplete_or_conflicting_metadata_leaves_queues_and_progress_unchanged() {
    for mode in 0..3 {
        let mut state = synthetic();
        let a = scope(1);
        let b = scope(2);
        let mut request = batch(request(a, b));
        match mode {
            0 => {
                request.selected_regions.remove(&b);
            }
            1 => {
                request.selected_regions.insert(scope(3), ["GRU".into()].into_iter().collect());
            }
            _ => {
                request.selected_regions.insert(b, BTreeSet::default());
            }
        }
        assert!(state.peers.add_databases(request).await.is_err());
        assert!(state.progress.boundaries().unwrap().is_empty());
        assert!(state.peers.admitted.lock().unwrap().is_empty());
        assert!(state.observations.lock().unwrap().is_empty());
        for receiver in state.receivers.values_mut() {
            assert!(matches!(
                receiver.receiver.as_mut().unwrap().try_recv(),
                Err(mpsc::error::TryRecvError::Empty)
            ));
        }
    }
}

#[tokio::test]
async fn begin_drain_rejects_new_lanes_and_drain_still_joins_owner() {
    let synthetic = synthetic();
    synthetic.peers.begin_drain();
    assert!(synthetic.peers.ensure_running().is_err());
    assert!(synthetic.peers.add_databases(batch(request(scope(1), scope(2)))).await.is_err());
    assert!(synthetic.progress.boundaries().unwrap().is_empty());
    assert!(synthetic.peers.drain().await.is_ok());
}

#[tokio::test]
async fn cancelled_drain_retains_join_for_retry_and_caches_failure() {
    let mut synthetic = synthetic();
    let pending = tokio::spawn(async { std::future::pending::<Result<(), String>>().await });
    synthetic.peers.worker_status = pending.abort_handle();
    *synthetic.peers.join.lock().await = Some(pending);
    let timed_out = tokio::time::timeout(Duration::from_millis(1), synthetic.peers.drain()).await;
    assert!(timed_out.is_err());
    assert!(synthetic.peers.join.lock().await.is_some());
    assert!(!synthetic.peers.drain_completed());
    synthetic.peers.join.lock().await.as_ref().unwrap().abort();
    let first = synthetic.peers.drain().await;
    let second = synthetic.peers.drain().await;
    assert!(first.is_err());
    assert_eq!(first, second);
    assert!(synthetic.peers.drain_completed());
}

#[tokio::test]
async fn concurrent_drains_share_cached_completion() {
    let synthetic = synthetic();
    let (first, second) = tokio::join!(synthetic.peers.drain(), synthetic.peers.drain());
    assert_eq!(first, second);
    assert!(first.is_ok());
}

#[tokio::test]
async fn closed_later_queue_leaves_earlier_queue_and_admission_unchanged() {
    let mut synthetic = synthetic();
    let a = scope(1);
    let b = scope(2);
    drop(synthetic.receivers.remove("peer-b").unwrap());

    assert!(synthetic.peers.add_databases(batch(request(a, b))).await.is_err());
    assert!(matches!(
        synthetic.receivers.get_mut("peer-a").unwrap().receiver.as_mut().unwrap().try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
    assert!(synthetic.peers.admitted.lock().unwrap().is_empty());
    assert!(synthetic.peers.observations().lock().unwrap().is_empty());
    assert!(synthetic.progress.boundaries().unwrap().is_empty());
}

#[tokio::test]
async fn receiver_closed_after_reservation_cannot_publish_partial_admission() {
    let mut synthetic = synthetic();
    let a = scope(1);
    let b = scope(2);
    synthetic
        .peers
        .admissions
        .get("peer-b")
        .unwrap()
        .try_send(PeerCommand::Add(Vec::new()))
        .unwrap();
    synthetic.progress.admit(a, std::collections::BTreeSet::from(["GRU".into()])).unwrap();
    synthetic.observations.lock().unwrap().insert(
        ("peer-a".into(), a),
        PeerFacts { connected: true, session_id: 1, ..PeerFacts::default() },
    );
    let add = synthetic.peers.add_databases(batch(request(a, b)));
    tokio::pin!(add);
    for _ in 0..100 {
        if synthetic.peers.admissions.get("peer-a").unwrap().capacity() == 0 {
            break;
        }
        tokio::select! { result = &mut add => panic!("admission completed before A reservation: {result:?}"), () = tokio::task::yield_now() => {} }
    }
    assert_eq!(synthetic.peers.admissions.get("peer-a").unwrap().capacity(), 0);
    drop(synthetic.receivers.remove("peer-a").unwrap());
    assert!(
        matches!(synthetic.receivers.get_mut("peer-b").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.is_empty())
    );
    let result = add.await;
    assert!(result.is_err());
    assert!(matches!(
        synthetic.receivers.get_mut("peer-b").unwrap().receiver.as_mut().unwrap().try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
    assert!(synthetic.peers.admitted.lock().unwrap().is_empty());
    assert!(synthetic.peers.observations().lock().unwrap().contains_key(&("peer-a".into(), a)));
    assert!(synthetic.progress.selected_regions(a).is_ok());
    assert!(!synthetic.progress.boundaries().unwrap().contains_key(&b));
    assert!(!synthetic.observations.lock().unwrap().get(&("peer-a".into(), a)).unwrap().connected);
}

#[tokio::test]
async fn cancelled_reservation_releases_prior_queue_capacity_for_retry() {
    let mut synthetic = synthetic();
    let a = scope(3);
    let b = scope(4);
    synthetic
        .peers
        .admissions
        .get("peer-b")
        .unwrap()
        .try_send(PeerCommand::Add(Vec::new()))
        .unwrap();

    let result = tokio::time::timeout(
        std::time::Duration::from_millis(10),
        synthetic.peers.add_databases(batch(request(a, b))),
    )
    .await;
    assert!(result.is_err());
    assert!(matches!(
        synthetic.receivers.get_mut("peer-a").unwrap().receiver.as_mut().unwrap().try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
    assert!(synthetic.peers.admitted.lock().unwrap().is_empty());
    assert!(synthetic.peers.observations().lock().unwrap().is_empty());
    assert!(synthetic.progress.boundaries().unwrap().is_empty());

    assert!(
        matches!(synthetic.receivers.get_mut("peer-b").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.is_empty())
    );
    synthetic.peers.add_databases(batch(request(a, b))).await.unwrap();
    assert!(synthetic.progress.selected_regions(a).is_ok());
    assert!(synthetic.progress.selected_regions(b).is_ok());
    assert!(
        matches!(synthetic.receivers.get_mut("peer-a").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.len() == 1)
    );
    assert!(
        matches!(synthetic.receivers.get_mut("peer-b").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.len() == 1)
    );
}

#[tokio::test]
async fn successful_admission_sends_each_lane_once_and_duplicate_is_atomic() {
    let mut synthetic = synthetic();
    let a = scope(5);
    let b = scope(6);
    let c = scope(7);
    let peers = BTreeMap::from([
        ("peer-a".into(), vec![lane(a), lane(b)]),
        ("peer-b".into(), vec![lane(c)]),
    ]);
    synthetic.peers.add_databases(batch(peers)).await.unwrap();
    assert!(
        matches!(synthetic.receivers.get_mut("peer-a").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.len() == 2)
    );
    assert!(
        matches!(synthetic.receivers.get_mut("peer-b").unwrap().recv().await, Some(PeerCommand::Add(lanes)) if lanes.len() == 1)
    );
    assert_eq!(synthetic.peers.admitted.lock().unwrap().len(), 3);
    let observations = synthetic.peers.observations().lock().unwrap().clone();
    assert_eq!(observations.len(), 3);

    let duplicate = BTreeMap::from([
        ("peer-a".into(), vec![lane(a), lane(b)]),
        ("peer-b".into(), vec![lane(c)]),
    ]);
    assert!(synthetic.peers.add_databases(batch(duplicate)).await.is_err());
    assert!(matches!(
        synthetic.receivers.get_mut("peer-a").unwrap().receiver.as_mut().unwrap().try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
    assert!(matches!(
        synthetic.receivers.get_mut("peer-b").unwrap().receiver.as_mut().unwrap().try_recv(),
        Err(mpsc::error::TryRecvError::Empty)
    ));
    assert_eq!(synthetic.peers.admitted.lock().unwrap().len(), 3);
    let after = synthetic.observations.lock().unwrap();
    assert_eq!(after.keys().collect::<Vec<_>>(), observations.keys().collect::<Vec<_>>());
    assert!(after.values().all(|facts| !facts.connected));
}

#[tokio::test]
async fn drain_rejects_admission_already_waiting_for_queue_capacity() {
    let mut synthetic = synthetic();
    synthetic.peers.admissions["peer-b"].try_send(PeerCommand::Add(Vec::new())).unwrap();
    {
        let add = synthetic.peers.add_databases(batch(request(scope(1), scope(2))));
        tokio::pin!(add);
        for _ in 0..100 {
            if synthetic.peers.admissions["peer-a"].capacity() == 0 {
                break;
            }
            tokio::select! {
                result = &mut add => panic!("admission completed before reservation: {result:?}"),
                () = tokio::task::yield_now() => {}
            }
        }
        assert_eq!(synthetic.peers.admissions["peer-a"].capacity(), 0);
        synthetic.peers.begin_drain();
        synthetic.peers.begin_drain();
        assert!(synthetic.receivers.get_mut("peer-b").unwrap().recv().await.is_some());
        assert!(add.await.is_err());
    }
    assert!(synthetic.progress.boundaries().unwrap().is_empty());
    assert!(synthetic.peers.admitted.lock().unwrap().is_empty());
    for receiver in synthetic.receivers.values_mut() {
        assert!(matches!(
            receiver.receiver.as_mut().unwrap().try_recv(),
            Err(mpsc::error::TryRecvError::Empty)
        ));
    }
    synthetic.peers.drain().await.unwrap();
}

#[tokio::test]
async fn real_worker_suspension_releases_engine_and_rebinds_same_assignment() {
    use crate::production::tests::{AcceptedMembership, Writer, opened_bootstrap, transport_pair};
    use terrabase_cluster::wal_driver::WalEngineHandle;
    use terrabase_cluster::wal_follower::WalTail;
    let (bootstrap, root, spec) = opened_bootstrap("replication-suspend");
    let parts = bootstrap.into_parts();
    let database = parts.member.database_ids().next().unwrap();
    parts.member.accept_database_role(&database, &Writer).unwrap();
    let placement = parts.placement.clone();
    let (transport, remote) = transport_pair(&placement);
    let config = transport.config();
    let local_identity = config.local_identity().clone();
    let peer = config.peers().next().unwrap().identity().clone();
    let database_scope = spec.scope().unwrap();
    let scope = DatabaseRouteScope::new(
        database_scope.tenant(),
        database_scope.project(),
        database_scope.database(),
    );
    let binding =
        WalReplicaBinding::new(scope, &placement, local_identity.clone(), peer.clone()).unwrap();
    let make_lane = |lease: Arc<dyn WalEngineHandle>| PeerDatabase {
        scope,
        physical: Some(PhysicalDriver::Writer(WalWriterDriver::new(
            binding.clone(),
            lease,
            WalTail::new(root.join("unused-tail")),
        ))),
        causal: None,
    };
    let member = parts.member.clone();
    let runtime = Arc::new(
        ProductionMemberRuntime::try_new(placement.clone(), parts.routes, parts.member, transport)
            .unwrap(),
    );
    runtime.admit_membership(&AcceptedMembership(placement)).unwrap();
    let lease: Arc<dyn WalEngineHandle> = Arc::new(runtime.acquire_replication(&scope).unwrap());
    let retained = Arc::downgrade(&lease);
    let progress = Arc::new(ReplicationProgress::default());
    let batch = |lane| PeerDatabaseBatch {
        lanes: BTreeMap::from([(peer.member().as_str().to_owned(), vec![lane])]),
        selected_regions: BTreeMap::from([(scope, BTreeSet::from(["GRU".into()]))]),
    };
    let peers = ReplicationPeers::start(runtime.clone(), progress.clone(), batch(make_lane(lease)))
        .unwrap();
    assert!(retained.upgrade().is_some());
    let token = peers.suspend_database(scope).await.unwrap();
    assert!(retained.upgrade().is_none(), "worker retained the old engine lease after its barrier");
    assert!(peers.pending_suspension(scope).unwrap().is_some());
    assert_eq!(progress.selected_regions(scope).unwrap(), BTreeSet::from(["GRU".into()]));
    let retry = peers.suspend_database(scope).await.unwrap();
    let rebound: Arc<dyn WalEngineHandle> = Arc::new(runtime.acquire_replication(&scope).unwrap());
    let rebound_retained = Arc::downgrade(&rebound);
    peers.resume_database(&retry, batch(make_lane(rebound))).await.unwrap();
    assert!(peers.pending_suspension(scope).unwrap().is_none());
    let stale: Arc<dyn WalEngineHandle> = Arc::new(runtime.acquire_replication(&scope).unwrap());
    assert!(peers.resume_database(&token, batch(make_lane(stale))).await.is_err());
    peers.suspend_database(scope).await.unwrap();
    assert!(rebound_retained.upgrade().is_none());
    peers.drain().await.unwrap();
    drop(peers);
    drop(remote);
    drop(runtime);
    drop(member);
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test]
async fn real_session_io_failure_fences_old_session_before_restart() {
    use crate::production::tests::{AcceptedMembership, Writer, opened_bootstrap, transport_pair};
    use terrabase_cluster::wal_driver::{
        WalAuthenticatedPeer, WalCheckpointMessage, WalEngineHandle, WalFollowerDriver,
    };
    use terrabase_cluster::wal_follower::{WalFollower, WalTail};

    let (bootstrap, root, spec) = opened_bootstrap("replication-session-restart");
    let parts = bootstrap.into_parts();
    let database = parts.member.database_ids().next().unwrap();
    parts.member.accept_database_role(&database, &Writer).unwrap();
    let placement = parts.placement.clone();
    let (transport, remote) = transport_pair(&placement);
    let config = transport.config();
    let local_identity = config.local_identity().clone();
    let peer = config.peers().next().unwrap().identity().clone();
    let remote_config = remote.config();
    let database_scope = spec.scope().unwrap();
    let scope = DatabaseRouteScope::new(
        database_scope.tenant(),
        database_scope.project(),
        database_scope.database(),
    );
    let binding =
        WalReplicaBinding::new(scope, &placement, config.local_identity().clone(), peer.clone())
            .unwrap();
    let member = parts.member.clone();
    let runtime = Arc::new(
        ProductionMemberRuntime::try_new(placement.clone(), parts.routes, parts.member, transport)
            .unwrap(),
    );
    runtime.admit_membership(&AcceptedMembership(placement.clone())).unwrap();
    let database_lease: Arc<dyn WalEngineHandle> =
        Arc::new(runtime.acquire_replication(&scope).unwrap());
    let lane = PeerDatabase {
        scope,
        physical: Some(PhysicalDriver::Writer(WalWriterDriver::new(
            binding,
            database_lease,
            WalTail::new(root.join("unused-tail")),
        ))),
        causal: None,
    };
    let progress = Arc::new(ReplicationProgress::default());
    let peers = ReplicationPeers::start(
        runtime.clone(),
        progress.clone(),
        PeerDatabaseBatch {
            lanes: BTreeMap::from([(peer.member().as_str().to_owned(), vec![lane])]),
            selected_regions: BTreeMap::from([(scope, BTreeSet::from(["GRU".into()]))]),
        },
    )
    .unwrap();
    let observations = peers.observations();
    // The follower must authorize from its own local placement view.
    let follower_placement = terrabase_cluster::production::ProductionPlacement::try_new(
        terrabase::identity::MemberUlid::from_text(
            remote_config.local_identity().member().as_str(),
        )
        .unwrap(),
        placement.generation(),
        placement.epoch(),
        placement.regions(),
    )
    .unwrap();
    let follower_binding = WalReplicaBinding::new(
        scope,
        &follower_placement,
        local_identity,
        remote_config.local_identity().clone(),
    )
    .unwrap();
    let follower_engine = runtime.acquire_replication(&scope).unwrap();
    let (follower_replica, _) =
        WalFollower::bootstrap_engine(follower_engine.engine(), &root.join("real-follower"))
            .unwrap();
    let mut follower = WalFollowerDriver::new(follower_binding, follower_replica);
    let engine = follower_engine.engine();
    let tenant =
        engine.scope_for(&terrabase::types::TenantId::try_new(&scope.tenant().to_text()).unwrap());
    let commit = || {
        use terrabase::types::*;
        let snapshot = engine.read_snapshot(&tenant).unwrap();
        let mut transaction =
            engine.begin(&tenant, snapshot, OperationId::try_new("lost-wal-ack").unwrap()).unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("doc").unwrap(),
                    CollectionName::try_new("items").unwrap(),
                    DocumentValue::Null,
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        match transaction.commit(LocalDurability::Synced) {
            terrabase::CommitOutcome::Committed(receipt) => receipt,
            outcome => panic!("unexpected commit: {outcome:?}"),
        }
    };
    let before = engine.read_snapshot(&tenant).unwrap().sequence();
    let receipt = commit();
    let (applied_tx, applied_rx) = tokio::sync::oneshot::channel();
    let (disconnect_tx, disconnect_rx) = tokio::sync::oneshot::channel();
    let (recovered_tx, recovered_rx) = tokio::sync::oneshot::channel();
    let (stop_tx, stop_rx) = tokio::sync::oneshot::channel();
    let remote_task = tokio::spawn(async move {
        let connection = remote.accept().await.unwrap().unwrap();
        let authenticated = WalAuthenticatedPeer::from_connection(&connection);
        loop {
            let frame = connection.receive_frame().await.unwrap();
            if frame.starts_with(b"TBWCP") {
                let response = follower
                    .checkpoint(&authenticated, WalCheckpointMessage::decode(&frame).unwrap())
                    .await
                    .unwrap();
                connection.send_frame(&response.encode().unwrap()).await.unwrap();
            } else if frame.starts_with(b"TBWAL") {
                let ack = follower
                    .apply(&authenticated, ScopedWalBatch::decode(&frame).unwrap())
                    .await
                    .unwrap();
                assert!(ack.encode().unwrap().starts_with(b"TBWACK"));
                // The disk sync completed, but its acknowledgement never reaches the writer.
                applied_tx.send(()).unwrap();
                disconnect_rx.await.unwrap();
                drop(connection);
                break;
            }
        }
        let connection = remote.accept().await.unwrap().unwrap();
        let authenticated = WalAuthenticatedPeer::from_connection(&connection);
        loop {
            let frame = connection.receive_frame().await.unwrap();
            if frame.starts_with(b"TBWCP") {
                let response = follower
                    .checkpoint(&authenticated, WalCheckpointMessage::decode(&frame).unwrap())
                    .await
                    .unwrap();
                connection.send_frame(&response.encode().unwrap()).await.unwrap();
                recovered_tx.send(()).unwrap();
                break;
            }
        }
        // Continue servicing real WAL replays while the replacement owner proves catch-up.
        tokio::pin!(stop_rx);
        loop {
            tokio::select! {
                _ = &mut stop_rx => break,
                frame = connection.receive_frame() => {
                    let frame = frame.unwrap();
                    if frame.starts_with(b"TBWAL") {
                        let ack = follower.apply(&authenticated,
                            ScopedWalBatch::decode(&frame).unwrap()).await.unwrap();
                        connection.send_frame(&ack.encode().unwrap()).await.unwrap();
                    }
                }
            }
        }
    });
    tokio::time::timeout(Duration::from_secs(5), applied_rx)
        .await
        .expect("real WAL reaches follower")
        .unwrap();
    let first = observations
        .lock()
        .unwrap()
        .get(&(peer.member().as_str().to_owned(), scope))
        .unwrap()
        .session_id;
    assert!(!progress.regional_covers(scope, receipt.sequence().get()).unwrap());
    assert_eq!(
        crate::api::confirm_regional_for_replication_test(
            engine,
            &tenant,
            &receipt,
            progress.clone(),
            scope,
            Duration::from_millis(20),
        )
        .await,
        Err("commit_unknown".to_owned())
    );
    disconnect_tx.send(()).unwrap();
    tokio::time::timeout(Duration::from_secs(5), recovered_rx)
        .await
        .expect("replacement receives durable follower checkpoint")
        .unwrap();
    let replay = commit();
    assert_eq!(receipt, replay, "retry must return the original operation receipt");
    crate::api::confirm_regional_for_replication_test(
        engine,
        &tenant,
        &replay,
        progress.clone(),
        scope,
        Duration::from_secs(5),
    )
    .await
    .unwrap();
    assert!(
        observations
            .lock()
            .unwrap()
            .get(&(peer.member().as_str().to_owned(), scope))
            .unwrap()
            .session_id
            > first
    );
    assert_eq!(engine.read_snapshot(&tenant).unwrap().sequence(), receipt.sequence());
    assert_eq!(
        engine.journal_rows(&tenant, before.get(), 10).unwrap().len(),
        1,
        "the same operation creates exactly one durable journal record"
    );
    stop_tx.send(()).unwrap();
    remote_task.await.unwrap();
    peers.drain().await.unwrap();
    drop(follower_engine);
    drop(runtime);
    drop(member);
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test]
async fn canceled_suspension_retries_without_retiring_or_removing_other_database() {
    let mut synthetic = synthetic();
    let target = scope(1);
    let other = scope(2);
    synthetic
        .peers
        .add_databases(PeerDatabaseBatch {
            lanes: BTreeMap::from([("peer-a".into(), vec![lane(target), lane(other)])]),
            selected_regions: BTreeMap::from([
                (target, BTreeSet::from(["GRU".into()])),
                (other, BTreeSet::from(["GRU".into()])),
            ]),
        })
        .await
        .unwrap();
    let mut inbox = synthetic.receivers.remove("peer-a").unwrap();
    let mut lanes = Vec::new();
    let command = inbox.recv().await.unwrap();
    apply_worker_command(command, &mut lanes, &synthetic.observations, "peer-a", &mut inbox);
    let first = {
        let suspend = synthetic.peers.suspend_database(target);
        tokio::pin!(suspend);
        tokio::select! {
            result = &mut suspend => panic!("barrier completed before worker drain: {result:?}"),
            command = inbox.recv() => command.unwrap(),
        }
        // Dropping the future models cancellation after command publication but
        // before the worker finishes its current disk callback.
    };
    assert!(synthetic.peers.pending_suspension(target).unwrap().is_some());
    assert!(
        synthetic
            .peers
            .add_databases(PeerDatabaseBatch {
                lanes: BTreeMap::from([("peer-b".into(), vec![lane(target)])]),
                selected_regions: BTreeMap::from([(target, BTreeSet::from(["GRU".into()]))]),
            })
            .await
            .is_err(),
        "a new peer must not reacquire a suspended database"
    );
    assert_eq!(lanes.len(), 2);
    apply_worker_command(first, &mut lanes, &synthetic.observations, "peer-a", &mut inbox);
    assert_eq!(lanes.iter().map(|lane| lane.scope).collect::<Vec<_>>(), vec![other]);
    let retry = synthetic.peers.suspend_database(target);
    tokio::pin!(retry);
    let token = tokio::select! {
        result = &mut retry => panic!("retry completed without worker: {result:?}"),
        command = inbox.recv() => {
            apply_worker_command(command.unwrap(), &mut lanes, &synthetic.observations, "peer-a", &mut inbox);
            retry.await.unwrap()
        }
    };
    synthetic
        .peers
        .resume_database(
            &token,
            PeerDatabaseBatch {
                lanes: BTreeMap::from([("peer-a".into(), vec![lane(target)])]),
                selected_regions: BTreeMap::from([(target, BTreeSet::from(["GRU".into()]))]),
            },
        )
        .await
        .unwrap();
    let command = inbox.recv().await.unwrap();
    apply_worker_command(command, &mut lanes, &synthetic.observations, "peer-a", &mut inbox);
    assert_eq!(
        lanes.iter().map(|lane| lane.scope).collect::<BTreeSet<_>>(),
        BTreeSet::from([target, other])
    );
    assert!(!inbox.suspended.contains(&target));
    assert!(synthetic.progress.selected_regions(target).is_ok());
    assert!(synthetic.progress.selected_regions(other).is_ok());
}
