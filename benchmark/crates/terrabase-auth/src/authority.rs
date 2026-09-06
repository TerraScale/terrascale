use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use openraft::{BasicNode, Config, Raft, RaftNetworkFactory};

use crate::{Command, Outcome, Record, SqliteStore, TypeConfig};

/// Consensus-backed admission for strict auth operations.
/// There is no local read or local write fallback.
#[derive(Clone)]
pub struct AuthAuthority {
    raft: Raft<TypeConfig>,
    store: SqliteStore,
    deadline: Duration,
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
            .bind_admission(network.group(), &network.initial_nodes())
            .map_err(|_| AuthorityError::InvalidConfiguration)?;
        Self::recover(network.local_node(), store, network, config, deadline).await
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
        Ok(Self { raft, store, deadline })
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

    /// Submit a stable conditional command to consensus; local followers refuse.
    pub async fn mutate(&self, command: Command) -> Result<Outcome, AuthorityError> {
        tokio::time::timeout(self.deadline, self.raft.client_write(command))
            .await
            .map_err(|_| AuthorityError::Deadline)?
            .map(|response| response.data)
            .map_err(|_| AuthorityError::Unavailable)
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

    /// The consensus RPC receiver for an authenticated transport dispatcher.
    pub(crate) fn raft(&self) -> &Raft<TypeConfig> {
        &self.raft
    }

    /// Stop consensus tasks before closing the host's durable resources.
    pub async fn shutdown(&self) -> Result<(), AuthorityError> {
        self.raft.shutdown().await.map_err(|_| AuthorityError::Unavailable)
    }
}
