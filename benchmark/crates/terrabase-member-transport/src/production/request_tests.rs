use super::*;

fn pair() -> (Arc<ProductionQuicEndpoint>, Arc<ProductionQuicEndpoint>, MemberId) {
    let (first, second) = super::tests::endpoint_pair();
    let peer = second.config.local_identity.member().clone();
    (Arc::new(first), Arc::new(second), peer)
}

async fn queued(endpoint: &ProductionQuicEndpoint, count: usize) {
    tokio::time::timeout(Duration::from_secs(2), async {
        loop {
            let found = endpoint
                .state
                .request_usage
                .lock()
                .unwrap()
                .get(&ProductionConnectionClass::AuthClient)
                .copied()
                .unwrap_or_default()
                .0;
            if found == count {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("request queued");
}

fn waiter(
    endpoint: &Arc<ProductionQuicEndpoint>,
    peer: &MemberId,
) -> tokio::task::JoinHandle<Result<ProductionRequestPermit, ProductionTransportError>> {
    let endpoint = Arc::clone(endpoint);
    let peer = peer.clone();
    tokio::spawn(async move {
        endpoint
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                100,
                Duration::from_secs(5),
            )
            .await
    })
}

#[tokio::test]
async fn request_gate_serializes_one_peer_and_class_fifo() {
    let (first, _second, peer) = pair();
    let active = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let next = waiter(&first, &peer);
    queued(&first, 2).await;
    let last = waiter(&first, &peer);
    queued(&first, 3).await;
    assert!(!next.is_finished() && !last.is_finished());
    drop(active);
    let next = next.await.unwrap().unwrap();
    assert!(!last.is_finished());
    drop(next);
    drop(last.await.unwrap().unwrap());
    queued(&first, 0).await;
}

#[tokio::test]
async fn request_gate_classes_are_independent_and_validate_peers() {
    let (first, _second, peer) = pair();
    let client = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let consensus = first
        .acquire_request(
            &peer,
            ProductionConnectionClass::AuthConsensus,
            100,
            Duration::from_secs(1),
        )
        .await
        .unwrap();
    assert!(matches!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::Replication,
                100,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::InvalidConfiguration)
    ));
    let unknown = MemberId::try_new("member-unknown").unwrap();
    assert!(matches!(
        first
            .acquire_request(
                &unknown,
                ProductionConnectionClass::AuthClient,
                100,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::PeerNotAdmitted)
    ));
    assert_eq!(first.state.request_gates.lock().unwrap().len(), 2);
    drop((client, consensus));
}

#[tokio::test]
async fn queued_request_cancellation_and_deadline_release_budget() {
    let (first, _second, peer) = pair();
    let active = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let waiting = waiter(&first, &peer);
    queued(&first, 2).await;
    waiting.abort();
    assert!(matches!(waiting.await, Err(error) if error.is_cancelled()));
    queued(&first, 1).await;
    assert!(matches!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                100,
                Duration::from_millis(10)
            )
            .await,
        Err(ProductionTransportError::Timeout)
    ));
    assert_eq!(
        first.state.request_usage.lock().unwrap()[&ProductionConnectionClass::AuthClient],
        (1, 100)
    );
    drop(active);
    queued(&first, 0).await;
}

#[tokio::test]
async fn request_gate_rejects_count_and_byte_overflow() {
    let (first, _second, peer) = pair();
    let active = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let mut waiting = Vec::new();
    for count in 2..=4 {
        waiting.push(waiter(&first, &peer));
        queued(&first, count).await;
    }
    assert!(matches!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                100,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::QueueFull)
    ));
    for task in waiting {
        task.abort();
        let _ = task.await;
    }
    drop(active);
    let bytes = first.config.limits.max_queued_bytes / 2;
    let active = first
        .acquire_request(
            &peer,
            ProductionConnectionClass::AuthClient,
            bytes,
            Duration::from_secs(1),
        )
        .await
        .unwrap();
    assert!(matches!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                1,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::QueueFull)
    ));
    drop(active);
    assert_eq!(
        first.state.request_usage.lock().unwrap()[&ProductionConnectionClass::AuthClient],
        (0, 0)
    );
}

#[tokio::test]
async fn completed_roundtrips_release_remote_slot_before_successor() {
    let (first, second, peer) = pair();
    let server = tokio::spawn(async move {
        for _ in 0..20 {
            let connection = second.accept().await.unwrap().unwrap();
            assert_eq!(connection.receive_frame().await.unwrap(), b"request");
            connection.send_frame(b"response").await.unwrap();
            drop(connection);
        }
    });
    for _ in 0..20 {
        let _permit = first
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                7,
                Duration::from_secs(1),
            )
            .await
            .unwrap();
        let connection = first.connect(&peer, ProductionConnectionClass::AuthClient).await.unwrap();
        connection.send_frame(b"request").await.unwrap();
        assert_eq!(connection.receive_frame().await.unwrap(), b"response");
        tokio::time::timeout(Duration::from_secs(1), connection.complete_request())
            .await
            .unwrap()
            .unwrap();
    }
    server.await.unwrap();
}

#[tokio::test]
async fn drain_wakes_queued_requests() {
    let (first, _second, peer) = pair();
    let active = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let waiting = waiter(&first, &peer);
    queued(&first, 2).await;
    first.state.stop_requests();
    assert!(matches!(waiting.await.unwrap(), Err(ProductionTransportError::Draining)));
    drop(active);
    queued(&first, 0).await;
}

#[tokio::test]
async fn drained_endpoint_rejects_retained_shared_owners() {
    let (first, _second, peer) = pair();
    let retained = Arc::clone(&first);
    let report = first.drain().await;
    assert!(report.operations_drained);
    assert_eq!(report.remaining_operations, 0);
    assert!(matches!(
        retained
            .connect_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                100,
                Duration::from_secs(1),
            )
            .await,
        Err(ProductionTransportError::Draining)
    ));
    assert!(matches!(retained.accept().await, Ok(None)));
}

#[tokio::test]
async fn cancelled_active_request_allows_successor_without_replaying_payload() {
    let (first, second, peer) = pair();
    let (seen_tx, seen_rx) = tokio::sync::oneshot::channel();
    let server = tokio::spawn(async move {
        let previous = second.accept().await.unwrap().unwrap();
        assert_eq!(previous.receive_frame().await.unwrap(), b"cancelled");
        seen_tx.send(()).unwrap();
        let successor = loop {
            match second.accept().await {
                Ok(Some(connection)) => break connection,
                Err(
                    ProductionTransportError::Saturated
                    | ProductionTransportError::ConnectionFailed,
                ) => {}
                other => panic!("unexpected accept result: {}", other.is_ok()),
            }
        };
        assert_eq!(successor.receive_frame().await.unwrap(), b"successor");
        successor.send_frame(b"response").await.unwrap();
        drop(successor);
        drop(previous);
    });
    let endpoint = Arc::clone(&first);
    let target = peer.clone();
    let previous = tokio::spawn(async move {
        let (_permit, connection) = endpoint
            .connect_request(
                &target,
                ProductionConnectionClass::AuthClient,
                9,
                Duration::from_secs(2),
            )
            .await
            .unwrap();
        connection.send_frame(b"cancelled").await.unwrap();
        let _ = connection.receive_frame().await;
    });
    seen_rx.await.unwrap();
    previous.abort();
    let _ = previous.await;
    let (_permit, connection) = first
        .connect_request(&peer, ProductionConnectionClass::AuthClient, 9, Duration::from_secs(2))
        .await
        .unwrap();
    connection.send_frame(b"successor").await.unwrap();
    assert_eq!(connection.receive_frame().await.unwrap(), b"response");
    tokio::time::timeout(Duration::from_secs(2), connection.complete_request())
        .await
        .unwrap()
        .unwrap();
    server.await.unwrap();
}

#[tokio::test]
async fn request_connect_retries_occupied_slot_before_sending_payload() {
    let (first, second, peer) = pair();
    let blocked = first
        .state
        .reserve_session(&peer, ProductionConnectionClass::AuthClient, SessionDirection::Outbound)
        .unwrap();
    let connecting = first.connect_request(
        &peer,
        ProductionConnectionClass::AuthClient,
        7,
        Duration::from_secs(2),
    );
    tokio::pin!(connecting);
    std::future::poll_fn(|context| {
        use std::future::Future;
        assert!(connecting.as_mut().poll(context).is_pending());
        std::task::Poll::Ready(())
    })
    .await;
    assert_eq!(first.state.request_count(), 1);
    assert_eq!(first.facts().inflight_operations, 0);
    drop(blocked);
    let server = tokio::spawn(async move {
        let connection = second.accept().await.unwrap().unwrap();
        assert_eq!(connection.receive_frame().await.unwrap(), b"request");
        connection.send_frame(b"response").await.unwrap();
    });
    let (_permit, connection) = connecting.await.unwrap();
    connection.send_frame(b"request").await.unwrap();
    assert_eq!(connection.receive_frame().await.unwrap(), b"response");
    tokio::time::timeout(Duration::from_secs(2), connection.complete_request())
        .await
        .unwrap()
        .unwrap();
    server.await.unwrap();
}
