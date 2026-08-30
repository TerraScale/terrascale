//! Public construction tests for native document-version contracts.

use terrabase::engine::{
    ConditionalLoadError, DocumentHead, DocumentVersionContract, DocumentVersionContractError,
    HeadCondition,
};
use terrabase::replication::ReplicationClockStamp;

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
        Some(DocumentVersionContractError::ZeroVersion)
    );
    assert_eq!(
        DocumentVersionContract::try_new(1, 0, ReplicationClockStamp::new(0, 0)).err(),
        Some(DocumentVersionContractError::ZeroSequence)
    );
}

#[test]
fn head_outcomes_carry_a_contract_when_state_exists() {
    let current = contract(1, 3);
    assert_eq!(DocumentHead::Missing.contract(), None);
    assert_eq!(DocumentHead::Unchanged(current).contract(), Some(&current));
    assert_eq!(DocumentHead::Current(current).contract(), Some(&current));
}

#[test]
fn head_conditions_are_distinct() {
    let baseline = contract(1, 3);
    assert_ne!(HeadCondition::Any, HeadCondition::IfUnchanged(baseline));
}

#[test]
fn conditional_load_errors_are_typed() {
    let current = contract(2, 9);
    assert_eq!(ConditionalLoadError::Missing, ConditionalLoadError::Missing);
    assert_eq!(ConditionalLoadError::Stale { current }, ConditionalLoadError::Stale { current });
    assert_eq!(
        ConditionalLoadError::Engine(terrabase::EngineError::StorageFailure),
        ConditionalLoadError::Engine(terrabase::EngineError::StorageFailure)
    );
}
