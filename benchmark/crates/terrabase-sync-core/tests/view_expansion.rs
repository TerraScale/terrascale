//! Subscription-expansion lane for the native result-view state machine
//! (qualification gap sweep row 18).
//!
//! Charter contract under test (GOAL.md `View checkpoints`): a widened
//! subscription creates a new view generation; `TerraBase` computes an
//! authorized diff when bounded and safe, otherwise sends a new snapshot;
//! it never skips newly visible records because an older cursor scanned
//! past their mutations; and permission loss emits removals before the new
//! generation advances.
//!
//! This file drives the transport-independent state machine only — it is
//! not the serving surface.

use std::collections::BTreeMap;

use terrabase_sync_core::native::{
    ApplyOutcome, DeliveryQueue, ExpansionRequest, GenerationAdvance, Limits, ProjectedValue,
    QueueOutcome, QueuedDelivery, ResetReason, ResultDelta, ResultItem, ResultOperation,
    ResumeDecision, ScopedItem, Snapshot, ViewError, ViewState,
};

/// The causal position the old checkpoint's cursor scanned through. Records
/// whose latest mutation is at or before this position are the ones an
/// older cursor would skip.
const SCAN_FRONTIER: u64 = 100;

fn field_value(body: &str) -> ProjectedValue {
    BTreeMap::from([("v".to_owned(), body.as_bytes().to_vec())])
}

fn item(name: &str, body: &str) -> ResultItem {
    ResultItem::new(name, field_value(body))
}

fn scoped(name: &str, body: &str, position: usize, last_mutation_position: u64) -> ScopedItem {
    ScopedItem { item: item(name, body), position, last_mutation_position }
}

fn limits(max_operations: usize) -> Limits {
    Limits { max_items: 1000, max_value_bytes: 4096, max_operations }
}

fn bootstrap_pair(names: &[&str], lim: Limits) -> (ViewState, ViewState) {
    let snapshot = Snapshot::new(
        1,
        1,
        0,
        names.iter().map(|name| item(name, &format!("{name}-v0"))).collect(),
        1,
        1_000,
    );
    let mut server = ViewState::new(lim);
    let mut client = ViewState::new(lim);
    server.bootstrap(snapshot.clone()).unwrap();
    client.bootstrap(snapshot).unwrap();
    (server, client)
}

#[test]
fn expansion_makes_pre_checkpoint_mutations_visible() {
    let (mut server, mut client) = bootstrap_pair(&["a", "b"], limits(8));
    let stale = server.checkpoint().unwrap().clone();

    // Both records are newly in scope after the widening. `settled-old`'s
    // latest mutation is at causal position 5, long before the old
    // checkpoint's cursor scanned through position 100: a cursor resuming
    // from that checkpoint would never see a mutation for it.
    let delivery = server
        .expand_subscription(
            &ExpansionRequest {
                next_view_generation: 2,
                authorization_generation: 1,
                newly_visible: vec![
                    scoped("settled-old", "settled-old-current", 2, 5),
                    scoped("fresh", "fresh-current", 3, 120),
                ],
                no_longer_visible: Vec::new(),
                scan_frontier: SCAN_FRONTIER,
                retention_generation: 1,
                expires_at: 1_000,
            },
            &mut DeliveryQueue::new(16, 1 << 20),
        )
        .unwrap();

    // Bounded diff: two insert operations in the new view generation.
    assert!(delivery.removals.is_none());
    let GenerationAdvance::Diff(delta) = &delivery.advance else {
        panic!("a two-record diff is bounded and must be delivered as a diff");
    };
    assert_eq!(delta.view_generation, 2);
    assert_eq!(delta.from_revision, 0);
    assert_eq!(delta.to_revision, 1);
    assert_eq!(delta.operations.len(), 2);
    assert!(matches!(
        &delta.operations[0],
        ResultOperation::Insert { identity, position: 2, .. } if identity == "settled-old"
    ));

    client.apply_expansion(&delivery).unwrap();
    let identities = client.items().iter().map(|entry| entry.identity.as_str()).collect::<Vec<_>>();
    assert_eq!(identities, ["a", "b", "settled-old", "fresh"]);
    // The pre-checkpoint record is visible with its CURRENT value — the
    // skip class the charter forbids.
    assert_eq!(client.items()[2].value["v"], b"settled-old-current");
    assert_eq!(client.items()[3].value["v"], b"fresh-current");

    // Producer and consumer agree on generation, revision, and digest.
    assert_eq!(client.view_generation(), server.view_generation());
    assert_eq!(client.revision(), server.revision());
    assert_eq!(client.digest(), server.digest());
    assert_eq!(client.items(), server.items());

    // The pre-expansion checkpoint can never resume into the new view: the
    // client must be answered with a reset, never a replay that skips the
    // newly visible records.
    assert_eq!(
        server.resume(&stale, 5, 1),
        ResumeDecision::ResetRequired(ResetReason::CheckpointNotCurrent)
    );
}

#[test]
fn unbounded_expansion_resets_and_never_skips() {
    // Five newly visible records against a two-operation budget: the
    // authorized diff cannot be bounded, so a snapshot must replace it.
    let (mut server, mut client) = bootstrap_pair(&["a", "b"], limits(3));
    let newly_visible = vec![
        scoped("w0", "w0-current", 2, 3),
        scoped("w1", "w1-current", 3, 7),
        scoped("w2", "w2-current", 4, 11),
        scoped("w3", "w3-current", 5, 13),
        scoped("w4", "w4-current", 6, 17),
    ];
    let mut queue = DeliveryQueue::new(16, 1 << 20);
    let delivery = server
        .expand_subscription(
            &ExpansionRequest {
                next_view_generation: 2,
                authorization_generation: 1,
                newly_visible,
                no_longer_visible: Vec::new(),
                scan_frontier: SCAN_FRONTIER,
                retention_generation: 1,
                expires_at: 1_000,
            },
            &mut queue,
        )
        .unwrap();

    let GenerationAdvance::Reset(snapshot) = &delivery.advance else {
        panic!("a five-record diff against a three-operation budget must reset");
    };
    assert_eq!(snapshot.view_generation, 2);
    assert_eq!(snapshot.items.len(), 7);
    for name in ["w0", "w1", "w2", "w3", "w4"] {
        let found = snapshot.items.iter().find(|entry| entry.identity == name);
        assert!(found.is_some(), "the reset snapshot skipped newly visible record {name}");
        assert_eq!(found.unwrap().value["v"], format!("{name}-current").as_bytes());
    }

    // The coalescing queue holds the snapshot as its sole delivery.
    assert_eq!(queue.pop(), Some(QueuedDelivery::Reset(snapshot.clone())));
    assert!(queue.is_empty());

    client.apply_expansion(&delivery).unwrap();
    assert_eq!(client.items(), server.items());
    for name in ["w0", "w1", "w2", "w3", "w4"] {
        assert!(
            client.items().iter().any(|entry| entry.identity == name),
            "the consumer skipped newly visible record {name} after the reset"
        );
    }
    assert_eq!(client.view_generation(), 2);
}

#[test]
fn coalescing_queue_replaces_a_bounded_diff_with_a_snapshot() {
    // The diff itself is bounded, but a full backlog trips the queue's
    // revision bound: the coalescing machinery must deliver its reset
    // snapshot instead, and the record must still never be skipped.
    let (mut server, mut client) = bootstrap_pair(&["a", "b"], limits(8));
    let mut queue = DeliveryQueue::new(2, 1 << 20);

    // Fill the backlog with two applied no-op revisions.
    for revision in 1..=2u64 {
        let result = server.items().to_vec();
        let delta = ResultDelta::new(1, revision - 1, revision, Vec::new(), &result, 1, 1_000);
        let fallback = Snapshot::new(1, 1, revision, result, 1, 1_000);
        assert_eq!(queue.push(delta.clone(), fallback), QueueOutcome::Queued);
        assert_eq!(server.apply_delta(&delta), ApplyOutcome::Applied);
        assert_eq!(client.apply_delta(&delta), ApplyOutcome::Applied);
    }

    let delivery = server
        .expand_subscription(
            &ExpansionRequest {
                next_view_generation: 2,
                authorization_generation: 1,
                newly_visible: vec![scoped("x", "x-current", 0, 5)],
                no_longer_visible: Vec::new(),
                scan_frontier: SCAN_FRONTIER,
                retention_generation: 1,
                expires_at: 1_000,
            },
            &mut queue,
        )
        .unwrap();
    assert!(matches!(delivery.advance, GenerationAdvance::Reset(_)));
    client.apply_expansion(&delivery).unwrap();
    assert!(client.items().iter().any(|entry| entry.identity == "x"));
    assert_eq!(client.items(), server.items());
    assert_eq!(client.revision(), server.revision());
}

#[test]
fn expansion_with_concurrent_revocation_removes_before_advancing() {
    let (mut server, mut client) = bootstrap_pair(&["a", "secret", "b"], limits(8));
    let stale = client.checkpoint().unwrap().clone();

    let delivery = server
        .expand_subscription(
            &ExpansionRequest {
                next_view_generation: 2,
                // The widening rides on a policy change.
                authorization_generation: 2,
                newly_visible: vec![scoped("granted", "granted-current", 2, 4)],
                no_longer_visible: vec!["secret".into(), "ghost".into()],
                scan_frontier: SCAN_FRONTIER,
                retention_generation: 1,
                expires_at: 1_000,
            },
            &mut DeliveryQueue::new(16, 1 << 20),
        )
        .unwrap();

    // Removals are emitted in the OLD view generation, before it advances.
    // The absent `ghost` is ignored.
    let removals = delivery.removals.as_ref().unwrap();
    assert_eq!(removals.view_generation, 1);
    assert_eq!(removals.from_revision, 0);
    assert_eq!(removals.to_revision, 1);
    assert_eq!(removals.operations, vec![ResultOperation::Remove { identity: "secret".into() }]);

    // The consumer observes the revocation while still in the old
    // generation — before the generation advances.
    assert_eq!(client.apply_delta(removals), ApplyOutcome::Applied);
    assert_eq!(client.view_generation(), 1);
    let after_removal =
        client.items().iter().map(|entry| entry.identity.as_str()).collect::<Vec<_>>();
    assert_eq!(after_removal, ["a", "b"]);

    // The full delivery still applies (removals are now a duplicate), and
    // the pre-checkpoint record becomes visible in the new generation.
    client.apply_expansion(&delivery).unwrap();
    let identities = client.items().iter().map(|entry| entry.identity.as_str()).collect::<Vec<_>>();
    assert_eq!(identities, ["a", "b", "granted"]);
    assert_eq!(client.items()[2].value["v"], b"granted-current");
    assert_eq!(client.view_generation(), 2);
    assert_eq!(client.authorization_generation(), 2);
    assert_eq!(client.items(), server.items());

    // The revoked record cannot return through the stale checkpoint, and a
    // replayed expansion is a typed refusal, never a re-application.
    assert_eq!(
        server.resume(&stale, 5, 1),
        ResumeDecision::ResetRequired(ResetReason::CheckpointNotCurrent)
    );
    // The replay's removals delta names the old generation the consumer
    // has already left: a typed refusal, never a re-application.
    assert_eq!(client.apply_expansion(&delivery), Err(ViewError::GenerationChanged));
    assert!(
        client.items().iter().all(|entry| entry.identity != "secret"),
        "the revoked record returned after the expansion"
    );
}

/// Deterministic xorshift64* generator: property-shaped scenarios without
/// adding a fuzzing dependency to the crate.
struct DeterministicRng(u64);

impl DeterministicRng {
    fn next_u64(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.0 = x;
        x.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next_u64() % bound
    }
}

#[test]
#[allow(clippy::too_many_lines)]
fn expansion_property_scenarios() {
    let mut pre_frontier_delivered = 0usize;
    for iteration in 0..800u64 {
        let mut rng = DeterministicRng(0x5eed_0000_0018_0000 ^ (iteration * 2 + 1));
        let max_operations = 1 + usize::try_from(rng.below(6)).unwrap();
        let lim = limits(max_operations);

        let existing: Vec<String> = (0..rng.below(6)).map(|k| format!("e{k}")).collect();
        let (mut server, mut client) =
            bootstrap_pair(&existing.iter().map(String::as_str).collect::<Vec<_>>(), lim);

        // Concurrent revocations are a random subset of the current result.
        let mut no_longer_visible: Vec<String> =
            existing.iter().filter(|_| rng.below(4) == 0).cloned().collect();
        // Occasionally one revoked record is simultaneously re-scoped by the
        // widening: it is removed from the old position and reinserted with
        // its new authorized value — the widened scope wins.
        let rescoped_name = if !existing.is_empty() && rng.below(8) == 0 {
            let name = existing
                [usize::try_from(rng.below(u64::try_from(existing.len()).unwrap())).unwrap()]
            .clone();
            if !no_longer_visible.contains(&name) {
                no_longer_visible.push(name.clone());
            }
            Some(name)
        } else {
            None
        };
        let removal_count = no_longer_visible.len();

        let scoped_count = usize::try_from(rng.below(9)).unwrap();
        let base_len = existing.len() - removal_count;
        let mut newly_visible = Vec::with_capacity(scoped_count);
        for j in 0..scoped_count {
            let rescoped = j == 0 && rescoped_name.is_some();
            let name = if rescoped { rescoped_name.clone().unwrap() } else { format!("n{j}") };
            let body =
                if rescoped { format!("{name}-rescoped") } else { format!("{name}-current") };
            // A position that is always valid where the insert applies.
            let position =
                usize::try_from(rng.below(u64::try_from(base_len + j + 1).unwrap())).unwrap();
            // Half the mutations precede the old checkpoint's frontier.
            let last_mutation_position = rng.below(2 * SCAN_FRONTIER);
            newly_visible.push(ScopedItem {
                item: ResultItem::new(name, field_value(&body)),
                position,
                last_mutation_position,
            });
        }
        let scoped_identities =
            newly_visible.iter().map(|s| s.item.identity.clone()).collect::<Vec<_>>();

        let mut queue = DeliveryQueue::new(1024, 1 << 30);
        let delivery = server
            .expand_subscription(
                &ExpansionRequest {
                    next_view_generation: 2,
                    authorization_generation: 1 + rng.below(3),
                    newly_visible: newly_visible.clone(),
                    no_longer_visible: no_longer_visible.clone(),
                    scan_frontier: SCAN_FRONTIER,
                    retention_generation: 1,
                    expires_at: 1_000,
                },
                &mut queue,
            )
            .unwrap();
        client.apply_expansion(&delivery).unwrap();

        // Producer and consumer converge exactly.
        assert_eq!(client.view_generation(), server.view_generation());
        assert_eq!(client.revision(), server.revision());
        assert_eq!(client.digest(), server.digest());
        assert_eq!(client.items(), server.items());

        // The delivery shape matches bounded-ness: a bounded diff is a
        // delta; an unbounded one is a snapshot that still carries every
        // newly visible record.
        let bounded = removal_count <= max_operations && scoped_count <= max_operations;
        match (&delivery.advance, bounded) {
            (GenerationAdvance::Diff(_), true) => {}
            (GenerationAdvance::Reset(snapshot), false) => {
                for name in &scoped_identities {
                    assert!(
                        snapshot.items.iter().any(|entry| &entry.identity == name),
                        "iteration {iteration}: the reset skipped {name}"
                    );
                }
            }
            (shape, bounded) => {
                panic!("iteration {iteration}: shape/bounded mismatch ({shape:?}/{bounded})");
            }
        }

        // Removals ride the old generation whenever they are emitted.
        if let Some(removals) = &delivery.removals {
            assert_eq!(removals.view_generation, 1);
        }
        assert_eq!(delivery.removals.is_some(), removal_count > 0 && bounded);

        // The never-skip invariant, record by record.
        for record in &newly_visible {
            let found = client
                .items()
                .iter()
                .find(|entry| entry.identity == record.item.identity)
                .unwrap_or_else(|| {
                    panic!("iteration {iteration}: skipped {}", record.item.identity)
                });
            assert_eq!(found.value, record.item.value);
            if record.last_mutation_position <= SCAN_FRONTIER {
                pre_frontier_delivered += 1;
            }
        }

        // Revocations stick unless the widened scope re-included the record.
        for identity in &no_longer_visible {
            if !scoped_identities.contains(identity) {
                assert!(
                    client.items().iter().all(|entry| &entry.identity != identity),
                    "iteration {iteration}: revoked {identity} is still visible"
                );
            }
        }
    }
    assert!(
        pre_frontier_delivered > 0,
        "the property loop never exercised the pre-checkpoint skip class"
    );
}
