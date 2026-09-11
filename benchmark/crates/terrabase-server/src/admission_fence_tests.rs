//! Real host/listener schedules and persistence fault regressions.

use super::*;
use crate::admission_fence::{FenceOutcome, FenceRecord};
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::sync::{
    Barrier,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

struct Fixture {
    root: PathBuf,
    record: AdmissionRecord,
}

impl Fixture {
    fn new() -> Self {
        let member = MemberUlid::generate();
        Self {
            root: std::env::temp_dir()
                .join(format!("admission-fence-host-{}", DatabaseUlid::generate())),
            record: AdmissionRecord {
                created_at: 1,
                environment: "production".into(),
                tenant_id: TenantUlid::generate().to_string(),
                project_id: ProjectUlid::generate().to_string(),
                database_id: DatabaseUlid::generate().to_string(),
                parent_database_id: None,
                key_version: DatabaseKeyVersion::generate().to_string(),
                name: "orders".into(),
                regions: vec!["GRU".into()],
                residency: "GRU".into(),
                operation_id: "create-1".into(),
                region: "GRU".into(),
                member_id: member.to_string(),
            },
        }
    }

    fn open(&self, callback: StorageAdmission) -> Result<AdmissionHost, String> {
        AdmissionHost::open(
            &self.root,
            "production".into(),
            RegionCode::GRU,
            MemberUlid::from_text(&self.record.member_id).unwrap(),
            callback,
        )
    }

    fn request(&self) -> serde_json::Value {
        serde_json::json!({"record": self.record, "deletion_operation_id": "delete-1", "project_fence_revision": 7})
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

struct Listener {
    socket: PathBuf,
    stop: tokio::sync::oneshot::Sender<()>,
    task: tokio::task::JoinHandle<std::io::Result<()>>,
}

impl Listener {
    fn start(host: Arc<AdmissionHost>, root: &Path) -> Self {
        let socket = root.join("admission.sock");
        let listener = tokio::net::UnixListener::bind(&socket).unwrap();
        fs::set_permissions(&socket, fs::Permissions::from_mode(0o600)).unwrap();
        let (stop, signal) = tokio::sync::oneshot::channel();
        let task =
            tokio::spawn(host.serve_until(listener, rustix::process::geteuid().as_raw(), async {
                let _ = signal.await;
            }));
        Self { socket, stop, task }
    }

    async fn stop(self) {
        self.stop.send(()).unwrap();
        self.task.await.unwrap().unwrap();
        fs::remove_file(self.socket).unwrap();
    }
}

async fn send(socket: &Path, route: &str, body: &serde_json::Value) -> tokio::net::UnixStream {
    let mut stream = tokio::net::UnixStream::connect(socket).await.unwrap();
    let body = serde_json::to_vec(body).unwrap();
    let headers = format!(
        "POST /internal/v1/databases/{route} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        body.len()
    );
    stream.write_all(headers.as_bytes()).await.unwrap();
    stream.write_all(&body).await.unwrap();
    stream
}

async fn response(mut stream: tokio::net::UnixStream) -> (u16, Vec<u8>) {
    let mut bytes = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), stream.read_to_end(&mut bytes))
        .await
        .unwrap()
        .unwrap();
    let split = bytes.windows(4).position(|part| part == b"\r\n\r\n").unwrap();
    let headers = std::str::from_utf8(&bytes[..split]).unwrap();
    let status = headers.split_whitespace().nth(1).unwrap().parse().unwrap();
    (status, bytes[split + 4..].to_vec())
}

async fn request(socket: &Path, route: &str, body: &serde_json::Value) -> (u16, Vec<u8>) {
    response(send(socket, route, body).await).await
}

fn receipt(body: &[u8], record: &AdmissionRecord, outcome: FenceOutcome) -> FenceRecord {
    let receipt: FenceRecord = serde_json::from_slice(body).unwrap();
    assert_eq!(&receipt.record, record);
    assert_eq!(receipt.deletion_operation_id, "delete-1");
    assert_eq!(receipt.project_fence_revision, 7);
    assert_eq!(receipt.outcome, outcome);
    receipt
}

#[tokio::test]
async fn fence_before_admit_returns_exact_immutable_receipt_and_survives_restart() {
    let fixture = Fixture::new();
    let calls = Arc::new(AtomicUsize::new(0));
    let counter = calls.clone();
    let callback: StorageAdmission = Arc::new(move |_| {
        counter.fetch_add(1, Ordering::SeqCst);
        Ok(())
    });
    let host = Arc::new(fixture.open(callback.clone()).unwrap());
    let server = Listener::start(host.clone(), &fixture.root);
    let (status, body) = request(&server.socket, "fence", &fixture.request()).await;
    assert_eq!(status, 200);
    let original = receipt(&body, &fixture.record, FenceOutcome::NoInstall);
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&body).unwrap()["outcome"],
        "no_install"
    );
    assert_eq!(
        request(&server.socket, "admit", &serde_json::to_value(&fixture.record).unwrap()).await.0,
        409
    );
    for field in ["project_fence_revision", "deletion_operation_id"] {
        let mut conflicting = fixture.request();
        conflicting[field] = if field == "project_fence_revision" {
            serde_json::json!(8)
        } else {
            serde_json::json!("delete-2")
        };
        assert_eq!(request(&server.socket, "fence", &conflicting).await.0, 409);
    }
    let mut unknown = fixture.request();
    unknown["extra"] = serde_json::json!(true);
    assert_eq!(request(&server.socket, "fence", &unknown).await.0, 400);
    server.stop().await;
    drop(host);
    let reopened = fixture.open(callback).unwrap();
    assert_eq!(reopened.fence(&fixture.record, "delete-1", 7).unwrap(), original);
    assert!(reopened.admit(&fixture.record).is_err());
    assert_eq!(calls.load(Ordering::SeqCst), 0);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn fence_waits_for_callback_after_admission_client_timeout_and_suppresses_replay() {
    let fixture = Fixture::new();
    let barrier = Arc::new(Barrier::new(2));
    let release = barrier.clone();
    let (entered, signal) = tokio::sync::oneshot::channel();
    let entered = Mutex::new(Some(entered));
    let calls = Arc::new(AtomicUsize::new(0));
    let counter = calls.clone();
    let host = Arc::new(
        fixture
            .open(Arc::new(move |_| {
                counter.fetch_add(1, Ordering::SeqCst);
                entered.lock().unwrap().take().unwrap().send(()).unwrap();
                release.wait();
                Ok(())
            }))
            .unwrap(),
    );
    let server = Listener::start(host.clone(), &fixture.root);
    let admit =
        send(&server.socket, "admit", &serde_json::to_value(&fixture.record).unwrap()).await;
    tokio::time::timeout(Duration::from_secs(5), signal).await.unwrap().unwrap();
    // Cancellation of the client must not cancel the blocking storage callback.
    assert!(tokio::time::timeout(Duration::from_millis(50), response(admit)).await.is_err());
    let mut fence = send(&server.socket, "fence", &fixture.request()).await;
    let mut byte = [0; 1];
    let early = tokio::time::timeout(Duration::from_millis(100), fence.read(&mut byte)).await;
    // Always release the callback before assertions so a regression cannot hang the test runtime.
    tokio::task::spawn_blocking(move || barrier.wait()).await.unwrap();
    assert!(early.is_err(), "fence replied while admission callback still held the records mutex");
    let (status, body) = response(fence).await;
    assert_eq!(status, 200);
    receipt(&body, &fixture.record, FenceOutcome::StopRequired);
    assert_eq!(
        host.records_snapshot().unwrap().get(&fixture.record.database_id),
        Some(&fixture.record)
    );
    server.stop().await;
    drop(host);
    let counter = calls.clone();
    let reopened = fixture
        .open(Arc::new(move |_| {
            counter.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }))
        .unwrap();
    assert_eq!(calls.load(Ordering::SeqCst), 1, "fenced record must not replay storage");
    assert_eq!(
        reopened.records_snapshot().unwrap().get(&fixture.record.database_id),
        Some(&fixture.record)
    );
    assert_eq!(
        reopened.fence(&fixture.record, "delete-1", 7).unwrap().outcome,
        FenceOutcome::StopRequired
    );
}

#[tokio::test]
async fn every_uncertain_fence_write_refuses_admission_and_recovers_or_refuses_restart() {
    // 1: interrupted write; 2: file fsync; 3: pending directory fsync;
    // 4: rename; 5: published directory fsync.
    for stage in 1..=5 {
        let fixture = Fixture::new();
        let calls = Arc::new(AtomicUsize::new(0));
        let counter = calls.clone();
        let callback: StorageAdmission = Arc::new(move |_| {
            counter.fetch_add(1, Ordering::SeqCst);
            Ok(())
        });
        let host = Arc::new(fixture.open(callback.clone()).unwrap());
        host.fence_store.fail_at(stage);
        let server = Listener::start(host.clone(), &fixture.root);
        assert_eq!(
            request(&server.socket, "fence", &fixture.request()).await.0,
            409,
            "stage {stage}"
        );
        assert_eq!(
            request(&server.socket, "admit", &serde_json::to_value(&fixture.record).unwrap())
                .await
                .0,
            409,
            "stage {stage}"
        );
        assert_eq!(request(&server.socket, "fence", &fixture.request()).await.0, 409);
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        server.stop().await;
        drop(host);
        let reopened = fixture.open(callback);
        if stage == 1 {
            assert!(reopened.is_err(), "partial pending frame must never prove absence");
        } else {
            let reopened = reopened.unwrap();
            assert_eq!(
                reopened.fence(&fixture.record, "delete-1", 7).unwrap().outcome,
                FenceOutcome::NoInstall
            );
            assert!(reopened.admit(&fixture.record).is_err());
            assert!(
                !fixture
                    .root
                    .join("admissions/fences")
                    .join(format!(".{}.pending", fixture.record.database_id))
                    .exists()
            );
        }
        assert_eq!(calls.load(Ordering::SeqCst), 0);
    }
}

#[test]
fn fence_rejects_symlink_and_conflicting_admission_intents_without_false_absence() {
    for kind in ["dangling", "directory", "wrong-record", "second-conflict"] {
        let fixture = Fixture::new();
        let host = fixture.open(Arc::new(|_| Ok(()))).unwrap();
        let path = host.root.join(format!("{}.pending", fixture.record.database_id));
        match kind {
            "dangling" => std::os::unix::fs::symlink(host.root.join("missing"), &path).unwrap(),
            "directory" => fs::create_dir(&path).unwrap(),
            _ => {
                let mut wrong = fixture.record.clone();
                wrong.project_id = ProjectUlid::generate().to_string();
                fs::write(&path, serde_json::to_vec(&wrong).unwrap()).unwrap();
                if kind == "second-conflict" {
                    fs::write(
                        host.root.join(format!("{}.json", fixture.record.database_id)),
                        serde_json::to_vec(&fixture.record).unwrap(),
                    )
                    .unwrap();
                }
            }
        }
        assert!(host.fence(&fixture.record, "delete-1", 7).is_err(), "{kind}");
        assert!(
            !host.root.join("fences").join(format!("{}.json", fixture.record.database_id)).exists()
        );
    }
}

#[test]
fn startup_validates_pending_fence_identity_and_keeps_conflicting_evidence() {
    for kind in ["filename", "host", "conflict", "oversized"] {
        let fixture = Fixture::new();
        let host = fixture.open(Arc::new(|_| Ok(()))).unwrap();
        let published = host.fence(&fixture.record, "delete-1", 7).unwrap();
        drop(host);
        let dir = fixture.root.join("admissions/fences");
        let final_path = dir.join(format!("{}.json", fixture.record.database_id));
        let pending = dir.join(format!(
            ".{}.pending",
            if kind == "filename" {
                DatabaseUlid::generate().to_string()
            } else {
                fixture.record.database_id.clone()
            }
        ));
        let mut candidate = published.clone();
        if kind == "host" {
            candidate.record.member_id = MemberUlid::generate().to_string();
        }
        if kind == "conflict" {
            candidate.project_fence_revision += 1;
        }
        let bytes = if kind == "oversized" {
            vec![b' '; 32769]
        } else {
            serde_json::to_vec(&candidate).unwrap()
        };
        fs::write(&pending, bytes).unwrap();
        assert!(
            fixture.open(Arc::new(|_| panic!("invalid startup must not replay"))).is_err(),
            "{kind}"
        );
        assert!(pending.exists(), "conflicting evidence must remain");
        assert_eq!(
            serde_json::from_slice::<FenceRecord>(&fs::read(final_path).unwrap()).unwrap(),
            published
        );
    }
}

#[test]
fn startup_retains_fenced_pending_admission_and_rejects_exact_record_drift() {
    let fixture = Fixture::new();
    let host = fixture.open(Arc::new(|_| panic!("must not admit"))).unwrap();
    let intent = host.root.join(format!("{}.pending", fixture.record.database_id));
    fs::write(&intent, serde_json::to_vec(&fixture.record).unwrap()).unwrap();
    assert_eq!(
        host.fence(&fixture.record, "delete-1", 7).unwrap().outcome,
        FenceOutcome::StopRequired
    );
    drop(host);
    let reopened = fixture.open(Arc::new(|_| panic!("fenced pending must not replay"))).unwrap();
    assert_eq!(
        reopened.records_snapshot().unwrap().get(&fixture.record.database_id),
        Some(&fixture.record)
    );
    drop(reopened);
    let mut drift = fixture.record.clone();
    drift.name = "different".into();
    fs::write(
        fixture.root.join("admissions").join(format!("{}.json", drift.database_id)),
        serde_json::to_vec(&drift).unwrap(),
    )
    .unwrap();
    assert!(fixture.open(Arc::new(|_| panic!("drift must not replay"))).is_err());
}

#[test]
fn relative_native_root_is_normalized_without_following_symlinks_or_traversal() {
    let mut fixture = Fixture::new();
    fixture.root = PathBuf::from(format!(".admission-fence-relative-{}", DatabaseUlid::generate()));
    let host = fixture.open(Arc::new(|_| Ok(()))).unwrap();
    assert!(host.root.is_absolute());
    assert_eq!(
        host.fence(&fixture.record, "delete-1", 7).unwrap().outcome,
        FenceOutcome::NoInstall
    );
    drop(host);
    let reopened = fixture.open(Arc::new(|_| panic!("fenced record cannot replay"))).unwrap();
    assert!(reopened.admit(&fixture.record).is_err());
    drop(reopened);
    let member = MemberUlid::from_text(&fixture.record.member_id).unwrap();
    let traversal = fixture.root.join("..").join(&fixture.root);
    assert!(
        AdmissionHost::open(
            &traversal,
            "production".into(),
            RegionCode::GRU,
            member,
            Arc::new(|_| Ok(()))
        )
        .is_err()
    );
    let link = fixture.root.join("link");
    std::os::unix::fs::symlink(std::env::current_dir().unwrap().join(&fixture.root), &link)
        .unwrap();
    assert!(
        AdmissionHost::open(
            &link,
            "production".into(),
            RegionCode::GRU,
            member,
            Arc::new(|_| Ok(()))
        )
        .is_err()
    );
}

#[test]
fn host_lock_inode_survives_restart_and_unsafe_paths_are_refused() {
    let fixture = Fixture::new();
    let host = fixture.open(Arc::new(|_| Ok(()))).unwrap();
    let lock = fixture.root.join(".native-admission.lock");
    let inode = fs::metadata(&lock).unwrap().ino();
    assert!(fixture.open(Arc::new(|_| Ok(()))).is_err());
    assert_eq!(fs::metadata(fixture.root.join("admissions/fences")).unwrap().mode() & 0o777, 0o700);
    drop(host);
    let reopened = fixture.open(Arc::new(|_| Ok(()))).unwrap();
    assert_eq!(fs::metadata(&lock).unwrap().ino(), inode);
    drop(reopened);
    fs::remove_file(&lock).unwrap();
    std::os::unix::fs::symlink(fixture.root.join("other-lock"), &lock).unwrap();
    assert!(fixture.open(Arc::new(|_| Ok(()))).is_err());
    assert!(!fixture.root.join("other-lock").exists());
}
