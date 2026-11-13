use serde_json::json;
use terrascale::v1::{Client, Error};

fn code(result: Result<(), Error>, uncertain: bool) -> String {
    match result {
        Ok(()) => {
            assert!(!uncertain, "operation accepted after uncertainty");
            "accepted".into()
        }
        Err(error) => {
            assert_eq!(error.is_uncertain(), uncertain, "{error:?}");
            match error {
                Error::CommitUncertain => "commit_uncertain".into(),
                Error::Sql {
                    error,
                    status: None,
                } => error.code,
                other => panic!("unexpected public error type: {other:?}"),
            }
        }
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
    let http = reqwest::Client::new();
    db.sql()
        .execute(&format!("CREATE TABLE {table} (n INTEGER)"), &[])
        .await?;
    if mode == "retry" {
        let result = async {
            http.post(format!("{origin}/arm-http"))
                .send()
                .await?
                .error_for_status()?;
            db.get_database_regions().await?;
            let mutation = db
                .sql()
                .execute(&format!("INSERT INTO {table} VALUES (1)"), &[])
                .await;
            http.post(format!("{origin}/disarm-http"))
                .send()
                .await?
                .error_for_status()?;
            let error = mutation.expect_err("mutation was retried");
            assert_eq!(error.code(), Some("commit_uncertain"));
            assert_eq!(error.status(), Some(503));
            assert!(error.is_uncertain());
            assert!(
                matches!(error, Error::Response { mutation: true, .. }),
                "{error:?}"
            );
            println!(
                "{}",
                json!({"read": "accepted", "mutation": "commit_uncertain"})
            );
            Ok::<_, Box<dyn std::error::Error>>(())
        }
        .await;
        db.sql()
            .execute(&format!("DROP TABLE {table}"), &[])
            .await?;
        return result;
    }
    let session = db.connect().await?;
    let result = async {
        let uncertain = !matches!(mode.as_str(), "before" | "read");
        if mode != "autocommit" && mode != "batch" {
            session.begin().await?;
            session
                .execute(&format!("INSERT INTO {table} VALUES (1)"), &[])
                .await?;
        }
        if mode == "before" {
            http.post(format!("{origin}/cut"))
                .send().await?.error_for_status()?;
            let _ = session.query("SELECT 1", &[]).await;
        }
        let first = if mode == "cancel" {
            {
                let commit = session.commit();
                tokio::pin!(commit);
                let committed = http.post(format!("{origin}/committed")).send();
                tokio::pin!(committed);
                tokio::select! {
                    answer = &mut commit => panic!("commit returned before cancellation: {answer:?}"),
                    answer = &mut committed => { answer?.error_for_status()?; }
                }
            }
            code(session.begin().await, true)
        } else if mode == "autocommit" {
            code(
                session.execute(&format!("INSERT INTO {table} VALUES (1)"), &[]).await.map(drop),
                uncertain,
            )
        } else if mode == "batch" {
            code(
                session.batch(vec![
                    format!("INSERT INTO {table} VALUES (1)").into(),
                    format!("INSERT INTO {table} VALUES (2)").into(),
                ]).await.map(drop),
                uncertain,
            )
        } else if mode == "read" {
            code(session.query("SELECT 1", &[]).await.map(drop), false)
        } else {
            code(session.commit().await, uncertain)
        };
        let next = code(session.begin().await, uncertain);
        let second = code(session.execute("SELECT 2", &[]).await.map(drop), uncertain);
        let rows = db
            .sql()
            .query(&format!("SELECT count(*) AS n FROM {table}"), &[])
            .await?;
        println!(
            "{}",
            json!({"first": first, "next": next, "second": second, "rows": rows[0].get_i64("n")?.to_string()})
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
