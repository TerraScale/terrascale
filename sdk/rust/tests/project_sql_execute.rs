#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde::Deserialize;
use serde_json::{json, Value};
use terrascale::v1::{sql_error_codes, Error, SqlStatement, SqlValue};

#[derive(Debug, Deserialize, PartialEq)]
struct User {
    id: i64,
    name: String,
    avatar: Option<String>,
}

#[tokio::test]
async fn execute_query_and_batch_send_the_execute_contract() {
    let insert =
        json!({"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7}]});
    let select = json!({"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"avatar","decltype":"BLOB"}],"rows":[[9007199254740993i64,"Ada",{"base64":"AAEC"}],[8,"Bob",null]],"rows_affected":0,"last_insert_rowid":null}]});
    let batch = json!({"results":[
        {"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":8},
        {"columns":[{"name":"n","decltype":null}],"rows":[[2.5]],"rows_affected":0,"last_insert_rowid":null}]});
    let fixture = Fixture::new(vec![
        reply(200, &insert.to_string()),
        reply(200, &select.to_string()),
        reply(200, &select.to_string()),
        reply(200, &batch.to_string()),
    ])
    .await;
    let db = fixture.client().database("orders").sql();

    let result = db
        .execute(
            "INSERT INTO users(name, active, avatar) VALUES (?, ?, ?)",
            &["Ada".into(), true.into(), vec![0u8, 1, 2].into()],
        )
        .await
        .unwrap();
    assert_eq!(
        (result.rows_affected, result.last_insert_rowid),
        (1, Some(7))
    );

    let rows = db
        .query_named(
            "SELECT id, name, avatar FROM users WHERE id > :id",
            [("id", 0)],
        )
        .await
        .unwrap();
    assert_eq!(rows[0].get_i64("id").unwrap(), 9007199254740993);
    assert_eq!(rows[0].get_str(1).unwrap(), "Ada");
    assert_eq!(rows[0].get_bytes("avatar").unwrap(), [0, 1, 2]);
    assert!(rows[1].is_null("avatar").unwrap());

    let users: Vec<User> = db
        .query_as("SELECT id, name, avatar FROM users", &[])
        .await
        .unwrap();
    assert_eq!(
        users[0],
        User {
            id: 9007199254740993,
            name: "Ada".into(),
            avatar: Some("AAEC".into())
        }
    );

    let results = db
        .batch(vec![
            SqlStatement::new("INSERT INTO users(name) VALUES (:name)").named("name", "Cy"),
            SqlStatement::new("SELECT avg(id) AS n FROM users WHERE id > ?").args([0]),
        ])
        .await
        .unwrap();
    assert_eq!(results[1].rows, [[SqlValue::Real(2.5)]]);

    let requests = fixture.requests.lock().unwrap();
    for request in requests.iter() {
        assert_eq!(request.method, "POST");
        assert_eq!(
            request.uri,
            format!("/v1/projects/{PROJECT}/databases/orders/sql/execute")
        );
        assert_eq!(request.headers["authorization"], "Bearer fixture-token");
    }
    let body = |index: usize| serde_json::from_slice::<Value>(&requests[index].body).unwrap();
    assert_eq!(
        body(0),
        json!({"statements":[{"sql":"INSERT INTO users(name, active, avatar) VALUES (?, ?, ?)","args":["Ada",1,{"base64":"AAEC"}]}]})
    );
    assert_eq!(
        body(1),
        json!({"statements":[{"sql":"SELECT id, name, avatar FROM users WHERE id > :id","named":{"id":0}}]})
    );
    assert_eq!(
        body(3),
        json!({"statements":[
            {"sql":"INSERT INTO users(name) VALUES (:name)","named":{"name":"Cy"}},
            {"sql":"SELECT avg(id) AS n FROM users WHERE id > ?","args":[0]}]})
    );
}

#[tokio::test]
async fn refusals_are_typed_and_only_serialization_conflicts_are_resent() {
    let codes: Value = serde_json::from_str(include_str!(
        "../../dotnet/contracts/terrabase/sql/error-codes.json"
    ))
    .unwrap();
    let error = |code: &str, statement: usize| {
        let entry = codes["codes"]
            .as_array()
            .unwrap()
            .iter()
            .find(|entry| entry["code"] == code)
            .unwrap();
        let body = json!({"error":{"code":code,"message":"refused","statement":statement,"sqlstate":entry["sqlstate"]}});
        reply(entry["status"].as_u64().unwrap() as u16, &body.to_string())
    };
    let ok =
        json!({"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]});
    let fixture = Fixture::new(vec![
        error(sql_error_codes::PRIMARY_KEY_VIOLATION, 1),
        error(sql_error_codes::SERIALIZATION_CONFLICT, 0),
        error(sql_error_codes::SERIALIZATION_CONFLICT, 0),
        reply(200, &ok.to_string()),
        reply(
            404,
            r#"{"error":{"code":"not_found","message":"no database"}}"#,
        ),
    ])
    .await;
    let db = fixture.client().database("orders").sql();

    let failed = db
        .batch(vec!["SELECT 1".into(), "INSERT INTO t VALUES (1)".into()])
        .await
        .unwrap_err();
    let refused = failed.sql().expect("typed SQL error");
    assert_eq!(
        (
            refused.code.as_str(),
            refused.statement,
            refused.sqlstate.as_deref()
        ),
        (sql_error_codes::PRIMARY_KEY_VIOLATION, 1, Some("23505"))
    );
    assert_eq!(failed.status(), Some(400));
    assert!(!refused.is_transient() && !failed.is_uncertain());

    let result = db.execute("UPDATE t SET v = 2", &[]).await.unwrap();
    assert_eq!(result.rows_affected, 1);

    let missing = db.execute("SELECT 1", &[]).await.unwrap_err();
    assert!(missing.sql().is_none() && missing.is_not_found());
    assert_eq!(fixture.requests.lock().unwrap().len(), 5);
}

#[tokio::test]
async fn invalid_statements_are_rejected_before_sending() {
    let fixture = Fixture::new(vec![]).await;
    let db = fixture.client().database("orders").sql();
    for statements in [
        vec![],
        vec![SqlStatement::new("SELECT 1"); 257],
        vec![SqlStatement::new(" ")],
        vec![SqlStatement::new("SELECT :a").named(":a", 1)],
        vec![SqlStatement::new("SELECT ?").args([f64::NAN])],
    ] {
        assert!(matches!(
            db.batch(statements).await,
            Err(Error::InvalidInput(_))
        ));
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}
