use super::*;
use aws_lc_rs::{
    digest,
    digest::SHA256,
    rand::SystemRandom,
    signature::{Ed25519KeyPair, KeyPair},
};
use base64::Engine as _;
use serde_json::json;
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    path::PathBuf,
    sync::Arc,
};
use terrabase::identity::{ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::{
    placement::{PlacementBoundary, PlacementJournal, transition::TransitionTrust},
    proto::terrabase::v1 as pb,
};

const DOMAIN: &[u8] = b"terrabase-placement-transition-v1\0";

#[derive(Serialize)]
struct Member {
    identity: String,
    region: String,
    placement_digest: String,
}

#[derive(Serialize)]
struct Request {
    operation_id: String,
    scope_id: String,
    prior_epoch: u64,
    epoch: u64,
    generation: u64,
    members: Vec<Member>,
}

struct RouteFixtures {
    environment: String,
    cluster: String,
    member: String,
    peer: String,
    scope: DatabaseRouteScope,
}

struct KeyFixtures {
    authority: Ed25519KeyPair,
    genesis: Ed25519KeyPair,
}

struct Fixture {
    root: PathBuf,
    usage_root: PathBuf,
    placement: Arc<PlacementJournal>,
    environment: String,
    member: String,
    region: String,
    scope: DatabaseRouteScope,
    proof_digest: String,
}

impl Fixture {
    fn new() -> Self {
        let route = route_fixtures();
        let (root, usage_root) = usage_roots(&route.member);
        let (journal, placement_path, proposal) = setup_journal(&route, &root);
        let keys = deliver_keys(&journal, &route, &proposal);
        let proof_digest = accepted_proof(&journal, &placement_path, &route, &proposal, &keys);
        Self {
            root,
            usage_root,
            placement: journal,
            environment: route.environment,
            member: route.member,
            region: "GRU".into(),
            scope: route.scope,
            proof_digest,
        }
    }

    fn registration(&self) -> UsageRegistration {
        UsageRegistration {
            schema_version: 1,
            environment: self.environment.clone(),
            tenant_id: self.scope.tenant().to_text(),
            project_id: self.scope.project().to_text(),
            database_id: self.scope.database().to_text(),
            member_id: self.member.clone(),
            region: self.region.clone(),
            selected_regions: vec![self.region.clone()],
            residency: "GRU".into(),
            partition: "partition-a".into(),
            stream_epoch: 1,
            placement_proof_digest: self.proof_digest.clone(),
            operation_id: "usage-registration".into(),
        }
    }

    fn open(&self) -> UsageAdmissionJournal {
        UsageAdmissionJournal::open(
            &self.usage_root,
            self.environment.clone(),
            self.member.clone(),
            self.region.clone(),
            self.placement.clone(),
        )
        .unwrap()
    }
}

fn route_fixtures() -> RouteFixtures {
    RouteFixtures {
        environment: "production".to_owned(),
        cluster: ClusterUlid::generate().to_text(),
        member: MemberUlid::generate().to_text(),
        peer: MemberUlid::generate().to_text(),
        scope: DatabaseRouteScope::new(
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
        ),
    }
}

fn usage_roots(member: &str) -> (PathBuf, PathBuf) {
    let root = std::env::temp_dir().join(format!(
        "terrabase-usage-placement-{}",
        hex::encode(&digest::digest(&SHA256, member.as_bytes()).as_ref()[..8])
    ));
    let usage_root = root.join("usage");
    fs::create_dir_all(&usage_root).unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&root, fs::Permissions::from_mode(0o700)).unwrap();
        fs::set_permissions(&usage_root, fs::Permissions::from_mode(0o700)).unwrap();
    }
    (root, usage_root)
}

fn setup_journal(
    route: &RouteFixtures,
    root: &std::path::Path,
) -> (Arc<PlacementJournal>, PathBuf, pb::PlacementProposal) {
    let boundary = PlacementBoundary::new(
        &route.environment,
        &route.cluster,
        &route.member,
        1,
        1,
        [(route.scope, Some("GRU".into()), BTreeSet::from(["GRU".into()]))],
    )
    .unwrap();
    let placement_path = root.join("placement.sqlite");
    let journal = Arc::new(PlacementJournal::open(&placement_path, boundary).unwrap());
    let request = pb::PlacementStageRequest {
        expected_revision: 0,
        proposal: Some(pb::PlacementProposal {
            environment: route.environment.clone(),
            cluster: route.cluster.clone(),
            generation: 1,
            epoch: 1,
            regions: vec![pb::PlacementRegionSeats {
                region: "GRU".into(),
                members: vec![route.member.clone(), route.peer.clone()],
            }],
            assignments: vec![pb::PlacementDatabaseAssignment {
                tenant: route.scope.tenant().to_text(),
                project: route.scope.project().to_text(),
                database: route.scope.database().to_text(),
                region: "GRU".into(),
                writer_member: route.member.clone(),
                follower_member: route.peer.clone(),
            }],
        }),
        declarations: Vec::new(),
    };
    let proposal = journal.stage("fixture", "usage-op", request).unwrap().proposal.unwrap();
    (journal, placement_path, proposal)
}

fn deliver_keys(
    journal: &PlacementJournal,
    route: &RouteFixtures,
    proposal: &pb::PlacementProposal,
) -> KeyFixtures {
    let authority = keypair();
    let genesis = keypair();
    let local = keypair();
    journal
        .configure_transition_trust(
            TransitionTrust::new(
                BTreeMap::from([("authority".into(), authority.public_key().as_ref().to_vec())]),
                BTreeMap::from([("genesis".into(), genesis.public_key().as_ref().to_vec())]),
                BTreeMap::from([
                    (
                        format!(
                            "spiffe://terrascale.tech/{}/terrabase-member/{}/{}",
                            route.environment, route.cluster, route.member
                        ),
                        local.public_key().as_ref().to_vec(),
                    ),
                    (
                        format!(
                            "spiffe://terrascale.tech/{}/terrabase-member/{}/{}",
                            route.environment, route.cluster, proposal.regions[0].members[1]
                        ),
                        local.public_key().as_ref().to_vec(),
                    ),
                ]),
            )
            .unwrap(),
        )
        .unwrap();
    KeyFixtures { authority, genesis }
}

fn accepted_proof(
    journal: &PlacementJournal,
    placement_path: &std::path::Path,
    route: &RouteFixtures,
    proposal: &pb::PlacementProposal,
    keys: &KeyFixtures,
) -> String {
    let encoded: Vec<u8> = Connection::open(placement_path)
        .unwrap()
        .query_row("SELECT proposal FROM proposals WHERE revision=1", [], |row| row.get(0))
        .unwrap();
    let proposal_digest = hash(&encoded);
    let mut members = proposal
        .regions
        .iter()
        .flat_map(|region| {
            region.members.iter().map(|identity| Member {
                identity: format!(
                    "spiffe://terrascale.tech/{}/terrabase-member/{}/{}",
                    route.environment, route.cluster, identity
                ),
                region: region.region.clone(),
                placement_digest: proposal_digest.clone(),
            })
        })
        .collect::<Vec<_>>();
    members.sort_by(|a, b| a.identity.cmp(&b.identity));
    let request = Request {
        operation_id: "usage-op".into(),
        scope_id: format!("{}/{}", route.environment, route.cluster),
        prior_epoch: 0,
        epoch: 1,
        generation: 1,
        members,
    };
    let request_digest = hash(&serde_json::to_vec(&request).unwrap());
    let grant_payload = serde_json::to_vec(
        &json!({"kind":"genesis_grant","grant_id":"grant-one","request_digest":request_digest}),
    )
    .unwrap();
    let proof = json!({"schema_version":1,"kind":"accepted_epoch","request":request,"request_digest":request_digest,"authority_epoch":1,"journal_revision":1,"genesis_grant_id":"grant-one","receipt_digests":[],"drain_receipts":[],"prior_generation":0,"previous_proof_digest":"","lease_id":"lease-one","genesis_grant":sign("genesis", &grant_payload, &keys.genesis)});
    let proof_payload = serde_json::to_vec(&proof).unwrap();
    let envelope = serde_json::to_vec(&sign("authority", &proof_payload, &keys.authority)).unwrap();
    journal
        .accept(
            "usage-op",
            &pb::PlacementAcceptRequest { proposal_revision: 1, proof: envelope },
        )
        .unwrap()
        .proof_digest
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

fn keypair() -> Ed25519KeyPair {
    Ed25519KeyPair::from_pkcs8(
        Ed25519KeyPair::generate_pkcs8(&SystemRandom::new()).unwrap().as_ref(),
    )
    .unwrap()
}
fn hash(bytes: &[u8]) -> String {
    hex::encode(digest::digest(&SHA256, bytes).as_ref())
}
fn sign(id: &str, payload: &[u8], key: &Ed25519KeyPair) -> serde_json::Value {
    let mut input = DOMAIN.to_vec();
    input.extend_from_slice(payload);
    json!({"key_id":id,"payload":base64::engine::general_purpose::STANDARD.encode(payload),"signature":base64::engine::general_purpose::STANDARD.encode(key.sign(&input).as_ref())})
}

#[test]
fn valid_registration_persists_reopens_and_is_idempotent() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    let record = fixture.registration();
    let first = journal.admit(record.clone()).unwrap();
    assert_eq!(
        journal.admit(record).unwrap().registration.operation_id,
        first.registration.operation_id
    );
    drop(journal);
    assert_eq!(
        fixture.open().admit(fixture.registration()).unwrap().registration.partition,
        "partition-a"
    );
}

#[test]
fn changed_retry_tuple_is_rejected() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    journal.admit(fixture.registration()).unwrap();
    for change in 0..3 {
        let mut record = fixture.registration();
        match change {
            0 => record.partition = "partition-b".into(),
            1 => record.placement_proof_digest = "0".repeat(64),
            _ => record.operation_id = "other-operation".into(),
        }
        assert!(journal.admit(record).is_err(), "changed retry {change} accepted");
    }
}

#[test]
fn resolve_distinguishes_missing_and_delivered_registration() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    let record = fixture.registration();
    let admission = crate::admission::AdmissionRecord {
        created_at: 1,
        environment: fixture.environment.clone(),
        tenant_id: record.tenant_id.clone(),
        project_id: record.project_id.clone(),
        database_id: record.database_id.clone(),
        parent_database_id: None,
        key_version: "v1".into(),
        name: "db".into(),
        regions: record.selected_regions.clone(),
        residency: record.residency.clone(),
        operation_id: record.operation_id.clone(),
        region: record.region.clone(),
        member_id: record.member_id.clone(),
    };
    assert_eq!(journal.resolve(&admission, 1, &fixture.proof_digest).unwrap(), None);
    journal.admit(record).unwrap();
    assert_eq!(
        journal.resolve(&admission, 1, &fixture.proof_digest).unwrap().as_deref(),
        Some("partition-a")
    );
}

#[test]
fn host_owner_regions_and_digest_mismatches_are_rejected() {
    let fixture = Fixture::new();
    for mutate in 0..7 {
        let journal = fixture.open();
        let mut record = fixture.registration();
        match mutate {
            0 => record.environment = "other".into(),
            1 => record.member_id = "other".into(),
            2 => record.selected_regions = vec!["SFO".into()],
            3 => record.placement_proof_digest = "0".repeat(64),
            4 => record.selected_regions = vec!["GRU".into(), "SFO".into()],
            5 => record.tenant_id = TenantUlid::generate().to_text(),
            _ => record.stream_epoch = 2,
        }
        assert!(journal.admit(record).is_err(), "mismatch {mutate} accepted");
    }
}

#[test]
fn epoch_storage_preserves_unsigned_order_and_refuses_scope_reuse() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    let first = fixture.registration();
    journal.admit(first.clone()).unwrap();
    // Storage-only boundary test. Public admission above still requires signed placement.
    let mut next = first.clone();
    next.stream_epoch = u64::MAX;
    next.operation_id = "next-epoch".into();
    let mut changed = next.clone();
    changed.tenant_id = TenantUlid::generate().to_text();
    assert!(journal.persist(changed).is_err());
    journal.persist(next.clone()).unwrap();
    assert!(journal.persist(first).is_err());
    assert!(journal.persist(next).is_ok());
    assert_eq!(
        journal
            .connection
            .lock()
            .unwrap()
            .query_row("SELECT count(*) FROM registrations", [], |row| row.get::<_, i64>(0))
            .unwrap(),
        2
    );
}

#[test]
fn corrupt_stored_registration_is_rejected() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    journal.admit(fixture.registration()).unwrap();
    drop(journal);
    let connection =
        rusqlite::Connection::open(fixture.usage_root.join("usage-admissions.sqlite")).unwrap();
    connection
        .execute("UPDATE registrations SET payload=?1", [b"invalid json".as_slice()])
        .unwrap();
    drop(connection);
    assert!(
        fixture
            .open()
            .resolve(
                &crate::admission::AdmissionRecord {
                    created_at: 1,
                    environment: fixture.environment.clone(),
                    tenant_id: fixture.scope.tenant().to_text(),
                    project_id: fixture.scope.project().to_text(),
                    database_id: fixture.scope.database().to_text(),
                    parent_database_id: None,
                    key_version: "v1".into(),
                    name: "db".into(),
                    regions: vec!["GRU".into()],
                    residency: "GRU".into(),
                    operation_id: "usage-registration".into(),
                    region: "GRU".into(),
                    member_id: fixture.member.clone()
                },
                1,
                &fixture.proof_digest
            )
            .is_err()
    );
}

#[cfg(unix)]
#[test]
fn unsafe_usage_paths_are_refused() {
    use std::os::unix::fs::PermissionsExt;
    let fixture = Fixture::new();
    let link = fixture.root.join("usage-link");
    std::os::unix::fs::symlink(&fixture.usage_root, &link).unwrap();
    assert!(
        UsageAdmissionJournal::open(
            &link,
            fixture.environment.clone(),
            fixture.member.clone(),
            fixture.region.clone(),
            fixture.placement.clone()
        )
        .is_err()
    );
    let world = fixture.root.join("world");
    fs::create_dir(&world).unwrap();
    fs::set_permissions(&world, fs::Permissions::from_mode(0o755)).unwrap();
    assert!(
        UsageAdmissionJournal::open(
            &world,
            fixture.environment.clone(),
            fixture.member.clone(),
            fixture.region.clone(),
            fixture.placement.clone()
        )
        .is_err()
    );
    fs::set_permissions(&world, fs::Permissions::from_mode(0o700)).unwrap();
    drop(
        UsageAdmissionJournal::open(
            &world,
            fixture.environment.clone(),
            fixture.member.clone(),
            fixture.region.clone(),
            fixture.placement.clone(),
        )
        .unwrap(),
    );
    let file = world.join("usage-admissions.sqlite");
    fs::set_permissions(&file, fs::Permissions::from_mode(0o644)).unwrap();
    assert!(
        UsageAdmissionJournal::open(
            &world,
            fixture.environment.clone(),
            fixture.member.clone(),
            fixture.region.clone(),
            fixture.placement.clone()
        )
        .is_err()
    );
    fs::remove_file(&file).unwrap();
    drop(fixture.open());
    std::os::unix::fs::symlink(fixture.usage_root.join("usage-admissions.sqlite"), &file).unwrap();
    assert!(
        UsageAdmissionJournal::open(
            &world,
            fixture.environment.clone(),
            fixture.member.clone(),
            fixture.region.clone(),
            fixture.placement.clone()
        )
        .is_err()
    );
}

#[test]
fn allocation_retry_identity_ignores_randomized_signature() {
    let first = terrabase::usage::SignedAllocation {
        payload: br#"{"grant":{"operation_id":"op"}}"#.to_vec(),
        signature: vec![1, 2, 3],
        key_id: "key".into(),
    };
    let mut retry = first.clone();
    retry.signature = vec![9, 8, 7, 6];
    assert!(same_allocation_identity(&first, &retry));
    retry.payload.push(b' ');
    assert!(!same_allocation_identity(&first, &retry));
}
