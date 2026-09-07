use super::*;

use axum::body::Body;
use axum::http::{Request, StatusCode};
use rcgen::{
    BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa, KeyPair,
    KeyUsagePurpose, SanType,
};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use std::collections::{BTreeMap, BTreeSet};
use std::net::{Ipv4Addr, UdpSocket};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use terrabase::encryption::{
    DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultDatabaseKeys,
};
use terrabase::identity::{ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid, Ulid};
use terrabase::regions::RegionCode;
use terrabase_auth::{Action, AuthAuthority, AuthNetwork, AuthorityError, Command, SqliteStore};
use terrabase_cluster::production::{
    MemberWorkloadIdentity, ProductionPeerAdmission, ProductionQuicEndpoint, ProductionRegion,
    ProductionTlsMaterial, ProductionTransportConfig, ProductionTransportError,
    ProductionTransportLimits,
};
use terrabase_tbep::auth_bootstrap::{AuthBootstrapBoundary, AuthBootstrapJournal};
use terrabase_tbep::proto::terrabase::v1::{AuthGroupBootstrapGrant, AuthGroupVoter};

struct Fixture {
    runtime: Arc<AuthRuntime>,
    authorities: Vec<AuthAuthority>,
    endpoints: Vec<Arc<ProductionQuicEndpoint>>,
    workers: Vec<tokio::task::JoinHandle<()>>,
    root: PathBuf,
    database: String,
    token: String,
    keys: crate::test_keys::KeyDeliveryFixture,
}

fn limits() -> ProductionTransportLimits {
    ProductionTransportLimits::try_new(
        16 * 1024 * 1024,
        32,
        8,
        128 * 1024 * 1024,
        Duration::from_secs(5),
        Duration::from_secs(5),
        Duration::from_secs(10),
        Duration::from_secs(5),
    )
    .unwrap()
}

fn tls(
    identity: &MemberWorkloadIdentity,
    dns: &str,
    issuer: &rcgen::Certificate,
    issuer_key: &KeyPair,
    anchor: &CertificateDer<'static>,
) -> ProductionTlsMaterial {
    let key = KeyPair::generate().unwrap();
    let mut params = CertificateParams::new(vec![dns.to_owned()]).unwrap();
    params.subject_alt_names.push(SanType::URI(Ia5String::try_from(identity.as_str()).unwrap()));
    params.extended_key_usages =
        vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
    let cert = params.signed_by(&key, issuer, issuer_key).unwrap();
    ProductionTlsMaterial::try_new(
        vec![CertificateDer::from(cert.der().to_vec())],
        PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
        vec![anchor.clone()],
    )
    .unwrap()
}

fn endpoints(ids: &[MemberWorkloadIdentity]) -> Vec<Arc<ProductionQuicEndpoint>> {
    let mut addresses = Vec::new();
    for _ in ids {
        let socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        addresses.push(socket.local_addr().unwrap());
    }
    let issuer_key = KeyPair::generate().unwrap();
    let mut issuer_params = CertificateParams::new(Vec::new()).unwrap();
    issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    issuer_params.key_usages =
        vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
    let issuer = issuer_params.self_signed(&issuer_key).unwrap();
    let anchor = CertificateDer::from(issuer.der().to_vec());
    ids.iter()
        .enumerate()
        .map(|(index, identity)| {
            let region = if index < 2 { ProductionRegion::Gru } else { ProductionRegion::Sfo };
            let peers = ids
                .iter()
                .enumerate()
                .filter(|(peer, _)| *peer != index)
                .map(|(peer, id)| {
                    let peer_region =
                        if peer < 2 { ProductionRegion::Gru } else { ProductionRegion::Sfo };
                    ProductionPeerAdmission::try_new(
                        id.clone(),
                        peer_region,
                        addresses[peer],
                        &format!("member-{peer}.internal"),
                    )
                    .unwrap()
                })
                .collect();
            let config = ProductionTransportConfig::try_new(
                addresses[index],
                identity.clone(),
                region,
                1,
                1,
                peers,
                limits(),
            )
            .unwrap();
            Arc::new(
                ProductionQuicEndpoint::bind(
                    config,
                    tls(
                        identity,
                        &format!("member-{index}.internal"),
                        &issuer,
                        &issuer_key,
                        &anchor,
                    ),
                )
                .unwrap(),
            )
        })
        .collect()
}

async fn create() -> Fixture {
    let root = std::env::temp_dir().join(format!("terrabase-auth-four-{}", Ulid::new()));
    std::fs::create_dir_all(&root).unwrap();
    let tenant = TenantUlid::generate();
    let project = ProjectUlid::generate();
    let database = DatabaseUlid::generate();
    let cluster = ClusterUlid::generate();
    let members: Vec<_> = (0..4).map(|_| MemberUlid::generate()).collect();
    let ids: Vec<_> = members
        .iter()
        .map(|member| {
            MemberWorkloadIdentity::try_new("production", &cluster.to_text(), &member.to_text())
                .unwrap()
        })
        .collect();
    let endpoints = endpoints(&ids);
    let keys = crate::test_keys::key_delivery();
    let group = format!("auth-four-{}", Ulid::new());
    let boundary = AuthBootstrapBoundary::new(
        "production",
        &cluster.to_text(),
        &members[0].to_text(),
        BTreeSet::from([terrabase_cluster::production::DatabaseRouteScope::new(
            tenant, project, database,
        )]),
        BTreeMap::from([
            (members[0].to_text(), "GRU".into()),
            (members[1].to_text(), "GRU".into()),
            (members[2].to_text(), "SFO".into()),
            (members[3].to_text(), "SFO".into()),
        ]),
    )
    .unwrap();
    let journal = Arc::new(
        AuthBootstrapJournal::open(&root.join("auth-bootstrap.sqlite"), boundary).unwrap(),
    );
    let incarnation = journal.identity().unwrap().storage_incarnation;
    let voters: Vec<_> = ids
        .iter()
        .enumerate()
        .map(|(i, id)| AuthGroupVoter {
            node_id: (i + 1) as u64,
            member: members[i].to_text(),
            region: if i < 2 { "GRU".into() } else { "SFO".into() },
            spiffe_uri: id.as_str().into(),
            storage_incarnation: if i == 0 { incarnation.clone() } else { Ulid::new().to_string() },
        })
        .collect();
    let grant = AuthGroupBootstrapGrant {
        environment: "production".into(),
        cluster: cluster.to_text(),
        group_id: group.clone(),
        tenant: tenant.to_text(),
        project: project.to_text(),
        database: database.to_text(),
        generation: 1,
        regions: vec!["GRU".into(), "SFO".into()],
        voters: voters.clone(),
        authority_revision: 1,
        genesis_node: 1,
    };
    journal
        .bootstrap(
            "test",
            terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapRequest {
                operation_id: Ulid::new().to_string(),
                grant: Some(grant.clone()),
            },
        )
        .unwrap();
    let source = |region| {
        OciVaultDatabaseKeys::new(
            keys.provider.clone(),
            KeyVersionMetadata::new(
                KeyScope::new_with_project(
                    "production",
                    tenant,
                    project,
                    database,
                    KeyPurpose::Data,
                )
                .unwrap(),
                DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
            ),
            region,
        )
        .unwrap()
    };
    let voter_map = BTreeMap::from([
        (1, ids[0].member().clone()),
        (2, ids[1].member().clone()),
        (3, ids[2].member().clone()),
        (4, ids[3].member().clone()),
    ]);
    let placement = BTreeSet::from([ProductionRegion::Gru, ProductionRegion::Sfo]);
    let mut authorities = Vec::new();
    for i in 1..4 {
        let store = SqliteStore::open_encrypted(
            &root.join(format!("voter-{i}.sqlite")),
            &group,
            Arc::new(source(if i < 2 { RegionCode::GRU } else { RegionCode::SFO })),
            true,
        )
        .unwrap();
        let network =
            AuthNetwork::new(endpoints[i].clone(), &group, voter_map.clone(), &placement).unwrap();
        authorities.push(
            AuthAuthority::recover_admitted(
                store,
                network,
                terrabase_auth::consensus_config(&group),
                Duration::from_secs(10),
            )
            .await
            .unwrap(),
        );
    }
    let token = "four-node-bootstrap-key-01234567890123456789".to_owned();
    let key_path = root.join("bootstrap.key");
    std::fs::write(&key_path, &token).unwrap();
    let mut production = super::tests::admitted_fixture::production_config(
        (tenant, project, database),
        cluster,
        members[0],
        members[1],
        &root,
        &keys,
    );
    production.databases[0].selected_regions = BTreeSet::from([RegionCode::GRU, RegionCode::SFO]);
    production.placement.regions.insert(RegionCode::SFO, [members[2], members[3]]);
    let public = super::tests::admitted_fixture::public_config(&root, key_path);
    let runtime = AuthRuntime::new(endpoints[0].clone(), journal, production, &public).unwrap();
    runtime.reconcile().await.unwrap();
    // Node one is the runtime-owned authority, sharing its encrypted log and receiver.
    authorities.insert(0, runtime.domains.read().unwrap()[&group].authority.clone());
    let mut workers = Vec::new();
    let runtime_receiver = runtime.clone();
    workers.push(tokio::spawn(async move {
        let _ = runtime_receiver.receive().await;
    }));
    for i in 1..4 {
        let endpoint = endpoints[i].clone();
        let authority = authorities[i].clone();
        let group = group.clone();
        let voter_map = voter_map.clone();
        let placement = placement.clone();
        workers.push(tokio::spawn(async move {
            loop {
                let connection = match endpoint.accept().await {
                    Ok(Some(c)) => c,
                    Ok(None) => break,
                    Err(ProductionTransportError::Draining) => break,
                    Err(_) => continue,
                };
                let network =
                    AuthNetwork::new(endpoint.clone(), &group, voter_map.clone(), &placement)
                        .unwrap();
                let _ = network.receive(&connection, &authority).await;
            }
        }));
    }
    wait_ready(&authorities[0]).await;
    Fixture {
        runtime,
        authorities,
        endpoints,
        workers,
        root,
        database: database.to_text(),
        token,
        keys,
    }
}

async fn wait_ready(authority: &AuthAuthority) {
    tokio::time::timeout(Duration::from_secs(20), async {
        loop {
            if authority.find_one("user".into(), "readiness".into()).await.is_ok() {
                break;
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
    })
    .await
    .expect("four admitted voters must elect a leader and complete a quorum read");
}

async fn stop(fixture: Fixture) {
    fixture.runtime.shutdown().await;
    for worker in &fixture.workers {
        worker.abort();
    }
    for worker in fixture.workers {
        let _ = worker.await;
    }
    for authority in &fixture.authorities {
        let _ = authority.shutdown().await;
    }
    drop(fixture.endpoints);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn four_voters_serve_public_create_and_consume() {
    let fixture = create().await;
    let auth = format!("Bearer {}", fixture.token);
    let insert = super::tests::admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/insertUnique", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "four-create")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"token","record":{"id":"four","value":"opaque"}}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(insert.0, StatusCode::OK, "{:#?}", insert.1);
    let consume = super::tests::admitted_fixture::request(&fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/consumeMatching", fixture.database))
            .header("authorization", &auth).header("idempotency-key", "four-consume")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"token","predicates":[{"field":"value","operator":"eq","value":"opaque"}]}"#)).unwrap()).await;
    assert_eq!(consume.0, StatusCode::OK, "{:#?}", consume.1);
    assert_eq!(consume.1["record"]["id"], "four");
    stop(fixture).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn four_voters_deny_mutation_without_quorum() {
    let fixture = create().await;
    fixture.workers[2].abort();
    fixture.workers[3].abort();
    let result = fixture.authorities[0]
        .mutate(Command {
            idempotency_key: "partitioned".into(),
            observed_at_ms: 1,
            action: Action::InsertUnique {
                model: "user".into(),
                record: BTreeMap::from([("id".into(), serde_json::json!("denied"))]),
            },
        })
        .await;
    assert!(matches!(result, Err(AuthorityError::Unavailable | AuthorityError::Deadline)));
    stop(fixture).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn four_voter_encrypted_logs_survive_authority_restart() {
    let mut fixture = create().await;
    let outcome = fixture.authorities[0]
        .mutate(Command {
            idempotency_key: "retained".into(),
            observed_at_ms: 1,
            action: Action::InsertUnique {
                model: "user".into(),
                record: BTreeMap::from([("id".into(), serde_json::json!("retained"))]),
            },
        })
        .await
        .unwrap();
    assert!(matches!(outcome, terrabase_auth::Outcome::Record { .. }));
    fixture.workers[0].abort();
    fixture.runtime.shutdown().await;
    let public = super::tests::admitted_fixture::public_config(
        &fixture.root,
        fixture.root.join("bootstrap.key"),
    );
    let runtime = AuthRuntime::new(
        fixture.endpoints[0].clone(),
        fixture.runtime.journal.clone(),
        fixture.runtime.production.clone(),
        &public,
    )
    .unwrap();
    runtime.reconcile().await.unwrap();
    fixture.authorities[0] =
        runtime.domains.read().unwrap().values().next().unwrap().authority.clone();
    let receiver = runtime.clone();
    fixture.workers[0] = tokio::spawn(async move {
        let _ = receiver.receive().await;
    });
    fixture.runtime = runtime;
    wait_ready(&fixture.authorities[0]).await;
    let retained = fixture.authorities[0].find_one("user".into(), "retained".into()).await.unwrap();
    assert_eq!(retained.unwrap()["id"], serde_json::json!("retained"));
    stop(fixture).await;
}
