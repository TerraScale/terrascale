use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::schema::{CollectionSchema, FieldDef, FieldType};
use terrascale::sql::{
    metadata_field, validate_parameters, validate_sql, Direction, Query, SqlExecuteOptions, SqlRow,
    SqlRows,
};
use terrascale::{
    ClientConfig, DatabaseScope, HttpTransport, Request, Response, Result, TerraScaleClient,
};

#[derive(Debug, Default)]
struct RecordingTransport {
    request: Mutex<Option<Request>>,
}

#[async_trait]
impl HttpTransport for RecordingTransport {
    async fn send(&self, request: Request) -> Result<Response> {
        *self.request.lock().expect("request mutex") = Some(request);
        Ok(Response::json(200, &json!({"matched": 0, "rows": []})))
    }
}

#[test]
fn schema_builder_parameterizes_fields_predicates_match_and_pagination() {
    let name = FieldDef::required("name", FieldType::String).indexed();
    let age = FieldDef::required("age", FieldType::Integer).indexed();
    let schema = CollectionSchema::new("users")
        .field(name.clone())
        .field(age.clone());

    let query = Query::select_schema(&schema)
        .expect("schema")
        .column(&name)
        .expect("column")
        .where_eq(&age, json!(18))
        .expect("predicate")
        .where_match(&name, "alice")
        .expect("match")
        .order_by_field(&age, Direction::Desc)
        .expect("order")
        .limit(10)
        .offset(5)
        .build()
        .expect("query");

    assert_eq!(
        query.sql(),
        "SELECT name FROM users WHERE age = $1 AND name MATCH $2 ORDER BY age DESC LIMIT $3 OFFSET $4"
    );
    assert_eq!(
        query.params(),
        &[json!(18), json!("alice"), json!(10), json!(5)]
    );
}

#[test]
fn schema_builder_rejects_undeclared_and_non_text_match_fields() {
    let name = FieldDef::required("name", FieldType::String);
    let age = FieldDef::required("age", FieldType::Integer);
    let other = FieldDef::required("other", FieldType::String);
    let schema = CollectionSchema::new("users")
        .field(name)
        .field(age.clone());
    let builder = Query::select_schema(&schema).expect("schema");

    assert!(builder.clone().column(&other).is_err());
    assert!(builder.where_match(&age, "18").is_err());
}

#[test]
fn raw_sql_accepts_read_only_statements_and_rejects_writes_and_multiple_statements() {
    for statement in [
        "SELECT * FROM users WHERE id = $1",
        "WITH recent AS (SELECT * FROM users) SELECT * FROM recent",
        "EXPLAIN SELECT * FROM users",
        "EXPLAIN QUERY PLAN SELECT * FROM users",
    ] {
        let params = if statement.contains("$1") {
            vec![json!("doc-1")]
        } else {
            Vec::new()
        };
        assert!(Query::try_raw(statement, params).is_ok());
    }
    for statement in [
        "INSERT INTO users VALUES ($1)",
        "UPDATE users SET name = $1",
        "DELETE FROM users",
        "CREATE TABLE users (id INTEGER)",
        "SELECT * FROM users; SELECT * FROM users",
        "SELECT * FROM users -- DROP TABLE users",
        "SELECT random() FROM users",
        "SELECT CURRENT_TIMESTAMP FROM users",
        "WITH RECURSIVE recent AS (SELECT * FROM users) SELECT * FROM recent",
        "SELECT sqlite_master FROM users",
    ] {
        assert!(
            Query::try_raw(statement, vec![json!("x")]).is_err(),
            "{statement}"
        );
    }
    assert!(Query::raw("DROP TABLE users", Vec::new()).is_err());
    assert!(Query::select(&["id"])
        .from("users")
        .expect("collection")
        .where_("age = $1 OR 1 = 1")
        .param(json!(18))
        .build()
        .is_err());
}

#[test]
fn raw_sql_binding_count_is_checked() {
    assert!(validate_sql("SELECT * FROM users WHERE id = $1", &[]).is_err());
    assert!(validate_sql("SELECT * FROM users", &[json!("unused")]).is_err());
    assert!(validate_sql("SELECT * FROM users WHERE id = $1", &[json!("doc-1")]).is_ok());
}

#[test]
fn named_and_ordinal_bindings_are_canonical_objects() {
    let parameters = BTreeMap::from([
        ("1".to_owned(), json!(18)),
        ("name".to_owned(), json!("Ada")),
    ]);
    let query = Query::try_raw(
        "SELECT * FROM users WHERE age >= $1 AND name = :name",
        parameters.clone(),
    )
    .expect("bindings");
    assert_eq!(query.parameters(), &parameters);
    assert_eq!(query.bindings(), &parameters);

    let query = Query::try_raw(
        "SELECT * FROM users WHERE age >= ? AND name = ?2",
        vec![json!(18), json!("Ada")],
    )
    .expect("ordinal bindings");
    assert_eq!(
        query.parameters(),
        &BTreeMap::from([("1".to_owned(), json!(18)), ("2".to_owned(), json!("Ada")),])
    );
}

#[test]
fn typed_builder_can_bind_named_compatibility_predicates() {
    let query = Query::select(&["*"])
        .from("users")
        .expect("collection")
        .where_("name = :name")
        .bind("name", json!("Ada"))
        .expect("binding")
        .build()
        .expect("query");
    assert_eq!(query.sql(), "SELECT * FROM users WHERE name = :name");
    assert_eq!(
        query.parameters(),
        &BTreeMap::from([("name".to_owned(), json!("Ada")),])
    );
}

#[test]
fn metadata_columns_and_sql_bounds_are_fail_closed() {
    let schema =
        CollectionSchema::new("users").field(FieldDef::required("name", FieldType::String));
    let id = metadata_field("_id").expect("metadata");
    let query = Query::select_schema(&schema)
        .expect("schema")
        .column(&id)
        .expect("metadata column")
        .order_by("_updated", Direction::Desc)
        .expect("metadata order")
        .build()
        .expect("query");
    assert_eq!(query.sql(), "SELECT _id FROM users ORDER BY _updated DESC");

    assert!(validate_parameters(
        "SELECT * FROM users LIMIT $1",
        &BTreeMap::from([(String::from("1"), json!(1025))]),
    )
    .is_err());
    assert!(validate_parameters("SELECT * FROM users LIMIT 0", &BTreeMap::new()).is_err());
    assert!(validate_parameters(
        "SELECT * FROM users OFFSET $1",
        &BTreeMap::from([(String::from("1"), json!("not-an-integer")),])
    )
    .is_err());
}

#[test]
fn response_shape_is_matched_rows_with_id_and_value() {
    let wire = json!({
        "matched": 2,
        "rows": [{"id": "doc-1", "value": {"name": "Ada"}}]
    });
    let result: SqlRows = serde_json::from_value(wire).expect("sql response");
    assert_eq!(result.matched, 2);
    assert_eq!(
        result.rows,
        vec![SqlRow {
            id: "doc-1".to_owned(),
            value: json!({"name": "Ada"}),
        }]
    );
}

#[test]
fn controls_reject_zero_and_values_above_the_deadline_bound() {
    assert!(SqlExecuteOptions {
        timeout_ms: Some(0),
        ..SqlExecuteOptions::default()
    }
    .validate()
    .is_err());
    assert!(SqlExecuteOptions {
        timeout_ms: Some(30_001),
        ..SqlExecuteOptions::default()
    }
    .validate()
    .is_err());
    assert!(SqlExecuteOptions {
        timeout_ms: Some(30_000),
        ..SqlExecuteOptions::default()
    }
    .validate()
    .is_ok());
}

#[tokio::test]
async fn execute_posts_exact_canonical_sql_wire_shape() {
    let transport = Arc::new(RecordingTransport::default());
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .project_id("project")
            .build()
            .expect("config"),
        transport.clone(),
    );
    let query = Query::try_raw(
        "SELECT * FROM users WHERE name = :name",
        BTreeMap::from([("name".to_owned(), json!("Ada"))]),
    )
    .expect("query")
    .explain()
    .timeout_ms(500)
    .expect("timeout")
    .cancelled();
    sdk.sql()
        .execute(&DatabaseScope::new("db"), &query)
        .await
        .expect("execute");
    let request = transport
        .request
        .lock()
        .expect("request mutex")
        .clone()
        .expect("request");
    assert_eq!(request.route_name, "sql.execute");
    let body: serde_json::Value =
        serde_json::from_slice(request.body.as_deref().expect("body")).expect("JSON body");
    assert_eq!(
        body,
        json!({
            "sql": "SELECT * FROM users WHERE name = :name",
            "parameters": {"name": "Ada"},
            "explain": true,
            "timeout_ms": 500,
            "cancelled": true,
        })
    );
}
