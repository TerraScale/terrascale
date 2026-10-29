#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde::Deserialize;
use serde_json::{json, Value};
use terrascale::v1::{
    Error, Filter, GeoNear, Index, IndexKind, IndexRequest, Near, Query, SortKey,
};

#[tokio::test]
async fn search_queries_preserve_grammar_scores_and_cursor() {
    let raw = r#"{"and":[{"eq":["/published",true]},{"search":["/title","quick fox"]}]}"#;
    let filters = [
        Filter::search("/title", "quick fox"),
        Filter::and([
            Filter::eq("/published", true),
            Filter::search("/title", "quick fox"),
        ]),
        Filter::and([
            Filter::search("/title", "quick fox"),
            Filter::within_radius("/loc", 0.0, 0.0, 1000.0),
            Filter::within_box("/loc", -1.0, -1.0, 1.0, 1.0),
        ]),
        Filter::from_json(raw).unwrap(),
    ];
    let page = r#"{"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"tbv1-00000000000000000000000000000000","state":"value","document":{},"score":1.25},{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB1","version":"tbv1-00000000000000000000000000000000","state":"value","document":{},"score":0},{"id":"01ARZ3NDEKTSV4RRFFQ69G5FB2","version":"tbv1-00000000000000000000000000000000","state":"value","document":{}}],"next_cursor":"next-score-id"}"#;
    let fixture = Fixture::new(vec![reply(200, page); filters.len()]).await;
    let articles = fixture.client().database("orders").collection("articles");
    for filter in &filters {
        let query = Query::new()
            .filter(filter.clone())
            .sort(SortKey::score())
            .cursor("score-id");
        let out = articles.query::<Value>(&query).await.unwrap();
        assert_eq!(
            out.items.iter().map(|d| d.score).collect::<Vec<_>>(),
            [Some(1.25), Some(0.0), None]
        );
        assert_eq!(out.next_cursor.as_deref(), Some("next-score-id"));
    }
    let requests = fixture.requests.lock().unwrap();
    for (request, filter) in requests.iter().zip(&filters) {
        assert_eq!(request.method, "POST");
        assert_eq!(
            request.uri,
            format!("/v1/projects/{PROJECT}/databases/orders/collections/articles/query")
        );
        assert_eq!(
            serde_json::from_slice::<Value>(&request.body).unwrap(),
            json!({"filter":filter,"sort":[{"path":"$score","order":"desc"}],"cursor":"score-id"})
        );
    }
    assert_eq!(serde_json::to_string(filters.last().unwrap()).unwrap(), raw);
}

#[tokio::test]
async fn invalid_search_and_score_queries_fail_before_sending() {
    let fixture = Fixture::new(vec![]).await;
    let articles = fixture.client().database("orders").collection("articles");
    let search = || Filter::search("/title", "fox");
    let two = || Filter::and([search(), search()]);
    let queries = [
        Query::new().filter(Filter::search("$id", "fox")),
        Query::new().filter(Filter::search("$score", "fox")),
        Query::new().filter(Filter::search("title", "fox")),
        Query::new().filter(Filter::search("/title", "")),
        Query::new().filter(Filter::search("/title", "x".repeat(4097))),
        Query::new().filter(Filter::search("/title", "🦊".repeat(1025))),
        Query::new().filter(Filter::search("/title", "quick\0fox")),
        Query::new().sort(SortKey::score()),
        Query::new().filter(search()).sort(SortKey::asc("$score")),
        Query::new()
            .filter(search())
            .sort(SortKey::score())
            .sort(SortKey::asc("$id")),
        Query::new().filter(two()).sort(SortKey::score()),
        Query::new()
            .filter(
                Filter::from_json(
                    r#"{"or":[{"search":["/title","fox"]},{"not":{"search":["/body","dog"]}}]}"#,
                )
                .unwrap(),
            )
            .sort(SortKey::score()),
        Query::new()
            .filter(Filter::from_json(r#"{"eq":["/title","search"]}"#).unwrap())
            .sort(SortKey::score()),
        Query::new()
            .filter(search())
            .near(Near::new("/embedding", vec![1.0])),
        Query::new()
            .filter(search())
            .geo_near(GeoNear::new("/loc", 0.0, 0.0)),
    ];
    for query in queries {
        assert!(matches!(
            articles.query::<Value>(&query).await,
            Err(Error::InvalidInput(_))
        ));
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
    assert!(Filter::search("/title", "x".repeat(4096))
        .validate()
        .is_ok());
    assert!(Filter::search("/title", "🦊".repeat(1024))
        .validate()
        .is_ok());
}

#[tokio::test]
async fn text_index_requests_and_responses_keep_the_kind() {
    const INDEX: &str = r#"{"name":"article_text","collection":"articles","fields":["title","article.body"],"state":"active","kind":"text"}"#;
    #[derive(Deserialize)]
    struct IndexList {
        items: Vec<Index>,
    }
    let fixture = Fixture::new(vec![
        reply(201, INDEX),
        reply(200, &format!(r#"{{"items":[{INDEX}]}}"#)),
    ])
    .await;
    let db = fixture.client().database("orders");
    let request = IndexRequest {
        collection: "articles".into(),
        fields: vec!["title".into(), "article.body".into()],
        name: Some("article_text".into()),
        kind: Some(IndexKind::Text),
        dimensions: None,
    };
    let created: Index = db.create_index(&request).await.unwrap();
    assert_eq!(created.kind, IndexKind::Text);
    let listed: IndexList = db
        .list_indexes(&[("collection", "articles".into())])
        .await
        .unwrap();
    assert_eq!(listed.items, vec![created]);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        requests[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/indexes")
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"collection":"articles","fields":["title","article.body"],"name":"article_text","kind":"text"})
    );
    assert_eq!(
        requests[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/indexes?collection=articles")
    );
    let old: Index = serde_json::from_value(
        json!({"name":"by_title","collection":"articles","fields":["title"],"state":"active"}),
    )
    .unwrap();
    assert_eq!(old.kind, IndexKind::BTree);
    assert_eq!(serde_json::to_value(IndexKind::BTree).unwrap(), "btree");
    assert_eq!(
        serde_json::to_value(IndexRequest {
            collection: "articles".into(),
            fields: vec!["title".into()],
            ..Default::default()
        })
        .unwrap(),
        json!({"collection":"articles","fields":["title"]})
    );
}

#[tokio::test]
async fn missing_text_index_retains_invalid_query_error() {
    let fixture = Fixture::new(vec![reply(
        400,
        r#"{"error":{"code":"invalid_query","message":"text index required"}}"#,
    )])
    .await;
    let err = fixture
        .client()
        .database("orders")
        .collection("articles")
        .query::<Value>(&Query::new().filter(Filter::search("/title", "fox")))
        .await
        .unwrap_err();
    assert_eq!(err.code(), Some("invalid_query"));
}
