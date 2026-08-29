//! The TBEP registry drift contract (SE-14): the committed generated
//! bindings equal regeneration byte-for-byte.
//!
//! A schema edit without regeneration cannot land quietly: this test
//! re-runs `terrabase_tbep::codegen::generate` over
//! `schema/tbep-registry.json` and compares the output against the two
//! committed artifacts —
//!
//! - `src/generated/registry.rs` (the Rust bindings this crate serves),
//! - `host/TerraBase.Protocol/Generated/TbepRegistry.g.cs` (the C#
//!   bindings the protocol client compiles).
//!
//! The CI wiring (regeneration plus this drift check in every pipeline)
//! is an integrator action recorded in the SE-14 packet; until the crate
//! joins the root workspace, the check runs from the crate directory
//! (`cargo test`) exactly as CI will run it.

use std::fs;

use terrabase_tbep::codegen;
use terrabase_tbep::generated as registry;

fn crate_root() -> std::path::PathBuf {
    std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn schema_document() -> String {
    let path = crate_root().join("schema/tbep-registry.json");
    fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()))
}

#[test]
fn committed_rust_bindings_equal_regeneration() {
    let document = schema_document();
    let generated = codegen::generate(&document)
        .unwrap_or_else(|error| panic!("the schema must regenerate: {error}"));
    let committed_path = crate_root().join(codegen::RUST_OUTPUT);
    let committed = fs::read_to_string(&committed_path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", committed_path.display()));
    assert_eq!(
        committed, generated.rust,
        "the committed Rust bindings drifted from the schema; rerun \
         tools/tbep-gen (`cargo run` in crates/terrabase-tbep/tools/tbep-gen)"
    );
}

#[test]
fn committed_csharp_bindings_equal_regeneration() {
    let document = schema_document();
    let generated = codegen::generate(&document)
        .unwrap_or_else(|error| panic!("the schema must regenerate: {error}"));
    let committed_path = crate_root().join("../..").join(codegen::CSHARP_OUTPUT);
    let committed = fs::read_to_string(&committed_path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", committed_path.display()));
    assert_eq!(
        committed, generated.csharp,
        "the committed C# bindings drifted from the schema; rerun \
         tools/tbep-gen (`cargo run` in crates/terrabase-tbep/tools/tbep-gen)"
    );
}

#[test]
fn the_generator_is_deterministic() {
    let document = schema_document();
    let first = codegen::generate(&document).expect("generates");
    let second = codegen::generate(&document).expect("generates again");
    assert_eq!(first.rust, second.rust);
    assert_eq!(first.csharp, second.csharp);
}

#[test]
fn generated_bindings_end_with_exactly_one_newline() {
    let generated = codegen::generate(&schema_document()).expect("generates");
    assert!(generated.rust.ends_with('\n'));
    assert!(!generated.rust.ends_with("\n\n"));
    assert!(generated.csharp.ends_with('\n'));
    assert!(!generated.csharp.ends_with("\n\n"));
}

#[test]
fn required_operations_are_registered() {
    assert!(!registry::OPERATIONS.is_empty());
    assert!(
        registry::OPERATIONS.iter().any(|operation| operation.name == "query.cancel"),
        "query cancellation is a registry operation"
    );
}

#[test]
fn mutation_operations_pair_with_idempotency_kinds() {
    for operation in registry::OPERATIONS {
        let request_kind = registry::FrameKind::from_code(operation.request_code)
            .unwrap_or_else(|| panic!("{} request code not in the frame table", operation.name));
        assert_eq!(
            request_kind.carries_idempotency(),
            operation.mutation,
            "operation {} mutation flag disagrees with the idempotency pairing",
            operation.name
        );
    }
}

#[test]
fn every_frame_kind_maps_to_a_budget_class() {
    let classes: Vec<&str> = registry::BUDGETS.iter().map(|(class, _)| *class).collect();
    for row in registry::FRAME_ROWS {
        assert!(
            classes.contains(&row.class),
            "frame kind {} names budget class {} outside the schema",
            row.kind,
            row.class
        );
        assert!(
            registry::FrameKind::from_code(row.code).is_some(),
            "frame row {} names a kind outside the enum",
            row.kind
        );
    }
}
