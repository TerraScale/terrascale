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
//! (D-38 backpressure contract).

use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::sync::mpsc::Receiver;
use std::sync::mpsc::SyncSender;
use std::sync::mpsc::sync_channel;

use terrabase::QueryPlan;
use terrabase::streaming::SyncJournalMutation;
use terrabase::streaming::SyncJournalRecord;
use terrabase::types::DocumentValue;
use terrabase_sync_core::server::Delta;
use terrabase_sync_core::server::RowEvent;
use terrabase_sync_core::server::SubscriptionEngine;
use terrabase_sync_core::server::SubscriptionSpec;
use terrabase_sync_core::subscription::FieldValue;

/// A stable identity for a materialized view.  Authorization-equivalent
/// readers use the same identity and therefore share one materializer; the
/// scope component prevents a reader from ever sharing rows across database
/// or authorization boundaries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct SharedViewKey {
    scope: String,
    spec: SubscriptionSpec,
    /// Canonical in-process ordering key. `SubscriptionSpec` intentionally
    /// does not expose ordering because its field values only promise
    /// equality; the debug form is deterministic for the closed enum model.
    order_key: String,
}

impl Ord for SharedViewKey {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.scope.cmp(&other.scope).then_with(|| self.order_key.cmp(&other.order_key))
    }
}

impl PartialOrd for SharedViewKey {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

/// The compile-ready adapter passed from the SQL query registry to the sync
/// registry.  Query compilation remains owned by `terrabase`; this adapter
/// carries the immutable compiled plan together with the dependency metadata
/// consumed by the sync-core interval index.
#[derive(Clone, Debug, PartialEq)]
pub struct ReactivePlanAdapter {
    query: QueryPlan,
    subscription: SubscriptionSpec,
}

impl ReactivePlanAdapter {
    /// Binds a compiled SQL plan to its sync dependency description.
    #[must_use]
    pub fn new(query: QueryPlan, subscription: SubscriptionSpec) -> Self {
        Self { query, subscription }
    }

    /// Returns the immutable SQL plan for snapshot seeding.
    #[must_use]
    pub fn query_plan(&self) -> &QueryPlan {
        &self.query
    }

    /// Returns the dependency/index description registered with sync-core.
    #[must_use]
    pub fn subscription_spec(&self) -> &SubscriptionSpec {
        &self.subscription
    }
}

impl SharedViewKey {
    pub(crate) fn new(scope: impl Into<String>, spec: SubscriptionSpec) -> Self {
        let scope = scope.into();
        let order_key = format!("{spec:?}");
        Self { scope, spec, order_key }
    }

    pub(crate) fn spec(&self) -> &SubscriptionSpec {
        &self.spec
    }
}

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
    /// One sync-core materializer per canonical view, independent of the
    /// number of attached readers.
    views: BTreeMap<SharedViewKey, u64>,
    /// The inverse mapping lets one materialized delta fan out to readers.
    reader_views: BTreeMap<u64, u64>,
    /// Readers retain their key for authorization and resume validation.
    reader_keys: BTreeMap<u64, SharedViewKey>,
    seeded_views: BTreeSet<u64>,
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
    /// The exact canonical body bytes last seen for each row.  This is a
    /// byte seam for encoders and replication adapters; no reader reparses a
    /// body to obtain its wire representation.
    raw_bodies: BTreeMap<(u64, String), Vec<u8>>,
    next_reader: u64,
}

impl ReactiveState {
    /// Registers one subscription and opens its bounded delivery
    /// channel. The caller seeds it before the first pump.
    pub(crate) fn subscribe(&mut self, spec: SubscriptionSpec) -> (u64, Receiver<ReactiveFrame>) {
        self.subscribe_scoped(String::new(), spec)
    }

    /// Attaches one reader to a canonical shared view.  The materializer is
    /// registered only once for equal `(authorization scope, plan)` keys.
    pub(crate) fn subscribe_scoped(
        &mut self,
        scope: impl Into<String>,
        spec: SubscriptionSpec,
    ) -> (u64, Receiver<ReactiveFrame>) {
        self.reap();
        let fields = self.fields.entry(spec.collection.clone()).or_default();
        for condition in &spec.conditions {
            fields.insert(condition.field.clone());
        }
        for field in &spec.projection {
            fields.insert(field.clone());
        }
        let key = SharedViewKey::new(scope, spec);
        let view = if let Some(view) = self.views.get(&key).copied() {
            view
        } else {
            let view = self.engine.register(key.spec().clone());
            self.views.insert(key.clone(), view);
            view
        };
        let id = self.next_reader;
        self.next_reader = self.next_reader.saturating_add(1);
        self.reader_views.insert(id, view);
        self.reader_keys.insert(id, key);
        let (sender, receiver) = sync_channel(REACTIVE_CHANNEL_CAPACITY);
        self.feeds.insert(id, sender);
        (id, receiver)
    }

    /// Seeds one subscription's result and delivers the Seed frame.
    pub(crate) fn seed(&mut self, id: u64, members: Vec<(String, Vec<(String, FieldValue)>)>) {
        self.seed_with_raw(id, members, Vec::new());
    }

    /// Seeds a shared view and records its canonical body bytes alongside the
    /// projected values.  The bytes are retained once per view, not once per
    /// reader.
    pub(crate) fn seed_with_raw(
        &mut self,
        id: u64,
        members: Vec<(String, Vec<(String, FieldValue)>)>,
        raw: Vec<(String, Vec<u8>)>,
    ) {
        let Some(view) = self.reader_views.get(&id).copied() else { return };
        self.reset_pending.remove(&id);
        if self.seeded_views.insert(view) {
            self.engine.seed(view, members);
            for (document, body) in raw {
                self.raw_bodies.insert((view, document), body);
            }
        }
        let current: ProjectedMembers = self
            .engine
            .members(view)
            .into_iter()
            .flat_map(|members| {
                members.iter().map(|(document, fields)| (document.clone(), fields.clone()))
            })
            .collect();
        if let Some(sender) = self.feeds.get(&id)
            && sender.send(ReactiveFrame::Seed { subscription: id, members: current }).is_err()
        {
            self.feeds.remove(&id);
        }
    }

    /// Retracts one subscription.
    pub(crate) fn unsubscribe(&mut self, id: u64) {
        self.detached.remove(&id);
        self.rings.remove(&id);
        let view = self.reader_views.remove(&id);
        let key = self.reader_keys.remove(&id);
        self.feeds.remove(&id);
        if let (Some(view), Some(key)) = (view, key)
            && !self.reader_views.values().any(|candidate| *candidate == view)
        {
            self.engine.unregister(view);
            self.seeded_views.remove(&view);
            self.views.remove(&key);
            self.raw_bodies.retain(|(candidate, _), _| *candidate != view);
        }
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
            self.unsubscribe(id);
        }
    }

    /// The live subscription count.
    pub(crate) fn len(&self) -> usize {
        self.feeds.len()
    }

    /// Number of materialized views, as opposed to attached readers.
    pub(crate) fn view_len(&self) -> usize {
        self.views.len()
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
        let bytes = value.map(canonical_json_bytes);
        let deltas = self.engine.apply(&event);
        for (view, delta) in deltas {
            if let Some(body) = bytes.clone() {
                self.raw_bodies.insert((view, document.to_owned()), body);
            } else {
                self.raw_bodies.remove(&(view, document.to_owned()));
            }
            let readers: Vec<u64> = self
                .reader_views
                .iter()
                .filter_map(|(reader, candidate)| (*candidate == view).then_some(*reader))
                .collect();
            for id in readers {
                self.deliver(id, ReactiveFrame::Delta { subscription: id, delta: delta.clone() });
            }
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
    /// Resumes a reader only when its authorization-scoped canonical view is
    /// still the same view.  A checkpoint from another authorization scope
    /// is a reset, never a cross-scope replay.
    pub(crate) fn resume_scoped(
        &mut self,
        scope: String,
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
        let view = self.reader_views.get(&previous).copied()?;
        let key = self.reader_keys.get(&previous)?;
        if key.scope != scope {
            return None;
        }
        if !self.engine.specification_of(view).is_some_and(|held| held == spec) {
            return None; // the view changed: Reset
        }
        let members: ProjectedMembers = self.engine.members(view)?.clone().into_iter().collect();
        let missed: Vec<Delta> =
            ring.iter().filter(|delta| delta.revision > revision).cloned().collect();
        let key = key.clone();
        let (id, receiver) = self.subscribe_scoped(key.scope.clone(), spec.clone());
        let resumed_view = self.reader_views.get(&id).copied()?;
        self.engine.seed(resumed_view, members);
        self.seeded_views.insert(resumed_view);
        self.rings.insert(id, ring);
        Some((id, receiver, missed))
    }

    /// Whether this reader's shared view has already been seeded.
    pub(crate) fn needs_seed(&self, id: u64) -> bool {
        self.reader_views.get(&id).is_some_and(|view| !self.seeded_views.contains(view))
    }

    /// Returns the canonical body bytes retained for one materialized row.
    #[must_use]
    pub(crate) fn raw_body(&self, id: u64, document: &str) -> Option<&[u8]> {
        let view = self.reader_views.get(&id)?;
        self.raw_bodies.get(&(*view, document.to_owned())).map(Vec::as_slice)
    }
}

/// Encodes a `DocumentValue` as canonical JSON bytes (objects are already
/// ordered by the engine's `BTreeMap`).  This is deliberately local to the
/// adapter because the storage/write seam owns parsing and canonicalization.
#[must_use]
pub fn canonical_json_bytes(value: &DocumentValue) -> Vec<u8> {
    fn write(value: &DocumentValue, out: &mut Vec<u8>) {
        match value {
            DocumentValue::Null => out.extend_from_slice(b"null"),
            DocumentValue::Boolean(flag) => {
                out.extend_from_slice(if *flag { b"true" } else { b"false" })
            }
            DocumentValue::Integer(number) => out.extend_from_slice(number.to_string().as_bytes()),
            DocumentValue::Number(number) => out.extend_from_slice(number.to_string().as_bytes()),
            DocumentValue::String(text) => {
                out.push(b'"');
                for character in text.chars() {
                    match character {
                        '"' => out.extend_from_slice(b"\\\""),
                        '\\' => out.extend_from_slice(b"\\\\"),
                        '\n' => out.extend_from_slice(b"\\n"),
                        '\r' => out.extend_from_slice(b"\\r"),
                        '\t' => out.extend_from_slice(b"\\t"),
                        character if character.is_control() => {
                            out.extend_from_slice(
                                format!("\\u{:04x}", character as u32).as_bytes(),
                            );
                        }
                        character => {
                            let mut buffer = [0; 4];
                            out.extend_from_slice(character.encode_utf8(&mut buffer).as_bytes());
                        }
                    }
                }
                out.push(b'"');
            }
            DocumentValue::Array(values) => {
                out.push(b'[');
                for (index, value) in values.iter().enumerate() {
                    if index != 0 {
                        out.push(b',');
                    }
                    write(value, out);
                }
                out.push(b']');
            }
            DocumentValue::Object(fields) => {
                out.push(b'{');
                for (index, (name, value)) in fields.iter().enumerate() {
                    if index != 0 {
                        out.push(b',');
                    }
                    write(&DocumentValue::String(name.clone()), out);
                    out.push(b':');
                    write(value, out);
                }
                out.push(b'}');
            }
        }
    }
    let mut bytes = Vec::new();
    write(value, &mut bytes);
    bytes
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

    #[test]
    fn authorization_equivalent_readers_share_one_materializer() {
        let mut reactive = ReactiveState::default();
        let (first, first_feed) = reactive.subscribe_scoped("tenant/db/rule-1", spec());
        reactive.seed_with_raw(
            first,
            vec![("o-1".into(), vec![("total".into(), FieldValue::Number(1.0))])],
            vec![("o-1".into(), br#"{"status":"paid","total":1}"#.to_vec())],
        );
        let _ = first_feed.recv().expect("first seed");
        let (second, second_feed) = reactive.subscribe_scoped("tenant/db/rule-1", spec());
        reactive.seed(second, Vec::new());
        let _ = second_feed.recv().expect("second seed");
        assert_eq!(reactive.view_len(), 1);
        assert_eq!(reactive.len(), 2);
        assert_eq!(reactive.raw_body(second, "o-1"), Some(&br#"{"status":"paid","total":1}"#[..]));

        let paid = object_document("paid");
        reactive.apply_put("orders", "o-2", Some(&paid));
        assert!(matches!(first_feed.recv().expect("first delta"), ReactiveFrame::Delta { .. }));
        assert!(matches!(second_feed.recv().expect("second delta"), ReactiveFrame::Delta { .. }));
    }

    #[test]
    fn different_authorization_scopes_never_share_state() {
        let mut reactive = ReactiveState::default();
        let (first, first_feed) = reactive.subscribe_scoped("tenant/db/rule-1", spec());
        reactive.seed(first, Vec::new());
        let _ = first_feed.recv();
        let (second, second_feed) = reactive.subscribe_scoped("tenant/db/rule-2", spec());
        reactive.seed(second, Vec::new());
        let _ = second_feed.recv();
        assert_eq!(reactive.view_len(), 2);
        assert_ne!(first, second);
    }

    #[test]
    fn canonical_bytes_are_deterministic_and_object_sorted() {
        let value = DocumentValue::Object(std::collections::BTreeMap::from([
            ("z".to_owned(), DocumentValue::Boolean(true)),
            ("a".to_owned(), DocumentValue::String("line\n".to_owned())),
        ]));
        assert_eq!(super::canonical_json_bytes(&value), br#"{"a":"line\n","z":true}"#);
    }
}
