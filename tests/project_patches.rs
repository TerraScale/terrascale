#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Patch, PatchOp, Tx, WriteOptions};

const VERSION: &str = "tbv1-00000000000000000000000000000000";
const DOCUMENT: &str = r#"{"counter":9223372036854775807,"tags":["a","b"],"exact":123456789012345678901234567890.123456789}"#;

#[tokio::test]
async fn patch_acknowledgements_retain_exact_documents_and_other_writes_omit_them() {
    let patched = format!(
        r#"{{"id":"customer/key","version":"{VERSION}","offset":"1","durability":"local_durable","document":{DOCUMENT}}}"#
    );
    let put = format!(
        r#"{{"id":"other","version":"{VERSION}","offset":"2","durability":"local_durable"}}"#
    );
    let committed = format!(
        r#"{{"offset":"3","durability":"local_durable","results":[{{"id":"customer/key","version":"{VERSION}","document":{DOCUMENT}}},{{"id":"other","version":"{VERSION}"}},{{"asserted":true}},{{"id":"deleted","version":"{VERSION}","document":null}}]}}"#
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
        .patch("customer/key", &patch, WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(written.document.as_deref().unwrap().get(), DOCUMENT);
    assert_eq!(written, written.clone());
    let put = db
        .collection("items")
        .put("other", &json!({}), WriteOptions::new())
        .await
        .unwrap();
    assert!(put.document.is_none());

    let mut tx = Tx::new();
    tx.patch("items", "customer/key", patch, None)
        .put("items", "other", &json!({}), None)
        .assert_absent("items", "missing")
        .delete("items", "deleted", None);
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
        r#"{{"id":"customer/key","version":"{VERSION}","offset":"1","durability":"local_durable","document":{{"tags":["blue"]}}}}"#
    );
    let committed = format!(
        r#"{{"offset":"2","durability":"local_durable","results":[{{"id":"customer/key","version":"{VERSION}","document":{{"tags":["blue"]}}}}]}}"#
    );
    let fixture = Fixture::new(vec![reply(200, &acknowledged), reply(200, &committed)]).await;
    let db = fixture.client().database("orders");
    let patch = Patch::ops(vec![
        PatchOp::add("/tags", "blue").unwrap(),
        PatchOp::remove("/tags", "red").unwrap(),
    ]);
    db.collection("items")
        .patch("customer/key", &patch, WriteOptions::new())
        .await
        .unwrap();
    let mut tx = Tx::new();
    tx.patch("items", "customer/key", patch, None);
    db.commit(&tx).await.unwrap();
    let operations = json!([
        {"op":"add","path":"/tags","value":"blue"},
        {"op":"remove","path":"/tags","value":"red"}
    ]);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests[0].method, "PATCH");
    assert_eq!(requests[0].uri, format!("/v1/projects/{PROJECT}/databases/orders/collections/items/documents/by-key?id=customer%2Fkey"));
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
        json!({"ops":[{"op":"patch","collection":"items","id":"customer/key","ops":operations}]})
    );
}
