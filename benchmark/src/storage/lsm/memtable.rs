//! The ordered mutable memory segment of the LSM engine (P1).
//!
//! A memtable is a `BTreeMap` over internal keys, so iteration is sorted by
//! construction and rotation is a move into the frozen list. Rotation is
//! bounded by bytes and by record count because document sizes vary
//! (ADR 0016 D-2).

use std::collections::BTreeMap;

use super::internal::{Entry, InternalKey};

/// Rotation defaults: freeze the memtable at 4 MiB or 65,536 entries,
/// whichever comes first.
pub(super) const DEFAULT_MAX_MEMTABLE_BYTES: usize = 4 << 20;
/// The record-count half of the rotation bound.
pub(super) const DEFAULT_MAX_MEMTABLE_ENTRIES: usize = 65_536;

/// One ordered mutable memory segment.
#[derive(Default)]
pub(super) struct MemTable {
    entries: BTreeMap<InternalKey, Vec<u8>>,
    bytes: usize,
}

impl MemTable {
    /// Inserts one entry, replacing any entry at the exact same internal key
    /// (WAL replay idempotence).
    pub(super) fn insert(&mut self, entry: Entry) {
        let had = self.entries.remove(&entry.key);
        if let Some(old) = had {
            self.bytes = self.bytes.saturating_sub(old.len() + entry.key.user_key().len() + 24);
        }
        self.bytes = self.bytes.saturating_add(entry.key.user_key().len() + entry.value.len() + 24);
        self.entries.insert(entry.key, entry.value);
    }

    /// Iterates every entry in sorted internal-key order.
    pub(super) fn iter(&self) -> impl Iterator<Item = Entry> + '_ {
        self.entries.iter().map(|(key, value)| Entry { key: key.clone(), value: value.clone() })
    }

    /// The number of entries held.
    #[cfg(test)]
    pub(super) fn len(&self) -> usize {
        self.entries.len()
    }

    /// Reports whether rotation bounds are exceeded.
    pub(super) fn should_rotate(&self, max_bytes: usize, max_entries: usize) -> bool {
        self.bytes >= max_bytes || self.entries.len() >= max_entries
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{CollectionName, DocumentId, TenantId};

    fn key(tenant: &str, id: &str, sequence: u64) -> InternalKey {
        InternalKey::document(
            &TenantId::try_new(tenant).expect("valid tenant"),
            &CollectionName::try_new("c").expect("valid collection"),
            &DocumentId::try_new(id).expect("valid document"),
            sequence,
        )
    }

    #[test]
    fn iteration_is_sorted_newest_first_per_key() {
        let mut table = MemTable::default();
        table.insert(Entry { key: key("t", "a", 1), value: b"one".to_vec() });
        table.insert(Entry { key: key("t", "a", 2), value: b"two".to_vec() });
        table.insert(Entry { key: key("t", "b", 1), value: b"bee".to_vec() });
        let order: Vec<_> = table.iter().map(|entry| entry.key.sequence()).collect();
        assert_eq!(order, vec![2, 1, 1]);
        let keys: Vec<_> = table.iter().map(|entry| entry.key.user_key().to_vec()).collect();
        assert!(keys[0] == keys[1], "same user key clusters");
    }

    #[test]
    fn duplicate_insert_replaces_idempotently() {
        let mut table = MemTable::default();
        table.insert(Entry { key: key("t", "a", 1), value: b"one".to_vec() });
        table.insert(Entry { key: key("t", "a", 1), value: b"uno".to_vec() });
        assert_eq!(table.len(), 1);
        let collected: Vec<_> = table.iter().collect();
        assert_eq!(collected[0].value, b"uno");
    }

    #[test]
    fn rotation_fires_on_both_bounds() {
        let mut table = MemTable::default();
        table.insert(Entry { key: key("t", "a", 1), value: vec![0_u8; 1024] });
        assert!(table.should_rotate(1024, usize::MAX));
        let mut small = MemTable::default();
        for sequence in 0..4 {
            small.insert(Entry { key: key("t", "a", sequence), value: Vec::new() });
        }
        assert!(small.should_rotate(usize::MAX, 4));
        assert!(!small.should_rotate(usize::MAX, 5));
    }
}
