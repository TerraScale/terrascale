//! Focused public contract tests for the D-54 SQL allow-list.

use terrabase::{
    MAX_SQL_TEXT_BYTES, QueryInputError, SqlQueryText,
    query::sql::{SqlErrorClass, SqlStatementKind, parse},
};

#[test]
fn public_query_text_constructor_uses_the_ast_allow_list() {
    SqlQueryText::try_new("SELECT body ->> '$.status' FROM orders")
        .expect("the public input admits a read-only SQLite query");
    assert_eq!(
        SqlQueryText::try_new("SELECT random() FROM orders"),
        Err(QueryInputError::UnsupportedGrammar)
    );
    assert_eq!(
        SqlQueryText::try_new("SELECT * FROM orders; DELETE FROM orders"),
        Err(QueryInputError::UnsupportedGrammar)
    );
}

#[test]
fn accepts_sqlite_select_with_json_metadata_and_placeholders() {
    let text = "SELECT _id, body ->> '$.customer' AS customer FROM orders \
                WHERE total >= ?1 ORDER BY _updated DESC LIMIT 25";
    let query = parse(text, MAX_SQL_TEXT_BYTES).expect("SQLite SELECT should pass");

    assert_eq!(query.kind(), SqlStatementKind::Select);
    assert_eq!(query.as_str(), text);
}

#[test]
fn accepts_with_and_explain_query_plan() {
    let with = parse(
        "WITH recent AS (SELECT * FROM orders WHERE _updated > :cursor) \
         SELECT _id FROM recent ORDER BY _id",
        MAX_SQL_TEXT_BYTES,
    )
    .expect("WITH SELECT should pass");
    assert_eq!(with.kind(), SqlStatementKind::With);

    let explain =
        parse("EXPLAIN QUERY PLAN SELECT * FROM orders WHERE total > ?", MAX_SQL_TEXT_BYTES)
            .expect("SQLite EXPLAIN QUERY PLAN should pass");
    assert_eq!(explain.kind(), SqlStatementKind::Explain);
}

#[test]
fn rejects_empty_oversized_invalid_and_multiple_statements() {
    assert_eq!(parse(" \n\t", MAX_SQL_TEXT_BYTES).unwrap_err().class(), SqlErrorClass::ParseError);
    assert_eq!(
        parse(&"x".repeat(MAX_SQL_TEXT_BYTES + 1), MAX_SQL_TEXT_BYTES).unwrap_err().class(),
        SqlErrorClass::UnsupportedStatement
    );
    assert_eq!(
        parse("SELECT FROM", MAX_SQL_TEXT_BYTES).unwrap_err().class(),
        SqlErrorClass::ParseError
    );
    assert_eq!(
        parse("SELECT * FROM orders; SELECT * FROM users", MAX_SQL_TEXT_BYTES).unwrap_err().class(),
        SqlErrorClass::UnsupportedStatement
    );
}

#[test]
fn rejects_non_query_statement_families() {
    for text in [
        "INSERT INTO orders (_id) VALUES ('1')",
        "UPDATE orders SET total = 0",
        "DELETE FROM orders",
        "CREATE TABLE stolen (id TEXT)",
        "SELECT * INTO stolen FROM orders",
        "DROP TABLE orders",
        "PRAGMA table_info(orders)",
        "ATTACH DATABASE '/tmp/other.db' AS other",
        "DETACH DATABASE other",
        "EXPLAIN DELETE FROM orders",
        "VALUES (1)",
    ] {
        assert!(
            matches!(
                parse(text, MAX_SQL_TEXT_BYTES).unwrap_err().class(),
                SqlErrorClass::UnsupportedStatement
                    | SqlErrorClass::ForbiddenKeyword
                    | SqlErrorClass::ParseError
            ),
            "{text}"
        );
    }
}

#[test]
fn rejects_volatile_and_host_sensitive_functions_at_any_depth() {
    for text in [
        "SELECT random() FROM orders",
        "SELECT * FROM orders WHERE token = randomblob(16)",
        "WITH x AS (SELECT sqlite_version() AS version FROM orders) SELECT * FROM x",
        "SELECT (SELECT load_extension('bad')) FROM orders",
        "SELECT CURRENT_TIMESTAMP FROM orders",
        "SELECT datetime('now') FROM orders",
    ] {
        assert_eq!(
            parse(text, MAX_SQL_TEXT_BYTES).unwrap_err().class(),
            SqlErrorClass::ForbiddenFunction,
            "{text}"
        );
    }
}

#[test]
fn rejects_internal_qualified_and_table_valued_relations() {
    for (text, class) in [
        ("SELECT * FROM sqlite_master", SqlErrorClass::ForbiddenRelation),
        ("SELECT * FROM main.orders", SqlErrorClass::ForbiddenRelation),
        ("SELECT * FROM pragma_table_info('orders')", SqlErrorClass::ForbiddenTableFunction),
        ("SELECT * FROM json_each(body)", SqlErrorClass::ForbiddenTableFunction),
    ] {
        assert_eq!(parse(text, MAX_SQL_TEXT_BYTES).unwrap_err().class(), class, "{text}");
    }
}

#[test]
fn deterministic_scalar_aggregate_and_window_functions_remain_available() {
    for text in [
        "SELECT lower(body ->> '$.name') FROM users",
        "SELECT count(*) FROM orders",
        "SELECT row_number() OVER (ORDER BY _id) FROM orders",
        "SELECT json_extract(body, '$.status') FROM orders",
    ] {
        parse(text, MAX_SQL_TEXT_BYTES).unwrap_or_else(|error| panic!("{text}: {error:?}"));
    }
}
