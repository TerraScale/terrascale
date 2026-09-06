use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use openraft::error::{InstallSnapshotError, NetworkError, RPCError, RaftError, RemoteError};
use openraft::network::RPCOption;
use openraft::raft::{
    AppendEntriesRequest, AppendEntriesResponse, InstallSnapshotRequest, InstallSnapshotResponse,
    VoteRequest, VoteResponse,
};
use openraft::{BasicNode, RaftNetwork, RaftNetworkFactory};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use terrabase_member_transport::MemberId;
use terrabase_member_transport::production::{
    ProductionConnection, ProductionQuicEndpoint, ProductionRegion,
};

use crate::{AuthAuthority, AuthorityError, TypeConfig};

const MAX_RPC_BYTES: usize = 2 * 1024 * 1024;

/// Raft RPC transport using existing mutually authenticated member QUIC.
/// The fixed voter map is separate from placement proposals and health opinions.
#[derive(Clone)]
pub struct AuthNetwork {
    endpoint: Arc<ProductionQuicEndpoint>,
    group: Arc<str>,
    voters: Arc<BTreeMap<u64, MemberId>>,
    local: u64,
}

/// A connection factory bound to one admitted consensus peer.
pub struct AuthPeer {
    network: AuthNetwork,
    target: u64,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Envelope {
    protocol: String,
    group: String,
    sender: u64,
    body: Request,
}

#[derive(Serialize, Deserialize)]
#[serde(tag = "method", content = "request", rename_all = "camelCase")]
enum Request {
    Append(AppendEntriesRequest<TypeConfig>),
    Vote(VoteRequest<u64>),
    Snapshot(InstallSnapshotRequest<TypeConfig>),
}

impl AuthNetwork {
    pub(crate) fn group(&self) -> &str {
        &self.group
    }
    pub(crate) fn local_node(&self) -> u64 {
        self.local
    }

    /// Validate an independently admitted voter mapping against transport identities
    /// and the database's immutable placement. This never elects or bootstraps a group.
    pub fn new(
        endpoint: Arc<ProductionQuicEndpoint>,
        group: &str,
        voters: BTreeMap<u64, MemberId>,
        placement: &BTreeSet<ProductionRegion>,
    ) -> Result<Self, AuthorityError> {
        if group.is_empty()
            || group.len() > 512
            || voters.len() < 2
            || voters.keys().any(|id| *id == 0)
            || !placement.contains(&endpoint.config().local_region())
        {
            return Err(AuthorityError::InvalidConfiguration);
        }
        let identities: BTreeSet<_> = voters.values().collect();
        if identities.len() != voters.len() {
            return Err(AuthorityError::InvalidConfiguration);
        }
        let local = voters
            .iter()
            .find_map(|(id, member)| {
                (member == endpoint.config().local_identity().member()).then_some(*id)
            })
            .ok_or(AuthorityError::InvalidConfiguration)?;
        for (id, member) in &voters {
            if *id == local {
                continue;
            }
            let peer =
                endpoint.config().peer(member).ok_or(AuthorityError::InvalidConfiguration)?;
            if !placement.contains(&peer.region()) {
                return Err(AuthorityError::InvalidConfiguration);
            }
        }
        Ok(Self { endpoint, group: Arc::from(group), voters: Arc::new(voters), local })
    }

    /// The exact initial nodes authorized by this fixed mapping.
    pub fn initial_nodes(&self) -> BTreeMap<u64, BasicNode> {
        self.voters
            .iter()
            .map(|(id, member)| (*id, BasicNode { addr: member.as_str().to_owned() }))
            .collect()
    }

    /// Dispatch one already authenticated member connection.
    /// The transport listener must route this dedicated lane exclusively to auth.
    pub async fn receive(
        &self,
        connection: &ProductionConnection,
        authority: &AuthAuthority,
    ) -> Result<(), AuthorityError> {
        let bytes = connection.receive_frame().await.map_err(|_| AuthorityError::Unavailable)?;
        if bytes.len() > MAX_RPC_BYTES {
            return Err(AuthorityError::Unavailable);
        }
        let envelope: Envelope =
            serde_json::from_slice(&bytes).map_err(|_| AuthorityError::Unavailable)?;
        if envelope.protocol != "terrabase.auth.raft.v1"
            || envelope.group != self.group.as_ref()
            || self.voters.get(&envelope.sender) != Some(connection.peer().member())
        {
            return Err(AuthorityError::Unavailable);
        }
        // A mutually authenticated voter may only speak as its own Raft candidate/leader.
        let claimed = match &envelope.body {
            Request::Append(request) => request.vote.leader_id.node_id,
            Request::Vote(request) => request.vote.leader_id.node_id,
            Request::Snapshot(request) => request.vote.leader_id.node_id,
        };
        if claimed != envelope.sender {
            return Err(AuthorityError::Unavailable);
        }
        let bytes = match envelope.body {
            Request::Append(request) => encode(&authority.raft().append_entries(request).await)?,
            Request::Vote(request) => encode(&authority.raft().vote(request).await)?,
            Request::Snapshot(request) => {
                encode(&authority.raft().install_snapshot(request).await)?
            }
        };
        connection.send_frame(&bytes).await.map_err(|_| AuthorityError::Unavailable)
    }
}

// Stop serialization at the frame bound instead of allocating an unbounded append batch.
fn encode<T: Serialize>(value: &T) -> Result<Vec<u8>, AuthorityError> {
    struct Bounded(Vec<u8>);
    impl std::io::Write for Bounded {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if self.0.len().saturating_add(bytes.len()) > MAX_RPC_BYTES {
                return Err(std::io::Error::other("auth RPC exceeds frame bound"));
            }
            self.0.extend_from_slice(bytes);
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    let mut writer = Bounded(Vec::new());
    serde_json::to_writer(&mut writer, value).map_err(|_| AuthorityError::Unavailable)?;
    Ok(writer.0)
}

impl AuthPeer {
    async fn send<T: DeserializeOwned, E: DeserializeOwned + std::error::Error>(
        &self,
        request: Request,
        option: RPCOption,
    ) -> Result<T, RPCError<u64, BasicNode, E>> {
        let transport_error = || {
            RPCError::Network(NetworkError::new(&std::io::Error::other(
                "auth consensus peer unavailable",
            )))
        };
        let member = self.network.voters.get(&self.target).ok_or_else(transport_error)?;
        let bytes = encode(&Envelope {
            protocol: "terrabase.auth.raft.v1".into(),
            group: self.network.group.to_string(),
            sender: self.network.local,
            body: request,
        })
        .map_err(|_| transport_error())?;
        let response = tokio::time::timeout(option.hard_ttl(), async {
            let connection = self.network.endpoint.connect(member).await.map_err(|_| ())?;
            connection.send_frame(&bytes).await.map_err(|_| ())?;
            connection.receive_frame().await.map_err(|_| ())
        })
        .await
        .map_err(|_| transport_error())?
        .map_err(|()| transport_error())?;
        if response.len() > MAX_RPC_BYTES {
            return Err(transport_error());
        }
        let result: Result<T, E> =
            serde_json::from_slice(&response).map_err(|_| transport_error())?;
        result.map_err(|error| RPCError::RemoteError(RemoteError::new(self.target, error)))
    }
}

impl RaftNetworkFactory<TypeConfig> for AuthNetwork {
    type Network = AuthPeer;
    async fn new_client(&mut self, target: u64, _: &BasicNode) -> AuthPeer {
        AuthPeer { network: self.clone(), target }
    }
}
impl RaftNetwork<TypeConfig> for AuthPeer {
    async fn append_entries(
        &mut self,
        rpc: AppendEntriesRequest<TypeConfig>,
        option: RPCOption,
    ) -> Result<AppendEntriesResponse<u64>, RPCError<u64, BasicNode, RaftError<u64>>> {
        self.send(Request::Append(rpc), option).await
    }
    async fn vote(
        &mut self,
        rpc: VoteRequest<u64>,
        option: RPCOption,
    ) -> Result<VoteResponse<u64>, RPCError<u64, BasicNode, RaftError<u64>>> {
        self.send(Request::Vote(rpc), option).await
    }
    async fn install_snapshot(
        &mut self,
        rpc: InstallSnapshotRequest<TypeConfig>,
        option: RPCOption,
    ) -> Result<
        InstallSnapshotResponse<u64>,
        RPCError<u64, BasicNode, RaftError<u64, InstallSnapshotError>>,
    > {
        self.send(Request::Snapshot(rpc), option).await
    }
}
