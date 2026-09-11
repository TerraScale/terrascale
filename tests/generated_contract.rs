use std::fs;

use serde::Deserialize;
use sha2::{Digest, Sha256};
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
    let manifest_bytes = fs::read(format!("{root}/manifest.json")).expect("manifest");
    let manifest: Manifest = serde_json::from_slice(&manifest_bytes).expect("valid manifest");
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
    assert_eq!(
        format!("{:x}", Sha256::digest(&manifest_bytes)),
        generated_contract::MANIFEST_SHA256
    );
    assert_eq!(artifacts.len(), generated_contract::ARTIFACTS.len());
    for artifact in generated_contract::ARTIFACTS {
        assert_eq!(
            artifacts.get(artifact.path).map(String::as_str),
            Some(artifact.blake3),
            "generated digest metadata for {}",
            artifact.path
        );
        assert!(
            fs::metadata(format!("{root}/{}", artifact.path)).is_ok(),
            "missing pinned artifact {}",
            artifact.path
        );
    }
}

#[test]
fn archive_pin_matches_the_producer_handoff() {
    let pin = fs::read_to_string(format!(
        "{}/CONTRACT_ARCHIVE_SHA256",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("archive pin");
    assert_eq!(
        pin.trim(),
        "e7a8bcbe752d814006c52fd40df055d54ac9adb7e3c1e5050630951d8f3498af"
    );
}

#[test]
fn generated_conformance_digests_match_the_frozen_bundle() {
    let root = format!("{}/contracts/v1", env!("CARGO_MANIFEST_DIR"));
    for (path, expected) in [
        (
            "conformance/msgpack/message-vectors.json",
            generated_contract::MESSAGE_VECTOR_SHA256,
        ),
        (
            "conformance/msgpack/hostile-vectors.json",
            generated_contract::HOSTILE_VECTOR_SHA256,
        ),
        (
            "conformance/http.json",
            generated_contract::HTTP_CONFORMANCE_SHA256,
        ),
        (
            "conformance/reactive.json",
            generated_contract::REACTIVE_CONFORMANCE_SHA256,
        ),
    ] {
        let bytes = fs::read(format!("{root}/{path}")).expect("conformance artifact");
        assert_eq!(format!("{:x}", Sha256::digest(bytes)), expected, "{path}");
    }
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

#[test]
fn generated_contract_publishes_import_lifecycle_routes() {
    let expected = [
        (
            "list_imports",
            generated_contract::HttpMethod::Get,
            "/api/v1/databases/{name}/imports",
        ),
        (
            "create_import",
            generated_contract::HttpMethod::Post,
            "/api/v1/databases/{name}/imports",
        ),
        (
            "get_import",
            generated_contract::HttpMethod::Get,
            "/api/v1/databases/{name}/imports/{id}",
        ),
        (
            "upload_import",
            generated_contract::HttpMethod::Put,
            "/api/v1/databases/{name}/imports/{id}/input",
        ),
        (
            "resume_import",
            generated_contract::HttpMethod::Post,
            "/api/v1/databases/{name}/imports/{id}/resume",
        ),
    ];
    for (operation_id, method, path) in expected {
        assert!(
            generated_contract::OPERATIONS
                .iter()
                .any(|operation| operation.operation_id == operation_id
                    && operation.method == method
                    && operation.path == path),
            "missing generated import operation {operation_id}: {method:?} {path}"
        );
    }
}

#[test]
fn generated_contract_resolves_inline_success_schema_types() {
    let list_imports = generated_contract::ROUTES
        .iter()
        .find(|route| route.operation_id == "list_imports")
        .expect("list_imports route");
    assert_eq!(list_imports.response_model, Some("Vec<ImportJob>"));

    let download_export = generated_contract::ROUTES
        .iter()
        .find(|route| route.operation_id == "download_export")
        .expect("download_export route");
    assert_eq!(download_export.response_model, Some("String"));
}
