#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Aggregate, Error, Filter, GeoNear, Metric, Near, Query, SortKey};

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

#[tokio::test]
async fn geo_queries_send_within_filters_and_the_near_sort_key() {
    let page = json!({"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"loc":{"lat":-23.55,"lon":-46.63}},"distance":12.5}],"next_cursor":null});
    let fixture = Fixture::new(vec![reply(200, &page.to_string())]).await;
    let query = Query::new()
        .filter(Filter::or([
            Filter::within_radius("/loc", -23.55, -46.63, 1000.0),
            Filter::within_box("/loc", -20.0, 179.0, -10.0, -170.0),
        ]))
        .geo_near(GeoNear::new("/loc", -23.55, -46.63))
        .limit(10);
    let result = fixture
        .client()
        .database("orders")
        .collection("places")
        .query::<Value>(&query)
        .await
        .unwrap();
    assert_eq!(result.items[0].distance, Some(12.5));
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"filter":{"or":[
            {"within":["/loc",{"lat":-23.55,"lon":-46.63,"meters":1000.0}]},
            {"within":["/loc",{"south":-20.0,"west":179.0,"north":-10.0,"east":-170.0}]}
        ]},"sort":[{"near":{"path":"/loc","lat":-23.55,"lon":-46.63}}],"limit":10})
    );
}

#[tokio::test]
async fn aggregate_queries_send_the_aggregate_and_decode_groups() {
    let page = json!({"items":[],"next_cursor":null,"groups":[
        {"key":["paid"],"count":"250","sum":{"/amount":12500.5},"avg":{"/amount":50.002},"min":{"/amount":1},"max":{"/amount":99}},
        {"key":[null],"count":"3","sum":{"/amount":0},"avg":{"/amount":null},"min":{"/amount":null},"max":{"/amount":null}}
    ]});
    let fixture = Fixture::new(vec![reply(200, &page.to_string())]).await;
    let query = Query::new()
        .filter(Filter::gte("/created", 1_761_264_000_000_i64))
        .aggregate(
            Aggregate::new()
                .group_by(["/status"])
                .sum(["/amount"])
                .avg(["/amount"])
                .min(["/amount"])
                .max(["/amount"]),
        )
        .limit(100);
    let result = fixture
        .client()
        .database("orders")
        .collection("items")
        .query::<Value>(&query)
        .await
        .unwrap();
    assert!(result.items.is_empty());
    let groups = result.groups.unwrap();
    assert_eq!(groups[0].key, vec![json!("paid")]);
    assert_eq!(groups[0].count, 250);
    assert_eq!(groups[0].sum["/amount"], Some(12500.5));
    assert_eq!(groups[0].max["/amount"], Some(99.0));
    assert_eq!(groups[1].key, vec![Value::Null]);
    assert_eq!(groups[1].avg["/amount"], None);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"filter":{"gte":["/created",1_761_264_000_000_i64]},"aggregate":{"group_by":["/status"],"sum":["/amount"],"avg":["/amount"],"min":["/amount"],"max":["/amount"]},"limit":100})
    );
}

#[tokio::test]
async fn invalid_aggregate_and_geo_queries_fail_before_sending() {
    let fixture = Fixture::new(vec![]).await;
    let items = fixture.client().database("orders").collection("items");
    let agg = Aggregate::new;
    let geo = || GeoNear::new("/loc", 0.0, 0.0);
    let metrics: Vec<String> = (0..17).map(|i| format!("/m{i}")).collect();
    for query in [
        Query::new().aggregate(agg()).sort(SortKey::asc("/a")),
        Query::new().aggregate(agg()).project(["/a"]),
        Query::new().aggregate(agg()).cursor("c"),
        Query::new().aggregate(agg()).count(true),
        Query::new().aggregate(agg()).geo_near(geo()),
        Query::new().aggregate(agg().group_by(["/a", "/b", "/c", "/d", "/e"])),
        Query::new().aggregate(agg().sum(metrics)),
        Query::new().aggregate(agg().min(["/a", "/a"])),
        Query::new().geo_near(geo()).sort(SortKey::asc("/a")),
        Query::new().geo_near(geo()).cursor("c"),
        Query::new().geo_near(geo()).limit(101),
        Query::new()
            .geo_near(geo())
            .near(Near::new("/v", vec![1.0])),
        Query::new().geo_near(GeoNear::new("/loc", f64::NAN, 0.0)),
        Query::new().filter(Filter::within_radius("/loc", 0.0, 0.0, -1.0)),
        Query::new().filter(Filter::within_radius("/loc", 0.0, 0.0, 20_000_001.0)),
        Query::new().filter(Filter::within_box("/loc", 0.0, f64::INFINITY, 1.0, 1.0)),
    ] {
        let err = items.query::<Value>(&query).await.unwrap_err();
        assert!(matches!(err, Error::InvalidInput(_)), "{err:?}");
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}
