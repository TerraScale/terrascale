//! Public-path tests for the session and unit-of-work vocabulary (GAP-D01,
//! ticket #58).
//!
//! These tests use only public construction: the option set, the
//! transaction-mode and tracking vocabulary, the WhatChanged report types,
//! and the typed errors. `TenantScope` has no public constructor by design
//! (Platform creates it after authorization), so the commit-path behavior is
//! proved by the in-crate session tests instead (`src/session/tests.rs`) —
//! the same evidence split as GAP-D02's metadata contract.

use terrabase::session::{
    DocumentChanges, FieldChange, FieldChangeKind, SessionError, SessionHookVeto, SessionOptions,
    SessionTracking, SessionTransactionMode,
};
use terrabase::types::{CollectionName, DocumentId, DocumentValue, LocalDurability};

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

#[test]
fn options_default_to_tracked_single_node_synced_saves() {
    let defaults = SessionOptions::default();
    assert_eq!(defaults.tracking(), SessionTracking::Tracked);
    assert_eq!(defaults.transaction_mode(), SessionTransactionMode::SingleNode);
    assert_eq!(defaults.save_durability(), LocalDurability::Synced);
}

#[test]
fn options_build_fluently_and_report_their_selection() {
    let built = SessionOptions::new().no_tracking().durability(LocalDurability::Written);
    assert_eq!(built.tracking(), SessionTracking::NoTracking);
    assert_eq!(built.transaction_mode(), SessionTransactionMode::SingleNode);
    assert_eq!(built.save_durability(), LocalDurability::Written);

    let cluster = SessionOptions::new().cluster_wide();
    assert_eq!(cluster.transaction_mode(), SessionTransactionMode::ClusterWide);
    assert_eq!(cluster.tracking(), SessionTracking::Tracked);
}

#[test]
fn change_kind_carries_the_closed_change_type_vocabulary() {
    // The enum has eight closed cases. Pinning the rendered names fails when a
    // variant is renamed, dropped, or added to the public set.
    let vocabulary = [
        FieldChangeKind::DocumentDeleted,
        FieldChangeKind::DocumentAdded,
        FieldChangeKind::FieldChanged,
        FieldChangeKind::NewField,
        FieldChangeKind::RemovedField,
        FieldChangeKind::ArrayValueChanged,
        FieldChangeKind::ArrayValueAdded,
        FieldChangeKind::ArrayValueRemoved,
    ];
    let names: Vec<String> = vocabulary.iter().map(|kind| format!("{kind:?}")).collect();
    assert_eq!(
        names.join(","),
        "DocumentDeleted,DocumentAdded,FieldChanged,NewField,RemovedField,ArrayValueChanged,\
         ArrayValueAdded,ArrayValueRemoved",
        "the closed change-type vocabulary matches V1's ChangeType set"
    );
}

#[test]
fn field_change_reports_names_paths_and_values() {
    let change = FieldChange::new(
        "tags".to_owned(),
        "1".to_owned(),
        Some(text("b")),
        Some(text("x")),
        FieldChangeKind::ArrayValueChanged,
    );
    assert_eq!(change.field_path(), "tags");
    assert_eq!(change.field_name(), "1");
    assert_eq!(change.field_full_name(), "tags.1", "V1's FieldFullName joins path and name");
    assert_eq!(change.old_value(), Some(&text("b")));
    assert_eq!(change.new_value(), Some(&text("x")));

    let root = FieldChange::new(
        String::new(),
        "name".to_owned(),
        Some(text("ada")),
        Some(text("grace")),
        FieldChangeKind::FieldChanged,
    );
    assert_eq!(root.field_full_name(), "name", "the root reports the name alone");
}

#[test]
fn document_changes_reports_its_document_and_entries() {
    let report = DocumentChanges::new(
        CollectionName::try_new("users").expect("valid collection"),
        DocumentId::try_new("u1").expect("valid identifier"),
        vec![FieldChange::new(
            String::new(),
            String::new(),
            None,
            None,
            FieldChangeKind::DocumentAdded,
        )],
    );
    assert_eq!(report.collection().as_str(), "users");
    assert_eq!(report.id().as_str(), "u1");
    assert_eq!(report.changes().len(), 1);
    assert_eq!(report.changes()[0].change(), FieldChangeKind::DocumentAdded);
}

#[test]
fn typed_errors_carry_their_documents_and_reasons() {
    let document = DocumentId::try_new("u1").expect("valid identifier");
    assert_eq!(
        SessionError::NotTracked { document: document.clone() },
        SessionError::NotTracked { document: document.clone() },
        "the error compares by its typed parts"
    );
    assert_eq!(
        SessionError::DeferredCommandOnDeletedDocument { document },
        SessionError::DeferredCommandOnDeletedDocument {
            document: DocumentId::try_new("u1").expect("valid identifier"),
        }
    );
    let veto = SessionHookVeto::new("deletion refused");
    assert_eq!(veto.reason(), "deletion refused");
    assert_ne!(SessionHookVeto::new("a"), SessionHookVeto::new("b"));
}
