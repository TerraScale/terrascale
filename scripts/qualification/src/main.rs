use serde_json::{json, Value};
use std::{env, process::ExitCode};
use terrascale::{
    CancellationToken, ClientConfig, DatabaseScope, DocumentOperationOptions, Query, Result,
    SubscribeOptions, TerraScaleClient, ViewOperation,
};

struct Config {
    api: String,
    _auth: String,
    project: String,
    database: String,
    key: String,
    run: String,
}

fn required(name: &str) -> std::result::Result<String, String> {
    env::var(name)
        .map_err(|_| format!("missing required environment variable {name}"))
        .and_then(|v| {
            if v.trim().is_empty() {
                Err(format!("{name} is empty"))
            } else {
                Ok(v)
            }
        })
}

fn config() -> std::result::Result<Config, String> {
    Ok(Config {
        api: required("TERRASCALE_API_URL")?,
        _auth: required("TERRASCALE_AUTH_URL")?,
        project: required("TERRASCALE_PROJECT_ID")?,
        database: required("TERRASCALE_DATABASE_ID")?,
        key: required("TERRASCALE_API_KEY")?,
        run: required("TERRASCALE_JOURNEY_RUN_ID")?,
    })
}

fn client(c: &Config) -> Result<TerraScaleClient> {
    let mut builder = ClientConfig::builder()
        .api_base_url(c.api.clone())
        .api_key(c.key.clone())
        .project_id(c.project.clone());
    if let Ok(sync) = std::env::var("TERRASCALE_SYNC_URL") {
        if !sync.trim().is_empty() {
            builder = builder.sync_base_url(sync.trim());
        }
    }
    Ok(TerraScaleClient::new(builder.build()?))
}

fn body(c: &Config, sdk: &str) -> Value {
    json!({"sdk": sdk, "runId": c.run})
}

fn own_id(c: &Config) -> String {
    format!("{}-rust", c.run)
}
fn peer_id(c: &Config, sdk: &str) -> String {
    format!("{}-{sdk}", c.run)
}

fn observation(phase: &str, outcome: &str, detail: &str) {
    let bounded: String = detail.chars().take(240).collect();
    println!(
        "{}",
        json!({"phase": phase, "outcome": outcome, "detail": bounded})
    );
}

fn redacted(detail: String, key: &str) -> String {
    detail
        .replace(key, "[REDACTED]")
        .replace("Bearer ", "Bearer [REDACTED]")
}

fn projected_update(value: &std::collections::BTreeMap<String, Vec<u8>>, run: &str) -> bool {
    let field = |name: &str| {
        value
            .get(name)
            .and_then(|bytes| serde_json::from_slice::<Value>(bytes).ok())
    };
    field("sdk") == Some(json!("rust"))
        && field("runId") == Some(json!(run))
        && field("reactive") == Some(json!(true))
}

async fn seed(c: &Config) -> Result<()> {
    let sdk = client(c)?;
    sdk.documents()
        .put(
            &DatabaseScope::in_project(c.database.clone(), c.project.clone()),
            "sdk_journey",
            &own_id(c),
            body(c, "rust"),
        )
        .await?;
    observation("seed", "passed", "put sdk_journey/<run>-rust");
    Ok(())
}

async fn verify(c: &Config) -> Result<()> {
    let sdk = client(c)?;
    let scope = DatabaseScope::in_project(c.database.clone(), c.project.clone());
    for peer in ["typescript", "go", "rust", "dotnet"] {
        let got = sdk
            .documents()
            .get(&scope, "sdk_journey", &peer_id(c, peer))
            .await?;
        let expected = body(c, peer);
        if got.body != expected {
            return Err(terrascale::Error::invalid_response(format!(
                "sdk_journey/{peer} body mismatch"
            )));
        }
    }
    observation("verify", "passed", "all four SDK bodies matched exactly");
    Ok(())
}

async fn exercise(c: &Config) -> Result<()> {
    let sdk = client(c)?;
    let scope = DatabaseScope::in_project(c.database.clone(), c.project.clone());
    let id = own_id(c);
    let first = sdk
        .documents()
        .put(&scope, "sdk_journey", &id, body(c, "rust"))
        .await?;
    if first.etag.is_none() {
        return Err(terrascale::Error::invalid_response("write omitted ETag"));
    }
    let stale = DocumentOperationOptions::default().with_if_match("stale-etag");
    match sdk
        .documents()
        .put_with_options(
            &scope,
            "sdk_journey",
            &id,
            json!({"sdk":"rust","runId":c.run,"stale":true}),
            stale,
        )
        .await
    {
        Ok(_) => {
            return Err(terrascale::Error::invalid_response(
                "stale ETag was accepted",
            ))
        }
        Err(e) if e.status == Some(412) && e.code == "precondition_failed" => {
            let unchanged = sdk.documents().get(&scope, "sdk_journey", &id).await?;
            if unchanged.body != body(c, "rust") {
                return Err(terrascale::Error::invalid_response(
                    "stale ETag mutated document",
                ));
            }
            observation(
                "exercise",
                "passed",
                "stale ETag returned 412 precondition_failed without mutation",
            );
        }
        Err(e) => {
            return Err(terrascale::Error::invalid_response(format!(
                "unexpected stale ETag result: status={:?} code={}",
                e.status, e.code
            )))
        }
    }
    let idem =
        DocumentOperationOptions::default().with_idempotency_key(format!("{}-rust-idem", c.run));
    let a = sdk
        .documents()
        .put_with_options(&scope, "sdk_journey", &id, body(c, "rust"), idem.clone())
        .await?;
    let b = sdk
        .documents()
        .put_with_options(&scope, "sdk_journey", &id, body(c, "rust"), idem)
        .await?;
    if a.revision != b.revision || a.etag != b.etag {
        return Err(terrascale::Error::invalid_response(
            "idempotency replay receipt changed",
        ));
    }
    let query = Query::raw(
        "SELECT _id, sdk, runId FROM sdk_journey WHERE _id = :id",
        json!({"id": id}),
    );
    let rows = sdk.sql().execute(&scope, &query).await?;
    if !rows.rows.iter().any(|row| {
        row.id == id
            && row.value.get("sdk") == Some(&json!("rust"))
            && row.value.get("runId") == Some(&json!(c.run))
    }) {
        return Err(terrascale::Error::invalid_response(
            "parameterized SQL did not return the owned document",
        ));
    }
    let sub = SubscribeOptions::new().with_subscription(format!("{}-rust", c.run));
    let reactive_query = Query::raw("SELECT * FROM sdk_journey", json!({}));
    let view = sdk.reactive().subscribe(&scope, &reactive_query, sub).await?;
    let update = json!({"sdk":"rust","runId":c.run,"reactive":true});
    sdk.documents()
        .put(&scope, "sdk_journey", &id, update.clone())
        .await?;
    let cancel = CancellationToken::new();
    let mut observed = false;
    for _ in 0..6 {
        let response = sdk
            .reactive()
            .poll_with_cancellation(
                &view,
                view.snapshot.revision.as_str(),
                terrascale::PollOptions::new(1),
                &cancel,
            )
            .await?;
        let deltas = response.deltas;
        observed |= deltas.iter().any(|d| d.to_revision != view.snapshot.revision)
            && deltas.iter().flat_map(|d| d.operations.iter()).any(|op| matches!(op, ViewOperation::Update { identity, value } if identity == &id && projected_update(value, &c.run)));
        if observed {
            break;
        }
    }
    if !observed {
        return Err(terrascale::Error::invalid_response(
            "reactive update was not observed after mutation",
        ));
    }
    cancel.cancel();
    let cancelled = sdk
        .reactive()
        .poll_with_cancellation(
            &view,
            view.snapshot.revision.as_str(),
            terrascale::PollOptions::new(1),
            &cancel,
        )
        .await;
    if !matches!(cancelled, Err(ref e) if e.code == "cancelled") {
        return Err(terrascale::Error::invalid_response(
            "reactive cancellation was not enforced",
        ));
    }
    observation(
        "exercise",
        "passed",
        "conditional write, SQL, idempotency, reactive cancellation",
    );
    Ok(())
}

async fn cleanup(c: &Config) -> Result<()> {
    let sdk = client(c)?;
    let scope = DatabaseScope::in_project(c.database.clone(), c.project.clone());
    sdk.documents()
        .delete(&scope, "sdk_journey", &own_id(c))
        .await?;
    observation("cleanup", "passed", "deleted only sdk_journey/<run>-rust");
    Ok(())
}

#[tokio::main]
async fn main() -> ExitCode {
    let phase = env::args().nth(1).unwrap_or_default();
    let c = match config() {
        Ok(c) => c,
        Err(e) => {
            observation(&phase, "blocked", &e);
            return ExitCode::from(2);
        }
    };
    let result = match phase.as_str() {
        "seed" => seed(&c).await,
        "verify" => verify(&c).await,
        "exercise" => exercise(&c).await,
        "cleanup" => cleanup(&c).await,
        _ => {
            observation(
                &phase,
                "blocked",
                "usage: qualification <seed|verify|exercise|cleanup>",
            );
            return ExitCode::from(2);
        }
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            observation(&phase, "failed", &redacted(format!("{}", e), &c.key));
            ExitCode::from(1)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::projected_update;
    use serde_json::json;
    use std::collections::BTreeMap;

    fn fields(run: &str, reactive: bool) -> BTreeMap<String, Vec<u8>> {
        [
            ("sdk", json!("rust")),
            ("runId", json!(run)),
            ("reactive", json!(reactive)),
        ]
        .into_iter()
        .map(|(k, v)| (k.to_owned(), serde_json::to_vec(&v).unwrap()))
        .collect()
    }

    #[test]
    fn producer_shaped_projection_accepts_exact_fields() {
        assert!(projected_update(&fields("r", true), "r"));
        assert!(!projected_update(&fields("wrong", true), "r"));
        assert!(!projected_update(&fields("r", false), "r"));
    }
}
