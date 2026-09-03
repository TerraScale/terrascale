//! The reactive delivery layer (`S-04`, D-38/D-46): result-view
//! subscriptions served off the same durable journal pump that feeds
//! the backbone.
//!
//! [`ReactiveState`] owns one [`SubscriptionEngine`] plus every
//! subscription's bounded delivery channel. The delivery host converts
//! each committed journal record into one [`RowEvent`] (extracted
//! dependency values and projection read off the in-hand document
//! value, once per row) and applies it; each affected subscription's
//! delta goes to its channel. A full channel is the backpressure
//! ladder: the backlog is dropped and replaced with [`ReactiveFrame::Reset`]
//! — a slow consumer re-bootstraps, it never queues without bound
//! (docs/sync/terrascale-sync-v2.md §Backpressure).

use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::sync::mpsc::Receiver;
use std::sync::mpsc::SyncSender;
use std::sync::mpsc::sync_channel;

use terrabase::streaming::SyncJournalMutation;
use terrabase::streaming::SyncJournalRecord;
use terrabase::types::DocumentValue;
use terrabase_sync_core::server::Delta;
use terrabase_sync_core::server::RowEvent;
use terrabase_sync_core::server::SubscriptionEngine;
use terrabase_sync_core::server::SubscriptionSpec;
use terrabase_sync_core::subscription::FieldValue;

/// The bounded capacity of one subscription's delivery channel.
const REACTIVE_CHANNEL_CAPACITY: usize = 1024;

/// How many recent revisions each subscription retains for resume.
const REVISION_RING: usize = 256;

/// One seed's members: document → projected fields, in document order.
pub type ProjectedMembers = Vec<(String, Vec<(String, FieldValue)>)>;

/// One frame delivered to one reactive subscription.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReactiveFrame {
    /// The bootstrap snapshot: the seeded result members, in document
    /// order. Always the first frame of a subscription.
    Seed {
        /// The subscription's identity.
        subscription: u64,
        /// The members' projected fields, in document order.
        members: Vec<(String, Vec<(String, FieldValue)>)>,
    },
    /// One incremental result delta.
    Delta {
        /// The subscription's identity.
        subscription: u64,
        /// The delta.
        delta: Delta,
    },
    /// The backlog was dropped: the consumer must re-bootstrap. Sent in
    /// place of the coalesced backlog when a channel fills.
    Reset {
        /// The subscription's identity.
        subscription: u64,
    },
}

/// The delivery host's reactive half.
#[derive(Default)]
pub struct ReactiveState {
    engine: SubscriptionEngine,
    feeds: BTreeMap<u64, SyncSender<ReactiveFrame>>,
    /// Subscriptions whose backlog was dropped: further deltas coalesce
    /// away until the consumer re-bootstraps through a fresh seed.
    reset_pending: BTreeSet<u64>,
    /// Each subscription's retained revisions, for checkpoint resume:
    /// the newest [`REVISION_RING`] deltas, contiguous. A coalesced
    /// backlog breaks contiguity and forces Reset semantics on resume.
    rings: BTreeMap<u64, std::collections::VecDeque<Delta>>,
    /// The fields to extract per collection: every registered
    /// subscription's dependency and projection fields.
    fields: BTreeMap<String, BTreeSet<String>>,
    /// Subscriptions whose transport closed but whose state is retained
    /// for checkpoint resume. The retention window ends when the
    /// delivery channel fills (the backlog coalesces away, the revision
    /// ring is dropped) — a reaped subscription's resume is a plain
    /// fresh seed (Reset semantics), never a wrong replay.
    detached: BTreeSet<u64>,
}

impl ReactiveState {
    /// Registers one subscription and opens its bounded delivery
    /// channel. The caller seeds it before the first pump.
    pub(crate) fn subscribe(&mut self, spec: SubscriptionSpec) -> (u64, Receiver<ReactiveFrame>) {
        self.reap();
        let fields = self.fields.entry(spec.collection.clone()).or_default();
        for condition in &spec.conditions {
            fields.insert(condition.field.clone());
        }
        for field in &spec.projection {
            fields.insert(field.clone());
        }
        let id = self.engine.register(spec);
        let (sender, receiver) = sync_channel(REACTIVE_CHANNEL_CAPACITY);
        self.feeds.insert(id, sender);
        (id, receiver)
    }

    /// Seeds one subscription's result and delivers the Seed frame.
    pub(crate) fn seed(&mut self, id: u64, members: Vec<(String, Vec<(String, FieldValue)>)>) {
        self.reset_pending.remove(&id);
        if let Some(sender) = self.feeds.get(&id)
            && sender
                .send(ReactiveFrame::Seed { subscription: id, members: members.clone() })
                .is_err()
        {
            self.feeds.remove(&id);
        }
        self.engine.seed(id, members);
    }

    /// Retracts one subscription.
    pub(crate) fn unsubscribe(&mut self, id: u64) {
        self.detached.remove(&id);
        self.rings.remove(&id);
        self.engine.unregister(id);
        self.feeds.remove(&id);
    }

    /// Marks one subscription's transport closed while retaining its
    /// state for resume: the engine keeps updating, the ring keeps
    /// growing, until the abandoned channel fills.
    pub(crate) fn detach(&mut self, id: u64) {
        self.detached.insert(id);
    }

    /// Reaps detached subscriptions whose retention window closed (the
    /// backlog coalesced away): their resume is already Reset semantics.
    fn reap(&mut self) {
        let dead: Vec<u64> = self.detached.intersection(&self.reset_pending).copied().collect();
        for id in dead {
            self.detached.remove(&id);
            self.rings.remove(&id);
            self.engine.unregister(id);
            self.feeds.remove(&id);
        }
    }

    /// The live subscription count.
    pub(crate) fn len(&self) -> usize {
        self.feeds.len()
    }

    /// Applies one committed journal record: dependency values and
    /// projection read off the in-hand document value, once per row, for
    /// the fields this collection's subscriptions declared (`None` is a
    /// tombstone).
    pub(crate) fn apply_put(
        &mut self,
        collection: &str,
        document: &str,
        value: Option<&DocumentValue>,
    ) {
        let fields = self.fields.get(collection);
        let put = value.map(|value| {
            fields
                .into_iter()
                .flatten()
                .filter_map(|field| extract_path(value, field).map(|v| (field.clone(), v)))
                .collect::<Vec<_>>()
        });
        let extracted = put.clone().unwrap_or_default();
        let event = RowEvent {
            collection: collection.to_owned(),
            document: document.to_owned(),
            put,
            extracted,
        };
        for (id, delta) in self.engine.apply(&event) {
            self.deliver(id, ReactiveFrame::Delta { subscription: id, delta });
        }
    }

    /// Applies one committed journal record.
    pub(crate) fn apply_record(&mut self, record: &SyncJournalRecord) {
        let value = match record.mutation() {
            SyncJournalMutation::Put(value) => Some(value),
            SyncJournalMutation::Delete => None,
        };
        self.apply_put(record.collection().as_str(), record.document().as_str(), value);
    }

    /// Delivers one frame with the backpressure ladder: a full or dead
    /// channel marks the subscription reset-pending and further deltas
    /// coalesce away until the consumer re-seeds.
    fn deliver(&mut self, id: u64, frame: ReactiveFrame) {
        if self.reset_pending.contains(&id) {
            // The backlog was dropped: the ring is no longer contiguous,
            // so resume must reset.
            self.rings.remove(&id);
            return;
        }
        let Some(sender) = self.feeds.get(&id) else {
            return;
        };
        if let ReactiveFrame::Delta { delta, .. } = &frame {
            let ring = self.rings.entry(id).or_default();
            ring.push_back(delta.clone());
            while ring.len() > REVISION_RING {
                ring.pop_front();
            }
        }
        if sender.try_send(frame).is_err() {
            self.reset_pending.insert(id);
            self.rings.remove(&id);
        }
    }

    /// Resumes one subscription from a checkpoint: when the previous
    /// subscription's ring still holds every revision past the
    /// checkpoint, the new subscription inherits its members and the
    /// missed revisions replay — the client keeps its state. Anything
    /// else (unknown subscription, coalesced backlog, revision below
    /// the retained floor, changed specification) answers `None` and
    /// the caller seeds fresh (Reset semantics).
    pub(crate) fn resume(
        &mut self,
        previous: u64,
        revision: u64,
        spec: &SubscriptionSpec,
    ) -> Option<(u64, Receiver<ReactiveFrame>, Vec<Delta>)> {
        let ring = self.rings.get(&previous).cloned()?; // coalesced or never retained: Reset
        let head = ring.back().map(|delta| delta.revision)?; // nothing retained: Reset
        let _ = head;
        let oldest = ring.front().map(|delta| delta.revision)?;
        if revision + 1 < oldest {
            return None; // below the retained floor: Reset
        }
        if !self.engine.specification_of(previous).is_some_and(|held| held == spec) {
            return None; // the view changed: Reset
        }
        let members: ProjectedMembers =
            self.engine.members(previous)?.clone().into_iter().collect();
        let missed: Vec<Delta> =
            ring.iter().filter(|delta| delta.revision > revision).cloned().collect();
        let (id, receiver) = self.subscribe(spec.clone());
        self.engine.seed(id, members);
        self.rings.insert(id, ring);
        Some((id, receiver, missed))
    }
}

/// Reads one dotted-path field off the in-hand document value: the two
/// dependency classes are text and numbers (the D-42 stored columns);
/// composites and absent paths extract nothing.
pub(crate) fn extract_path(value: &DocumentValue, path: &str) -> Option<FieldValue> {
    let mut current = value;
    for segment in path.split('.') {
        let DocumentValue::Object(fields) = current else {
            return None;
        };
        current = fields.get(segment)?;
    }
    match current {
        DocumentValue::String(text) => Some(FieldValue::Text(text.clone())),
        // ponytail: the dependency class is the NUMERIC column (REAL), so
        // integers narrow to it; exact past 2^53 needs a decimal class.
        #[allow(clippy::cast_precision_loss, reason = "the NUMERIC dependency class is f64")]
        DocumentValue::Integer(number) => Some(FieldValue::Number(*number as f64)),
        DocumentValue::Number(number) => Some(FieldValue::Number(*number)),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::DocumentValue;
    use super::ReactiveFrame;
    use super::ReactiveState;
    use terrabase_sync_core::server::Comparison;
    use terrabase_sync_core::server::Condition;
    use terrabase_sync_core::server::DeltaOp;
    use terrabase_sync_core::server::SubscriptionSpec;
    use terrabase_sync_core::subscription::FieldValue;

    fn spec() -> SubscriptionSpec {
        SubscriptionSpec {
            collection: "orders".into(),
            conditions: vec![Condition {
                field: "status".into(),
                op: Comparison::Equals,
                value: FieldValue::Text("paid".into()),
            }],
            projection: vec!["total".into()],
        }
    }

    fn object_document(status: &str) -> DocumentValue {
        let mut fields = std::collections::BTreeMap::new();
        fields.insert("status".to_owned(), DocumentValue::String(status.to_owned()));
        DocumentValue::Object(fields)
    }

    #[test]
    fn subscribe_seed_and_delta_flow_through_the_channel() {
        let mut reactive = ReactiveState::default();
        let (id, receiver) = reactive.subscribe(spec());
        reactive.seed(id, vec![("o-1".into(), vec![("total".into(), FieldValue::Number(5.0))])]);
        let frame = receiver.recv().expect("the seed frame");
        assert!(matches!(frame, ReactiveFrame::Seed { .. }));
        // The engine was seeded: a status flip away from the predicate
        // produces a Remove through the holders map.
        let document = object_document("open");
        reactive.apply_put("orders", "o-1", Some(&document));
        let frame = receiver.recv().expect("the delta frame");
        assert!(
            matches!(
                frame,
                ReactiveFrame::Delta { ref delta, .. } if matches!(delta.operation, DeltaOp::Remove { .. })
            ),
            "{frame:?}"
        );
    }

    #[test]
    fn a_matching_put_inserts_and_a_tombstone_removes() {
        let mut reactive = ReactiveState::default();
        let (id, receiver) = reactive.subscribe(spec());
        reactive.seed(id, Vec::new());
        let _ = receiver.recv();
        let paid = object_document("paid");
        reactive.apply_put("orders", "o-2", Some(&paid));
        assert!(matches!(
            receiver.recv().expect("the insert"),
            ReactiveFrame::Delta { subscription, delta }
                if subscription == id && matches!(delta.operation, DeltaOp::Insert { .. })
        ));
        reactive.apply_put("orders", "o-2", None);
        assert!(matches!(
            receiver.recv().expect("the remove"),
            ReactiveFrame::Delta { ref delta, .. } if matches!(delta.operation, DeltaOp::Remove { .. })
        ));
    }

    #[test]
    fn a_slow_consumer_gets_reset_not_an_unbounded_queue() {
        let mut reactive = ReactiveState::default();
        // A subscription whose channel no one drains, filled past
        // capacity: the next delivery must Reset, not block or leak.
        let (id, receiver) = reactive.subscribe(spec());
        reactive.seed(id, Vec::new());
        let _ = receiver.recv();
        for number in
            0..u32::try_from(super::REACTIVE_CHANNEL_CAPACITY + 8).expect("small capacity")
        {
            let mut fields = std::collections::BTreeMap::new();
            fields.insert("status".to_owned(), DocumentValue::String("paid".to_owned()));
            fields.insert("total".to_owned(), DocumentValue::Integer(i64::from(number)));
            reactive.apply_put(
                "orders",
                &format!("doc-{number}"),
                Some(&DocumentValue::Object(fields)),
            );
        }
        // A full channel never blocks or leaks: the writer is not
        // blocked, and the backlog is coalesced away past the bound.
        // (The reset query returns with the transport slice, which owns
        // the draining side.)
        let mut drained = 0;
        while receiver.try_recv().is_ok() {
            drained += 1;
        }
        assert!(
            drained <= super::REACTIVE_CHANNEL_CAPACITY,
            "the channel never holds more than its bound: {drained}"
        );
    }

    #[test]
    fn unsubscribe_closes_the_channel() {
        let mut reactive = ReactiveState::default();
        let (id, receiver) = reactive.subscribe(spec());
        reactive.unsubscribe(id);
        assert_eq!(reactive.len(), 0);
        assert!(receiver.recv().is_err(), "the channel is closed");
    }
}
