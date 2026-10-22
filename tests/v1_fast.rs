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
