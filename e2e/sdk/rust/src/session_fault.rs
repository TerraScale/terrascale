use serde_json::json;
use terrascale::v1::{Client, Error};

fn code(error: Result<(), Error>) -> String {
    match error {
        Ok(()) => "accepted".into(),
        Err(Error::CommitUncertain) => "commit_uncertain".into(),
        Err(error) => error
            .code()
            .map(str::to_owned)
            .unwrap_or_else(|| error.to_string()),
    }
}

pub async fn run() -> Result<(), Box<dyn std::error::Error>> {
    let origin = std::env::var("E2E_SESSION_PROXY")?;
    let table = std::env::var("E2E_SESSION_TABLE")?;
    let mode = std::env::var("E2E_SESSION_FAULT")?;
    let client = Client::for_project_api_key(
        std::env::var("TERRASCALE_PROJECT_ID")?,
        std::env::var("TERRASCALE_API_KEY")?,
    )
    .endpoint(&origin)
    .auth_base_url("https://auth.terrascale.tech")
    .add_root_ca_pem(std::fs::read(std::env::var("E2E_CA_FILE")?)?)
    .build()?;
    let db = client.database(std::env::var("TERRASCALE_DATABASE_NAME")?);
    db.sql()
        .execute(&format!("CREATE TABLE {table} (n INTEGER)"), &[])
        .await?;
    let session = db.connect().await?;
    let result = async {
        if mode != "autocommit" && mode != "batch" {
            session.begin().await?;
            session
                .execute(&format!("INSERT INTO {table} VALUES (1)"), &[])
                .await?;
        }
        if mode == "before" {
            reqwest::Client::new()
                .post(format!("{origin}/cut"))
                .send()
                .await?
                .error_for_status()?;
            let _ = session.query("SELECT 1", &[]).await;
        }
        let first = if mode == "autocommit" {
            code(
                session
                    .execute(&format!("INSERT INTO {table} VALUES (1)"), &[])
                    .await
                    .map(drop),
            )
        } else if mode == "batch" {
            code(
                session
                    .batch(vec![
                        format!("INSERT INTO {table} VALUES (1)").into(),
                        format!("INSERT INTO {table} VALUES (2)").into(),
                    ])
                    .await
                    .map(drop),
            )
        } else if mode == "read" {
            code(session.query("SELECT 1", &[]).await.map(drop))
        } else {
            code(session.commit().await)
        };
        let next = code(session.begin().await);
        let rows = db
            .sql()
            .query(&format!("SELECT count(*) AS n FROM {table}"), &[])
            .await?;
        println!(
            "{}",
            json!({"first": first, "next": next, "rows": rows[0].get_i64("n")?.to_string()})
        );
        Ok::<_, Box<dyn std::error::Error>>(())
    }
    .await;
    session.close().await;
    db.sql()
        .execute(&format!("DROP TABLE {table}"), &[])
        .await?;
    result
}
