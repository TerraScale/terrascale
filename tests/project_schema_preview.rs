#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, CollectionSchemaPreview};
fn page(count: Value) -> Value {
    json!({"schema_hash":"hash","frontier":"local-frontier","examined":count,"valid":count,"invalid":0,
        "complete":false,"next_cursor":"opaque+/=","diagnostics":[{"id_hash":"id-hash","path_hash":"path-hash","keyword":"type"}]})
}
#[tokio::test]
async fn preview_repeats_identical_bytes_and_encodes_opaque_continuation() {
    let fixture = Fixture::new(vec![
        reply(200, &page(json!(1)).to_string()),
        reply(200, &page(json!("1")).to_string()),
    ])
    .await;
    let client = fixture.client();
    let collection = client.database("orders").collection("items");
    let schema = br#"{ "type": "object" }"#;
    let first = collection.preview_schema(schema, None).await.unwrap();
    assert_eq!(first.diagnostics[0].keyword, "type");
    collection
        .preview_schema(schema, first.next_cursor.as_deref())
        .await
        .unwrap();
    let seen = fixture.requests.lock().unwrap();
    for r in seen.iter() {
        assert_eq!(r.method, "POST");
        assert_eq!(r.body, schema);
        assert_eq!(r.headers["authorization"], "Bearer fixture-token");
    }
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/collections/items/schema/preview")
    );
    assert!(seen[1].uri.ends_with("?cursor=opaque%2B%2F%3D"));
}
#[test]
fn preview_counts_accept_integer_string_union_and_require_cursor_member() {
    for value in [json!(100), json!("100")] {
        assert_eq!(
            serde_json::from_value::<CollectionSchemaPreview>(page(value))
                .unwrap()
                .examined,
            100
        );
    }
    for value in [json!(-1), json!(1.5), json!("01"), json!(2147483648u64)] {
        assert!(serde_json::from_value::<CollectionSchemaPreview>(page(value)).is_err());
    }
    let mut p = page(json!(1));
    p.as_object_mut().unwrap().remove("next_cursor");
    assert!(serde_json::from_value::<CollectionSchemaPreview>(p).is_err());
}
#[tokio::test]
async fn invalid_preview_inputs_do_not_dispatch_and_stale_does_not_retry() {
    let fixture = Fixture::new(vec![reply(409, r#"{"error":{"code":"preview_stale"}}"#)]).await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .build()
        .unwrap();
    let collection = client.database("orders").collection("items");
    for schema in [vec![], vec![b' '; 65537], b"{invalid}".to_vec()] {
        assert!(collection.preview_schema(&schema, None).await.is_err());
    }
    for cursor in ["".to_owned(), "x".repeat(2049)] {
        assert!(collection
            .preview_schema(b"{}", Some(&cursor))
            .await
            .is_err());
    }
    let error = collection.preview_schema(b"true", None).await.unwrap_err();
    assert_eq!(error.code(), Some("preview_stale"));
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
