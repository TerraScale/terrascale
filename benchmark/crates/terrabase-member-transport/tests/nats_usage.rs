//! The NATS KV/config/discovery integration of `TerraBaseV2` (SE-06).
//!
//! PD-2026-08-26-13 D2 and ADR 0012 D-2: within `TerraBaseV2`, NATS serves
//! exactly three roles — a key-value store for operational state,
//! configuration sharing, and service/member discoverability — under the
//! Orchestrator NATS spec's separation rules (own account and subject
//! namespace; never the control plane's), and no database data is stored in
//! or transported through NATS. These are the red-then-green contract tests
//! of that boundary. Every test runs against the in-fixture NATS-facing
//! semantics; a live NATS server run is an integration step recorded in the
//! SE-06 evidence, not a prerequisite here.

use terrabase_member_transport::nats_usage::{
    AuditReport, ConfigName, ConfigRecord, DATABASE_NATS_NAMESPACE, DiscoveryRecord, NatsFixture,
    ObservationState, RecordValue, Subject, UsageError,
};

/// One fixture with its database client authenticated.
fn database_fixture() -> NatsFixture {
    NatsFixture::new().expect("the fixture builds its accounts")
}

#[test]
fn the_kv_role_stores_operational_state_under_revisions() {
    let mut fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");
    let subject = Subject::operational("cell-a", "ownership/main").expect("valid subject");
    let record = RecordValue::operational_range_ownership("main", "m-a1", 1).expect("valid record");
    let revision = client.put(&subject, record).expect("the KV put succeeds");
    assert_eq!(revision, 1, "the first revision is 1");
    let second = client
        .put(
            &subject,
            RecordValue::operational_range_ownership("main", "m-a1", 2).expect("valid record"),
        )
        .expect("the second put succeeds");
    assert_eq!(second, 2, "every put advances the revision");
    let stored = client.get(&subject).expect("the value reads back");
    assert!(
        matches!(&stored.value, RecordValue::Operational(record)
            if record.range() == "main" && record.epoch() == 2),
        "the KV holds the latest operational record: {stored:?}"
    );
    assert_eq!(stored.revision, 2);
    let _ = &mut fixture;
}

#[test]
fn ownership_transitions_use_compare_and_swap_and_fence_stale_epochs() {
    let fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");
    client
        .put(
            &Subject::operational("cell-a", "ownership/main").expect("valid subject"),
            RecordValue::operational_range_ownership("main", "m-a1", 1).expect("record"),
        )
        .expect("seed ownership");

    // A transition that does not name the current epoch is rejected: no
    // stale writer can flip the advertised ownership.
    let stale = client.transition_ownership("cell-a", "main", "m-b1", 1, 2);
    assert_eq!(stale, Err(UsageError::CasConflict), "a stale epoch is fenced: {stale:?}");

    // The correct transition succeeds and the advertisement raises its
    // epoch; the deposed owner's re-assertion at the old epoch is fenced.
    client
        .transition_ownership("cell-a", "main", "m-b1", 2, 2)
        .expect("the raised-epoch transition commits");
    let deposed = client.transition_ownership("cell-a", "main", "m-a1", 2, 3);
    assert_eq!(deposed, Err(UsageError::CasConflict), "the deposed owner is fenced: {deposed:?}");
}

#[test]
fn the_config_role_shares_versioned_configuration_monotonically() {
    let fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");
    let settings = terrabase_member_transport::nats_usage::LaneSettings { catch_up_batch: 32 };
    client
        .publish_config(
            ConfigRecord::new(ConfigName::LaneSettings, 1, settings).expect("valid config"),
        )
        .expect("the first version publishes");
    let latest = client.latest_config(ConfigName::LaneSettings).expect("the config reads back");
    assert_eq!(latest.version(), 1);

    // A stale version is rejected, never silently overwriting the newer
    // configuration the Members already share.
    let stale = client.publish_config(
        ConfigRecord::new(ConfigName::LaneSettings, 1, settings).expect("valid config"),
    );
    assert_eq!(stale, Err(UsageError::StaleConfigVersion), "stale config is fenced: {stale:?}");

    client
        .publish_config(
            ConfigRecord::new(ConfigName::LaneSettings, 2, settings).expect("valid config"),
        )
        .expect("the next version publishes");
    assert_eq!(client.latest_config(ConfigName::LaneSettings).expect("reads").version(), 2);
}

#[test]
fn the_discovery_role_heartbeats_members_with_lease_bounded_observation() {
    let fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");
    let record = |member: &str, generation: u64| {
        DiscoveryRecord::new(
            member,
            &format!("r-{}", &member[2..]),
            &format!("fd-{}", &member[2..]),
            ([127, 0, 0, 1], u16::try_from(9100 + generation).expect("port")).into(),
            generation,
        )
        .expect("valid discovery record")
    };
    client.publish_discovery("cell-a", &record("m-a1", 1)).expect("m-a1 heartbeats");
    client.publish_discovery("cell-a", &record("m-b1", 1)).expect("m-b1 heartbeats");

    // Inside the lease: both members observe healthy; a roster member with
    // no record observes missing — readers distinguish the states.
    let observed = client.discover("cell-a", &["m-a1", "m-b1", "m-g1"]);
    assert_eq!(
        observed
            .iter()
            .map(|(member, state)| (member.member().as_str().to_owned(), *state))
            .collect::<Vec<_>>(),
        vec![
            ("m-a1".to_owned(), ObservationState::Healthy),
            ("m-b1".to_owned(), ObservationState::Healthy),
            ("m-g1".to_owned(), ObservationState::Missing),
        ]
    );

    // m-a1 stops heartbeating: after the lease its record observes stale;
    // the still-heartbeating member stays healthy.
    for _ in 0..=terrabase_member_transport::nats_usage::LEASE_TICKS {
        fixture.tick();
    }
    client.publish_discovery("cell-a", &record("m-b1", 2)).expect("m-b1 renews");
    let observed = client.discover("cell-a", &["m-a1", "m-b1"]);
    assert_eq!(observed[0].1, ObservationState::Stale, "the expired record is stale");
    assert_eq!(observed[1].1, ObservationState::Healthy, "the renewed record stays healthy");
}

#[test]
fn discovery_names_the_engine_socket_lanes_endpoints() {
    // The integration fact: member discoverability through NATS carries the
    // engine-socket lane's endpoint addresses — connection metadata only;
    // no member frame ever rides NATS.
    let fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");
    let mut lane = terrabase_member_transport::QuicLane::new(&["m-a1", "m-b1"]).expect("binds");
    for name in ["m-a1", "m-b1"] {
        let member = terrabase_member_transport::MemberId::try_new(name).expect("valid member");
        let address = lane.address_of(&member).expect("the endpoint is bound");
        client
            .publish_discovery(
                "cell-a",
                &DiscoveryRecord::new(name, "r-alpha", "fd-alpha", address, 1)
                    .expect("valid discovery record"),
            )
            .expect("the discovery record publishes");
    }
    for name in ["m-a1", "m-b1"] {
        let member = terrabase_member_transport::MemberId::try_new(name).expect("valid member");
        let discovered = client
            .discover("cell-a", &[name])
            .first()
            .expect("the member is discoverable")
            .0
            .address();
        assert_eq!(
            discovered,
            lane.address_of(&member).expect("bound"),
            "the discovered address names the socket lane endpoint"
        );
    }
    lane.close();
}

#[test]
fn the_database_namespace_is_own_and_the_control_plane_never_mixes() {
    let fixture = NatsFixture::new().expect("the fixture builds its accounts");

    // The database account and the control-plane account are distinct, with
    // disjoint subject namespaces and no shared credentials.
    let (db_account, cp_account) = (fixture.database_account(), fixture.control_plane_account());
    assert_ne!(db_account, cp_account, "the accounts are distinct");
    let db_credentials = fixture.credentials_of(&db_account);
    let cp_credentials = fixture.credentials_of(&cp_account);
    assert!(
        db_credentials.iter().all(|credential| !cp_credentials.contains(credential)),
        "no credential is shared between the accounts"
    );

    // The database usage stays inside its own subject namespace: every
    // subject the module can form starts with the database prefix.
    for subject in [
        Subject::operational("cell-a", "ownership/main"),
        Subject::config("cell-a", ConfigName::LaneSettings),
        Subject::discovery("cell-a", "m-a1"),
    ] {
        let subject = subject.expect("valid subject");
        assert!(
            subject.as_str().starts_with(&format!("{DATABASE_NATS_NAMESPACE}.")),
            "the subject {subject} stays in the database namespace"
        );
    }

    // A subject outside the database namespace is rejected at construction:
    // the control plane's namespace is never addressable from here.
    assert_eq!(
        Subject::try_new("terrabase.orchestrator.cell-a.secret"),
        Err(UsageError::OutsideDatabaseNamespace)
    );
    assert_eq!(Subject::try_new("terrabase.db.cell-a.?.weird"), Err(UsageError::InvalidSubject));

    // In the fixture, a database-authenticated client cannot publish a
    // control-plane subject, and a control-plane client cannot publish into
    // the database namespace: separation is enforced, not conventional.
    let db_client = fixture.database_client().expect("the database client authenticates");
    let cp_subject = Subject::audit_probe("terrabase.orchestrator.cell-a.kv.authority");
    let record = RecordValue::operational_range_ownership("main", "m-a1", 1).expect("record");
    let leaked = db_client.put(&cp_subject, record.clone());
    assert_eq!(
        leaked,
        Err(UsageError::CrossAccountSubject),
        "the database account stays in its namespace"
    );

    let cp_client = fixture
        .client_for(&cp_account, cp_credentials.first().expect("a cp credential"))
        .expect("the control-plane client authenticates");
    let db_subject = Subject::operational("cell-a", "ownership/main").expect("db subject");
    let inverse = cp_client.put(&db_subject, record);
    assert_eq!(
        inverse,
        Err(UsageError::CrossAccountSubject),
        "the control-plane account cannot write the database namespace"
    );

    // A credential of one account never authenticates the other.
    let swapped = fixture.client_for(&cp_account, db_credentials.first().expect("a db credential"));
    assert!(matches!(swapped, Err(UsageError::BadCredentials)), "credentials are account-bound");
}

#[test]
fn no_database_data_rides_nats_and_the_audit_proves_it() {
    let fixture = database_fixture();
    let client = fixture.database_client().expect("the database account authenticates");

    // Every string-ish parameter of every record constructor is a validated
    // identifier or a typed address: a document body, an encoded member
    // envelope, a journal record, or a sync checkpoint cannot be placed in
    // any of the three roles.
    let canary = "CANARY-document-value {\"doc\":\"body\"} op-1 v1";
    assert_eq!(
        Subject::operational("cell-a", canary),
        Err(UsageError::InvalidSubject),
        "a document body is not a subject token"
    );
    let envelope = terrabase_member_transport::Envelope {
        kind: terrabase_member_transport::FrameKind::Push,
        message_id: 1,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: terrabase_member_transport::MemberId::try_new("m-a1").expect("valid"),
        target: terrabase_member_transport::MemberId::try_new("m-b1").expect("valid"),
        issued_at: terrabase_member_transport::LaneInstant::at_ticks(0),
        expires_at: None,
        range: None,
        epoch: None,
        payload: terrabase_member_transport::Payload::Push {
            tenant: terrabase_member_transport::TenantId::try_new("tenant-a").expect("valid"),
            range: "main".to_owned(),
            epoch: 1,
            commits: Vec::new(),
        },
    };
    let encoded = format!("{:?}", terrabase_member_transport::encode(&envelope).expect("encodes"));
    assert!(
        Subject::operational("cell-a", &encoded).is_err()
            && DiscoveryRecord::new(&encoded, "r-alpha", "fd-alpha", ([127, 0, 0, 1], 9).into(), 1)
                .is_err()
            && terrabase_member_transport::nats_usage::RecordValue::operational_range_ownership(
                &encoded, "m-a1", 1
            )
            .is_err(),
        "an encoded member envelope fits no record constructor"
    );

    // Fill every role with its legitimate records, then run the audit.
    client
        .put(
            &Subject::operational("cell-a", "ownership/main").expect("subject"),
            RecordValue::operational_range_ownership("main", "m-a1", 4).expect("record"),
        )
        .expect("kv");
    client
        .publish_config(
            ConfigRecord::new(
                ConfigName::LaneSettings,
                1,
                terrabase_member_transport::nats_usage::LaneSettings { catch_up_batch: 32 },
            )
            .expect("valid config"),
        )
        .expect("config");
    client
        .publish_discovery(
            "cell-a",
            &DiscoveryRecord::new("m-a1", "r-alpha", "fd-alpha", ([127, 0, 0, 1], 9).into(), 1)
                .expect("record"),
        )
        .expect("discovery");

    let report: AuditReport = client.audit();
    assert!(report.passed(), "every audit check passes:\n{}", report.render());
    eprintln!("{}", report.render());
    let rendered = report.render();
    for line in ["subject namespace", "closed value set", "no raw byte path"] {
        assert!(rendered.contains(line), "the audit records its {line} check");
    }
    for forbidden in ["CANARY", "tenant-a", "doc-", "value"] {
        assert!(
            !client.stored_rendering().contains(forbidden),
            "a database-data marker crossed the NATS surface ({forbidden})"
        );
    }
}

#[test]
fn the_audit_fails_when_a_foreign_subject_or_marker_appears() {
    // The audit is a real check, not a constant: it fails when the fixture
    // holds anything outside the database namespace.
    let fixture = NatsFixture::new().expect("fixture");
    let broken = fixture;
    broken.debug_insert_foreign_subject();
    let client = broken.database_client().expect("authenticates");
    let report = client.audit();
    assert!(!report.passed(), "the audit notices a foreign subject");
}
