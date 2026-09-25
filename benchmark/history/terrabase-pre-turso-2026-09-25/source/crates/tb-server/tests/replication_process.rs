//! Process-level coverage for the signed-authority mTLS replication receiver.

use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, TcpListener, TcpStream};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
#[cfg(not(feature = "slow-tests"))]
use std::os::unix::net::UnixStream;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair as _};
use futures_util::{Stream, stream};
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, PKCS_ED25519, SanType,
};
#[cfg(not(feature = "slow-tests"))]
use serde_json::Value;
#[cfg(not(feature = "slow-tests"))]
use sha2::{Digest, Sha256};
use tb_core::{Attribution, MemoryCategory};
use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, DocumentId, MembershipEpoch, MemoryBudget, OriginId,
    ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
use tb_document::DocumentPoint;
use tb_document::open_distributed;
use tb_document::{DocumentStore, DocumentStoreConfig, DocumentWrite, DocumentWriteValue};
use tb_format::{DocumentBinding, encode_catalog_into, encode_schema_into};
#[cfg(feature = "slow-tests")]
use tb_format::{
    FormatLimits, StatePointer, WalOperation, decode_wal_frame, encode_state_pointer,
    encode_wal_frame_into,
};
use tb_protocol::{
    AdmissionController, AdmissionLimits, CommittedGroupMetadata, NegotiationPolicy,
    V1_EVENT_FORMAT, V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR, client_mtls_config,
    encode_committed_group, negotiate_hello, v1, v1_transfer_limits,
};
use tb_replication::{AcceptedAuthority, LocalAuthorityScope};
use tb_replication::{
    AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload, AuthoritySchemaInput,
    CollectionBinding, ConfigurationId, ReplicationLimits,
};
use tb_replication::{CursorLimits, CursorScope, ReplicaReceiver};
#[cfg(feature = "slow-tests")]
use tb_replication::{WalRole, decode_metadata, encode_metadata_into};
#[cfg(not(feature = "slow-tests"))]
use tb_server::{ReplicationConfig, ReplicationOutboundTls};
#[cfg(feature = "slow-tests")]
use tonic::transport::ClientTlsConfig;
use tonic::transport::{Certificate, Channel, Endpoint, Identity, Server, ServerTlsConfig};

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
#[cfg(feature = "slow-tests")]
const CONNECTIONS_ACTIVE_METRIC: &str = "terrabase_replication_connections_active";
#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
const STREAMS_ACTIVE_METRIC: &str = "terrabase_replication_streams_active";
#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
const APPEND_WORKERS_ACTIVE_METRIC: &str = "terrabase_replication_append_workers_active";
// Server startup must use a real unified cgroup-v2 mount with finite
// memory.max >= this process cap; the test does not fabricate cgroup inputs.
const STARTUP_TIMEOUT: Duration = Duration::from_secs(20);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(12);

static NEXT_TEST_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

static NEXT_MEMBER_CONTROL_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

#[cfg(not(feature = "slow-tests"))]
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
    assert_member_control_status(&server, &pki, "0123456789abcdef0123456789abcdef", true);

    let mut client = replication_client(&pki, server.replication_port).await;
    let hello = make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let welcome = open_stream(&mut client, vec![hello.clone()]).await;
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    assert_eq!(
        frontier_applied_sequence(&welcome, SENDER, SENDER_INCARNATION),
        None
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

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn signed_v2_member_receives_document_and_reopens_public_point_read() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let [first_commit, second_commit] = source_commits(directory.path(), pki.configuration);
    let mut server = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut server);

    let mut client = replication_client(&pki, server.replication_port).await;
    let hello = make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let (observer_requests, mut observer, welcome) =
        open_persistent_stream(&mut client, hello.clone()).await;
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    assert_eq!(
        frontier_applied_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    let (writer_requests, mut writer, writer_welcome) =
        open_persistent_stream(&mut client, hello).await;
    assert_eq!(
        frontier_applied_sequence(&writer_welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    let second = encode_transport_group(&second_commit, &pki);
    assert_eq!(second.batch().codec, v1::PayloadCodec::Lz4BlockV1 as i32);
    writer_requests
        .send(committed_group_request(second.batch().clone()))
        .await
        .expect("send out-of-order v2 group");
    assert!(matches!(
        next_sync_response(&mut writer).await.frame,
        Some(v1::sync_response::Frame::Credit(_))
    ));
    assert!(
        tokio::time::timeout(Duration::from_millis(100), writer.message())
            .await
            .is_err(),
        "a durable gap must not produce an AppliedAck"
    );
    assert!(
        tokio::time::timeout(Duration::from_millis(100), observer.message())
            .await
            .is_err(),
        "an observer must not see a gap as applied"
    );
    let first = encode_transport_group(&first_commit, &pki);
    assert_eq!(first.batch().codec, v1::PayloadCodec::Lz4BlockV1 as i32);
    writer_requests
        .send(committed_group_request(first.batch().clone()))
        .await
        .expect("send missing v2 group");
    let first_response = next_sync_response(&mut writer).await;
    let Some(v1::sync_response::Frame::Durable(first_durable)) = first_response.frame else {
        panic!("first response to contiguous group must be DurableAck");
    };
    assert_eq!(first_durable.durable_prefix, 2);
    assert_ack_context(first_durable.context.as_ref(), 1, &pki);
    assert!(matches!(
        next_sync_response(&mut writer).await.frame,
        Some(v1::sync_response::Frame::Credit(_))
    ));
    let mut writer_disclosed_durable = 2;
    loop {
        match next_sync_response(&mut writer).await.frame {
            Some(v1::sync_response::Frame::Durable(ack)) => {
                writer_disclosed_durable = writer_disclosed_durable.max(ack.durable_prefix);
            }
            Some(v1::sync_response::Frame::Applied(ack)) => {
                assert!(ack.applied_prefix <= writer_disclosed_durable);
                assert_ack_context(ack.context.as_ref(), ack.applied_prefix, &pki);
                if ack.applied_prefix == 2 {
                    break;
                }
            }
            other => panic!("unexpected writer response after Credit: {other:?}"),
        }
    }
    let mut observer_disclosed_durable = 0;
    let mut observer_disclosed_applied = 0;
    loop {
        match next_sync_response(&mut observer).await.frame {
            Some(v1::sync_response::Frame::Durable(ack)) => {
                assert!(ack.durable_prefix > observer_disclosed_durable);
                assert_ack_context(ack.context.as_ref(), ack.durable_prefix, &pki);
                observer_disclosed_durable = ack.durable_prefix;
            }
            Some(v1::sync_response::Frame::Applied(ack)) => {
                assert!(ack.applied_prefix > observer_disclosed_applied);
                assert!(ack.applied_prefix <= observer_disclosed_durable);
                assert_ack_context(ack.context.as_ref(), ack.applied_prefix, &pki);
                observer_disclosed_applied = ack.applied_prefix;
                if ack.applied_prefix == 2 {
                    break;
                }
            }
            other => panic!("unexpected observer response: {other:?}"),
        }
    }
    drop(writer_requests);
    drop(observer_requests);
    drop(writer);
    drop(observer);
    drop(first);
    drop(second);
    let reconnected = open_stream(
        &mut client,
        vec![make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER)],
    )
    .await;
    assert_eq!(
        frontier_sequence(&reconnected, SENDER, SENDER_INCARNATION),
        Some(2)
    );
    assert_eq!(
        frontier_applied_sequence(&reconnected, SENDER, SENDER_INCARNATION),
        Some(2)
    );
    drop(client);

    // Sequence 1 applied on the receive path. Sequence 2 was already durable
    // but needed detached replay; prove it completed while this process was
    // still serving, before shutdown can drain or startup can recover it.
    wait_for_metric(
        &mut server,
        "terrabase_replication_document_replay_applied_total",
        1.0,
        Duration::from_secs(5),
    );

    let status = server.terminate_and_wait();
    assert!(status.success(), "v2 member failed shutdown: {status}");
    assert!(
        !directory.path().join("receiver-local-store").exists(),
        "v2 document member must not open an unrelated raw shard"
    );
    assert_reopened_v2_documents(
        directory.path(),
        &pki,
        [
            (first_commit.event, [0x29; 16]),
            (second_commit.event, [0x2A; 16]),
        ],
    );
    let mut restarted = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut restarted);
    let mut restarted_client = replication_client(&pki, restarted.replication_port).await;
    let recovered = open_stream(
        &mut restarted_client,
        vec![make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER)],
    )
    .await;
    assert_eq!(
        frontier_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(2)
    );
    assert_eq!(
        frontier_applied_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(2)
    );
    assert!(restarted.terminate_and_wait().success());
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_tls_requires_signed_leaf_after_webpki_verification() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory.path(), &pki, &budget);
    let accepted = Arc::new(AtomicUsize::new(0));

    let (good_addr, good_shutdown, good_task) = serve_test_peer_tls(
        &pki.ca_pem,
        &pki.sender_certificate_pem,
        &pki.sender_key_pem,
        Arc::clone(&accepted),
    )
    .await;
    let good_config = outbound_config(&pki, good_addr);
    let good = ReplicationOutboundTls::from_signed_view(
        authority.view(),
        good_config.outbound_peer().unwrap(),
        pki.ca_pem.as_bytes(),
        pki.server_certificate_pem.as_bytes(),
        pki.server_key_pem.as_bytes(),
    )
    .expect("bind signed outbound peer TLS");
    assert_eq!(good.peer_origin().replica_id, SENDER);
    assert!(
        ReplicationOutboundTls::from_signed_view(
            authority.view(),
            good_config.outbound_peer().unwrap(),
            b"",
            pki.server_certificate_pem.as_bytes(),
            pki.server_key_pem.as_bytes(),
        )
        .is_err(),
        "empty outbound roots cannot authenticate a server"
    );
    assert!(
        ReplicationOutboundTls::from_signed_view(
            authority.view(),
            good_config.outbound_peer().unwrap(),
            &vec![b'x'; 2 * 1024 * 1024 + 1],
            pki.server_certificate_pem.as_bytes(),
            pki.server_key_pem.as_bytes(),
        )
        .is_err(),
        "outbound roots must remain bounded"
    );
    let channel = good
        .endpoint()
        .connect()
        .await
        .expect("connect signed peer");
    let mut client = v1::replication_client::ReplicationClient::new(channel);
    client
        .sync(stream::iter(vec![make_hello(
            &pki,
            RECEIVER,
            RECEIVER_INCARNATION,
            SENDER,
        )]))
        .await
        .expect("reach RPC after signed TLS handshake");
    assert_eq!(accepted.load(Ordering::Acquire), 1);
    drop(client);
    let _ = good_shutdown.send(());
    tokio::time::timeout(Duration::from_secs(5), good_task)
        .await
        .expect("signed test TLS peer shutdown timed out")
        .expect("join signed test TLS peer");

    let (rogue_addr, rogue_shutdown, rogue_task) = serve_test_peer_tls(
        &pki.ca_pem,
        &pki.rogue_sender_certificate_pem,
        &pki.rogue_sender_key_pem,
        Arc::clone(&accepted),
    )
    .await;
    let rogue_config = outbound_config(&pki, rogue_addr);
    let rogue = ReplicationOutboundTls::from_signed_view(
        authority.view(),
        rogue_config.outbound_peer().unwrap(),
        pki.ca_pem.as_bytes(),
        pki.server_certificate_pem.as_bytes(),
        pki.server_key_pem.as_bytes(),
    )
    .expect("construct endpoint for same-CA same-DNS wrong leaf");
    let error = match rogue.endpoint().connect().await {
        Ok(_) => panic!("the WebPKI-valid same-name leaf must fail its signed fingerprint pin"),
        Err(error) => error,
    };
    let mut source: &(dyn std::error::Error + 'static) = &error;
    let mut found_signed_leaf_error = false;
    loop {
        if matches!(
            source.downcast_ref::<rustls::Error>(),
            Some(rustls::Error::General(reason))
                if reason == "TLS server leaf differs from signed replication peer"
        ) {
            found_signed_leaf_error = true;
            break;
        }
        let Some(next) = source.source() else {
            break;
        };
        source = next;
    }
    assert!(
        found_signed_leaf_error,
        "wrong signed leaf must retain its typed rustls verifier error: {error:?}"
    );
    assert_eq!(accepted.load(Ordering::Acquire), 1);
    let _ = rogue_shutdown.send(());
    rogue_task.await.expect("join rogue test TLS peer");

    let mut wrong_ca_params = CertificateParams::default();
    wrong_ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    wrong_ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    let wrong_ca = CertifiedIssuer::self_signed(
        wrong_ca_params,
        KeyPair::generate_for(&PKCS_ED25519).unwrap(),
    )
    .unwrap();
    let (wrong_ca_addr, wrong_ca_shutdown, wrong_ca_task) = serve_test_peer_tls(
        &pki.ca_pem,
        &pki.sender_certificate_pem,
        &pki.sender_key_pem,
        Arc::clone(&accepted),
    )
    .await;
    let wrong_ca_config = outbound_config(&pki, wrong_ca_addr);
    let wrong_ca_bound = ReplicationOutboundTls::from_signed_view(
        authority.view(),
        wrong_ca_config.outbound_peer().unwrap(),
        wrong_ca.pem().as_bytes(),
        pki.server_certificate_pem.as_bytes(),
        pki.server_key_pem.as_bytes(),
    )
    .expect("bind wrong outbound CA without weakening WebPKI");
    assert!(wrong_ca_bound.endpoint().connect().await.is_err());
    assert_eq!(accepted.load(Ordering::Acquire), 1);
    let _ = wrong_ca_shutdown.send(());
    wrong_ca_task.await.expect("join wrong-CA test TLS peer");
}

#[cfg(not(feature = "slow-tests"))]
#[test]
fn outbound_tls_rejects_malformed_and_excess_ca_roots() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory.path(), &pki, &budget);
    let config = outbound_config(&pki, "127.0.0.1:40231".parse().unwrap());
    let malformed_pem = b"-----BEGIN CERTIFICATE-----\n%%%\n-----END CERTIFICATE-----\n";
    let malformed_der = b"-----BEGIN CERTIFICATE-----\nAQIDBA==\n-----END CERTIFICATE-----\n";
    let excess_roots = pki.ca_pem.repeat(33);
    for (roots, case) in [
        (&malformed_pem[..], "malformed PEM"),
        (&malformed_der[..], "malformed DER"),
        (excess_roots.as_bytes(), "more than 32 CA roots"),
    ] {
        let result = ReplicationOutboundTls::from_signed_view(
            authority.view(),
            config.outbound_peer().unwrap(),
            roots,
            pki.server_certificate_pem.as_bytes(),
            pki.server_key_pem.as_bytes(),
        );
        let error = match result {
            Ok(_) => panic!("{case} must fail TLS binding before an outbound connection"),
            Err(error) => error.to_string(),
        };
        assert!(
            error.contains("invalid outbound replication TLS material"),
            "{case} rejected for the wrong reason: {error}"
        );
        if case == "more than 32 CA roots" {
            assert!(error.contains("too many certificates"), "{error}");
        }
    }
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_tls_rejects_leaf_with_wrong_signed_dns_name() {
    let directory = TestDirectory::new();
    let pki =
        TestPki::create_v2_with_signed_sender_name(directory.path(), "other-sender.terrabase.test");
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory.path(), &pki, &budget);
    let accepted = Arc::new(AtomicUsize::new(0));
    let (addr, shutdown, task) = serve_test_peer_tls(
        &pki.ca_pem,
        &pki.sender_certificate_pem,
        &pki.sender_key_pem,
        Arc::clone(&accepted),
    )
    .await;
    let config = outbound_config(&pki, addr);
    let endpoint = ReplicationOutboundTls::from_signed_view(
        authority.view(),
        config.outbound_peer().unwrap(),
        pki.ca_pem.as_bytes(),
        pki.server_certificate_pem.as_bytes(),
        pki.server_key_pem.as_bytes(),
    )
    .expect("bind signed name and exact signed leaf");
    assert!(
        endpoint.endpoint().connect().await.is_err(),
        "matching leaf hash cannot bypass WebPKI signed-name validation"
    );
    assert_eq!(accepted.load(Ordering::Acquire), 0);
    let _ = shutdown.send(());
    task.await.expect("join wrong-name test TLS peer");
}

#[cfg(not(feature = "slow-tests"))]
#[test]
fn outbound_config_rejects_self_unroutable_and_relative_roots() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let base = receiver_only_config(&pki);
    assert!(
        base.outbound_peer().is_none(),
        "receiver-only API remains valid"
    );
    let good_addr: std::net::SocketAddr = "127.0.0.1:40231".parse().unwrap();
    assert!(
        base.clone()
            .with_outbound_peer(RECEIVER, good_addr, pki.directory.join("ca.pem"))
            .is_err(),
        "local member cannot be the outbound peer"
    );
    assert!(
        base.clone()
            .with_outbound_peer(
                ReplicaId::from_bytes([0; 16]),
                good_addr,
                pki.directory.join("ca.pem")
            )
            .is_err(),
        "zero member ID cannot identify an authority peer"
    );
    for addr in [
        "0.0.0.0:40231",
        "224.0.0.1:40231",
        "255.255.255.255:40231",
        "127.0.0.1:0",
        "[ff02::1]:40231",
    ] {
        assert!(
            base.clone()
                .with_outbound_peer(SENDER, addr.parse().unwrap(), pki.directory.join("ca.pem"))
                .is_err(),
            "invalid outbound address {addr} must fail"
        );
    }
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory.path(), &pki, &budget);
    let foreign = base
        .clone()
        .with_outbound_peer(
            ReplicaId::from_bytes([0x91; 16]),
            good_addr,
            pki.directory.join("ca.pem"),
        )
        .expect("a static mapping cannot infer signed membership before authority recovery");
    assert!(
        ReplicationOutboundTls::from_signed_view(
            authority.view(),
            foreign.outbound_peer().unwrap(),
            pki.ca_pem.as_bytes(),
            pki.server_certificate_pem.as_bytes(),
            pki.server_key_pem.as_bytes(),
        )
        .is_err(),
        "outbound member must be the sole other signed member"
    );
    assert!(
        base.with_outbound_peer(SENDER, good_addr, PathBuf::from("relative-ca.pem"))
            .is_err(),
        "outbound server roots must use an absolute path"
    );
}

#[cfg(not(feature = "slow-tests"))]
#[test]
fn outbound_process_refuses_partial_mapping_and_v1_authority() {
    let directory = TestDirectory::new();
    let v2 = TestPki::create_v2(directory.path());
    let partial = rejected_outbound_process(
        &v2,
        &["--replication-peer-member-id".into(), SENDER.to_string()],
    );
    assert!(
        partial.contains("outbound replication requires"),
        "partial outbound mapping failure: {partial}"
    );

    let v1_directory = TestDirectory::new();
    let v1 = TestPki::create(v1_directory.path());
    let tls = v1.write_tls_files();
    let v1_error = rejected_outbound_process(
        &v1,
        &[
            "--replication-peer-member-id".into(),
            SENDER.to_string(),
            "--replication-peer-addr".into(),
            "127.0.0.1:40231".into(),
            "--replication-server-ca-roots".into(),
            tls.ca.display().to_string(),
        ],
    );
    assert!(
        v1_error.contains("signed-v2 authority with exactly two members"),
        "v1 outbound mapping failure: {v1_error}"
    );
}

#[cfg(not(feature = "slow-tests"))]
#[test]
fn outbound_process_refuses_malformed_roots_before_readiness() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let invalid_roots = directory.path().join("malformed-outbound-ca.pem");
    fs::write(
        &invalid_roots,
        b"-----BEGIN CERTIFICATE-----\nAQIDBA==\n-----END CERTIFICATE-----\n",
    )
    .expect("write malformed outbound roots");
    let error = rejected_outbound_process(
        &pki,
        &[
            "--replication-peer-member-id".into(),
            SENDER.to_string(),
            "--replication-peer-addr".into(),
            "127.0.0.1:40231".into(),
            "--replication-server-ca-roots".into(),
            invalid_roots.display().to_string(),
        ],
    );
    assert!(
        error.contains("invalid outbound replication TLS material"),
        "malformed outbound roots must fail startup: {error}"
    );
}

#[cfg(not(feature = "slow-tests"))]
#[test]
fn outbound_binding_refuses_three_member_authority() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2_three_members(directory.path());
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory.path(), &pki, &budget);
    assert_eq!(authority.view().members().len(), 3);
    let config = outbound_config(&pki, "127.0.0.1:40231".parse().unwrap());
    assert!(
        ReplicationOutboundTls::from_signed_view(
            authority.view(),
            config.outbound_peer().unwrap(),
            pki.ca_pem.as_bytes(),
            pki.server_certificate_pem.as_bytes(),
            pki.server_key_pem.as_bytes(),
        )
        .is_err(),
        "one outbound mapping must not silently under-replicate a three-member authority"
    );
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn signed_v2_process_accepts_complete_outbound_tls_binding() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let mut server = RunningServer::spawn_with_outbound_tls(directory.path(), &pki);
    wait_until_ready(&mut server);
    assert_member_control_status(&server, &pki, "fedcba9876543210fedcba9876543210", false);
    let mut client = replication_client(&pki, server.replication_port).await;
    let welcome = open_stream(
        &mut client,
        vec![make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER)],
    )
    .await;
    assert_eq!(
        frontier_applied_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    assert!(server.terminate_and_wait().success());
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_wrong_signed_leaf_fails_ready_process() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    // Keep the TCP port reserved while the process reaches readiness. The
    // sender can establish its socket, but it cannot see the wrong certificate
    // until the scripted TLS service starts below.
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0))
        .await
        .expect("reserve outbound peer address until the process is ready");
    let peer_addr = listener
        .local_addr()
        .expect("read reserved outbound peer address");
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer_addr);
    wait_until_ready(&mut server);
    assert!(
        server.is_ready(),
        "server must be ready before peer identity fails"
    );

    let (accepted_tx, mut accepted_rx) = tokio::sync::mpsc::unbounded_channel();
    let (hello_tx, mut hello_rx) = tokio::sync::mpsc::unbounded_channel();
    let mut peer = serve_scripted_test_peer_tls_with_listener_observed(
        listener,
        &pki,
        &pki.rogue_sender_certificate_pem,
        &pki.rogue_sender_key_pem,
        Some(accepted_tx),
        Some(hello_tx),
    )
    .await;
    assert!(
        matches!(
            tokio::time::timeout(Duration::from_secs(10), accepted_rx.recv()).await,
            Ok(Some(()))
        ),
        "sender did not reach the wrong-leaf TCP listener before TLS; metrics: {}\n{}",
        sender_failure_metrics(&server),
        server.stderr()
    );
    assert!(
        matches!(
            tokio::time::timeout(Duration::from_secs(10), hello_rx.recv()).await,
            Ok(Some(true))
        ),
        "sender did not provide a TLS ClientHello to the wrong-leaf peer; metrics: {}\n{}",
        sender_failure_metrics(&server),
        server.stderr()
    );
    wait_for_fatal_process_exit(&mut server, "wrong signed peer leaf").await;
    let stderr = server.stderr();
    assert!(
        stderr.contains("TLS server leaf differs from signed replication peer"),
        "process must fail for the signed leaf pin, not another TLS error: {stderr}"
    );
    assert!(
        peer.connections.try_recv().is_err(),
        "TLS identity rejection must happen before the peer receives a Sync Hello"
    );
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_idle_source_does_not_scan_or_send_groups() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let mut peer = serve_scripted_test_peer_tls(&pki).await;
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut session, hello) = connect_scripted_sender(&mut server, &pki, &mut peer).await;
    assert_sender_hello(&pki, &hello);
    session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));

    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_connected",
        1.0,
        Duration::from_secs(5),
    );
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_source_scans_total",
        0.0,
        Duration::from_secs(5),
    );
    assert_no_outbound_group_for(&mut session, Duration::from_millis(300)).await;
    assert!(server.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_received_and_credit_do_not_advance_durable_or_applied_progress() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 1);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut session, hello) = connect_scripted_sender(&mut server, &pki, &mut peer).await;
    assert_sender_hello(&pki, &hello);
    session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));

    let group = next_outbound_group(&mut session).await;
    assert_local_source_group(&pki, &group, &events[0]);
    let credit =
        u64::from(group.encoded_application_bytes) + u64::from(group.decoded_application_bytes);
    session.send_response(v1::sync_response::Frame::Received(v1::ReceivedAck {
        context: Some(sender_ack_context(&pki, 1)),
        received_prefix: 1,
    }));
    session.send_response(v1::sync_response::Frame::Credit(v1::CreditUpdate {
        granted_bytes: credit,
    }));
    tokio::time::sleep(Duration::from_millis(200)).await;
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_durable_lag_events"),
        Some(1.0),
        "ReceivedAck and returned byte credit must not move durable progress"
    );
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_applied_lag_events"),
        Some(1.0),
        "ReceivedAck and returned byte credit must not move applied progress"
    );
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_source_scans_total"),
        Some(1.0),
        "the in-flight event must not be rescanned before DurableAck"
    );
    assert_no_outbound_group_for(&mut session, Duration::from_millis(200)).await;

    session.send_response(sender_durable_ack(&pki, 1, 1));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_durable_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_applied_lag_events"),
        Some(1.0),
        "DurableAck must advance durable progress independently"
    );
    session.send_response(sender_applied_ack(&pki, 1));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert!(server.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_durable_prefix_closes_gap_without_sending_skipped_group() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 2);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut session, hello) = connect_scripted_sender(&mut server, &pki, &mut peer).await;
    assert_sender_hello(&pki, &hello);
    session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));

    let group = next_outbound_group(&mut session).await;
    assert_local_source_group(&pki, &group, &events[0]);
    session.send_response(sender_durable_ack(&pki, 1, 2));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_durable_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_applied_lag_events"),
        Some(2.0),
        "a durable prefix may close a gap while applied progress remains separate"
    );
    assert_eq!(
        metric_value(&server, "terrabase_replication_sender_source_scans_total"),
        Some(1.0),
        "the source after the acknowledged prefix must not be scanned"
    );
    assert_no_outbound_group_for(&mut session, Duration::from_millis(300)).await;
    session.send_response(sender_applied_ack(&pki, 2));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert!(server.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_idle_synthetic_ack_drops_an_unsent_undercredited_group() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 2);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut session, hello) = connect_scripted_sender(&mut server, &pki, &mut peer).await;
    assert_sender_hello(&pki, &hello);
    session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &hello,
        Some(1),
        Some(1),
        1,
    )));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_connected",
        1.0,
        Duration::from_secs(5),
    );
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_source_scans_total",
        1.0,
        Duration::from_secs(5),
    );
    assert_no_outbound_group_for(&mut session, Duration::from_millis(200)).await;

    session.send_response(sender_durable_ack(&pki, 2, 2));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_durable_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    session.send_response(sender_applied_ack(&pki, 2));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert_eq!(events[1].event.sequence, 2);
    assert_no_outbound_group_for(&mut session, Duration::from_millis(200)).await;
    assert!(server.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_process_restart_replays_from_lower_welcome_frontier() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 1);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;

    let mut first_server =
        RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut first_session, first_hello) =
        connect_scripted_sender(&mut first_server, &pki, &mut peer).await;
    first_session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &first_hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));
    let first_group = next_outbound_group(&mut first_session).await;
    assert_local_source_group(&pki, &first_group, &events[0]);
    first_session.send_response(sender_durable_ack(&pki, 1, 1));
    first_session.send_response(sender_applied_ack(&pki, 1));
    wait_for_metric(
        &mut first_server,
        "terrabase_replication_sender_durable_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    wait_for_metric(
        &mut first_server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert!(first_server.terminate_and_wait().success());
    drop(first_session);

    let mut restarted = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
    let (mut restarted_session, restarted_hello) =
        connect_scripted_sender(&mut restarted, &pki, &mut peer).await;
    restarted_session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &restarted_hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));
    let replayed = next_outbound_group(&mut restarted_session).await;
    assert_local_source_group(&pki, &replayed, &events[0]);
    assert_eq!(
        replayed.event_digest_sha256,
        first_group.event_digest_sha256
    );
    assert_eq!(
        replayed.distributed_metadata,
        first_group.distributed_metadata
    );
    assert_eq!(
        replayed.application_envelope,
        first_group.application_envelope
    );
    restarted_session.send_response(sender_durable_ack(&pki, 1, 1));
    restarted_session.send_response(sender_applied_ack(&pki, 1));
    assert!(restarted.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(feature = "slow-tests")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_slow_process_reconnect_replays_retained_source() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 1);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;
    let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);

    let (mut first_session, first_hello) =
        connect_scripted_sender(&mut server, &pki, &mut peer).await;
    first_session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &first_hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));
    let first_group = next_outbound_group(&mut first_session).await;
    assert_local_source_group(&pki, &first_group, &events[0]);
    first_session.send_response(sender_durable_ack(&pki, 1, 1));
    first_session.send_response(sender_applied_ack(&pki, 1));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );

    // Drop the peer stream after reporting full progress. The sender must
    // negotiate a fresh Welcome on reconnect and replay when the peer's
    // surviving durable frontier is lower than that prior ACK.
    drop(first_session);
    let mut second_session = peer.next_connection().await;
    let request = second_session.next_request().await;
    let Some(v1::sync_request::Frame::Hello(second_hello)) = request.frame else {
        panic!("outbound reconnect must begin with a fresh Hello");
    };
    assert_sender_hello(&pki, &second_hello);
    second_session.send_response(v1::sync_response::Frame::Welcome(scripted_welcome(
        &pki,
        &second_hello,
        Some(0),
        Some(0),
        64 * 1024 * 1024,
    )));
    let replayed = next_outbound_group(&mut second_session).await;
    assert_local_source_group(&pki, &replayed, &events[0]);
    assert_eq!(
        replayed.event_digest_sha256,
        first_group.event_digest_sha256
    );
    second_session.send_response(sender_durable_ack(&pki, 1, 1));
    second_session.send_response(sender_applied_ack(&pki, 1));
    wait_for_metric(
        &mut server,
        "terrabase_replication_sender_applied_lag_events",
        0.0,
        Duration::from_secs(5),
    );
    assert!(server.terminate_and_wait().success());
    peer.shutdown().await;
}

#[cfg(not(feature = "slow-tests"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn outbound_invalid_welcome_ack_and_credit_fail_the_process() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let events = seed_local_source_events(directory.path(), &pki, 1);
    let mut peer = serve_scripted_test_peer_tls(&pki).await;

    for case in [
        "welcome",
        "frontier",
        "ack",
        "foreign-ack",
        "applied-before-durable",
        "credit",
        "unsolicited-credit",
    ] {
        let mut server = RunningServer::spawn_with_outbound_peer(directory.path(), &pki, peer.addr);
        let (mut session, hello) = connect_scripted_sender(&mut server, &pki, &mut peer).await;
        let no_source_to_send = case == "unsolicited-credit";
        let welcome_frontier = if no_source_to_send { 1 } else { 0 };
        let mut welcome = scripted_welcome(
            &pki,
            &hello,
            Some(welcome_frontier),
            Some(welcome_frontier),
            64 * 1024 * 1024,
        );
        match case {
            "welcome" => {
                welcome
                    .local_identity
                    .as_mut()
                    .expect("scripted Welcome local identity")
                    .member_id = vec![0x99; 16].into();
                session.send_response(v1::sync_response::Frame::Welcome(welcome));
            }
            "frontier" => {
                welcome.frontiers[0].received_sequence = Some(2);
                welcome.frontiers[0].durable_sequence = Some(2);
                welcome.frontiers[0].applied_sequence = Some(2);
                session.send_response(v1::sync_response::Frame::Welcome(welcome));
            }
            "unsolicited-credit" => {
                session.send_response(v1::sync_response::Frame::Welcome(welcome));
                session.send_response(v1::sync_response::Frame::Credit(v1::CreditUpdate {
                    granted_bytes: 1,
                }));
            }
            _ => {
                session.send_response(v1::sync_response::Frame::Welcome(welcome));
                let group = next_outbound_group(&mut session).await;
                assert_local_source_group(&pki, &group, &events[0]);
                match case {
                    "ack" => session.send_response(sender_durable_ack(&pki, 1, 2)),
                    "foreign-ack" => {
                        let v1::sync_response::Frame::Durable(mut ack) =
                            sender_durable_ack(&pki, 1, 1)
                        else {
                            unreachable!("helper constructs a DurableAck")
                        };
                        ack.context
                            .as_mut()
                            .expect("ACK context")
                            .acknowledging_peer
                            .as_mut()
                            .expect("ACK peer identity")
                            .member_id = vec![0x99; 16].into();
                        session.send_response(v1::sync_response::Frame::Durable(ack));
                    }
                    "applied-before-durable" => {
                        session.send_response(sender_applied_ack(&pki, 1));
                    }
                    "credit" => {
                        let granted_bytes = u64::from(group.encoded_application_bytes)
                            + u64::from(group.decoded_application_bytes)
                            + 1;
                        session.send_response(v1::sync_response::Frame::Credit(v1::CreditUpdate {
                            granted_bytes,
                        }));
                    }
                    _ => unreachable!("cases are enumerated above"),
                }
            }
        }
        wait_for_fatal_process_exit(&mut server, case).await;
        drop(session);
    }
    peer.shutdown().await;
}

struct SeededSourceEvent {
    event: tb_format::DocumentEventId,
    transaction_id: TransactionId,
}

fn seed_local_source_events(directory: &Path, pki: &TestPki, count: u64) -> Vec<SeededSourceEvent> {
    assert!(count > 0, "source seed must contain at least one event");
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let events = {
        let authority = reopen_v2_authority(directory, pki, &budget);
        let view = authority.view();
        let collection = CollectionId::from_bytes([0x21; 16]);
        let (active, _catalog) = view
            .checked_active_catalog(collection)
            .expect("signed active collection binding");
        let limits = ReplicationLimits {
            max_origins: 64,
            max_changes: tb_replication::MAX_EVENT_CHANGES,
            max_siblings: 64,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
        };
        let store = open_distributed(
            Arc::clone(&authority),
            DocumentStoreConfig {
                storage: tb_storage::StorageConfig::local(
                    view.documents_path(),
                    TENANT,
                    WorkerId(1),
                ),
                database: DATABASE,
                shard: SHARD,
                origin: view.local_origin(),
                epoch: view.epoch(),
                configuration: view.configuration(),
                replication: limits,
            },
            budget.clone(),
        )
        .expect("open signed local source document store");
        let mut events = Vec::with_capacity(count as usize);
        for sequence in 1..=count {
            let observed = tb_replication::CausalContext::new();
            let sequence_byte = u8::try_from(sequence).expect("seed sequence fits byte");
            let document_byte = 0x80_u8
                .checked_add(sequence_byte)
                .expect("seed document ID byte does not overflow");
            let expiry_base_ms = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock is after Unix epoch")
                .as_millis();
            let expires_at_unix_ms = u64::try_from(expiry_base_ms)
                .expect("current Unix time fits transaction expiry")
                .checked_add(60 * 60 * 1_000)
                .expect("test transaction expiry does not overflow");
            let nonce_byte = 0xA1_u8
                .checked_add(sequence_byte)
                .expect("seed transaction nonce byte does not overflow");
            let transaction_id = TransactionId::new(expires_at_unix_ms, [nonce_byte; 16]);
            let committed = store
                .commit_documents(
                    transaction_id,
                    &[DocumentWrite {
                        binding: DocumentBinding {
                            collection,
                            document: DocumentId::from_bytes([document_byte; 16]),
                            generation: active.generation,
                            schema_digest: active.schema_digest,
                        },
                        observed: &observed,
                        value: DocumentWriteValue::Live(&[]),
                    }],
                )
                .expect("commit a real signed local document source event");
            assert_eq!(committed.event.sequence, sequence);
            let source_frame = tb_format::decode_wal_frame(
                committed.wal_frame(),
                &tb_format::FormatLimits::default(),
            )
            .expect("decode the committed signed local source frame");
            assert_eq!(
                source_frame.transaction_id(),
                transaction_id,
                "the local source frame must retain the exact seeded retry token"
            );
            events.push(SeededSourceEvent {
                event: committed.event,
                transaction_id,
            });
        }
        assert_eq!(store.source_high_water().unwrap(), count);
        let first = store
            .next_source_after(0)
            .expect("read audited local source high-water")
            .expect("first local source event is retained");
        assert_eq!(first.event, events[0].event);
        drop(first);
        drop(store);
        events
    };
    events
}

#[cfg(not(feature = "slow-tests"))]
fn assert_reopened_v2_documents(
    directory: &Path,
    pki: &TestPki,
    events: [(tb_format::DocumentEventId, [u8; 16]); 2],
) {
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory, pki, &budget);
    let view = authority.view();
    let limits = ReplicationLimits {
        max_origins: 64,
        max_changes: tb_replication::MAX_EVENT_CHANGES,
        max_siblings: 64,
        max_value_bytes: tb_core::MAX_VALUE_BYTES,
        max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
    };
    let store = open_distributed(
        Arc::clone(&authority),
        DocumentStoreConfig {
            storage: tb_storage::StorageConfig::local(view.documents_path(), TENANT, WorkerId(1)),
            database: DATABASE,
            shard: SHARD,
            origin: view.local_origin(),
            epoch: view.epoch(),
            configuration: view.configuration(),
            replication: limits,
        },
        budget.clone(),
    )
    .expect("reopen one distributed document owner");
    let cursor_limits = CursorLimits {
        max_pending: tb_replication::MAX_CURSOR_PENDING,
        max_sequence_distance: tb_replication::MAX_CURSOR_SEQUENCE_DISTANCE,
    };
    let receivers = view
        .members()
        .filter(|member| member.origin != view.local_origin())
        .map(|member| {
            ReplicaReceiver::open(
                view,
                tb_storage::StorageConfig::local(
                    view.inbox_path(member.origin),
                    TENANT,
                    WorkerId(2),
                ),
                budget.clone(),
                CursorScope {
                    database: DATABASE,
                    shard: SHARD,
                    origin: member.origin,
                    epoch: view.epoch(),
                    configuration: view.configuration(),
                },
                cursor_limits,
            )
            .expect("reopen signed remote inbox")
        })
        .collect();
    store
        .install_receivers(receivers)
        .expect("install recovered inboxes");
    store
        .reconcile_inboxes()
        .expect("reconcile recovered D5 and inboxes");
    assert_eq!(
        store.durable_prefix(events[0].0.origin).unwrap().sequence,
        2
    );
    for (event, document_id) in events {
        let read = store
            .point_read(
                CollectionId::from_bytes([0x21; 16]),
                DocumentId::from_bytes(document_id),
            )
            .expect("public distributed point read after process shutdown");
        match read.point() {
            DocumentPoint::Value { sibling, .. } => {
                assert_eq!(sibling.event, event);
                assert_eq!(
                    tb_format::decode_document(&sibling.document_bytes)
                        .unwrap()
                        .field_count(),
                    0
                );
            }
            _ => panic!("received document must be visible after durable reopen"),
        }
    }
}

fn reopen_v2_authority(
    directory: &Path,
    pki: &TestPki,
    budget: &MemoryBudget,
) -> Arc<AcceptedAuthority> {
    let certificate = fs::read(&pki.authority_path).expect("read signed v2 authority");
    let public_key: [u8; 32] = fs::read(&pki.trust_key_path)
        .expect("read authority trust key")
        .try_into()
        .expect("32-byte authority trust key");
    Arc::new(
        AcceptedAuthority::open_or_install(
            &directory.join("receiver-replication-data"),
            &certificate,
            &public_key,
            LocalAuthorityScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                member: RECEIVER,
            },
            &budget,
            Attribution {
                tenant: TENANT,
                worker: WorkerId(1),
                category: MemoryCategory::Replication,
            },
        )
        .expect("reopen accepted v2 authority"),
    )
}

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(not(feature = "slow-tests"))]
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
    assert!(
        responses.iter().all(|response| !matches!(
            response.frame.as_ref(),
            Some(v1::sync_response::Frame::Applied(_))
        )),
        "a v1 gap must not produce an AppliedAck"
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
    assert_ack_context(durable.context.as_ref(), event_sequence, pki);
    assert!(
        responses.iter().all(|response| {
            !matches!(
                response.frame.as_ref(),
                Some(v1::sync_response::Frame::Applied(_))
            )
        }),
        "v1 inbox durability does not assert document application"
    );
}

fn assert_ack_context(context: Option<&v1::AckContext>, event_sequence: u64, pki: &TestPki) {
    let ack_context = context.expect("ACK context");
    let acked_event = ack_context
        .event_id
        .as_ref()
        .expect("ACK binds its committed event");
    assert_eq!(acked_event.sequence, event_sequence);
    assert_eq!(
        acked_event.database_id.as_ref(),
        DATABASE.to_bytes().as_slice()
    );
    assert_eq!(acked_event.shard_id, SHARD.get());
    assert_eq!(
        acked_event
            .origin
            .as_ref()
            .map(|origin| origin.replica_id.as_ref()),
        Some(SENDER.to_bytes().as_slice())
    );
    assert_eq!(
        acked_event
            .origin
            .as_ref()
            .map(|origin| origin.incarnation.as_ref()),
        Some(SENDER_INCARNATION.as_slice())
    );
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

#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
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

#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
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

#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn synced_document_survives_disconnect_before_durable_ack_and_replays_after_restart() {
    let directory = TestDirectory::new();
    let pki = TestPki::create_v2(directory.path());
    let [first_commit, _second_commit] = source_commits(directory.path(), pki.configuration);
    let barrier_directory = directory.path().join("post-sync-barrier");
    fs::create_dir(&barrier_directory).expect("create empty post-sync barrier directory");
    let mut server =
        RunningServer::spawn_with_post_sync_barrier(directory.path(), &pki, &barrier_directory);
    wait_until_ready(&mut server);

    let mut client = replication_client(&pki, server.replication_port).await;
    let encoded = encode_transport_group(&first_commit, &pki);
    let mut responses = client
        .sync(stream::iter(vec![
            make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER),
            v1::SyncRequest {
                frame: Some(v1::sync_request::Frame::CommittedGroup(
                    encoded.batch().clone(),
                )),
            },
        ]))
        .await
        .expect("open Sync stream for post-sync barrier")
        .into_inner();
    let welcome = tokio::time::timeout(Duration::from_secs(10), responses.message())
        .await
        .expect("timed out waiting for post-sync barrier Welcome")
        .expect("read post-sync barrier Welcome")
        .expect("Sync stream should begin with Welcome");
    let welcome = match welcome.frame {
        Some(v1::sync_response::Frame::Welcome(welcome)) => welcome,
        other => panic!("expected Welcome before post-sync barrier, got {other:?}"),
    };
    assert_eq!(
        frontier_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );
    assert_eq!(
        frontier_applied_sequence(&welcome, SENDER, SENDER_INCARNATION),
        Some(0)
    );

    wait_for_file(
        &mut server,
        &barrier_directory.join("entered"),
        Duration::from_secs(10),
    );
    assert_eq!(metric_value(&server, STREAMS_ACTIVE_METRIC), Some(1.0));
    assert_eq!(
        metric_value(&server, APPEND_WORKERS_ACTIVE_METRIC),
        Some(1.0)
    );
    assert!(
        tokio::time::timeout(Duration::from_millis(200), responses.message())
            .await
            .is_err(),
        "receiver must not send DurableAck or another response before releasing the post-sync barrier"
    );

    // The worker has completed decode_and_append, including durable storage,
    // but the response is still withheld. Drop the real RPC before signaling
    // shutdown so no DurableAck can reach the sender.
    drop(responses);
    drop(client);
    wait_for_file(
        &mut server,
        &barrier_directory.join("cancelled"),
        Duration::from_secs(10),
    );
    assert_eq!(metric_value(&server, STREAMS_ACTIVE_METRIC), Some(1.0));
    assert_eq!(
        metric_value(&server, APPEND_WORKERS_ACTIVE_METRIC),
        Some(1.0)
    );

    // Graceful shutdown must wait for the active append worker. This is the
    // durability boundary being exercised: data is already synced, while its
    // response remains uncertain until the connection has been lost.
    server.send_sigterm();
    thread::sleep(Duration::from_millis(200));
    assert!(
        server.try_wait().is_none(),
        "receiver exited while its post-sync append worker remained held\n{}",
        server.stderr()
    );
    fs::write(barrier_directory.join("release"), b"release\n")
        .expect("release the post-sync append worker");
    let status = server.wait_for_exit(SHUTDOWN_TIMEOUT);
    assert!(
        status.success(),
        "receiver did not shut down cleanly after releasing synced append: {status}\n{}",
        server.stderr()
    );
    drop(encoded);

    assert_reopened_v2_document(directory.path(), &pki, first_commit.event);

    // Restart without the hook. Welcome must advertise both recovered
    // frontiers, and the exact replay must return the same durable prefix.
    let mut restarted = RunningServer::spawn(directory.path(), &pki);
    wait_until_ready(&mut restarted);
    let mut restarted_client = replication_client(&pki, restarted.replication_port).await;
    let hello = make_hello(&pki, SENDER, SENDER_INCARNATION, RECEIVER);
    let recovered = open_stream(&mut restarted_client, vec![hello.clone()]).await;
    assert_eq!(
        frontier_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(1),
        "restart must report the durable prefix even though the original ACK was lost"
    );
    assert_eq!(
        frontier_applied_sequence(&recovered, SENDER, SENDER_INCARNATION),
        Some(1),
        "restart must report the recovered applied document prefix"
    );
    let replay = encode_transport_group(&first_commit, &pki);
    let replayed = send_group(&mut restarted_client, hello.clone(), replay.batch().clone()).await;
    assert_durable_ack_context(&replayed, 1, 1, &pki);
    drop(replay);
    let after_replay = open_stream(&mut restarted_client, vec![hello]).await;
    assert_eq!(
        frontier_sequence(&after_replay, SENDER, SENDER_INCARNATION),
        Some(1),
        "exact replay must not advance or roll back the durable frontier"
    );
    assert_eq!(
        frontier_applied_sequence(&after_replay, SENDER, SENDER_INCARNATION),
        Some(1),
        "exact replay must preserve the applied frontier"
    );
    drop(restarted_client);
    assert!(restarted.terminate_and_wait().success());
    assert_reopened_v2_document(directory.path(), &pki, first_commit.event);
}

#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
fn assert_reopened_v2_document(
    directory: &Path,
    pki: &TestPki,
    expected_event: tb_format::DocumentEventId,
) {
    let budget = MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).unwrap());
    let authority = reopen_v2_authority(directory, pki, &budget);
    let view = authority.view();
    let limits = ReplicationLimits {
        max_origins: 64,
        max_changes: tb_replication::MAX_EVENT_CHANGES,
        max_siblings: 64,
        max_value_bytes: tb_core::MAX_VALUE_BYTES,
        max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
    };
    let store = open_distributed(
        Arc::clone(&authority),
        DocumentStoreConfig {
            storage: tb_storage::StorageConfig::local(view.documents_path(), TENANT, WorkerId(1)),
            database: DATABASE,
            shard: SHARD,
            origin: view.local_origin(),
            epoch: view.epoch(),
            configuration: view.configuration(),
            replication: limits,
        },
        budget.clone(),
    )
    .expect("reopen distributed document receiver");
    let cursor_limits = CursorLimits {
        max_pending: tb_replication::MAX_CURSOR_PENDING,
        max_sequence_distance: tb_replication::MAX_CURSOR_SEQUENCE_DISTANCE,
    };
    let receivers = view
        .members()
        .filter(|member| member.origin != view.local_origin())
        .map(|member| {
            ReplicaReceiver::open(
                view,
                tb_storage::StorageConfig::local(
                    view.inbox_path(member.origin),
                    TENANT,
                    WorkerId(2),
                ),
                budget.clone(),
                CursorScope {
                    database: DATABASE,
                    shard: SHARD,
                    origin: member.origin,
                    epoch: view.epoch(),
                    configuration: view.configuration(),
                },
                cursor_limits,
            )
            .expect("reopen signed remote inbox")
        })
        .collect();
    store
        .install_receivers(receivers)
        .expect("install recovered inboxes");
    store
        .reconcile_inboxes()
        .expect("reconcile recovered document inbox");

    let sender_origin = OriginId {
        replica_id: SENDER,
        incarnation: SENDER_INCARNATION,
    };
    assert_eq!(
        store.durable_prefix(sender_origin).unwrap().sequence,
        1,
        "reopened inbox must retain the synced durable frontier"
    );
    assert_eq!(
        store.applied_prefix(sender_origin).unwrap(),
        1,
        "reopened D5 state must retain the applied frontier"
    );
    let read = store
        .point_read(
            CollectionId::from_bytes([0x21; 16]),
            DocumentId::from_bytes([0x29; 16]),
        )
        .expect("read the received document after process shutdown");
    match read.point() {
        DocumentPoint::Value { sibling, .. } => {
            assert_eq!(sibling.event, expected_event);
            assert_eq!(
                tb_format::decode_document(&sibling.document_bytes)
                    .unwrap()
                    .field_count(),
                0,
                "the exact committed document value must survive receiver restart"
            );
        }
        other => panic!("received document must remain visible after reopen: {other:?}"),
    }
}

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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
    #[cfg(not(feature = "slow-tests"))]
    receiver_leaf_der: Vec<u8>,
    #[cfg(not(feature = "slow-tests"))]
    rogue_sender_certificate_pem: String,
    #[cfg(not(feature = "slow-tests"))]
    rogue_sender_key_pem: String,
    server_certificate_pem: String,
    server_key_pem: String,
    sender_certificate_pem: String,
    sender_key_pem: String,
    #[cfg(feature = "slow-tests")]
    untrusted_certificate_pem: String,
    #[cfg(feature = "slow-tests")]
    untrusted_key_pem: String,
}

impl TestPki {
    fn create(directory: &Path) -> Self {
        Self::create_with_authority_version(directory, false)
    }

    fn create_v2(directory: &Path) -> Self {
        Self::create_with_authority_version(directory, true)
    }

    #[cfg(not(feature = "slow-tests"))]
    fn create_v2_with_signed_sender_name(directory: &Path, signed_name: &str) -> Self {
        Self::create_with_sender_name(directory, true, signed_name, false)
    }

    #[cfg(not(feature = "slow-tests"))]
    fn create_v2_three_members(directory: &Path) -> Self {
        Self::create_with_sender_name(directory, true, SENDER_NAME, true)
    }

    fn create_with_authority_version(directory: &Path, document_member: bool) -> Self {
        Self::create_with_sender_name(directory, document_member, SENDER_NAME, false)
    }

    fn create_with_sender_name(
        directory: &Path,
        document_member: bool,
        signed_sender_name: &str,
        third_member: bool,
    ) -> Self {
        let ca_key = KeyPair::generate_for(&PKCS_ED25519).expect("generate test CA key");
        let mut ca_params = CertificateParams::default();
        ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
        let ca_issuer = CertifiedIssuer::self_signed(ca_params, ca_key).expect("self-sign test CA");

        let (receiver_certificate, receiver_key) = member_certificate(&ca_issuer, RECEIVER_NAME);
        let (sender_certificate, sender_key) = member_certificate(&ca_issuer, SENDER_NAME);
        #[cfg(not(feature = "slow-tests"))]
        let (rogue_sender_certificate, rogue_sender_key) =
            member_certificate(&ca_issuer, SENDER_NAME);
        #[cfg(feature = "slow-tests")]
        let (untrusted_certificate, untrusted_key) = {
            let untrusted_ca_key =
                KeyPair::generate_for(&PKCS_ED25519).expect("generate untrusted CA key");
            let mut untrusted_ca_params = CertificateParams::default();
            untrusted_ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
            untrusted_ca_params.key_usages =
                vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
            let untrusted_issuer =
                CertifiedIssuer::self_signed(untrusted_ca_params, untrusted_ca_key)
                    .expect("self-sign untrusted test CA");
            member_certificate(&untrusted_issuer, "untrusted.terrabase.test")
        };
        let receiver_der = receiver_certificate.der().as_ref().to_vec();
        let sender_der = sender_certificate.der().as_ref().to_vec();
        let third_der = third_member.then(|| {
            member_certificate(&ca_issuer, "member-c.terrabase.test")
                .0
                .der()
                .as_ref()
                .to_vec()
        });

        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).expect("encode authority schema");
        let collection = CollectionId::from_bytes([0x21; 16]);
        let mut catalog = Vec::new();
        let schema_digest = encode_catalog_into(&mut catalog, collection, 1, 0, &schema)
            .expect("encode authority catalog");
        let mut members = vec![
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
                server_name: signed_sender_name,
                failure_domain: "region-b",
            },
        ];
        if let Some(third_der) = third_der.as_deref() {
            members.push(AuthorityMemberInput {
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([0x81; 16]),
                    incarnation: [0x82; 16],
                },
                leaf_der: third_der,
                server_name: "member-c.terrabase.test",
                failure_domain: "region-c",
            });
        }
        let schemas = [AuthoritySchemaInput {
            binding: CollectionBinding {
                collection,
                generation: 1,
                schema_digest,
            },
            catalog: &catalog,
        }];
        let scope = AuthorityCertificateScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: SHARD,
            epoch: MembershipEpoch::new(1),
        };
        let payload = if document_member {
            AuthorityPayload::encode_v2(scope, &members, &schemas, &[schemas[0].binding])
        } else {
            AuthorityPayload::encode(scope, &members, &schemas)
        }
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
            #[cfg(not(feature = "slow-tests"))]
            receiver_leaf_der: receiver_der,
            #[cfg(not(feature = "slow-tests"))]
            rogue_sender_certificate_pem: rogue_sender_certificate.pem(),
            #[cfg(not(feature = "slow-tests"))]
            rogue_sender_key_pem: rogue_sender_key.serialize_pem(),
            server_certificate_pem: receiver_certificate.pem(),
            server_key_pem: receiver_key.serialize_pem(),
            sender_certificate_pem: sender_certificate.pem(),
            sender_key_pem: sender_key.serialize_pem(),
            #[cfg(feature = "slow-tests")]
            untrusted_certificate_pem: untrusted_certificate.pem(),
            #[cfg(feature = "slow-tests")]
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
    #[cfg(not(feature = "slow-tests"))]
    member_control_socket: PathBuf,
    log_path: PathBuf,
}

impl RunningServer {
    fn spawn(directory: &Path, pki: &TestPki) -> Self {
        Self::spawn_inner(directory, pki, None, None)
    }

    #[cfg(not(feature = "slow-tests"))]
    fn spawn_with_outbound_tls(directory: &Path, pki: &TestPki) -> Self {
        Self::spawn_inner(directory, pki, None, Some("127.0.0.1:1".parse().unwrap()))
    }

    fn spawn_with_outbound_peer(directory: &Path, pki: &TestPki, peer_addr: SocketAddr) -> Self {
        Self::spawn_inner(directory, pki, None, Some(peer_addr))
    }

    #[cfg(feature = "replication-test-hooks")]
    fn spawn_with_append_barrier(
        directory: &Path,
        pki: &TestPki,
        append_barrier_directory: &Path,
    ) -> Self {
        Self::spawn_inner(directory, pki, Some(append_barrier_directory), None)
    }

    #[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
    fn spawn_with_post_sync_barrier(
        directory: &Path,
        pki: &TestPki,
        post_sync_barrier_directory: &Path,
    ) -> Self {
        Self::spawn_inner_with_post_sync(
            directory,
            pki,
            None,
            None,
            Some(post_sync_barrier_directory),
        )
    }

    fn spawn_inner(
        directory: &Path,
        pki: &TestPki,
        append_barrier_directory: Option<&Path>,
        outbound_peer_addr: Option<SocketAddr>,
    ) -> Self {
        Self::spawn_inner_with_post_sync(
            directory,
            pki,
            append_barrier_directory,
            outbound_peer_addr,
            None,
        )
    }

    fn spawn_inner_with_post_sync(
        directory: &Path,
        pki: &TestPki,
        append_barrier_directory: Option<&Path>,
        outbound_peer_addr: Option<SocketAddr>,
        post_sync_barrier_directory: Option<&Path>,
    ) -> Self {
        let tls = pki.write_tls_files();
        let (admin_port, replication_port) = available_loopback_ports();
        let local_data = directory.join("receiver-local-store");
        let replication_data = directory.join("receiver-replication-data");
        let member_control_directory = directory.join(format!(
            "mc-{}",
            NEXT_MEMBER_CONTROL_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&member_control_directory)
            .expect("create private member-control socket directory");
        fs::set_permissions(&member_control_directory, fs::Permissions::from_mode(0o730))
            .expect("set member-control directory mode");
        let member_control_metadata = fs::symlink_metadata(&member_control_directory)
            .expect("inspect member-control socket directory");
        assert_eq!(
            member_control_metadata.uid(),
            0,
            "member-control directory must be root-owned"
        );
        assert_eq!(
            member_control_metadata.mode() & 0o777,
            0o730,
            "member-control directory must have exact mode 0730"
        );
        assert!(
            member_control_metadata.file_type().is_dir(),
            "member-control path must be a real directory"
        );
        let member_control_socket = member_control_directory.join("m.sock");
        assert!(
            member_control_socket.as_os_str().as_bytes().len() < 108,
            "member-control Unix socket path exceeds sockaddr_un capacity"
        );
        let log_path = directory.join(format!("receiver-{replication_port}.log"));
        let log = File::create(&log_path).expect("create process stderr log");
        let outbound_ca = directory.join("outbound-server-ca.pem");
        if outbound_peer_addr.is_some() {
            fs::write(&outbound_ca, &pki.ca_pem).expect("write separate outbound CA roots");
        }
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
            .arg(hex(&RECEIVER_INCARNATION))
            .arg("--member-control-socket")
            .arg(&member_control_socket);
        if let Some(peer_addr) = outbound_peer_addr {
            command
                .arg("--replication-peer-member-id")
                .arg(SENDER.to_string())
                .arg("--replication-peer-addr")
                .arg(peer_addr.to_string())
                .arg("--replication-server-ca-roots")
                .arg(outbound_ca);
        }
        if let Some(append_barrier_directory) = append_barrier_directory {
            command
                .arg("--replication-test-append-barrier-dir")
                .arg(append_barrier_directory);
        }
        if let Some(post_sync_barrier_directory) = post_sync_barrier_directory {
            command
                .arg("--replication-test-post-sync-barrier-dir")
                .arg(post_sync_barrier_directory);
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
            #[cfg(not(feature = "slow-tests"))]
            member_control_socket,
            log_path,
        }
    }

    fn try_wait(&mut self) -> Option<ExitStatus> {
        self.child
            .try_wait()
            .expect("inspect receiver process status")
    }

    fn terminate_and_wait(&mut self) -> ExitStatus {
        self.send_sigterm();
        self.wait_for_exit(SHUTDOWN_TIMEOUT)
    }

    fn send_sigterm(&mut self) {
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
    }

    fn wait_for_exit(&mut self, timeout: Duration) -> ExitStatus {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(status) = self.try_wait() {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "receiver exceeded shutdown deadline of {timeout:?}"
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

#[cfg(not(feature = "slow-tests"))]
fn receiver_only_config(pki: &TestPki) -> ReplicationConfig {
    ReplicationConfig::new(
        SocketAddrV4::new(Ipv4Addr::LOCALHOST, 1).into(),
        pki.directory.join("outbound-test-data"),
        pki.authority_path.clone(),
        pki.trust_key_path.clone(),
        pki.directory.join("receiver.pem"),
        pki.directory.join("receiver-key.pem"),
        pki.directory.join("inbound-ca.pem"),
        LocalAuthorityScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: SHARD,
            member: RECEIVER,
        },
        RECEIVER_INCARNATION,
        PROCESS_MEMORY_LIMIT_BYTES.parse().unwrap(),
        PROCESS_MEMORY_HEADROOM_BYTES.parse().unwrap(),
    )
    .expect("valid receiver-only replication config")
}

#[cfg(not(feature = "slow-tests"))]
fn outbound_config(pki: &TestPki, peer_addr: std::net::SocketAddr) -> ReplicationConfig {
    receiver_only_config(pki)
        .with_outbound_peer(SENDER, peer_addr, pki.directory.join("outbound-ca.pem"))
        .expect("valid static outbound peer mapping")
}

#[cfg(not(feature = "slow-tests"))]
fn rejected_outbound_process(pki: &TestPki, extra: &[String]) -> String {
    let tls = pki.write_tls_files();
    let (admin_port, replication_port) = available_loopback_ports();
    let log_path = pki.directory.join("rejected-outbound.log");
    let log = File::create(&log_path).expect("create rejected outbound process log");
    let mut command = Command::new(server_binary());
    command
        .arg("--data-dir")
        .arg(pki.directory.join("rejected-local-data"))
        .arg("--memory-limit-bytes")
        .arg(PROCESS_MEMORY_LIMIT_BYTES)
        .arg("--memory-headroom-bytes")
        .arg(PROCESS_MEMORY_HEADROOM_BYTES)
        .arg("--port")
        .arg(admin_port.to_string())
        .arg("--replication-listen-addr")
        .arg(format!("127.0.0.1:{replication_port}"))
        .arg("--replication-data-dir")
        .arg(pki.directory.join("rejected-replication-data"))
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
        .arg(hex(&RECEIVER_INCARNATION))
        .args(extra)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(log));
    let mut child = command.spawn().expect("spawn rejected outbound process");
    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        let readiness = exchange(
            admin_port,
            b"GET /readyz HTTP/1.1\r\nHost: localhost\r\n\r\n",
        );
        assert!(
            !readiness.is_ok_and(|response| response.starts_with("HTTP/1.1 200")),
            "rejected outbound process exposed document readiness"
        );
        if let Some(status) = child.try_wait().expect("poll rejected process") {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            panic!("outbound config process did not refuse startup");
        }
        thread::sleep(Duration::from_millis(20));
    };
    assert!(
        !status.success(),
        "outbound config was unexpectedly accepted"
    );
    fs::read_to_string(log_path).expect("read rejected outbound process log")
}

#[cfg(not(feature = "slow-tests"))]
#[derive(Clone)]
struct CountingTestReplication(Arc<AtomicUsize>);

#[cfg(not(feature = "slow-tests"))]
#[tonic::async_trait]
impl v1::replication_server::Replication for CountingTestReplication {
    type SyncStream = stream::Empty<Result<v1::SyncResponse, tonic::Status>>;

    async fn sync(
        &self,
        _request: tonic::Request<tonic::Streaming<v1::SyncRequest>>,
    ) -> Result<tonic::Response<Self::SyncStream>, tonic::Status> {
        self.0.fetch_add(1, Ordering::AcqRel);
        Ok(tonic::Response::new(stream::empty()))
    }
}

#[cfg(not(feature = "slow-tests"))]
async fn serve_test_peer_tls(
    ca_pem: &str,
    certificate_pem: &str,
    key_pem: &str,
    accepted: Arc<AtomicUsize>,
) -> (
    std::net::SocketAddr,
    tokio::sync::oneshot::Sender<()>,
    tokio::task::JoinHandle<()>,
) {
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0))
        .await
        .expect("bind mTLS test peer");
    let addr = listener.local_addr().expect("read mTLS test peer address");
    let incoming = Box::pin(stream::unfold(listener, |listener| async move {
        Some((listener.accept().await.map(|(stream, _)| stream), listener))
    }));
    let tls = ServerTlsConfig::new()
        .identity(Identity::from_pem(certificate_pem, key_pem))
        .client_ca_root(Certificate::from_pem(ca_pem));
    let service = v1::replication_server::ReplicationServer::new(CountingTestReplication(accepted));
    let (shutdown_tx, shutdown_rx) = tokio::sync::oneshot::channel();
    let task = tokio::spawn(async move {
        Server::builder()
            .tls_config(tls)
            .expect("configure mTLS test peer")
            .serve_with_incoming_shutdown(service, incoming, async move {
                let _ = shutdown_rx.await;
            })
            .await
            .expect("serve mTLS test peer");
    });
    (addr, shutdown_tx, task)
}

struct ScriptedTestReplication {
    connections: tokio::sync::mpsc::UnboundedSender<ScriptedPeerSession>,
}

struct ScriptedPeerSession {
    requests: tonic::Streaming<v1::SyncRequest>,
    responses: tokio::sync::mpsc::UnboundedSender<Result<v1::SyncResponse, tonic::Status>>,
}

type ScriptedResponseStream =
    Pin<Box<dyn Stream<Item = Result<v1::SyncResponse, tonic::Status>> + Send + 'static>>;

#[tonic::async_trait]
impl v1::replication_server::Replication for ScriptedTestReplication {
    type SyncStream = ScriptedResponseStream;

    async fn sync(
        &self,
        request: tonic::Request<tonic::Streaming<v1::SyncRequest>>,
    ) -> Result<tonic::Response<Self::SyncStream>, tonic::Status> {
        let (responses, response_stream) = tokio::sync::mpsc::unbounded_channel();
        self.connections
            .send(ScriptedPeerSession {
                requests: request.into_inner(),
                responses,
            })
            .map_err(|_| tonic::Status::unavailable("test peer stopped accepting streams"))?;
        let response_stream = stream::unfold(response_stream, |mut receiver| async move {
            receiver.recv().await.map(|response| (response, receiver))
        });
        Ok(tonic::Response::new(Box::pin(response_stream)))
    }
}

struct ScriptedTestPeer {
    addr: SocketAddr,
    connections: tokio::sync::mpsc::UnboundedReceiver<ScriptedPeerSession>,
    shutdown: tokio::sync::oneshot::Sender<()>,
    task: tokio::task::JoinHandle<()>,
}

impl ScriptedTestPeer {
    async fn next_connection(&mut self) -> ScriptedPeerSession {
        tokio::time::timeout(Duration::from_secs(10), self.connections.recv())
            .await
            .expect("outbound sender did not connect to scripted signed peer")
            .expect("scripted peer connection queue closed")
    }

    async fn shutdown(self) {
        let _ = self.shutdown.send(());
        tokio::time::timeout(Duration::from_secs(5), self.task)
            .await
            .expect("scripted signed peer shutdown timed out")
            .expect("join scripted signed peer");
    }
}

impl ScriptedPeerSession {
    async fn next_request(&mut self) -> v1::SyncRequest {
        tokio::time::timeout(Duration::from_secs(10), self.requests.message())
            .await
            .expect("timed out waiting for outbound Sync request")
            .expect("outbound Sync request stream failed")
            .expect("outbound sender closed Sync request stream")
    }

    fn send_response(&self, frame: v1::sync_response::Frame) {
        self.responses
            .send(Ok(v1::SyncResponse { frame: Some(frame) }))
            .expect("send scripted Sync response");
    }
}

async fn serve_scripted_test_peer_tls(pki: &TestPki) -> ScriptedTestPeer {
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0))
        .await
        .expect("bind scripted signed mTLS peer");
    serve_scripted_test_peer_tls_with_listener(
        listener,
        pki,
        &pki.sender_certificate_pem,
        &pki.sender_key_pem,
    )
    .await
}

async fn serve_scripted_test_peer_tls_with_listener(
    listener: tokio::net::TcpListener,
    pki: &TestPki,
    certificate_pem: &str,
    key_pem: &str,
) -> ScriptedTestPeer {
    serve_scripted_test_peer_tls_with_listener_observed(
        listener,
        pki,
        certificate_pem,
        key_pem,
        None,
        None,
    )
    .await
}

async fn serve_scripted_test_peer_tls_with_listener_observed(
    listener: tokio::net::TcpListener,
    pki: &TestPki,
    certificate_pem: &str,
    key_pem: &str,
    accepted: Option<tokio::sync::mpsc::UnboundedSender<()>>,
    client_hello: Option<tokio::sync::mpsc::UnboundedSender<bool>>,
) -> ScriptedTestPeer {
    let addr = listener
        .local_addr()
        .expect("read scripted signed peer address");
    let incoming = Box::pin(stream::unfold(
        (listener, accepted, client_hello),
        |(listener, accepted, client_hello)| async move {
            let next = match listener.accept().await {
                Ok((stream, _)) => {
                    if let Some(signal) = &accepted {
                        let _ = signal.send(());
                    }
                    if let Some(signal) = &client_hello {
                        let mut record_type = [0_u8; 1];
                        let observed = matches!(
                            tokio::time::timeout(
                                Duration::from_secs(5),
                                stream.peek(&mut record_type)
                            )
                            .await,
                            Ok(Ok(size)) if size > 0 && record_type[0] == 22
                        );
                        let _ = signal.send(observed);
                    }
                    Ok(stream)
                }
                Err(error) => Err(error),
            };
            Some((next, (listener, accepted, client_hello)))
        },
    ));
    let tls = ServerTlsConfig::new()
        .identity(Identity::from_pem(certificate_pem, key_pem))
        .client_ca_root(Certificate::from_pem(pki.ca_pem.as_bytes()));
    let (connections, connection_rx) = tokio::sync::mpsc::unbounded_channel();
    let service =
        v1::replication_server::ReplicationServer::new(ScriptedTestReplication { connections });
    let (shutdown, shutdown_rx) = tokio::sync::oneshot::channel();
    let task = tokio::spawn(async move {
        Server::builder()
            .tls_config(tls)
            .expect("configure scripted signed peer mTLS")
            .serve_with_incoming_shutdown(service, incoming, async move {
                let _ = shutdown_rx.await;
            })
            .await
            .expect("serve scripted signed peer");
    });
    ScriptedTestPeer {
        addr,
        connections: connection_rx,
        shutdown,
        task,
    }
}

async fn connect_scripted_sender(
    server: &mut RunningServer,
    pki: &TestPki,
    peer: &mut ScriptedTestPeer,
) -> (ScriptedPeerSession, v1::Hello) {
    wait_until_ready(server);
    let mut session = peer.next_connection().await;
    let request = session.next_request().await;
    let Some(v1::sync_request::Frame::Hello(hello)) = request.frame else {
        panic!("outbound sender must begin each stream with Hello");
    };
    assert_sender_hello(pki, &hello);
    (session, hello)
}

fn scripted_peer_identity(
    pki: &TestPki,
    member: ReplicaId,
    incarnation: [u8; 16],
) -> v1::PeerIdentity {
    v1::PeerIdentity {
        tenant_id: TENANT.to_bytes().to_vec().into(),
        database_id: DATABASE.to_bytes().to_vec().into(),
        shard_id: SHARD.get(),
        member_id: member.to_bytes().to_vec().into(),
        member_incarnation: incarnation.to_vec().into(),
        membership_epoch: 1,
        configuration_id: pki.configuration.to_bytes().to_vec().into(),
    }
}

fn local_source_frontier(durable: Option<u64>, applied: Option<u64>) -> v1::PeerFrontier {
    v1::PeerFrontier {
        origin: Some(v1::OriginIdentity {
            replica_id: RECEIVER.to_bytes().to_vec().into(),
            incarnation: RECEIVER_INCARNATION.to_vec().into(),
        }),
        received_sequence: durable,
        durable_sequence: durable,
        applied_sequence: applied,
    }
}

fn scripted_welcome(
    pki: &TestPki,
    hello: &v1::Hello,
    durable: Option<u64>,
    applied: Option<u64>,
    byte_credit: u64,
) -> v1::Welcome {
    let policy = NegotiationPolicy {
        local_identity: scripted_peer_identity(pki, SENDER, SENDER_INCARNATION),
        protocol_version: v1::ProtocolVersion {
            major: V1_PROTOCOL_MAJOR,
            minor: V1_PROTOCOL_MINOR,
        },
        event_format_version: V1_EVENT_FORMAT,
        record_format_versions: vec![2],
        merge_policy_version: 1,
        codecs: vec![v1::PayloadCodec::Lz4BlockV1],
        limits: hello.limits.clone().expect("sender Hello transfer limits"),
        frontiers: vec![local_source_frontier(durable, applied)],
    };
    let mut welcome = negotiate_hello(hello, &policy)
        .expect("construct a Welcome accepted by the production negotiator");
    let limits = welcome
        .limits
        .as_mut()
        .expect("negotiated Welcome transfer limits");
    assert!(byte_credit <= limits.byte_credit);
    limits.byte_credit = byte_credit;
    welcome
}

fn sender_ack_context(pki: &TestPki, sequence: u64) -> v1::AckContext {
    v1::AckContext {
        acknowledging_peer: Some(scripted_peer_identity(pki, SENDER, SENDER_INCARNATION)),
        membership_epoch: 1,
        configuration_id: pki.configuration.to_bytes().to_vec().into(),
        event_id: Some(v1::CommittedEventId {
            database_id: DATABASE.to_bytes().to_vec().into(),
            shard_id: SHARD.get(),
            origin: Some(v1::OriginIdentity {
                replica_id: RECEIVER.to_bytes().to_vec().into(),
                incarnation: RECEIVER_INCARNATION.to_vec().into(),
            }),
            sequence,
        }),
    }
}

fn sender_durable_ack(
    pki: &TestPki,
    context_sequence: u64,
    durable_prefix: u64,
) -> v1::sync_response::Frame {
    v1::sync_response::Frame::Durable(v1::DurableAck {
        context: Some(sender_ack_context(pki, context_sequence)),
        durable_prefix,
    })
}

fn sender_applied_ack(pki: &TestPki, applied_prefix: u64) -> v1::sync_response::Frame {
    v1::sync_response::Frame::Applied(v1::AppliedAck {
        context: Some(sender_ack_context(pki, applied_prefix)),
        applied_prefix,
    })
}

fn assert_sender_hello(pki: &TestPki, hello: &v1::Hello) {
    let local = hello
        .local_identity
        .as_ref()
        .expect("outbound Hello local member identity");
    assert_eq!(local.member_id.as_ref(), RECEIVER.to_bytes().as_slice());
    assert_eq!(
        local.member_incarnation.as_ref(),
        RECEIVER_INCARNATION.as_slice()
    );
    assert_eq!(local.membership_epoch, 1);
    assert_eq!(
        local.configuration_id.as_ref(),
        pki.configuration.to_bytes().as_slice()
    );
    assert_eq!(
        hello.expected_remote_member_id.as_ref(),
        SENDER.to_bytes().as_slice()
    );
    assert_eq!(hello.codecs, [v1::PayloadCodec::Lz4BlockV1 as i32]);
    let protocol = hello
        .protocol_versions
        .as_ref()
        .expect("outbound Hello protocol range");
    assert_eq!(protocol.minimum, protocol.maximum);
    let selected_protocol = protocol.minimum.as_ref().expect("protocol minimum");
    assert_eq!(selected_protocol.major, V1_PROTOCOL_MAJOR);
    assert_eq!(selected_protocol.minor, V1_PROTOCOL_MINOR);
    let event = hello
        .event_format_versions
        .as_ref()
        .expect("outbound Hello event format range");
    assert_eq!(event.minimum, event.maximum);
    assert_eq!(event.minimum.as_ref().unwrap().major, V1_EVENT_FORMAT);
    let record = hello
        .record_format_versions
        .as_ref()
        .expect("outbound Hello record format range");
    assert_eq!(record.minimum, record.maximum);
    assert_eq!(record.minimum.as_ref().unwrap().major, 2);
    assert_eq!(hello.minimum_merge_policy_version, 1);
    assert_eq!(hello.maximum_merge_policy_version, 1);
}

async fn next_outbound_group(session: &mut ScriptedPeerSession) -> v1::CommittedGroupBatch {
    loop {
        let request = session.next_request().await;
        if let Some(v1::sync_request::Frame::CommittedGroup(group)) = request.frame {
            return group;
        }
    }
}

fn assert_local_source_group(
    pki: &TestPki,
    group: &v1::CommittedGroupBatch,
    seeded: &SeededSourceEvent,
) {
    let event_id = group.event_id.as_ref().expect("outbound source event ID");
    assert_eq!(
        event_id.database_id.as_ref(),
        DATABASE.to_bytes().as_slice()
    );
    assert_eq!(event_id.shard_id, SHARD.get());
    let origin = event_id.origin.as_ref().expect("outbound source origin");
    assert_eq!(origin.replica_id.as_ref(), RECEIVER.to_bytes().as_slice());
    assert_eq!(origin.incarnation.as_ref(), RECEIVER_INCARNATION.as_slice());
    assert_eq!(event_id.sequence, seeded.event.sequence);
    assert_eq!(group.membership_epoch, 1);
    assert_eq!(
        group.configuration_id.as_ref(),
        pki.configuration.to_bytes().as_slice()
    );
    assert_eq!(
        group.transaction_id.as_ref(),
        seeded.transaction_id.to_bytes().as_slice(),
        "outbound source transport must retain the transaction ID committed to WAL"
    );
    assert_eq!(group.event_format_version, V1_EVENT_FORMAT);
    assert_eq!(group.record_format_version, 2);
    assert_eq!(group.merge_policy_version, 1);
    assert_eq!(group.codec, v1::PayloadCodec::Lz4BlockV1 as i32);
    assert!(group.wal_group_bytes > 0);
    assert_eq!(
        group.event_metadata_bytes as usize,
        group.distributed_metadata.len()
    );
    assert_eq!(group.event_digest_sha256.len(), 32);
}

async fn assert_no_outbound_group_for(session: &mut ScriptedPeerSession, duration: Duration) {
    let deadline = Instant::now() + duration;
    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return;
        }
        match tokio::time::timeout(remaining, session.requests.message()).await {
            Err(_) | Ok(Ok(None)) => return,
            Ok(Err(status)) => panic!("outbound Sync request failed while idle: {status}"),
            Ok(Ok(Some(request))) => assert!(
                !matches!(
                    request.frame,
                    Some(v1::sync_request::Frame::CommittedGroup(_))
                ),
                "sender sent a redundant local source group while the peer was already durable"
            ),
        }
    }
}

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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
    const MAX_OPENS: usize = 5;
    const MAX_RETRY_INDEX: usize = MAX_OPENS - 1;
    let mut responses = None;
    let mut final_error = None;
    for attempt in 0..MAX_OPENS {
        match client.sync(stream::iter(requests.clone())).await {
            Ok(opened) => {
                responses = Some(opened.into_inner());
                break;
            }
            Err(status)
                if attempt < MAX_RETRY_INDEX
                    && status.code() == tonic::Code::ResourceExhausted
                    && status.message() == "memory controller is busy; retry admission" =>
            {
                tokio::time::sleep(Duration::from_millis(10_u64 << attempt)).await;
            }
            Err(status) => {
                final_error = Some(status);
                break;
            }
        }
    }
    let mut responses = responses.unwrap_or_else(|| {
        panic!(
            "open Sync stream: {}",
            final_error.expect("failed stream open retains its status")
        )
    });
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

#[cfg(not(feature = "slow-tests"))]
async fn open_persistent_stream(
    client: &mut v1::replication_client::ReplicationClient<Channel>,
    hello: v1::SyncRequest,
) -> (
    tokio::sync::mpsc::Sender<v1::SyncRequest>,
    tonic::Streaming<v1::SyncResponse>,
    v1::Welcome,
) {
    let (requests, receiver) = tokio::sync::mpsc::channel(4);
    requests.send(hello).await.expect("send persistent Hello");
    let inbound = stream::unfold(receiver, |mut receiver| async {
        receiver.recv().await.map(|request| (request, receiver))
    });
    let mut responses = client
        .sync(inbound)
        .await
        .expect("open persistent Sync stream")
        .into_inner();
    let first = next_sync_response(&mut responses).await;
    let Some(v1::sync_response::Frame::Welcome(welcome)) = first.frame else {
        panic!("persistent Sync stream must begin with Welcome");
    };
    (requests, responses, welcome)
}

#[cfg(not(feature = "slow-tests"))]
async fn next_sync_response(
    responses: &mut tonic::Streaming<v1::SyncResponse>,
) -> v1::SyncResponse {
    tokio::time::timeout(Duration::from_secs(10), responses.message())
        .await
        .expect("timed out waiting for Sync response")
        .expect("read Sync response")
        .expect("Sync stream ended before expected response")
}

#[cfg(not(feature = "slow-tests"))]
fn committed_group_request(group: v1::CommittedGroupBatch) -> v1::SyncRequest {
    v1::SyncRequest {
        frame: Some(v1::sync_request::Frame::CommittedGroup(group)),
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

#[cfg(feature = "slow-tests")]
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

async fn wait_for_fatal_process_exit(server: &mut RunningServer, trigger: &str) -> ExitStatus {
    let deadline = Instant::now() + Duration::from_secs(12);
    let status = loop {
        if let Some(status) = server.try_wait() {
            break status;
        }
        if Instant::now() >= deadline {
            panic!(
                "{trigger} did not fail the process; readiness remained {}; sender metrics: {}\n{}",
                server.is_ready(),
                sender_failure_metrics(server),
                server.stderr()
            );
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    };
    assert!(
        !status.success(),
        "{trigger} must terminate the process as a fatal outbound error; stderr:\n{}",
        server.stderr()
    );
    assert!(
        !server.is_ready(),
        "process readiness must close after {trigger}; stderr:\n{}",
        server.stderr()
    );
    status
}

fn sender_failure_metrics(server: &RunningServer) -> String {
    let Ok(response) = exchange(
        server.admin_port,
        b"GET /metrics HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
    ) else {
        return "unavailable".to_owned();
    };
    let Some((_, body)) = response.split_once("\r\n\r\n") else {
        return "invalid response".to_owned();
    };
    let selected: Vec<_> = body
        .lines()
        .filter(|line| {
            line.starts_with("terrabase_replication_sender_reconnect_total")
                || line.starts_with("terrabase_replication_sender_fatal_total")
                || line.starts_with("terrabase_replication_sender_connected")
        })
        .collect();
    if selected.is_empty() {
        "no sender series".to_owned()
    } else {
        selected.join("; ")
    }
}

#[cfg(all(feature = "slow-tests", feature = "replication-test-hooks"))]
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

#[cfg(not(feature = "slow-tests"))]
fn exchange_member_control(socket_path: &Path, request: &[u8]) -> io::Result<String> {
    let mut stream = UnixStream::connect(socket_path)?;
    stream.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream.write_all(request)?;
    let mut response = Vec::new();
    stream.read_to_end(&mut response)?;
    Ok(String::from_utf8_lossy(&response).into_owned())
}

#[cfg(not(feature = "slow-tests"))]
fn assert_member_control_status(
    server: &RunningServer,
    pki: &TestPki,
    nonce: &str,
    receiver_only: bool,
) {
    assert!(
        is_lower_hex(nonce, 32),
        "test nonce must be 32 lowercase hex digits"
    );
    let request = format!(
        "GET /v1/member/status?nonce={nonce} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"
    );
    let response = exchange_member_control(&server.member_control_socket, request.as_bytes())
        .expect("query packaged member-control status over Unix socket");
    let (headers, body) = response
        .split_once("\r\n\r\n")
        .expect("member-control response has an HTTP header terminator");
    assert!(headers.starts_with("HTTP/1.1 200 "), "{headers}");
    let status: Value = serde_json::from_str(body).expect("member-control response is JSON");

    assert_eq!(status["schema_version"], 1);
    assert_eq!(status["nonce"], nonce, "status echoes the caller's nonce");
    assert_eq!(status["process"]["pid"], u64::from(server.child.id()));
    let start_nonce = status["process"]["start_nonce"]
        .as_str()
        .expect("process start nonce");
    assert!(is_lower_hex(start_nonce, 32), "{start_nonce}");

    assert_eq!(status["scope"]["cluster_id"], hex(&CLUSTER));
    assert_eq!(status["scope"]["tenant_id"], TENANT.to_string());
    assert_eq!(status["scope"]["database_id"], DATABASE.to_string());
    assert_eq!(status["scope"]["shard_id"], SHARD.get());
    assert_eq!(status["scope"]["member_id"], RECEIVER.to_string());
    assert_eq!(status["scope"]["incarnation"], hex(&RECEIVER_INCARNATION));

    assert_eq!(status["authority"]["epoch"], 1);
    assert_eq!(
        status["authority"]["configuration_id"],
        hex(&pki.configuration.to_bytes())
    );
    let authority_bytes = fs::read(&pki.authority_path).expect("read signed authority fixture");
    assert_eq!(
        status["authority"]["certificate_sha256"],
        hex(&Sha256::digest(authority_bytes))
    );
    assert_eq!(
        status["authority"]["tls_leaf_sha256"],
        hex(&Sha256::digest(&pki.receiver_leaf_der))
    );

    assert_eq!(status["local"]["storage_recovered"], true);
    assert_eq!(status["local"]["replication_listener_ready"], true);
    assert_eq!(status["local"]["healthy"], true);
    assert_eq!(status["local"]["ready"], true);
    assert_eq!(status["receiver_only"], receiver_only);
    assert_eq!(status["serving"], false);
    assert_eq!(status["capabilities"]["serving_proof"], false);
    assert_eq!(status["capabilities"]["durable_drain"], false);
}

#[cfg(not(feature = "slow-tests"))]
fn is_lower_hex(value: &str, expected_len: usize) -> bool {
    value.len() == expected_len
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
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
    crate::server_fixture::server_binary()
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
