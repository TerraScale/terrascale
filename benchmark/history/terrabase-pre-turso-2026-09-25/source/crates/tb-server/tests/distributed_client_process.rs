//! Signed-v2 packaged HTTPS caller over the same owner as replication.

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::net::{IpAddr, Ipv4Addr, SocketAddrV4, TcpListener};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair as _};
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, PKCS_ED25519, SanType,
};
use serde_json::{Value, json};
use tb_core::{
    CollectionId, DatabaseId, DocumentId, MembershipEpoch, OriginId, ReplicaId, ShardId, TenantId,
};
use tb_format::{encode_catalog_into, encode_schema_into};
use tb_replication::{
    AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload, AuthoritySchemaInput,
    CollectionBinding,
};

const CLUSTER: [u8; 16] = [0x31; 16];
const TENANT: TenantId = TenantId::from_bytes([0x41; 16]);
const DATABASE: DatabaseId = DatabaseId::from_bytes([0x51; 16]);
const SHARD: ShardId = ShardId::new(7);
const LOCAL: ReplicaId = ReplicaId::from_bytes([0x61; 16]);
const REMOTE: ReplicaId = ReplicaId::from_bytes([0x71; 16]);
const LOCAL_INCARNATION: [u8; 16] = [0x62; 16];
const REMOTE_INCARNATION: [u8; 16] = [0x72; 16];
const ACTIVE: CollectionId = CollectionId::from_bytes([0x21; 16]);
const INACTIVE: CollectionId = CollectionId::from_bytes([0x22; 16]);
const DOCUMENT: DocumentId = DocumentId::from_bytes([0x29; 16]);
const REVERSE_DOCUMENT: DocumentId = DocumentId::from_bytes([0x2a; 16]);
const AFTER_RESTART_DOCUMENT: DocumentId = DocumentId::from_bytes([0x2b; 16]);
const AFTER_RESTART_REVERSE: DocumentId = DocumentId::from_bytes([0x2c; 16]);
const STALE_DOCUMENT: DocumentId = DocumentId::from_bytes([0x2d; 16]);
const OFFLINE_DOCUMENT: DocumentId = DocumentId::from_bytes([0x2e; 16]);
const STARTUP_TIMEOUT: Duration = Duration::from_secs(20);
const PROCESS_LIMIT: &str = "4294967296";
const PROCESS_HEADROOM: &str = "1073741824";
static NEXT_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn signed_v2_https_commit_read_retry_and_active_scope() {
    let fixture = Fixture::new();
    let mut server = fixture.spawn();
    fixture.wait_ready(&mut server).await;
    let client = fixture.https_client();
    let body = fixture.commit_body();
    let first = client
        .post(fixture.transaction_url())
        .bearer_auth(&fixture.token)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .body(body.clone())
        .send()
        .await
        .expect("HTTPS commit");
    assert_eq!(first.status().as_u16(), 200);
    let first_body = first.text().await.expect("commit response");
    let receipt: Value = serde_json::from_str(&first_body).expect("typed receipt");
    assert_eq!(receipt["durability"], "local_durable");
    assert_eq!(receipt.as_object().expect("object receipt").len(), 5);
    assert!(receipt.get("received").is_none());
    assert!(receipt.get("applied").is_none());
    assert!(receipt.get("remote_ack").is_none());
    let retry = client
        .post(fixture.transaction_url())
        .bearer_auth(&fixture.token)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .body(body)
        .send()
        .await
        .expect("HTTPS exact retry");
    assert_eq!(retry.status().as_u16(), 200);
    assert_eq!(retry.text().await.expect("retry response"), first_body);
    let read = client
        .get(fixture.point_url(ACTIVE))
        .bearer_auth(&fixture.token)
        .send()
        .await
        .expect("HTTPS read");
    assert_eq!(read.status().as_u16(), 200);
    let read_body = read.text().await.expect("point response");
    let read: Value = serde_json::from_str(&read_body).expect("typed point");
    assert_eq!(read["state"], "value");
    let forbidden = client
        .get(fixture.point_url(INACTIVE))
        .bearer_auth(&fixture.token)
        .send()
        .await
        .expect("inactive collection read");
    assert_eq!(forbidden.status().as_u16(), 403);
    let mut stale: Value = serde_json::from_str(&fixture.commit_body_for(STALE_DOCUMENT, 0x95))
        .expect("stale binding request");
    stale["writes"][0]["schema_generation"] =
        json!(fixture.historical_binding.generation.to_string());
    stale["writes"][0]["schema_digest"] = json!(hex(&fixture.historical_binding.schema_digest));
    let rejected = client
        .post(fixture.transaction_url())
        .bearer_auth(&fixture.token)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .body(stale.to_string())
        .send()
        .await
        .expect("stale binding commit");
    assert_eq!(rejected.status().as_u16(), 409);
    fixture.write_registry(INACTIVE);
    server.signal_hup();
    fixture.wait_unready(&mut server).await;
    assert!(server.terminate().success(), "{}", server.stderr());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn signed_v2_https_refuses_inactive_registry_grant_on_startup() {
    let fixture = Fixture::new();
    fixture.write_registry(INACTIVE);
    let mut server = fixture.spawn();
    let deadline = Instant::now() + STARTUP_TIMEOUT;
    loop {
        if let Some(status) = server.child.try_wait().expect("poll process") {
            assert!(!status.success(), "inactive grant unexpectedly started");
            assert!(!server.ready().await, "inactive grant exposed readiness");
            break;
        }
        assert!(
            Instant::now() < deadline,
            "inactive grant did not fail startup: {}",
            server.stderr()
        );
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn signed_v2_https_replication_round_trip_and_restart() {
    let fixture = Fixture::new();
    let mut a = fixture.spawn_a();
    let mut b = fixture.spawn_b();
    fixture.wait_ready(&mut a).await;
    fixture.wait_ready(&mut b).await;
    let client = fixture.https_client();
    fixture
        .commit_on(&client, fixture.client_port, DOCUMENT, 0x91)
        .await;
    fixture
        .wait_value(&client, fixture.remote_client_port, DOCUMENT, &mut b)
        .await;
    fixture
        .commit_on(&client, fixture.remote_client_port, REVERSE_DOCUMENT, 0x92)
        .await;
    fixture
        .wait_value(&client, fixture.client_port, REVERSE_DOCUMENT, &mut a)
        .await;

    assert!(b.terminate().success(), "{}", b.stderr());
    fixture
        .commit_on(&client, fixture.client_port, OFFLINE_DOCUMENT, 0x93)
        .await;
    assert!(
        a.ready().await,
        "local durability must remain ready while the peer is down"
    );
    let mut b = fixture.spawn_b();
    fixture.wait_ready(&mut b).await;
    fixture
        .wait_value(
            &client,
            fixture.remote_client_port,
            OFFLINE_DOCUMENT,
            &mut b,
        )
        .await;
    fixture
        .commit_on(&client, fixture.client_port, AFTER_RESTART_DOCUMENT, 0x94)
        .await;
    fixture
        .wait_value(
            &client,
            fixture.remote_client_port,
            AFTER_RESTART_DOCUMENT,
            &mut b,
        )
        .await;
    fixture
        .commit_on(
            &client,
            fixture.remote_client_port,
            AFTER_RESTART_REVERSE,
            0x96,
        )
        .await;
    fixture
        .wait_value(&client, fixture.client_port, AFTER_RESTART_REVERSE, &mut a)
        .await;
    assert!(b.terminate().success(), "{}", b.stderr());
    assert!(a.terminate().success(), "{}", a.stderr());
}

struct Fixture {
    directory: PathBuf,
    authority: PathBuf,
    trust_key: PathBuf,
    member_certificate: PathBuf,
    member_key: PathBuf,
    remote_certificate: PathBuf,
    remote_key: PathBuf,
    member_roots: PathBuf,
    client_config: PathBuf,
    remote_client_config: PathBuf,
    registry: PathBuf,
    client_root: String,
    token: String,
    active_binding: CollectionBinding,
    historical_binding: CollectionBinding,
    admin_port: u16,
    replication_port: u16,
    client_port: u16,
    remote_admin_port: u16,
    remote_replication_port: u16,
    remote_client_port: u16,
}

impl Fixture {
    fn new() -> Self {
        let directory = std::env::temp_dir().join(format!(
            "tb-distributed-client-{}-{}",
            std::process::id(),
            NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&directory).expect("create test directory");
        let member_issuer = issuer();
        let (local_cert, local_key) = member_certificate(&member_issuer, "member-a.terrabase.test");
        let (remote_cert, remote_keypair) =
            member_certificate(&member_issuer, "member-b.terrabase.test");
        let members = [
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: LOCAL,
                    incarnation: LOCAL_INCARNATION,
                },
                leaf_der: local_cert.der().as_ref(),
                server_name: "member-a.terrabase.test",
                failure_domain: "region-a",
            },
            AuthorityMemberInput {
                origin: OriginId {
                    replica_id: REMOTE,
                    incarnation: REMOTE_INCARNATION,
                },
                leaf_der: remote_cert.der().as_ref(),
                server_name: "member-b.terrabase.test",
                failure_domain: "region-b",
            },
        ];
        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).expect("empty signed schema");
        let mut active_catalog = Vec::new();
        let active_digest = encode_catalog_into(&mut active_catalog, ACTIVE, 2, 0, &schema)
            .expect("active signed catalog");
        let mut historical_catalog = Vec::new();
        let historical_digest = encode_catalog_into(&mut historical_catalog, ACTIVE, 1, 0, &schema)
            .expect("historical signed catalog");
        let active_binding = CollectionBinding {
            collection: ACTIVE,
            generation: 2,
            schema_digest: active_digest,
        };
        let historical_binding = CollectionBinding {
            collection: ACTIVE,
            generation: 1,
            schema_digest: historical_digest,
        };
        let schemas = [
            AuthoritySchemaInput {
                binding: historical_binding,
                catalog: &historical_catalog,
            },
            AuthoritySchemaInput {
                binding: active_binding,
                catalog: &active_catalog,
            },
        ];
        let payload = AuthorityPayload::encode_v2(
            AuthorityCertificateScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                epoch: MembershipEpoch::new(1),
            },
            &members,
            &schemas,
            &[active_binding],
        )
        .expect("signed-v2 payload");
        let signer = authority_signer();
        let signing_input = payload.signing_input().expect("authority signing input");
        let signature: [u8; 64] = signer
            .sign(&signing_input)
            .as_ref()
            .try_into()
            .expect("signature");
        let public_key: [u8; 32] = signer.public_key().as_ref().try_into().expect("public key");
        let certificate = payload
            .finish(&signature, &public_key)
            .expect("signed-v2 certificate");
        let authority = directory.join("authority.tbca");
        let trust_key = directory.join("authority.pub");
        fs::write(&authority, certificate).expect("write authority");
        fs::write(&trust_key, public_key).expect("write trust key");
        let member_certificate = directory.join("member.pem");
        let member_key = directory.join("member-key.pem");
        let remote_certificate = directory.join("remote-member.pem");
        let remote_key = directory.join("remote-member-key.pem");
        let member_roots = directory.join("member-ca.pem");
        fs::write(
            &member_certificate,
            format!("{}\n{}", local_cert.pem(), member_issuer.pem()),
        )
        .expect("write local member certificate");
        write_private(&member_key, local_key.serialize_pem().as_bytes());
        fs::write(
            &remote_certificate,
            format!("{}\n{}", remote_cert.pem(), member_issuer.pem()),
        )
        .expect("write remote member certificate");
        write_private(&remote_key, remote_keypair.serialize_pem().as_bytes());
        fs::write(&member_roots, member_issuer.pem()).expect("write member roots");
        let client_issuer = issuer();
        let client_key = KeyPair::generate().expect("client TLS key");
        let mut client_params = CertificateParams::default();
        client_params.subject_alt_names = vec![SanType::IpAddress(IpAddr::V4(Ipv4Addr::LOCALHOST))];
        client_params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
        client_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let client_certificate = client_params
            .signed_by(&client_key, &client_issuer)
            .expect("client HTTPS certificate");
        let client_root = client_issuer.pem();
        let client_cert_path = directory.join("client.pem");
        let client_key_path = directory.join("client-key.pem");
        fs::write(
            &client_cert_path,
            format!("{}\n{}", client_certificate.pem(), client_root),
        )
        .expect("write HTTPS certificate");
        write_private(&client_key_path, client_key.serialize_pem().as_bytes());
        let registry = directory.join("registry.json");
        let output = Command::new(crate::server_fixture::keygen_binary())
            .output()
            .expect("packaged keygen");
        assert!(
            output.status.success(),
            "keygen: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let stdout = String::from_utf8(output.stdout).expect("keygen output UTF-8");
        let token = stdout
            .lines()
            .next()
            .and_then(|line| line.strip_prefix("API key (copy once): "))
            .expect("raw API key")
            .to_owned();
        let template: Value = serde_json::from_str(
            stdout
                .lines()
                .find(|line| line.starts_with('{'))
                .expect("keygen template"),
        )
        .expect("parse keygen template");
        let admin_port = loopback_port();
        let replication_port = loopback_port();
        let client_port = loopback_port();
        let remote_admin_port = loopback_port();
        let remote_replication_port = loopback_port();
        let remote_client_port = loopback_port();
        let client_config = directory.join("client.json");
        let remote_client_config = directory.join("remote-client.json");
        fs::write(
            &client_config,
            serde_json::to_vec(&json!({
                "version": 1,
                "listen_addr": format!("127.0.0.1:{client_port}"),
                "certificate_chain": &client_cert_path,
                "private_key": &client_key_path,
                "api_key_registry": &registry,
                "handshake_timeout_ms": 1000,
            }))
            .expect("client config JSON"),
        )
        .expect("write client config");
        fs::write(
            &remote_client_config,
            serde_json::to_vec(&json!({
                "version": 1,
                "listen_addr": format!("127.0.0.1:{remote_client_port}"),
                "certificate_chain": client_cert_path,
                "private_key": client_key_path,
                "api_key_registry": registry,
                "handshake_timeout_ms": 1000,
            }))
            .expect("remote client config JSON"),
        )
        .expect("write remote client config");
        let fixture = Self {
            directory,
            authority,
            trust_key,
            member_certificate,
            member_key,
            remote_certificate,
            remote_key,
            member_roots,
            client_config,
            remote_client_config,
            registry,
            client_root,
            token,
            active_binding,
            historical_binding,
            admin_port,
            replication_port,
            client_port,
            remote_admin_port,
            remote_replication_port,
            remote_client_port,
        };
        fixture.write_registry_entry(template, ACTIVE);
        fixture
    }

    fn write_registry_entry(&self, mut template: Value, collection: CollectionId) {
        template["tenant"] = json!(TENANT.to_string());
        template["database"] = json!(DATABASE.to_string());
        template["shard"] = json!(SHARD.get().to_string());
        template["collections"] = json!([collection.to_string()]);
        template["permissions"] = json!(["read", "write"]);
        write_private(
            &self.registry,
            &serde_json::to_vec(&json!({"version": 1, "keys": [template]})).expect("registry JSON"),
        );
    }

    fn write_registry(&self, collection: CollectionId) {
        let bytes = fs::read(&self.registry).expect("read current registry");
        let mut registry: Value = serde_json::from_slice(&bytes).expect("parse current registry");
        registry["keys"][0]["collections"] = json!([collection.to_string()]);
        write_private(
            &self.registry,
            &serde_json::to_vec(&registry).expect("registry JSON"),
        );
    }

    fn spawn(&self) -> RunningServer {
        self.spawn_node(
            LOCAL,
            LOCAL_INCARNATION,
            REMOTE,
            "127.0.0.1:1".into(),
            &self.member_certificate,
            &self.member_key,
            &self.client_config,
            self.admin_port,
            self.replication_port,
            "local",
            "replication",
            "server.log",
        )
    }

    fn spawn_a(&self) -> RunningServer {
        self.spawn_node(
            LOCAL,
            LOCAL_INCARNATION,
            REMOTE,
            format!("127.0.0.1:{}", self.remote_replication_port),
            &self.member_certificate,
            &self.member_key,
            &self.client_config,
            self.admin_port,
            self.replication_port,
            "local-a",
            "replication-a",
            "server-a.log",
        )
    }

    fn spawn_b(&self) -> RunningServer {
        self.spawn_node(
            REMOTE,
            REMOTE_INCARNATION,
            LOCAL,
            format!("127.0.0.1:{}", self.replication_port),
            &self.remote_certificate,
            &self.remote_key,
            &self.remote_client_config,
            self.remote_admin_port,
            self.remote_replication_port,
            "local-b",
            "replication-b",
            "server-b.log",
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn spawn_node(
        &self,
        member: ReplicaId,
        incarnation: [u8; 16],
        peer: ReplicaId,
        peer_addr: String,
        certificate: &Path,
        private_key: &Path,
        client_config: &Path,
        admin_port: u16,
        replication_port: u16,
        local_dir: &str,
        replication_dir: &str,
        log_name: &str,
    ) -> RunningServer {
        let log_path = self.directory.join(log_name);
        let log = File::create(&log_path).expect("server log");
        let child = Command::new(crate::server_fixture::server_binary())
            .arg("--data-dir")
            .arg(self.directory.join(local_dir))
            .arg("--memory-limit-bytes")
            .arg(PROCESS_LIMIT)
            .arg("--memory-headroom-bytes")
            .arg(PROCESS_HEADROOM)
            .arg("--port")
            .arg(admin_port.to_string())
            .arg("--replication-listen-addr")
            .arg(format!("127.0.0.1:{replication_port}"))
            .arg("--replication-data-dir")
            .arg(self.directory.join(replication_dir))
            .arg("--authority-certificate")
            .arg(&self.authority)
            .arg("--trusted-public-key")
            .arg(&self.trust_key)
            .arg("--replication-server-certificate")
            .arg(certificate)
            .arg("--replication-server-private-key")
            .arg(private_key)
            .arg("--replication-client-ca-roots")
            .arg(&self.member_roots)
            .arg("--replication-peer-member-id")
            .arg(peer.to_string())
            .arg("--replication-peer-addr")
            .arg(peer_addr)
            .arg("--replication-server-ca-roots")
            .arg(&self.member_roots)
            .arg("--cluster-id")
            .arg(hex(&CLUSTER))
            .arg("--tenant-id")
            .arg(TENANT.to_string())
            .arg("--database-id")
            .arg(DATABASE.to_string())
            .arg("--shard-id")
            .arg(SHARD.get().to_string())
            .arg("--member-id")
            .arg(member.to_string())
            .arg("--member-incarnation")
            .arg(hex(&incarnation))
            .arg("--client-config")
            .arg(client_config)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::from(log))
            .spawn()
            .expect("spawn signed-v2 HTTPS process");
        RunningServer {
            child,
            log_path,
            admin_port,
        }
    }

    fn https_client(&self) -> reqwest::Client {
        reqwest::Client::builder()
            .add_root_certificate(
                reqwest::Certificate::from_pem(self.client_root.as_bytes())
                    .expect("HTTPS trust root"),
            )
            .https_only(true)
            .timeout(Duration::from_secs(15))
            .build()
            .expect("HTTPS client")
    }

    async fn wait_ready(&self, server: &mut RunningServer) {
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        while !server.ready().await {
            assert!(
                server.child.try_wait().expect("poll process").is_none(),
                "{}",
                server.stderr()
            );
            assert!(
                Instant::now() < deadline,
                "readiness timeout: {}",
                server.stderr()
            );
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    async fn wait_unready(&self, server: &mut RunningServer) {
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        while server.ready().await {
            assert!(
                Instant::now() < deadline,
                "readiness did not close: {}",
                server.stderr()
            );
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    fn transaction_url(&self) -> String {
        self.transaction_url_for(self.client_port)
    }

    fn point_url(&self, collection: CollectionId) -> String {
        self.point_url_for(self.client_port, collection, DOCUMENT)
    }

    fn transaction_url_for(&self, port: u16) -> String {
        format!(
            "https://127.0.0.1:{port}/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{}/transactions",
            SHARD.get()
        )
    }

    fn point_url_for(&self, port: u16, collection: CollectionId, document: DocumentId) -> String {
        format!(
            "https://127.0.0.1:{port}/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{}/collections/{collection}/documents/{document}",
            SHARD.get()
        )
    }

    fn commit_body(&self) -> String {
        self.commit_body_for(DOCUMENT, 0x91)
    }

    fn commit_body_for(&self, document: DocumentId, nonce: u8) -> String {
        let expires_at = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time")
            .as_millis() as u64
            + 3_600_000;
        serde_json::to_string(&json!({
            "version": 1,
            "transaction_id": { "expires_at_unix_ms": expires_at.to_string(), "nonce": hex(&[nonce; 16]) },
            "writes": [{
                "collection": ACTIVE.to_string(), "document": document.to_string(),
                "schema_generation": self.active_binding.generation.to_string(),
                "schema_digest": hex(&self.active_binding.schema_digest),
                "observed": [], "value": {"kind": "live", "fields": []}
            }]
        })).expect("commit body")
    }

    async fn commit_on(
        &self,
        client: &reqwest::Client,
        port: u16,
        document: DocumentId,
        nonce: u8,
    ) {
        let response = client
            .post(self.transaction_url_for(port))
            .bearer_auth(&self.token)
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .body(self.commit_body_for(document, nonce))
            .send()
            .await
            .expect("HTTPS source commit");
        let status = response.status().as_u16();
        let body = response.text().await.expect("source receipt");
        assert_eq!(status, 200, "{body}");
        let receipt: Value = serde_json::from_str(&body).expect("source receipt JSON");
        assert_eq!(receipt["durability"], "local_durable");
    }

    async fn wait_value(
        &self,
        client: &reqwest::Client,
        port: u16,
        document: DocumentId,
        server: &mut RunningServer,
    ) {
        let deadline = Instant::now() + Duration::from_secs(60);
        loop {
            assert!(
                server
                    .child
                    .try_wait()
                    .expect("poll target process")
                    .is_none(),
                "{}",
                server.stderr()
            );
            if let Ok(response) = client
                .get(self.point_url_for(port, ACTIVE, document))
                .bearer_auth(&self.token)
                .send()
                .await
                && response.status().as_u16() == 200
                && let Ok(body_text) = response.text().await
                && let Ok(body) = serde_json::from_str::<Value>(&body_text)
                && body["state"] == "value"
            {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "replicated point did not become visible: {}",
                server.stderr()
            );
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.directory);
    }
}

struct RunningServer {
    child: Child,
    log_path: PathBuf,
    admin_port: u16,
}
impl RunningServer {
    async fn ready(&self) -> bool {
        reqwest::Client::new()
            .get(format!("http://127.0.0.1:{}/readyz", self.admin_port))
            .send()
            .await
            .is_ok_and(|response| response.status().as_u16() == 200)
    }
    fn stderr(&self) -> String {
        fs::read_to_string(&self.log_path).unwrap_or_default()
    }
    fn signal_hup(&self) {
        signal(self.child.id(), "HUP");
    }
    fn terminate(&mut self) -> std::process::ExitStatus {
        signal(self.child.id(), "TERM");
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        loop {
            if let Some(status) = self.child.try_wait().expect("poll process") {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "process did not stop: {}",
                self.stderr()
            );
            std::thread::sleep(Duration::from_millis(20));
        }
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

fn signal(pid: u32, name: &str) {
    let output = Command::new("kill")
        .arg(format!("-{name}"))
        .arg(pid.to_string())
        .output()
        .expect("send process signal");
    assert!(
        output.status.success(),
        "kill failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
}
fn issuer() -> CertifiedIssuer<'static, KeyPair> {
    let key = KeyPair::generate_for(&PKCS_ED25519).expect("CA key");
    let mut params = CertificateParams::default();
    params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    CertifiedIssuer::self_signed(params, key).expect("self-signed CA")
}
fn member_certificate(
    issuer: &CertifiedIssuer<'_, KeyPair>,
    name: &str,
) -> (rcgen::Certificate, KeyPair) {
    let key = KeyPair::generate_for(&PKCS_ED25519).expect("member key");
    let mut params = CertificateParams::default();
    params.subject_alt_names = vec![SanType::DnsName(name.try_into().expect("DNS name"))];
    params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    params.extended_key_usages = vec![
        ExtendedKeyUsagePurpose::ServerAuth,
        ExtendedKeyUsagePurpose::ClientAuth,
    ];
    (params.signed_by(&key, issuer).expect("signed member"), key)
}
fn authority_signer() -> Ed25519KeyPair {
    let rng = SystemRandom::new();
    let pkcs8 = Ed25519KeyPair::generate_pkcs8(&rng).expect("authority key");
    Ed25519KeyPair::from_pkcs8(pkcs8.as_ref()).expect("load authority key")
}
fn write_private(path: &Path, bytes: &[u8]) {
    let mut options = OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(path).expect("private fixture file");
    file.write_all(bytes).expect("write private fixture file");
    file.sync_all().expect("sync private fixture file");
}
fn loopback_port() -> u16 {
    TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
        .expect("bind test port")
        .local_addr()
        .expect("test port")
        .port()
}
fn hex(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut output, "{byte:02x}").unwrap();
    }
    output
}
