//! Fast v1 suite: the `terrascale::v1` client against the in-process HTTPS
//! contract fixture (tests/support/v1_fixture.rs).

mod support;

use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::json;
use serde_json::value::RawValue;
use support::v1_fixture::{Fault, Fixture};
use terrascale::v1::*;

fn data(f: &Fixture) -> Client {
    data_with(f, &f.data_token)
}

fn data_with(f: &Fixture, token: &str) -> Client {
    Client::builder(&f.url, token)
        .add_root_ca_pem(&f.ca_pem)
        .retry_policy(RetryPolicy {
            base_delay: Duration::from_millis(1),
            max_delay: Duration::from_millis(5),
            ..RetryPolicy::default()
        })
        .build()
        .unwrap()
}

fn key(s: &str) -> IdempotencyKey {
    IdempotencyKey::new(s).unwrap()
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
struct User {
    email: String,
    name: String,
    #[serde(default)]
    logins: i64,
    #[serde(default)]
    tags: Vec<String>,
}

#[test]
fn ids_names_and_builder_validation() {
    assert_eq!(
        uuidv5_for("users", "ana@example.com").to_string(),
        "b85e5c40-9a0a-517f-ba69-779a0cd2b159"
    );
    assert!(is_valid_name("users") && is_valid_name("a-b_9"));
    assert!(!is_valid_name("Users") && !is_valid_name("9a") && !is_valid_name(""));
    assert!(Version::new("tbv1-0123456789abcdef0123456789abcdef").is_ok());
    assert!(Version::new("tbv1-0123456789ABCDEF0123456789abcdef").is_err());
    assert!(IdempotencyKey::new("a.b-c_D9").is_ok());
    assert!(IdempotencyKey::new("").is_err() && IdempotencyKey::new("a b").is_err());
    assert!(IdempotencyKey::new("x".repeat(65)).is_err());
    let derived = IdempotencyKey::derive(&["refresh", "abc"]);
    assert_eq!(derived, IdempotencyKey::derive(&["refresh", "abc"]));
    assert_ne!(derived, IdempotencyKey::derive(&["refres", "habc"]));
    assert_eq!(derived.as_str().len(), 64);
    assert_ne!(IdempotencyKey::random(), IdempotencyKey::random());

    let token = format!("tbk1.{}.{}", "a".repeat(22), "b".repeat(43));
    let bad = |url: &str, tok: &str| {
        matches!(
            Client::builder(url, tok).build(),
            Err(Error::InvalidInput(_))
        )
    };
    assert!(bad("https://example.com", "tbk1.short.key"));
    assert!(bad("http://example.com", &token));
    assert!(bad("https://example.com?x=1", &token));
    assert!(bad("ftp://example.com", &token));
    assert!(Client::builder("http://127.0.0.1:1", &token)
        .build()
        .is_ok());
    assert!(Client::builder("https://example.com/prefix/", &token)
        .build()
        .is_ok());
    assert!(matches!(
        Client::builder("https://example.com", &token)
            .add_root_ca_pem("not a pem")
            .build(),
        Err(Error::InvalidInput(_))
    ));
}

#[test]
fn numbers_and_filters_encode_exactly() {
    let n: Number = "123456789012345678901234567890.000000000000000000001"
        .parse()
        .unwrap();
    assert_eq!(
        serde_json::to_string(&n).unwrap(),
        "123456789012345678901234567890.000000000000000000001"
    );
    let back: Number = serde_json::from_str("1e400").unwrap();
    assert_eq!(back.as_str(), "1e400");
    assert!("01".parse::<Number>().is_err() && "1.".parse::<Number>().is_err());
    assert!(serde_json::from_str::<Number>("\"1\"").is_err());

    let f = Filter::and([
        Filter::eq(Path::lower("/email"), "Ana@Example.com"),
        Filter::gte("/n", n.clone()),
        Filter::in_("$id", [uuidv5_for("users", "a")]),
        Filter::not(Filter::exists("/deleted_at")),
    ]);
    assert_eq!(
        serde_json::to_string(&f).unwrap(),
        format!(
            r#"{{"and":[{{"eq":[{{"lower":"/email"}},"Ana@Example.com"]}},{{"gte":["/n",{n}]}},{{"in":["$id",["{}"]]}},{{"not":{{"exists":"/deleted_at"}}}}]}}"#,
            uuidv5_for("users", "a")
        )
    );
    assert!(Filter::and([]).validate().is_err());
    assert!(Filter::lt("/n", Scalar::Null).validate().is_err());
    assert!(Filter::from_json("[1]").is_err());
    let raw = Filter::from_json(r#"{"eq":["/a",1]}"#).unwrap();
    assert_eq!(serde_json::to_string(&raw).unwrap(), r#"{"eq":["/a",1]}"#);
    assert_eq!(raw, Filter::eq("/a", 1));
    let ops = Patch::ops(vec![
        PatchOp::set("/profile/name", "Ana").unwrap(),
        PatchOp::unset("/legacy"),
        PatchOp::inc("/db_count", 1),
        PatchOp::append("/tags", "beta").unwrap(),
    ]);
    let Patch::Ops(list) = ops else {
        unreachable!()
    };
    assert_eq!(
        serde_json::to_string(&list).unwrap(),
        r#"[{"op":"set","path":"/profile/name","value":"Ana"},{"op":"unset","path":"/legacy"},{"op":"inc","path":"/db_count","by":1},{"op":"append","path":"/tags","value":"beta"}]"#
    );
}

#[tokio::test]
async fn tls_requires_the_configured_root() {
    let f = Fixture::start().await;
    let untrusted = Client::builder(&f.url, &f.data_token)
        .retry_policy(RetryPolicy::none())
        .build()
        .unwrap();
    let err = untrusted
        .database("main")
        .collection("users")
        .get::<User>(uuidv5_for("users", "x"))
        .await
        .unwrap_err();
    assert!(matches!(err, Error::Transport(_)), "{err:?}");
}

#[tokio::test]
async fn oversized_responses_are_not_retried() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let id = uuidv5_for("items", "big");
    items
        .put(id, &json!({"pad": "x".repeat(4096)}), WriteOptions::new())
        .await
        .unwrap();
    let small = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .max_response_bytes(1024)
        .build()
        .unwrap();
    let path = format!("/v1/databases/main/collections/items/documents/{id}");
    let before = f.count("GET", &path);
    let err = small
        .database("main")
        .collection("items")
        .get::<Box<RawValue>>(id)
        .await
        .unwrap_err();
    assert!(
        matches!(
            err,
            Error::ResponseTooLarge {
                limit: 1024,
                status: 200,
                may_have_applied: false
            }
        ),
        "{err:?}"
    );
    assert!(!err.is_uncertain());
    assert_eq!(f.count("GET", &path) - before, 1, "sent exactly once");
}

#[tokio::test]
async fn only_rate_limited_429s_are_retried() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let id = uuidv5_for("items", "429");
    let path = format!("{docs}/{id}");

    let mut unknown = Fault::new("PUT", docs, 429, "slow_down");
    unknown.retry_after = Some("0");
    f.inject(unknown);
    let before = f.count("PUT", &path);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 429, code, .. } if code == "slow_down"),
        "{err:?}"
    );
    assert_eq!(f.count("PUT", &path) - before, 1);

    let mut malformed = Fault::new("PUT", docs, 429, "rate_limited");
    malformed.body = Some("busy");
    f.inject(malformed);
    let before = f.count("PUT", &path);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 429, code, .. } if code == "http_429"),
        "{err:?}"
    );
    assert_eq!(f.count("PUT", &path) - before, 1);
    assert_eq!(f.faults_left(), 0);
}

async fn raw_post(f: &Fixture, path: &str, body: &str) -> (u16, serde_json::Value) {
    let http = reqwest::Client::builder()
        .no_proxy()
        .add_root_certificate(reqwest::Certificate::from_pem(f.ca_pem.as_bytes()).unwrap())
        .build()
        .unwrap();
    let resp = http
        .post(format!("{}{path}", f.url))
        .bearer_auth(&f.data_token)
        .header("content-type", "application/json")
        .body(body.to_owned())
        .send()
        .await
        .unwrap();
    let status = resp.status().as_u16();
    (status, resp.json().await.unwrap_or(serde_json::Value::Null))
}

#[tokio::test]
async fn oversized_mutation_responses_are_uncertain() {
    let f = Fixture::start().await;
    let small = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .max_response_bytes(16)
        .build()
        .unwrap();
    let items = small.database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let id = uuidv5_for("items", "ack");

    // A committed write whose acknowledgment is too large.
    let err = items
        .put(id, &json!({"a": 1}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(
            err,
            Error::ResponseTooLarge {
                status: 201,
                may_have_applied: true,
                ..
            }
        ),
        "{err:?}"
    );
    assert!(err.is_uncertain());
    assert!(data(&f)
        .database("main")
        .collection("items")
        .get::<Box<RawValue>>(id)
        .await
        .is_ok());

    // An oversized commit_uncertain body, keyed: still not retried, still uncertain.
    let mut fault = Fault::new("POST", docs, 503, "commit_uncertain");
    fault.body =
        Some(r#"{"error":{"code":"commit_uncertain","message":"xxxxxxxxxxxxxxxxxxxxxxxx"}}"#);
    f.inject(fault);
    let before = f.count("POST", docs);
    let err = items
        .create(
            &json!({}),
            WriteOptions::new().idempotency_key(key("big-503")),
        )
        .await
        .unwrap_err();
    assert!(
        matches!(
            err,
            Error::ResponseTooLarge {
                status: 503,
                may_have_applied: true,
                ..
            }
        ),
        "{err:?}"
    );
    assert!(err.is_uncertain());
    assert_eq!(f.count("POST", docs) - before, 1);

    // A 4xx was not applied.
    let mut fault = Fault::new("PUT", docs, 412, "precondition_failed");
    fault.body = Some(r#"{"error":{"code":"precondition_failed","message":"xxxxxxxxxxxx"}}"#);
    f.inject(fault);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(
            err,
            Error::ResponseTooLarge {
                status: 412,
                may_have_applied: false,
                ..
            }
        ),
        "{err:?}"
    );
    assert!(!err.is_uncertain());
}

#[tokio::test]
async fn retry_after_http_dates_are_honoured() {
    let f = Fixture::start().await;
    let docs = "/v1/databases/main/collections/items/documents";
    let mut fault = Fault::new("PUT", docs, 429, "rate_limited");
    fault.retry_after = Some("Wed, 21 Oct 2015 07:28:00 GMT");
    f.inject(fault.clone());
    let id = uuidv5_for("items", "date");
    let items = data(&f).database("main").collection("items");
    items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(f.faults_left(), 0);
    f.inject(fault);
    let strict = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .retry_policy(RetryPolicy::none())
        .build()
        .unwrap();
    let err = strict
        .database("main")
        .collection("items")
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::RateLimited { retry_after: Some(d) } if d == Duration::ZERO),
        "{err:?}"
    );
}

#[tokio::test]
async fn number_exponents_are_bounded_and_kept_exact() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let raw = RawValue::from_string(r#"{"ten":1e0000000001,"zero":0e5}"#.into()).unwrap();
    let w = items.create(&raw, WriteOptions::new()).await.unwrap();
    let doc = items.get::<Box<RawValue>>(&w.id).await.unwrap();
    assert_eq!(
        doc.value().unwrap().get(),
        r#"{"ten":1e0000000001,"zero":0e5}"#
    );
    let users = data(&f).database("main").collection("users");
    for body in [r#"{"email":1e999999999}"#, r#"{"email":1e1025}"#] {
        let err = users
            .create(
                &RawValue::from_string(body.into()).unwrap(),
                WriteOptions::new(),
            )
            .await
            .unwrap_err();
        assert!(
            matches!(&err, Error::Api { status: 400, code, .. } if code == "invalid_request"),
            "{body}: {err:?}"
        );
    }
}

#[tokio::test]
async fn invalid_collections_and_pointers_are_rejected() {
    let f = Fixture::start().await;
    let tx = "/v1/databases/main/transactions";
    for body in [
        r#"{"ops":[{"op":"assert_count","filter":{"exists":"/a"},"lte":1}]}"#,
        r#"{"ops":[{"op":"create","collection":"","document":{}}]}"#,
        r#"{"ops":[{"op":"create","collection":7,"document":{}}]}"#,
    ] {
        let (status, e) = raw_post(&f, tx, body).await;
        assert_eq!(
            (
                status,
                e["error"]["code"].as_str(),
                e["error"]["op"].as_u64()
            ),
            (400, Some("invalid_request"), Some(0)),
            "{body}"
        );
    }
    // The fixture is still healthy.
    let items = data(&f).database("main").collection("items");
    let w = items
        .create(&json!({"a": 1}), WriteOptions::new())
        .await
        .unwrap();

    for op in [
        PatchOp::unset("name"),
        PatchOp::set("/a~2", &1).unwrap(),
        PatchOp::unset("/a~"),
    ] {
        let err = items
            .patch(&w.id, &Patch::ops(vec![op]), WriteOptions::new())
            .await
            .unwrap_err();
        assert!(
            matches!(&err, Error::Api { status: 400, code, .. } if code == "invalid_patch"),
            "{err:?}"
        );
    }
    let err = items
        .query::<Box<RawValue>>(&Query::new().filter(Filter::eq("/a~", 1)))
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 400, code, .. } if code == "invalid_query"),
        "{err:?}"
    );
    items
        .patch(
            &w.id,
            &Patch::ops(vec![PatchOp::set("/a~1b~0", &2).unwrap()]),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    let doc = items
        .get::<serde_json::Value>(&w.id)
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!(doc, json!({"a": 1, "a/b~": 2}));
}

#[tokio::test]
async fn cursors_distinguish_present_values_from_missing_keys() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    for k in ["x", "y"] {
        items
            .put(
                uuidv5_for("items", k),
                &json!({"v": {"m": 1}}),
                WriteOptions::new(),
            )
            .await
            .unwrap();
    }
    items
        .put(uuidv5_for("items", "z"), &json!({}), WriteOptions::new())
        .await
        .unwrap();
    let all = items
        .query_all::<Box<RawValue>>(Query::new().sort(SortKey::asc("/v")).limit(1), 10)
        .await
        .unwrap();
    let mut ids: Vec<_> = all.iter().map(|d| d.id.as_str()).collect();
    assert_eq!(ids.len(), 3, "{ids:?}");
    ids.sort();
    ids.dedup();
    assert_eq!(ids.len(), 3);
}

async fn raw_req(
    f: &Fixture,
    method: reqwest::Method,
    path: &str,
    content_type: Option<&str>,
    body: &str,
) -> (u16, serde_json::Value) {
    let http = reqwest::Client::builder()
        .no_proxy()
        .add_root_certificate(reqwest::Certificate::from_pem(f.ca_pem.as_bytes()).unwrap())
        .build()
        .unwrap();
    let mut rb = http
        .request(method, format!("{}{path}", f.url))
        .bearer_auth(&f.data_token)
        .body(body.to_owned());
    if let Some(ct) = content_type {
        rb = rb.header("content-type", ct);
    }
    let resp = rb.send().await.unwrap();
    let status = resp.status().as_u16();
    (status, resp.json().await.unwrap_or(serde_json::Value::Null))
}

fn code_op(v: &serde_json::Value) -> (Option<&str>, Option<u64>) {
    (v["error"]["code"].as_str(), v["error"]["op"].as_u64())
}

#[tokio::test]
async fn undecodable_mutation_acks_are_uncertain() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let mut fault = Fault::new("POST", docs, 201, "ignored");
    fault.after_commit = true;
    fault.body = Some(r#"{"id":"not-a-uuid"}"#);
    f.inject(fault);
    let err = items
        .create(&json!({"k": 1}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::UndecodableAck { status: 201, .. }),
        "{err:?}"
    );
    assert!(err.is_uncertain());
    let page = items
        .query::<Box<RawValue>>(&Query::new().count(true))
        .await
        .unwrap();
    assert_eq!(page.count.unwrap().value, 1, "the write was applied");

    let tx_path = "/v1/databases/main/transactions";
    let mut fault = Fault::new("POST", tx_path, 200, "ignored");
    fault.after_commit = true;
    fault.body = Some("{}");
    f.inject(fault);
    let mut tx = Tx::new();
    tx.create("items", None, &json!({}));
    let err = data(&f).database("main").commit(&tx).await.unwrap_err();
    assert!(
        matches!(err, Error::UndecodableAck { status: 200, .. }) && err.is_uncertain(),
        "{err:?}"
    );

    // A malformed read is a plain decode error.
    let id = uuidv5_for("items", "read");
    items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap();
    let mut fault = Fault::new("GET", docs, 200, "ignored");
    fault.body = Some("[]");
    f.inject(fault);
    let err = items.get::<Box<RawValue>>(id).await.unwrap_err();
    assert!(
        matches!(err, Error::Decode(_)) && !err.is_uncertain(),
        "{err:?}"
    );
}

#[tokio::test]
async fn fixture_rejects_wrong_media_types_and_malformed_members() {
    let f = Fixture::start().await;
    let id = uuidv5_for("items", "m");
    let doc = format!("/v1/databases/main/collections/items/documents/{id}");
    let query = "/v1/databases/main/collections/items/query";
    let tx = "/v1/databases/main/transactions";
    for ct in [None, Some("text/plain")] {
        for (m, path, body) in [
            (reqwest::Method::PUT, doc.as_str(), "{}"),
            (
                reqwest::Method::POST,
                "/v1/databases/main/collections/items/documents",
                "{}",
            ),
            (reqwest::Method::POST, query, "{}"),
            (
                reqwest::Method::POST,
                tx,
                r#"{"ops":[{"op":"create","collection":"items","document":{}}]}"#,
            ),
        ] {
            let (status, e) = raw_req(&f, m, path, ct, body).await;
            assert_eq!(
                (status, code_op(&e).0),
                (415, Some("unsupported_media_type")),
                "{path} {ct:?}"
            );
        }
    }
    let json = Some("application/json; charset=utf-8");
    for body in [
        r#"{"sort":42}"#,
        r#"{"sort":[42]}"#,
        r#"{"project":[42]}"#,
        r#"{"project":"/a"}"#,
        r#"{"project":["a"]}"#,
        r#"{"project":["/a~2"]}"#,
        r#"{"cursor":7}"#,
        r#"{"count":"yes"}"#,
        r#"{"limit":"5"}"#,
        r#"{"explain":"all"}"#,
    ] {
        let (status, e) = raw_req(&f, reqwest::Method::POST, query, json, body).await;
        assert_eq!(
            (status, code_op(&e).0),
            (400, Some("invalid_query")),
            "{body}"
        );
    }
    let (status, _) = raw_req(
        &f,
        reqwest::Method::POST,
        query,
        json,
        r#"{"project":["/a"],"count":true}"#,
    )
    .await;
    assert_eq!(status, 200);

    // Count bounds: one valid and one invalid bound is rejected.
    for body in [
        r#"{"ops":[{"op":"assert_count","collection":"items","filter":{"exists":"/a"},"gte":1.5,"lte":10}]}"#,
        r#"{"ops":[{"op":"assert_count","collection":"items","filter":{"exists":"/a"},"gte":0,"lte":-1}]}"#,
    ] {
        let (status, e) = raw_req(&f, reqwest::Method::POST, tx, json, body).await;
        assert_eq!(
            (status, code_op(&e)),
            (400, (Some("invalid_request"), Some(0))),
            "{body}"
        );
    }

    // Transaction ops patches need 1-256 ops.
    let (status, _) = raw_req(&f, reqwest::Method::PUT, &doc, json, r#"{"n":0}"#).await;
    assert_eq!(status, 201);
    let inc = r#"{"op":"inc","path":"/n","by":1}"#;
    let patch = |n: usize| {
        format!(
            r#"{{"ops":[{{"op":"patch","collection":"items","id":"{id}","ops":[{}]}}]}}"#,
            vec![inc; n].join(",")
        )
    };
    for n in [0, 257] {
        let (status, e) = raw_req(&f, reqwest::Method::POST, tx, json, &patch(n)).await;
        assert_eq!(
            (status, code_op(&e)),
            (400, (Some("invalid_patch"), Some(0))),
            "{n} ops"
        );
    }
    for n in [1, 256] {
        let (status, _) = raw_req(&f, reqwest::Method::POST, tx, json, &patch(n)).await;
        assert_eq!(status, 200, "{n} ops");
    }
    let n = data(&f)
        .database("main")
        .collection("items")
        .get::<serde_json::Value>(id)
        .await
        .unwrap();
    assert_eq!(n.into_value().unwrap()["n"], json!(257));
}

#[tokio::test]
async fn future_retry_after_dates_delay_the_retry() {
    let f = Fixture::start().await;
    let docs = "/v1/databases/main/collections/items/documents";
    let items = data(&f).database("main").collection("items");
    let id = uuidv5_for("items", "future");
    let path = format!("{docs}/{id}");

    // The retry must not arrive before the HTTP-date (same clock as the
    // fixture's arrival timestamps), whatever the scheduling delays.
    let not_before = std::time::SystemTime::now() + Duration::from_secs(2);
    let date: &'static str = Box::leak(httpdate::fmt_http_date(not_before).into_boxed_str());
    let not_before = httpdate::parse_http_date(date).unwrap();
    let mut fault = Fault::new("PUT", docs, 429, "rate_limited");
    fault.retry_after = Some(date);
    f.inject(fault);
    items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap();
    let times = f.request_times("PUT", &path);
    assert_eq!(times.len(), 2);
    assert!(
        times[1] >= not_before,
        "retried {:?} before the date",
        not_before.duration_since(times[1])
    );

    // A far date is capped by max_retry_after, which is still waited for.
    let cap = Duration::from_millis(200);
    let capped = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .retry_policy(RetryPolicy {
            max_retry_after: cap,
            ..RetryPolicy::default()
        })
        .build()
        .unwrap();
    let far: &'static str = Box::leak(
        httpdate::fmt_http_date(std::time::SystemTime::now() + Duration::from_secs(3600))
            .into_boxed_str(),
    );
    let mut fault = Fault::new("PUT", docs, 429, "rate_limited");
    fault.retry_after = Some(far);
    f.inject(fault);
    capped
        .database("main")
        .collection("items")
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap();
    let times = f.request_times("PUT", &path);
    assert_eq!(times.len(), 4);
    let gap = times[3].duration_since(times[2]).unwrap();
    assert!(gap >= cap && gap < Duration::from_secs(30), "{gap:?}");
    assert_eq!(f.faults_left(), 0);
}

#[tokio::test]
async fn non_4xx_mutation_failures_are_uncertain() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    for (status, body) in [
        (502, Some("<html>bad gateway</html>")),
        (504, Some("")),
        (500, Some(r#"{"error":"boom"}"#)),
        (503, Some(r#"{"unexpected":true}"#)),
        (503, None),
    ] {
        let mut fault = Fault::new("POST", docs, status, "mystery");
        fault.after_commit = true;
        fault.body = body;
        f.inject(fault);
        let before = f.count("POST", docs);
        let err = items
            .create(&json!({}), WriteOptions::new())
            .await
            .unwrap_err();
        assert!(
            matches!(err, Error::Api { status: s, .. } if s == status),
            "{err:?}"
        );
        assert!(err.is_uncertain(), "{status} {body:?}");
        assert_eq!(
            f.count("POST", docs) - before,
            1,
            "unkeyed writes are not retried"
        );
    }
    let page = items
        .query::<Box<RawValue>>(&Query::new().count(true))
        .await
        .unwrap();
    assert_eq!(page.count.unwrap().value, 5, "every write committed");
    // 4xx failures stay certain.
    let err = items
        .put(
            uuidv5_for("items", "x"),
            &json!({}),
            WriteOptions::new().if_match("tbv1-00000000000000000000000000000000".parse().unwrap()),
        )
        .await
        .unwrap_err();
    assert!(!err.is_uncertain());
}

#[tokio::test]
async fn malformed_acknowledgements_and_documents_are_rejected() {
    let f = Fixture::start().await;
    let db = data(&f).database("main");
    let items = db.collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let id = uuidv5_for("items", "ack");
    let path = format!("{docs}/{id}");
    let v = "tbv1-00000000000000000000000000000000";

    // A write acknowledgement for a different document.
    let mut fault = Fault::new("PUT", docs, 200, "x");
    fault.after_commit = true;
    let other = format!(
        r#"{{"id":"{}","version":"{v}","offset":"9","durability":"local_durable"}}"#,
        uuidv5_for("items", "other")
    );
    fault.body = Some(Box::leak(other.into_boxed_str()));
    f.inject(fault);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::UndecodableAck { status: 200, .. }) && err.is_uncertain(),
        "{err:?}"
    );

    // Transaction results that do not match the submitted ops.
    let tx_path = "/v1/databases/main/transactions";
    let mut tx = Tx::new();
    tx.put("items", id, &json!({}), None).assert_count(
        "items",
        Filter::exists("/a"),
        CountBounds::at_most(9),
    );
    for body in [
        r#"{"offset":"1","durability":"local_durable","results":[]}"#.to_owned(),
        r#"{"offset":"1","durability":"local_durable","results":[{"asserted":true},{"asserted":true,"count":"0"}]}"#.to_owned(),
        format!(r#"{{"offset":"1","durability":"local_durable","results":[{{"id":"{}","version":"{v}"}},{{"asserted":true,"count":"0"}}]}}"#, uuidv5_for("items", "other")),
        format!(r#"{{"offset":"1","durability":"local_durable","results":[{{"id":"{id}","version":"{v}"}},{{"asserted":true}}]}}"#),
    ] {
        let mut fault = Fault::new("POST", tx_path, 200, "x");
        fault.after_commit = true;
        fault.body = Some(Box::leak(body.clone().into_boxed_str()));
        f.inject(fault);
        let err = db.commit(&tx).await.unwrap_err();
        assert!(matches!(err, Error::UndecodableAck { status: 200, .. }), "{body}: {err:?}");
    }
    db.commit(&tx).await.unwrap();

    // Document reads: wrong ID, empty conflicts, inconsistent siblings.
    let ev = r#"{"replica":"0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4a","incarnation":"8f14e45fceea167a5a36dedd4bea2543","sequence":"1"}"#;
    for body in [
        format!(
            r#"{{"id":"{}","version":"{v}","state":"value","document":{{}}}}"#,
            uuidv5_for("items", "other")
        ),
        format!(r#"{{"id":"{id}","version":"{v}","state":"conflict","siblings":[]}}"#),
        format!(
            r#"{{"id":"{id}","version":"{v}","state":"conflict","siblings":[{{"event":{ev},"deleted":false}}]}}"#
        ),
        format!(
            r#"{{"id":"{id}","version":"{v}","state":"conflict","siblings":[{{"event":{ev},"deleted":false,"document":null}}]}}"#
        ),
        format!(
            r#"{{"id":"{id}","version":"{v}","state":"conflict","siblings":[{{"event":{ev},"deleted":true,"document":{{}}}}]}}"#
        ),
    ] {
        let mut fault = Fault::new("GET", &path, 200, "x");
        fault.body = Some(Box::leak(body.clone().into_boxed_str()));
        f.inject(fault);
        let err = items.get::<serde_json::Value>(id).await.unwrap_err();
        assert!(
            matches!(err, Error::Decode(_)) && !err.is_uncertain(),
            "{body}: {err:?}"
        );
    }
    assert_eq!(f.faults_left(), 0);
}
