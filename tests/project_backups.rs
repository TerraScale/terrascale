#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
const BACKUP: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAX","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-22T12:00:00Z"}"#;
const RESTORED: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAY","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"restored","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FAW","createdAt":"2026-10-22T12:00:00Z","homeRegion":"gru","replicaRegions":["sjc","fra"],"placementVersion":9007199254740993,"replicas":["sjc","fra"]}"#;
const UNASSIGNED: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAY","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"restored","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FAW","createdAt":"2026-10-22T12:00:00Z","homeRegion":null,"replicaRegions":null,"placementVersion":9007199254740993,"replicas":[]}"#;
const DESTINATION: &str = r#"{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","mode":"both","updatedAt":"2026-10-22T12:00:00Z"}"#;
const POLICY: &str =
    r#"{"fullBackupCron":"0 0 * * *","retentionDays":7,"disabled":false,"updatedAt":null}"#;
const WINDOW: &str = r#"{"earliest":"2026-10-22T12:00:00Z","latest":"2026-10-22T12:00:16Z"}"#;
fn value(body: &str) -> Value {
    serde_json::from_str(body).unwrap()
}

#[tokio::test]
async fn backup_contracts_preserve_fields_and_restore_without_a_serving_region() {
    let plan = json!({"window":value(WINDOW),"pointInTime":"2026-10-22T12:00:13Z","snapshotAt":"2026-10-22T12:00:10Z","latestSegmentAt":null,"segmentCount":0,"historyCheck":"metadata"});
    let page = json!({"items":[value(BACKUP)],"next":"opaque/+"});
    let fixture = Fixture::new(vec![
        reply(200, DESTINATION),
        reply(200, DESTINATION),
        reply(204, ""),
        reply(201, BACKUP),
        reply(200, &page.to_string()),
        reply(201, RESTORED),
        reply(200, POLICY),
        reply(200, POLICY),
        reply(200, WINDOW),
        reply(200, &plan.to_string()),
        reply(201, UNASSIGNED),
    ])
    .await;
    let client = fixture.client();
    let destination = json!({"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","accessKeyId":"fixture-key","secretKey":"fixture-secret","mode":"both"});
    assert_eq!(
        client.get_backup_destination::<Value>().await.unwrap(),
        value(DESTINATION)
    );
    assert_eq!(
        client
            .set_backup_destination::<Value>(&destination)
            .await
            .unwrap(),
        value(DESTINATION)
    );
    client.delete_backup_destination().await.unwrap();
    let db = client.database("orders");
    assert_eq!(db.create_backup::<Value>().await.unwrap(), value(BACKUP));
    assert_eq!(
        db.list_backups::<Value>(&[("after", "opaque/+".into())])
            .await
            .unwrap(),
        page
    );
    let restored = db
        .restore_backup("01ARZ3NDEKTSV4RRFFQ69G5FAX", Some("restored"))
        .await
        .unwrap();
    assert_eq!(restored.id, "01ARZ3NDEKTSV4RRFFQ69G5FAY");
    assert_eq!(restored.project_id, PROJECT);
    assert_eq!(restored.name, "restored");
    assert_eq!(
        restored.branched_from.as_deref(),
        Some("01ARZ3NDEKTSV4RRFFQ69G5FAW")
    );
    assert_eq!(restored.created_at, "2026-10-22T12:00:00Z");
    assert_eq!(restored.home_region.as_deref(), Some("gru"));
    assert_eq!(
        restored.replica_regions,
        Some(vec!["sjc".into(), "fra".into()])
    );
    assert_eq!(restored.replicas(), &["sjc", "fra"]);
    assert_eq!(restored.placement_version, 9_007_199_254_740_993);
    assert!(value(RESTORED).get("region").is_none());
    let policy = json!({"fullBackupCron":"0 0 * * *","retentionDays":7,"disabled":false});
    assert_eq!(
        db.get_backup_policy::<Value>().await.unwrap(),
        value(POLICY)
    );
    assert_eq!(
        db.set_backup_policy::<Value>(&policy).await.unwrap(),
        value(POLICY)
    );
    assert_eq!(
        db.get_restore_window::<Value>(&[("destination", "customer".into())])
            .await
            .unwrap(),
        value(WINDOW)
    );
    let preview = json!({"pointInTime":"2026-10-22T12:00:13Z","destination":"customer"});
    assert_eq!(
        db.plan_point_in_time::<Value>(&preview).await.unwrap(),
        plan
    );
    let restore =
        json!({"pointInTime":"2026-10-22T12:00:13Z","name":"restored","destination":"customer"});
    let unassigned = db.restore_point_in_time(&restore).await.unwrap();
    assert_eq!(unassigned.id, restored.id);
    assert_eq!(unassigned.home_region, None);
    assert_eq!(unassigned.replica_regions, None);
    assert!(unassigned.replicas().is_empty());
    assert_eq!(unassigned.placement_version, 9_007_199_254_740_993);
    assert!(value(UNASSIGNED).get("region").is_none());
    let requests = fixture.requests.lock().unwrap();
    for (i, method, suffix) in [
        (0, "GET", "backup-destination"),
        (1, "PUT", "backup-destination"),
        (2, "DELETE", "backup-destination"),
        (3, "POST", "databases/orders/backups"),
        (4, "GET", "databases/orders/backups?after=opaque%2F%2B"),
        (
            5,
            "POST",
            "databases/orders/backups/01ARZ3NDEKTSV4RRFFQ69G5FAX/restore",
        ),
        (6, "GET", "databases/orders/backup-policy"),
        (7, "PUT", "databases/orders/backup-policy"),
        (
            8,
            "GET",
            "databases/orders/point-in-time?destination=customer",
        ),
        (9, "POST", "databases/orders/point-in-time/plan"),
        (10, "POST", "databases/orders/point-in-time/restore"),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(requests[i].uri, format!("/v1/projects/{PROJECT}/{suffix}"));
        assert!(!requests[i].headers.contains_key("idempotency-key"));
    }
    for (i, body) in [
        (1, destination),
        (5, json!({"name":"restored"})),
        (7, policy),
        (9, preview),
        (10, restore),
    ] {
        assert_eq!(
            serde_json::from_slice::<Value>(&requests[i].body).unwrap(),
            body
        );
    }
}

#[tokio::test]
async fn restore_refuses_prefixed_ids_and_preserves_uncertain_acknowledgements() {
    let fixture = Fixture::new(vec![
        reply(
            201,
            &RESTORED.replace("01ARZ3NDEKTSV4RRFFQ69G5FAY", "db_prefixed"),
        ),
        reply(
            503,
            r#"{"error":{"code":"write_outcome_unknown","message":"unknown"}}"#,
        ),
    ])
    .await;
    let db = fixture.client().database("orders");
    assert!(db.restore_backup("backup_prefixed", None).await.is_err());
    assert!(db
        .restore_backup("01ARZ3NDEKTSV4RRFFQ69G5FAX", None)
        .await
        .unwrap_err()
        .is_uncertain());
    let error = db
        .restore_point_in_time(&json!({"pointInTime":"2026-10-22T12:00:13Z"}))
        .await
        .unwrap_err();
    assert!(error.is_uncertain());
    assert_eq!(error.code(), Some("write_outcome_unknown"));
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
}
