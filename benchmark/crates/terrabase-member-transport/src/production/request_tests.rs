use std::sync::Arc;
use std::time::Duration;

use super::*;

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn request_gate_serializes_one_peer_and_class_fifo() {
    let (first, _second) = super::tests::endpoint_pair();
    let peer = first.config.peers().next().unwrap().identity().member().clone();
    let first_permit = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 32, Duration::from_secs(1))
        .await
        .unwrap();
    let waiting = {
        let endpoint = Arc::new(first);
        let endpoint_for_task = Arc::clone(&endpoint);
        tokio::spawn(async move {
            endpoint_for_task
                .acquire_request(
                    &peer,
                    ProductionConnectionClass::AuthClient,
                    32,
                    Duration::from_secs(1),
                )
                .await
        })
    };
    tokio::task::yield_now().await;
    drop(first_permit);
    assert!(waiting.await.unwrap().is_ok());
}

#[tokio::test]
async fn request_gate_classes_are_independent_and_validate_peers() {
    let (first, _second) = super::tests::endpoint_pair();
    let peer = first.config.peers().next().unwrap().identity().member().clone();
    let client = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 32, Duration::from_secs(1))
        .await
        .unwrap();
    let consensus = first
        .acquire_request(
            &peer,
            ProductionConnectionClass::AuthConsensus,
            32,
            Duration::from_secs(1),
        )
        .await
        .unwrap();
    assert!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::Replication,
                32,
                Duration::from_secs(1)
            )
            .await
            .is_err()
    );
    let unknown = MemberId::try_new("member-unknown").unwrap();
    assert!(matches!(
        first
            .acquire_request(
                &unknown,
                ProductionConnectionClass::AuthClient,
                32,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::PeerNotAdmitted)
    ));
    drop((client, consensus));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn queued_request_cancellation_releases_count_and_byte_budget() {
    let (first, _second) = super::tests::endpoint_pair();
    let peer = first.config.peers().next().unwrap().identity().member().clone();
    let active = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await
        .unwrap();
    let queued = first.acquire_request(
        &peer,
        ProductionConnectionClass::AuthClient,
        100,
        Duration::from_secs(10),
    );
    let task = tokio::spawn(queued);
    tokio::task::yield_now().await;
    task.abort();
    let _ = task.await;
    drop(active);
    let reclaimed = first
        .acquire_request(&peer, ProductionConnectionClass::AuthClient, 100, Duration::from_secs(1))
        .await;
    assert!(reclaimed.is_ok());
}

#[tokio::test]
async fn request_gate_rejects_count_and_byte_overflow() {
    let (first, _second) = super::tests::endpoint_pair();
    let peer = first.config.peers().next().unwrap().identity().member().clone();
    let (a, b, c, d) = tokio::join!(
        first.acquire_request(
            &peer,
            ProductionConnectionClass::AuthClient,
            100,
            Duration::from_secs(1)
        ),
        first.acquire_request(
            &peer,
            ProductionConnectionClass::AuthClient,
            100,
            Duration::from_secs(1)
        ),
        first.acquire_request(
            &peer,
            ProductionConnectionClass::AuthClient,
            100,
            Duration::from_secs(1)
        ),
        first.acquire_request(
            &peer,
            ProductionConnectionClass::AuthClient,
            100,
            Duration::from_secs(1)
        ),
    );
    let permits = [a.unwrap(), b.unwrap(), c.unwrap(), d.unwrap()];
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
    drop(permits);
    assert!(matches!(
        first
            .acquire_request(
                &peer,
                ProductionConnectionClass::AuthClient,
                3001,
                Duration::from_secs(1)
            )
            .await,
        Err(ProductionTransportError::QueueFull)
    ));
}
