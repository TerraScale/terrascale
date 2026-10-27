use serde_json::json;
use terrascale::v1::RestoredDatabase;
const BASE: &str = r#""id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"restored","createdAt":"2026-10-26T00:00:00Z""#;

fn decode(wire: &str) -> serde_json::Result<RestoredDatabase> {
    serde_json::from_str(&format!("{{{BASE},\"placementVersion\":{wire}}}"))
}

#[test]
fn placement_version_preserves_exact_i64_in_both_wire_encodings() {
    for value in [i64::MIN, -1, 0, 1, 9007199254740993, i64::MAX] {
        let integer = value.to_string();
        for wire in [integer.clone(), serde_json::to_string(&integer).unwrap()] {
            let actual: i64 = decode(&wire).unwrap().placement_version;
            assert_eq!(actual, value);
        }
    }
    // The production decimal-string pattern permits signed zero.
    assert_eq!(decode(r#""-0""#).unwrap().placement_version, 0);
}

#[test]
fn malformed_placement_versions_are_never_coerced_or_echoed() {
    for wire in [
        "1.0",
        "1e0",
        "true",
        "false",
        "null",
        "[]",
        "{}",
        "9223372036854775808",
        "-9223372036854775809",
        r#""9223372036854775808""#,
        r#""-9223372036854775809""#,
        r#""01""#,
        r#""-01""#,
        r#""+1""#,
        r#"" 1""#,
        r#""1 ""#,
        r#""1.0""#,
        r#""1e0""#,
        r#""""#,
        r#""-""#,
        r#""synthetic-private""#,
    ] {
        let error = decode(wire).unwrap_err();
        assert!(error
            .to_string()
            .contains("expected exact signed placement version"));
        assert!(!error.to_string().contains("synthetic-private"));
    }
    assert!(serde_json::from_str::<RestoredDatabase>(&format!("{{{BASE}}}")).is_err());
}

#[test]
fn optional_placement_fields_retain_absent_null_and_populated_values() {
    let absent = decode("0").unwrap();
    assert!(absent.branched_from.is_none() && absent.home_region.is_none());
    assert!(absent.replica_regions.is_none() && absent.replicas().is_empty());
    let mut body: serde_json::Value =
        serde_json::from_str(&format!("{{{BASE},\"placementVersion\":\"1\"}}")).unwrap();
    for key in ["branchedFrom", "homeRegion", "replicaRegions"] {
        body[key] = json!(null);
    }
    let explicit: RestoredDatabase = serde_json::from_value(body.clone()).unwrap();
    assert!(explicit.branched_from.is_none() && explicit.home_region.is_none());
    assert!(explicit.replica_regions.is_none());
    body["branchedFrom"] = json!("source");
    body["homeRegion"] = json!("gru");
    body["replicaRegions"] = json!(["gru", "fra"]);
    let populated: RestoredDatabase = serde_json::from_value(body).unwrap();
    assert_eq!(populated.branched_from.as_deref(), Some("source"));
    assert_eq!(populated.home_region.as_deref(), Some("gru"));
    assert_eq!(populated.replicas(), ["gru", "fra"]);
}
