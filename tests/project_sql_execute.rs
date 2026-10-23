#[path = "support/project_http.rs"]
mod http;
use std::collections::HashMap;

use http::{reply, Fixture, PROJECT};
use serde::Deserialize;
use serde_json::{json, Value};
use terrascale::v1::{Error, SqlStatement, SqlValue};

#[derive(Debug, Deserialize, PartialEq)]
struct User {
    id: i64,
    name: String,
    score: Option<f64>,
}

#[tokio::test]
async fn execute_query_and_batch_send_the_execute_contract() {
    let insert =
        json!({"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7}]});
    let select = json!({"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"score","decltype":null}],"rows":[[7,"Ada",1.5],[9007199254740993i64,"Bob",null]],"rows_affected":0,"last_insert_rowid":null}]});
    let batch = json!({"results":[
        {"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":8},
        {"columns":[{"name":"n","decltype":null}],"rows":[[2]],"rows_affected":0,"last_insert_rowid":null}]});
    let fixture = Fixture::new(vec![
        reply(200, &insert.to_string()),
        reply(200, &select.to_string()),
        reply(200, &select.to_string()),
        reply(200, &batch.to_string()),
    ])
    .await;
    let sql = fixture.client().database("orders").sql();

    let result = sql
        .execute(
            "INSERT INTO users(name, active, score) VALUES (?, ?, ?)",
            &["Ada".into(), true.into(), SqlValue::Null],
        )
        .await
        .unwrap();
    assert_eq!(
        (result.rows_affected, result.last_insert_rowid),
        (1, Some(7))
    );

    let result = sql
        .execute("SELECT id, name, score FROM users", &[])
        .await
        .unwrap();
    assert_eq!(result.columns[0].decltype.as_deref(), Some("INTEGER"));
    assert_eq!(result.columns[2].decltype, None);
    assert_eq!(
        result.rows[1],
        [
            SqlValue::Integer(9007199254740993),
            SqlValue::Text("Bob".into()),
            SqlValue::Null
        ]
    );
    assert_eq!(result.rows[0][2], SqlValue::Real(1.5));
    let maps = result.decode_rows::<HashMap<String, SqlValue>>().unwrap();
    assert_eq!(maps[0]["name"], SqlValue::Text("Ada".into()));

    let users: Vec<User> = sql
        .query("SELECT id, name, score FROM users", &[])
        .await
        .unwrap();
    assert_eq!(
        users[0],
        User {
            id: 7,
            name: "Ada".into(),
            score: Some(1.5)
        }
    );

    let results = sql
        .batch(vec![
            SqlStatement::new("INSERT INTO users(name) VALUES (:name)").named("name", "Cy"),
            SqlStatement::new("SELECT count(*) AS n FROM users WHERE id > ?").args([0]),
        ])
        .await
        .unwrap();
    assert_eq!(results[1].rows, [[SqlValue::Integer(2)]]);

    let requests = fixture.requests.lock().unwrap();
    for request in requests.iter() {
        assert_eq!(request.method, "POST");
        assert_eq!(
            request.uri,
            format!("/v1/projects/{PROJECT}/databases/orders/sql/execute")
        );
        assert_eq!(request.headers["authorization"], "Bearer fixture-token");
        assert_eq!(request.headers["content-type"], "application/json");
    }
    let body = |index: usize| serde_json::from_slice::<Value>(&requests[index].body).unwrap();
    assert_eq!(
        body(0),
        json!({"statements":[{"sql":"INSERT INTO users(name, active, score) VALUES (?, ?, ?)","args":["Ada",1,null]}]})
    );
    assert_eq!(
        body(1),
        json!({"statements":[{"sql":"SELECT id, name, score FROM users"}]})
    );
    assert_eq!(
        body(3),
        json!({"statements":[
            {"sql":"INSERT INTO users(name) VALUES (:name)","named":{"name":"Cy"}},
            {"sql":"SELECT count(*) AS n FROM users WHERE id > ?","args":[0]}]})
    );
}

#[tokio::test]
async fn sql_failures_are_typed_and_generic_errors_stay_generic() {
    let constraint = json!({"error":{"code":"sql_error","message":"UNIQUE constraint failed: users.id","statement":1,"sqlite_code":"SQLITE_CONSTRAINT_PRIMARYKEY"}});
    let scope = json!({"error":{"code":"forbidden_scope","message":"writes need databases:write","statement":0}});
    let fixture = Fixture::new(vec![
        reply(400, &constraint.to_string()),
        reply(403, &scope.to_string()),
        reply(
            409,
            r#"{"error":{"code":"sql_conflict","message":"retry"}}"#,
        ),
        reply(
            404,
            r#"{"error":{"code":"not_found","message":"no database"}}"#,
        ),
        reply(200, r#"{"results":[]}"#),
    ])
    .await;
    let sql = fixture.client().database("orders").sql();

    let error = sql.execute("INSERT", &[]).await.unwrap_err();
    let typed = error.sql().expect("typed SQL error");
    assert_eq!(typed.code, "sql_error");
    assert_eq!(typed.statement, Some(1));
    assert_eq!(
        typed.sqlite_code.as_deref(),
        Some("SQLITE_CONSTRAINT_PRIMARYKEY")
    );
    assert_eq!(
        (error.status(), error.code()),
        (Some(400), Some("sql_error"))
    );
    assert_eq!(error.request_id(), Some("parity-fixture"));
    assert!(!error.is_uncertain());

    let error = sql.execute("DELETE", &[]).await.unwrap_err();
    assert_eq!(error.sql().unwrap().code, "forbidden_scope");
    assert_eq!(error.status(), Some(403));

    let error = sql.execute("UPDATE", &[]).await.unwrap_err();
    let typed = error.sql().unwrap();
    assert_eq!(
        (typed.code.as_str(), typed.statement),
        ("sql_conflict", None)
    );

    let error = sql.execute("SELECT 1", &[]).await.unwrap_err();
    assert!(error.sql().is_none() && error.is_not_found());

    let error = sql.execute("SELECT 1", &[]).await.unwrap_err();
    assert!(
        matches!(&error, Error::Response { error, .. } if matches!(error.as_ref(), Error::UndecodableAck { .. }))
    );
}

#[tokio::test]
async fn invalid_statements_are_rejected_before_sending() {
    let fixture = Fixture::new(vec![]).await;
    let sql = fixture.client().database("orders").sql();
    for statements in [
        vec![],
        vec![SqlStatement::new("SELECT 1"); 257],
        vec![SqlStatement::new("")],
        vec![SqlStatement::new("x".repeat(16 * 1024 + 1))],
        vec![SqlStatement::new("SELECT :a").named(":a", 1)],
        vec![SqlStatement::new("SELECT ?").args([f64::NAN])],
    ] {
        assert!(matches!(
            sql.batch(statements).await,
            Err(Error::InvalidInput(_))
        ));
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}
