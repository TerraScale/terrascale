//! Packaged local-v1 HTTPS/JSON caller coverage.

use std::fs::{self, File, OpenOptions};
#[cfg(feature = "slow-tests")]
use std::io::Read;
use std::io::Write;
#[cfg(feature = "slow-tests")]
use std::net::TcpStream;
use std::net::{IpAddr, Ipv4Addr, SocketAddrV4, TcpListener};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, SanType,
};
use serde_json::{Value, json};
use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, DocumentId, MembershipEpoch, MemoryBudget, OriginId,
    ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
use tb_document::{DocumentStore, DocumentStoreConfig, LocalSchemaBinding};
use tb_format::{FieldType, SchemaField, encode_catalog_into, encode_schema_into};
use tb_replication::{ConfigurationId, MAX_EVENT_CHANGES, ReplicationLimits};
use tb_storage::{LocalDocumentMode, StorageConfig};

const PROCESS_MEMORY_LIMIT_BYTES: &str = "268435456";
const PROCESS_MEMORY_HEADROOM_BYTES: &str = "16777216";
const ADMIN_TIMEOUT: Duration = Duration::from_secs(20);
const CLIENT_TIMEOUT: Duration = Duration::from_secs(30);
const STARTUP_TIMEOUT: Duration = Duration::from_secs(20);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(12);
const HANDSHAKE_TIMEOUT_MS: u64 = 1_000;

const TENANT: TenantId = TenantId::from_bytes([0x11; 16]);
const DATABASE: DatabaseId = DatabaseId::from_bytes([0x22; 16]);
const SHARD: ShardId = ShardId::new(7);
const REPLICA: ReplicaId = ReplicaId::from_bytes([0x33; 16]);
const INCARNATION: [u8; 16] = [0x44; 16];
const EPOCH: u64 = 1;
const CONFIGURATION: [u8; 32] = [0x55; 32];
const FIRST_COLLECTION: CollectionId = CollectionId::from_bytes([0x61; 16]);
const SECOND_COLLECTION: CollectionId = CollectionId::from_bytes([0x62; 16]);
const FIRST_DOCUMENT: DocumentId = DocumentId::from_bytes([0x71; 16]);
const SECOND_DOCUMENT: DocumentId = DocumentId::from_bytes([0x72; 16]);
#[cfg(feature = "slow-tests")]
const CONFLICT_DOCUMENT: DocumentId = DocumentId::from_bytes([0x73; 16]);
#[cfg(feature = "slow-tests")]
const LARGE_DOCUMENT: DocumentId = DocumentId::from_bytes([0x74; 16]);

static NEXT_DIRECTORY: AtomicUsize = AtomicUsize::new(0);
static NEXT_MATERIAL: AtomicUsize = AtomicUsize::new(0);

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_local_v1_serves_typed_commits_and_retries_after_restart() {
    let fixture = LocalFixture::new();
    let mut server = fixture.spawn();
    fixture.wait_for_readiness(&mut server, true).await;
    let client = fixture.client(&fixture.tls.ca_pem);

    let absent = send_get(
        &client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(absent.0, 200, "{}", absent.1);
    assert_eq!(
        serde_json::from_str::<Value>(&absent.1).expect("typed absent response"),
        json!({"version": 1, "state": "absent", "context": [], "siblings": []})
    );

    let first_body = fixture.two_collection_commit_body();
    let first_commit = send_commit(
        &client,
        &fixture.transaction_url(),
        &fixture.primary.token,
        &first_body,
    )
    .await;
    assert_eq!(first_commit.0, 200, "{}", first_commit.1);
    let commit_json: Value = serde_json::from_str(&first_commit.1).expect("commit response JSON");
    assert_eq!(commit_json["durability"], "local_durable");
    assert_eq!(commit_json["transaction_id"]["nonce"], hex(&[0x91; 16]));
    assert!(commit_json["commit_sequence"].as_str().is_some());

    let typed_read = send_get(
        &client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(typed_read.0, 200, "{}", typed_read.1);
    let typed_json: Value = serde_json::from_str(&typed_read.1).expect("typed read JSON");
    assert_eq!(typed_json["state"], "value");
    let fields = &typed_json["siblings"][0]["fields"];
    assert_eq!(field(fields, 1)["value"]["type"], "null");
    assert_eq!(
        field(fields, 2)["value"],
        json!({"type": "bytes", "value": "AQIDBA"})
    );
    assert_eq!(
        field(fields, 3)["value"],
        json!({"type": "i64", "value": "-42"})
    );
    assert!(
        fields
            .as_array()
            .expect("fields array")
            .iter()
            .all(|entry| entry["id"] != 4),
        "the omitted optional field must remain missing"
    );

    let second_read = send_get(
        &client,
        &fixture.point_url(SECOND_COLLECTION, SECOND_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(second_read.0, 200, "{}", second_read.1);
    let second_json: Value = serde_json::from_str(&second_read.1).expect("second read JSON");
    assert_eq!(
        second_json["siblings"][0]["fields"][0]["value"],
        json!({"type": "utf8", "value": "second collection"})
    );

    let update_body = fixture.single_collection_commit_body(
        0x92,
        FIRST_COLLECTION,
        FIRST_DOCUMENT,
        json!([
            {"id": 1, "value": {"type": "null"}},
            {"id": 3, "value": {"type": "i64", "value": "17"}}
        ]),
    );
    let update_commit = send_commit(
        &client,
        &fixture.transaction_url(),
        &fixture.primary.token,
        &update_body,
    )
    .await;
    assert_eq!(update_commit.0, 200, "{}", update_commit.1);
    let updated_read = send_get(
        &client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(updated_read.0, 200, "{}", updated_read.1);
    let updated_json: Value = serde_json::from_str(&updated_read.1).expect("updated read JSON");
    assert_eq!(updated_json["state"], "value");
    assert_eq!(updated_json["siblings"].as_array().unwrap().len(), 1);
    assert_eq!(
        field(&updated_json["siblings"][0]["fields"], 3)["value"]["value"],
        "17"
    );

    let exact_retry = send_commit(
        &client,
        &fixture.transaction_url(),
        &fixture.primary.token,
        &first_body,
    )
    .await;
    assert_eq!(exact_retry.0, 200, "{}", exact_retry.1);
    assert_eq!(
        exact_retry.1, first_commit.1,
        "an exact retry returns the original durable receipt"
    );

    let metrics = fixture.metrics().await;
    assert!(metrics.contains("terrabase_server_ready 1"), "{metrics}");
    assert!(
        !metrics.contains(&fixture.primary.token),
        "Prometheus output must never contain the bearer token"
    );
    assert!(
        !metrics.contains(&TENANT.to_string()),
        "Prometheus labels must not include tenant identity"
    );
    assert_eq!(
        fixture.admin_get("/v1/documents").await.0,
        404,
        "the admin listener must not serve the client document routes"
    );

    assert!(server.terminate_and_wait().success());
    let first_logs = server.stderr();
    assert!(
        !first_logs.contains(&fixture.primary.token),
        "server logs must never contain the bearer token"
    );

    let mut restarted = fixture.spawn();
    fixture.wait_for_readiness(&mut restarted, true).await;
    let restarted_client = fixture.client(&fixture.tls.ca_pem);
    let retry_after_restart = send_commit(
        &restarted_client,
        &fixture.transaction_url(),
        &fixture.primary.token,
        &first_body,
    )
    .await;
    assert_eq!(retry_after_restart.0, 200, "{}", retry_after_restart.1);
    assert_eq!(retry_after_restart.1, first_commit.1);
    let persisted_update = send_get(
        &restarted_client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(persisted_update.0, 200, "{}", persisted_update.1);
    let persisted_json: Value =
        serde_json::from_str(&persisted_update.1).expect("persisted update response");
    assert_eq!(persisted_json["state"], "value");
    assert_eq!(
        field(&persisted_json["siblings"][0]["fields"], 3)["value"]["value"],
        "17"
    );

    #[cfg(feature = "slow-tests")]
    {
        // The request fits just below 4 MiB after JSON escaping, while the typed
        // read adds its event and sibling metadata and must hit the real response
        // writer's 4-MiB cap with a single valid local write.
        const JSON_CAP_BYTES: usize = 4 * 1024 * 1024;
        let empty_large_body = fixture.single_collection_commit_body(
            0x93,
            FIRST_COLLECTION,
            LARGE_DOCUMENT,
            json!([
                {"id": 1, "value": {"type": "null"}},
                {"id": 4, "value": {"type": "utf8", "value": ""}}
            ]),
        );
        let escaped_character_width = serde_json::to_string("\u{0001}")
            .expect("encode escaped UTF-8 character")
            .len()
            - 2;
        let escaped_character_count =
            (JSON_CAP_BYTES - empty_large_body.len()) / escaped_character_width;
        let escaped = "\u{0001}".repeat(escaped_character_count);
        let large_body_one = fixture.single_collection_commit_body(
            0x93,
            FIRST_COLLECTION,
            LARGE_DOCUMENT,
            json!([
                {"id": 1, "value": {"type": "null"}},
                {"id": 4, "value": {"type": "utf8", "value": escaped}}
            ]),
        );
        assert!(large_body_one.len() <= JSON_CAP_BYTES);
        assert!(
            large_body_one.len() + escaped_character_width > JSON_CAP_BYTES,
            "fixture should place its request at the JSON input boundary"
        );
        let large_commit_one = send_commit(
            &restarted_client,
            &fixture.transaction_url(),
            &fixture.primary.token,
            &large_body_one,
        )
        .await;
        assert_eq!(large_commit_one.0, 200, "{}", large_commit_one.1);
        let escaped_read = send_get(
            &restarted_client,
            &fixture.point_url(FIRST_COLLECTION, LARGE_DOCUMENT),
            &fixture.primary.token,
        )
        .await;
        assert_eq!(escaped_read.0, 503, "{}", escaped_read.1);
        assert_error_code(&escaped_read.1, "capacity");

        // Exhaust every pre-handshake socket slot with peers that never send a
        // ClientHello. The configured deadline must release them all.
        let previous_rejections = rejected_handshake_count(&fixture.metrics().await).unwrap_or(0);
        let mut stalled = Vec::new();
        for _ in 0..8 {
            stalled.push(
                TcpStream::connect(SocketAddrV4::new(Ipv4Addr::LOCALHOST, fixture.client_port))
                    .expect("connect a stalled TLS peer"),
            );
        }
        wait_for_rejected_handshakes(&fixture, previous_rejections + 8).await;
        let after_timeout = send_get(
            &restarted_client,
            &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
            &fixture.primary.token,
        )
        .await;
        assert_eq!(after_timeout.0, 200, "{}", after_timeout.1);
        drop(stalled);

        let mut draining_peer =
            TcpStream::connect(SocketAddrV4::new(Ipv4Addr::LOCALHOST, fixture.client_port))
                .expect("connect a peer stalled in TLS handshake");
        draining_peer
            .set_read_timeout(Some(Duration::from_secs(2)))
            .expect("set stalled peer read timeout");
        thread::sleep(Duration::from_millis(100));
        let status = restarted.terminate_and_wait();
        assert!(status.success(), "server did not drain cleanly: {status}");
        let mut byte = [0_u8; 1];
        let close = draining_peer.read(&mut byte);
        assert!(
            matches!(close, Ok(0) | Err(_)),
            "shutdown must close a peer stalled before TLS completes"
        );
        let logs = restarted.stderr();
        assert!(!logs.contains(&fixture.primary.token));
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_initializer_bootstraps_fresh_local_v1_for_https_document_caller() {
    let directory = TestDirectory::new();
    let data_dir = directory.path().join("initialized-shard");
    let local_config = directory.path().join("initialized-local-v1.json");
    let input = directory.path().join("initializer-input.json");
    write_material(
        &input,
        &serde_json::to_vec(&initializer_input()).expect("serialize initializer input"),
        false,
    );

    let malformed_input = directory.path().join("malformed-input.json");
    let malformed_data = directory.path().join("malformed-shard");
    let malformed_config = directory.path().join("malformed-local-v1.json");
    const INPUT_SECRET: &str = "initializer-input-secret-marker";
    let mut malformed_json = initializer_input();
    malformed_json["unexpected"] = json!(INPUT_SECRET);
    write_material(
        &malformed_input,
        &serde_json::to_vec(&malformed_json).expect("serialize strictness test input"),
        false,
    );
    let malformed = run_initializer(&malformed_input, &malformed_config, &malformed_data);
    assert!(
        !malformed.status.success(),
        "malformed initializer input unexpectedly succeeded: stdout={} stderr={}",
        String::from_utf8_lossy(&malformed.stdout),
        String::from_utf8_lossy(&malformed.stderr)
    );
    assert!(
        !malformed_data.exists(),
        "malformed input claimed its data path"
    );
    assert!(
        !malformed_config.exists(),
        "malformed input published a local-v1 config"
    );
    assert!(
        !String::from_utf8_lossy(&malformed.stderr).contains(INPUT_SECRET),
        "initializer error exposed input contents"
    );

    let initialized = run_initializer(&input, &local_config, &data_dir);
    assert!(
        initialized.status.success(),
        "initializer failed: stdout={} stderr={}",
        String::from_utf8_lossy(&initialized.stdout),
        String::from_utf8_lossy(&initialized.stderr)
    );
    assert!(
        data_dir.is_dir(),
        "initializer did not create the shard directory"
    );
    assert!(
        local_config.is_file(),
        "initializer did not publish its config"
    );
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(
            fs::metadata(&data_dir)
                .expect("stat initialized shard")
                .permissions()
                .mode()
                & 0o777,
            0o700,
            "initialized shard directory must be owner-only"
        );
        assert_eq!(
            fs::metadata(&local_config)
                .expect("stat initialized local config")
                .permissions()
                .mode()
                & 0o777,
            0o600,
            "initialized local config must be owner-only"
        );
    }

    let first_binding = local_schema_binding(FIRST_COLLECTION, &first_schema());
    let second_binding = local_schema_binding(SECOND_COLLECTION, &second_schema());
    let generated_config = fs::read(&local_config).expect("read generated local-v1 config");
    let generated_json: Value =
        serde_json::from_slice(&generated_config).expect("parse generated local-v1 config");
    assert_eq!(generated_json["version"], 1);
    assert_eq!(generated_json["tenant"], TENANT.to_string());
    assert_eq!(generated_json["database"], DATABASE.to_string());
    assert_eq!(generated_json["shard"], SHARD.get().to_string());
    assert_eq!(generated_json["epoch"], "1");
    assert_eq!(
        generated_json["collections"],
        json!([
            {
                "collection": FIRST_COLLECTION.to_string(),
                "generation": "1",
                "digest": hex(&first_binding.schema_digest),
            },
            {
                "collection": SECOND_COLLECTION.to_string(),
                "generation": "1",
                "digest": hex(&second_binding.schema_digest),
            }
        ]),
        "initializer config must bind both encoded schemas in sorted order"
    );

    let preexisting_output_data = directory.path().join("output-refusal-shard");
    let output_refusal = run_initializer(&input, &local_config, &preexisting_output_data);
    assert!(
        !output_refusal.status.success(),
        "initializer overwrote an existing output config"
    );
    assert!(
        !preexisting_output_data.exists(),
        "existing output config refusal mutated the fresh data path"
    );
    assert_eq!(
        fs::read(&local_config).expect("read config after overwrite refusal"),
        generated_config,
        "existing output config bytes changed after refusal"
    );

    let refused_config = directory.path().join("live-store-refusal.json");
    let fixture = LocalFixture::with_initialized_storage(
        directory,
        data_dir,
        local_config,
        first_binding,
        second_binding,
        &[FIRST_COLLECTION],
    );
    let mut server = fixture.spawn();
    fixture.wait_for_readiness(&mut server, true).await;
    let client = fixture.client(&fixture.tls.ca_pem);
    let commit_body = fixture.single_collection_commit_body(
        0xa1,
        FIRST_COLLECTION,
        FIRST_DOCUMENT,
        json!([
            {"id": 1, "value": {"type": "null"}},
            {"id": 3, "value": {"type": "i64", "value": "73"}}
        ]),
    );
    let commit = send_commit(
        &client,
        &fixture.transaction_url(),
        &fixture.primary.token,
        &commit_body,
    )
    .await;
    assert_eq!(commit.0, 200, "{}", commit.1);
    assert_eq!(
        serde_json::from_str::<Value>(&commit.1).expect("commit receipt JSON")["durability"],
        "local_durable"
    );

    let read = send_get(
        &client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(read.0, 200, "{}", read.1);
    let read_json: Value = serde_json::from_str(&read.1).expect("document read JSON");
    assert_eq!(read_json["state"], "value");
    assert_eq!(
        field(&read_json["siblings"][0]["fields"], 3)["value"]["value"],
        "73"
    );

    let before_refusal = snapshot_directory(&fixture.data_dir);
    let live_refusal = run_initializer(&input, &refused_config, &fixture.data_dir);
    assert!(
        !live_refusal.status.success(),
        "initializer accepted a store that is live in the packaged server"
    );
    assert!(
        !refused_config.exists(),
        "live-store refusal published a config"
    );
    assert_eq!(
        snapshot_directory(&fixture.data_dir),
        before_refusal,
        "live-store refusal changed existing shard files"
    );
    assert_eq!(
        fs::read(&fixture.local_config).expect("read live store's config"),
        generated_config,
        "live-store refusal changed the active config"
    );

    assert!(server.terminate_and_wait().success());
    let mut restarted = fixture.spawn();
    fixture.wait_for_readiness(&mut restarted, true).await;
    let restarted_client = fixture.client(&fixture.tls.ca_pem);
    let persisted = send_get(
        &restarted_client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
        &fixture.primary.token,
    )
    .await;
    assert_eq!(persisted.0, 200, "{}", persisted.1);
    let persisted_json: Value = serde_json::from_str(&persisted.1).expect("persisted read JSON");
    assert_eq!(persisted_json["state"], "value");
    assert_eq!(
        field(&persisted_json["siblings"][0]["fields"], 3)["value"]["value"],
        "73"
    );
    assert!(restarted.terminate_and_wait().success());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_client_rejects_scope_and_permission_violations_and_rate_limits() {
    let fixture = LocalFixture::new();
    #[cfg(feature = "slow-tests")]
    let mut fixture = fixture;
    #[cfg(feature = "slow-tests")]
    let read_only = fixture.add_key(&[FIRST_COLLECTION], &["read"], None);
    #[cfg(feature = "slow-tests")]
    let collection_scoped = fixture.add_key(&[FIRST_COLLECTION], &["read", "write"], None);
    #[cfg(feature = "slow-tests")]
    let rate_limited = fixture.add_key(&[FIRST_COLLECTION], &["read"], Some((1, 1)));
    #[cfg(feature = "slow-tests")]
    fixture.write_registry();
    let mut server = fixture.spawn();
    fixture.wait_for_readiness(&mut server, true).await;
    let client = fixture.client(&fixture.tls.ca_pem);

    let no_key = send_get_without_key(
        &client,
        &fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT),
    )
    .await;
    assert_eq!(no_key.0, 401, "{}", no_key.1);
    assert_eq!(
        serde_json::from_str::<Value>(&no_key.1).unwrap()["error"]["code"],
        "invalid_key"
    );

    #[cfg(feature = "slow-tests")]
    {
        let malformed = send_commit(
            &client,
            &fixture.transaction_url(),
            &fixture.primary.token,
            "{",
        )
        .await;
        assert_eq!(malformed.0, 400, "{}", malformed.1);
        assert_error_code(&malformed.1, "invalid_request");

        let oversized_body = " ".repeat(4 * 1024 * 1024 + 1);
        let oversized_request = send_commit(
            &client,
            &fixture.transaction_url(),
            &fixture.primary.token,
            &oversized_body,
        )
        .await;
        assert_eq!(oversized_request.0, 400, "{}", oversized_request.1);
        assert_error_code(&oversized_request.1, "invalid_request");

        let denied_permission_body = fixture.single_collection_commit_body(
            0xa1,
            FIRST_COLLECTION,
            CONFLICT_DOCUMENT,
            json!([{"id": 1, "value": {"type": "null"}}]),
        );
        let denied_permission = send_commit(
            &client,
            &fixture.transaction_url(),
            &read_only.token,
            &denied_permission_body,
        )
        .await;
        assert_eq!(denied_permission.0, 403, "{}", denied_permission.1);
        assert_error_code(&denied_permission.1, "forbidden_scope");

        let denied_read = send_get(
            &client,
            &fixture.point_url(SECOND_COLLECTION, SECOND_DOCUMENT),
            &collection_scoped.token,
        )
        .await;
        assert_eq!(denied_read.0, 403, "{}", denied_read.1);
        assert_error_code(&denied_read.1, "forbidden_scope");

        let denied_collection_body = fixture.single_collection_commit_body(
            0xa2,
            SECOND_COLLECTION,
            SECOND_DOCUMENT,
            json!([{"id": 7, "value": {"type": "utf8", "value": "must not commit"}}]),
        );
        let denied_collection = send_commit(
            &client,
            &fixture.transaction_url(),
            &collection_scoped.token,
            &denied_collection_body,
        )
        .await;
        assert_eq!(denied_collection.0, 403, "{}", denied_collection.1);
        assert_error_code(&denied_collection.1, "forbidden_scope");

        let unchanged = send_get(
            &client,
            &fixture.point_url(SECOND_COLLECTION, SECOND_DOCUMENT),
            &fixture.primary.token,
        )
        .await;
        assert_eq!(unchanged.0, 200, "{}", unchanged.1);
        assert_eq!(
            serde_json::from_str::<Value>(&unchanged.1).unwrap()["state"],
            "absent",
            "a scope refusal must happen before mutation"
        );

        let limited_url = fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT);
        let (first, second) = tokio::join!(
            send_get(&client, &limited_url, &rate_limited.token),
            send_get(&client, &limited_url, &rate_limited.token),
        );
        let statuses = [first.0, second.0];
        assert!(
            statuses.contains(&200) && statuses.contains(&429),
            "one request should pass and its concurrent peer should be rate-limited: {statuses:?}; {first:?}; {second:?}"
        );
        if first.0 == 429 {
            assert_error_code(&first.1, "rate_limited");
        }
        if second.0 == 429 {
            assert_error_code(&second.1, "rate_limited");
        }

        let metrics = fixture.metrics().await;
        for secret in [
            &fixture.primary.token,
            &read_only.token,
            &collection_scoped.token,
            &rate_limited.token,
        ] {
            assert!(!metrics.contains(secret), "metrics exposed an API token");
        }
        assert!(
            !metrics.contains(&TENANT.to_string()),
            "metric labels exposed a tenant ID"
        );

        assert!(server.terminate_and_wait().success());
        let logs = server.stderr();
        for secret in [
            &fixture.primary.token,
            &read_only.token,
            &collection_scoped.token,
            &rate_limited.token,
        ] {
            assert!(!logs.contains(secret), "server logs exposed an API token");
        }
    }
}

#[cfg(feature = "slow-tests")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_client_reload_fails_closed_and_rotates_keys_and_tls() {
    let mut fixture = LocalFixture::new();
    let replacement = fixture.add_key(
        &[FIRST_COLLECTION, SECOND_COLLECTION],
        &["read", "write"],
        None,
    );
    let mut server = fixture.spawn();
    fixture.wait_for_readiness(&mut server, true).await;
    let old_client = fixture.client(&fixture.tls.ca_pem);
    let point = fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT);
    assert_eq!(
        send_get(&old_client, &point, &fixture.primary.token)
            .await
            .0,
        200
    );

    fixture.write_registry();
    server.signal("HUP");
    wait_for_status(&old_client, &point, &replacement.token, 200).await;

    let rotated_tls = TlsMaterial::new();
    fixture.write_tls(&rotated_tls);
    fixture.replace_keys(vec![replacement.clone()]);
    server.signal("HUP");

    let new_client = fixture.client(&rotated_tls.ca_pem);
    wait_for_status(&new_client, &point, &replacement.token, 200).await;
    assert!(
        old_client
            .get(&point)
            .bearer_auth(&replacement.token)
            .send()
            .await
            .is_err(),
        "TLS rotation must stop accepting connections under the previous trust root"
    );
    let old_key_after_rotation = send_get(&new_client, &point, &fixture.primary.token).await;
    assert_eq!(
        old_key_after_rotation.0, 401,
        "{}",
        old_key_after_rotation.1
    );
    assert_error_code(&old_key_after_rotation.1, "invalid_key");

    // An invalid registry replacement must close admissions and data readiness,
    // while the separate admin listener stays available for recovery.
    fixture.write_registry_bytes(br#"{"version":1,"keys":[}"#);
    server.signal("HUP");
    fixture.wait_for_readiness(&mut server, false).await;
    let closed = send_get(&new_client, &point, &replacement.token).await;
    assert_eq!(closed.0, 503, "{}", closed.1);
    assert_eq!(
        serde_json::from_str::<Value>(&closed.1).unwrap()["error"]["code"],
        "capacity"
    );
    assert_eq!(fixture.admin_get("/healthz").await.0, 200);

    fixture.write_registry();
    server.signal("HUP");
    fixture.wait_for_readiness(&mut server, true).await;
    let recovered = send_get(&new_client, &point, &replacement.token).await;
    assert_eq!(recovered.0, 200, "{}", recovered.1);
    let still_revoked = send_get(&new_client, &point, &fixture.primary.token).await;
    assert_eq!(still_revoked.0, 401, "{}", still_revoked.1);

    assert!(server.terminate_and_wait().success());
    let logs = server.stderr();
    assert!(!logs.contains(&fixture.primary.token));
    assert!(!logs.contains(&replacement.token));
}

#[cfg(feature = "slow-tests")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_client_hup_is_registered_before_readiness() {
    let mut fixture = LocalFixture::new();
    let replacement = fixture.add_key(&[FIRST_COLLECTION], &["read"], None);
    let replacement_registry = fixture.registry.with_file_name("api-keys-replacement.json");
    let replacement_bytes = serde_json::to_vec(&json!({
        "version": 1,
        "keys": [replacement.entry.clone()],
    }))
    .expect("serialize replacement API-key registry");
    write_material(&replacement_registry, &replacement_bytes, true);

    let client = fixture.client(&fixture.tls.ca_pem);
    let point = fixture.point_url(FIRST_COLLECTION, FIRST_DOCUMENT);
    let mut server = fixture.spawn();
    fixture.wait_for_readiness(&mut server, true).await;

    // Make the new credentials visible and signal the clean process as soon
    // as readiness is observed, before sending any client request.
    fs::rename(&replacement_registry, &fixture.registry)
        .expect("install pre-staged replacement API-key registry");
    server.signal("HUP");

    wait_for_status(&client, &point, &replacement.token, 200).await;
    let revoked = send_get(&client, &point, &fixture.primary.token).await;
    assert_eq!(revoked.0, 401, "{}", revoked.1);
    assert_error_code(&revoked.1, "invalid_key");
    assert!(
        server.try_wait().is_none(),
        "SIGHUP must reload credentials without terminating the process"
    );
    assert!(server.terminate_and_wait().success());
}

#[cfg(feature = "slow-tests")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn packaged_shutdown_signals_are_registered_before_readiness() {
    let fixture = LocalFixture::new();
    for signal in ["TERM", "INT"] {
        let mut server = fixture.spawn();
        fixture.wait_for_readiness(&mut server, true).await;

        server.signal(signal);
        let status = server.wait_for_exit();
        assert!(
            status.success(),
            "immediate SIG{signal} did not exit gracefully: {status}\n{}",
            server.stderr()
        );
    }
}

struct LocalFixture {
    _directory: TestDirectory,
    data_dir: PathBuf,
    local_config: PathBuf,
    client_config: PathBuf,
    registry: PathBuf,
    #[cfg(feature = "slow-tests")]
    certificate: PathBuf,
    #[cfg(feature = "slow-tests")]
    private_key: PathBuf,
    admin_port: u16,
    client_port: u16,
    first_binding: LocalSchemaBinding,
    second_binding: LocalSchemaBinding,
    primary: KeyMaterial,
    keys: Vec<KeyMaterial>,
    tls: TlsMaterial,
    admin: reqwest::Client,
}

impl LocalFixture {
    fn new() -> Self {
        let directory = TestDirectory::new();
        let data_dir = directory.path().join("shard");
        let local_config = directory.path().join("local-v1.json");

        let first_schema = first_schema();
        let second_schema = second_schema();
        let mut store = DocumentStore::bootstrap_local_v1(
            document_store_config(&data_dir),
            fixture_budget(),
            local_mode(),
        )
        .expect("bootstrap a trusted local-v1 fixture");
        let first_binding = provision_schema(&mut store, 0x81, FIRST_COLLECTION, &first_schema);
        let second_binding = provision_schema(&mut store, 0x82, SECOND_COLLECTION, &second_schema);
        assert!(first_binding.collection < second_binding.collection);
        drop(store);

        let local_json = json!({
            "version": 1,
            "tenant": TENANT.to_string(),
            "database": DATABASE.to_string(),
            "shard": SHARD.get().to_string(),
            "replica": REPLICA.to_string(),
            "incarnation": hex(&INCARNATION),
            "epoch": EPOCH.to_string(),
            "configuration": hex(&CONFIGURATION),
            "collections": [
                {
                    "collection": first_binding.collection.to_string(),
                    "generation": first_binding.generation.to_string(),
                    "digest": hex(&first_binding.schema_digest),
                },
                {
                    "collection": second_binding.collection.to_string(),
                    "generation": second_binding.generation.to_string(),
                    "digest": hex(&second_binding.schema_digest),
                }
            ]
        });
        fs::write(
            &local_config,
            serde_json::to_vec(&local_json).expect("serialize trusted local config"),
        )
        .expect("write trusted local config");

        Self::with_initialized_storage(
            directory,
            data_dir,
            local_config,
            first_binding,
            second_binding,
            &[FIRST_COLLECTION, SECOND_COLLECTION],
        )
    }

    fn with_initialized_storage(
        directory: TestDirectory,
        data_dir: PathBuf,
        local_config: PathBuf,
        first_binding: LocalSchemaBinding,
        second_binding: LocalSchemaBinding,
        primary_collections: &[CollectionId],
    ) -> Self {
        let client_config = directory.path().join("client.json");
        let registry = directory.path().join("api-keys.json");
        let certificate = directory.path().join("server.pem");
        let private_key = directory.path().join("server-key.pem");
        let admin_port = available_loopback_port();
        let client_port = available_loopback_port();
        assert_ne!(
            admin_port, client_port,
            "test listeners need distinct ports"
        );

        let primary = KeyMaterial::generate(primary_collections, &["read", "write"], None);
        let keys = vec![primary.clone()];
        let tls = TlsMaterial::new();
        write_material(&certificate, tls.certificate_pem.as_bytes(), false);
        write_material(&private_key, tls.private_key_pem.as_bytes(), true);
        let client_json = json!({
            "version": 1,
            "listen_addr": format!("127.0.0.1:{client_port}"),
            "certificate_chain": certificate,
            "private_key": private_key,
            "api_key_registry": registry,
            "handshake_timeout_ms": HANDSHAKE_TIMEOUT_MS,
        });
        fs::write(
            &client_config,
            serde_json::to_vec(&client_json).expect("serialize client listener config"),
        )
        .expect("write client listener config");

        let fixture = Self {
            _directory: directory,
            data_dir,
            local_config,
            client_config,
            registry,
            #[cfg(feature = "slow-tests")]
            certificate,
            #[cfg(feature = "slow-tests")]
            private_key,
            admin_port,
            client_port,
            first_binding,
            second_binding,
            primary,
            keys,
            tls,
            admin: reqwest::Client::builder()
                .timeout(ADMIN_TIMEOUT)
                .build()
                .expect("build admin HTTP client"),
        };
        fixture.write_registry();
        fixture
    }

    fn spawn(&self) -> RunningServer {
        RunningServer::spawn(
            &server_binary(),
            &self.data_dir,
            &self.local_config,
            &self.client_config,
            self.admin_port,
        )
    }

    fn client(&self, root_pem: &str) -> reqwest::Client {
        let root = reqwest::Certificate::from_pem(root_pem.as_bytes())
            .expect("parse generated test root certificate");
        reqwest::Client::builder()
            .add_root_certificate(root)
            .https_only(true)
            .timeout(CLIENT_TIMEOUT)
            .build()
            .expect("build normally verifying HTTPS client")
    }

    async fn wait_for_readiness(&self, server: &mut RunningServer, expected: bool) {
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        loop {
            if let Some(status) = server.try_wait() {
                panic!(
                    "server exited before readiness changed to {expected}: {status}\nchild stderr:\n{}",
                    server.stderr()
                );
            }
            let actual = match self.try_admin_get("/readyz").await {
                Ok((status, _)) => Some(status == 200),
                Err(error) if error.is_connect() => None,
                Err(error) => panic!(
                    "admin readiness request failed: {error}\nchild stderr:\n{}",
                    server.stderr()
                ),
            };
            if actual == Some(expected) {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "admin readiness did not become {expected} within {STARTUP_TIMEOUT:?}\nchild stderr:\n{}",
                server.stderr()
            );
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    }

    #[cfg(feature = "slow-tests")]
    fn add_key(
        &mut self,
        collections: &[CollectionId],
        permissions: &[&str],
        rate: Option<(u32, u32)>,
    ) -> KeyMaterial {
        let key = KeyMaterial::generate(collections, permissions, rate);
        self.keys.push(key.clone());
        key
    }

    #[cfg(feature = "slow-tests")]
    fn replace_keys(&mut self, keys: Vec<KeyMaterial>) {
        self.keys = keys;
        self.write_registry();
    }

    fn write_registry(&self) {
        let entries: Vec<_> = self.keys.iter().map(|key| key.entry.clone()).collect();
        self.write_registry_bytes(
            &serde_json::to_vec(&json!({"version": 1, "keys": entries}))
                .expect("serialize protected API-key registry"),
        );
    }

    fn write_registry_bytes(&self, bytes: &[u8]) {
        write_material(&self.registry, bytes, true);
    }

    #[cfg(feature = "slow-tests")]
    fn write_tls(&mut self, tls: &TlsMaterial) {
        write_material(&self.certificate, tls.certificate_pem.as_bytes(), false);
        write_material(&self.private_key, tls.private_key_pem.as_bytes(), true);
        self.tls = tls.clone();
    }

    async fn admin_get(&self, path: &str) -> (u16, String) {
        self.try_admin_get(path)
            .await
            .expect("send admin HTTP request")
    }

    async fn try_admin_get(&self, path: &str) -> Result<(u16, String), reqwest::Error> {
        let response = self
            .admin
            .get(format!("http://127.0.0.1:{}{path}", self.admin_port))
            .send()
            .await?;
        let status = response.status().as_u16();
        let body = response.text().await?;
        Ok((status, body))
    }

    async fn metrics(&self) -> String {
        let (status, body) = self.admin_get("/metrics").await;
        assert_eq!(status, 200, "{body}");
        body
    }

    fn transaction_url(&self) -> String {
        format!(
            "https://127.0.0.1:{}/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{}/transactions",
            self.client_port,
            SHARD.get(),
        )
    }

    fn point_url(&self, collection: CollectionId, document: DocumentId) -> String {
        format!(
            "https://127.0.0.1:{}/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{}/collections/{collection}/documents/{document}",
            self.client_port,
            SHARD.get(),
        )
    }

    fn two_collection_commit_body(&self) -> String {
        let expires_at = expires_at_unix_ms();
        serialize_commit(
            expires_at,
            [0x91; 16],
            vec![
                json!({
                    "collection": FIRST_COLLECTION.to_string(),
                    "document": FIRST_DOCUMENT.to_string(),
                    "schema_generation": self.first_binding.generation.to_string(),
                    "schema_digest": hex(&self.first_binding.schema_digest),
                    "observed": [],
                    "value": {"kind": "live", "fields": [
                        {"id": 1, "value": {"type": "null"}},
                        {"id": 2, "value": {"type": "bytes", "value": "AQIDBA"}},
                        {"id": 3, "value": {"type": "i64", "value": "-42"}}
                    ]}
                }),
                json!({
                    "collection": SECOND_COLLECTION.to_string(),
                    "document": SECOND_DOCUMENT.to_string(),
                    "schema_generation": self.second_binding.generation.to_string(),
                    "schema_digest": hex(&self.second_binding.schema_digest),
                    "observed": [],
                    "value": {"kind": "live", "fields": [
                        {"id": 7, "value": {"type": "utf8", "value": "second collection"}}
                    ]}
                }),
            ],
        )
    }

    fn single_collection_commit_body(
        &self,
        nonce: u8,
        collection: CollectionId,
        document: DocumentId,
        fields: Value,
    ) -> String {
        let binding = if collection == FIRST_COLLECTION {
            self.first_binding
        } else {
            self.second_binding
        };
        serialize_commit(
            expires_at_unix_ms(),
            [nonce; 16],
            vec![json!({
                "collection": collection.to_string(),
                "document": document.to_string(),
                "schema_generation": binding.generation.to_string(),
                "schema_digest": hex(&binding.schema_digest),
                "observed": [],
                "value": {"kind": "live", "fields": fields}
            })],
        )
    }
}

struct KeyMaterial {
    token: String,
    entry: Value,
}

impl Clone for KeyMaterial {
    fn clone(&self) -> Self {
        Self {
            token: self.token.clone(),
            entry: self.entry.clone(),
        }
    }
}

impl KeyMaterial {
    fn generate(
        collections: &[CollectionId],
        permissions: &[&str],
        rate: Option<(u32, u32)>,
    ) -> Self {
        let output = Command::new(keygen_binary())
            .output()
            .expect("run packaged API-key generator");
        assert!(
            output.status.success(),
            "keygen failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let stdout = String::from_utf8(output.stdout).expect("keygen output is UTF-8");
        let mut lines = stdout.lines();
        let token = lines
            .next()
            .and_then(|line| line.strip_prefix("API key (copy once): "))
            .expect("keygen prints the raw token exactly once")
            .to_owned();
        let template = lines
            .find(|line| line.starts_with('{'))
            .expect("keygen prints a verifier-entry template");
        let mut entry: Value = serde_json::from_str(template).expect("parse keygen template");
        entry["tenant"] = json!(TENANT.to_string());
        entry["database"] = json!(DATABASE.to_string());
        entry["shard"] = json!(SHARD.get().to_string());
        let mut sorted: Vec<_> = collections.to_vec();
        sorted.sort_unstable();
        entry["collections"] = json!(sorted.iter().map(ToString::to_string).collect::<Vec<_>>());
        entry["permissions"] = json!(permissions);
        if let Some((rate_per_second, burst)) = rate {
            entry["rate_per_second"] = json!(rate_per_second);
            entry["burst"] = json!(burst);
        }
        Self { token, entry }
    }
}

#[derive(Clone)]
struct TlsMaterial {
    certificate_pem: String,
    private_key_pem: String,
    ca_pem: String,
}

impl TlsMaterial {
    fn new() -> Self {
        let ca_key = KeyPair::generate().expect("generate test TLS CA key");
        let mut ca_parameters = CertificateParams::default();
        ca_parameters.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        ca_parameters.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
        let ca =
            CertifiedIssuer::self_signed(ca_parameters, ca_key).expect("self-sign test TLS CA");

        let key = KeyPair::generate().expect("generate TLS server key");
        let mut parameters = CertificateParams::default();
        parameters.subject_alt_names = vec![SanType::IpAddress(IpAddr::V4(Ipv4Addr::LOCALHOST))];
        parameters.key_usages = vec![KeyUsagePurpose::DigitalSignature];
        parameters.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let certificate = parameters
            .signed_by(&key, &ca)
            .expect("issue TLS server certificate");
        Self {
            certificate_pem: format!("{}\n{}", certificate.pem(), ca.pem()),
            private_key_pem: key.serialize_pem(),
            ca_pem: ca.pem(),
        }
    }
}

struct RunningServer {
    child: Child,
    stderr_path: PathBuf,
}

impl RunningServer {
    fn spawn(
        executable: &Path,
        data_dir: &Path,
        local_config: &Path,
        client_config: &Path,
        admin_port: u16,
    ) -> Self {
        let stderr_path = local_config.with_extension("server.log");
        let stderr = File::create(&stderr_path).expect("create packaged server stderr log");
        let child = Command::new(executable)
            .arg("--data-dir")
            .arg(data_dir)
            .arg("--memory-limit-bytes")
            .arg(PROCESS_MEMORY_LIMIT_BYTES)
            .arg("--memory-headroom-bytes")
            .arg(PROCESS_MEMORY_HEADROOM_BYTES)
            .arg("--port")
            .arg(admin_port.to_string())
            .arg("--local-v1-config")
            .arg(local_config)
            .arg("--client-config")
            .arg(client_config)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::from(stderr))
            .spawn()
            .expect("spawn packaged local-v1 TerraBase server");
        Self { child, stderr_path }
    }

    fn try_wait(&mut self) -> Option<ExitStatus> {
        self.child.try_wait().expect("inspect server process")
    }

    fn signal(&self, signal: &str) {
        let pid = self.child.id().to_string();
        let output = Command::new("sh")
            .arg("-c")
            .arg("kill -$1 \"$2\"")
            .arg("terrabase-client-process-test")
            .arg(signal)
            .arg(pid)
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .output()
            .expect("send signal to packaged server");
        assert!(
            output.status.success(),
            "could not send SIG{signal}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }

    fn terminate_and_wait(&mut self) -> ExitStatus {
        if self
            .child
            .try_wait()
            .expect("inspect server process")
            .is_none()
        {
            self.signal("TERM");
        }
        self.wait_for_exit()
    }

    fn wait_for_exit(&mut self) -> ExitStatus {
        let deadline = Instant::now() + SHUTDOWN_TIMEOUT;
        loop {
            if let Some(status) = self.try_wait() {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "local-v1 server did not drain within {SHUTDOWN_TIMEOUT:?}"
            );
            thread::sleep(Duration::from_millis(20));
        }
    }

    fn stderr(&self) -> String {
        fs::read_to_string(&self.stderr_path).unwrap_or_else(|error| {
            format!(
                "could not read packaged server stderr at {}: {error}",
                self.stderr_path.display()
            )
        })
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

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after Unix epoch")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-client-process-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create local-v1 process-test directory");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&path, fs::Permissions::from_mode(0o700))
                .expect("protect process-test directory");
        }
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

fn server_binary() -> PathBuf {
    crate::server_fixture::server_binary()
}

fn keygen_binary() -> PathBuf {
    crate::server_fixture::keygen_binary()
}

fn init_binary() -> PathBuf {
    if let Some(path) = std::env::var_os("TB_INIT_TEST_BINARY") {
        let path = PathBuf::from(path);
        assert!(
            path.is_file(),
            "TB_INIT_TEST_BINARY is not a file: {}",
            path.display()
        );
        return path
            .canonicalize()
            .expect("canonicalize TB_INIT_TEST_BINARY");
    }

    if let Some(path) = option_env!("CARGO_BIN_EXE_terrabase-init") {
        let path = PathBuf::from(path);
        assert!(
            path.is_file(),
            "Cargo-provided terrabase-init binary is not a file: {}",
            path.display()
        );
        return path
            .canonicalize()
            .expect("canonicalize co-packaged terrabase-init binary");
    }

    panic!(
        "could not find terrabase-init; stage it and set TB_INIT_TEST_BINARY for packaged tests"
    );
}

fn run_initializer(input: &Path, local_config: &Path, data_dir: &Path) -> std::process::Output {
    Command::new(init_binary())
        .arg("--input")
        .arg(input)
        .arg("--local-v1-config")
        .arg(local_config)
        .arg("--data-dir")
        .arg(data_dir)
        .arg("--memory-limit-bytes")
        .arg(PROCESS_MEMORY_LIMIT_BYTES)
        .arg("--memory-headroom-bytes")
        .arg(PROCESS_MEMORY_HEADROOM_BYTES)
        .stdin(Stdio::null())
        .output()
        .expect("run packaged terrabase-init")
}

fn snapshot_directory(directory: &Path) -> Vec<(PathBuf, bool, Vec<u8>)> {
    fn collect(root: &Path, directory: &Path, entries: &mut Vec<(PathBuf, bool, Vec<u8>)>) {
        let mut children: Vec<_> = fs::read_dir(directory)
            .expect("read shard directory for no-mutation assertion")
            .map(|entry| entry.expect("read shard directory entry"))
            .collect();
        children.sort_by_key(|entry| entry.file_name());
        for entry in children {
            let path = entry.path();
            let relative = path
                .strip_prefix(root)
                .expect("shard entry is below snapshot root")
                .to_path_buf();
            let kind = entry.file_type().expect("inspect shard entry type");
            if kind.is_dir() {
                entries.push((relative, true, Vec::new()));
                collect(root, &path, entries);
            } else {
                assert!(
                    kind.is_file(),
                    "unexpected special file in shard: {}",
                    path.display()
                );
                entries.push((
                    relative,
                    false,
                    fs::read(&path).expect("read shard file for no-mutation assertion"),
                ));
            }
        }
    }

    let mut entries = Vec::new();
    collect(directory, directory, &mut entries);
    entries
}

fn document_store_config(path: &Path) -> DocumentStoreConfig {
    DocumentStoreConfig {
        storage: StorageConfig::local(path, TENANT, WorkerId(9)),
        database: DATABASE,
        shard: SHARD,
        origin: OriginId {
            replica_id: REPLICA,
            incarnation: INCARNATION,
        },
        epoch: MembershipEpoch::new(EPOCH),
        configuration: ConfigurationId::from_bytes(CONFIGURATION)
            .expect("nonzero local-v1 configuration"),
        replication: ReplicationLimits {
            max_origins: 64,
            max_changes: MAX_EVENT_CHANGES,
            max_siblings: 64,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
        },
    }
}

fn local_mode() -> LocalDocumentMode {
    LocalDocumentMode::new(
        TENANT,
        DATABASE,
        SHARD,
        OriginId {
            replica_id: REPLICA,
            incarnation: INCARNATION,
        },
        MembershipEpoch::new(EPOCH),
        CONFIGURATION,
    )
}

fn fixture_budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).expect("fixture memory budget"))
}

fn first_schema() -> Vec<u8> {
    let fields = [
        SchemaField {
            field_id: 1,
            parent_field_id: 0,
            value_type: FieldType::Null,
            element_type: None,
            flags: 1,
            name: "explicit_null",
        },
        SchemaField {
            field_id: 2,
            parent_field_id: 0,
            value_type: FieldType::Bytes,
            element_type: None,
            flags: 0,
            name: "payload",
        },
        SchemaField {
            field_id: 3,
            parent_field_id: 0,
            value_type: FieldType::I64,
            element_type: None,
            flags: 0,
            name: "count",
        },
        SchemaField {
            field_id: 4,
            parent_field_id: 0,
            value_type: FieldType::Utf8,
            element_type: None,
            flags: 0,
            name: "label",
        },
    ];
    let mut encoded = Vec::new();
    encode_schema_into(&mut encoded, &fields).expect("encode first local schema");
    encoded
}

fn second_schema() -> Vec<u8> {
    let fields = [SchemaField {
        field_id: 7,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 1,
        name: "label",
    }];
    let mut encoded = Vec::new();
    encode_schema_into(&mut encoded, &fields).expect("encode second local schema");
    encoded
}

fn initializer_input() -> Value {
    let field = |field_id, name, value_type, required| {
        json!({
            "field_id": field_id,
            "parent_field_id": 0,
            "value_type": value_type,
            "element_type": null,
            "required": required,
            "nullable": false,
            "array_element_nullable": false,
            "name": name,
        })
    };
    json!({
        "version": 1,
        "tenant": TENANT.to_string(),
        "database": DATABASE.to_string(),
        "shard": SHARD.get().to_string(),
        "collections": [
            {
                "collection": FIRST_COLLECTION.to_string(),
                "fields": [
                    field(1, "explicit_null", "Null", true),
                    field(2, "payload", "Bytes", false),
                    field(3, "count", "I64", false),
                    field(4, "label", "Utf8", false),
                ],
            },
            {
                "collection": SECOND_COLLECTION.to_string(),
                "fields": [field(7, "label", "Utf8", true)],
            },
        ],
    })
}

fn provision_schema(
    store: &mut DocumentStore,
    nonce: u8,
    collection: CollectionId,
    schema_bytes: &[u8],
) -> LocalSchemaBinding {
    let mut catalog = Vec::new();
    let schema_digest = encode_catalog_into(&mut catalog, collection, 1, 0, schema_bytes)
        .expect("encode immutable schema catalog fixture");
    assert!(
        store
            .provision_schema(
                TransactionId::new(expires_at_unix_ms(), [nonce; 16]),
                collection,
                1,
                0,
                schema_bytes,
            )
            .expect("provision trusted local-v1 schema")
            .is_some()
    );
    LocalSchemaBinding {
        collection,
        generation: 1,
        schema_digest,
    }
}

fn local_schema_binding(collection: CollectionId, schema_bytes: &[u8]) -> LocalSchemaBinding {
    let mut catalog = Vec::new();
    let schema_digest = encode_catalog_into(&mut catalog, collection, 1, 0, schema_bytes)
        .expect("encode expected initializer schema catalog");
    LocalSchemaBinding {
        collection,
        generation: 1,
        schema_digest,
    }
}

fn serialize_commit(expires_at: u64, nonce: [u8; 16], writes: Vec<Value>) -> String {
    serde_json::to_string(&json!({
        "version": 1,
        "transaction_id": {
            "expires_at_unix_ms": expires_at.to_string(),
            "nonce": hex(&nonce),
        },
        "writes": writes,
    }))
    .expect("serialize local-v1 transaction")
}

fn expires_at_unix_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock is after Unix epoch")
        .as_millis() as u64
        + 60 * 60 * 1_000
}

fn hex(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut result, "{byte:02x}").expect("write lowercase hex");
    }
    result
}

fn write_material(path: &Path, bytes: &[u8], private: bool) {
    let ordinal = NEXT_MATERIAL.fetch_add(1, Ordering::Relaxed);
    let name = path
        .file_name()
        .expect("test material has a file name")
        .to_string_lossy();
    let temporary = path.with_file_name(format!(".{name}.{}.{}.tmp", std::process::id(), ordinal));
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(if private { 0o600 } else { 0o644 });
    }
    let mut file = options
        .open(&temporary)
        .expect("create replacement process-test material");
    file.write_all(bytes).expect("write process-test material");
    file.sync_all().expect("sync process-test material");
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mode = if private { 0o600 } else { 0o644 };
        fs::set_permissions(&temporary, fs::Permissions::from_mode(mode))
            .expect("set process-test material permissions");
    }
    fs::rename(&temporary, path).expect("atomically replace process-test material");
}

fn available_loopback_port() -> u16 {
    TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
        .expect("bind ephemeral loopback port")
        .local_addr()
        .expect("read ephemeral port")
        .port()
}

async fn send_get(client: &reqwest::Client, url: &str, token: &str) -> (u16, String) {
    let response = client
        .get(url)
        .bearer_auth(token)
        .send()
        .await
        .expect("send authenticated HTTPS GET");
    let status = response.status().as_u16();
    let body = response.text().await.expect("read HTTPS GET response");
    (status, body)
}

async fn send_get_without_key(client: &reqwest::Client, url: &str) -> (u16, String) {
    let response = client
        .get(url)
        .send()
        .await
        .expect("send unauthenticated HTTPS GET");
    let status = response.status().as_u16();
    let body = response
        .text()
        .await
        .expect("read unauthenticated response");
    (status, body)
}

async fn send_commit(
    client: &reqwest::Client,
    url: &str,
    token: &str,
    body: &str,
) -> (u16, String) {
    let response = client
        .post(url)
        .bearer_auth(token)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .body(body.to_owned())
        .send()
        .await
        .expect("send authenticated HTTPS commit");
    let status = response.status().as_u16();
    let body = response.text().await.expect("read HTTPS commit response");
    (status, body)
}

fn field(fields: &Value, field_id: u64) -> &Value {
    fields
        .as_array()
        .expect("encoded fields array")
        .iter()
        .find(|entry| entry["id"] == field_id)
        .expect("expected field in typed response")
}

#[cfg(feature = "slow-tests")]
fn assert_error_code(body: &str, code: &str) {
    assert_eq!(
        serde_json::from_str::<Value>(body).expect("error response JSON")["error"]["code"],
        code
    );
}

#[cfg(feature = "slow-tests")]
async fn wait_for_status(client: &reqwest::Client, url: &str, token: &str, expected: u16) {
    let deadline = Instant::now() + STARTUP_TIMEOUT;
    loop {
        if let Ok(response) = client.get(url).bearer_auth(token).send().await {
            if response.status().as_u16() == expected {
                return;
            }
        }
        assert!(
            Instant::now() < deadline,
            "client listener did not return HTTP {expected} within {STARTUP_TIMEOUT:?}"
        );
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

#[cfg(feature = "slow-tests")]
async fn wait_for_rejected_handshakes(fixture: &LocalFixture, expected: u64) {
    let deadline = Instant::now() + STARTUP_TIMEOUT;
    loop {
        let metrics = fixture.metrics().await;
        let rejected = rejected_handshake_count(&metrics);
        if rejected.is_some_and(|count| count >= expected) {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "timed-out TLS handshakes did not release all {expected} socket slots; metrics: {metrics}"
        );
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

#[cfg(feature = "slow-tests")]
fn rejected_handshake_count(metrics: &str) -> Option<u64> {
    metrics.lines().find_map(|line| {
        (line.starts_with("terrabase_client_tls_handshakes_total")
            && line.contains("result=\"rejected\""))
        .then(|| line.split_whitespace().last()?.parse::<u64>().ok())
        .flatten()
    })
}
