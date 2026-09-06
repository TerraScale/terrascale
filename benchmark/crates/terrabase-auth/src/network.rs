use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::time::Duration;

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
    ProductionConnection, ProductionConnectionClass, ProductionQuicEndpoint, ProductionRegion,
};

use crate::{Action, AuthAuthority, AuthorityError, Command, Outcome, TypeConfig};

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
    ClientWrite(Command),
    ClientQuery(Action),
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ClientReply {
    outcome: Option<Outcome>,
    error: Option<ClientError>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum ClientError {
    Unavailable,
    Deadline,
    Invalid,
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
            // Local configuration must support the full auth RPC envelope.
            // A peer's negotiated limit is known only after connecting;
            // transport sends also enforce that limit and fail closed.
            || endpoint.config().limits().max_frame_bytes() < MAX_RPC_BYTES
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

    /// Return the bounded group routing key and whether this is forwarded client work.
    /// Listeners must reserve consensus capacity independently of forwarded clients.
    pub fn frame_route(bytes: &[u8]) -> Result<(String, bool), AuthorityError> {
        if bytes.len() > MAX_RPC_BYTES {
            return Err(AuthorityError::Unavailable);
        }
        let envelope: Envelope =
            serde_json::from_slice(bytes).map_err(|_| AuthorityError::Unavailable)?;
        if envelope.protocol != "terrabase.auth.raft.v1" {
            return Err(AuthorityError::Unavailable);
        }
        let client = matches!(envelope.body, Request::ClientWrite(_) | Request::ClientQuery(_));
        Ok((envelope.group, client))
    }

    /// Dispatch one already authenticated member connection.
    /// The transport listener must route this dedicated lane exclusively to auth.
    pub async fn receive(
        &self,
        connection: &ProductionConnection,
        authority: &AuthAuthority,
    ) -> Result<(), AuthorityError> {
        let bytes = connection.receive_frame().await.map_err(|_| AuthorityError::Unavailable)?;
        self.receive_frame(connection, authority, &bytes).await
    }

    /// Dispatch an already received bounded frame without consuming a second frame.
    pub async fn receive_frame(
        &self,
        connection: &ProductionConnection,
        authority: &AuthAuthority,
        bytes: &[u8],
    ) -> Result<(), AuthorityError> {
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
            Request::ClientWrite(_) | Request::ClientQuery(_) => envelope.sender,
        };
        if !matches!(&envelope.body, Request::ClientWrite(_) | Request::ClientQuery(_))
            && claimed != envelope.sender
        {
            return Err(AuthorityError::Unavailable);
        }
        let expected_class = match &envelope.body {
            Request::ClientWrite(_) | Request::ClientQuery(_) => {
                ProductionConnectionClass::AuthClient
            }
            Request::Append(_) | Request::Vote(_) | Request::Snapshot(_) => {
                ProductionConnectionClass::AuthConsensus
            }
        };
        if connection.class() != expected_class {
            return Err(AuthorityError::Unavailable);
        }
        let bytes = match envelope.body {
            Request::Append(request) => encode(&authority.raft().append_entries(request).await)?,
            Request::Vote(request) => encode(&authority.raft().vote(request).await)?,
            Request::Snapshot(request) => {
                encode(&authority.raft().install_snapshot(request).await)?
            }
            Request::ClientWrite(command) => encode_client(authority.mutate_local(command).await)?,
            Request::ClientQuery(action) => encode_client(authority.query_local(action).await)?,
        };
        connection.send_frame(&bytes).await.map_err(|_| AuthorityError::Unavailable)
    }
}

fn encode_client(result: Result<Outcome, AuthorityError>) -> Result<Vec<u8>, AuthorityError> {
    let reply = match result {
        Ok(outcome) => ClientReply { outcome: Some(outcome), error: None },
        Err(AuthorityError::Unavailable) => {
            ClientReply { outcome: None, error: Some(ClientError::Unavailable) }
        }
        Err(AuthorityError::Deadline) => {
            ClientReply { outcome: None, error: Some(ClientError::Deadline) }
        }
        Err(AuthorityError::InvalidConfiguration) => {
            ClientReply { outcome: None, error: Some(ClientError::Invalid) }
        }
    };
    encode(&reply)
}

impl AuthNetwork {
    pub(crate) async fn client_write(
        &self,
        target: u64,
        command: Command,
        deadline: Duration,
    ) -> Result<Outcome, AuthorityError> {
        self.client_request(target, Request::ClientWrite(command), deadline).await
    }

    pub(crate) async fn client_query(
        &self,
        target: u64,
        action: Action,
        deadline: Duration,
    ) -> Result<Outcome, AuthorityError> {
        self.client_request(target, Request::ClientQuery(action), deadline).await
    }

    async fn client_request(
        &self,
        target: u64,
        request: Request,
        deadline: Duration,
    ) -> Result<Outcome, AuthorityError> {
        let member = self.voters.get(&target).ok_or(AuthorityError::Unavailable)?;
        let bytes = encode(&Envelope {
            protocol: "terrabase.auth.raft.v1".into(),
            group: self.group.to_string(),
            sender: self.local,
            body: request,
        })?;
        let response = tokio::time::timeout(deadline, async {
            let _permit = self
                .endpoint
                .acquire_request(
                    member,
                    ProductionConnectionClass::AuthClient,
                    bytes.len(),
                    deadline,
                )
                .await
                .map_err(|error| {
                    diagnose_transport("auth-client admission", target, error);
                    AuthorityError::Unavailable
                })?;
            let connection = self
                .endpoint
                .connect(member, ProductionConnectionClass::AuthClient)
                .await
                .map_err(|error| {
                    diagnose_transport("auth-client connect", target, error);
                    AuthorityError::Unavailable
                })?;
            connection.send_frame(&bytes).await.map_err(|error| {
                diagnose_transport("auth-client send", target, error);
                AuthorityError::Unavailable
            })?;
            let response = connection.receive_frame().await.map_err(|error| {
                diagnose_transport("auth-client receive", target, error);
                AuthorityError::Unavailable
            })?;
            connection.complete_request().await.map_err(|error| {
                diagnose_transport("auth-client close", target, error);
                AuthorityError::Unavailable
            })?;
            Ok(response)
        })
        .await
        .map_err(|_| AuthorityError::Deadline)??;
        if response.len() > MAX_RPC_BYTES {
            return Err(AuthorityError::Unavailable);
        }
        let reply: ClientReply =
            serde_json::from_slice(&response).map_err(|_| AuthorityError::Unavailable)?;
        match (reply.outcome, reply.error) {
            (Some(outcome), None) => Ok(outcome),
            (None, Some(ClientError::Unavailable)) => Err(AuthorityError::Unavailable),
            (None, Some(ClientError::Deadline)) => Err(AuthorityError::Deadline),
            (None, Some(ClientError::Invalid)) | (Some(_), Some(_)) | (None, None) => {
                Err(AuthorityError::Unavailable)
            }
        }
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
            let _permit = self
                .network
                .endpoint
                .acquire_request(
                    member,
                    ProductionConnectionClass::AuthConsensus,
                    bytes.len(),
                    option.hard_ttl(),
                )
                .await
                .map_err(|error| {
                    diagnose_transport("auth-consensus admission", self.target, error);
                })?;
            let connection = self
                .network
                .endpoint
                .connect(member, ProductionConnectionClass::AuthConsensus)
                .await
                .map_err(|error| {
                    diagnose_transport("auth-consensus connect", self.target, error);
                })?;
            connection.send_frame(&bytes).await.map_err(|error| {
                diagnose_transport("auth-consensus send", self.target, error);
            })?;
            let response = connection.receive_frame().await.map_err(|error| {
                diagnose_transport("auth-consensus receive", self.target, error);
            })?;
            connection.complete_request().await.map_err(|error| {
                diagnose_transport("auth-consensus close", self.target, error);
            })?;
            Ok(response)
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

fn diagnose_transport<T: std::fmt::Debug>(operation: &str, target: u64, error: T) {
    if std::env::var_os("TERRASCALE_AUTH_DIAGNOSTICS").is_some() {
        eprintln!("[auth transport diagnostics] {operation} target={target}: {error:?}");
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
