//! Qualification cases for range-based journal set reconciliation.

use terrabase_replication_core::anti_entropy::{
    JournalRowId, RangeSummaryTable, ReconciliationMessage, RepairError, RowDifference, RowSummary,
    row_hash,
};

fn row(origin: &str, sequence: u64, value: &[u8]) -> RowSummary {
    RowSummary::new(JournalRowId::new(origin, sequence).unwrap(), row_hash(value))
}

#[test]
fn insertion_order_does_not_change_the_additive_proof() {
    let rows = [row("gru", 1, b"one"), row("gru", 2, b"two"), row("fra", 1, b"three")];
    let mut left = RangeSummaryTable::new();
    for item in &rows {
        left.insert(item);
    }
    let mut right = RangeSummaryTable::new();
    for item in rows.iter().rev() {
        right.insert(item);
    }
    assert_eq!(left.proof(), right.proof());
    assert!(left.reconcile(&right, 1).unwrap().is_converged());
}

#[test]
fn one_difference_descends_to_one_leaf_and_skips_equal_ranges() {
    let mut local = RangeSummaryTable::with_layout(4, 4).unwrap();
    let mut peer = RangeSummaryTable::with_layout(4, 4).unwrap();
    for sequence in 1..=40 {
        local.insert(&row("gru", sequence, &sequence.to_be_bytes()));
        peer.insert(&row("gru", sequence, &sequence.to_be_bytes()));
    }
    peer.insert(&row("gru", 34, b"different"));

    let plan = local.reconcile(&peer, 1).unwrap();
    assert_eq!(plan.rounds(), 3, "forty rows need root, branch, and leaf rounds");
    assert_eq!(plan.differences().len(), 1);
    assert!(matches!(
        plan.differences()[0],
        RowDifference::HashMismatch { ref id, .. } if id.sequence() == 34
    ));
    assert_eq!(
        plan.messages()
            .iter()
            .filter(|message| matches!(message, ReconciliationMessage::IdList { .. }))
            .count(),
        1
    );
    assert!(
        plan.messages().iter().any(|message| matches!(message, ReconciliationMessage::Skip { .. }))
    );
}

#[test]
fn missing_rows_are_directional_and_budgeted_exactly() {
    let mut local = RangeSummaryTable::with_layout(4, 4).unwrap();
    let mut peer = RangeSummaryTable::with_layout(4, 4).unwrap();
    local.insert(&row("gru", 1, b"local"));
    peer.insert(&row("gru", 10, b"peer"));

    assert_eq!(
        local.reconcile(&peer, 1),
        Err(RepairError::RepairBudgetExceeded { differences: 2, max_differences: 1 })
    );
    let plan = local.reconcile(&peer, 2).unwrap();
    assert!(matches!(plan.differences()[0], RowDifference::MissingRemote(_)));
    assert!(matches!(plan.differences()[1], RowDifference::MissingLocal(_)));
}

#[test]
fn difference_budget_stops_leaf_work_without_accumulating_the_divergence() {
    let local = RangeSummaryTable::with_layout(2, 2).unwrap();
    let mut peer = RangeSummaryTable::with_layout(2, 2).unwrap();
    for sequence in 1..=20_000 {
        peer.insert(&row("gru", sequence, &sequence.to_be_bytes()));
    }

    assert_eq!(
        local.reconcile(&peer, 1),
        Err(RepairError::RepairBudgetExceeded { differences: 2, max_differences: 1 })
    );
}

#[test]
fn maximum_sequence_occupies_one_path_without_duplicate_differences() {
    let local = RangeSummaryTable::new();
    let mut peer = RangeSummaryTable::new();
    peer.insert(&row("gru", u64::MAX, b"last"));

    let plan = local.reconcile(&peer, 1).unwrap();
    assert_eq!(plan.differences().len(), 1);
    assert_eq!(plan.differences()[0].id().sequence(), u64::MAX);
}

#[test]
fn replacement_and_removal_update_the_maintained_proof() {
    let mut table = RangeSummaryTable::new();
    let id = JournalRowId::new("gru", 7).unwrap();
    table.insert(&RowSummary::new(id.clone(), row_hash(b"old")));
    let old = table.proof();
    assert_eq!(
        table.insert(&RowSummary::new(id.clone(), row_hash(b"new"))),
        Some(row_hash(b"old"))
    );
    assert_ne!(table.proof(), old);
    assert_eq!(table.remove(&id), Some(row_hash(b"new")));
    assert!(table.is_empty());
    assert_eq!(table.proof().count, 0);
}
