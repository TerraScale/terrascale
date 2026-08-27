//! Public-path tests for the document metadata and change-vector contract
//! (GAP-D02, ticket #59).
//!
//! These tests use only public construction: the version-contract types,
//! the HEAD condition and outcome vocabulary, the conditional-load error
//! vocabulary, and the recorded V1 change-vector mapping. `TenantScope` has
//! no public constructor by design (Platform creates it after authorization),
//! so the commit-path behavior is proved by the in-crate engine tests
//! instead (`src/engine/metadata_tests.rs`).

use terrabase_v2::engine::{
    ConditionalLoadError, DocumentHead, DocumentVersionContract, DocumentVersionContractError,
    HeadCondition, V1ChangeVector, V1ChangeVectorError, V1ChangeVectorMapping,
};
use terrabase_v2::replication::ReplicationClockStamp;

/// A 22-character unpadded base64 database identity, V1's exact shape.
const DB_ID_A: &str = "AAAAAAAAAAAAAAAAAAAAAA";
const DB_ID_B: &str = "BBBBBBBBBBBBBBBBBBBBBB";

fn contract(version: u64, sequence: u64) -> DocumentVersionContract {
    DocumentVersionContract::try_new(version, sequence, ReplicationClockStamp::new(0, 0))
        .expect("valid contract facts")
}

#[test]
fn version_contract_reports_its_three_components() {
    let stamp = ReplicationClockStamp::new(7, 3);
    let contract = DocumentVersionContract::try_new(2, 41, stamp).expect("valid facts");
    assert_eq!(contract.version(), 2);
    assert_eq!(contract.sequence().get(), 41);
    assert_eq!(contract.stamp(), stamp);
}

#[test]
fn version_contract_rejects_impossible_facts() {
    assert_eq!(
        DocumentVersionContract::try_new(0, 1, ReplicationClockStamp::new(0, 0)).err(),
        Some(DocumentVersionContractError::ZeroVersion),
        "no publication ever assigns version zero"
    );
    assert_eq!(
        DocumentVersionContract::try_new(1, 0, ReplicationClockStamp::new(0, 0)).err(),
        Some(DocumentVersionContractError::ZeroSequence),
        "committed publication sequences start at one"
    );
}

#[test]
fn head_outcome_carries_the_contract_where_one_exists() {
    let current = contract(1, 3);
    assert_eq!(DocumentHead::Missing.contract(), None);
    assert_eq!(
        DocumentHead::Unchanged(current).contract(),
        Some(&current),
        "the unchanged answer is self-describing, like V1's 304 with its ETag"
    );
    assert_eq!(DocumentHead::Current(current).contract(), Some(&current));
}

#[test]
fn head_condition_any_and_if_unchanged_are_distinct() {
    let baseline = contract(1, 3);
    assert_eq!(HeadCondition::Any, HeadCondition::Any);
    assert_ne!(HeadCondition::Any, HeadCondition::IfUnchanged(baseline));
    assert_eq!(HeadCondition::IfUnchanged(baseline), HeadCondition::IfUnchanged(baseline));
}

#[test]
fn conditional_load_error_vocabulary_is_typed() {
    let current = contract(2, 9);
    assert_eq!(ConditionalLoadError::Missing, ConditionalLoadError::Missing);
    assert_eq!(
        ConditionalLoadError::Stale { current },
        ConditionalLoadError::Stale { current },
        "the stale rejection carries the current contract"
    );
    let engine = ConditionalLoadError::Engine(terrabase_v2::EngineError::StorageFailure);
    assert_eq!(engine, ConditionalLoadError::Engine(terrabase_v2::EngineError::StorageFailure));
}

#[test]
fn v1_change_vector_parses_the_v1_grammar() {
    let text = format!("A:5-{DB_ID_A}, B:12-{DB_ID_B}");
    let vector = V1ChangeVector::parse(&text).expect("V1 serializes vectors in this shape");
    assert_eq!(vector.entries().len(), 2);
    assert_eq!(vector.entries()[0].node_tag(), 0, "the V1 base-26 fold maps 'A' to zero");
    assert_eq!(vector.entries()[0].etag(), 5);
    assert_eq!(vector.entries()[0].db_id(), DB_ID_A);
    assert_eq!(vector.entries()[1].node_tag(), 1, "and 'B' to one");

    // V1 skips any run of commas and whitespace between entries.
    let loose = format!("A:5-{DB_ID_A},\n  B:12-{DB_ID_B}");
    assert_eq!(V1ChangeVector::parse(&loose).expect("parses"), vector);
}

#[test]
fn v1_change_vector_dominant_entry_follows_the_v1_etag_order() {
    let vector = V1ChangeVector::parse(&format!("A:5-{DB_ID_A}, B:12-{DB_ID_B}")).expect("parses");
    let dominant = vector.dominant().expect("a nonempty vector has one");
    assert_eq!(dominant.etag(), 12, "the greatest etag dominates, as V1 orders vectors");
    assert_eq!(dominant.db_id(), DB_ID_B);
    assert!(V1ChangeVector::parse("").expect("empty parses to no entries").entries().is_empty());
    assert!(V1ChangeVector::parse("").expect("empty parses to no entries").dominant().is_none());
}

#[test]
fn v1_change_vector_mapping_records_provenance_not_live_identity() {
    let vector = V1ChangeVector::parse(&format!("A:5-{DB_ID_A}, B:12-{DB_ID_B}")).expect("parses");
    let mapping = V1ChangeVectorMapping::from_vector(&vector);

    assert_eq!(mapping.entry_count(), 2, "the arity is disclosed, not the entries' meaning");
    assert_eq!(mapping.dominant().expect("dominant entry recorded").etag(), 12);

    // The provenance digest is canonical over the entry set: serializing the
    // same entries in a different order records the same identity.
    let reordered =
        V1ChangeVector::parse(&format!("B:12-{DB_ID_B}, A:5-{DB_ID_A}")).expect("parses");
    assert_eq!(
        V1ChangeVectorMapping::from_vector(&reordered).provenance_digest(),
        mapping.provenance_digest(),
        "the digest is order-canonical over the parsed entry set"
    );
    // A different vector records a different identity.
    let other = V1ChangeVector::parse(&format!("A:6-{DB_ID_A}, B:12-{DB_ID_B}")).expect("parses");
    assert_ne!(
        V1ChangeVectorMapping::from_vector(&other).provenance_digest(),
        mapping.provenance_digest()
    );

    // The mapping deliberately carries no live V2 change identity: a V1
    // vector never becomes a V2 version, sequence, or stamp.
    let empty = V1ChangeVectorMapping::from_vector(&V1ChangeVector::parse("").expect("parses"));
    assert_eq!(empty.entry_count(), 0);
    assert!(empty.dominant().is_none());
}

#[test]
fn v1_change_vector_parse_fails_closed() {
    assert_eq!(V1ChangeVector::parse("garbage").err(), Some(V1ChangeVectorError::Malformed));
    assert_eq!(
        V1ChangeVector::parse(&format!("a:5-{DB_ID_A}")).err(),
        Some(V1ChangeVectorError::Malformed),
        "node tags are V1's uppercase base-26 letters only"
    );
    assert_eq!(
        V1ChangeVector::parse("A:x-AAAAAAAAAAAAAAAAAAAAAA").err(),
        Some(V1ChangeVectorError::Malformed),
        "etags are decimal digits only"
    );
    assert_eq!(
        V1ChangeVector::parse(&format!("A:5-{}", &DB_ID_A[..21])).err(),
        Some(V1ChangeVectorError::Malformed),
        "a V1 database identity is exactly 22 characters"
    );
    assert_eq!(
        V1ChangeVector::parse("A:18446744073709551616-AAAAAAAAAAAAAAAAAAAAAA").err(),
        Some(V1ChangeVectorError::EtagOverflow),
        "an etag past the u64 range fails closed instead of wrapping"
    );
    assert_eq!(
        V1ChangeVector::parse(&format!("A:5-{DB_ID_A},")).expect("V1 accepts a trailing separator"),
        V1ChangeVector::parse(&format!("A:5-{DB_ID_A}")).expect("parses"),
        "V1's parser ends in its whitespace state and returns the parsed list"
    );
    assert_eq!(
        V1ChangeVector::parse("A:5-").err(),
        Some(V1ChangeVectorError::Malformed),
        "an entry without its database identity is malformed"
    );
}

#[test]
fn v1_change_vector_parse_bounds_fail_closed() {
    // The contract's recorded bounds (GAP-D02, warden MINOR at integration):
    // inputs past 4096 bytes or 64 entries fail closed as Malformed rather
    // than parsing — pinned here, not just documented.
    let oversized = format!("A:5-{}", "X".repeat(4_100));
    assert_eq!(
        V1ChangeVector::parse(&oversized).err(),
        Some(V1ChangeVectorError::Malformed),
        "a text past MAX_V1_CHANGE_VECTOR_BYTES fails closed"
    );

    let mut many = String::new();
    for index in 0..65 {
        if index > 0 {
            many.push_str(", ");
        }
        many.push_str(&format!("A:{index}-{DB_ID_A}"));
    }
    assert!(
        many.len() <= 4_096,
        "the fixture stays inside the byte bound so the entry bound is what refuses it"
    );
    assert_eq!(
        V1ChangeVector::parse(&many).err(),
        Some(V1ChangeVectorError::Malformed),
        "a vector past MAX_V1_CHANGE_VECTOR_ENTRIES fails closed"
    );

    // Exactly at the bounds still parses: the bounds are bounds, not
    // off-by-one cliffs.
    let mut at_bound = String::new();
    for index in 0..64 {
        if index > 0 {
            at_bound.push_str(", ");
        }
        at_bound.push_str(&format!("A:{index}-{DB_ID_A}"));
    }
    assert_eq!(V1ChangeVector::parse(&at_bound).expect("64 entries parse").entries().len(), 64);
}
