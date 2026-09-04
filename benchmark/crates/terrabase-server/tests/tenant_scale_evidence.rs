//! Hermetic tests for the E-16 evidence validator.
//!
//! These tests deliberately invoke only `tenant-scale.sh validate`; they do
//! not start a member or create databases.  The 100,000-database run belongs
//! to the explicitly opt-in qualification command, never to portable tests.

use std::fs;
use std::path::PathBuf;
use std::process::{Command, Output};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(0);

fn validator() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../scripts/tenant-scale.sh")
}

fn invoke(fixture: &str) -> Output {
    let nonce =
        SystemTime::now().duration_since(UNIX_EPOCH).expect("test clock is after epoch").as_nanos();
    let suffix = NEXT_FIXTURE.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir().join(format!("terrabase-e16-{nonce}-{suffix}.json"));
    fs::write(&path, fixture).expect("fixture writes");
    let output = Command::new("bash")
        .arg(validator())
        .arg("validate")
        .arg(&path)
        .output()
        .expect("validator starts");
    fs::remove_file(path).expect("fixture cleanup");
    output
}

fn valid_evidence() -> String {
    r#"{
      "schema":"terrabase.e16.tenant-scale/v1",
      "qualification":"E-16",
      "status":"passed",
      "mode":"live-member",
      "transport":"member-http",
      "subject":{"database_count":100000,"created_count":100000,"verified_count":100000,"skipped":0,"placeholder_databases":0,"storage":"sqlite","synthetic":false,"member_api":true},
      "envelope":{"cpu_ocpu":2,"memory_gib":12,"cgroup_verified":true},
      "metrics":{"source":"member-prometheus","endpoint":"http://127.0.0.1:9461/internal/metrics","scrapes":2,"required":["terrabase_ready","terrabase_requests_total","terrabase_request_duration_seconds","terrabase_active_members"],"observed_before":{"terrabase_ready":1,"terrabase_requests_total":1,"terrabase_request_duration_seconds":1,"terrabase_active_members":1},"observed":{"terrabase_ready":1,"terrabase_requests_total":2,"terrabase_request_duration_seconds":2,"terrabase_active_members":1},"missing":[]},
      "api_paths":{"health":"/api/v1/health","regions":"/api/v1/regions","create_database":"/api/v1/databases","get_database":"/api/v1/databases/e16-victim","victim_probe":"/api/v1/databases/e16-victim/collections","neighbour_probe":"/api/v1/databases/e16-neighbour/collections"},
      "create":{"requests":100000,"successes":100000,"p99_seconds":4.9,"wall_seconds":300},
      "idle":{"seconds":30,"cpu_seconds":0.2,"cpu_ratio":0.00666667,"approximately_zero":true},
      "victim":{"solo_p99_seconds":0.02,"solo_requests":1000,"under_neighbour_p99_seconds":0.03,"loaded_victim_requests":1000,"ratio":1.5,"adversarial_neighbour":true,"neighbour_requests":10000},
      "run":{"executed":true,"errors":0,"member_pid":"1234"}
    }"#
    .to_owned()
}

#[test]
fn accepts_complete_e16_live_member_evidence() {
    let output = invoke(&valid_evidence());
    assert!(output.status.success(), "validator rejected valid evidence: {:?}", output.stderr);
}

#[test]
fn rejects_missing_prometheus_series() {
    let fixture = valid_evidence().replace(",\"terrabase_active_members\":1", "");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "missing metric was accepted");
    assert!(String::from_utf8_lossy(&output.stderr).contains("terrabase_active_members"));
}

#[test]
fn rejects_missing_before_scrape_and_database_verification() {
    let fixture = valid_evidence()
        .replace("\"verified_count\":100000", "\"verified_count\":99999")
        .replace("\"observed_before\":{\"terrabase_ready\":1,\"terrabase_requests_total\":1,\"terrabase_request_duration_seconds\":1,\"terrabase_active_members\":1}", "\"observed_before\":{\"terrabase_ready\":0,\"terrabase_requests_total\":1,\"terrabase_request_duration_seconds\":1,\"terrabase_active_members\":1}");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "unverified databases or scrape was accepted");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("verified_count") || stderr.contains("observed_before"));
}

#[test]
fn rejects_public_metrics_and_weak_adversarial_sample_count() {
    let fixture = valid_evidence()
        .replace(
            "http://127.0.0.1:9461/internal/metrics",
            "https://metrics.example.com/internal/metrics",
        )
        .replace("\"neighbour_requests\":10000", "\"neighbour_requests\":9999");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "public metrics or weak adversarial run was accepted");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("private member endpoint") || stderr.contains("neighbour_requests"));
}

#[test]
fn rejects_synthetic_or_skipped_work() {
    let fixture = valid_evidence()
        .replace("\"skipped\":0", "\"skipped\":1")
        .replace("\"synthetic\":false", "\"synthetic\":true");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "synthetic/skipped work was accepted");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("skipped") || stderr.contains("synthetic"));
}

#[test]
fn rejects_threshold_failures_and_inconsistent_ratio() {
    let fixture = valid_evidence()
        .replace("\"p99_seconds\":4.9", "\"p99_seconds\":5")
        .replace("\"ratio\":1.5", "\"ratio\":2.1");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "threshold failure was accepted");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("p99_seconds") || stderr.contains("ratio"));
}

#[test]
fn rejects_stub_mode_and_non_member_api_paths() {
    let fixture = valid_evidence()
        .replace("\"mode\":\"live-member\"", "\"mode\":\"stub\"")
        .replace("/api/v1/databases/e16-victim/collections", "/synthetic/victim");
    let output = invoke(&fixture);
    assert!(!output.status.success(), "stub/non-member evidence was accepted");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("live-member") || stderr.contains("victim_probe"));
}
