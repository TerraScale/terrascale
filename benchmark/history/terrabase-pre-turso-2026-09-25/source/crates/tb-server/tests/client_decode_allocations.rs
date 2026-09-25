//! Isolated allocation evidence for the bounded local-v1 JSON commit decoder.
//!
//! This binary is intentionally configured with `harness = false`: one thread
//! builds each body, measures the actual decode-plus-`with_writes` probe, and
//! checks its allocator high-water delta against the declared reservation.
//! Counts use requested allocation sizes, excluding allocator metadata, stack
//! storage, and each prebuilt request body. Results are workload evidence, not
//! a proof for every possible request.

#![deny(unsafe_op_in_unsafe_fn)]

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use tb_core::{CollectionId, DocumentId, MAX_VALUE_BYTES, ReplicaId};
use tb_server::{ClientDecodeProbeError, ClientDecodeProbeStats};

const MAX_JSON_BODY_BYTES: usize = 4 * 1024 * 1024;
// One-field encoded documents have 111 bytes of fixed document, field-directory,
// and checksum overhead in addition to this typed field payload.
const NEAR_MAX_FIELD_PAYLOAD_BYTES: usize = MAX_VALUE_BYTES - 128;
const DIGEST: &str = "0000000000000000000000000000000000000000000000000000000000000000";
const NONCE: &str = "00000000000000000000000000000000";
const CHECKSUM_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;

struct TrackingAllocator;

static TRACKING: AtomicBool = AtomicBool::new(false);
static LIVE_BYTES: AtomicUsize = AtomicUsize::new(0);
static PEAK_BYTES: AtomicUsize = AtomicUsize::new(0);
static ACCOUNTING_ERRORS: AtomicUsize = AtomicUsize::new(0);

#[global_allocator]
static ALLOCATOR: TrackingAllocator = TrackingAllocator;

fn account_add(bytes: usize) {
    let previous = LIVE_BYTES
        .fetch_update(Ordering::SeqCst, Ordering::SeqCst, |live| {
            Some(live.saturating_add(bytes))
        })
        .unwrap_or(0);
    let live = previous.saturating_add(bytes);
    if previous.checked_add(bytes).is_none() {
        ACCOUNTING_ERRORS.fetch_add(1, Ordering::Relaxed);
    }
    if TRACKING.load(Ordering::SeqCst) {
        PEAK_BYTES.fetch_max(live, Ordering::SeqCst);
    }
}

fn account_sub(bytes: usize) {
    let previous = LIVE_BYTES.fetch_update(Ordering::SeqCst, Ordering::SeqCst, |live| {
        Some(live.saturating_sub(bytes))
    });
    if previous.is_ok_and(|live| live < bytes) {
        ACCOUNTING_ERRORS.fetch_add(1, Ordering::Relaxed);
    }
}

// SAFETY: Every operation delegates to `System` with the caller's original
// pointer and layout. Accounting uses atomics only and does not inspect or
// alter allocated memory, pointer provenance, alignment, or ownership.
unsafe impl GlobalAlloc for TrackingAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        // SAFETY: The caller of `GlobalAlloc::alloc` provides a valid layout;
        // this wrapper forwards it unchanged to the system allocator.
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() {
            account_add(layout.size());
        }
        pointer
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        // SAFETY: The caller of `GlobalAlloc::alloc_zeroed` provides a valid
        // layout; this wrapper forwards it unchanged to the system allocator.
        let pointer = unsafe { System.alloc_zeroed(layout) };
        if !pointer.is_null() {
            account_add(layout.size());
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: The caller must pass the live pointer and matching original
        // layout required by `GlobalAlloc::dealloc`; both are forwarded intact.
        unsafe { System.dealloc(pointer, layout) };
        account_sub(layout.size());
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        // Precharge the replacement while the original remains counted. This
        // deliberately records the conservative old-plus-new peak even when
        // the system allocator can grow the allocation in place.
        account_add(new_size);
        // SAFETY: The caller must pass a live allocation and its original
        // layout; this wrapper forwards them unchanged with the requested size.
        let replacement = unsafe { System.realloc(pointer, layout, new_size) };
        if replacement.is_null() {
            // A failed realloc retains the original allocation, so roll back
            // only the speculative replacement charge.
            account_sub(new_size);
        } else {
            account_sub(layout.size());
        }
        replacement
    }
}

#[derive(Clone, Copy, Debug)]
enum Outcome {
    Accepted(ClientDecodeProbeStats),
    Rejected(ClientDecodeProbeError),
}

#[derive(Clone, Copy, Debug)]
struct Observation {
    declared_bound: Option<usize>,
    peak_delta: usize,
    retained_delta: usize,
    outcome: Outcome,
}

fn observe(body: &[u8]) -> Observation {
    let baseline = LIVE_BYTES.load(Ordering::SeqCst);
    PEAK_BYTES.store(baseline, Ordering::SeqCst);
    TRACKING.store(true, Ordering::SeqCst);

    let (declared_bound, result) = tb_server::client_decode_allocation_probe(body);
    let peak = PEAK_BYTES.load(Ordering::SeqCst);
    TRACKING.store(false, Ordering::SeqCst);

    let outcome = match result {
        Ok(stats) => Outcome::Accepted(stats),
        Err(error) => Outcome::Rejected(error),
    };
    let retained = LIVE_BYTES.load(Ordering::SeqCst);
    assert_eq!(
        retained, baseline,
        "decoder heap allocations must be released before the probe returns"
    );
    assert_eq!(
        ACCOUNTING_ERRORS.load(Ordering::Relaxed),
        0,
        "allocator accounting overflowed or underflowed"
    );

    Observation {
        declared_bound,
        peak_delta: peak.saturating_sub(baseline),
        retained_delta: retained.saturating_sub(baseline),
        outcome,
    }
}

#[derive(Clone, Copy)]
enum Expected {
    Accepted {
        writes: usize,
        live_writes: usize,
        fields: usize,
        payload_bytes: Option<usize>,
    },
    Rejected {
        error: ClientDecodeProbeError,
        over_body_cap: bool,
    },
}

fn check_case(name: &str, body: &str, expected: Expected) {
    let observation = observe(body.as_bytes());
    assert_eq!(
        observation.retained_delta, 0,
        "{name}: decoder retained requested heap bytes after returning"
    );

    match (expected, observation.outcome) {
        (
            Expected::Accepted {
                writes,
                live_writes,
                fields,
                payload_bytes,
            },
            Outcome::Accepted(stats),
        ) => {
            let bound = observation
                .declared_bound
                .unwrap_or_else(|| panic!("{name}: accepted request has no declared bound"));
            assert!(
                observation.peak_delta <= bound,
                "{name}: measured {} bytes above baseline, declared bound is {bound}",
                observation.peak_delta
            );
            assert_eq!(stats.writes, writes, "{name}: write descriptors visited");
            assert_eq!(
                stats.live_writes, live_writes,
                "{name}: live write descriptors visited"
            );
            assert_eq!(stats.fields, fields, "{name}: borrowed fields visited");
            if let Some(expected_bytes) = payload_bytes {
                assert_eq!(
                    stats.payload_bytes, expected_bytes,
                    "{name}: borrowed field payload bytes visited"
                );
            }
            if stats.payload_bytes == 0 {
                assert_eq!(
                    stats.checksum, CHECKSUM_OFFSET,
                    "{name}: empty payload checksum"
                );
            } else {
                assert_ne!(
                    stats.checksum, CHECKSUM_OFFSET,
                    "{name}: payload bytes were read"
                );
            }
        }
        (
            Expected::Rejected {
                error,
                over_body_cap,
            },
            Outcome::Rejected(actual),
        ) => {
            assert_eq!(actual, error, "{name}: decoder rejection category");
            if over_body_cap {
                assert!(
                    observation.declared_bound.is_none(),
                    "{name}: over-cap body unexpectedly received a decode reservation"
                );
                assert_eq!(
                    observation.peak_delta, 0,
                    "{name}: raw body cap should reject before allocating"
                );
            } else {
                let bound = observation
                    .declared_bound
                    .unwrap_or_else(|| panic!("{name}: bounded rejected request has no bound"));
                assert!(
                    observation.peak_delta <= bound,
                    "{name}: rejected request measured {} bytes above baseline, declared bound is {bound}",
                    observation.peak_delta
                );
            }
        }
        (Expected::Accepted { .. }, Outcome::Rejected(error)) => {
            panic!("{name}: expected acceptance, got {error:?}")
        }
        (Expected::Rejected { error, .. }, Outcome::Accepted(stats)) => {
            panic!("{name}: expected {error:?}, got accepted stats {stats:?}")
        }
    }
}

fn main() {
    warm_decoder();

    let tombstones = many_tombstones(1_024);
    check_case(
        "1024 sorted tombstone writes",
        &tombstones,
        Expected::Accepted {
            writes: 1_024,
            live_writes: 0,
            fields: 0,
            payload_bytes: Some(0),
        },
    );

    let observed = request_with_value(&tombstone_value(), &observed_context(1_024));
    check_case(
        "1024 unique observed origins",
        &observed,
        Expected::Accepted {
            writes: 1,
            live_writes: 0,
            fields: 0,
            payload_bytes: Some(0),
        },
    );

    let dense_arrays = many_repeated_values(64, &null_array_value(64));
    check_case(
        "dense null arrays across 64 writes",
        &dense_arrays,
        Expected::Accepted {
            writes: 64,
            live_writes: 64,
            fields: 64,
            payload_bytes: None,
        },
    );

    let dense_fields = many_writes_with_dense_null_fields(8, 512);
    check_case(
        "4096 dense null fields across 8 writes",
        &dense_fields,
        Expected::Accepted {
            writes: 8,
            live_writes: 8,
            fields: 4_096,
            payload_bytes: Some(0),
        },
    );

    let exact_nodes = node_tree_value(&[1_023, 1_023, 1_023, 1_022]);
    let exact_node_limit = request_with_value(&live_value(&exact_nodes), "[]");
    check_case(
        "exactly 4096 document value nodes",
        &exact_node_limit,
        Expected::Accepted {
            writes: 1,
            live_writes: 1,
            fields: 1,
            payload_bytes: None,
        },
    );

    let over_nodes = node_tree_value(&[1_023, 1_023, 1_023, 1_023]);
    let over_node_limit = request_with_value(&live_value(&over_nodes), "[]");
    check_case(
        "4097th document value node rejected",
        &over_node_limit,
        Expected::Rejected {
            error: ClientDecodeProbeError::Validation,
            over_body_cap: false,
        },
    );

    let depth_16_value = nested_map_value(15);
    let depth_16 = request_with_value(&live_value(&depth_16_value), "[]");
    check_case(
        "value depth 16 accepted",
        &depth_16,
        Expected::Accepted {
            writes: 1,
            live_writes: 1,
            fields: 1,
            payload_bytes: None,
        },
    );

    let depth_17_value = nested_map_value(16);
    let depth_17 = request_with_value(&live_value(&depth_17_value), "[]");
    check_case(
        "value depth 17 rejected",
        &depth_17,
        Expected::Rejected {
            error: ClientDecodeProbeError::Validation,
            over_body_cap: false,
        },
    );

    let base64_body = multiple_large_base64_writes();
    assert!(base64_body.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "two base64 values near the 1 MiB decoded-value limit",
        &base64_body,
        Expected::Accepted {
            writes: 2,
            live_writes: 2,
            fields: 2,
            payload_bytes: Some(2 * NEAR_MAX_FIELD_PAYLOAD_BYTES),
        },
    );

    let escaped_utf8 = long_escaped_utf8_and_keys();
    assert!(escaped_utf8.0.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "long escaped UTF-8 scalar and escaped recognized keys",
        &escaped_utf8.0,
        Expected::Accepted {
            writes: 1,
            live_writes: 1,
            fields: 1,
            payload_bytes: Some(escaped_utf8.1),
        },
    );

    let near_cap = near_body_cap_valid_utf8();
    assert!(near_cap.len() > 3 * MAX_JSON_BODY_BYTES / 4);
    assert!(near_cap.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "valid UTF-8 writes near the request body cap",
        &near_cap,
        Expected::Accepted {
            writes: 4,
            live_writes: 4,
            fields: 4,
            payload_bytes: Some(3_600_000),
        },
    );

    let unknown_tombstone = unknown_large_value_at_tombstone();
    assert!(unknown_tombstone.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "3.3 MiB ignored unknown value on tombstone",
        &unknown_tombstone,
        Expected::Rejected {
            error: ClientDecodeProbeError::Validation,
            over_body_cap: false,
        },
    );

    let unknown_nested = unknown_large_value_at_nested_null();
    assert!(unknown_nested.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "3.3 MiB ignored unknown value on nested null",
        &unknown_nested,
        Expected::Rejected {
            error: ClientDecodeProbeError::Validation,
            over_body_cap: false,
        },
    );

    let overflow = oversized_array_with_large_ignored_item();
    assert!(overflow.len() > 3 * MAX_JSON_BODY_BYTES / 4);
    assert!(overflow.len() < MAX_JSON_BODY_BYTES);
    check_case(
        "1025th array item with near-cap ignored payload",
        &overflow,
        Expected::Rejected {
            error: ClientDecodeProbeError::Validation,
            over_body_cap: false,
        },
    );

    let over_body_cap = body_over_request_cap();
    assert_eq!(over_body_cap.len(), MAX_JSON_BODY_BYTES + 1);
    check_case(
        "request body cap plus one byte",
        &over_body_cap,
        Expected::Rejected {
            error: ClientDecodeProbeError::Capacity,
            over_body_cap: true,
        },
    );
}

fn warm_decoder() {
    let body = request_with_value(&tombstone_value(), "[]");
    let (bound, result) = tb_server::client_decode_allocation_probe(body.as_bytes());
    assert!(bound.is_some());
    assert!(matches!(result, Ok(stats) if stats.writes == 1));
}

fn many_tombstones(count: usize) -> String {
    let collection = collection_id();
    let observed = "[]";
    let value = tombstone_value();
    let mut body = request_start();
    for index in 1..=count {
        let document = document_id(index);
        append_write(
            &mut body,
            index == 1,
            &collection,
            &document,
            observed,
            &value,
        );
    }
    request_finish(body)
}

fn many_repeated_values(count: usize, value: &str) -> String {
    let collection = collection_id();
    let observed = "[]";
    let mut body = request_start();
    for index in 1..=count {
        let document = document_id(index);
        let live = live_value(value);
        append_write(
            &mut body,
            index == 1,
            &collection,
            &document,
            observed,
            &live,
        );
    }
    request_finish(body)
}

fn many_writes_with_dense_null_fields(writes: usize, fields_per_write: usize) -> String {
    let collection = collection_id();
    let observed = "[]";
    let mut body = request_start();
    for index in 1..=writes {
        let document = document_id(index);
        let mut fields = String::with_capacity(fields_per_write * 32);
        for field_id in 1..=fields_per_write {
            if field_id > 1 {
                fields.push(',');
            }
            fields.push_str("{\"id\":");
            fields.push_str(&field_id.to_string());
            fields.push_str(",");
            fields.push_str(r#""value":{"type":"null"}}"#);
        }
        let value = live_value_with_fields(&fields);
        append_write(
            &mut body,
            index == 1,
            &collection,
            &document,
            observed,
            &value,
        );
    }
    request_finish(body)
}

fn request_with_value(value: &str, observed: &str) -> String {
    let collection = collection_id();
    let document = document_id(1);
    let write_value = value.to_owned();
    let mut body = request_start();
    append_write(
        &mut body,
        true,
        &collection,
        &document,
        observed,
        &write_value,
    );
    request_finish(body)
}

fn request_start() -> String {
    let mut body = String::from(
        r#"{"version":1,"transaction_id":{"expires_at_unix_ms":"9999999999999","nonce":""#,
    );
    body.push_str(NONCE);
    body.push_str(r#""},"writes":["#);
    body
}

fn request_finish(mut body: String) -> String {
    body.push_str("]}");
    body
}

fn append_write(
    body: &mut String,
    first: bool,
    collection: &str,
    document: &str,
    observed: &str,
    value: &str,
) {
    if !first {
        body.push(',');
    }
    body.push_str("{\"collection\":\"");
    body.push_str(collection);
    body.push_str("\",\"document\":\"");
    body.push_str(document);
    body.push_str("\",\"schema_generation\":\"1\",\"schema_digest\":\"");
    body.push_str(DIGEST);
    body.push_str("\",\"observed\":");
    body.push_str(observed);
    body.push_str(",\"value\":");
    body.push_str(value);
    body.push('}');
}

fn collection_id() -> String {
    CollectionId::from_bytes([0x12; 16]).to_string()
}

fn document_id(number: usize) -> String {
    let mut bytes = [0; 16];
    bytes[8..].copy_from_slice(&(number as u64).to_be_bytes());
    DocumentId::from_bytes(bytes).to_string()
}

fn replica_id(number: usize) -> String {
    let mut bytes = [0x21; 16];
    bytes[8..].copy_from_slice(&(number as u64).to_be_bytes());
    ReplicaId::from_bytes(bytes).to_string()
}

fn tombstone_value() -> String {
    String::from(r#"{"kind":"tombstone"}"#)
}

fn live_value(typed_value: &str) -> String {
    let mut value = String::from(r#"{"kind":"live","fields":[{"id":1,"value":"#);
    value.push_str(typed_value);
    value.push_str("}]}");
    value
}

fn live_value_with_fields(fields: &str) -> String {
    let mut value = String::from(r#"{"kind":"live","fields":["#);
    value.push_str(fields);
    value.push_str("]}");
    value
}

fn observed_context(entries: usize) -> String {
    let mut observed = String::from("[");
    for index in 1..=entries {
        if index > 1 {
            observed.push(',');
        }
        observed.push_str(r#"{"replica":"#);
        observed.push_str(&replica_id(index));
        observed.push_str(r#"","incarnation":"11111111111111111111111111111111","sequence":"#);
        observed.push_str(&index.to_string());
        observed.push_str(r#""}"#);
    }
    observed.push(']');
    observed
}

fn null_array_value(items: usize) -> String {
    let mut encoded_items = String::with_capacity(items * 15);
    for index in 0..items {
        if index != 0 {
            encoded_items.push(',');
        }
        encoded_items.push_str(r#"{"type":"null"}"#);
    }
    typed_array_value(&encoded_items)
}

fn typed_array_value(items: &str) -> String {
    let mut value = String::from(r#"{"type":"array","items":["#);
    value.push_str(items);
    value.push_str("]}");
    value
}

fn node_tree_value(leaf_counts: &[usize]) -> String {
    let mut child_maps = String::new();
    for (index, leaves) in leaf_counts.iter().enumerate() {
        if index != 0 {
            child_maps.push(',');
        }
        let fields = null_map_fields(*leaves);
        append_map_field(&mut child_maps, index + 1, &map_value(&fields));
    }
    map_value(&child_maps)
}

fn map_value(fields: &str) -> String {
    let mut value = String::from(r#"{"type":"map","fields":["#);
    value.push_str(fields);
    value.push_str("]}");
    value
}

fn null_map_fields(count: usize) -> String {
    let mut fields = String::new();
    for field_id in 1..=count {
        if field_id > 1 {
            fields.push(',');
        }
        append_map_field(&mut fields, field_id, r#"{"type":"null"}"#);
    }
    fields
}

fn append_map_field(fields: &mut String, field_id: usize, value: &str) {
    fields.push_str("{\"id\":");
    fields.push_str(&field_id.to_string());
    fields.push_str(",\"value\":");
    fields.push_str(value);
    fields.push('}');
}

fn nested_map_value(map_levels: usize) -> String {
    let mut value = String::from(r#"{"type":"null"}"#);
    for _ in 0..map_levels {
        let field = {
            let mut field = String::new();
            append_map_field(&mut field, 1, &value);
            field
        };
        value = map_value(&field);
    }
    value
}

fn multiple_large_base64_writes() -> String {
    let payload = vec![0xa5; NEAR_MAX_FIELD_PAYLOAD_BYTES];
    let encoded = URL_SAFE_NO_PAD.encode(&payload);
    let typed = scalar_typed_value("bytes", &format!("\"{encoded}\""));
    let value = live_value(&typed);
    let collection = collection_id();
    let mut body = request_start();
    for index in 1..=2 {
        let document = document_id(index);
        append_write(&mut body, index == 1, &collection, &document, "[]", &value);
    }
    request_finish(body)
}

fn long_escaped_utf8_and_keys() -> (String, usize) {
    let scalar = "quote=\" slash=\\ nul=\0 utf8=🦀".repeat(20_000);
    let payload_bytes = scalar.len();
    let encoded_scalar = serde_json::to_string(&scalar).expect("encode escaped UTF-8 scalar");
    let typed = scalar_typed_value("utf8", &encoded_scalar);
    let value = live_value(&typed);
    let mut body = request_with_value(&value, "[]");
    for (plain, escaped) in [
        ("version", "\\u0076ersion"),
        ("transaction_id", "transaction\\u005fid"),
        ("writes", "wr\\u0069tes"),
        ("collection", "collect\\u0069on"),
        ("value", "val\\u0075e"),
        ("type", "t\\u0079pe"),
    ] {
        body = body.replace(&format!("\"{plain}\""), &format!("\"{escaped}\""));
    }
    (body, payload_bytes)
}

fn near_body_cap_valid_utf8() -> String {
    let payload = "a".repeat(900_000);
    let typed = scalar_typed_value("utf8", &format!("\"{payload}\""));
    let value = live_value(&typed);
    let collection = collection_id();
    let mut body = request_start();
    for index in 1..=4 {
        let document = document_id(index);
        append_write(&mut body, index == 1, &collection, &document, "[]", &value);
    }
    request_finish(body)
}

fn large_numeric_array(items: usize) -> String {
    let mut values = String::with_capacity(items * 2);
    for index in 0..items {
        if index != 0 {
            values.push(',');
        }
        values.push('0');
    }
    let mut array = String::with_capacity(values.len() + 2);
    array.push('[');
    array.push_str(&values);
    array.push(']');
    array
}

fn unknown_large_value_at_tombstone() -> String {
    let ignored = large_numeric_array(1_730_000);
    let mut value = String::from(r#"{"kind":"tombstone","extra":"#);
    value.push_str(&ignored);
    value.push('}');
    request_with_value(&value, "[]")
}

fn unknown_large_value_at_nested_null() -> String {
    let ignored = large_numeric_array(1_730_000);
    let mut value =
        String::from(r#"{"kind":"live","fields":[{"id":1,"value":{"type":"null","extra":"#);
    value.push_str(&ignored);
    value.push_str("}}]}");
    request_with_value(&value, "[]")
}

fn oversized_array_with_large_ignored_item() -> String {
    let mut items = String::with_capacity(3_950_000);
    for index in 0..1_024 {
        if index != 0 {
            items.push(',');
        }
        items.push_str(r#"{"type":"null"}"#);
    }
    let ignored = large_numeric_array(1_950_000);
    if !items.is_empty() {
        items.push(',');
    }
    items.push_str(r#"{"ignored":"#);
    items.push_str(&ignored);
    items.push('}');
    let typed = {
        let mut value = String::from(r#"{"type":"array","items":["#);
        value.push_str(&items);
        value.push_str("]}");
        value
    };
    request_with_value(&live_value(&typed), "[]")
}

fn scalar_typed_value(value_type: &str, encoded_scalar: &str) -> String {
    let mut value = String::from(r#"{"type":"#);
    value.push_str(value_type);
    value.push_str(r#"","value":"#);
    value.push_str(encoded_scalar);
    value.push('}');
    value
}

fn body_over_request_cap() -> String {
    let mut body = request_with_value(&tombstone_value(), "[]");
    body.resize(MAX_JSON_BODY_BYTES + 1, ' ');
    body
}
