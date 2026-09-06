use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use openraft::{BasicNode, Config, Raft, RaftNetworkFactory};

use crate::{Action, Command, Outcome, Record, SqliteStore, TypeConfig};

/// Consensus-backed admission for strict auth operations.
/// There is no local read or local write fallback.
#[derive(Clone)]
pub struct AuthAuthority {
    raft: Raft<TypeConfig>,
    store: SqliteStore,
    deadline: Duration,
    durability_voters: Option<Arc<BTreeMap<u64, BasicNode>>>,
    local: u64,
}

/// Authority, deadline, or durable storage failure. Mutation outcomes may be unknown.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthorityError {
    /// No verified quorum leader or an unavailable durable dependency.
    Unavailable,
    /// Deadline elapsed; resolve mutations with the same idempotency identity.
    Deadline,
    /// Supplied group configuration is invalid.
    InvalidConfiguration,
}

impl AuthAuthority {
    /// Recover using an independently admitted, immutable QUIC voter identity mapping.
    /// Existing disk state refuses a changed database or voter identity mapping.
    pub async fn recover_admitted(
        store: SqliteStore,
        network: crate::AuthNetwork,
        config: Config,
        deadline: Duration,
    ) -> Result<Self, AuthorityError> {
        store
            .bind_admission(network.group(), network.local_node(), &network.initial_nodes())
            .map_err(|_| AuthorityError::InvalidConfiguration)?;
        let voters = Arc::new(network.initial_nodes());
        let mut authority =
            Self::recover(network.local_node(), store, network, config, deadline).await?;
        authority.durability_voters = Some(voters);
        Ok(authority)
    }

    /// Recover one durable Raft member. This does not create a new group.
    /// The network must authenticate admitted member identities and restrict placement.
    pub(crate) async fn recover<N>(
        node: u64,
        store: SqliteStore,
        network: N,
        config: Config,
        deadline: Duration,
    ) -> Result<Self, AuthorityError>
    where
        N: RaftNetworkFactory<TypeConfig>,
    {
        if node == 0 || deadline.is_zero() {
            return Err(AuthorityError::InvalidConfiguration);
        }
        let config = config.validate().map_err(|_| AuthorityError::InvalidConfiguration)?;
        let raft = Raft::new(node, Arc::new(config), network, store.clone(), store.clone())
            .await
            .map_err(|_| AuthorityError::Unavailable)?;
        Ok(Self { raft, store, deadline, durability_voters: None, local: node })
    }

    /// Initialize an explicitly authorized, new voter configuration once.
    /// The caller must supply an admitted, placement-restricted durable bootstrap record.
    /// Never call this automatically on recovery from missing or lost state.
    pub async fn initialize(&self, voters: BTreeMap<u64, BasicNode>) -> Result<(), AuthorityError> {
        if voters.len() < 2 || voters.keys().any(|node| *node == 0) {
            return Err(AuthorityError::InvalidConfiguration);
        }
        self.store.verify_bootstrap(&voters).map_err(|_| AuthorityError::InvalidConfiguration)?;
        self.raft.initialize(voters).await.map_err(|_| AuthorityError::Unavailable)
    }

    /// Whether recovered durable state already has a Raft membership.
    pub async fn is_initialized(&self) -> Result<bool, AuthorityError> {
        self.raft.is_initialized().await.map_err(|_| AuthorityError::Unavailable)
    }

    /// Submit a stable conditional command to consensus; local followers refuse.
    pub async fn mutate(&self, command: Command) -> Result<Outcome, AuthorityError> {
        tokio::time::timeout(self.deadline, async {
            let response =
                self.raft.client_write(command).await.map_err(|_| AuthorityError::Unavailable)?;
            // The admitted group has two independently durable SQLite voters per Region.
            // A majority alone need not include both regional seats. Require every voter
            // to durably append the committed entry before acknowledging auth success.
            if let Some(voters) = &self.durability_voters {
                let mut metrics = self.raft.metrics();
                loop {
                    let complete = {
                        let observed = metrics.borrow();
                        if observed.current_leader != Some(self.local) {
                            return Err(AuthorityError::Unavailable);
                        }
                        observed.replication.as_ref().is_some_and(|replication| {
                            voters.keys().all(|id| {
                                *id == self.local
                                    || replication.get(id).is_some_and(|matched| {
                                        matched.is_some_and(|log| log >= response.log_id)
                                    })
                            })
                        })
                    };
                    if complete {
                        break;
                    }
                    metrics.changed().await.map_err(|_| AuthorityError::Unavailable)?;
                }
            }
            Ok(response.data)
        })
        .await
        .map_err(|_| AuthorityError::Deadline)?
    }

    /// Read only after a quorum-confirmed barrier and application of its index.
    pub async fn find_one(
        &self,
        model: String,
        id: String,
    ) -> Result<Option<Record>, AuthorityError> {
        tokio::time::timeout(self.deadline, self.raft.ensure_linearizable())
            .await
            .map_err(|_| AuthorityError::Deadline)?
            .map_err(|_| AuthorityError::Unavailable)?;
        let store = self.store.clone();
        tokio::task::spawn_blocking(move || store.read_record(&model, &id))
            .await
            .map_err(|_| AuthorityError::Unavailable)?
            .map_err(|_| AuthorityError::Unavailable)
    }

    /// Execute a read-only predicate query after a quorum-confirmed applied barrier.
    /// Transactional reads use `mutate` to observe their replicated workspace.
    pub async fn query(&self, action: Action) -> Result<Outcome, AuthorityError> {
        if !matches!(action, Action::Find { .. } | Action::Count { .. }) {
            return Err(AuthorityError::InvalidConfiguration);
        }
        tokio::time::timeout(self.deadline, async {
            self.raft.ensure_linearizable().await.map_err(|_| AuthorityError::Unavailable)?;
            let store = self.store.clone();
            tokio::task::spawn_blocking(move || store.query(&action))
                .await
                .map_err(|_| AuthorityError::Unavailable)?
                .map_err(|_| AuthorityError::Unavailable)
        })
        .await
        .map_err(|_| AuthorityError::Deadline)?
    }

    /// The consensus RPC receiver for an authenticated transport dispatcher.
    pub(crate) fn raft(&self) -> &Raft<TypeConfig> {
        &self.raft
    }

    /// Stop consensus tasks before closing the host's durable resources.
    pub async fn shutdown(&self) -> Result<(), AuthorityError> {
        self.raft.shutdown().await.map_err(|_| AuthorityError::Unavailable)
    }
}
