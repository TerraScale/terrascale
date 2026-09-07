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
                suspended: Default::default(),
            },
        );
    }
    let (stop, _) = watch::channel(false);
    let join = tokio::spawn(async { Ok(()) });
    Synthetic {
        peers: ReplicationPeers {
            cuts: Arc::new(ReplicationCuts::default()),
            progress: progress.clone(),
            admissions,
            suspensions: Default::default(),
            suspension_operation: Default::default(),
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
    let peer = config.peers().next().unwrap().identity().clone();
    let database_scope = spec.scope().unwrap();
    let scope = DatabaseRouteScope::new(
        database_scope.tenant(),
        database_scope.project(),
        database_scope.database(),
    );
    let binding =
        WalReplicaBinding::new(scope, &placement, config.local_identity().clone(), peer.clone())
            .unwrap();
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
    use terrabase_cluster::wal_driver::WalEngineHandle;
    use terrabase_cluster::wal_follower::WalTail;

    let (bootstrap, root, spec) = opened_bootstrap("replication-session-restart");
    let parts = bootstrap.into_parts();
    let database = parts.member.database_ids().next().unwrap();
    parts.member.accept_database_role(&database, &Writer).unwrap();
    let placement = parts.placement.clone();
    let (transport, remote) = transport_pair(&placement);
    let config = transport.config();
    let peer = config.peers().next().unwrap().identity().clone();
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
    runtime.admit_membership(&AcceptedMembership(placement)).unwrap();
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
    progress.advance_regional_from_peer(scope, peer.member().as_str(), 8);
    assert!(progress.regional_covers(scope, 8).unwrap());
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
    let remote_task = tokio::spawn(async move {
        for index in 0..2 {
            let Some(connection) = remote.accept().await.unwrap() else { return };
            let _ = connection.receive_frame().await;
            let Some(checkpoint) = connection.receive_frame().await.ok() else { return };
            let request = WalCheckpointMessage::decode(&checkpoint).expect("encoded checkpoint");
            if index == 1 {
                // Echoing the request is deliberately not a valid response:
                // the writer must refuse it and keep readiness fenced until a
                // real follower checkpoint and readiness cut arrive.
                connection.send_frame(&request.encode().unwrap()).await.unwrap();
            }
            // Closing immediately after authentication forces the real session
            // owner through its transport-error fence and restart path.
            drop(connection);
        }
    });
    let first = tokio::time::timeout(Duration::from_secs(2), async {
        loop {
            if let Some(facts) =
                observations.lock().unwrap().get(&(peer.member().as_str().to_owned(), scope))
                && facts.connected
                && facts.session_id != 0
            {
                break facts.session_id;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("first authenticated session");
    assert!(progress.regional_covers(scope, 8).unwrap());
    let second = tokio::time::timeout(Duration::from_secs(3), async {
        loop {
            if let Some(facts) =
                observations.lock().unwrap().get(&(peer.member().as_str().to_owned(), scope))
                && facts.session_id > first
            {
                break facts.session_id;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("replacement session");
    assert!(second > first);
    assert!(progress.regional_covers(scope, 8).is_err(), "fenced evidence must not cover before a fresh response");
    let facts = observations.lock().unwrap();
    let facts = facts.get(&(peer.member().as_str().to_owned(), scope)).unwrap();
    assert!(facts.physical.is_none(), "closed session must not publish physical proof");
    assert!(facts.readiness.is_none(), "closed session must not publish readiness proof");
    drop(facts);
    assert!(peers.ensure_running().is_ok(), "terminal command path remains usable after session restart");
    peers.drain().await.unwrap();
    remote_task.await.unwrap();
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
