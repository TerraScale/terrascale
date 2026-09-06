use std::ops::Bound;

use openraft::storage::RaftLogReader;
use openraft::{Entry, EntryPayload, LogId};

use crate::{SqliteStore, TypeConfig};

fn entry(index: u64) -> Entry<TypeConfig> {
    Entry {
        log_id: LogId::new(openraft::CommittedLeaderId::new(1, 1), index),
        payload: EntryPayload::Blank,
    }
}

fn install(store: &SqliteStore, index: u64) {
    let bytes = serde_json::to_vec(&entry(index)).unwrap();
    store.insert_raw_log_for_test(index, &bytes).unwrap();
}

#[tokio::test]
async fn log_reader_applies_all_range_bounds_and_uses_index() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    for index in [0, 1, 2, u64::MAX] {
        install(&store, index);
    }

    let indexes = |entries: Vec<Entry<TypeConfig>>| {
        entries.into_iter().map(|entry| entry.log_id.index).collect::<Vec<_>>()
    };
    assert_eq!(indexes(store.try_get_log_entries(0..=2).await.unwrap()), vec![0, 1, 2]);
    assert_eq!(indexes(store.try_get_log_entries(1..3).await.unwrap()), vec![1, 2]);
    assert_eq!(indexes(store.try_get_log_entries(..=0).await.unwrap()), vec![0]);
    assert_eq!(indexes(store.try_get_log_entries(..).await.unwrap()), vec![0, 1, 2, u64::MAX]);
    assert_eq!(
        indexes(store.try_get_log_entries(u64::MAX..=u64::MAX).await.unwrap()),
        vec![u64::MAX]
    );
    assert_eq!(
        indexes(store.try_get_log_entries((Bound::Excluded(2), Bound::Unbounded)).await.unwrap()),
        vec![u64::MAX]
    );
    assert!(store.try_get_log_entries(2..2).await.unwrap().is_empty());
    assert!(
        store
            .try_get_log_entries((Bound::Included(3), Bound::Included(2)))
            .await
            .unwrap()
            .is_empty()
    );
    assert!(
        store
            .try_get_log_entries((Bound::Excluded(u64::MAX), Bound::Unbounded))
            .await
            .unwrap()
            .is_empty()
    );
    assert!(
        store.try_get_log_entries((Bound::Unbounded, Bound::Excluded(0))).await.unwrap().is_empty()
    );

    let plan = store.explain_log_range_for_test().unwrap().join(" ");
    assert!(plan.contains("SEARCH raft_log USING INDEX sqlite_autoindex_raft_log_1"), "{plan}");
    assert!(plan.contains("log_index>?") && plan.contains("log_index<?"), "{plan}");
}

#[tokio::test]
async fn log_reader_does_not_decode_rows_outside_requested_range() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    install(&store, 51);
    store.insert_raw_log_for_test(50, b"not json").unwrap();

    assert_eq!(store.try_get_log_entries(51..=51).await.unwrap().len(), 1);
    assert!(store.try_get_log_entries(50..=50).await.is_err());
}

#[tokio::test]
async fn log_reader_rejects_entry_with_mismatched_storage_key() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap();
    store.insert_raw_log_for_test(51, &serde_json::to_vec(&entry(52)).unwrap()).unwrap();
    assert!(store.try_get_log_entries(51..=51).await.is_err());
}
