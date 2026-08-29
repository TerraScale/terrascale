//! `tbep-gen` — regenerates the committed TBEP bindings from the shared
//! schema.
//!
//! Run from `crates/terrabase-tbep/tools/tbep-gen`:
//!
//! ```text
//! cargo run
//! ```
//!
//! Writes `../../src/generated/registry.rs` (Rust) and
//! `../../../../host/TerraBase.Protocol/Generated/TbepRegistry.g.cs`
//! (C#). The committed files must
//! equal regeneration exactly; `tests/registry_drift.rs` re-runs the
//! same generator against the committed files and fails on any
//! difference, so a schema edit without regeneration cannot land
//! quietly.
//!
//! The generator compiles the crate's `src/codegen.rs` directly through
//! a `#[path]` include (one implementation, two compilation contexts):
//! this package must run before the `terrabase-tbep` library compiles,
//! because the library consumes the file this tool produces.

#[path = "../../../src/codegen.rs"]
mod codegen;

use std::path::PathBuf;

fn main() {
    let crate_root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
    let schema_path = crate_root.join("schema/tbep-registry.json");
    let document = std::fs::read_to_string(&schema_path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", schema_path.display()));

    let generated = codegen::generate(&document)
        .unwrap_or_else(|error| panic!("schema validation failed: {error}"));

    let rust_path = crate_root.join(codegen::RUST_OUTPUT);
    std::fs::write(&rust_path, generated.rust)
        .unwrap_or_else(|error| panic!("cannot write {}: {error}", rust_path.display()));

    let csharp_path = crate_root.join("../..").join(codegen::CSHARP_OUTPUT);
    if let Some(parent) = csharp_path.parent() {
        std::fs::create_dir_all(parent)
            .unwrap_or_else(|error| panic!("cannot create {}: {error}", parent.display()));
    }
    std::fs::write(&csharp_path, generated.csharp)
        .unwrap_or_else(|error| panic!("cannot write {}: {error}", csharp_path.display()));

    println!("wrote {}", rust_path.display());
    println!("wrote {}", csharp_path.display());

}
