#![allow(missing_docs)]

use terrabase_causal::{ActorId, CausalContext, DottedVersion, HlcTimestamp, MutationOrdinal, OriginCounter, ReplicaEpoch, ReplicaId, Dot};
use terrabase_crdt::{Candidate, LwwRegister, MvRegister};

fn actor(byte: u8) -> ActorId {
    ActorId::new(ReplicaId::from_bytes([byte; 16]), ReplicaEpoch::new(1).unwrap())
}

fn candidate(byte: u8, counter: u64, context: CausalContext, hlc: u64, value: Option<&str>) -> Candidate<String> {
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
    let right = LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("right")));
    assert_eq!(left.join(&right), right.join(&left));
    assert_eq!(left.join(&right).materialize(), Some(&"right".to_owned()));
}

#[test]
fn lww_join_context_prevents_a_discarded_candidate_from_resurfacing() {
    let winner = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 20, Some("winner")));
    let loser = LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("loser")));
    let merged = winner.join(&loser);
    assert_eq!(merged.materialize(), Some(&"winner".to_owned()));
    assert!(merged.context().contains(Dot::new(actor(2), OriginCounter::new(1).unwrap())));

    let replay = LwwRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("loser")));
    assert_eq!(merged.join(&replay).materialize(), Some(&"winner".to_owned()));
}

#[test]
fn mv_retains_concurrent_candidates_and_sorts_materialization() {
    let left = MvRegister::from_candidate(candidate(1, 1, CausalContext::new(), 20, Some("left")));
    let right = MvRegister::from_candidate(candidate(2, 1, CausalContext::new(), 10, Some("right")));
    let merged = left.join(&right);
    assert_eq!(merged, right.join(&left));
    assert_eq!(merged.materialize(), vec![(merged.candidates()[0].identity(), "right".to_owned()), (merged.candidates()[1].identity(), "left".to_owned())]);
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
