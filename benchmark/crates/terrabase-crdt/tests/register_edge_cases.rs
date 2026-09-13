#![allow(missing_docs)]

use terrabase_causal::{
    ActorId, CanonicalEncode, CausalContext, Dot, DottedVersion, HlcTimestamp, MutationOrdinal,
    OriginCounter, ReplicaEpoch, ReplicaId,
};
use terrabase_crdt::{
    Candidate, DecodeError, DecodeLimits, LwwRegister, MvRegister, RegisterCodec,
};

fn assignment(
    actor: u8,
    context: CausalContext,
    clock: u64,
    value: Option<&str>,
) -> Candidate<String> {
    Candidate::new(
        value.map(str::to_owned),
        DottedVersion::new(
            context,
            Dot::new(
                ActorId::new(ReplicaId::from_bytes([actor; 16]), ReplicaEpoch::new(1).unwrap()),
                OriginCounter::new(1).unwrap(),
            ),
        ),
        MutationOrdinal::new(0),
        HlcTimestamp::new(clock, 0),
    )
}

fn mixed_history() -> [Candidate<String>; 3] {
    let a = assignment(1, CausalContext::new(), 30, Some("a"));
    let b = assignment(2, CausalContext::new(), 20, Some("b"));
    let mut context = CausalContext::new();
    context.insert(a.dot());
    let c = assignment(3, context, 10, Some("c"));
    [a, b, c]
}

const ORDERS: [[usize; 3]; 6] = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

#[test]
fn lww_mixed_causal_history_converges_for_every_order_and_grouping() {
    let states = mixed_history().map(LwwRegister::from_candidate);
    let expected = states[0].join(&states[1].join(&states[2]));
    assert_eq!(expected.materialize().map(String::as_str), Some("b"));
    for [a, b, c] in ORDERS {
        assert_eq!(states[a].join(&states[b]).join(&states[c]), expected, "order {a}{b}{c}");
        assert_eq!(states[a].join(&states[b].join(&states[c])), expected);
    }
}

#[test]
fn mv_mixed_causal_history_converges_for_every_order_and_grouping() {
    let states = mixed_history().map(MvRegister::from_candidate);
    let expected = states[0].join(&states[1].join(&states[2]));
    assert_eq!(
        expected.materialize().iter().map(|(_, v)| v.as_str()).collect::<Vec<_>>(),
        ["c", "b"]
    );
    for [a, b, c] in ORDERS {
        assert_eq!(states[a].join(&states[b]).join(&states[c]), expected);
        assert_eq!(states[a].join(&states[b].join(&states[c])), expected);
    }
}

#[test]
fn lww_serialization_retains_evidence_needed_by_a_later_causal_write() {
    let [a, b, c] = mixed_history().map(LwwRegister::from_candidate);
    let before = a.join(&b);
    let restored =
        RegisterCodec::decode_lww::<String>(&before.canonical_bytes(), DecodeLimits::default())
            .unwrap();
    assert_eq!(restored.join(&c), a.join(&b.join(&c)));
}

#[test]
fn lww_removal_of_a_concurrent_winner_preserves_the_other_branch() {
    let [a, b, c] = mixed_history();
    let removal = Candidate::new(None, c.version().clone(), c.mutation_ordinal(), c.hlc());
    let (a, b, c) = (
        LwwRegister::from_candidate(a),
        LwwRegister::from_candidate(b),
        LwwRegister::from_candidate(removal),
    );
    assert_eq!(a.join(&b).join(&c), a.join(&b.join(&c)));
    assert_eq!(a.join(&b).join(&c).materialize().map(String::as_str), Some("b"));
}

#[test]
fn both_registers_have_empty_identity_on_each_side() {
    for value in [None, Some(""), Some("\0雪😀")] {
        let c = assignment(1, CausalContext::new(), 0, value);
        let lww = LwwRegister::from_candidate(c.clone());
        let mv = MvRegister::from_candidate(c);
        assert_eq!(lww.join(&LwwRegister::empty()), lww);
        assert_eq!(LwwRegister::empty().join(&lww), lww);
        assert_eq!(mv.join(&MvRegister::empty()), mv);
        assert_eq!(MvRegister::empty().join(&mv), mv);
    }
}

#[test]
fn an_observed_removal_cannot_be_undone_by_replaying_old_values() {
    let value = assignment(1, CausalContext::new(), u64::MAX, Some("old"));
    let mut context = CausalContext::new();
    context.insert(value.dot());
    let remove = assignment(2, context, 0, None);
    let lww = LwwRegister::from_candidate(value.clone());
    let mv = MvRegister::from_candidate(value);
    let removed_lww = lww.join(&LwwRegister::from_candidate(remove.clone()));
    let removed_mv = mv.join(&MvRegister::from_candidate(remove));
    assert!(removed_lww.materialize().is_none());
    assert!(removed_mv.materialize().is_empty());
    assert_eq!(removed_lww.join(&lww), removed_lww);
    assert_eq!(removed_mv.join(&mv), removed_mv);
}

#[test]
fn every_truncated_register_prefix_is_rejected() {
    let c = assignment(1, CausalContext::new(), u64::MAX, Some("Unicode 雪😀"));
    let lww = LwwRegister::from_candidate(c.clone()).canonical_bytes();
    let mv = MvRegister::from_candidate(c).canonical_bytes();
    for end in 0..lww.len() {
        assert!(
            RegisterCodec::decode_lww::<String>(&lww[..end], DecodeLimits::default()).is_err(),
            "LWW prefix {end}"
        );
        assert!(
            RegisterCodec::decode_mv::<String>(&mv[..end], DecodeLimits::default()).is_err(),
            "MV prefix {end}"
        );
    }
}

#[test]
fn decoder_accepts_exact_byte_and_utf8_value_budgets() {
    for value in ["", "\0", "雪", "😀", "a\n\r\t雪😀"] {
        let state =
            LwwRegister::from_candidate(assignment(1, CausalContext::new(), 0, Some(value)));
        let bytes = state.canonical_bytes();
        let limits = DecodeLimits {
            max_bytes: bytes.len(),
            max_value_bytes: value.len(),
            ..DecodeLimits::default()
        };
        assert_eq!(RegisterCodec::decode_lww::<String>(&bytes, limits).unwrap(), state);
        if !value.is_empty() {
            assert_eq!(
                RegisterCodec::decode_lww::<String>(
                    &bytes,
                    DecodeLimits { max_value_bytes: value.len() - 1, ..limits }
                ),
                Err(DecodeError::LimitExceeded)
            );
        }
    }
}

#[test]
fn decoder_rejects_invalid_utf8_without_replacing_bytes() {
    let state = LwwRegister::from_candidate(assignment(1, CausalContext::new(), 0, Some("x")));
    for byte in [0x80, 0xbf, 0xc0, 0xc1, 0xf5, 0xff] {
        let mut bytes = state.canonical_bytes();
        *bytes.last_mut().unwrap() = byte;
        assert_eq!(
            RegisterCodec::decode_lww::<String>(&bytes, DecodeLimits::default()),
            Err(DecodeError::InvalidUtf8)
        );
    }
}

#[test]
fn decoder_candidate_limit_is_exact_for_concurrent_values_and_removals() {
    let state = MvRegister::from_candidate(assignment(1, CausalContext::new(), 0, None))
        .join(&MvRegister::from_candidate(assignment(2, CausalContext::new(), 1, Some("x"))));
    let bytes = state.canonical_bytes();
    assert_eq!(
        RegisterCodec::decode_mv::<String>(
            &bytes,
            DecodeLimits { max_candidates: 2, ..DecodeLimits::default() }
        )
        .unwrap(),
        state
    );
    for max_candidates in [0, 1] {
        assert_eq!(
            RegisterCodec::decode_mv::<String>(
                &bytes,
                DecodeLimits { max_candidates, ..DecodeLimits::default() }
            ),
            Err(DecodeError::LimitExceeded)
        );
    }
}

#[test]
fn decoder_context_actor_limit_is_exact() {
    let [a, b, _] = mixed_history().map(MvRegister::from_candidate);
    let state = a.join(&b);
    let bytes = state.canonical_bytes();
    assert_eq!(
        RegisterCodec::decode_mv::<String>(
            &bytes,
            DecodeLimits { max_context_actors: 2, ..DecodeLimits::default() }
        )
        .unwrap(),
        state
    );
    assert_eq!(
        RegisterCodec::decode_mv::<String>(
            &bytes,
            DecodeLimits { max_context_actors: 1, ..DecodeLimits::default() }
        ),
        Err(DecodeError::LimitExceeded)
    );
}

#[test]
fn empty_registers_decode_with_zero_candidate_and_context_budgets() {
    let limits = DecodeLimits {
        max_candidates: 0,
        max_context_actors: 0,
        max_intervals_per_actor: 0,
        max_value_bytes: 0,
        ..DecodeLimits::default()
    };
    assert_eq!(
        RegisterCodec::decode_lww::<String>(
            &LwwRegister::<String>::empty().canonical_bytes(),
            limits
        )
        .unwrap(),
        LwwRegister::empty()
    );
    assert_eq!(
        RegisterCodec::decode_mv::<String>(
            &MvRegister::<String>::empty().canonical_bytes(),
            limits
        )
        .unwrap(),
        MvRegister::empty()
    );
}

#[test]
fn unsupported_versions_are_rejected_for_every_reserved_byte() {
    let state = LwwRegister::<String>::empty();
    for version in (0..=u8::MAX).filter(|v| *v != 1) {
        let mut bytes = state.canonical_bytes();
        bytes[0] = version;
        assert_eq!(
            RegisterCodec::decode_lww::<String>(&bytes, DecodeLimits::default()),
            Err(DecodeError::UnsupportedVersion)
        );
    }
}

#[test]
fn every_nonempty_suffix_is_rejected_even_on_an_empty_register() {
    for byte in 0..=u8::MAX {
        let mut bytes = LwwRegister::<String>::empty().canonical_bytes();
        bytes.push(byte);
        assert_eq!(
            RegisterCodec::decode_lww::<String>(&bytes, DecodeLimits::default()),
            Err(DecodeError::TrailingBytes)
        );
    }
}
