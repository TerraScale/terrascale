use super::*;

use std::collections::BTreeMap;
use std::sync::Arc;
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
        TenantUlid::from_text(&value).unwrap(),
        ProjectUlid::from_text(&value).unwrap(),
        DatabaseUlid::from_text(&value).unwrap(),
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
                live_workers: live_workers.clone(),
                progress: progress.clone(),
                observations: observations.clone(),
            },
        );
    }
    let (stop, _) = watch::channel(false);
    Synthetic {
        peers: ReplicationPeers {
            cuts: Arc::new(ReplicationCuts::default()),
            progress: progress.clone(),
            admissions,
            admitted: std::sync::Mutex::new(std::collections::BTreeSet::new()),
            observations: observations.clone(),
            live_workers,
            stop,
            join: tokio::spawn(async { Ok(()) }),
        },
        receivers,
        progress,
        observations,
    }
}

fn request(a: DatabaseRouteScope, b: DatabaseRouteScope) -> BTreeMap<String, Vec<PeerDatabase>> {
    BTreeMap::from([("peer-a".into(), vec![lane(a)]), ("peer-b".into(), vec![lane(b)])])
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
                request.selected_regions.insert(b, Default::default());
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
    synthetic
        .observations
        .lock()
        .unwrap()
        .insert(("peer-a".into(), a), PeerFacts { connected: true, ..PeerFacts::default() });
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
    assert!(synthetic.progress.selected_regions(a).is_err());
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
