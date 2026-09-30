//! Installed-crate journey; the producer owns the signed TLS listener.
use std::time::Duration;
use terrascale::postgres::{PostgresClient, PostgresConfig, PostgresQuery, PostgresValue};

fn required(name: &str) -> String {
    std::env::var(name).unwrap_or_else(|_| panic!("missing {name}"))
}
fn identifier(name: &str) -> String {
    let value = required(name);
    assert!(
        !value.is_empty()
            && value.bytes().enumerate().all(|(index, byte)| byte == b'_'
                || byte.is_ascii_lowercase()
                || index > 0 && byte.is_ascii_digit()),
        "invalid {name}"
    );
    format!("\"{value}\"")
}
fn query(statement: impl Into<String>, parameters: Vec<PostgresValue>) -> PostgresQuery {
    PostgresQuery::new(statement, parameters).expect("valid journey descriptor")
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut client = PostgresClient::connect(PostgresConfig {
        host: required("TERRASCALE_PG_HOST"),
        port: required("TERRASCALE_PG_PORT")
            .parse()
            .map_err(|_| "invalid TERRASCALE_PG_PORT")?,
        tls_server_name: required("TERRASCALE_PG_TLS_SERVER_NAME"),
        ca_certificate_pem: std::fs::read(required("TERRASCALE_PG_CA_FILE"))
            .map_err(|_| "cannot read TERRASCALE_PG_CA_FILE")?,
        database: required("TERRASCALE_PG_DATABASE"),
        key_id: required("TERRASCALE_PG_KEY_ID"),
        password: required("TERRASCALE_PG_PASSWORD"),
        connect_timeout: Duration::from_secs(5),
    })
    .await?;
    let table = identifier("TERRASCALE_PG_TABLE");
    let key = identifier("TERRASCALE_PG_KEY_COLUMN");
    let payload_column = identifier("TERRASCALE_PG_PAYLOAD_COLUMN");
    let payload = "quote ' ; DROP TABLE sentinel; -- \\ Unicode α 🌍";
    let transaction = client.transaction().await?;
    let inserted = transaction
        .execute(&query(
            format!("INSERT INTO {table} ({key},{payload_column}) VALUES ($1,$2),($3,$4)"),
            vec![
                PostgresValue::Int8(i64::MIN),
                PostgresValue::Text(payload.to_owned()),
                PostgresValue::Int8(i64::MAX),
                PostgresValue::Text("maximum".to_owned()),
            ],
        ))
        .await?;
    assert_eq!(inserted, 2);
    let staged = transaction
        .query(&query(
            format!("SELECT {payload_column},{key} FROM {table} WHERE {key}=$1"),
            vec![PostgresValue::Int8(i64::MIN)],
        ))
        .await?;
    assert_eq!(
        staged.rows,
        [vec![
            PostgresValue::Text(payload.to_owned()),
            PostgresValue::Int8(i64::MIN)
        ]]
    );
    assert_eq!((staged.columns[0].oid, staged.columns[1].oid), (25, 20));
    transaction.commit().await?;
    let committed = client
        .query(&query(
            format!("SELECT {key},{payload_column} FROM {table} WHERE {key}=$1"),
            vec![PostgresValue::Int8(i64::MAX)],
        ))
        .await?;
    assert_eq!(
        committed.rows,
        [vec![
            PostgresValue::Int8(i64::MAX),
            PostgresValue::Text("maximum".to_owned())
        ]]
    );
    let denied = client
        .query(&query(format!("SELECT {key} FROM {table}"), vec![]))
        .await
        .expect_err("scan rejected");
    assert_eq!(denied.sql_state.as_deref(), Some("0A000"));
    for integer in [i64::MIN, i64::MAX] {
        assert_eq!(
            client
                .execute(&query(
                    format!("DELETE FROM {table} WHERE {key}=$1"),
                    vec![PostgresValue::Int8(integer)]
                ))
                .await?,
            1
        );
    }
    client.close().await?;
    println!("PostgreSQL installed-crate point journey passed");
    Ok(())
}
