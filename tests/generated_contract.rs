use std::fs;

use serde::Deserialize;
use terrascale::generated_contract;

#[derive(Deserialize)]
struct Manifest {
    #[serde(rename = "schemaVersion")]
    schema_version: u32,
    #[serde(rename = "contractVersion")]
    contract_version: String,
    artifacts: Vec<Artifact>,
}

#[derive(Deserialize)]
struct Artifact {
    path: String,
    blake3: String,
}

#[test]
fn generated_contract_is_pinned_to_the_committed_bundle() {
    let root = format!("{}/contracts/v1", env!("CARGO_MANIFEST_DIR"));
    let manifest: Manifest = serde_json::from_str(
        &fs::read_to_string(format!("{root}/manifest.json")).expect("manifest"),
    )
    .expect("valid manifest");
    assert_eq!(manifest.schema_version, 1);
    assert_eq!(
        manifest.contract_version,
        generated_contract::CONTRACT_VERSION
    );
    let artifacts = manifest
        .artifacts
        .into_iter()
        .map(|artifact| (artifact.path, artifact.blake3))
        .collect::<std::collections::BTreeMap<_, _>>();
    assert_eq!(
        artifacts["openapi.json"],
        generated_contract::OPENAPI_BLAKE3
    );
    assert_eq!(
        artifacts["msgpack/terrascale.sync.v1.msgpack.json"],
        generated_contract::MSGPACK_MANIFEST_BLAKE3
    );
}

#[test]
fn generated_http_and_reactive_surface_is_non_empty() {
    assert!(!generated_contract::ROUTES.is_empty());
    assert_eq!(generated_contract::ROUTES, generated_contract::OPERATIONS);
    assert_eq!(generated_contract::MSGPACK_ENVELOPE.len(), 7);
    assert_eq!(generated_contract::MSGPACK_MESSAGES.len(), 11);
    assert_eq!(generated_contract::MSGPACK_TYPES.len(), 8);
    assert_eq!(
        generated_contract::MSGPACK_SCAN.max_message_bytes,
        8_388_608
    );
}
