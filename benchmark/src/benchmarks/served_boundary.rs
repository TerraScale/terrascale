//! Rust-owned orchestration for the served TerraBase benchmark boundary.
//!
//! The production harness and the Go adapter are separate processes. This
//! module owns their lifecycle and accepts a result only when it is fresh,
//! complete, and describes the requested network mTLS boundary. It never
//! substitutes the in-process engine lane.

use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use super::lane::{LaneConfig, Workload};
use super::lane_run::{self, SideFacts};

const READY_PREFIX: &str = "TBEP_PRODUCTION_LISTENER_READY ";
const MAX_RESULT_BYTES: u64 = 128 * 1024 * 1024;
const MAX_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const DEFAULT_TIMEOUT: Duration = Duration::from_secs(30 * 60);
const DEFAULT_AUTHORITY: &str = "terrabase-bench";
const DEFAULT_SOURCE_IDENTITY: &str = "terrabase-rust-served-boundary-benchmark";
const DEFAULT_TRANSPORT_IDENTITY: &str = "terrabase-go network mTLS over loopback TCP";
static ATTEMPT_COUNTER: AtomicU64 = AtomicU64::new(0);

/// Configuration for one served-boundary workload.
#[derive(Clone, Debug)]
pub(crate) struct ServedBoundaryConfig {
    /// Exact lane counts and classification threshold passed to the adapter.
    pub lane: LaneConfig,
    /// CRUD workload selected for this invocation.
    pub workload: Workload,
    /// Existing production harness executable.
    pub harness_executable: PathBuf,
    /// Go executable used when adapter_executable is absent.
    pub go_executable: PathBuf,
    /// Working directory containing the Go adapter command.
    pub go_workdir: PathBuf,
    /// Optional prebuilt adapter executable, useful for hermetic runners.
    pub adapter_executable: Option<PathBuf>,
    /// Directory where immutable complete or failed attempts are retained.
    pub output_directory: PathBuf,
    /// Total wall-clock deadline for startup, adapter execution, and cleanup.
    pub timeout: Duration,
    /// mTLS server name in the generated certificate and Go authority.
    pub server_identity: String,
    /// Principal bound by each Go client.
    pub principal: String,
    /// Tenant bound by each Go client.
    pub tenant: String,
    /// Source identity carried in the adapter's retained limits.
    pub source_identity: String,
    /// Transport identity carried in the adapter's environment facts.
    pub transport_identity: String,
    /// Executable used to create the temporary certificate hierarchy.
    pub openssl_executable: PathBuf,
}

impl ServedBoundaryConfig {
    /// Validates the configuration without starting an external process.
    pub(crate) fn validate(&self) -> Result<(), String> {
        self.lane.validate().map_err(|error| format!("invalid lane configuration: {error:?}"))?;
        if !matches!(
            self.workload,
            Workload::DocumentRead
                | Workload::DocumentWrite
                | Workload::IndexedQuery
                | Workload::Mixed
        ) {
            return Err(format!(
                "served adapter does not support workload {:?}",
                self.workload.as_str()
            ));
        }
        if !self.harness_executable.is_file() {
            return Err(format!(
                "harness executable is missing: {}",
                self.harness_executable.display()
            ));
        }
        if !self.go_workdir.is_dir() {
            return Err(format!(
                "Go adapter working directory is missing: {}",
                self.go_workdir.display()
            ));
        }
        if let Some(adapter) = &self.adapter_executable
            && !adapter.is_file()
        {
            return Err(format!("adapter executable is missing: {}", adapter.display()));
        }
        if self.timeout.is_zero() || self.timeout > MAX_TIMEOUT {
            return Err(format!(
                "timeout must be greater than zero and no more than {MAX_TIMEOUT:?}"
            ));
        }
        for (name, value) in [
            ("server identity", &self.server_identity),
            ("principal", &self.principal),
            ("tenant", &self.tenant),
            ("source identity", &self.source_identity),
            ("transport identity", &self.transport_identity),
        ] {
            validate_text(name, value)?;
        }
        if self.transport_identity.contains("in-process") {
            return Err("served transport identity cannot contain in-process".to_owned());
        }
        Ok(())
    }

    /// Resolves the served lane from TERRABASE_BENCH_* settings.
    ///
    /// The harness and output directory are required. Other executable paths
    /// have local defaults, while benchmark identities remain explicit in the
    /// resulting typed configuration.
    pub(crate) fn from_env(workload: Workload) -> Result<Self, String> {
        let repository = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let harness_executable = required_env("TERRABASE_BENCH_HARNESS")?;
        let go_workdir =
            env_path("TERRABASE_BENCH_GO_DIR").unwrap_or_else(|| repository.join("terrabase-go"));
        let output_directory = required_env("TERRABASE_BENCH_OUTPUT_DIR")?;
        let lane = LaneConfig {
            seed_documents: env_usize(
                "TERRABASE_BENCH_SEED_DOCUMENTS",
                LaneConfig::RECORD_MINIMUM.seed_documents,
            )?,
            operations: env_usize(
                "TERRABASE_BENCH_OPERATIONS",
                LaneConfig::RECORD_MINIMUM.operations,
            )?,
            samples: env_usize("TERRABASE_BENCH_SAMPLES", LaneConfig::RECORD_MINIMUM.samples)?,
            concurrency: env_usize(
                "TERRABASE_BENCH_CONCURRENCY",
                LaneConfig::RECORD_MINIMUM.concurrency,
            )?,
            warmup_operations: env_usize(
                "TERRABASE_BENCH_WARMUP_OPERATIONS",
                LaneConfig::RECORD_MINIMUM.warmup_operations,
            )?,
            noise_percent: env_f64(
                "TERRABASE_BENCH_NOISE_PERCENT",
                LaneConfig::RECORD_MINIMUM.noise_percent,
            )?,
        };
        let server_identity = optional_env("TERRABASE_BENCH_SERVER_IDENTITY")
            .unwrap_or_else(|| DEFAULT_AUTHORITY.to_owned());
        Ok(Self {
            lane,
            workload,
            harness_executable,
            go_executable: env_path("TERRABASE_BENCH_GO").unwrap_or_else(|| PathBuf::from("go")),
            go_workdir,
            adapter_executable: env_path("TERRABASE_BENCH_ADAPTER"),
            output_directory,
            timeout: env_duration("TERRABASE_BENCH_TIMEOUT", DEFAULT_TIMEOUT)?,
            server_identity,
            principal: required_env("TERRABASE_BENCH_PRINCIPAL")?,
            tenant: required_env("TERRABASE_BENCH_TENANT")?,
            source_identity: optional_env("TERRABASE_BENCH_SOURCE_IDENTITY")
                .unwrap_or_else(|| DEFAULT_SOURCE_IDENTITY.to_owned()),
            transport_identity: optional_env("TERRABASE_BENCH_TRANSPORT_IDENTITY")
                .unwrap_or_else(|| DEFAULT_TRANSPORT_IDENTITY.to_owned()),
            openssl_executable: env_path("TERRABASE_BENCH_OPENSSL")
                .unwrap_or_else(|| PathBuf::from("openssl")),
        })
    }
}

/// A complete served-boundary result and its retained attempt path.
#[derive(Debug)]
pub(crate) struct ServedBoundaryResult {
    /// Facts parsed from the Go adapter's complete result document.
    pub facts: SideFacts,
    /// Immutable JSON path retained for audit and later comparison.
    pub output_path: PathBuf,
    /// Exact endpoint announced by the production harness.
    pub endpoint: String,
}

/// A failed orchestration attempt. The optional path points at a retained
/// failed document with an empty sample list.
#[derive(Debug)]
pub(crate) struct ServedBoundaryError {
    reason: String,
    retained_path: Option<PathBuf>,
}

impl ServedBoundaryError {
    /// Returns the operator-facing failure reason.
    pub(crate) fn reason(&self) -> &str {
        &self.reason
    }

    /// Returns the retained failed-attempt path, when it could be written.
    pub(crate) fn retained_path(&self) -> Option<&Path> {
        self.retained_path.as_deref()
    }
}

impl std::fmt::Display for ServedBoundaryError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.reason)
    }
}

impl std::error::Error for ServedBoundaryError {}

/// Starts one production harness, runs one Go adapter workload, and returns
/// only a validated network-boundary result.
pub(crate) fn run_served_boundary(
    config: &ServedBoundaryConfig,
) -> Result<ServedBoundaryResult, ServedBoundaryError> {
    if let Err(reason) = config.validate() {
        return Err(ServedBoundaryError { reason, retained_path: None });
    }
    let run_directory = match create_run_directory() {
        Ok(path) => path,
        Err(reason) => return failed(config, None, reason),
    };
    let output_path = match create_attempt_path(&config.output_directory, config.workload.as_str())
    {
        Ok(path) => path,
        Err(reason) => return failed(config, Some(&run_directory), reason),
    };
    let invocation_started = SystemTime::now();
    let result = run_inner(config, &run_directory, &output_path, invocation_started);
    let cleanup = fs::remove_dir_all(&run_directory);
    match result {
        Ok(result) => {
            if let Err(error) = cleanup {
                return Err(ServedBoundaryError {
                    reason: format!("served run completed but temporary cleanup failed: {error}"),
                    retained_path: Some(result.output_path),
                });
            }
            Ok(result)
        }
        Err(reason) => {
            let error = failed(config, Some(&run_directory), reason);
            if let Err(cleanup_error) = cleanup
                && let Ok(mut error) = error
            {
                error.reason.push_str(&format!("; temporary cleanup failed: {cleanup_error}"));
                return Err(error);
            }
            error
        }
    }
}

fn run_inner(
    config: &ServedBoundaryConfig,
    run_directory: &Path,
    output_path: &Path,
    invocation_started: SystemTime,
) -> Result<ServedBoundaryResult, String> {
    let deadline = Instant::now()
        .checked_add(config.timeout)
        .ok_or_else(|| "benchmark deadline overflowed".to_owned())?;
    let pki = create_pki(config, run_directory, deadline)?;
    let mut harness = start_harness(config, &pki, run_directory, deadline)?;
    let port = match wait_for_ready(&mut harness, deadline) {
        Ok(port) => port,
        Err(error) => {
            stop_harness(&mut harness, deadline);
            return Err(error);
        }
    };
    let endpoint = format!("127.0.0.1:{port}");
    let adapter_result = run_adapter(config, &pki, output_path, &endpoint, deadline);
    stop_harness(&mut harness, deadline);
    adapter_result?;
    validate_output(output_path, invocation_started, config, &endpoint)
}

struct PkiPaths {
    ca_certificate: PathBuf,
    server_certificate: PathBuf,
    server_key: PathBuf,
    client_certificate: PathBuf,
    client_key: PathBuf,
}

fn create_pki(
    config: &ServedBoundaryConfig,
    directory: &Path,
    deadline: Instant,
) -> Result<PkiPaths, String> {
    let ca_certificate = directory.join("ca.pem");
    let ca_key = directory.join("ca.key");
    let server_certificate = directory.join("server.pem");
    let server_key = directory.join("server.key");
    let server_csr = directory.join("server.csr");
    let server_ext = directory.join("server.ext");
    let client_certificate = directory.join("client.pem");
    let client_key = directory.join("client.key");
    let client_csr = directory.join("client.csr");
    let client_ext = directory.join("client.ext");
    let serial = directory.join("ca.srl");
    fs::write(
        &server_ext,
        "subjectAltName=DNS:terrabase-bench,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n",
    )
    .map_err(|error| format!("write server certificate extensions: {error}"))?;
    fs::write(&client_ext, "extendedKeyUsage=clientAuth\n")
        .map_err(|error| format!("write client certificate extensions: {error}"))?;
    openssl(
        config,
        deadline,
        [
            "req",
            "-x509",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path_arg(&ca_key),
            "-out",
            path_arg(&ca_certificate),
            "-days",
            "2",
            "-subj",
            "/CN=terrabase-bench-ca",
        ],
    )?;
    openssl(
        config,
        deadline,
        [
            "req",
            "-new",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path_arg(&server_key),
            "-out",
            path_arg(&server_csr),
            "-subj",
            "/CN=terrabase-bench",
        ],
    )?;
    openssl(
        config,
        deadline,
        [
            "x509",
            "-req",
            "-in",
            path_arg(&server_csr),
            "-CA",
            path_arg(&ca_certificate),
            "-CAkey",
            path_arg(&ca_key),
            "-CAcreateserial",
            "-CAserial",
            path_arg(&serial),
            "-out",
            path_arg(&server_certificate),
            "-days",
            "2",
            "-extfile",
            path_arg(&server_ext),
        ],
    )?;
    openssl(
        config,
        deadline,
        [
            "req",
            "-new",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path_arg(&client_key),
            "-out",
            path_arg(&client_csr),
            "-subj",
            "/CN=terrabase-bench-client",
        ],
    )?;
    openssl(
        config,
        deadline,
        [
            "x509",
            "-req",
            "-in",
            path_arg(&client_csr),
            "-CA",
            path_arg(&ca_certificate),
            "-CAkey",
            path_arg(&ca_key),
            "-CAcreateserial",
            "-CAserial",
            path_arg(&serial),
            "-out",
            path_arg(&client_certificate),
            "-days",
            "2",
            "-extfile",
            path_arg(&client_ext),
        ],
    )?;
    Ok(PkiPaths { ca_certificate, server_certificate, server_key, client_certificate, client_key })
}

fn path_arg(path: &Path) -> &str {
    path.to_str().expect("temporary paths are valid UTF-8")
}

fn openssl<const N: usize>(
    config: &ServedBoundaryConfig,
    deadline: Instant,
    arguments: [&str; N],
) -> Result<(), String> {
    let mut child = Command::new(&config.openssl_executable)
        .args(arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("start openssl: {error}"))?;
    wait_child(&mut child, deadline, "openssl")
}

struct HarnessProcess {
    child: Child,
    stdin: Option<ChildStdin>,
    events: mpsc::Receiver<ReadEvent>,
}

enum ReadEvent {
    Line(String),
    Eof,
    Error(String),
}

fn start_harness(
    config: &ServedBoundaryConfig,
    pki: &PkiPaths,
    run_directory: &Path,
    deadline: Instant,
) -> Result<HarnessProcess, String> {
    let storage = run_directory.join("storage");
    fs::create_dir(&storage).map_err(|error| format!("create fresh harness storage: {error}"))?;
    if Instant::now() >= deadline {
        return Err("benchmark deadline expired before harness start".to_owned());
    }
    let mut child = Command::new(&config.harness_executable)
        .arg(&pki.server_certificate)
        .arg(&pki.server_key)
        .arg(&pki.ca_certificate)
        .arg(&storage)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("start production harness: {error}"))?;
    let stdout = child.stdout.take().ok_or("harness stdout was not piped")?;
    let (sender, events) = mpsc::channel();
    thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        let mut line = String::new();
        loop {
            line.clear();
            match reader.read_line(&mut line) {
                Ok(0) => {
                    let _ = sender.send(ReadEvent::Eof);
                    break;
                }
                Ok(_) => {
                    let _ = sender
                        .send(ReadEvent::Line(line.trim_end_matches(['\r', '\n']).to_owned()));
                }
                Err(error) => {
                    let _ = sender.send(ReadEvent::Error(error.to_string()));
                    break;
                }
            }
        }
    });
    let stdin = child.stdin.take();
    Ok(HarnessProcess { child, stdin, events })
}

fn wait_for_ready(harness: &mut HarnessProcess, deadline: Instant) -> Result<u16, String> {
    loop {
        let remaining = remaining(deadline)?;
        match harness.events.recv_timeout(remaining) {
            Ok(ReadEvent::Line(line)) => {
                if let Some(port) = parse_ready_line(&line) {
                    return Ok(port);
                }
            }
            Ok(ReadEvent::Eof) => {
                return Err("production harness exited before readiness".to_owned());
            }
            Ok(ReadEvent::Error(error)) => {
                return Err(format!("read harness readiness: {error}"));
            }
            Err(RecvTimeoutError::Timeout) => {
                return Err("production harness readiness timed out".to_owned());
            }
            Err(RecvTimeoutError::Disconnected) => {
                return Err("harness readiness channel closed".to_owned());
            }
        }
    }
}

fn parse_ready_line(line: &str) -> Option<u16> {
    let port = line.strip_prefix(READY_PREFIX)?.parse::<u16>().ok()?;
    (port != 0).then_some(port)
}

fn run_adapter(
    config: &ServedBoundaryConfig,
    pki: &PkiPaths,
    output_path: &Path,
    endpoint: &str,
    deadline: Instant,
) -> Result<(), String> {
    let timeout = duration_argument(remaining(deadline)?);
    let mut command = match &config.adapter_executable {
        Some(adapter) => {
            let mut command = Command::new(adapter);
            command.current_dir(&config.go_workdir);
            command
        }
        None => {
            let mut command = Command::new(&config.go_executable);
            command.current_dir(&config.go_workdir).arg("run").arg("./cmd/terrabase-bench-adapter");
            command
        }
    };
    let arguments = [
        ("--endpoint", endpoint.to_owned()),
        ("--root-cert", path_arg(&pki.ca_certificate).to_owned()),
        ("--client-cert", path_arg(&pki.client_certificate).to_owned()),
        ("--client-key", path_arg(&pki.client_key).to_owned()),
        ("--authority", config.server_identity.clone()),
        ("--principal", config.principal.clone()),
        ("--tenant", config.tenant.clone()),
        ("--server-identity", config.server_identity.clone()),
        ("--source-identity", config.source_identity.clone()),
        ("--transport-identity", config.transport_identity.clone()),
        ("--workload", config.workload.as_str().to_owned()),
        ("--out", output_path.to_string_lossy().into_owned()),
        ("--timeout", timeout),
        ("--seed-documents", config.lane.seed_documents.to_string()),
        ("--operations", config.lane.operations.to_string()),
        ("--samples", config.lane.samples.to_string()),
        ("--concurrency", config.lane.concurrency.to_string()),
        ("--warmup", config.lane.warmup_operations.to_string()),
        ("--noise-percent", config.lane.noise_percent.to_string()),
    ];
    for (name, value) in arguments {
        command.arg(name).arg(value);
    }
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("start Go benchmark adapter: {error}"))?;
    wait_child(&mut child, deadline, "Go benchmark adapter")
}

fn validate_output(
    path: &Path,
    invocation_started: SystemTime,
    config: &ServedBoundaryConfig,
    endpoint: &str,
) -> Result<ServedBoundaryResult, String> {
    let metadata =
        fs::metadata(path).map_err(|error| format!("adapter result is missing: {error}"))?;
    if !metadata.is_file() {
        return Err("adapter result is not a regular file".to_owned());
    }
    if metadata.len() > MAX_RESULT_BYTES {
        return Err(format!("adapter result exceeds {MAX_RESULT_BYTES} bytes"));
    }
    if metadata.modified().map_err(|error| format!("read adapter result timestamp: {error}"))?
        < invocation_started
    {
        return Err("adapter result is stale from before this invocation".to_owned());
    }
    let mut file = File::open(path).map_err(|error| format!("open adapter result: {error}"))?;
    let mut text = String::new();
    file.read_to_string(&mut text).map_err(|error| format!("read adapter result: {error}"))?;
    let value = lane_run::json::parse(&text)
        .map_err(|error| format!("malformed adapter result: {error}"))?;
    if lane_run::json::status(&value).map_err(|error| error.to_string())? != "complete" {
        return Err("adapter result reports a failed or incomplete run".to_owned());
    }
    let facts = lane_run::json::reference_side(&value)
        .map_err(|error| format!("invalid adapter result: {error}"))?;
    if facts.target != "terrabase" || facts.workload != config.workload.as_str() {
        return Err("adapter result target or workload does not match the request".to_owned());
    }
    if facts.config != config.lane {
        return Err(
            "adapter result configuration does not match the requested LaneConfig".to_owned()
        );
    }
    if facts.endpoint != endpoint {
        return Err(format!(
            "adapter result endpoint {:?} does not match {:?}",
            facts.endpoint, endpoint
        ));
    }
    if facts.observed_identity != config.server_identity {
        return Err(
            "adapter result server identity does not match the requested identity".to_owned()
        );
    }
    if facts.client_transport != config.transport_identity
        || facts.client_transport.contains("in-process")
        || !facts.client_transport.contains("mTLS")
    {
        return Err(
            "adapter result does not identify the requested network mTLS boundary".to_owned()
        );
    }
    if !result_contains_source_identity(&value, &config.source_identity) {
        return Err("adapter result does not retain the requested source identity".to_owned());
    }
    Ok(ServedBoundaryResult { facts, output_path: path.to_owned(), endpoint: endpoint.to_owned() })
}

fn result_contains_source_identity(value: &lane_run::json::Value, source_identity: &str) -> bool {
    let lane_run::json::Value::Obj(fields) = value else {
        return false;
    };
    let Some(lane_run::json::Value::Arr(limits)) = fields.get("limits") else {
        return false;
    };
    limits.iter().any(|limit| match limit {
        lane_run::json::Value::Str(value) => {
            value == &format!("source identity: {source_identity}")
        }
        _ => false,
    })
}

fn stop_harness(harness: &mut HarnessProcess, deadline: Instant) {
    if let Some(mut stdin) = harness.stdin.take() {
        let _ = stdin.write_all(b"shutdown\n");
        let _ = stdin.flush();
    }
    if wait_child(&mut harness.child, deadline, "production harness").is_err() {
        let _ = harness.child.kill();
        let _ = harness.child.wait();
    }
}

fn wait_child(child: &mut Child, deadline: Instant, name: &str) -> Result<(), String> {
    loop {
        match child.try_wait().map_err(|error| format!("wait for {name}: {error}"))? {
            Some(status) if status.success() => return Ok(()),
            Some(status) => return Err(format!("{name} exited with status {status}")),
            None => {
                if Instant::now() >= deadline {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(format!("{name} timed out"));
                }
                thread::sleep(Duration::from_millis(10));
            }
        }
    }
}

fn remaining(deadline: Instant) -> Result<Duration, String> {
    deadline
        .checked_duration_since(Instant::now())
        .ok_or_else(|| "benchmark deadline expired".to_owned())
}

fn duration_argument(duration: Duration) -> String {
    if duration.as_secs() > 0 {
        format!("{}s", duration.as_secs())
    } else {
        format!("{}ms", duration.as_millis().max(1))
    }
}

fn create_run_directory() -> Result<PathBuf, String> {
    let base = std::env::temp_dir();
    for _ in 0..32 {
        let id = ATTEMPT_COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| format!("read clock for benchmark directory: {error}"))?
            .as_nanos();
        let path = base.join(format!("terrabase-served-bench-{}-{nanos}-{id}", std::process::id()));
        match fs::create_dir(&path) {
            Ok(()) => return Ok(path),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(format!("create temporary benchmark directory: {error}")),
        }
    }
    Err("could not allocate a unique temporary benchmark directory".to_owned())
}

fn create_attempt_path(directory: &Path, workload: &str) -> Result<PathBuf, String> {
    fs::create_dir_all(directory)
        .map_err(|error| format!("create benchmark output directory: {error}"))?;
    for _ in 0..32 {
        let id = ATTEMPT_COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| format!("read clock for benchmark output: {error}"))?
            .as_nanos();
        let path = directory.join(format!("terrabase-{workload}-{nanos}-{id}.json"));
        if !path.exists() {
            return Ok(path);
        }
    }
    Err("could not allocate a unique benchmark output path".to_owned())
}

fn failed(
    config: &ServedBoundaryConfig,
    run_directory: Option<&Path>,
    reason: String,
) -> Result<ServedBoundaryResult, ServedBoundaryError> {
    let retained_path = create_attempt_path(&config.output_directory, config.workload.as_str())
        .ok()
        .and_then(|path| {
            let document = failure_document(config, &reason, "unbound");
            write_exclusive(&path, document.as_bytes()).ok().map(|()| path)
        });
    let reason = if run_directory.is_some() {
        reason
    } else {
        format!("{reason}; no temporary run directory was created")
    };
    Err(ServedBoundaryError { reason, retained_path })
}

fn failure_document(config: &ServedBoundaryConfig, reason: &str, endpoint: &str) -> String {
    use std::fmt::Write as _;
    let mut text = String::new();
    let _ = writeln!(text, "{{");
    let _ = writeln!(text, "  \"status\": \"failed\",");
    let _ = writeln!(text, "  \"target\": \"terrabase\",");
    let _ = writeln!(text, "  \"workload\": {},", json_string(config.workload.as_str()));
    let _ = writeln!(
        text,
        "  \"config\": {{\"seed_documents\": {}, \"operations\": {}, \"samples\": {}, \"concurrency\": {}, \"warmup_operations\": {}, \"noise_percent\": {}}},",
        config.lane.seed_documents,
        config.lane.operations,
        config.lane.samples,
        config.lane.concurrency,
        config.lane.warmup_operations,
        config.lane.noise_percent
    );
    let _ = writeln!(
        text,
        "  \"environment\": {{\"observed_identity\": {}, \"endpoint\": {}, \"client_transport\": {}}},",
        json_string(&config.server_identity),
        json_string(endpoint),
        json_string(&config.transport_identity)
    );
    let _ = writeln!(text, "  \"error\": {},", json_string(reason));
    let _ = writeln!(text, "  \"samples\": [],");
    let _ = writeln!(
        text,
        "  \"limits\": [{}]",
        json_string(&format!("source identity: {}", config.source_identity))
    );
    let _ = writeln!(text, "}}");
    text
}

fn write_exclusive(path: &Path, contents: &[u8]) -> Result<(), String> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|error| format!("create retained benchmark attempt: {error}"))?;
    file.write_all(contents)
        .map_err(|error| format!("write retained benchmark attempt: {error}"))?;
    file.sync_all().map_err(|error| format!("sync retained benchmark attempt: {error}"))
}

fn validate_text(name: &str, value: &str) -> Result<(), String> {
    if value.trim().is_empty() {
        return Err(format!("{name} is required"));
    }
    if value.len() > 4096 || value.bytes().any(|byte| byte == 0 || byte < 0x20) {
        return Err(format!("{name} contains an invalid control character or is too long"));
    }
    Ok(())
}

fn required_env(name: &str) -> Result<PathBuf, String> {
    std::env::var_os(name)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .ok_or_else(|| format!("{name} is required"))
}

fn optional_env(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

fn env_path(name: &str) -> Option<PathBuf> {
    std::env::var_os(name).filter(|value| !value.is_empty()).map(PathBuf::from)
}

fn env_usize(name: &str, default: usize) -> Result<usize, String> {
    optional_env(name).map_or(Ok(default), |value| {
        value.parse().map_err(|error| format!("{name} is not a valid integer: {error}"))
    })
}

fn env_f64(name: &str, default: f64) -> Result<f64, String> {
    optional_env(name).map_or(Ok(default), |value| {
        value.parse().map_err(|error| format!("{name} is not a valid number: {error}"))
    })
}

fn env_duration(name: &str, default: Duration) -> Result<Duration, String> {
    let Some(value) = optional_env(name) else {
        return Ok(default);
    };
    let (number, multiplier) = if let Some(number) = value.strip_suffix("ms") {
        (number, 1_000_000_u64)
    } else if let Some(number) = value.strip_suffix('s') {
        (number, 1_000_000_000_u64)
    } else {
        return Err(format!("{name} must use a duration suffix (ms or s)"));
    };
    let number: u64 =
        number.parse().map_err(|error| format!("{name} is not a valid duration: {error}"))?;
    let nanos = number.checked_mul(multiplier).ok_or_else(|| format!("{name} is too large"))?;
    Ok(Duration::from_nanos(nanos))
}

fn json_string(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len() + 2);
    escaped.push('"');
    for character in value.chars() {
        match character {
            '"' => escaped.push_str("\\\""),
            '\\' => escaped.push_str("\\\\"),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            '\t' => escaped.push_str("\\t"),
            other => escaped.push(other),
        }
    }
    escaped.push('"');
    escaped
}
