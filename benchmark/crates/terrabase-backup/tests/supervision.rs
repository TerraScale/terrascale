//! Litestream supervision integration tests.
//!
//! Every test needs the real `litestream` binary (discovered through
//! `TERRABASE_LITESTREAM_BIN` or `PATH`; on NixOS:
//! `nix shell nixpkgs#litestream -c cargo test`) and the `sqlite3` CLI to
//! drive writes. Tests skip with a message when either is unavailable.

use std::net::SocketAddr;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::AtomicUsize;
use std::sync::atomic::Ordering;
use std::time::Duration;
use std::time::Instant;

use terrabase_backup::LitestreamSupervisor;
use terrabase_backup::ReplicaConfig;
use terrabase_backup::SupervisorConfig;

const DATABASE: &str = "tenant-a.sqlite3";

fn sqlite3_available() -> bool {
    Command::new("sqlite3")
        .arg("--version")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .is_ok_and(|status| status.success())
}

fn temporary_root(label: &str) -> PathBuf {
    static NEXT: AtomicUsize = AtomicUsize::new(0);
    std::env::temp_dir().join(format!(
        "terrabase-backup-{label}-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ))
}

fn run(command: &mut Command) -> String {
    let output = command.output().unwrap_or_else(|error| panic!("{command:?}: {error}"));
    assert!(
        output.status.success(),
        "{command:?} failed: {}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn sqlite3(database: &Path, statements: &str) -> String {
    run(Command::new("sqlite3").arg(database).arg(statements))
}

fn free_loopback_address() -> SocketAddr {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").expect("temporary bind");
    let address = listener.local_addr().expect("temporary address");
    drop(listener);
    address
}

async fn wait_until(limit: Duration, mut condition: impl FnMut() -> bool) -> bool {
    let deadline = Instant::now() + limit;
    loop {
        if condition() {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        tokio::time::sleep(Duration::from_millis(250)).await;
    }
}

fn replica_has_generations(replica: &Path) -> bool {
    let generations = replica.join(DATABASE).join("ltx");
    std::fs::read_dir(&generations)
        .is_ok_and(|levels| {
            levels.filter_map(Result::ok).any(|level| {
                std::fs::read_dir(level.path())
                    .is_ok_and(|entries| entries.filter_map(Result::ok).count() > 0)
            })
        })
}

#[tokio::test]
async fn replicates_writes_and_restores_point_in_time() {
    let Some(binary) = terrabase_backup::litestream_binary() else {
        eprintln!(
            "skipping: no litestream binary (set TERRABASE_LITESTREAM_BIN or add litestream to PATH)"
        );
        return;
    };
    if !sqlite3_available() {
        eprintln!("skipping: sqlite3 CLI not found on PATH");
        return;
    }

    let root = temporary_root("replicate");
    let data = root.join("data");
    let replica = root.join("replica");
    std::fs::create_dir_all(&data).expect("data directory creates");
    let database = data.join(DATABASE);
    sqlite3(
        &database,
        "PRAGMA journal_mode=WAL; CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (42);",
    );

    let mut config = SupervisorConfig::new(data.clone(), ReplicaConfig::File { path: replica.clone() });
    let metrics_address = free_loopback_address();
    config.metrics_addr = Some(metrics_address);
    config.config_path = Some(root.join("litestream.yml"));
    let supervisor = LitestreamSupervisor::start(config).expect("supervisor starts");

    assert!(
        wait_until(Duration::from_secs(30), || replica_has_generations(&replica)).await,
        "replica never received an initial generation"
    );

    sqlite3(&database, "INSERT INTO t VALUES (43); INSERT INTO t VALUES (44);");
    let restored = root.join("restored.sqlite3");
    let url = format!("file://{}", replica.join(DATABASE).display());
    let mut restored_rows = String::new();
    assert!(
        wait_until(Duration::from_secs(30), || {
            let _ = std::fs::remove_file(&restored);
            let restore = Command::new(&binary)
                .args(["restore", "-o"])
                .arg(&restored)
                .arg("-integrity-check")
                .arg("quick")
                .arg(&url)
                .output();
            restore.is_ok_and(|output| {
                if !output.status.success() {
                    return false;
                }
                restored_rows = sqlite3(&restored, "SELECT count(*), max(x) FROM t;");
                restored_rows.trim() == "3|44"
            })
        })
        .await,
        "restored database never contained all written rows (last observed: {restored_rows})"
    );

    assert!(metrics_serves_litestream(metrics_address).await);
    assert!(supervisor.status().running);
    supervisor.shutdown().await.expect("supervisor shuts down");
    assert!(!supervisor.status().running);
    assert!(matches!(
        supervisor.status().last_exit,
        Some(terrabase_backup::ChildExit { code: Some(0), .. })
    ));

    std::fs::remove_dir_all(&root).expect("test root removes");
}

#[tokio::test]
async fn survives_database_deletion_and_crash_restarts_within_grace() {
    if terrabase_backup::litestream_binary().is_none() {
        eprintln!(
            "skipping: no litestream binary (set TERRABASE_LITESTREAM_BIN or add litestream to PATH)"
        );
        return;
    }
    if !sqlite3_available() {
        eprintln!("skipping: sqlite3 CLI not found on PATH");
        return;
    }

    let root = temporary_root("survive");
    let data = root.join("data");
    let replica = root.join("replica");
    let mut config = SupervisorConfig::new(data.clone(), ReplicaConfig::File { path: replica.clone() });
    config.shutdown_grace = Duration::from_secs(5);
    config.config_path = Some(root.join("litestream.yml"));
    let supervisor = LitestreamSupervisor::start(config).expect("supervisor starts");
    assert!(supervisor.status().running);

    // Deleting a watched database stops its replication without stopping
    // the child (watch mode unregisters cleanly).
    let stale_dir = data.join("stale");
    std::fs::create_dir_all(&stale_dir).expect("stale directory creates");
    let stale = stale_dir.join("deleted.sqlite3");
    sqlite3(&stale, "PRAGMA journal_mode=WAL; CREATE TABLE t(x INTEGER);");
    assert!(
        wait_until(Duration::from_secs(30), || {
            replica.join("stale").join("deleted.sqlite3").join("ltx").is_dir()
        })
        .await,
        "nested database never replicated"
    );
    let _ = std::fs::remove_file(&stale);
    let _ = std::fs::remove_file(stale_dir.join("deleted.sqlite3-wal"));
    let _ = std::fs::remove_file(stale_dir.join("deleted.sqlite3-shm"));
    tokio::time::sleep(Duration::from_secs(3)).await;
    assert!(supervisor.status().running, "child must survive db deletion");

    // An externally killed child is restarted by the supervisor.
    #[cfg(unix)]
    {
        let pid = supervisor.status().pid.expect("child pid is published");
        let _ = Command::new("kill").args(["-9", &pid.to_string()]).status();
        assert!(
            wait_until(Duration::from_secs(20), || {
                supervisor.status().restarts >= 1 && supervisor.status().running
            })
            .await,
            "supervisor never restarted the crashed child"
        );
    }

    // Shutdown completes inside the grace window with the child reaped.
    let pid = supervisor.status().pid;
    let started = Instant::now();
    supervisor.shutdown().await.expect("supervisor shuts down");
    let elapsed = started.elapsed();
    assert!(elapsed < Duration::from_secs(10), "shutdown took {elapsed:?}");
    assert!(!supervisor.status().running);
    assert!(matches!(
        supervisor.status().last_exit,
        Some(terrabase_backup::ChildExit { code: Some(0), .. })
    ));
    if let Some(pid) = pid {
        assert!(!Path::new(&format!("/proc/{pid}")).exists(), "child must be reaped");
    }

    std::fs::remove_dir_all(&root).expect("test root removes");
}

async fn metrics_serves_litestream(address: SocketAddr) -> bool {
    use tokio::io::AsyncReadExt as _;
    use tokio::io::AsyncWriteExt as _;

    let Ok(Ok(mut stream)) =
        tokio::time::timeout(Duration::from_secs(5), tokio::net::TcpStream::connect(address)).await
    else {
        return false;
    };
    if stream
        .write_all(b"GET /metrics HTTP/1.0\r\nhost: litestream\r\nconnection: close\r\n\r\n")
        .await
        .is_err()
    {
        return false;
    }
    let mut response = Vec::new();
    let Ok(Ok(_)) = tokio::time::timeout(
        Duration::from_secs(5),
        stream.read_to_end(&mut response),
    )
    .await
    else {
        return false;
    };
    let response = String::from_utf8_lossy(&response);
    response.contains(" 200 OK\r\n") && response.contains("litestream_sync_count")
}
