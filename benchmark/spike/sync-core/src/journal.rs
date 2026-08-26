//! A minimal stand-in journal of committed change events.
//!
//! The real Change Event Backbone is SE-03's; this module only fixes the
//! shape of what the spike's materializer consumes: one typed, ordered,
//! stable-identity commit event per materialization step. Events are
//! ordered by [`JournalEvent::storage_sequence`] and identified by
//! [`JournalEvent::operation_id`] so delivery can stay at-least-once with
//! duplicates identifiable by the consumer — the streaming seam's contract,
//! restated for the spike.

use std::collections::BTreeMap;

/// The synthetic document value model of the spike: the JSON-ish subset a
/// schema-free collection body uses in the spike's generators.
#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    /// A null field.
    Null,
    /// A boolean field.
    Bool(bool),
    /// A signed integer field.
    Int(i64),
    /// A double field.
    Float(f64),
    /// A string field.
    Str(String),
}

impl Value {
    /// The deterministic text encoding used for full-document re-send
    /// baselines: `key=value` pairs joined in key order. Byte counts from
    /// this encoding are the spike's full-document baseline, not a claim
    /// about any production encoding.
    #[must_use]
    pub fn encode_field(key: &str, value: &Value) -> String {
        match value {
            Value::Null => format!("{key}=null"),
            Value::Bool(b) => format!("{key}={b}"),
            Value::Int(i) => format!("{key}={i}"),
            Value::Float(f) => format!("{key}={f}"),
            Value::Str(s) => format!("{key}={s}"),
        }
    }
}

/// One committed change as the spike's materializer consumes it.
#[derive(Clone, Debug)]
pub struct JournalEvent {
    /// The storage sequence ordering this commit. Strictly increasing.
    pub storage_sequence: u64,
    /// The stable operation identity of the commit (at-least-once anchor).
    pub operation_id: String,
    /// The logical collection the document belongs to.
    pub collection: String,
    /// The identity of the document within the collection.
    pub document_id: String,
    /// What happened to the document.
    pub change: JournalChange,
}

/// The shape of a committed document change.
#[derive(Clone, Debug)]
pub enum JournalChange {
    /// The document body is put (replaced) with these fields.
    Put {
        /// The full new body of the document.
        body: BTreeMap<String, Value>,
    },
    /// The document is deleted (a tombstone is materialized).
    Delete,
}

/// The number of bytes a full-document re-send of this body would carry
/// under the spike's baseline encoding (see [`Value::encode_field`]).
#[must_use]
pub fn full_body_bytes(body: &BTreeMap<String, Value>) -> usize {
    body.iter().map(|(k, v)| Value::encode_field(k, v).len() + 1).sum::<usize>().saturating_sub(1)
}

/// A deterministic synthetic journal generator (xorshift64*; no external
/// dependency, fixed by an explicit seed so every measurement is
/// reproducible).
pub struct JournalGenerator {
    seed: u64,
    next_sequence: u64,
    next_operation: u64,
}

impl JournalGenerator {
    /// Creates a generator whose output is fixed by `seed`.
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self { seed: seed.max(1), next_sequence: 1, next_operation: 1 }
    }

    fn next_u64(&mut self) -> u64 {
        // xorshift64*: deterministic, dependency-free, synthetic data only.
        let mut x = self.seed;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.seed = x;
        x.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    /// Produces the next `put` event: a document body with `fields` fields,
    /// each a short synthetic string, identified by `document_id`.
    #[must_use]
    pub fn next_put(&mut self, document_id: &str, fields: usize) -> JournalEvent {
        let mut body = BTreeMap::new();
        for field in 0..fields {
            let value = self.next_u64();
            body.insert(format!("f{field:03}"), Value::Str(format!("v{value:016x}")));
        }
        let event = JournalEvent {
            storage_sequence: self.next_sequence,
            operation_id: format!("op-{:08}", self.next_operation),
            collection: "spike".to_string(),
            document_id: document_id.to_string(),
            change: JournalChange::Put { body },
        };
        self.next_sequence += 1;
        self.next_operation += 1;
        event
    }

    /// Produces the next patch event: a single-field change to an existing
    /// document body of `fields` fields (the body is regenerated so the
    /// patch is a genuine field-level change against the previous body).
    #[must_use]
    pub fn next_patch(&mut self, document_id: &str, fields: usize, field: usize) -> JournalEvent {
        let mut event = self.next_put(document_id, fields);
        event.storage_sequence = self.next_sequence;
        event.operation_id = format!("op-{:08}", self.next_operation);
        self.next_sequence += 1;
        self.next_operation += 1;
        // Keep only the patched field: a single-field patch body.
        let value = match event.change {
            JournalChange::Put { ref mut body } => {
                let key = format!("f{field:03}");
                body.get(&key).cloned().unwrap_or(Value::Str(String::new()))
            }
            JournalChange::Delete => unreachable!("next_put produced a put"),
        };
        let mut body = BTreeMap::new();
        body.insert(format!("f{field:03}"), value);
        event.change = JournalChange::Put { body };
        event
    }

    /// Produces the next delete event for `document_id`.
    #[must_use]
    pub fn next_delete(&mut self, document_id: &str) -> JournalEvent {
        let event = JournalEvent {
            storage_sequence: self.next_sequence,
            operation_id: format!("op-{:08}", self.next_operation),
            collection: "spike".to_string(),
            document_id: document_id.to_string(),
            change: JournalChange::Delete,
        };
        self.next_sequence += 1;
        self.next_operation += 1;
        event
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generator_is_deterministic_for_a_fixed_seed() {
        let mut a = JournalGenerator::new(7);
        let mut b = JournalGenerator::new(7);
        let ea = a.next_put("d-1", 4);
        let eb = b.next_put("d-1", 4);
        assert_eq!(format!("{ea:?}"), format!("{eb:?}"));
    }

    #[test]
    fn sequences_are_strictly_increasing() {
        let mut journal = JournalGenerator::new(1);
        let e1 = journal.next_put("d-1", 2);
        let e2 = journal.next_patch("d-1", 2, 0);
        let e3 = journal.next_delete("d-1");
        assert!(e1.storage_sequence < e2.storage_sequence);
        assert!(e2.storage_sequence < e3.storage_sequence);
    }

    #[test]
    fn patch_events_carry_exactly_one_field() {
        let mut journal = JournalGenerator::new(3);
        let event = journal.next_patch("d-9", 20, 5);
        match event.change {
            JournalChange::Put { body } => assert_eq!(body.len(), 1),
            JournalChange::Delete => panic!("patch must be a put"),
        }
    }

    #[test]
    fn full_body_bytes_counts_the_baseline_encoding() {
        let mut body = BTreeMap::new();
        body.insert("k".to_string(), Value::Str("abcd".to_string()));
        // "k=abcd" is 6 bytes.
        assert_eq!(full_body_bytes(&body), 6);
    }
}
