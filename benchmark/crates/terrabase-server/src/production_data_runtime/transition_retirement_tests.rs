use super::*;
use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair as _};
use base64::Engine as _;
use serde::Serialize;
use std::collections::BTreeMap;
use std::sync::Arc;

#[derive(Serialize)]
#[serde(deny_unknown_fields)]
struct SignedEnvelope {
    key_id: String,
    payload: String,
    signature: String,
}

#[derive(Serialize)]
#[serde(deny_unknown_fields)]
struct Member {
    identity: String,
    region: String,
    placement_digest: String,
}

#[derive(Serialize)]
#[serde(deny_unknown_fields)]
struct Request {
    operation_id: String,
    scope_id: String,
    prior_epoch: u64,
    epoch: u64,
    generation: u64,
    members: Vec<Member>,
}

fn key(seed: u8) -> Ed25519KeyPair {
    Ed25519KeyPair::from_seed_unchecked(&[seed; 32]).unwrap()
}

fn hash(bytes: &[u8]) -> String {
    let digest = aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, bytes);
    digest.as_ref().iter().map(|byte| format!("{byte:02x}")).collect()
}

fn signed(key_id: &str, key: &Ed25519KeyPair, payload: &[u8]) -> Vec<u8> {
    let mut message = b"terrabase-placement-transition-v1\0".to_vec();
    message.extend_from_slice(payload);
    serde_json::to_vec(&SignedEnvelope {
        key_id: key_id.into(),
        payload: base64::engine::general_purpose::STANDARD.encode(payload),
        signature: base64::engine::general_purpose::STANDARD.encode(key.sign(&message)),
    })
    .unwrap()
}

fn member(identity: String) -> Member {
    Member { identity, region: "GRU".into(), placement_digest: "a".repeat(64) }
}

fn prepared_journal(
    runtime: &ProductionMemberRuntime,
    root: &std::path::Path,
) -> (Arc<terrabase_tbep::placement::PlacementJournal>, Vec<u8>) {
    let config = runtime.transport().config();
    let local = config.local_identity();
    let peer = config.peers().next().unwrap().identity();
    let environment = local.environment().to_owned();
    let cluster = local.cluster().to_owned();
    let member_id = local.member().as_str().to_owned();
    let boundary = terrabase_tbep::placement::PlacementBoundary::new(
        &environment,
        &cluster,
        &member_id,
        config.placement_generation(),
        config.replica_set_epoch(),
        std::iter::empty(),
    )
    .unwrap();
    let directory = root.join("transition-journal");
    std::fs::create_dir(&directory).unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700)).unwrap();
    }
    let path = directory.join("placement.sqlite");
    let journal =
        Arc::new(terrabase_tbep::placement::PlacementJournal::open(&path, boundary).unwrap());
    let authority = key(1);
    let genesis = key(2);
    journal
        .configure_transition_trust(
            terrabase_tbep::placement::transition::TransitionTrust::new(
                BTreeMap::from([("authority".into(), authority.public_key().as_ref().to_vec())]),
                BTreeMap::from([("genesis".into(), genesis.public_key().as_ref().to_vec())]),
                BTreeMap::new(),
            )
            .unwrap(),
        )
        .unwrap();
    let proposal = terrabase_tbep::proto::terrabase::v1::PlacementProposal {
        environment: environment.clone(),
        cluster: cluster.clone(),
        generation: config.placement_generation(),
        epoch: config.replica_set_epoch(),
        regions: vec![terrabase_tbep::proto::terrabase::v1::PlacementRegionSeats {
            region: "GRU".into(),
            members: vec![member_id.clone(), peer.member().as_str().to_owned()],
        }],
        assignments: Vec::new(),
    };
    journal
        .stage(
            "authenticated-orchestrator",
            "retirement-prior",
            terrabase_tbep::proto::terrabase::v1::PlacementStageRequest {
                expected_revision: 0,
                proposal: Some(proposal),
                declarations: Vec::new(),
            },
        )
        .unwrap()
        .proposal
        .unwrap();
    let bytes: Vec<u8> = rusqlite::Connection::open(&path)
        .unwrap()
        .query_row("SELECT proposal FROM proposals WHERE revision=1", [], |row| row.get(0))
        .unwrap();
    let placement_digest = hash(&bytes);
    let mut members = vec![member(local.as_str().to_owned()), member(peer.as_str().to_owned())];
    for member in &mut members {
        member.placement_digest.clone_from(&placement_digest);
    }
    members.sort_by(|left, right| left.identity.cmp(&right.identity));
    let request = Request {
        operation_id: "retirement-prior".into(),
        scope_id: format!("{environment}/{cluster}"),
        prior_epoch: 0,
        epoch: config.replica_set_epoch(),
        generation: config.placement_generation(),
        members,
    };
    let request_bytes = serde_json::to_vec(&request).unwrap();
    let request_digest = hash(&request_bytes);
    let genesis_payload = serde_json::to_vec(&serde_json::json!({
        "kind": "genesis_grant", "grant_id": "grant", "request_digest": request_digest,
    }))
    .unwrap();
    let accepted_payload = serde_json::to_vec(&serde_json::json!({
        "schema_version": 1, "kind": "accepted_epoch", "request": request,
        "request_digest": request_digest, "authority_epoch": 1,
        "journal_revision": 1, "genesis_grant_id": "grant", "receipt_digests": [],
        "drain_receipts": [], "prior_generation": 0, "previous_proof_digest": "",
        "lease_id": "lease", "genesis_grant": serde_json::from_slice::<serde_json::Value>(&signed("genesis", &genesis, &genesis_payload)).unwrap(),
    })).unwrap();
    let accepted = signed("authority", &authority, &accepted_payload);
    journal
        .accept(
            "retirement-prior",
            &terrabase_tbep::proto::terrabase::v1::PlacementAcceptRequest {
                proposal_revision: 1,
                proof: accepted,
            },
        )
        .unwrap();
    runtime.admit_membership(&*journal).unwrap();
    let prior_digest = hash(&accepted_payload);
    let mut next_members =
        vec![member(local.as_str().to_owned()), member(peer.as_str().to_owned())];
    next_members.sort_by(|left, right| left.identity.cmp(&right.identity));
    let next_request = Request {
        operation_id: "retirement-next".into(),
        scope_id: format!("{environment}/{cluster}"),
        prior_epoch: config.replica_set_epoch(),
        epoch: config.replica_set_epoch() + 1,
        generation: config.placement_generation() + 1,
        members: next_members,
    };
    let next_digest = hash(&serde_json::to_vec(&next_request).unwrap());
    let next_payload = serde_json::to_vec(&serde_json::json!({
        "schema_version": 1, "kind": "prepared_epoch", "request": next_request,
        "request_digest": next_digest, "prior_generation": config.placement_generation(),
        "previous_proof_digest": prior_digest, "authority_epoch": 2,
        "lease_id": "lease-next", "journal_revision": 2,
    }))
    .unwrap();
    let prepared = signed("authority", &authority, &next_payload);
    (journal, prepared)
}

#[tokio::test]
async fn valid_prepared_transition_fences_empty_runtime_epoch() {
    use crate::production::tests::{NoControlFrames, NoDatabaseAssignments, runtime_for_readiness};
    let (runtime, root, _) = runtime_for_readiness("transition-retirement-test");
    let runtime = Arc::new(runtime);
    let (journal, prepared_bytes) = prepared_journal(&runtime, &root);
    let prepared = journal.begin_transition_drain("retirement-next", &prepared_bytes).unwrap();
    let registry =
        Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| true)));
    let lifecycle =
        Arc::new(ProductionDataLifecycle::new(Arc::new(ReplicationProgress::default()), registry));
    lifecycle
        .start(runtime.clone(), &NoDatabaseAssignments, Vec::new(), Arc::new(NoControlFrames))
        .await
        .unwrap();
    lifecycle.record_inventory(Ok(BTreeMap::new())).unwrap();
    let fences = lifecycle.begin_transition_retirement(runtime.clone(), &prepared).await.unwrap();
    assert!(fences.sequences().is_empty());
    assert!(lifecycle.is_draining());
    lifecycle.drain().await.unwrap();
    drop(fences);
    runtime.drain().await.storage.unwrap();
    drop(journal);
    std::fs::remove_dir_all(root).unwrap();
}

async fn empty_lifecycle(runtime: Arc<ProductionMemberRuntime>) -> Arc<ProductionDataLifecycle> {
    use crate::production::tests::{NoControlFrames, NoDatabaseAssignments};
    let registry =
        Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| true)));
    let lifecycle =
        Arc::new(ProductionDataLifecycle::new(Arc::new(ReplicationProgress::default()), registry));
    lifecycle
        .start(runtime, &NoDatabaseAssignments, Vec::new(), Arc::new(NoControlFrames))
        .await
        .unwrap();
    lifecycle.record_inventory(Ok(BTreeMap::new())).unwrap();
    lifecycle
}

#[tokio::test]
async fn matching_prepared_tuple_cannot_fence_a_different_lifecycle_owner() {
    use crate::production::tests::runtime_for_readiness;
    let (runtime, root, _) = runtime_for_readiness("retirement-wrong-owner");
    let runtime = Arc::new(runtime);
    let (other, other_root, _) = runtime_for_readiness("retirement-actual-owner");
    let other = Arc::new(other);
    let (journal, bytes) = prepared_journal(&runtime, &root);
    let prepared = journal.begin_transition_drain("retirement-next", &bytes).unwrap();
    let lifecycle = empty_lifecycle(other.clone()).await;
    let result = lifecycle.begin_transition_retirement(runtime.clone(), &prepared).await;
    assert!(
        matches!(result, Err(ref error) if error == "transition runtime does not own this lifecycle")
    );
    assert!(!lifecycle.is_draining());
    assert!(
        !runtime
            .facts()
            .blockers
            .contains(&terrabase_cluster::production::ProductionReadinessBlocker::Draining)
    );
    lifecycle.drain().await.unwrap();
    runtime.drain().await.storage.unwrap();
    other.drain().await.storage.unwrap();
    drop(journal);
    std::fs::remove_dir_all(root).unwrap();
    std::fs::remove_dir_all(other_root).unwrap();
}

#[test]
fn cancelled_retirement_retains_barrier_until_queued_fencing_finishes() {
    let executor = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .max_blocking_threads(1)
        .build()
        .unwrap();
    executor.block_on(async {
        let (runtime, root, _) =
            crate::production::tests::runtime_for_readiness("retirement-cancellation");
        let runtime = Arc::new(runtime);
        let (journal, bytes) = prepared_journal(&runtime, &root);
        let prepared = journal.begin_transition_drain("retirement-next", &bytes).unwrap();
        let lifecycle = empty_lifecycle(runtime.clone()).await;
        let (entered_tx, entered_rx) = tokio::sync::oneshot::channel();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let blocker = tokio::task::spawn_blocking(move || {
            entered_tx.send(()).unwrap();
            release_rx.recv().unwrap();
        });
        entered_rx.await.unwrap();
        let retiring = tokio::spawn({
            let lifecycle = lifecycle.clone();
            let runtime = runtime.clone();
            async move { lifecycle.begin_transition_retirement(runtime, &prepared).await }
        });
        tokio::time::timeout(std::time::Duration::from_secs(2), async {
            while lifecycle.startup.try_lock().is_ok() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        retiring.abort();
        assert!(matches!(retiring.await, Err(error) if error.is_cancelled()));
        // The blocking closure remains queued even though its async caller is gone.
        assert!(lifecycle.startup.try_lock().is_err());
        let mut drain = tokio::spawn({
            let lifecycle = lifecycle.clone();
            async move { lifecycle.drain().await }
        });
        assert!(
            tokio::time::timeout(std::time::Duration::from_millis(30), &mut drain).await.is_err()
        );
        release_tx.send(()).unwrap();
        blocker.await.unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(2), drain)
            .await
            .unwrap()
            .unwrap()
            .unwrap();
        assert!(
            runtime
                .facts()
                .blockers
                .contains(&terrabase_cluster::production::ProductionReadinessBlocker::Draining)
        );
        runtime.drain().await.storage.unwrap();
        drop(journal);
        std::fs::remove_dir_all(root).unwrap();
    });
}
