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
    Command { idempotency_key: key.into(), observed_at_ms: 1000, action }
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

fn tx(id: &str, action: Action) -> Action {
    Action::TransactionOperation { transaction_id: id.into(), action: Box::new(action) }
}

#[tokio::test]
async fn transaction_reads_own_writes_and_commit_replay_survives_expiry_and_restart() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("auth.sqlite");
    let mut store = SqliteStore::open(&path, "group").unwrap();
    assert!(matches!(
        apply(
            &mut store,
            1,
            command("begin", Action::BeginTransaction { transaction_id: "tx".into() })
        )
        .await,
        Outcome::Transaction { .. }
    ));
    apply(
        &mut store,
        2,
        command(
            "tx-insert",
            tx(
                "tx",
                Action::InsertUnique { model: "user".into(), record: user("a", "secret@test") },
            ),
        ),
    )
    .await;
    assert!(store.read_record("user", "a").unwrap().is_none());
    let selected = apply(
        &mut store,
        3,
        command(
            "tx-read",
            tx(
                "tx",
                Action::Find { model: "user".into(), predicates: vec![], offset: 0, limit: 100 },
            ),
        ),
    )
    .await;
    assert!(matches!(selected,Outcome::Records { records } if records.len()==1));
    let commit = command("commit", Action::CommitTransaction { transaction_id: "tx".into() });
    assert_eq!(apply(&mut store, 4, commit.clone()).await, Outcome::Applied);
    drop(store);
    let mut store = SqliteStore::open(&path, "group").unwrap();
    assert!(store.read_record("user", "a").unwrap().is_some());
    let mut retry = commit;
    retry.observed_at_ms = 100_000;
    assert_eq!(apply(&mut store, 5, retry).await, Outcome::Applied);
}

#[tokio::test]
async fn caught_inner_conflict_cannot_commit_partial_effects() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    apply(
        &mut store,
        1,
        command("begin", Action::BeginTransaction { transaction_id: "tx".into() }),
    )
    .await;
    apply(
        &mut store,
        2,
        command(
            "first",
            tx("tx", Action::InsertUnique { model: "user".into(), record: user("a", "one@test") }),
        ),
    )
    .await;
    assert_eq!(
        apply(
            &mut store,
            3,
            command(
                "conflict",
                tx(
                    "tx",
                    Action::InsertUnique { model: "user".into(), record: user("a", "two@test") }
                )
            )
        )
        .await,
        Outcome::UniqueConflict
    );
    assert_eq!(
        apply(
            &mut store,
            4,
            command("commit", Action::CommitTransaction { transaction_id: "tx".into() })
        )
        .await,
        Outcome::TransactionConflict
    );
    assert!(store.read_record("user", "a").unwrap().is_none());
}

#[tokio::test]
async fn competing_owner_departure_snapshots_allow_only_one_commit() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    apply(
        &mut store,
        1,
        command("a", Action::InsertUnique { model: "member".into(), record: user("a", "a@test") }),
    )
    .await;
    apply(
        &mut store,
        2,
        command("b", Action::InsertUnique { model: "member".into(), record: user("b", "b@test") }),
    )
    .await;
    for (index, id) in [(3, "a"), (4, "b")] {
        apply(
            &mut store,
            index,
            command(&format!("begin-{id}"), Action::BeginTransaction { transaction_id: id.into() }),
        )
        .await;
    }
    for (index, id) in [(5, "a"), (6, "b")] {
        let seen = apply(
            &mut store,
            index,
            command(
                &format!("read-{id}"),
                tx(
                    id,
                    Action::Find {
                        model: "member".into(),
                        predicates: vec![],
                        offset: 0,
                        limit: 100,
                    },
                ),
            ),
        )
        .await;
        assert!(matches!(seen,Outcome::Records { records } if records.len()==2));
    }
    for (index, id) in [(7, "a"), (8, "b")] {
        apply(
            &mut store,
            index,
            command(
                &format!("remove-{id}"),
                tx(
                    id,
                    Action::ConsumeOne {
                        model: "member".into(),
                        id: id.into(),
                        expected: expected(1),
                    },
                ),
            ),
        )
        .await;
    }
    assert_eq!(
        apply(
            &mut store,
            9,
            command("commit-a", Action::CommitTransaction { transaction_id: "a".into() })
        )
        .await,
        Outcome::Applied
    );
    assert_eq!(
        apply(
            &mut store,
            10,
            command("commit-b", Action::CommitTransaction { transaction_id: "b".into() })
        )
        .await,
        Outcome::TransactionConflict
    );
    assert!(store.read_record("member", "a").unwrap().is_none());
    assert!(store.read_record("member", "b").unwrap().is_some());
}

#[tokio::test]
async fn transaction_concurrency_bound_and_expiry_gc_are_replicated() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    for index in 1..=32 {
        assert!(matches!(
            apply(
                &mut store,
                index,
                command(
                    &format!("begin-{index}"),
                    Action::BeginTransaction { transaction_id: index.to_string() }
                )
            )
            .await,
            Outcome::Transaction { .. }
        ));
    }
    assert!(matches!(
        apply(
            &mut store,
            33,
            command("excess", Action::BeginTransaction { transaction_id: "excess".into() })
        )
        .await,
        Outcome::Invalid { .. }
    ));
    let mut gc = command("gc", Action::BeginTransaction { transaction_id: "fresh".into() });
    gc.observed_at_ms = 40_000;
    assert!(matches!(apply(&mut store, 34, gc).await, Outcome::Transaction { .. }));
    assert_eq!(
        apply(
            &mut store,
            35,
            command("expired", Action::CommitTransaction { transaction_id: "1".into() })
        )
        .await,
        Outcome::TransactionConflict
    );
}

#[tokio::test]
async fn transaction_touches_one_record_in_database_larger_than_snapshot_bound() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    let entries = (1..=1100)
        .map(|index| Entry {
            log_id: LogId::new(openraft::CommittedLeaderId::new(1, 1), index),
            payload: EntryPayload::Normal(command(
                &format!("create-{index}"),
                Action::InsertUnique {
                    model: "user".into(),
                    record: user(&format!("{index:04}"), &format!("{index}@test")),
                },
            )),
        })
        .collect::<Vec<_>>();
    store.apply(entries).await.unwrap();
    apply(
        &mut store,
        1101,
        command("begin", Action::BeginTransaction { transaction_id: "large".into() }),
    )
    .await;
    let predicate: Predicate =
        serde_json::from_value(json!({"field":"email","value":"1100@test"})).unwrap();
    let found = apply(
        &mut store,
        1102,
        command(
            "find",
            tx(
                "large",
                Action::Find {
                    model: "user".into(),
                    predicates: vec![predicate],
                    offset: 0,
                    limit: 1,
                },
            ),
        ),
    )
    .await;
    assert!(
        matches!(found,Outcome::Records { records } if records.len()==1 && records[0]["id"]=="1100")
    );
    apply(
        &mut store,
        1103,
        command(
            "change",
            tx(
                "large",
                Action::CompareAndSwap {
                    model: "user".into(),
                    id: "1100".into(),
                    expected: expected(1),
                    update: expected(2),
                },
            ),
        ),
    )
    .await;
    assert_eq!(
        apply(
            &mut store,
            1104,
            command("commit", Action::CommitTransaction { transaction_id: "large".into() })
        )
        .await,
        Outcome::Applied
    );
    assert_eq!(store.read_record("user", "1100").unwrap().unwrap()["version"], 2);
    assert_eq!(store.read_record("user", "0001").unwrap().unwrap()["version"], 1);
}
