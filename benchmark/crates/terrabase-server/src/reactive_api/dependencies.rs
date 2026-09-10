//! Conservative interval matching over both sides of each durable publication.

use std::collections::{BTreeMap, BTreeSet};
use terrabase::query::{LiveQueryPlan, QueryCondition};
use terrabase::streaming::{JournalBeforeImage, SyncJournalRecord};
use terrabase::types::{Document, DocumentValue};
use terrabase_sync_core::subscription::{
    CommittedRow, DependencyIndex, DependencyKey, FieldValue, RangeEnd, ValueRange,
};

const MAX_MEMO_BYTES: usize = 8 * 1024 * 1024;

#[derive(Default)]
pub(super) struct Dependencies {
    next: u64,
    index: DependencyIndex,
    keys: BTreeMap<u64, DependencyKey>,
    fields: BTreeMap<String, BTreeSet<String>>,
    memo: BTreeMap<(u64, u32), Vec<u64>>,
    memo_bytes: usize,
}

impl Dependencies {
    pub(super) fn register(&mut self, plan: &LiveQueryPlan) -> u64 {
        self.next = self.next.checked_add(1).expect("view dependency identity exhausted");
        let key = dependency(plan);
        self.fields.entry(key.collection.clone()).or_default().insert(key.field.clone());
        self.index.register(self.next, key.clone(), false);
        self.keys.insert(self.next, key);
        self.clear_memo();
        self.next
    }

    pub(super) fn remove(&mut self, id: u64) {
        self.index.retract(id);
        self.keys.remove(&id);
        self.fields.clear();
        for key in self.keys.values() {
            self.fields.entry(key.collection.clone()).or_default().insert(key.field.clone());
        }
        self.clear_memo();
    }

    pub(super) fn affects(&mut self, id: u64, record: &SyncJournalRecord) -> bool {
        self.affected(record).binary_search(&id).is_ok()
    }

    pub(super) fn affected(&mut self, record: &SyncJournalRecord) -> std::borrow::Cow<'_, [u64]> {
        let position = (record.storage_sequence(), record.position().write_index());
        if !self.memo.contains_key(&position) {
            let affected = self.match_record(record);
            let bytes =
                affected.len().saturating_mul(std::mem::size_of::<u64>()).saturating_add(64);
            if self.memo_bytes.saturating_add(bytes) > MAX_MEMO_BYTES || self.memo.len() >= 4096 {
                self.clear_memo();
            }
            if bytes > MAX_MEMO_BYTES {
                return std::borrow::Cow::Owned(affected);
            }
            self.memo_bytes += bytes;
            self.memo.insert(position, affected);
        }
        std::borrow::Cow::Borrowed(&self.memo[&position])
    }

    fn clear_memo(&mut self) {
        self.memo.clear();
        self.memo_bytes = 0;
    }

    fn match_record(&self, record: &SyncJournalRecord) -> Vec<u64> {
        if matches!(record.before_image(), JournalBeforeImage::Unknown) {
            return self
                .keys
                .iter()
                .filter(|(_, key)| key.collection == record.collection().as_str())
                .map(|(id, _)| *id)
                .collect();
        }
        let mut affected = Vec::new();
        if let JournalBeforeImage::Document(document) = record.before_image() {
            affected.extend(self.match_document(document));
        }
        if let Some(document) = record.published_document() {
            affected.extend(self.match_document(&document));
        }
        affected.sort_unstable();
        affected.dedup();
        affected
    }

    fn match_document(&self, document: &Document) -> Vec<u64> {
        let Some(fields) = self.fields.get(document.collection().as_str()) else {
            return Vec::new();
        };
        let extracted = fields
            .iter()
            .filter_map(|field| {
                let value = if field.is_empty() {
                    Some(FieldValue::Number(0.0))
                } else {
                    LiveQueryPlan::dependency_value(document, field).as_deref().and_then(scalar)
                }?;
                Some((field.as_str(), value))
            })
            .collect();
        self.index.match_row(&CommittedRow {
            collection: document.collection().as_str(),
            document: document.id().as_str(),
            extracted,
        })
    }
}

// Numeric boundaries are inclusive even for strict comparisons. Conversion
// to f64 can collapse adjacent i64 values; this produces a superset and the
// native row evaluator retains exact comparison semantics.
#[allow(clippy::cast_precision_loss, reason = "inclusive rounded bounds deliberately overmatch")]
fn scalar(value: &DocumentValue) -> Option<FieldValue> {
    match value {
        DocumentValue::String(value) => Some(FieldValue::Text(value.clone())),
        DocumentValue::Integer(value) => Some(FieldValue::Number(*value as f64)),
        DocumentValue::Number(value) if value.is_finite() => {
            Some(FieldValue::Number(if *value == 0.0 { 0.0 } else { *value }))
        }
        _ => None,
    }
}

fn dependency(plan: &LiveQueryPlan) -> DependencyKey {
    for condition in plan.predicate().conditions() {
        let (field, range) = match condition {
            QueryCondition::Equals { field, value } => {
                let Some(value) = scalar(value) else { continue };
                (field, ValueRange::equals(value))
            }
            QueryCondition::GreaterThan { field, value }
            | QueryCondition::GreaterOrEqual { field, value } => {
                let Some(value) = scalar(value) else { continue };
                (field, ValueRange { start: RangeEnd::Included(value), end: RangeEnd::Unbounded })
            }
            QueryCondition::LessThan { field, value }
            | QueryCondition::LessOrEqual { field, value } => {
                let Some(value) = scalar(value) else { continue };
                (field, ValueRange { start: RangeEnd::Unbounded, end: RangeEnd::Included(value) })
            }
            _ => continue,
        };
        return DependencyKey {
            collection: plan.collection().as_str().into(),
            field: field.clone(),
            range,
        };
    }
    DependencyKey {
        collection: plan.collection().as_str().into(),
        field: String::new(),
        range: ValueRange::all(),
    }
}
