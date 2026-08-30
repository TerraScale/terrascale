//! Compiles the TBEP protobuf contract at build time.
//!
//! `protox` parses and validates `proto/terrabase/v1/engine.proto`
//! without a system `protoc`; `tonic-prost-build` then emits the prost
//! messages and tonic service stubs into `OUT_DIR`. Nothing generated
//! is committed: the schema file is the only source of the wire
//! contract (ADR 0028).

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let file = "proto/terrabase/v1/engine.proto";
    let file_path = std::path::Path::new(file);
    println!("cargo:rerun-if-changed={}", file_path.display());
    println!("cargo:rerun-if-changed=build.rs");

    let file_descriptors = protox::compile([file_path], [file_path.parent().unwrap()])?;
    tonic_prost_build::compile_fds(file_descriptors)?;

    Ok(())
}
