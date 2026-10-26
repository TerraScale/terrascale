#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, ExportSchedule};
fn schedule(n: Value) -> Value {
    json!({"cron":"0 3 * * *","updatedAt":"2026-10-26T12:00:00Z","lastRun":{
        "startedAt":"2026-10-26T03:00:00Z","finishedAt":"2026-10-26T03:00:04Z",
        "status":"succeeded","key":"export.ndjson","documents":n,"bytes":n,"error":null}})
}
#[tokio::test]
async fn schedules_get_put_and_stop_use_camelcase_json() {
    let empty = r#"{"cron":null,"updatedAt":null,"lastRun":null}"#;
    let fixture = Fixture::new(vec![
        reply(200, empty),
        reply(200, &schedule(json!(5)).to_string()),
        reply(200, empty),
        reply(200, empty),
    ])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    assert!(db.get_export_schedule().await.unwrap().cron.is_none());
    assert_eq!(
        db.set_export_schedule(Some("0 3 * * *"))
            .await
            .unwrap()
            .last_run
            .unwrap()
            .documents,
        5
    );
    db.set_export_schedule(None).await.unwrap();
    db.set_export_schedule(Some("")).await.unwrap();
    let seen = fixture.requests.lock().unwrap();
    for r in seen.iter() {
        assert_eq!(
            r.uri,
            format!("/v1/projects/{PROJECT}/databases/orders/export-schedule")
        );
        assert_eq!(r.headers["authorization"], "Bearer fixture-token");
    }
    assert_eq!(seen[0].method, "GET");
    for (i, cron) in [(1, json!("0 3 * * *")), (2, Value::Null), (3, json!(""))] {
        assert_eq!(seen[i].method, "PUT");
        assert_eq!(
            serde_json::from_slice::<Value>(&seen[i].body).unwrap(),
            json!({"cron":cron})
        );
    }
}
#[test]
fn counters_accept_both_wire_encodings_without_precision_loss() {
    for n in [0, 9_007_199_254_740_993, i64::MAX as u64] {
        for value in [json!(n), json!(n.to_string())] {
            assert_eq!(
                serde_json::from_value::<ExportSchedule>(schedule(value))
                    .unwrap()
                    .last_run
                    .unwrap()
                    .bytes,
                n
            );
        }
    }
    for value in [
        json!(-1),
        json!(1.5),
        json!("01"),
        json!("+1"),
        json!("1e2"),
        json!(u64::MAX),
        Value::Null,
    ] {
        assert!(serde_json::from_value::<ExportSchedule>(schedule(value)).is_err());
    }
    assert!(serde_json::from_value::<ExportSchedule>(json!({})).is_err());
}
#[tokio::test]
async fn schedule_errors_preserve_conflict_and_uncertainty_without_write_replay() {
    let fixture = Fixture::new(vec![
        reply(409, r#"{"error":{"code":"backup_destination_required"}}"#),
        reply(500, r#"{"error":{"code":"internal_error"}}"#),
    ])
    .await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .build()
        .unwrap();
    let db = client.database("orders");
    assert_eq!(
        db.set_export_schedule(Some("0 3 * * *"))
            .await
            .unwrap_err()
            .status(),
        Some(409)
    );
    assert!(db
        .set_export_schedule(None)
        .await
        .unwrap_err()
        .is_uncertain());
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
    assert!(client
        .database("../bad")
        .get_export_schedule()
        .await
        .is_err());
}
