use terrascale::v1::{new_id, sql_error_codes, Client, SqlStatement};

#[derive(Debug, serde::Deserialize)]
struct Note {
    id: String,
    body: String,
    data: Option<String>,
}

#[tokio::main]
async fn main() -> terrascale::v1::Result<()> {
    let project = std::env::var("TERRASCALE_PROJECT_ID").expect("TERRASCALE_PROJECT_ID");
    let api_key = std::env::var("TERRASCALE_API_KEY").expect("TERRASCALE_API_KEY");
    let database = std::env::var("TERRASCALE_DATABASE_NAME").unwrap_or_else(|_| "main".into());
    let client = Client::for_project_api_key(project, api_key).build()?;
    let handle = client.database(database);
    match handle.create().await {
        Err(error) if error.status() != Some(409) => return Err(error),
        _ => {}
    }
    let db = handle.sql();

    let table = format!("notes_{}", new_id().to_lowercase());
    db.execute(
        &format!("CREATE TABLE {table} (id TEXT PRIMARY KEY, body TEXT NOT NULL, data BLOB)"),
        &[],
    )
    .await?;
    let id = new_id();
    db.execute(
        &format!("INSERT INTO {table} (id, body, data) VALUES (?, ?, ?)"),
        &[id.as_str().into(), "hello".into(), vec![1u8, 2, 3].into()],
    )
    .await?;

    let rows = db
        .query(&format!("SELECT id, body, data FROM {table}"), &[])
        .await?;
    assert_eq!(rows[0].get_str("body")?, "hello");
    assert_eq!(rows[0].get_bytes("data")?, [1, 2, 3]);

    let results = db
        .batch(vec![
            SqlStatement::new(format!("UPDATE {table} SET body = :body WHERE id = :id"))
                .named("body", "edited")
                .named("id", id.as_str()),
            SqlStatement::new(format!("SELECT count(*) AS n FROM {table}")),
        ])
        .await?;
    assert_eq!(results[0].rows_affected, 1);

    let notes: Vec<Note> = db
        .query_as_named(
            &format!("SELECT id, body, data FROM {table} WHERE id = :id"),
            [("id", id.as_str())],
        )
        .await?;
    assert_eq!(notes[0].body, "edited");
    assert_eq!(notes[0].data.as_deref(), Some("AQID"));

    let refused = db
        .execute(
            &format!("INSERT INTO {table} (id, body) VALUES (?, ?)"),
            &[id.as_str().into(), "again".into()],
        )
        .await
        .expect_err("duplicate primary key");
    let sql = refused.sql().expect("typed SQL error");
    println!(
        "refused: status {:?} code {} statement {} sqlstate {:?}",
        refused.status(),
        sql.code,
        sql.statement,
        sql.sqlstate
    );

    db.execute(&format!("DROP TABLE {table}"), &[]).await?;
    println!(
        "ok: {} note(s), first {:?}, constraint code known: {}",
        notes.len(),
        notes[0].id,
        sql.code == sql_error_codes::PRIMARY_KEY_VIOLATION
            || sql.code == sql_error_codes::SQL_ERROR
    );
    Ok(())
}
