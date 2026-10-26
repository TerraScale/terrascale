#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::ShapeMirrorManifest;
const HANDLE: &str = "h1-0123456789abcdef";
fn manifest(size: Value) -> Value {
    json!({"handle":HANDLE,"objects":[{"offset":"0","nextOffset":"1","binding":"binding",
        "object":"object","url":"https://storage.example/sealed?signature=fixture-private",
        "expiresAt":"2026-10-26T12:10:00Z","epoch":"9007199254740993","keyId":PROJECT,
        "sha256":"digest","size":size}]})
}
#[tokio::test]
async fn manifests_keep_private_capabilities_and_exact_int64_sizes() {
    let fixture = Fixture::new(vec![
        reply(200, &manifest(json!("9007199254740993")).to_string()),
        reply(200, "null"),
        reply(200, &json!({"handle":HANDLE,"objects":[]}).to_string()),
    ])
    .await;
    let collection = fixture.client().database("orders").collection("items");
    let manifest = collection
        .get_shape_manifest(HANDLE, "0", None)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(manifest.objects[0].size, 9_007_199_254_740_993);
    assert!(manifest.objects[0].url.contains("fixture-private"));
    assert!(!format!("{manifest:?}").contains("fixture-private"));
    assert!(collection
        .get_shape_manifest(HANDLE, "snap.0.0", None)
        .await
        .unwrap()
        .is_none());
    assert!(collection
        .get_shape_manifest(HANDLE, "1", None)
        .await
        .unwrap()
        .unwrap()
        .objects
        .is_empty());
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen[0].uri,format!("/v1/projects/{PROJECT}/databases/orders/collections/items/shape/manifest?handle={HANDLE}&offset=0"));
    assert_eq!(seen[0].headers["authorization"], "Bearer fixture-token");
    assert_eq!(seen[0].headers["cache-control"], "no-store");
}
#[test]
fn mirror_sizes_accept_integer_or_canonical_decimal() {
    for n in [0, 9_007_199_254_740_993, i64::MAX as u64] {
        for size in [json!(n), json!(n.to_string())] {
            assert_eq!(
                serde_json::from_value::<ShapeMirrorManifest>(manifest(size))
                    .unwrap()
                    .objects[0]
                    .size,
                n
            );
        }
    }
    for bad in [json!(-1), json!(1.5), json!("01"), json!(u64::MAX)] {
        assert!(serde_json::from_value::<ShapeMirrorManifest>(manifest(bad)).is_err());
    }
}
#[tokio::test]
async fn malformed_and_refused_manifests_scrub_capabilities_and_do_not_retry() {
    for status in [200, 409, 503] {
        let mut body = manifest(json!(5));
        body["objects"][0]["size"] = json!("fixture-private");
        body["error"] = json!({"code":"must_refetch","message":"fixture-private"});
        let fixture = Fixture::new(vec![reply(status, &body.to_string())]).await;
        let collection = fixture.client().database("orders").collection("items");
        let error = collection
            .get_shape_manifest(HANDLE, "0", None)
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert!(!format!("{error:?} {error}").contains("fixture-private"));
        assert!(!String::from_utf8_lossy(error.raw_body().unwrap()).contains("fixture-private"));
        if status == 409 {
            assert_eq!(error.code(), Some("must_refetch"));
        }
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}
#[tokio::test]
async fn invalid_manifest_identity_does_not_dispatch() {
    let fixture = Fixture::new(vec![]).await;
    let collection = fixture.client().database("orders").collection("items");
    for offset in ["-1", "01", "snap.1.invalid", "9223372036854775808"] {
        assert!(collection
            .get_shape_manifest(HANDLE, offset, None)
            .await
            .is_err());
    }
    assert!(collection
        .get_shape_manifest("h1-ABCDEF0123456789", "0", None)
        .await
        .is_err());
    assert!(fixture.requests.lock().unwrap().is_empty());
}
