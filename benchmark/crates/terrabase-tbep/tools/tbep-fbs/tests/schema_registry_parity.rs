//! Schema ↔ registry parity for the private TBEP FlatBuffers surface
//! (ADR 0025, gap-sweep row 27).
//!
//! `schema/tbep.fbs` and `schema/tbep-registry.json` are two checked-in
//! authorities that must say the same thing: the registry owns the
//! wire-authoritative operation codes and the written budgets, and the
//! schema owns the FlatBuffers declaration that the generated bindings
//! are compiled from. Nothing else reconciles them — this test does.
//!
//! A schema edit without the registry (or the reverse), a reordered
//! `TbepFrame` union member (which silently renumbers every structural
//! union tag), a renamed body table, or a changed budget each fail a
//! named test here, independently of the digest and regeneration gates in
//! `tests/fbs_drift.rs`.

use std::collections::HashMap;

use serde_json::Value;
use tbep_fbs::production::{CONTROL_FRAME_BYTES, LARGE_OPERATION_FRAME_BYTES, OPERATION_FRAME_BYTES};
use tbep_fbs::schema_identity::{PinnedInputs, REGISTRY_PATH};
use tbep_fbs::tbep_generated::terrabase::tbep as fbs;
use tbep_fbs::{MAX_FRAME_BYTES, body_kind_for};

fn crate_root() -> std::path::PathBuf {
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn registry() -> Value {
    // Reading through `PinnedInputs` keeps the parity suite and the
    // digest gate pinned to the very same bytes.
    let inputs = PinnedInputs::load(&crate_root()).expect("the pinned inputs read");
    let path = crate_root().join(REGISTRY_PATH);
    assert_eq!(
        inputs.registry.len(),
        std::fs::read(&path).expect("the registry reads").len(),
        "the parity suite reads exactly the pinned registry bytes"
    );
    serde_json::from_slice(&inputs.registry).expect("the registry is JSON")
}

/// `snake_case` registry name → the generated Rust identifier casing.
fn camel(name: &str) -> String {
    name.split('_').map(|part| {
        let mut characters = part.chars();
        match characters.next() {
            Some(first) => first.to_uppercase().collect::<String>() + characters.as_str(),
            None => String::new(),
        }
    }).collect()
}

/// `snake_case` registry name → the generated `SCREAMING_SNAKE` variant.
fn screaming(name: &str) -> String {
    name.to_ascii_uppercase()
}

/// One registry frame row: its wire code and the body table the schema
/// must declare for it.
struct FrameRow {
    code: u16,
    body: String,
}

/// Every frame the registry registers, in registry order: the three
/// channel frames, then every operation's request and response, then the
/// notifications. Channel frames and notifications carry a `Frame` suffix
/// in the schema; operation bodies use the registry type name verbatim.
fn registry_rows(registry: &Value) -> Vec<FrameRow> {
    let mut rows = Vec::new();
    for frame in registry["channel_frames"].as_array().expect("channel_frames") {
        rows.push(FrameRow {
            code: frame["code"].as_u64().expect("code") as u16,
            body: format!("{}Frame", camel(frame["name"].as_str().expect("name"))),
        });
    }
    for operation in registry["operations"].as_array().expect("operations") {
        // The request carries the operation's own code; only the response
        // repeats a code of its own.
        let request_code = operation["code"].as_u64().expect("operation code") as u16;
        rows.push(FrameRow {
            code: request_code,
            body: camel(operation["request"]["name"].as_str().expect("request name")),
        });
        rows.push(FrameRow {
            code: operation["response"]["code"].as_u64().expect("response code") as u16,
            body: camel(operation["response"]["name"].as_str().expect("response name")),
        });
    }
    for notification in registry["notifications"].as_array().expect("notifications") {
        rows.push(FrameRow {
            code: notification["code"].as_u64().expect("code") as u16,
            body: format!("{}Frame", camel(notification["name"].as_str().expect("name"))),
        });
    }
    rows
}

#[test]
fn every_registered_code_maps_to_the_declared_body_table() {
    let registry = registry();
    for row in registry_rows(&registry) {
        let operation = fbs::TbepOperation(row.code);
        let member = body_kind_for(operation).unwrap_or_else(|| {
            panic!("registry code {} has no body member in the operation map", row.code)
        });
        let name = member
            .variant_name()
            .unwrap_or_else(|| panic!("registry code {} maps to an unnamed union member", row.code));
        assert_eq!(
            name, row.body,
            "registry code {} must ride in the schema table the registry names",
            row.code
        );
    }
}

#[test]
fn the_operation_map_is_total_over_the_registry_and_nothing_else() {
    let registry = registry();
    let registered: HashMap<u16, ()> =
        registry_rows(&registry).iter().map(|row| (row.code, ())).collect();
    let mut mapped = 0_u32;
    for code in 0_u16..=u16::MAX {
        let known = body_kind_for(fbs::TbepOperation(code)).is_some();
        assert_eq!(
            known,
            registered.contains_key(&code),
            "code {code} is mapped by the receiver but not by the registry, or the reverse"
        );
        mapped += u32::from(known);
    }
    assert_eq!(
        mapped,
        registry_rows(&registry).len() as u32,
        "the operation map and the registry register a different number of frames"
    );
}

#[test]
fn the_body_union_declaration_order_is_the_registry_order() {
    // `flatc` assigns union tags by declaration order and the schema has
    // no syntax to make them explicit, so the order *is* the wire
    // contract. The generated `ENUM_VALUES_TBEP_FRAME` array is emitted
    // in declaration order, which makes the pin checkable.
    let registry = registry();
    let expected = registry_rows(&registry);
    let declared = fbs::ENUM_VALUES_TBEP_FRAME;
    assert_eq!(declared.len(), expected.len() + 1, "NONE plus one member per frame");
    assert_eq!(declared[0], fbs::TbepFrame::NONE);
    for (tag, row) in expected.iter().enumerate() {
        assert_eq!(
            declared[tag + 1].variant_name(),
            Some(row.body.as_str()),
            "union tag {} must be {} (registry order); reordering the TbepFrame \
             union silently renumbers every structural tag",
            tag + 1,
            row.body
        );
    }
}

#[test]
fn the_registry_error_codes_and_the_schema_enum_agree() {
    let registry = registry();
    let declared = fbs::ENUM_VALUES_ERROR_CODE;
    let errors = registry["errors"].as_array().expect("errors");
    assert_eq!(declared.len(), errors.len() + 1, "NONE plus one variant per error");
    for (variant, error) in declared.iter().skip(1).zip(errors.iter()) {
        assert_eq!(
            variant.variant_name(),
            Some(screaming(error["name"].as_str().expect("name")).as_str()),
            "error code {} is declared with a different name than the registry",
            error["code"]
        );
        assert_eq!(variant.0, error["code"].as_u64().expect("code") as u16);
    }
}

#[test]
fn the_receiver_ceilings_are_the_written_registry_limits() {
    let registry = registry();
    assert_eq!(
        MAX_FRAME_BYTES,
        registry["max_frame_bytes"].as_u64().expect("max_frame_bytes") as usize,
        "the receiver's global frame ceiling must be the registry's written ceiling"
    );
    let budgets: HashMap<String, u64> = registry["budgets"]
        .as_array()
        .expect("budgets")
        .iter()
        .map(|budget| {
            (
                budget["class"].as_str().expect("class").to_owned(),
                budget["wire_bytes"].as_u64().expect("wire_bytes"),
            )
        })
        .collect();
    assert_eq!(
        CONTROL_FRAME_BYTES,
        budgets["control"] as usize,
        "the control budget must be the registry's written control budget"
    );
    assert_eq!(
        OPERATION_FRAME_BYTES,
        budgets["operation"] as usize,
        "the ordinary-operation budget must be the registry's written budget"
    );
    assert_eq!(
        LARGE_OPERATION_FRAME_BYTES,
        budgets["operation_large"] as usize,
        "the large-operation budget must be the registry's written budget"
    );
}
