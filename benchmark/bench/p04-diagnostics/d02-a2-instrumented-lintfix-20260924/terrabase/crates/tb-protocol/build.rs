//! Generate the checked-in v1 contract using the system `protoc` executable.

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let proto_root = "../../proto";
    let contract = "../../proto/terrabase/v1/replication.proto";
    println!("cargo:rerun-if-changed={contract}");

    tonic_prost_build::configure()
        .bytes(".")
        .compile_protos(&[contract], &[proto_root])?;
    Ok(())
}
