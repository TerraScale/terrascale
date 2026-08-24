//! Public-construction tests for the replication seam (W4-REPLICATION R-1).
//!
//! These tests reach only what an external caller can construct: the
//! validated topology identifiers, the builder-style `TopologyView`, the
//! request configuration that names an explicit acknowledgement level, the
//! health vocabulary, and the durable progress cursor. `TenantScope` has no
//! public constructor by design — Platform creates it after authorization —
//! so scope-bound replication behavior is proved by the in-crate tests in
//! `src/replication/tests.rs`.
//!
//! R-1 non-claims held here: no acknowledgement above `Local` is evaluated,
//! no convergence fact is produced, and no multi-region or production
//! topology capability is implied by anything below.

use terrabase_v2::{
    AcknowledgementLevel, Convergence, MemberId, RegionId, ReplicationConfig, ReplicationCursor,
    ReplicationError, ReplicationStatus, TopologyView,
};

/// One Region holding one Member that owns one data range.
fn single_region_view() -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_owner("main", "m-alpha")
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

#[test]
fn replication_request_names_an_explicit_acknowledgement_level() {
    // The default is `Local` (ADR-0004 D-2: V1 parity — local save,
    // replication asynchronous).
    assert_eq!(AcknowledgementLevel::default(), AcknowledgementLevel::Local);

    let config = ReplicationConfig::try_new(single_region_view()).expect("valid topology");
    assert_eq!(config.acknowledgement(), AcknowledgementLevel::Local);

    // Every stronger level exists as an explicit, distinct variant, and the
    // only way a request carries one is by naming it: the acknowledgement is
    // a per-request input, never inferred from a local durability fact.
    let levels = [
        AcknowledgementLevel::Local,
        AcknowledgementLevel::RegionQuorum,
        AcknowledgementLevel::MajorityRegions,
        AcknowledgementLevel::AllRegions,
    ];
    for (index, level) in levels.iter().enumerate() {
        for other in levels.iter().skip(index + 1) {
            assert_ne!(level, other, "each acknowledgement level is a distinct request");
        }
    }
    for level in &levels[1..] {
        let named = config.clone().with_acknowledgement(*level);
        assert_eq!(named.acknowledgement(), *level);
        assert_ne!(named.acknowledgement(), config.acknowledgement());
    }

    // Naming a level is a value transformation: the original configuration
    // keeps its own explicit level.
    assert_eq!(config.acknowledgement(), AcknowledgementLevel::Local);
}

#[test]
fn replication_status_reports_health_separately_from_convergence() {
    let statuses = [
        ReplicationStatus::Assigned,
        ReplicationStatus::Active,
        ReplicationStatus::Responsive,
        ReplicationStatus::Stale,
        ReplicationStatus::Unavailable,
        ReplicationStatus::Degraded,
        ReplicationStatus::Unknown,
    ];
    for (index, status) in statuses.iter().enumerate() {
        for other in statuses.iter().skip(index + 1) {
            assert_ne!(status, other, "each health observation is a distinct state");
        }
        // A status value is a pure observation: it carries no payload, so no
        // convergence, acknowledgement, member set, or receipt fact can ride
        // on it.
        let rendered = format!("{status:?}");
        assert!(
            !rendered.to_ascii_lowercase().contains("convergen"),
            "a health observation names no convergence fact: {rendered}"
        );
        assert!(
            !rendered.to_ascii_lowercase().contains("acknowledg"),
            "a health observation names no acknowledgement fact: {rendered}"
        );
    }
    assert_eq!(
        std::mem::size_of::<ReplicationStatus>(),
        1,
        "the health vocabulary stays a payload-free observation"
    );

    // Convergence is its own observation type, never inferred from an
    // acknowledgement or a health status, and R-1 implements no content for
    // it (that is R-2's phase).
    assert_ne!(
        std::any::type_name::<ReplicationStatus>(),
        std::any::type_name::<Convergence>(),
        "health and convergence are separate types"
    );
    assert_eq!(
        std::mem::size_of::<Convergence>(),
        0,
        "R-1 adds no convergence content; the type stays reserved for R-2"
    );
}

#[test]
fn region_and_member_identifiers_validate_as_logical_identifiers() {
    for valid in ["r-alpha", "m-alpha-1", "r_gamma.2"] {
        assert_eq!(RegionId::try_new(valid).map(|id| id.as_str().to_owned()), Ok(valid.to_owned()));
        assert_eq!(MemberId::try_new(valid).map(|id| id.as_str().to_owned()), Ok(valid.to_owned()));
    }
    for invalid in ["", " leading", "trailing ", "control\u{7}char", &"x".repeat(256)] {
        assert!(RegionId::try_new(invalid).is_err(), "region text must be rejected: {invalid:?}");
        assert!(MemberId::try_new(invalid).is_err(), "member text must be rejected: {invalid:?}");
    }
    let boundary = "r".repeat(255);
    assert!(RegionId::try_new(&boundary).is_ok(), "255 bytes is the accepted boundary");

    // `Display` renders the canonical identifier and nothing else.
    let region = RegionId::try_new("r-alpha").expect("valid region");
    let member = MemberId::try_new("m-alpha").expect("valid member");
    assert_eq!(region.to_string(), "r-alpha");
    assert_eq!(member.to_string(), "m-alpha");
}

#[test]
fn topology_views_validate_membership_ownership_and_failure_domains() {
    // The D-1 rule the builder enforces: exactly one Member owns each data
    // range's write path, every Member is seated in one Region, and a
    // failure domain groups Members whose correlated loss is one failure.
    let view = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-alpha-2", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-beta-1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner("main", "m-alpha-1")
        .expect("valid owner")
        .with_owner("archive", "m-beta-1")
        .expect("valid owner")
        .validated()
        .expect("valid topology");

    assert_eq!(view.owner_of("main").map(|id| id.as_str().to_owned()), Some("m-alpha-1".to_owned()));
    assert_eq!(view.owner_of("archive").map(|id| id.as_str().to_owned()), Some("m-beta-1".to_owned()));
    assert_eq!(
        view.region_of(&MemberId::try_new("m-alpha-2").expect("valid member"))
            .map(|id| id.as_str().to_owned()),
        Some("r-alpha".to_owned())
    );
    assert_eq!(view.failure_domain_of(&MemberId::try_new("m-alpha-1").expect("valid member")), Some("fd-alpha"));
    assert!(view.contains_region(&RegionId::try_new("r-beta").expect("valid region")));
    assert!(!view.contains_region(&RegionId::try_new("r-gamma").expect("valid region")));
    assert!(view.contains_member(&MemberId::try_new("m-beta-1").expect("valid member")));
    assert!(!view.is_empty());

    // The view is an immutable snapshot: builders are value transformations.
    let before = view.clone();
    let _ = view.owner_of("main");
    assert_eq!(view, before);

    // Typed rejections, all `InvalidTopology`.
    let empty = TopologyView::new().validated();
    assert_eq!(empty, Err(ReplicationError::InvalidTopology));

    let unknown_region = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-ghost", "r-gamma", "fd-gamma");
    assert_eq!(unknown_region, Err(ReplicationError::InvalidTopology));

    let duplicate_member = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-alpha-1", "r-alpha", "fd-other");
    assert_eq!(duplicate_member, Err(ReplicationError::InvalidTopology));

    let spanning_domain = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-shared")
        .expect("valid member")
        .with_member("m-beta-1", "r-beta", "fd-shared");
    assert_eq!(
        spanning_domain,
        Err(ReplicationError::InvalidTopology),
        "a failure domain cannot span Regions: each Region is its own failure domain"
    );

    let re_owned_range = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-alpha-2", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_owner("main", "m-alpha-1")
        .expect("valid owner")
        .with_owner("main", "m-alpha-2");
    assert_eq!(
        re_owned_range,
        Err(ReplicationError::InvalidTopology),
        "one Member owns each data range's write path (D-1)"
    );

    let unknown_owner = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_owner("main", "m-ghost");
    assert_eq!(unknown_owner, Err(ReplicationError::InvalidTopology));

    for invalid_text in ["", " leading", "control\u{7}", &"r".repeat(256)] {
        assert_eq!(
            TopologyView::new().with_region(invalid_text),
            Err(ReplicationError::InvalidTopology)
        );
    }

    // A Region with no Member is not a servable membership view.
    let memberless = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha-1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .validated();
    assert_eq!(
        memberless,
        Err(ReplicationError::InvalidTopology),
        "every Region in the view seats at least one Member"
    );
}

#[test]
fn replication_config_validates_its_topology_and_policy_bounds() {
    let view = single_region_view();
    let config = ReplicationConfig::try_new(view.clone()).expect("valid topology");
    assert_eq!(config.topology(), &view);
    assert_eq!(config.acknowledgement(), AcknowledgementLevel::Local);

    // An empty, never-validated topology view is rejected with the typed
    // category: the configuration does not authorize or provision around it.
    assert_eq!(
        ReplicationConfig::try_new(TopologyView::new()),
        Err(ReplicationError::InvalidTopology)
    );

    // The configuration carries no credential, path, or routing fact: its
    // rendered form names only topology identities and the policy level.
    let rendered = format!("{config:?}");
    assert!(rendered.contains("Local"));
    assert!(
        !rendered.contains('/') && !rendered.contains("credential") && !rendered.contains("connec"),
        "no path, credential, or connection fact crosses the configuration: {rendered}"
    );
}

#[test]
fn replication_cursor_starts_empty_and_reports_only_progress_facts() {
    let member = MemberId::try_new("m-alpha").expect("valid member");
    let cursor = ReplicationCursor::start(member.clone());
    assert_eq!(cursor.member(), &member);
    assert_eq!(cursor.high_water(), None);
    assert_eq!(
        cursor.tenant_progress(&terrabase_v2::types::TenantId::try_new("tenant-a").expect("valid tenant")),
        None
    );
    assert!(cursor.tenants().next().is_none());
    assert!(cursor.is_empty());

    // The cursor carries identities and storage sequences only: no document,
    // payload, digest, path, or transport detail.
    let rendered = format!("{cursor:?}");
    for forbidden in ["doc-", "payload", "digest", "wal", "frame", "/tmp"] {
        assert!(
            !rendered.to_ascii_lowercase().contains(forbidden),
            "the cursor carries a forbidden fact ({forbidden}): {rendered}"
        );
    }
}
