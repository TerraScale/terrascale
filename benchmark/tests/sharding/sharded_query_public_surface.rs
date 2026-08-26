//! External-caller construction of the SH-04 sharded query surface's
//! externally reachable types (warden MINOR at SH-04 integration,
//! 2026-08-26).
//!
//! Visibility finding recorded at integration: `ShardedIndexDefinition`
//! takes crate-private `IndexNamespace`/`IndexDefinition`, and
//! `ShardSnapshots` takes a `ReadSnapshot` obtainable only through a
//! `TenantScope` the engine seam keeps crate-private (tenant context is
//! backend-supplied per PD-2026-08-26-11). Those types are therefore
//! in-process surfaces by design — the external surface is TBEP (SE-14),
//! whose operation registry decides what becomes wire-visible. This test
//! pins what an external caller CAN construct today: the member-serving
//! endpoint pairing and its refusal shapes.

use terrabase_v2::MemberId;
use terrabase_v2::engine::{Engine, EngineConfig};
use terrabase_v2::sharding::{MemberServing, ShardServingError, ShardedExecutionError};

fn open_engine(tag: &str) -> Engine {
    let dir =
        std::env::temp_dir().join(format!("tbv2-sh04-public-surface-{}-{tag}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir).expect("durable location");
    Engine::open(config).expect("engine opens")
}

#[test]
fn member_serving_seats_members_and_refuses_duplicates() {
    let engine = open_engine("member-serving");
    let member = MemberId::try_new("m-alpha").expect("valid member");
    let duplicate =
        MemberServing::try_new(vec![(member.clone(), &engine), (member.clone(), &engine)])
            .err()
            .expect("duplicate member refused");
    match duplicate {
        ShardedExecutionError::DuplicateMember { member: refused } => {
            assert_eq!(refused, MemberId::try_new("m-alpha").expect("valid member"));
        }
        _ => panic!("expected DuplicateMember"),
    }

    let serving =
        MemberServing::try_new(vec![(member.clone(), &engine)]).expect("single member seats");
    assert!(serving.engine_of(&member).is_ok(), "the seated member resolves");
}

#[test]
fn member_serving_names_the_unserved_owner() {
    let engine = open_engine("unserved-owner");
    let member = MemberId::try_new("m-alpha").expect("valid member");
    let serving = MemberServing::try_new(vec![(member, &engine)]).expect("single member seats");
    let unserved = serving
        .engine_of(&MemberId::try_new("m-beta").expect("other member"))
        .err()
        .expect("unserved owner refused");
    match unserved {
        ShardServingError::MemberUnserved { owner } => {
            assert_eq!(owner, MemberId::try_new("m-beta").expect("other member"));
        }
        _ => panic!("expected MemberUnserved"),
    }
}
