#![allow(missing_docs)]

use terrabase_causal::{
    ActorId, CanonicalEncode, CausalContext, Dot, DottedVersion, HlcTimestamp, MutationOrdinal,
    OriginCounter, ReplicaEpoch, ReplicaId,
};
use terrabase_crdt::{
    Candidate, DecodeError, DecodeLimits, LwwRegister, MvRegister, RegisterCodec,
};

fn actor(byte: u8) -> ActorId {
    ActorId::new(ReplicaId::from_bytes([byte; 16]), ReplicaEpoch::new(1).unwrap())
}

fn candidate(
    byte: u8,
    counter: u64,
    context: CausalContext,
    hlc: u64,
    value: Option<&str>,
) -> Candidate<String> {
    let dot = Dot::new(actor(byte), OriginCounter::new(counter).unwrap());
    Candidate::new(
        value.map(str::to_owned),
        DottedVersion::new(context, dot),
        MutationOrdinal::new(0),
        HlcTimestamp::new(hlc, 0),
    )
}

fn context_of(candidate: &Candidate<String>) -> CausalContext {
    let mut context = candidate.version().context().clone();
    context.insert(candidate.dot());
    context
}

#[test]
fn causal_dominance_precedes_the_concurrent_hlc_order() {
    let first = candidate(1, 1, CausalContext::new(), 900, Some("first"));
    let observed = context_of(&first);
    let later = candidate(2, 1, observed, 1, Some("later"));

    let merged = LwwRegister::from_candidate(first).join(&LwwRegister::from_candidate(later));
    assert_eq!(merged.materialize(), Some(&"later".to_owned()));
    assert_eq!(merged.winner().unwrap().dot().actor(), actor(2));
}

#[test]
fn concurrent_lww_candidates_use_the_full_deterministic_key() {
    let left = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 10, Some("left")));
    let right =
        LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("right")));
    assert_eq!(left.join(&right), right.join(&left));
    assert_eq!(left.join(&right).materialize(), Some(&"right".to_owned()));
}

#[test]
fn lww_join_context_prevents_a_discarded_candidate_from_resurfacing() {
    let winner =
        LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 20, Some("winner")));
    let loser =
        LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("loser")));
    let merged = winner.join(&loser);
    assert_eq!(merged.materialize(), Some(&"winner".to_owned()));
    assert!(merged.context().contains(Dot::new(actor(2), OriginCounter::new(1).unwrap())));

    let replay =
        LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("loser")));
    assert_eq!(merged.join(&replay).materialize(), Some(&"winner".to_owned()));
}

#[test]
fn mv_retains_concurrent_candidates_and_sorts_materialization() {
    let left = MvRegister::from_candidate(candidate(1, 1, CausalContext::new(), 20, Some("left")));
    let right =
        MvRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("right")));
    let merged = left.join(&right);
    assert_eq!(merged, right.join(&left));
    assert_eq!(
        merged.materialize(),
        vec![
            (merged.candidates()[0].identity(), "right".to_owned()),
            (merged.candidates()[1].identity(), "left".to_owned())
        ]
    );
    assert_eq!(merged.candidates().len(), 2);
}

#[test]
fn mv_removes_only_causally_dominated_candidates() {
    let first = candidate(1, 1, CausalContext::new(), 100, Some("first"));
    let context = context_of(&first);
    let later = candidate(2, 1, context, 1, Some("later"));
    let merged = MvRegister::from_candidate(first).join(&MvRegister::from_candidate(later));
    assert_eq!(merged.materialize().len(), 1);
    assert_eq!(merged.materialize()[0].1, "later");
}

#[test]
fn all_join_shapes_are_equal_for_the_same_candidate_set() {
    let a = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 1, Some("a")));
    let b = LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 2, Some("b")));
    let c = LwwRegister::from_candidate(candidate(3, 1, CausalContext::new(), 3, Some("c")));
    let left = a.join(&b).join(&c);
    let right = a.join(&b.join(&c));
    assert_eq!(left, right);
    assert_eq!(left.join(&left), left);
}

#[test]
fn mv_join_is_commutative_associative_and_idempotent_for_all_orders() {
    let states = [
        MvRegister::from_candidate(candidate(1, 1, CausalContext::new(), 1, Some("a"))),
        MvRegister::from_candidate(candidate(2, 1, CausalContext::new(), 2, Some("b"))),
        MvRegister::from_candidate(candidate(3, 1, CausalContext::new(), 3, None)),
    ];
    let baseline = states[0].join(&states[1]).join(&states[2]);
    for order in [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]] {
        let merged = states[order[0]].join(&states[order[1]]).join(&states[order[2]]);
        assert_eq!(merged, baseline);
        assert_eq!(merged.join(&merged), merged);
    }
    assert_eq!(states[0].join(&states[1]), states[1].join(&states[0]));
}

#[test]
fn removal_is_a_candidate_and_can_win_by_the_concurrent_key() {
    let value =
        LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 10, Some("live")));
    let removal = LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 11, None));
    let merged = value.join(&removal);
    assert!(merged.winner().unwrap().is_remove());
    assert_eq!(merged.materialize(), None);
}

#[test]
fn register_decoder_rejects_candidate_that_observes_its_own_dot() {
    let dot = Dot::new(actor(7), OriginCounter::new(1).unwrap());
    let mut context = CausalContext::new();
    context.insert(dot);
    let malformed = LwwRegister::from_candidate(Candidate::new(
        Some("value".to_owned()),
        DottedVersion::new(context, dot),
        MutationOrdinal::new(0),
        HlcTimestamp::new(1, 0),
    ));
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&malformed.canonical_bytes(), DecodeLimits::default()),
        Err(DecodeError::NonCanonicalContext)
    );
}

#[test]
fn lww_join_is_commutative_for_equivocating_equal_identities() {
    let left = LwwRegister::from_candidate(candidate(9, 1, CausalContext::new(), 7, Some("left")));
    let right =
        LwwRegister::from_candidate(candidate(9, 1, CausalContext::new(), 7, Some("right")));
    let forward = left.join(&right);
    let reverse = right.join(&left);
    assert_eq!(forward, reverse);
    assert_eq!(forward.materialize(), reverse.materialize());
}

#[test]
fn mv_join_is_commutative_for_equivocating_equal_identities() {
    let left = MvRegister::from_candidate(candidate(9, 1, CausalContext::new(), 7, Some("left")));
    let right = MvRegister::from_candidate(candidate(9, 1, CausalContext::new(), 7, Some("right")));
    assert_eq!(left.join(&right), right.join(&left));
    assert_eq!(left.join(&right).materialize().len(), 1);
}

#[test]
fn equal_identity_removal_and_value_have_a_stable_winner() {
    let live = LwwRegister::from_candidate(candidate(4, 8, CausalContext::new(), 2, Some("live")));
    let removed = LwwRegister::from_candidate(candidate(4, 8, CausalContext::new(), 2, None));
    assert_eq!(live.join(&removed), removed.join(&live));
    assert_eq!(live.join(&removed).materialize(), Some(&"live".to_owned()));
}

#[test]
fn equal_identity_and_value_with_different_contexts_still_join_commutatively() {
    let dot = Dot::new(actor(5), OriginCounter::new(2).unwrap());
    let mut observed = CausalContext::new();
    observed.insert(Dot::new(actor(1), OriginCounter::new(1).unwrap()));
    let left = LwwRegister::from_candidate(Candidate::new(
        Some("same".to_owned()),
        DottedVersion::new(CausalContext::new(), dot),
        MutationOrdinal::new(0),
        HlcTimestamp::new(3, 0),
    ));
    let right = LwwRegister::from_candidate(Candidate::new(
        Some("same".to_owned()),
        DottedVersion::new(observed, dot),
        MutationOrdinal::new(0),
        HlcTimestamp::new(3, 0),
    ));
    assert_eq!(left.join(&right), right.join(&left));
}

#[test]
fn canonical_scalar_values_round_trip_at_boundaries() {
    for value in [false, true] {
        let register = LwwRegister::from_candidate(Candidate::new(
            Some(value),
            DottedVersion::new(
                CausalContext::new(),
                Dot::new(actor(1), OriginCounter::new(1).unwrap()),
            ),
            MutationOrdinal::new(0),
            HlcTimestamp::new(1, 0),
        ));
        let bytes = register.canonical_bytes();
        assert_eq!(
            RegisterCodec::decode_lww::<bool>(&bytes, DecodeLimits::default()).unwrap(),
            register
        );
    }
    for value in [0_u64, 1, u64::MAX] {
        let register = LwwRegister::from_candidate(Candidate::new(
            Some(value),
            DottedVersion::new(
                CausalContext::new(),
                Dot::new(actor(2), OriginCounter::new(1).unwrap()),
            ),
            MutationOrdinal::new(0),
            HlcTimestamp::new(1, 0),
        ));
        let bytes = register.canonical_bytes();
        assert_eq!(
            RegisterCodec::decode_lww::<u64>(&bytes, DecodeLimits::default()).unwrap(),
            register
        );
    }
    for value in [i64::MIN, -1, 0, 1, i64::MAX] {
        let register = LwwRegister::from_candidate(Candidate::new(
            Some(value),
            DottedVersion::new(
                CausalContext::new(),
                Dot::new(actor(3), OriginCounter::new(1).unwrap()),
            ),
            MutationOrdinal::new(0),
            HlcTimestamp::new(1, 0),
        ));
        let bytes = register.canonical_bytes();
        assert_eq!(
            RegisterCodec::decode_lww::<i64>(&bytes, DecodeLimits::default()).unwrap(),
            register
        );
    }
}

#[test]
fn decoder_rejects_trailing_bytes_and_wrong_datatypes() {
    let register = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 1, Some("x")));
    let mut trailing = register.canonical_bytes();
    trailing.push(0);
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&trailing, DecodeLimits::default()),
        Err(DecodeError::TrailingBytes)
    );
    assert_eq!(
        RegisterCodec::decode_mv::<String>(&register.canonical_bytes(), DecodeLimits::default()),
        Err(DecodeError::WrongDatatype)
    );
    assert_eq!(
        RegisterCodec::decode_lww::<u64>(&register.canonical_bytes(), DecodeLimits::default()),
        Err(DecodeError::WrongDatatype)
    );
}

#[test]
fn decoder_limits_are_checked_before_value_allocation() {
    let value = "x".repeat(64);
    let register =
        LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 1, Some(&value)));
    let bytes = register.canonical_bytes();
    let limits = DecodeLimits { max_bytes: bytes.len() - 1, ..DecodeLimits::default() };
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&bytes, limits),
        Err(DecodeError::LimitExceeded)
    );
    let limits = DecodeLimits { max_value_bytes: value.len() - 1, ..DecodeLimits::default() };
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&bytes, limits),
        Err(DecodeError::LimitExceeded)
    );
}
