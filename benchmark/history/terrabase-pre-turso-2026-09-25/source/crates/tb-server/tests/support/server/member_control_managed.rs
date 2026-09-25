//! Real root-supervisor -> tenant TerraBase process and Go member-status client.

use std::fs;
use std::io::{self, Read, Write};
use std::net::TcpListener;
use std::os::unix::fs::{PermissionsExt, chown};
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair as _};
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, PKCS_ED25519, SanType,
};
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};
use tb_core::{CollectionId, DatabaseId, MembershipEpoch, OriginId, ReplicaId, ShardId, TenantId};
use tb_format::{encode_catalog_into, encode_schema_into};
use tb_replication::{
    AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload, AuthoritySchemaInput,
    CollectionBinding,
};

const CLUSTER: [u8; 16] = [0x31; 16];
const TENANT: TenantId = TenantId::from_bytes([0x41; 16]);
const DATABASE: DatabaseId = DatabaseId::from_bytes([0x51; 16]);
const SHARD: ShardId = ShardId::new(7);
const RECEIVER: ReplicaId = ReplicaId::from_bytes([0x61; 16]);
const SENDER: ReplicaId = ReplicaId::from_bytes([0x71; 16]);
const RECEIVER_INCARNATION: [u8; 16] = [0x62; 16];
const SENDER_INCARNATION: [u8; 16] = [0x72; 16];
const TENANT_UID: u32 = 65_534;
const TENANT_GID: u32 = 65_534;
const WRONG_PARENT_GID: u32 = 65_533;
const MAX_PROBE_OUTPUT_BYTES: usize = 1 << 20;
const PROBE_DEADLINE: Duration = Duration::from_secs(120);

static NEXT_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

#[test]
fn managed_member_status_cross_language_process_boundary() {
    let probe = std::env::var_os("TB_MEMBER_STATUS_PROBE_BINARY")
        .map(PathBuf::from)
        .expect("TB_MEMBER_STATUS_PROBE_BINARY must name the static Go process probe");
    assert!(
        probe.is_file(),
        "Go member-status probe is missing: {}",
        probe.display()
    );
    let server = std::env::var_os("TB_SERVER_TEST_BINARY")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(env!("CARGO_BIN_EXE_tb-server")));
    assert!(
        server.is_file(),
        "TerraBase server binary is missing: {}",
        server.display()
    );

    let directory = TestDirectory::new();
    eprintln!(
        "MEMBER_BOUNDARY_PHASE=signed_fixture_begin pid={}",
        std::process::id()
    );
    let manifest = signed_fixture(directory.path(), &server);
    let manifest_path = directory.path().join("member-fixture.json");
    fs::write(
        &manifest_path,
        serde_json::to_vec(&manifest).expect("encode member fixture"),
    )
    .expect("write member fixture manifest");
    eprintln!(
        "MEMBER_BOUNDARY_PHASE=probe_launch pid={}",
        std::process::id()
    );
    let output = run_probe_bounded(&probe, &manifest_path);
    eprintln!("MEMBER_BOUNDARY_PHASE=probe_exit status={}", output.status);
    assert!(
        output.status.success(),
        "Go member-status probe failed: status={} stdout={} stderr={}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    assert!(
        String::from_utf8_lossy(&output.stdout).contains("MEMBER_STATUS_GO_OK"),
        "Go process probe did not report success: {}",
        String::from_utf8_lossy(&output.stdout),
    );
    reject_wrong_gid_parent(&server, &manifest, directory.path());
}

fn run_probe_bounded(probe: &Path, manifest_path: &Path) -> Output {
    let mut child = Command::new(probe)
        .arg(manifest_path)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("run Go process runner against real TerraBase server");
    eprintln!(
        "MEMBER_BOUNDARY_PHASE=probe_spawned child_pid={}",
        child.id()
    );
    let (sender, receiver) = mpsc::channel();
    forward_probe_output(
        child.stdout.take().expect("probe stdout"),
        false,
        sender.clone(),
    );
    forward_probe_output(child.stderr.take().expect("probe stderr"), true, sender);
    let deadline = Instant::now() + PROBE_DEADLINE;
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(20)),
            Ok(None) => {
                child.kill().expect("kill timed-out Go member probe");
                let status = child.wait().expect("reap timed-out Go member probe");
                panic!(
                    "Go member probe exceeded {:?}: child_pid={} status={status}",
                    PROBE_DEADLINE,
                    child.id()
                );
            }
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                panic!("wait for Go member probe: {error}");
            }
        }
    };
    let mut stdout = None;
    let mut stderr = None;
    for _ in 0..2 {
        let (is_stderr, captured) = receiver
            .recv_timeout(Duration::from_secs(5))
            .expect("Go member probe output pipe stayed open after exit");
        let captured =
            captured.expect("Go member probe output exceeded bound or could not be read");
        if is_stderr {
            stderr = Some(captured);
        } else {
            stdout = Some(captured);
        }
    }
    Output {
        status,
        stdout: stdout.expect("probe stdout capture"),
        stderr: stderr.expect("probe stderr capture"),
    }
}

fn forward_probe_output<R: Read + Send + 'static>(
    mut source: R,
    is_stderr: bool,
    sender: mpsc::Sender<(bool, io::Result<Vec<u8>>)>,
) {
    thread::spawn(move || {
        let result = (|| -> io::Result<Vec<u8>> {
            let mut captured = Vec::new();
            let mut overflow = false;
            let mut chunk = [0_u8; 4096];
            loop {
                let count = source.read(&mut chunk)?;
                if count == 0 {
                    break;
                }
                if overflow || captured.len() + count > MAX_PROBE_OUTPUT_BYTES {
                    overflow = true;
                    continue;
                }
                captured.extend_from_slice(&chunk[..count]);
                if is_stderr {
                    let _ = io::stderr().write_all(&chunk[..count]);
                } else {
                    let _ = io::stdout().write_all(&chunk[..count]);
                }
            }
            if overflow {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "Go member probe output exceeded bound",
                ));
            }
            Ok(captured)
        })();
        let _ = sender.send((is_stderr, result));
    });
}

fn reject_wrong_gid_parent(server: &Path, manifest: &Value, directory: &Path) {
    fs::set_permissions(directory, fs::Permissions::from_mode(0o711))
        .expect("allow tenant to traverse managed fixture parent");
    let parent = directory.join("wrong-gid-control");
    fs::create_dir(&parent).expect("create wrong-GID control parent");
    chown(&parent, Some(0), Some(WRONG_PARENT_GID)).expect("own wrong-GID control parent");
    fs::set_permissions(&parent, fs::Permissions::from_mode(0o730))
        .expect("set private control parent mode");
    let socket = parent.join("member.sock");

    let admin = TcpListener::bind("127.0.0.1:0").expect("reserve negative probe admin port");
    let admin_port = admin.local_addr().expect("admin port address").port();
    drop(admin);

    let mut command = Command::new("setpriv");
    command
        .arg(format!("--reuid={TENANT_UID}"))
        .arg(format!("--regid={TENANT_GID}"))
        .arg(format!("--groups={WRONG_PARENT_GID}"))
        .arg("--bounding-set=-all")
        .arg("--no-new-privs")
        .arg("--")
        .arg(server)
        .arg("--data-dir")
        .arg(directory.join("wrong-gid-data"))
        .arg("--memory-limit-bytes")
        .arg("4294967296")
        .arg("--memory-headroom-bytes")
        .arg("1073741824")
        .arg("--port")
        .arg(admin_port.to_string())
        .arg("--replication-listen-addr")
        .arg("127.0.0.1:0")
        .arg("--replication-data-dir")
        .arg(directory.join("wrong-gid-replication"))
        .arg("--authority-certificate")
        .arg(
            manifest["authority_certificate"]
                .as_str()
                .expect("signed authority path"),
        )
        .arg("--trusted-public-key")
        .arg(
            manifest["trusted_public_key"]
                .as_str()
                .expect("authority trust path"),
        )
        .arg("--replication-server-certificate")
        .arg(
            manifest["server_certificate"]
                .as_str()
                .expect("member certificate path"),
        )
        .arg("--replication-server-private-key")
        .arg(
            manifest["server_private_key"]
                .as_str()
                .expect("member private key path"),
        )
        .arg("--replication-client-ca-roots")
        .arg(
            manifest["client_ca_roots"]
                .as_str()
                .expect("member CA path"),
        )
        .arg("--cluster-id")
        .arg(
            manifest["scope"]["cluster_id"]
                .as_str()
                .expect("cluster ID"),
        )
        .arg("--tenant-id")
        .arg(manifest["scope"]["tenant_id"].as_str().expect("tenant ID"))
        .arg("--database-id")
        .arg(
            manifest["scope"]["database_id"]
                .as_str()
                .expect("database ID"),
        )
        .arg("--shard-id")
        .arg(
            manifest["scope"]["shard_id"]
                .as_u64()
                .expect("shard ID")
                .to_string(),
        )
        .arg("--member-id")
        .arg(manifest["scope"]["member_id"].as_str().expect("member ID"))
        .arg("--member-incarnation")
        .arg(
            manifest["scope"]["incarnation"]
                .as_str()
                .expect("member incarnation"),
        )
        .arg("--member-control-socket")
        .arg(&socket)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());

    let mut child = command
        .spawn()
        .expect("launch real wrong-GID member process");
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(20)),
            Ok(None) => {
                child
                    .kill()
                    .expect("kill member that did not reject wrong GID");
                let output = child.wait_with_output().expect("reap wrong-GID member");
                panic!(
                    "wrong-GID member did not reject before deadline: status={} stderr={}",
                    output.status,
                    String::from_utf8_lossy(&output.stderr)
                );
            }
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                panic!("wait for wrong-GID member: {error}");
            }
        }
    }
    let output = child.wait_with_output().expect("reap wrong-GID member");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        !output.status.success() && stderr.contains("match process effective GID"),
        "wrong-GID member did not fail at control parent check: status={} stderr={stderr}",
        output.status
    );
    let socket_metadata = fs::symlink_metadata(&socket);
    assert!(
        matches!(&socket_metadata, Err(error) if error.kind() == std::io::ErrorKind::NotFound),
        "wrong-GID member left a control socket or its state could not be checked: {socket_metadata:?}"
    );
    println!("memberstatus: wrong-GID parent rejected before socket bind");
}

fn signed_fixture(directory: &Path, server: &Path) -> serde_json::Value {
    let ca_key = KeyPair::generate_for(&PKCS_ED25519).expect("generate test CA key");
    let mut ca_params = CertificateParams::default();
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    let ca = CertifiedIssuer::self_signed(ca_params, ca_key).expect("sign test CA");
    let (receiver_certificate, receiver_key) = member_certificate(&ca, "member-a.terrabase.test");
    let (sender_certificate, _) = member_certificate(&ca, "member-b.terrabase.test");
    let receiver_der = receiver_certificate.der().as_ref();
    let sender_der = sender_certificate.der().as_ref();

    let mut schema = Vec::new();
    encode_schema_into(&mut schema, &[]).expect("encode test schema");
    let collection = CollectionId::from_bytes([0x21; 16]);
    let mut catalog = Vec::new();
    let schema_digest =
        encode_catalog_into(&mut catalog, collection, 1, 0, &schema).expect("encode test catalog");
    let members = [
        AuthorityMemberInput {
            origin: OriginId {
                replica_id: RECEIVER,
                incarnation: RECEIVER_INCARNATION,
            },
            leaf_der: receiver_der,
            server_name: "member-a.terrabase.test",
            failure_domain: "region-a",
        },
        AuthorityMemberInput {
            origin: OriginId {
                replica_id: SENDER,
                incarnation: SENDER_INCARNATION,
            },
            leaf_der: sender_der,
            server_name: "member-b.terrabase.test",
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
    let rng = SystemRandom::new();
    let pkcs8 = Ed25519KeyPair::generate_pkcs8(&rng).expect("generate authority key");
    let authority_key = Ed25519KeyPair::from_pkcs8(pkcs8.as_ref()).expect("load authority key");
    let public_key: [u8; 32] = authority_key
        .public_key()
        .as_ref()
        .try_into()
        .expect("authority public key size");
    let signature: [u8; 64] = authority_key
        .sign(&signing_input)
        .as_ref()
        .try_into()
        .expect("authority signature size");
    let authority = payload
        .finish(&signature, &public_key)
        .expect("sign authority certificate");

    let authority_path = directory.join("authority.tbca");
    let trust_path = directory.join("authority.pub");
    let ca_path = directory.join("member-ca.pem");
    let certificate_path = directory.join("receiver.pem");
    let key_path = directory.join("receiver-key.pem");
    fs::write(&authority_path, &authority).expect("write signed authority");
    fs::write(&trust_path, public_key).expect("write authority trust key");
    fs::write(&ca_path, ca.pem()).expect("write member CA");
    fs::write(&certificate_path, receiver_certificate.pem()).expect("write receiver leaf");
    fs::write(&key_path, receiver_key.serialize_pem()).expect("write receiver key");

    json!({
        "server_binary": server,
        "tenant_uid": TENANT_UID,
        "tenant_gid": TENANT_GID,
        "authority_certificate": authority_path,
        "trusted_public_key": trust_path,
        "server_certificate": certificate_path,
        "server_private_key": key_path,
        "client_ca_roots": ca_path,
        "outbound_peer_member_id": SENDER.to_string(),
        "memory_limit_bytes": 4_294_967_296_u64,
        "memory_headroom_bytes": 1_073_741_824_u64,
        "scope": {
            "cluster_id": hex(&CLUSTER),
            "tenant_id": TENANT.to_string(),
            "database_id": DATABASE.to_string(),
            "shard_id": SHARD.get(),
            "member_id": RECEIVER.to_string(),
            "incarnation": hex(&RECEIVER_INCARNATION),
        },
        "authority": {
            "epoch": 1,
            "configuration_id": hex(&configuration.to_bytes()),
            "certificate_sha256": hex(&Sha256::digest(&authority)),
            "tls_leaf_sha256": hex(&Sha256::digest(receiver_der)),
        },
    })
}

fn member_certificate(
    issuer: &CertifiedIssuer<'_, KeyPair>,
    server_name: &str,
) -> (rcgen::Certificate, KeyPair) {
    let key = KeyPair::generate_for(&PKCS_ED25519).expect("generate member key");
    let mut params = CertificateParams::default();
    params.subject_alt_names = vec![SanType::DnsName(
        server_name.try_into().expect("test DNS name"),
    )];
    params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    params.extended_key_usages = vec![
        ExtendedKeyUsagePurpose::ServerAuth,
        ExtendedKeyUsagePurpose::ClientAuth,
    ];
    let certificate = params.signed_by(&key, issuer).expect("issue member leaf");
    (certificate, key)
}

fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(&mut output, "{byte:02x}").expect("write to String");
    }
    output
}

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock after epoch")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "terrabase-managed-member-{}-{nanos}-{}",
            std::process::id(),
            NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed),
        ));
        fs::create_dir(&path).expect("create managed process fixture");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).expect("remove managed process fixture");
    }
}
