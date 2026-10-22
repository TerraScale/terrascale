#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Filter, Query, SortKey};

#[tokio::test]
async fn queries_use_supported_pointer_paths_and_preserve_rows_and_counts() {
    let page = json!({"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"email":"Ana@Example.com"}}],"next_cursor":null,"count":{"value":"1","exact":true}});
    let fixture = Fixture::new(vec![reply(200, &page.to_string())]).await;
    let query = Query::new()
        .filter(Filter::eq("/email", "Ana@Example.com"))
        .sort(SortKey::asc("/email"))
        .project(["/email"])
        .limit(10)
        .count(true);
    let result = fixture
        .client()
        .database("orders")
        .collection("items")
        .query::<Value>(&query)
        .await
        .unwrap();
    assert_eq!(result.items[0].id.to_string(), "01ARZ3NDEKTSV4RRFFQ69G5FB0");
    assert_eq!(result.items[0].value().unwrap()["email"], "Ana@Example.com");
    assert_eq!(result.count.unwrap().value, 1);
    assert!(result.next_cursor.is_none());
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests[0].method, "POST");
    assert_eq!(
        requests[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/collections/items/query")
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"filter":{"eq":["/email","Ana@Example.com"]},"sort":[{"path":"/email","order":"asc"}],"project":["/email"],"limit":10,"count":true})
    );
}
