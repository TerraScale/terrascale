use std::{
    fs,
    path::PathBuf,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "ts761-rust-test-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }

    fn command(&self) -> Command {
        let mut command = Command::new(env!("CARGO_BIN_EXE_bench-client"));
        command
            .args(["run", "--workload"])
            .arg(self.0.join("workload.json"))
            .args(["--region", "gru", "--out"])
            .arg(self.0.join("results.jsonl"));
        command
            .env("TS_BENCH_CLIENT_ID", "secret-client-marker")
            .env("TS_BENCH_CLIENT_SECRET", "secret-value-marker");
        command
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

#[test]
fn blocked_run_preserves_output_and_does_not_disclose_secrets() {
    let fixture = Fixture::new();
    fs::write(
        fixture.0.join("workload.json"),
        include_str!("../bench/gru-preflight-workload.json"),
    )
    .unwrap();
    for existing in [false, true] {
        let out = fixture.0.join("results.jsonl");
        if existing {
            fs::write(&out, "existing result\n").unwrap();
        }
        let result = fixture.command().output().unwrap();
        assert_eq!(result.status.code(), Some(1));
        assert!(result.stdout.is_empty());
        let error = String::from_utf8(result.stderr).unwrap();
        assert!(error.starts_with("SDK_BLOCKED:"));
        assert!(!error.contains("secret-client-marker"));
        assert!(!error.contains("secret-value-marker"));
        if existing {
            assert_eq!(fs::read_to_string(out).unwrap(), "existing result\n");
        } else {
            assert!(!out.exists());
        }
    }
}

#[test]
fn rejects_malformed_and_oversized_workloads_without_echoing_input() {
    let fixture = Fixture::new();
    for input in [b"secret-value-marker".to_vec(), vec![b' '; 1024 * 1024 + 1]] {
        fs::write(fixture.0.join("workload.json"), input).unwrap();
        let result = fixture.command().output().unwrap();
        assert_eq!(result.status.code(), Some(1));
        assert!(result.stdout.is_empty());
        assert!(!String::from_utf8(result.stderr)
            .unwrap()
            .contains("secret-value-marker"));
        assert!(!fixture.0.join("results.jsonl").exists());
    }
}

#[test]
fn help_is_successful_without_credentials() {
    let result = Command::new(env!("CARGO_BIN_EXE_bench-client"))
        .arg("--help")
        .env_remove("TS_BENCH_CLIENT_ID")
        .env_remove("TS_BENCH_CLIENT_SECRET")
        .output()
        .unwrap();
    assert!(result.status.success());
    assert!(result.stderr.is_empty());
    assert!(String::from_utf8(result.stdout)
        .unwrap()
        .contains("SDK revision cannot satisfy TS-761"));
}
