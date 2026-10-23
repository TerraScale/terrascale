#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Error, Filter, Metric, Near, Query, SortKey};

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

#[tokio::test]
async fn near_queries_send_the_only_sort_key_and_decode_distance() {
    let page = json!({"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"title":"a"},"distance":0.25}],"next_cursor":null});
    let fixture = Fixture::new(vec![reply(200, &page.to_string())]).await;
    let query = Query::new()
        .filter(Filter::eq("/kind", "doc"))
        .near(Near::new("/embedding", vec![0.1, 0.2]).metric(Metric::L2))
        .project(["/title"])
        .limit(5);
    let result = fixture
        .client()
        .database("orders")
        .collection("items")
        .query::<Value>(&query)
        .await
        .unwrap();
    assert_eq!(result.items[0].distance, Some(0.25));
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"filter":{"eq":["/kind","doc"]},"sort":[{"near":{"path":"/embedding","vector":[0.1,0.2],"metric":"l2"}}],"project":["/title"],"limit":5})
    );
}

#[tokio::test]
async fn invalid_near_queries_fail_before_sending() {
    let fixture = Fixture::new(vec![]).await;
    let items = fixture.client().database("orders").collection("items");
    let near = || Near::new("/embedding", vec![1.0]);
    for query in [
        Query::new().near(near()).sort(SortKey::asc("/title")),
        Query::new().near(near()).cursor("c"),
        Query::new().near(near()).limit(101),
        Query::new().near(Near::new("/embedding", vec![])),
        Query::new().near(Near::new("/embedding", vec![f32::NAN])),
        Query::new().near(Near::new("/embedding", vec![0.0, 0.0])),
    ] {
        let err = items.query::<Value>(&query).await.unwrap_err();
        assert!(matches!(err, Error::InvalidInput(_)), "{err:?}");
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}
