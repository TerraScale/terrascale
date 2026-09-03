//! Litestream backup streams (S-06, D-29): one stream child per database
//! file, artifact-readiness, and restore-to-a-new-database including
//! point-in-time restore. Backups are files, never replication, never the
//! hot path (D-29); restore always lands as a new database.
//!
//! Readiness is judged from replica artifacts (the snapshot file inside a
//! generation directory), never from the child process being alive — a
//! stream that spawns happily and ships nothing is not ready.

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// The database file Litestream streams, inside one engine location.
pub fn database_file(location: &Path) -> PathBuf {
    terrabase::engine::database_file(location)
}

/// One attached stream: the child and where its artifacts land.
struct Stream {
    child: Child,
    /// The replica directory generations live under.
    replica_dir: PathBuf,
    /// The `file://` URL `restore` consumes for this database.
    replica_url: String,
}

/// The per-member fleet of Litestream streams, one per hosted database.
pub struct BackupStreams {
    litestream: PathBuf,
    streams: Mutex<BTreeMap<String, Stream>>,
}

impl BackupStreams {
    /// Starts a fleet that shells out to the given Litestream binary.
    pub fn new(litestream: PathBuf) -> Self {
        Self { litestream, streams: Mutex::new(BTreeMap::new()) }
    }

    /// The member's fleet: the packaged Litestream when present, else a
    /// fleet that refuses every operation (backups off, never degraded).
    pub fn open_member() -> Self {
        Self::new(Self::discover().unwrap_or_else(|| PathBuf::new()))
    }

    /// Whether a stream child runs for the database.
    pub fn attached(&self, database: &str) -> bool {
        self.streams.lock().expect("the stream lock is not poisoned").contains_key(database)
    }

    /// Finds Litestream on `PATH`: the pinned 0.5.x the operator packaged.
    /// `None` means backups are simply off (every attach then refuses).
    pub fn discover() -> Option<PathBuf> {
        let path = std::env::var_os("PATH")?;
        std::env::split_paths(&path).map(|dir| dir.join("litestream")).find(|candidate| {
            candidate.is_file()
                && std::fs::metadata(candidate)
                    .and_then(|meta| {
                        use std::os::unix::fs::PermissionsExt;
                        Ok(meta.permissions().mode() & 0o111 != 0)
                    })
                    .unwrap_or(false)
        })
    }

    /// Attaches one database: writes its per-database config (retention per
    /// plan) and starts `litestream replicate` for it. Re-attaching an
    /// already-streamed database refuses — detach first.
    pub fn attach(
        &self,
        database: &str,
        location: &Path,
        replica_root: &Path,
        retention: &str,
    ) -> io::Result<()> {
        let file = database_file(location);
        let replica_dir = replica_root.join(database);
        std::fs::create_dir_all(&replica_dir)?;
        let replica_url = format!("file://{}", replica_dir.display());
        let config = replica_dir.join("litestream.yml");
        std::fs::write(
            &config,
            format!(
                "dbs:\n  - path: {}\n    replicas:\n      - url: {replica_url}\n        retention: {retention}\n",
                file.display()
            ),
        )?;
        let log = std::fs::File::create(replica_dir.join("litestream.log"))?;
        let child = Command::new(&self.litestream)
            .arg("replicate")
            .arg("-config")
            .arg(&config)
            .stdout(Stdio::from(log.try_clone()?))
            .stderr(Stdio::from(log))
            .spawn()?;
        let mut streams = self.streams.lock().expect("the stream lock is not poisoned");
        if streams.contains_key(database) {
            return Err(io::Error::other(format!("a stream for {database} already runs")));
        }
        streams.insert(database.to_owned(), Stream { child, replica_dir, replica_url });
        Ok(())
    }

    /// Readiness from artifacts: some generation directory holds a
    /// non-empty snapshot of the database file. Never asks the child.
    pub fn is_ready(&self, database: &str) -> bool {
        let streams = self.streams.lock().expect("the stream lock is not poisoned");
        streams.get(database).is_some_and(|stream| snapshot_artifact(&stream.replica_dir).is_some())
    }

    /// Polls [`Self::is_ready`] until it holds or the deadline passes.
    pub fn wait_until_ready(&self, database: &str, within: Duration) -> bool {
        let deadline = std::time::Instant::now() + within;
        while !self.is_ready(database) {
            if std::time::Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        true
    }

    /// Restores into a LOCATION directory (S-06: a restore lands as a new
    /// database, never under a live engine): the location's engine markers
    /// (`format`, ownership) are initialized by the engine itself, that
    /// initializer closes, and the restored bytes replace the empty file.
    /// The next engine open serves the restored state.
    pub fn restore_to_location(
        &self,
        database: &str,
        at: Option<u64>,
        location: &Path,
    ) -> Result<(), RestoreError> {
        let file = database_file(location);
        if !file.exists() {
            let init = || {
                let config = terrabase::EngineConfig::durable(location)
                    .map_err(|_| io::Error::other("the location refused to initialize"))?;
                let engine = terrabase::Engine::open(config)
                    .map_err(|_| io::Error::other("the engine refused to initialize"))?;
                drop(engine);
                Ok::<(), io::Error>(())
            };
            init().map_err(RestoreError::Spawn)?;
        }
        let _ = std::fs::remove_file(&file);
        self.restore(database, at, &file)
    }

    /// Restores the database (at a point in time when given) to `out`,
    /// which must not exist. Runs to completion; Litestream's stderr tail
    /// rides the error so failures are diagnosable.
    pub fn restore(&self, database: &str, at: Option<u64>, out: &Path) -> Result<(), RestoreError> {
        let replica_url = {
            let streams = self.streams.lock().expect("the stream lock is not poisoned");
            streams
                .get(database)
                .map(|stream| stream.replica_url.clone())
                .ok_or_else(|| RestoreError::NotStreamed(database.to_owned()))?
        };
        let mut command = Command::new(&self.litestream);
        command.arg("restore").arg("-o").arg(out);
        if let Some(seconds) = at {
            command.arg("-timestamp").arg(rfc3339(seconds));
        }
        command.arg(&replica_url);
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(RestoreError::Spawn)?;
        }
        let output = command.output().map_err(RestoreError::Spawn)?;
        if output.status.success() {
            return Ok(());
        }
        let mut tail = String::from_utf8_lossy(&output.stderr).trim().to_owned();
        if tail.is_empty() {
            tail = String::from_utf8_lossy(&output.stdout).trim().to_owned();
        }
        if tail.len() > 400 {
            tail.truncate(400);
        }
        Err(RestoreError::Litestream { status: output.status, stderr_tail: tail })
    }

    /// Stops one database's stream. The artifacts stay (retention owns
    /// them). Absent databases are a no-op.
    pub fn detach(&self, database: &str) {
        if let Some(mut stream) =
            self.streams.lock().expect("the stream lock is not poisoned").remove(database)
        {
            let _ = stream.child.kill();
            let _ = stream.child.wait();
        }
    }

    /// Stops every stream (member shutdown).
    pub fn detach_all(&self) {
        let streams: Vec<String> =
            self.streams.lock().expect("the stream lock is not poisoned").keys().cloned().collect();
        for database in streams {
            self.detach(&database);
        }
    }
}

/// Why a restore refused.
#[derive(Debug)]
pub enum RestoreError {
    /// No stream runs for that database, so there is no replica to read.
    NotStreamed(String),
    /// The Litestream binary could not run.
    Spawn(io::Error),
    /// Litestream ran and failed; the stderr tail says why.
    Litestream {
        /// The failing exit status.
        status: std::process::ExitStatus,
        /// The last of Litestream's stderr, for diagnosis.
        stderr_tail: String,
    },
}

impl std::fmt::Display for RestoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NotStreamed(database) => {
                write!(f, "no backup stream runs for {database}")
            }
            Self::Spawn(error) => write!(f, "litestream did not run: {error}"),
            Self::Litestream { status, stderr_tail } => {
                write!(f, "litestream restore failed ({status}): {stderr_tail}")
            }
        }
    }
}

/// Finds a non-empty snapshot artifact under one replica directory: the
/// first LTX snapshot (Litestream 0.5's layout is `ltx/{level}/…ltx`,
/// level 1 = snapshot). `None` until the first sync.
fn snapshot_artifact(replica_dir: &Path) -> Option<PathBuf> {
    let snapshots = std::fs::read_dir(replica_dir.join("ltx").join("1")).ok()?;
    snapshots
        .flatten()
        .map(|entry| entry.path())
        .find(|path| std::fs::metadata(path).map(|meta| meta.len() > 0).unwrap_or(false))
}

/// Counts the WAL segments shipped so far (LTX level 0) — tests wait on
/// this to know a write has actually left the building.
fn wal_segment_count(replica_dir: &Path) -> usize {
    std::fs::read_dir(replica_dir.join("ltx").join("0"))
        .map(|segments| segments.flatten().count())
        .unwrap_or(0)
}

/// Renders unix seconds as RFC3339 UTC (Litestream's `-timestamp` format).
/// The workspace keeps unix millis everywhere else; this is the one civil
/// conversion, pinned by tests.
fn rfc3339(unix_seconds: u64) -> String {
    let days = i64::try_from(unix_seconds / 86_400).unwrap_or(i64::MAX);
    let seconds_of_day = unix_seconds % 86_400;
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
        seconds_of_day / 3_600,
        (seconds_of_day % 3_600) / 60,
        seconds_of_day % 60
    )
}

/// Howard Hinnant's `civil_from_days`: days since the unix epoch to
/// (y, m, d) in the proleptic Gregorian calendar.
fn civil_from_days(days: i64) -> (i64, u64, u64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m as u64, d as u64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;
    use std::sync::atomic::{AtomicU64, Ordering};

    use terrabase::engine::{BulkIngestRequest, CommitOutcome, IngestConflictMode};
    use terrabase::types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId,
    };
    use terrabase_tbep::engine_host::EngineHost;

    static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

    struct TestLocation(PathBuf);

    impl TestLocation {
        fn new(label: &str) -> Self {
            let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
            Self(
                std::env::temp_dir()
                    .join(format!("terrabase-s06-{label}-{}-{number}", std::process::id())),
            )
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestLocation {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn host(label: &str) -> (TestLocation, EngineHost) {
        let location = TestLocation::new(label);
        let host = EngineHost::open(location.path(), 1, "tenant-a", "project-a", "database-a")
            .expect("the engine host opens");
        (location, host)
    }

    fn put(host: &EngineHost, id: &str, value: i64) {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).expect("the engine serves a snapshot");
        let operation = OperationId::try_new(&format!("put-{id}-{value}"))
            .expect("the operation identity parses");
        let mut transaction = engine.begin(&scope, snapshot, operation).expect("the write begins");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(id).expect("the document id parses"),
                    CollectionName::try_new("orders").expect("the collection parses"),
                    DocumentValue::Object(BTreeMap::from([(
                        "value".to_owned(),
                        DocumentValue::Integer(value),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .expect("the put stages");
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {}
            outcome => panic!("the write committed: {outcome:?}"),
        }
    }

    fn orders_of(host: &EngineHost) -> Vec<Document> {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).expect("the engine serves a snapshot");
        engine
            .snapshot_documents_in_collection(
                &scope,
                &snapshot,
                &CollectionName::try_new("orders").expect("the collection parses"),
            )
            .expect("the collection scan serves")
    }

    /// The fleet plus its replica root (`attach` derives the per-database
    /// directory under it).
    fn streams(label: &str) -> Option<(TestLocation, BackupStreams, PathBuf)> {
        let litestream = BackupStreams::discover()?;
        let replica_root = TestLocation::new(label);
        let root = replica_root.path().to_path_buf();
        let fleet = BackupStreams::new(litestream);
        Some((replica_root, fleet, root))
    }

    /// The replica directory `attach` creates for the standard database.
    fn replica_of(replica_root: &Path) -> PathBuf {
        replica_root.join("database-a")
    }

    /// Waits until the replica directory holds at least `segments` WAL
    /// files, so a write is provably shipped before the next step.
    /// Waits until the shipped WAL segment count exceeds `baseline` — a
    /// write between reading the baseline and this call provably left the
    /// building (schema-init txns already in the baseline don't count).
    fn wait_for_wal_growth(replica_dir: &Path, baseline: usize) -> bool {
        let deadline = std::time::Instant::now() + Duration::from_secs(30);
        while wal_segment_count(replica_dir) <= baseline {
            if std::time::Instant::now() >= deadline {
                eprintln!("litestream log tail: {}", litestream_log_tail(replica_dir));
                return false;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        true
    }

    /// The last kilobyte of the stream's log, for failed-shipping diagnosis.
    fn litestream_log_tail(replica_dir: &Path) -> String {
        std::fs::read_to_string(replica_dir.join("litestream.log"))
            .map(|log| {
                let start = log.len().saturating_sub(1_000);
                log[start..].to_owned()
            })
            .unwrap_or_else(|_| "<no log>".to_owned())
    }

    /// The done-when journey: write, wait, write, restore to between the
    /// two writes — the restored database serves exactly the first write.
    #[test]
    fn restore_between_two_writes_serves_only_the_first() {
        let Some((replica_location, fleet, replica_root)) = streams("pitr") else {
            eprintln!("skipping: litestream is not on PATH");
            return;
        };
        let replica_dir = replica_of(&replica_root);
        let (location, host) = host("pitr-live");
        fleet
            .attach("database-a", location.path(), &replica_root, "24h")
            .expect("the stream attaches");
        assert!(
            fleet.wait_until_ready("database-a", Duration::from_secs(30)),
            "the snapshot artifact lands"
        );

        // The journey: write, wait, write — both writes ship as WAL
        // segments, and the point in time sits strictly between them.
        // Litestream stamps a WAL segment when the NEXT sync finalizes it,
        // so the restore point waits one sync past the first write's
        // shipping before it is chosen (a write is addressable a sync
        // after it leaves the building).
        let baseline = wal_segment_count(&replica_dir);
        put(&host, "orders/first", 1);
        assert!(wait_for_wal_growth(&replica_dir, baseline), "the first write ships");
        std::thread::sleep(Duration::from_millis(2_500));
        let at = unix_now() + 1;
        while unix_now() <= at {
            std::thread::sleep(Duration::from_millis(100));
        }
        let between = wal_segment_count(&replica_dir);
        put(&host, "orders/second", 2);
        assert!(wait_for_wal_growth(&replica_dir, between), "the second write ships");

        let restored_at = TestLocation::new("pitr-restored");
        fleet
            .restore_to_location("database-a", Some(at), restored_at.path())
            .expect("the point-in-time restore runs");
        let restored = host_of_existing(&restored_at);
        let documents = orders_of(&restored);
        let ids: Vec<&str> = documents.iter().map(|document| document.id().as_str()).collect();
        assert_eq!(ids, vec!["orders/first"], "only the first write is restored");
        drop(restored);

        // Latest restore serves both.
        let latest_at = TestLocation::new("pitr-latest");
        fleet
            .restore_to_location("database-a", None, latest_at.path())
            .expect("the latest restore runs");
        let latest = host_of_existing(&latest_at);
        assert_eq!(orders_of(&latest).len(), 2, "the latest restore serves both writes");
        drop(latest);

        fleet.detach_all();
        drop(host);
        drop(location);
        drop(fleet);
        drop(replica_location);
    }

    /// The done-when timing gate: a 100k-document database branches —
    /// restore to a new location — in under ten seconds locally.
    #[test]
    fn branching_100k_documents_restores_under_ten_seconds() {
        let Some((replica_location, fleet, replica_root)) = streams("branch") else {
            eprintln!("skipping: litestream is not on PATH");
            return;
        };
        let (location, host) = host("branch-live");
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let scope = engine.scope_for(&tenant);
        let request = BulkIngestRequest::try_new(
            IngestConflictMode::CreateOnly,
            LocalDurability::Synced,
            Deadline::after(Duration::from_secs(120)).expect("the deadline is positive"),
        )
        .expect("the ingest request validates");
        let source = (0..100_000).map(|index| {
            Document::new(
                DocumentId::try_new(&format!("orders/seed-{index}"))
                    .expect("the document id parses"),
                CollectionName::try_new("orders").expect("the collection parses"),
                DocumentValue::Object(BTreeMap::from([(
                    "value".to_owned(),
                    DocumentValue::Integer(index),
                )])),
            )
        });
        let receipt = engine
            .ingest_stream(&scope, request)
            .expect("the stream opens")
            .run(source, &Cancellation::new());
        assert_eq!(
            receipt.committed(),
            100_000,
            "every seed document commits (termination: {:?}, refusals: {})",
            receipt.termination(),
            receipt.refused()
        );

        fleet
            .attach("database-a", location.path(), &replica_root, "24h")
            .expect("the stream attaches");
        assert!(
            fleet.wait_until_ready("database-a", Duration::from_secs(60)),
            "the snapshot artifact lands"
        );

        let branch_at = TestLocation::new("branch-restored");
        let started = std::time::Instant::now();
        fleet
            .restore_to_location("database-a", None, branch_at.path())
            .expect("the branch restore runs");
        let elapsed = started.elapsed();
        let branch = host_of_existing(&branch_at);
        assert_eq!(orders_of(&branch).len(), 100_000, "the branch serves every document");
        assert!(elapsed < Duration::from_secs(10), "the branch took {elapsed:?}");
        drop(branch);
        fleet.detach_all();
        drop(host);
        drop(location);
        drop(fleet);
        drop(replica_location);
    }

    /// Opens an engine host over an already-restored location directory
    /// (the file exists; the host verifies it serves).
    fn host_of_existing(location: &TestLocation) -> EngineHost {
        EngineHost::open(location.path(), 1, "tenant-a", "project-a", "database-a")
            .expect("the restored location opens")
    }

    fn unix_now() -> u64 {
        SystemTime::now().duration_since(UNIX_EPOCH).map(|elapsed| elapsed.as_secs()).unwrap_or(0)
    }

    /// The civil conversion is pinned: known instants must render exactly.
    #[test]
    fn rfc3339_pins_known_instants() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        // 2026-09-03T08:00:00Z
        assert_eq!(rfc3339(1_788_422_400), "2026-09-03T08:00:00Z");
        // A leap-year day: 2024-02-29T12:17:41Z
        assert_eq!(rfc3339(1_709_209_061), "2024-02-29T12:17:41Z");
    }
}
