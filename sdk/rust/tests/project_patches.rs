#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{DocumentId, Patch, PatchOp, Tx, WriteOptions};

const KEY: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
const OTHER: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
const MISSING: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB2";
const DELETED: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB3";
const VERSION: &str = "tbv1-00000000000000000000000000000000";
fn id(value: &str) -> DocumentId {
    value.parse().unwrap()
}

const DOCUMENT: &str = r#"{"counter":9223372036854775807,"tags":["a","b"],"exact":123456789012345678901234567890.123456789}"#;

#[tokio::test]
async fn patch_acknowledgements_retain_exact_documents_and_other_writes_omit_them() {
    let patched = format!(
        r#"{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"{VERSION}","offset":"1","durability":"local_durable","document":{DOCUMENT}}}"#
    );
    let put = format!(
        r#"{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB1","version":"{VERSION}","offset":"2","durability":"local_durable"}}"#
    );
    let committed = format!(
        r#"{{"offset":"3","durability":"local_durable","results":[{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"{VERSION}","document":{DOCUMENT}}},{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB1","version":"{VERSION}"}},{{"asserted":true}},{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB3","version":"{VERSION}","document":null}}]}}"#
    );
    let fixture = Fixture::new(vec![
        reply(200, &patched),
        reply(200, &put),
        reply(200, &committed),
    ])
    .await;
    let db = fixture.client().database("orders");
    let patch = Patch::merge(&json!({"name":"updated"})).unwrap();
    let written = db
        .collection("items")
        .patch(id(KEY), &patch, WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(written.document.as_deref().unwrap().get(), DOCUMENT);
    assert_eq!(written, written.clone());
    let put = db
        .collection("items")
        .put(id(OTHER), &json!({}), WriteOptions::new())
        .await
        .unwrap();
    assert!(put.document.is_none());

    let mut tx = Tx::new();
    tx.patch("items", id(KEY), patch, None)
        .put("items", id(OTHER), &json!({}), None)
        .assert_absent("items", id(MISSING))
        .delete("items", id(DELETED), None);
    let result = db.commit(&tx).await.unwrap();
    assert_eq!(result.results[0].document().unwrap().get(), DOCUMENT);
    for result in &result.results[1..] {
        assert!(result.document().is_none());
    }
    assert_eq!(result, result.clone());
    assert_eq!(fixture.requests.lock().unwrap().len(), 3);
}

#[tokio::test]
async fn set_operations_encode_identically_in_patch_and_transaction_requests() {
    let acknowledged = format!(
        r#"{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"{VERSION}","offset":"1","durability":"local_durable","document":{{"tags":["blue"]}}}}"#
    );
    let committed = format!(
        r#"{{"offset":"2","durability":"local_durable","results":[{{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"{VERSION}","document":{{"tags":["blue"]}}}}]}}"#
    );
    let fixture = Fixture::new(vec![reply(200, &acknowledged), reply(200, &committed)]).await;
    let db = fixture.client().database("orders");
    let patch = Patch::ops(vec![
        PatchOp::add("/tags", "blue").unwrap(),
        PatchOp::remove("/tags", "red").unwrap(),
    ]);
    db.collection("items")
        .patch(id(KEY), &patch, WriteOptions::new())
        .await
        .unwrap();
    let mut tx = Tx::new();
    tx.patch("items", id(KEY), patch, None);
    db.commit(&tx).await.unwrap();
    let operations = json!([
        {"op":"add","path":"/tags","value":"blue"},
        {"op":"remove","path":"/tags","value":"red"}
    ]);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests[0].method, "PATCH");
    assert_eq!(requests[0].uri, format!("/v1/projects/{PROJECT}/databases/orders/collections/items/documents/by-key?id=01ARZ3NDEKTSV4RRFFQ69G5FB0"));
    assert_eq!(
        requests[0].headers["content-type"],
        "application/vnd.terrabase.ops+json"
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"ops":operations})
    );
    assert_eq!(requests[1].method, "POST");
    assert_eq!(
        requests[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/transactions")
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[1].body).unwrap(),
        json!({"ops":[{"op":"patch","collection":"items","id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","ops":operations}]})
    );
}
