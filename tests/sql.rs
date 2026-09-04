use serde_json::json;
use terrascale::schema::{CollectionSchema, FieldDef, FieldType};
use terrascale::sql::{validate_sql, Direction, Query};

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
