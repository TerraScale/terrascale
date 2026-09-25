//! Process-level coverage for the signed-authority mTLS replication receiver.

use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair as _};
use futures_util::stream;
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, PKCS_ED25519, SanType,
};
use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, DocumentId, MembershipEpoch, MemoryBudget, OriginId,
    ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
use tb_document::{DocumentStore, DocumentStoreConfig, DocumentWrite, DocumentWriteValue};
use tb_format::{
    DocumentBinding, FormatLimits, StatePointer, WalOperation, decode_wal_frame,
    encode_catalog_into, encode_schema_into, encode_state_pointer, encode_wal_frame_into,
};
use tb_protocol::{
    AdmissionController, AdmissionLimits, CommittedGroupMetadata, V1_EVENT_FORMAT,
    V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR, client_mtls_config, encode_committed_group, v1,
    v1_transfer_limits,
};
use tb_replication::{
    AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload, AuthoritySchemaInput,
    CollectionBinding, ConfigurationId, ReplicationLimits, WalRole, decode_metadata,
    encode_metadata_into,
};
use tonic::transport::{Certificate, Channel, ClientTlsConfig, Endpoint, Identity};

const CLUSTER: [u8; 16] = [0x31; 16];
const TENANT: TenantId = TenantId::from_bytes([0x41; 16]);
const DATABASE: DatabaseId = DatabaseId::from_bytes([0x51; 16]);
const SHARD: ShardId = ShardId::new(7);
const RECEIVER: ReplicaId = ReplicaId::from_bytes([0x61; 16]);
const SENDER: ReplicaId = ReplicaId::from_bytes([0x71; 16]);
const RECEIVER_INCARNATION: [u8; 16] = [0x62; 16];
const SENDER_INCARNATION: [u8; 16] = [0x72; 16];
const RECEIVER_NAME: &str = "member-a.terrabase.test";
const SENDER_NAME: &str = "member-b.terrabase.test";
const PROCESS_MEMORY_LIMIT_BYTES: &str = "4294967296";
const PROCESS_MEMORY_HEADROOM_BYTES: &str = "1073741824";
const CONNECTIONS_ACTIVE_METRIC: &str = "terrabase_replication_connections_active";
#[cfg(feature = "replication-test-hooks")]
const STREAMS_ACTIVE_METRIC: &str = "terrabase_replication_streams_active";
#[cfg(feature = "replication-test-hooks")]
const APPEND_WORKERS_ACTIVE_METRIC: &str = "terrabase_replication_append_workers_active";
// Server startup must use a real unified cgroup-v2 mount with finite
// memory.max >= this process cap; the test does not fabricate cgroup inputs.
const STARTUP_TIMEOUT: Duration = Duration::from_secs(20);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(12);

static NEXT_TEST_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn committed_lz4_group_gets_durable_ack_and_frontier_survives_process_restart() {
    let directory = TestDirectory::new();
    let pki = TestPki::create(directory.path());
    let [first_commit, second_commit] = source_commits(directory.path(), pki.configuration);
    assert_eq!(first_commit.event.sequence, 1);
    assert_eq!(second_commit.event.sequence, 2);
    assert!(
        !first_commit.wal_frame().is_empty(),
        "the sender must use a retained complete source WAL group"
    );

    let mut server = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut server);

    let mut client = replication_client(&pki, server.replication_port).await;
    let hello = make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let welcome = open_stream(&mut client, vec![hello.clone()]).await;
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );

    assert_gap_does_not_advance(&mut client, &pki, &hello, &second_commit).await;

    let encoded = encode_transport_group(&first_commit, &pki);
    assert_eq!(
        encoded.batch().codec,
        v1::PayloadCodec::Lz4BlockV1 as i32,
        "the process boundary must exercise LZ4 application encoding"
    );
    let group = encoded.batch().clone();
    let responses = send_group(&mut client, hello, group).await;
    drop(encoded);

    assert_durable_ack_context(&responses, first_commit.event.sequence, 2, &pki);

    let status = server.terminate_and_wait();
    assert!(
        status.success(),
        "receiver did not shut down cleanly: {status}"
    );
    drop(client);

    assert_recovered_replay(directory.path(), &pki, &first_commit, 2).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn negotiated_format_two_group_is_durable_and_format_mismatch_is_rejected() {
    let directory = TestDirectory::new();
    let pki = TestPki::create(directory.path());
    let [first_commit, second_commit] = source_commits(directory.path(), pki.configuration);
    let (second_metadata, second_wal) = format_two_source_group(&second_commit);

    let mut server = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut server);
    let mut client = replication_client(&pki, server.replication_port).await;

    assert_unsupported_record_format_leaves_frontier_zero(&mut client, &pki).await;
    assert_v1_group_rejected_on_v2_stream(&mut client, &pki, &first_commit).await;
    let v2_group = accept_mixed_format_groups(
        &mut client,
        &pki,
        &first_commit,
        &second_commit,
        &second_metadata,
        &second_wal,
    )
    .await;

    let status = server.terminate_and_wait();
    assert!(
        status.success(),
        "receiver did not shut down cleanly: {status}"
    );
    drop(client);

    assert_replays_after_restart(&directory, &pki, &first_commit, v2_group).await;
}

async fn assert_unsupported_record_format_leaves_frontier_zero(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    pki: &TestPki,
) {
    let unsupported =
        make_hello_with_record_format_range(pki, SENDER, SENDER_INCARNATION, RECEIVER, 3, 3);
    let error = stream_error(client, unsupported).await;
    assert_eq!(error.code(), tonic::Code::InvalidArgument, "{error}");
    assert_eq!(
        error.message(),
        "invalid replication protocol field: no common record format"
    );

    // The failed negotiation must leave the durable inbox untouched.
    let supported =
        make_hello_with_record_format_range(pki, SENDER, SENDER_INCARNATION, RECEIVER, 1, 2);
    let welcome = open_stream(client, vec![supported]).await;
    assert_eq!(welcome.record_format_version, 2);
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
}

async fn assert_v1_group_rejected_on_v2_stream(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    pki: &TestPki,
    first_commit: &tb_document::DocumentCommit,
) {
    // A selected v2 stream rejects a valid v1 batch before it earns credit or
    // any durability/application acknowledgment.
    let v1_group = encode_transport_group(first_commit, pki);
    let (responses, error) = send_group_until_status(
        client,
        make_hello_with_record_format_range(pki, SENDER, SENDER_INCARNATION, RECEIVER, 1, 2),
        v1_group.batch().clone(),
    )
    .await;
    drop(v1_group);
    assert_eq!(error.code(), tonic::Code::InvalidArgument, "{error}");
    assert_eq!(
        error.message(),
        "committed group record format differs from selected Welcome"
    );
    assert!(responses.iter().any(|response| matches!(
        response.frame.as_ref(),
        Some(v1::sync_response::Frame::Welcome(welcome))
            if welcome.record_format_version == 2
    )));
    assert!(responses.iter().all(|response| !matches!(
        response.frame.as_ref(),
        Some(
            v1::sync_response::Frame::Credit(_)
                | v1::sync_response::Frame::Durable(_)
                | v1::sync_response::Frame::Applied(_)
        )
    )));

    let unchanged = open_stream(
        client,
        vec![make_hello_with_record_format_range(
            pki,
            SENDER,
            SENDER_INCARNATION,
            RECEIVER,
            2,
            2,
        )],
    )
    .await;
    assert_eq!(
        frontier_sequence(&unchanged, SENDER, SENDER_INCARNATION),
        Some(0),
        "a mismatched group must not change the durable frontier"
    );
}

async fn accept_mixed_format_groups(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    pki: &TestPki,
    first_commit: &tb_document::DocumentCommit,
    second_commit: &tb_document::DocumentCommit,
    second_metadata: &[u8],
    second_wal: &[u8],
) -> tb_protocol::EncodedCommittedGroup {
    // D1 bytes and ordinals in the v2 source remain those of the real commit.
    let v1_hello = make_hello(pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let v1_responses = send_group(
        client,
        v1_hello,
        encode_transport_group(first_commit, pki).batch().clone(),
    )
    .await;
    assert_durable_ack_context(&v1_responses, 1, 1, pki);

    let v2_group = encode_transport_group_parts(second_commit, pki, 2, second_metadata, second_wal);
    assert_eq!(v2_group.batch().record_format_version, 2);
    let v2_hello =
        make_hello_with_record_format_range(pki, SENDER, SENDER_INCARNATION, RECEIVER, 1, 2);
    let welcome = open_stream(client, vec![v2_hello.clone()]).await;
    assert_eq!(welcome.record_format_version, 2);
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(1)
    );
    let v2_responses = send_group(client, v2_hello, v2_group.batch().clone()).await;
    assert!(v2_responses.iter().any(|response| matches!(
        response.frame.as_ref(),
        Some(v1::sync_response::Frame::Welcome(welcome))
            if welcome.record_format_version == 2
    )));
    assert_durable_ack_context(&v2_responses, 2, 2, pki);
    v2_group
}

async fn assert_replays_after_restart(
    directory: &TestDirectory,
    pki: &TestPki,
    first_commit: &tb_document::DocumentCommit,
    v2_group: tb_protocol::EncodedCommittedGroup,
) {
    let mut restarted = RunningServer::spawn(directory.path(), pki);
    wait_until_ready(&mut restarted);
    let mut client = replication_client(pki, restarted.replication_port).await;
    let v2_replay_hello =
        make_hello_with_record_format_range(pki, SENDER, SENDER_INCARNATION, RECEIVER, 2, 2);
    let recovered = open_stream(&mut client, vec![v2_replay_hello.clone()]).await;
    assert_eq!(recovered.record_format_version, 2);
    assert_eq!(
        frontier_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(2),
        "the mixed-format durable prefix must survive restart"
    );
    assert_eq!(
        frontier_applied_sequence(&recovered, SENDER, SENDER_INCARNATION),
        None
    );
    let replayed_v2 = send_group(&mut client, v2_replay_hello, v2_group.batch().clone()).await;
    assert_durable_ack_context(&replayed_v2, 2, 2, pki);
    drop(v2_group);

    let v1_replay_hello = make_hello(pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let v1_replay_welcome = open_stream(&mut client, vec![v1_replay_hello.clone()]).await;
    assert_eq!(v1_replay_welcome.record_format_version, 1);
    assert_eq!(
        frontier_sequence(&v1_replay_welcome, SENDER, SENDER_INCARNATION),
        Some(2)
    );
    let replayed_v1 = send_group(
        &mut client,
        v1_replay_hello,
        encode_transport_group(first_commit, pki).batch().clone(),
    )
    .await;
    assert_durable_ack_context(&replayed_v1, 1, 2, pki);

    let status = restarted.terminate_and_wait();
    assert!(
        status.success(),
        "restarted receiver did not shut down cleanly: {status}"
    );
}

async fn assert_gap_does_not_advance(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    pki: &TestPki,
    hello: &v1::SyncRequest,
    second_commit: &tb_document::DocumentCommit,
) {
    // Sequence 2 is a real source commit but sequence 1 has not been offered.
    let encoded = encode_transport_group(second_commit, pki);
    let responses = send_group(client, hello.clone(), encoded.batch().clone()).await;
    drop(encoded);
    assert!(
        responses.iter().any(|response| matches!(
            response.frame.as_ref(),
            Some(v1::sync_response::Frame::Credit(_))
        )),
        "the synced out-of-order event returns staging credit"
    );
    assert!(
        responses.iter().all(|response| !matches!(
            response.frame.as_ref(),
            Some(v1::sync_response::Frame::Durable(_))
        )),
        "a gap must not produce a contiguous durable ACK"
    );
    let after_gap = open_stream(client, vec![hello.clone()]).await;
    assert_eq!(
        frontier_sequence(&after_gap, SENDER, SENDER_INCARNATION),
        Some(0),
        "a staged sequence 2 cannot advance past the missing sequence 1"
    );
}

fn assert_durable_ack_context(
    responses: &[v1::SyncResponse],
    event_sequence: u64,
    durable_prefix: u64,
    pki: &TestPki,
) {
    let durable = durable_ack(responses);
    assert_eq!(durable.durable_prefix, durable_prefix);
    let acked_event = durable
        .context
        .as_ref()
        .and_then(|context| context.event_id.as_ref())
        .expect("durable ACK binds its committed event");
    assert_eq!(acked_event.sequence, event_sequence);
    assert_eq!(
        acked_event
            .origin
            .as_ref()
            .map(|origin| origin.replica_id.as_ref()),
        Some(SENDER.to_bytes().as_slice())
    );
    let ack_context = durable.context.as_ref().expect("durable ACK context");
    assert_eq!(ack_context.membership_epoch, 1);
    assert_eq!(
        ack_context.configuration_id.as_ref(),
        pki.configuration.to_bytes().as_slice()
    );
    assert_eq!(
        ack_context
            .acknowledging_peer
            .as_ref()
            .map(|identity| identity.member_id.as_ref()),
        Some(RECEIVER.to_bytes().as_slice())
    );
    assert!(
        responses.iter().all(|response| {
            !matches!(
                response.frame.as_ref(),
                Some(v1::sync_response::Frame::Applied(_))
            )
        }),
        "inbox durability does not assert document application"
    );
}

async fn assert_recovered_replay(
    directory: &Path,
    pki: &TestPki,
    first_commit: &tb_document::DocumentCommit,
    expected_prefix: u64,
) {
    let mut restarted = RunningServer::spawn(directory, pki);
    wait_until_ready(&mut restarted);
    let mut client = replication_client(pki, restarted.replication_port).await;
    let hello = make_hello(pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let recovered = open_stream(&mut client, vec![hello.clone()]).await;
    assert_eq!(
        frontier_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(expected_prefix),
        "Welcome must report the recovered synced prefix"
    );
    assert_eq!(
        frontier_applied_sequence(&recovered, SENDER, SENDER_INCARNATION),
        None
    );

    // Simulate losing the original ACK and require the same durable marker.
    let replay = encode_transport_group(first_commit, pki);
    let replayed = send_group(&mut client, hello.clone(), replay.batch().clone()).await;
    drop(replay);
    assert_durable_ack_context(&replayed, first_commit.event.sequence, expected_prefix, pki);
    let after_replay = open_stream(&mut client, vec![hello]).await;
    assert_eq!(
        frontier_sequence(&after_replay, SENDER, SENDER_INCARNATION),
        Some(expected_prefix),
        "replaying an existing marker must not move the durable frontier"
    );

    let status = restarted.terminate_and_wait();
    assert!(
        status.success(),
        "restarted receiver did not shut down cleanly: {status}"
    );
}

#[cfg(feature = "replication-test-hooks")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn canceled_blocked_append_releases_guards_and_replays_after_restart() {
    let directory = TestDirectory::new();
    let pki = TestPki::create(directory.path());
    let [first_commit, _second_commit] = source_commits(directory.path(), pki.configuration);
    let barrier_directory = directory.path().join("append-barrier");
    fs::create_dir(&barrier_directory).expect("create empty append barrier directory");
    let mut server =
        RunningServer::spawn_with_append_barrier(directory.path(), &pki, &barrier_directory);
    wait_until_ready(&mut server);

    cancel_blocked_append(&mut server, &pki, &first_commit, &barrier_directory).await;

    let mut client = replication_client(&pki, server.replication_port).await;
    let after_cancel = open_stream(
        &mut client,
        vec![make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER)],
    )
    .await;
    assert_eq!(
        frontier_sequence(&after_cancel, SENDER, SENDER_INCARNATION),
        Some(1),
        "the detached append completes durably after its caller cancels"
    );
    drop(client);

    let status = server.terminate_and_wait();
    assert!(
        status.success(),
        "receiver did not shut down cleanly after canceled append: {status}"
    );

    // Restart without the one-shot test hook, then replay the exact event to
    // prove that uncertain ACK delivery remains safe after process recovery.
    assert_recovered_replay(directory.path(), &pki, &first_commit, 1).await;
}

#[cfg(feature = "replication-test-hooks")]
async fn cancel_blocked_append(
    server: &mut RunningServer,
    pki: &TestPki,
    commit: &tb_document::DocumentCommit,
    barrier_directory: &Path,
) {
    let mut client = replication_client(pki, server.replication_port).await;
    let hello = make_hello(pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let encoded = encode_transport_group(commit, pki);
    let mut responses = client
        .sync(stream::iter(vec![
            hello,
            v1::SyncRequest {
                frame: Some(v1::sync_request::Frame::CommittedGroup(
                    encoded.batch().clone(),
                )),
            },
        ]))
        .await
        .expect("open Sync stream for cancellable append")
        .into_inner();
    let welcome = tokio::time::timeout(Duration::from_secs(10), responses.message())
        .await
        .expect("timed out waiting for cancellable append Welcome")
        .expect("read cancellable append Welcome")
        .expect("Sync stream should begin with Welcome");
    let welcome = match welcome.frame {
        Some(v1::sync_response::Frame::Welcome(welcome)) => welcome,
        other => panic!("expected Welcome before append barrier, got {other:?}"),
    };
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );

    wait_for_file(
        server,
        &barrier_directory.join("entered"),
        Duration::from_secs(10),
    );
    assert_eq!(metric_value(server, STREAMS_ACTIVE_METRIC), Some(1.0));
    assert_eq!(
        metric_value(server, APPEND_WORKERS_ACTIVE_METRIC),
        Some(1.0)
    );
    drop(encoded);

    // Closing the response stream cancels the RPC while its blocking append
    // owns the stream and worker guards. The already-running append continues
    // after the test releases it, but the canceled RPC cannot receive its ACK.
    drop(responses);
    drop(client);
    wait_for_file(
        server,
        &barrier_directory.join("cancelled"),
        Duration::from_secs(10),
    );
    assert_eq!(metric_value(server, STREAMS_ACTIVE_METRIC), Some(1.0));
    assert_eq!(
        metric_value(server, APPEND_WORKERS_ACTIVE_METRIC),
        Some(1.0)
    );
    fs::write(barrier_directory.join("release"), b"release\n")
        .expect("release the blocked append worker");
    wait_for_metric(server, STREAMS_ACTIVE_METRIC, 0.0, Duration::from_secs(10));
    wait_for_metric(
        server,
        APPEND_WORKERS_ACTIVE_METRIC,
        0.0,
        Duration::from_secs(10),
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn valid_member_certificate_with_a_different_hello_identity_is_rejected() {
    let directory = TestDirectory::new();
    let pki = TestPki::create(directory.path());
    let [first_commit, _second_commit] = source_commits(directory.path(), pki.configuration);
    let mut server = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut server);

    assert_tls_peer_rejected(
        &pki,
        server.replication_port,
        Some((&pki.untrusted_certificate_pem, &pki.untrusted_key_pem)),
        "client certificate signed by an untrusted CA",
    )
    .await;
    assert_tls_peer_rejected(
        &pki,
        server.replication_port,
        None,
        "client without a certificate",
    )
    .await;

    let mut client = replication_client(&pki, server.replication_port).await;

    // The TLS peer is the signed sender member, while Hello claims the signed
    // receiver member. The chain is valid; its exact leaf does not match Hello.
    let mismatched = make_hello(&pki, RECEIVER, RECEIVER_INCARNATION, RECEIVER);
    let error = stream_error(&mut client, mismatched).await;
    assert_eq!(error.code(), tonic::Code::Unauthenticated, "{error}");

    // Authentication succeeds for the sender leaf, but the server must not
    // negotiate the identity codec even when the peer offers it alone.
    let identity_only = make_hello_with_codecs(
        &pki,
        SENDER,
        SENDER_INCARNATION,
        RECEIVER,
        vec![v1::PayloadCodec::Identity as i32],
    );
    let error = stream_error(&mut client, identity_only).await;
    assert_eq!(error.code(), tonic::Code::InvalidArgument, "{error}");
    assert!(
        error.message().contains("no common application codec"),
        "{error}"
    );

    let encoded = encode_transport_group(&first_commit, &pki);
    let mut over_limit = encoded.batch().clone();
    over_limit.wal_group_bytes = u32::try_from(tb_core::MAX_WAL_GROUP_BYTES + 1)
        .expect("oversized WAL declaration fits u32");
    over_limit.decoded_application_bytes = over_limit
        .event_metadata_bytes
        .checked_add(over_limit.wal_group_bytes)
        .expect("oversized decoded length fits u32");
    let error = send_overlimit_group(
        &mut client,
        make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER),
        over_limit,
    )
    .await;
    drop(encoded);
    assert_eq!(error.code(), tonic::Code::InvalidArgument, "{error}");
    assert!(error.message().contains("preflight batch bytes"), "{error}");

    let valid = make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let welcome = open_stream(&mut client, vec![valid]).await;
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0),
        "a rejected identity claim must not advance the sender's durable frontier"
    );
    let status = server.terminate_and_wait();
    assert!(
        status.success(),
        "receiver did not shut down cleanly: {status}"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn silent_tls_peers_release_connection_slots_after_handshake_timeout() {
    let directory = TestDirectory::new();
    let pki = TestPki::create(directory.path());
    let mut server = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut server);

    let address = SocketAddrV4::new(Ipv4Addr::LOCALHOST, server.replication_port);
    let silent_peers = (0..8)
        .map(|_| {
            TcpStream::connect_timeout(&address.into(), Duration::from_secs(2))
                .expect("connect a silent TLS peer")
        })
        .collect::<Vec<_>>();
    wait_for_metric(
        &mut server,
        CONNECTIONS_ACTIVE_METRIC,
        8.0,
        Duration::from_secs(5),
    );
    assert!(
        server.is_ready(),
        "silent TLS peers must not make the receiver unready"
    );

    // Keep client sockets open: only the server-side TLS handshake deadline
    // should release these pre-authentication connection reservations.
    wait_for_metric(
        &mut server,
        CONNECTIONS_ACTIVE_METRIC,
        0.0,
        Duration::from_secs(20),
    );
    assert!(
        server.is_ready(),
        "replication remains ready after stalled TLS peers are closed"
    );

    let mut client = replication_client(&pki, server.replication_port).await;
    let welcome = open_stream(
        &mut client,
        vec![make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER)],
    )
    .await;
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0),
        "a valid member can open Sync after stalled TLS handshakes expire"
    );

    drop(client);
    drop(silent_peers);
    let status = server.terminate_and_wait();
    assert!(
        status.success(),
        "receiver did not shut down cleanly after stalled TLS peers: {status}"
    );
}

fn source_commits(
    directory: &Path,
    configuration: ConfigurationId,
) -> [tb_document::DocumentCommit; 2] {
    let origin = OriginId {
        replica_id: SENDER,
        incarnation: SENDER_INCARNATION,
    };
    let limits = ReplicationLimits {
        max_origins: 64,
        max_changes: 4,
        max_siblings: 4,
        max_value_bytes: tb_core::MAX_VALUE_BYTES,
        max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
    };
    let budget = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0).expect("construct source memory budget"),
    );
    let mut store = DocumentStore::open(
        DocumentStoreConfig {
            storage: tb_storage::StorageConfig::local(
                directory.join("source-store"),
                TENANT,
                WorkerId(2),
            )
            .with_clock(Arc::new(|| 1_000)),
            database: DATABASE,
            shard: SHARD,
            origin,
            epoch: MembershipEpoch::new(1),
            configuration,
            replication: limits,
        },
        budget,
    )
    .expect("open local document source");

    let collection = CollectionId::from_bytes([0x21; 16]);
    let mut schema = Vec::new();
    encode_schema_into(&mut schema, &[]).expect("encode empty document schema");
    let mut catalog = Vec::new();
    let digest = encode_catalog_into(&mut catalog, collection, 1, 0, &schema)
        .expect("encode source collection catalog");
    store
        .provision_schema(
            TransactionId::new(50_000, [0xA1; 16]),
            collection,
            1,
            0,
            &schema,
        )
        .expect("provision source document schema")
        .expect("first source schema provisioning must commit");
    let first = commit_source_document(
        &mut store,
        collection,
        digest,
        DocumentId::from_bytes([0x29; 16]),
        1,
    );
    let second = commit_source_document(
        &mut store,
        collection,
        digest,
        DocumentId::from_bytes([0x2A; 16]),
        2,
    );
    [first, second]
}

fn commit_source_document(
    store: &mut DocumentStore,
    collection: CollectionId,
    schema_digest: [u8; 32],
    document: DocumentId,
    sequence: u64,
) -> tb_document::DocumentCommit {
    let context = tb_replication::CausalContext::new();
    store
        .commit_documents(
            commit_transaction_id(sequence),
            &[DocumentWrite {
                binding: DocumentBinding {
                    collection,
                    document,
                    generation: 1,
                    schema_digest,
                },
                observed: &context,
                value: DocumentWriteValue::Live(&[]),
            }],
        )
        .expect("commit one real local document event")
}

fn commit_transaction_id(sequence: u64) -> TransactionId {
    let sequence = u8::try_from(sequence).expect("test event sequence fits byte");
    TransactionId::new(50_000, [0xA1 + sequence; 16])
}

fn format_two_source_group(commit: &tb_document::DocumentCommit) -> (Vec<u8>, Vec<u8>) {
    let limits = ReplicationLimits {
        max_origins: 64,
        max_changes: 4,
        max_siblings: 4,
        max_value_bytes: tb_core::MAX_VALUE_BYTES,
        max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
    };
    let mut metadata =
        decode_metadata(commit.metadata(), limits).expect("decode retained source commit metadata");
    assert_eq!(metadata.header.record_format, 1);
    assert_eq!(metadata.bindings.len(), 1);
    assert_eq!(metadata.changes.len(), 1);
    metadata.header.record_format = 2;

    let mut encoded_metadata = Vec::new();
    encode_metadata_into(
        &mut encoded_metadata,
        metadata.header,
        &metadata.bindings,
        &metadata.changes,
        &metadata.roles,
        limits,
    )
    .expect("encode format-two metadata for retained source event");

    let source_frame = decode_wal_frame(commit.wal_frame(), &FormatLimits::default())
        .expect("decode complete retained source WAL group");
    assert_eq!(source_frame.operation_count(), metadata.roles.len());
    let binding = metadata.bindings[0];
    let change = &metadata.changes[0];
    let (pointer_key, pointer_value) = encode_state_pointer(StatePointer {
        collection: binding.collection,
        document: change.document,
        manifest_sha256: [u8::try_from(commit.event.sequence).expect("fixture sequence fits byte");
            32],
    });
    let mut owned_operations = Vec::with_capacity(metadata.roles.len());
    for (role, operation) in metadata.roles.iter().zip(source_frame.operations()) {
        let WalOperation::Put { key, value } = operation.expect("retained source WAL operation")
        else {
            panic!("document source fixture contains only Put operations")
        };
        let (key, value) = match role {
            WalRole::Marker => (key.to_vec(), encoded_metadata.clone()),
            WalRole::Primary => {
                assert_eq!(key, pointer_key);
                (pointer_key.to_vec(), pointer_value.to_vec())
            }
            _ => (key.to_vec(), value.to_vec()),
        };
        owned_operations.push((key, value));
    }
    let operations = owned_operations
        .iter()
        .map(|(key, value)| WalOperation::Put { key, value })
        .collect::<Vec<_>>();
    let mut wal_frame = Vec::new();
    encode_wal_frame_into(
        &mut wal_frame,
        source_frame.sequence(),
        source_frame.transaction_id(),
        &operations,
        &FormatLimits::default(),
    )
    .expect("re-encode source WAL group with a format-two D0 pointer");
    (encoded_metadata, wal_frame)
}

fn encode_transport_group(
    commit: &tb_document::DocumentCommit,
    pki: &TestPki,
) -> tb_protocol::EncodedCommittedGroup {
    encode_transport_group_parts(commit, pki, 1, commit.metadata(), commit.wal_frame())
}

fn encode_transport_group_parts(
    commit: &tb_document::DocumentCommit,
    pki: &TestPki,
    record_format_version: u32,
    distributed_metadata: &[u8],
    wal_frame: &[u8],
) -> tb_protocol::EncodedCommittedGroup {
    let transfer_limits =
        v1_transfer_limits(64 * 1024 * 1024, 8, 2).expect("construct transfer limits");
    let peer_admission = AdmissionController::new(
        AdmissionLimits::new(64 * 1024 * 1024, 8, 2).expect("construct peer admission limits"),
    );
    let event = commit.event;
    let metadata = CommittedGroupMetadata {
        event_id: v1::CommittedEventId {
            database_id: event.database.to_bytes().to_vec().into(),
            shard_id: event.shard.get(),
            origin: Some(v1::OriginIdentity {
                replica_id: event.origin.replica_id.to_bytes().to_vec().into(),
                incarnation: event.origin.incarnation.to_vec().into(),
            }),
            sequence: event.sequence,
        },
        membership_epoch: 1,
        configuration_id: pki.configuration.to_bytes().to_vec().into(),
        transaction_id: commit_transaction_id(event.sequence)
            .to_bytes()
            .to_vec()
            .into(),
        event_format_version: V1_EVENT_FORMAT,
        record_format_version,
        merge_policy_version: 1,
        distributed_metadata: distributed_metadata.to_vec().into(),
        change_count: 1,
    };
    encode_committed_group(
        metadata,
        wal_frame,
        &mut Vec::new(),
        &peer_admission,
        &[v1::PayloadCodec::Lz4BlockV1],
        &transfer_limits,
    )
    .expect("encode the committed event with the negotiated LZ4 codec")
}

fn durable_ack(responses: &[v1::SyncResponse]) -> &v1::DurableAck {
    responses
        .iter()
        .find_map(|response| match response.frame.as_ref() {
            Some(v1::sync_response::Frame::Durable(ack)) => Some(ack),
            _ => None,
        })
        .expect("receiver must return a durable ACK after syncing the group")
}

struct TestPki {
    directory: PathBuf,
    configuration: ConfigurationId,
    authority_path: PathBuf,
    trust_key_path: PathBuf,
    ca_pem: String,
    server_certificate_pem: String,
    server_key_pem: String,
    sender_certificate_pem: String,
    sender_key_pem: String,
    untrusted_certificate_pem: String,
    untrusted_key_pem: String,
}

impl TestPki {
    fn create(directory: &Path) -> Self {
        let ca_key = KeyPair::generate_for(&PKCS_ED25519).expect("generate test CA key");
        let mut ca_params = CertificateParams::default();
        ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
        let ca_issuer = CertifiedIssuer::self_signed(ca_params, ca_key).expect("self-sign test CA");

        let (receiver_certificate, receiver_key) = member_certificate(&ca_issuer, RECEIVER_NAME);
        let (sender_certificate, sender_key) = member_certificate(&ca_issuer, SENDER_NAME);
        let untrusted_ca_key =
            KeyPair::generate_for(&PKCS_ED25519).expect("generate untrusted CA key");
        let mut untrusted_ca_params = CertificateParams::default();
        untrusted_ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        untrusted_ca_params.key_usages =
            vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
        let untrusted_issuer = CertifiedIssuer::self_signed(untrusted_ca_params, untrusted_ca_key)
            .expect("self-sign untrusted test CA");
        let (untrusted_certificate, untrusted_key) =
            member_certificate(&untrusted_issuer, "untrusted.terrabase.test");
        let receiver_der = receiver_certificate.der().as_ref().to_vec();
        let sender_der = sender_certificate.der().as_ref().to_vec();

        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).expect("encode authority schema");
        let collection = CollectionId::from_bytes([0x21; 16]);
        let mut catalog = Vec::new();
        let schema_digest = encode_catalog_into(&mut catalog, collection, 1, 0, &schema)
            .expect("encode authority catalog");
        let members = [
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: RECEIVER,
                    incarnation: RECEIVER_INCARNATION,
                },
                leaf_der: &receiver_der,
                server_name: RECEIVER_NAME,
                failure_domain: "region-a",
            },
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: SENDER,
                    incarnation: SENDER_INCARNATION,
                },
                leaf_der: &sender_der,
                server_name: SENDER_NAME,
                failure_domain: "region-b",
            },
        ];
        let schemas = [AuthoritySchemaInput {
            binding: CollectionBinding {
                collection,
                generation: 1,
                schema_digest,
            },
            catalog: &catalog,
        }];
        let payload = AuthorityPayload::encode(
            AuthorityCertificateScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                epoch: MembershipEpoch::new(1),
            },
            &members,
            &schemas,
        )
        .expect("encode signed authority payload");
        let configuration = payload.configuration();
        let signing_input = payload
            .signing_input()
            .expect("build authority signing input");
        let authority_key = test_authority_key();
        let public_key: [u8; 32] = authority_key
            .public_key()
            .as_ref()
            .try_into()
            .expect("authority Ed25519 public key is 32 bytes");
        let signature: [u8; 64] = authority_key
            .sign(&signing_input)
            .as_ref()
            .try_into()
            .expect("authority Ed25519 signature is 64 bytes");
        let authority = payload
            .finish(&signature, &public_key)
            .expect("finish signed authority certificate");

        let authority_path = directory.join("authority.tbca");
        let trust_key_path = directory.join("authority.pub");
        fs::write(&authority_path, authority).expect("write signed authority");
        fs::write(&trust_key_path, public_key).expect("write authority trust key");

        Self {
            directory: directory.to_path_buf(),
            configuration,
            authority_path,
            trust_key_path,
            ca_pem: ca_issuer.pem(),
            server_certificate_pem: receiver_certificate.pem(),
            server_key_pem: receiver_key.serialize_pem(),
            sender_certificate_pem: sender_certificate.pem(),
            sender_key_pem: sender_key.serialize_pem(),
            untrusted_certificate_pem: untrusted_certificate.pem(),
            untrusted_key_pem: untrusted_key.serialize_pem(),
        }
    }

    fn write_tls_files(&self) -> TlsPaths {
        let ca = self.directory.join("member-ca.pem");
        let server_certificate = self.directory.join("receiver.pem");
        let server_key = self.directory.join("receiver-key.pem");
        let sender_certificate = self.directory.join("sender.pem");
        let sender_key = self.directory.join("sender-key.pem");
        fs::write(&ca, &self.ca_pem).expect("write test CA PEM");
        fs::write(&server_certificate, &self.server_certificate_pem)
            .expect("write receiver certificate PEM");
        fs::write(&server_key, &self.server_key_pem).expect("write receiver private key PEM");
        fs::write(&sender_certificate, &self.sender_certificate_pem)
            .expect("write sender certificate PEM");
        fs::write(&sender_key, &self.sender_key_pem).expect("write sender private key PEM");
        TlsPaths {
            ca,
            server_certificate,
            server_key,
            sender_certificate,
            sender_key,
        }
    }
}

fn member_certificate(
    issuer: &CertifiedIssuer<'_, KeyPair>,
    server_name: &str,
) -> (rcgen::Certificate, KeyPair) {
    let key_pair = KeyPair::generate_for(&PKCS_ED25519).expect("generate member key");
    let mut params = CertificateParams::default();
    params.subject_alt_names = vec![SanType::DnsName(
        server_name.try_into().expect("valid test certificate name"),
    )];
    params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    params.extended_key_usages = vec![
        ExtendedKeyUsagePurpose::ServerAuth,
        ExtendedKeyUsagePurpose::ClientAuth,
    ];
    let certificate = params
        .signed_by(&key_pair, issuer)
        .expect("issue dual-use member certificate");
    (certificate, key_pair)
}

fn test_authority_key() -> Ed25519KeyPair {
    let rng = SystemRandom::new();
    let pkcs8 = Ed25519KeyPair::generate_pkcs8(&rng).expect("generate authority key");
    Ed25519KeyPair::from_pkcs8(pkcs8.as_ref()).expect("load generated authority key")
}

struct TlsPaths {
    ca: PathBuf,
    server_certificate: PathBuf,
    server_key: PathBuf,
    sender_certificate: PathBuf,
    sender_key: PathBuf,
}

struct RunningServer {
    child: Child,
    admin_port: u16,
    replication_port: u16,
    log_path: PathBuf,
}

impl RunningServer {
    fn spawn(directory: &Path, pki: &TestPki) -> Self {
        Self::spawn_inner(directory, pki, None)
    }

    #[cfg(feature = "replication-test-hooks")]
    fn spawn_with_append_barrier(
        directory: &Path,
        pki: &TestPki,
        append_barrier_directory: &Path,
    ) -> Self {
        Self::spawn_inner(directory, pki, Some(append_barrier_directory))
    }

    fn spawn_inner(
        directory: &Path,
        pki: &TestPki,
        append_barrier_directory: Option<&Path>,
    ) -> Self {
        let tls = pki.write_tls_files();
        let (admin_port, replication_port) = available_loopback_ports();
        let local_data = directory.join("receiver-local-store");
        let replication_data = directory.join("receiver-replication-data");
        let log_path = directory.join(format!("receiver-{replication_port}.log"));
        let log = File::create(&log_path).expect("create process stderr log");
        let mut command = Command::new(server_binary());
        command
            .arg("--data-dir")
            .arg(local_data)
            .arg("--memory-limit-bytes")
            .arg(PROCESS_MEMORY_LIMIT_BYTES)
            .arg("--memory-headroom-bytes")
            .arg(PROCESS_MEMORY_HEADROOM_BYTES)
            .arg("--port")
            .arg(admin_port.to_string())
            .arg("--replication-listen-addr")
            .arg(format!("127.0.0.1:{replication_port}"))
            .arg("--replication-data-dir")
            .arg(replication_data)
            .arg("--authority-certificate")
            .arg(&pki.authority_path)
            .arg("--trusted-public-key")
            .arg(&pki.trust_key_path)
            .arg("--replication-server-certificate")
            .arg(tls.server_certificate)
            .arg("--replication-server-private-key")
            .arg(tls.server_key)
            .arg("--replication-client-ca-roots")
            .arg(tls.ca)
            .arg("--cluster-id")
            .arg(hex(&CLUSTER))
            .arg("--tenant-id")
            .arg(TENANT.to_string())
            .arg("--database-id")
            .arg(DATABASE.to_string())
            .arg("--shard-id")
            .arg(SHARD.get().to_string())
            .arg("--member-id")
            .arg(RECEIVER.to_string())
            .arg("--member-incarnation")
            .arg(hex(&RECEIVER_INCARNATION));
        if let Some(append_barrier_directory) = append_barrier_directory {
            command
                .arg("--replication-test-append-barrier-dir")
                .arg(append_barrier_directory);
        }
        let child = command
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::from(
                log.try_clone().expect("clone process log handle"),
            ))
            .spawn()
            .expect("spawn networked TerraBase server process");
        Self {
            child,
            admin_port,
            replication_port,
            log_path,
        }
    }

    fn try_wait(&mut self) -> Option<ExitStatus> {
        self.child
            .try_wait()
            .expect("inspect receiver process status")
    }

    fn terminate_and_wait(&mut self) -> ExitStatus {
        let output = Command::new("sh")
            .arg("-c")
            .arg("kill -TERM \"$1\"")
            .arg("terrabase-replication-process-test")
            .arg(self.child.id().to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .output()
            .expect("send SIGTERM to networked TerraBase server");
        assert!(
            output.status.success(),
            "failed to send SIGTERM: {}",
            String::from_utf8_lossy(&output.stderr)
        );

        let deadline = Instant::now() + SHUTDOWN_TIMEOUT;
        loop {
            if let Some(status) = self.try_wait() {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "receiver exceeded shutdown deadline"
            );
            thread::sleep(Duration::from_millis(20));
        }
    }

    fn is_ready(&mut self) -> bool {
        match exchange(
            self.admin_port,
            b"GET /readyz HTTP/1.1\r\nHost: localhost\r\n\r\n",
        ) {
            Ok(response) => response.starts_with("HTTP/1.1 200"),
            Err(_) => false,
        }
    }

    fn stderr(&self) -> String {
        fs::read_to_string(&self.log_path)
            .unwrap_or_else(|error| format!("could not read {}: {error}", self.log_path.display()))
    }
}

impl Drop for RunningServer {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

async fn replication_client(
    pki: &TestPki,
    port: u16,
) -> v1::replication_client::ReplicationClient<Channel> {
    let tls = pki.write_tls_files();
    let ca = fs::read(&tls.ca).expect("read test client CA");
    let cert = fs::read(&tls.sender_certificate).expect("read sender certificate");
    let key = fs::read(&tls.sender_key).expect("read sender key");
    let tls =
        client_mtls_config(RECEIVER_NAME, &ca, &cert, &key).expect("build client mTLS settings");
    Endpoint::from_shared(format!("https://127.0.0.1:{port}"))
        .expect("build replication endpoint")
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(15))
        .tls_config(tls)
        .expect("configure replication mTLS")
        .connect()
        .await
        .map(v1::replication_client::ReplicationClient::new)
        .expect("connect to replication receiver")
}

async fn assert_tls_peer_rejected(
    pki: &TestPki,
    port: u16,
    client_identity: Option<(&str, &str)>,
    case: &str,
) {
    let mut tls = ClientTlsConfig::new()
        .domain_name(RECEIVER_NAME)
        .ca_certificate(Certificate::from_pem(pki.ca_pem.as_bytes()));
    if let Some((certificate, private_key)) = client_identity {
        tls = tls.identity(Identity::from_pem(
            certificate.as_bytes(),
            private_key.as_bytes(),
        ));
    }
    let endpoint = Endpoint::from_shared(format!("https://127.0.0.1:{port}"))
        .expect("build negative mTLS endpoint")
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(15))
        .tls_config(tls)
        .expect("configure negative mTLS endpoint");
    let Ok(channel) = endpoint.connect().await else {
        return;
    };
    let mut client = v1::replication_client::ReplicationClient::new(channel);
    let mut responses = match client
        .sync(stream::iter(vec![make_hello(
            pki,
            SENDER,
            SENDER_INCARNATION,
            RECEIVER,
        )]))
        .await
    {
        Ok(response) => response.into_inner(),
        Err(_) => return,
    };
    loop {
        match tokio::time::timeout(Duration::from_secs(10), responses.message())
            .await
            .unwrap_or_else(|_| panic!("{case}: unauthenticated Sync remained open"))
        {
            Err(_) | Ok(None) => return,
            Ok(Some(response)) => assert!(
                !matches!(
                    response.frame.as_ref(),
                    Some(
                        v1::sync_response::Frame::Welcome(_)
                            | v1::sync_response::Frame::Received(_)
                            | v1::sync_response::Frame::Durable(_)
                            | v1::sync_response::Frame::Applied(_)
                    )
                ),
                "{case}: rejected mTLS peer received an acceptance response"
            ),
        }
    }
}

fn make_hello(
    pki: &TestPki,
    claimed_member: ReplicaId,
    claimed_incarnation: [u8; 16],
    expected_remote: ReplicaId,
) -> v1::SyncRequest {
    make_hello_with_codecs(
        pki,
        claimed_member,
        claimed_incarnation,
        expected_remote,
        vec![v1::PayloadCodec::Lz4BlockV1 as i32],
    )
}

fn make_hello_with_codecs(
    pki: &TestPki,
    claimed_member: ReplicaId,
    claimed_incarnation: [u8; 16],
    expected_remote: ReplicaId,
    codecs: Vec<i32>,
) -> v1::SyncRequest {
    make_hello_with_record_format_range_and_codecs(
        pki,
        claimed_member,
        claimed_incarnation,
        expected_remote,
        1,
        1,
        codecs,
    )
}

fn make_hello_with_record_format_range(
    pki: &TestPki,
    claimed_member: ReplicaId,
    claimed_incarnation: [u8; 16],
    expected_remote: ReplicaId,
    minimum_record_format: u32,
    maximum_record_format: u32,
) -> v1::SyncRequest {
    make_hello_with_record_format_range_and_codecs(
        pki,
        claimed_member,
        claimed_incarnation,
        expected_remote,
        minimum_record_format,
        maximum_record_format,
        vec![v1::PayloadCodec::Lz4BlockV1 as i32],
    )
}

fn make_hello_with_record_format_range_and_codecs(
    pki: &TestPki,
    claimed_member: ReplicaId,
    claimed_incarnation: [u8; 16],
    expected_remote: ReplicaId,
    minimum_record_format: u32,
    maximum_record_format: u32,
    codecs: Vec<i32>,
) -> v1::SyncRequest {
    let protocol = v1::VersionRange {
        minimum: Some(v1::ProtocolVersion {
            major: V1_PROTOCOL_MAJOR,
            minor: V1_PROTOCOL_MINOR,
        }),
        maximum: Some(v1::ProtocolVersion {
            major: V1_PROTOCOL_MAJOR,
            minor: V1_PROTOCOL_MINOR,
        }),
    };
    let record_formats = v1::VersionRange {
        minimum: Some(v1::ProtocolVersion {
            major: minimum_record_format,
            minor: 0,
        }),
        maximum: Some(v1::ProtocolVersion {
            major: maximum_record_format,
            minor: 0,
        }),
    };
    let limits =
        v1_transfer_limits(64 * 1024 * 1024, 8, 2).expect("construct Hello transfer limits");
    v1::SyncRequest {
        frame: Some(v1::sync_request::Frame::Hello(v1::Hello {
            local_identity: Some(v1::PeerIdentity {
                tenant_id: TENANT.to_bytes().to_vec().into(),
                database_id: DATABASE.to_bytes().to_vec().into(),
                shard_id: SHARD.get(),
                member_id: claimed_member.to_bytes().to_vec().into(),
                member_incarnation: claimed_incarnation.to_vec().into(),
                membership_epoch: 1,
                configuration_id: pki.configuration.to_bytes().to_vec().into(),
            }),
            expected_remote_member_id: expected_remote.to_bytes().to_vec().into(),
            protocol_versions: Some(protocol),
            event_format_versions: Some(protocol),
            record_format_versions: Some(record_formats),
            codecs,
            limits: Some(limits),
            frontiers: Vec::new(),
            minimum_merge_policy_version: 1,
            maximum_merge_policy_version: 1,
        })),
    }
}

async fn open_stream(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    requests: Vec<v1::SyncRequest>,
) -> v1::Welcome {
    let mut responses = client
        .sync(stream::iter(requests))
        .await
        .expect("open Sync stream")
        .into_inner();
    let response = tokio::time::timeout(Duration::from_secs(10), responses.message())
        .await
        .expect("timed out waiting for Sync response")
        .expect("read Sync response")
        .expect("Sync stream should begin with Welcome");
    match response.frame {
        Some(v1::sync_response::Frame::Welcome(welcome)) => welcome,
        other => panic!("expected Welcome, got {other:?}"),
    }
}

async fn send_group(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    hello: v1::SyncRequest,
    group: v1::CommittedGroupBatch,
) -> Vec<v1::SyncResponse> {
    let requests = vec![
        hello,
        v1::SyncRequest {
            frame: Some(v1::sync_request::Frame::CommittedGroup(group)),
        },
    ];
    let mut responses = client
        .sync(stream::iter(requests))
        .await
        .expect("open Sync stream for committed group")
        .into_inner();
    let mut collected = Vec::new();
    while let Some(response) = responses.message().await.expect("read Sync response") {
        collected.push(response);
    }
    collected
}

async fn send_group_until_status(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    hello: v1::SyncRequest,
    group: v1::CommittedGroupBatch,
) -> (Vec<v1::SyncResponse>, tonic::Status) {
    let requests = vec![
        hello,
        v1::SyncRequest {
            frame: Some(v1::sync_request::Frame::CommittedGroup(group)),
        },
    ];
    let mut responses = client
        .sync(stream::iter(requests))
        .await
        .expect("open Sync stream for record-format mismatch test")
        .into_inner();
    let mut collected = Vec::new();
    loop {
        match tokio::time::timeout(Duration::from_secs(10), responses.message())
            .await
            .expect("timed out waiting for record-format mismatch status")
        {
            Err(status) => return (collected, status),
            Ok(None) => panic!("mismatched record-format group ended without a status"),
            Ok(Some(response)) => collected.push(response),
        }
    }
}

async fn send_overlimit_group(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    hello: v1::SyncRequest,
    group: v1::CommittedGroupBatch,
) -> tonic::Status {
    let requests = vec![
        hello,
        v1::SyncRequest {
            frame: Some(v1::sync_request::Frame::CommittedGroup(group)),
        },
    ];
    let mut responses = client
        .sync(stream::iter(requests))
        .await
        .expect("open Sync stream for over-limit preflight test")
        .into_inner();
    loop {
        match tokio::time::timeout(Duration::from_secs(10), responses.message())
            .await
            .expect("timed out waiting for over-limit response")
        {
            Err(status) => return status,
            Ok(None) => panic!("over-limit group ended without a rejection status"),
            Ok(Some(response)) => assert!(
                !matches!(
                    response.frame.as_ref(),
                    Some(
                        v1::sync_response::Frame::Received(_)
                            | v1::sync_response::Frame::Durable(_)
                            | v1::sync_response::Frame::Applied(_)
                    )
                ),
                "preflight-rejected group received an acknowledgment"
            ),
        }
    }
}

async fn stream_error(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    hello: v1::SyncRequest,
) -> tonic::Status {
    let mut responses = client
        .sync(stream::iter(vec![hello]))
        .await
        .expect("open Sync stream for identity rejection")
        .into_inner();
    tokio::time::timeout(Duration::from_secs(10), responses.message())
        .await
        .expect("timed out waiting for identity rejection")
        .expect_err("rejected Hello must fail before Welcome")
}

fn frontier_sequence(
    welcome: &v1::Welcome,
    replica: ReplicaId,
    incarnation: [u8; 16],
) -> Option<u64> {
    welcome
        .frontiers
        .iter()
        .find(|frontier| frontier_matches(frontier, replica, incarnation))
        .and_then(|frontier| frontier.durable_sequence)
}

fn frontier_applied_sequence(
    welcome: &v1::Welcome,
    replica: ReplicaId,
    incarnation: [u8; 16],
) -> Option<u64> {
    welcome
        .frontiers
        .iter()
        .find(|frontier| frontier_matches(frontier, replica, incarnation))
        .and_then(|frontier| frontier.applied_sequence)
}

fn frontier_matches(
    frontier: &v1::PeerFrontier,
    replica: ReplicaId,
    incarnation: [u8; 16],
) -> bool {
    frontier.origin.as_ref().is_some_and(|origin| {
        origin.replica_id.as_ref() == replica.to_bytes().as_slice()
            && origin.incarnation.as_ref() == incarnation.as_slice()
    })
}

fn wait_until_ready(server: &mut RunningServer) {
    let deadline = Instant::now() + STARTUP_TIMEOUT;
    loop {
        if server.is_ready() {
            return;
        }
        if let Some(status) = server.try_wait() {
            panic!(
                "receiver exited before readiness with status {status}\n{}",
                server.stderr()
            );
        }
        assert!(
            Instant::now() < deadline,
            "receiver did not become ready\n{}",
            server.stderr()
        );
        thread::sleep(Duration::from_millis(25));
    }
}

#[cfg(feature = "replication-test-hooks")]
fn wait_for_file(server: &mut RunningServer, path: &Path, timeout: Duration) {
    let deadline = Instant::now() + timeout;
    loop {
        if path.is_file() {
            return;
        }
        if let Some(status) = server.try_wait() {
            panic!(
                "receiver exited before append barrier marker {} appeared with status {status}\n{}",
                path.display(),
                server.stderr()
            );
        }
        assert!(
            Instant::now() < deadline,
            "append barrier marker {} did not appear\n{}",
            path.display(),
            server.stderr()
        );
        thread::sleep(Duration::from_millis(10));
    }
}

fn metric_value(server: &RunningServer, metric: &str) -> Option<f64> {
    let response = exchange(
        server.admin_port,
        b"GET /metrics HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
    )
    .ok()?;
    let (_, body) = response.split_once("\r\n\r\n")?;
    body.lines().find_map(|line| {
        let (name, value) = line.split_once(' ')?;
        (name == metric)
            .then(|| value.parse::<f64>().ok())
            .flatten()
    })
}

fn wait_for_metric(server: &mut RunningServer, metric: &str, expected: f64, timeout: Duration) {
    let deadline = Instant::now() + timeout;
    let mut observed;
    loop {
        observed = metric_value(server, metric);
        if observed == Some(expected) {
            return;
        }
        if let Some(status) = server.try_wait() {
            panic!(
                "receiver exited while waiting for {metric}={expected} with status {status}\n{}",
                server.stderr()
            );
        }
        assert!(
            Instant::now() < deadline,
            "timed out waiting for {metric}={expected}; last value was {observed:?}\n{}",
            server.stderr()
        );
        thread::sleep(Duration::from_millis(10));
    }
}

fn exchange(port: u16, request: &[u8]) -> io::Result<String> {
    let address = SocketAddrV4::new(Ipv4Addr::LOCALHOST, port);
    let mut stream = TcpStream::connect_timeout(&address.into(), Duration::from_millis(250))?;
    stream.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream.write_all(request)?;
    let mut response = Vec::new();
    stream.read_to_end(&mut response)?;
    Ok(String::from_utf8_lossy(&response).into_owned())
}

fn available_loopback_ports() -> (u16, u16) {
    let admin = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
        .expect("reserve ephemeral loopback port");
    let replication = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
        .expect("reserve distinct ephemeral loopback port");
    (
        admin.local_addr().expect("read admin port").port(),
        replication
            .local_addr()
            .expect("read replication port")
            .port(),
    )
}

fn server_binary() -> PathBuf {
    if let Some(path) = std::env::var_os("TB_SERVER_TEST_BINARY") {
        let path = PathBuf::from(path);
        assert!(path.is_file(), "TB_SERVER_TEST_BINARY is not a file");
        return path.canonicalize().expect("canonicalize test binary");
    }
    let candidates = [
        PathBuf::from(env!("CARGO_BIN_EXE_tb-server")),
        PathBuf::from("/root/fc-harness-payloads/tb-server"),
    ];
    candidates
        .iter()
        .find(|path| path.is_file())
        .unwrap_or_else(|| panic!("could not find tb-server binary in {candidates:?}"))
        .canonicalize()
        .expect("canonicalize tb-server binary")
}

fn hex(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut output, "{byte:02x}").expect("write hex byte");
    }
    output
}

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock after Unix epoch")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "terrabase-replication-process-{}-{nonce}-{}",
            std::process::id(),
            NEXT_TEST_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&path).expect("create replication process test directory");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
