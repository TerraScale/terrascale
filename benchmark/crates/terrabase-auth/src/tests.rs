use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use openraft::error::{InstallSnapshotError, NetworkError, RPCError, RaftError, RemoteError};
use openraft::network::RPCOption;
use openraft::raft::{
    AppendEntriesRequest, AppendEntriesResponse, InstallSnapshotRequest, InstallSnapshotResponse,
    VoteRequest, VoteResponse,
};
use openraft::storage::{RaftLogStorage, RaftStateMachine};
use openraft::testing::{StoreBuilder, Suite};
use openraft::{
    BasicNode, Config, Entry, EntryPayload, LogId, Raft, RaftNetwork, RaftNetworkFactory,
    StorageError, Vote,
};
use serde_json::json;

use crate::*;

struct Builder;
impl StoreBuilder<TypeConfig, SqliteStore, SqliteStore, tempfile::TempDir> for Builder {
    async fn build(
        &self,
    ) -> Result<(tempfile::TempDir, SqliteStore, SqliteStore), StorageError<u64>> {
        let dir = tempfile::tempdir().unwrap();
        let store = SqliteStore::open(&dir.path().join("auth.sqlite"), "test-group")?;
        Ok((dir, store.clone(), store))
    }
}

#[test]
fn maintained_openraft_storage_suite() {
    Suite::test_all(Builder).unwrap();
}

fn command(key: &str, action: Action) -> Command {
    Command { idempotency_key: key.into(), action }
}
fn user(id: &str, email: &str) -> Record {
    serde_json::from_value(json!({"id":id,"email":email,"version":1})).unwrap()
}
fn expected(version: u64) -> Record {
    serde_json::from_value(json!({"version":version})).unwrap()
}
async fn apply(store: &mut SqliteStore, index: u64, command: Command) -> Outcome {
    store
        .apply([Entry {
            log_id: LogId::new(openraft::CommittedLeaderId::new(1, 1), index),
            payload: EntryPayload::Normal(command),
        }])
        .await
        .unwrap()
        .remove(0)
}

#[tokio::test]
async fn atomic_unique_cas_consume_replay_survive_reopen_and_snapshot() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("auth.sqlite");
    let mut store = SqliteStore::open(&path, "group").unwrap();
    store.save_vote(&Vote::new_committed(3, 1)).await.unwrap();
    assert_eq!(
        apply(
            &mut store,
            1,
            command(
                "index",
                Action::CreateUniqueIndex { model: "user".into(), fields: vec!["email".into()] }
            )
        )
        .await,
        Outcome::Applied
    );
    let create = command(
        "create",
        Action::InsertUnique { model: "user".into(), record: user("a", "a@test") },
    );
    assert!(matches!(apply(&mut store, 2, create.clone()).await, Outcome::Record { .. }));
    assert_eq!(
        apply(
            &mut store,
            3,
            command(
                "duplicate",
                Action::InsertUnique { model: "user".into(), record: user("b", "a@test") }
            )
        )
        .await,
        Outcome::UniqueConflict
    );
    let cas = command(
        "cas",
        Action::CompareAndSwap {
            model: "user".into(),
            id: "a".into(),
            expected: expected(1),
            update: expected(2),
        },
    );
    assert!(matches!(apply(&mut store, 4, cas).await, Outcome::Record { .. }));
    assert_eq!(
        apply(
            &mut store,
            5,
            command(
                "stale",
                Action::CompareAndSwap {
                    model: "user".into(),
                    id: "a".into(),
                    expected: expected(1),
                    update: expected(3)
                }
            )
        )
        .await,
        Outcome::NotMatched
    );
    let consume = command(
        "consume",
        Action::ConsumeOne { model: "user".into(), id: "a".into(), expected: expected(2) },
    );
    let consumed = apply(&mut store, 6, consume.clone()).await;
    assert!(matches!(consumed, Outcome::Record { .. }));
    drop(store);
    let mut store = SqliteStore::open(&path, "group").unwrap();
    assert_eq!(store.read_vote().await.unwrap(), Some(Vote::new_committed(3, 1)));
    assert_eq!(apply(&mut store, 7, consume.clone()).await, consumed);
    assert!(store.read_record("user", "a").unwrap().is_none());
    assert_eq!(
        apply(&mut store, 8, command("consume-again", consume.action.clone())).await,
        Outcome::NotMatched
    );
    assert_eq!(
        apply(&mut store, 9, command("consume", create.action)).await,
        Outcome::IdempotencyConflict
    );
    let snapshot = openraft::RaftSnapshotBuilder::build_snapshot(&mut store).await.unwrap();
    let mut restored = SqliteStore::open(&dir.path().join("restored.sqlite"), "group").unwrap();
    restored.install_snapshot(&snapshot.meta, snapshot.snapshot).await.unwrap();
    assert_eq!(apply(&mut restored, 10, consume).await, consumed);
    assert!(restored.read_record("user", "a").unwrap().is_none());
    assert!(SqliteStore::open(&path, "different-group").is_err());
}

// Protocol fault injection beneath actual OpenRaft members and real SQLite stores.
// This is not public API or production transport qualification.
#[derive(Clone, Default)]
struct Network {
    nodes: Arc<Mutex<BTreeMap<u64, Raft<TypeConfig>>>>,
    isolated: Arc<Mutex<Option<u64>>>,
    source: u64,
}
struct Client {
    network: Network,
    target: u64,
}
impl RaftNetworkFactory<TypeConfig> for Network {
    type Network = Client;
    async fn new_client(&mut self, target: u64, _: &BasicNode) -> Client {
        Client { network: self.clone(), target }
    }
}
impl Client {
    fn peer<E>(&self) -> Result<Raft<TypeConfig>, RPCError<u64, BasicNode, E>>
    where
        E: std::error::Error,
    {
        let isolated = *self.network.isolated.lock().unwrap();
        if isolated == Some(self.target) || isolated == Some(self.network.source) {
            return Err(RPCError::Network(NetworkError::new(&std::io::Error::other("partition"))));
        }
        self.network
            .nodes
            .lock()
            .unwrap()
            .get(&self.target)
            .cloned()
            .ok_or_else(|| RPCError::Network(NetworkError::new(&std::io::Error::other("offline"))))
    }
}
impl RaftNetwork<TypeConfig> for Client {
    async fn append_entries(
        &mut self,
        rpc: AppendEntriesRequest<TypeConfig>,
        _: RPCOption,
    ) -> Result<AppendEntriesResponse<u64>, RPCError<u64, BasicNode, RaftError<u64>>> {
        self.peer()?
            .append_entries(rpc)
            .await
            .map_err(|error| RPCError::RemoteError(RemoteError::new(self.target, error)))
    }
    async fn vote(
        &mut self,
        rpc: VoteRequest<u64>,
        _: RPCOption,
    ) -> Result<VoteResponse<u64>, RPCError<u64, BasicNode, RaftError<u64>>> {
        self.peer()?
            .vote(rpc)
            .await
            .map_err(|error| RPCError::RemoteError(RemoteError::new(self.target, error)))
    }
    async fn install_snapshot(
        &mut self,
        rpc: InstallSnapshotRequest<TypeConfig>,
        _: RPCOption,
    ) -> Result<
        InstallSnapshotResponse<u64>,
        RPCError<u64, BasicNode, RaftError<u64, InstallSnapshotError>>,
    > {
        self.peer()?
            .install_snapshot(rpc)
            .await
            .map_err(|error| RPCError::RemoteError(RemoteError::new(self.target, error)))
    }
}

async fn leader(nodes: &[AuthAuthority], excluded: Option<usize>) -> usize {
    tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            for (index, node) in nodes.iter().enumerate() {
                if excluded != Some(index)
                    && node.raft().metrics().borrow().current_leader == Some(index as u64 + 1)
                {
                    return index;
                }
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("quorum elects leader")
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn real_raft_rejects_partitioned_leader_and_serializes_competing_auth_writes() {
    let dir = tempfile::tempdir().unwrap();
    let network = Network::default();
    let mut nodes = Vec::new();
    for id in 1..=3 {
        let store = SqliteStore::open(&dir.path().join(format!("{id}.sqlite")), "group").unwrap();
        store
            .bind_admission(
                "group",
                id,
                &(1..=3).map(|id| (id, BasicNode { addr: id.to_string() })).collect(),
            )
            .unwrap();
        let config = Config {
            cluster_name: "auth-test".into(),
            heartbeat_interval: 30,
            election_timeout_min: 120,
            election_timeout_max: 240,
            ..Default::default()
        };
        let node = AuthAuthority::recover(
            id,
            store,
            Network { source: id, ..network.clone() },
            config,
            Duration::from_millis(500),
        )
        .await
        .unwrap();
        network.nodes.lock().unwrap().insert(id, node.raft().clone());
        nodes.push(node);
    }
    nodes[0]
        .initialize((1..=3).map(|id| (id, BasicNode { addr: id.to_string() })).collect())
        .await
        .unwrap();
    let current = leader(&nodes, None).await;
    nodes[current]
        .mutate(command(
            "index",
            Action::CreateUniqueIndex { model: "user".into(), fields: vec!["email".into()] },
        ))
        .await
        .unwrap();
    let mut tasks = Vec::new();
    for i in 0..12 {
        let node = nodes[current].clone();
        tasks.push(tokio::spawn(async move {
            node.mutate(command(
                &format!("create-{i}"),
                Action::InsertUnique {
                    model: "user".into(),
                    record: user(&i.to_string(), "same@test"),
                },
            ))
            .await
            .unwrap()
        }));
    }
    let mut winning_ids = Vec::new();
    for task in tasks {
        if let Outcome::Record { record } = task.await.unwrap() {
            winning_ids.push(record["id"].as_str().unwrap().to_owned());
        }
    }
    assert_eq!(winning_ids.len(), 1);
    let id = winning_ids.pop().unwrap();
    for consume in [false, true] {
        let mut tasks = Vec::new();
        for i in 0..12 {
            let node = nodes[current].clone();
            let id = id.clone();
            tasks.push(tokio::spawn(async move {
                let action = if consume {
                    Action::ConsumeOne { model: "user".into(), id, expected: expected(2) }
                } else {
                    Action::CompareAndSwap {
                        model: "user".into(),
                        id,
                        expected: expected(1),
                        update: expected(2),
                    }
                };
                node.mutate(command(&format!("conditional-{consume}-{i}"), action)).await.unwrap()
            }));
        }
        let mut winners = 0;
        for task in tasks {
            if matches!(task.await.unwrap(), Outcome::Record { .. }) {
                winners += 1;
            }
        }
        assert_eq!(winners, 1);
    }
    *network.isolated.lock().unwrap() = Some(current as u64 + 1);
    assert!(nodes[current].find_one("user".into(), "0".into()).await.is_err());
    assert!(
        nodes[current]
            .mutate(command(
                "minority",
                Action::InsertUnique {
                    model: "user".into(),
                    record: user("minority", "minority@test")
                }
            ))
            .await
            .is_err()
    );
    let replacement = leader(&nodes, Some(current)).await;
    assert!(nodes[replacement].find_one("user".into(), "minority".into()).await.unwrap().is_none());
    *network.isolated.lock().unwrap() = None;
    for node in &nodes {
        node.shutdown().await.unwrap();
    }
}
