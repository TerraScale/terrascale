//! Process orchestration for the served TerraBase benchmark lane.
//!
//! This runner owns the diagnostic qualification harness and Go adapter lifecycle. A
//! result is accepted only when it is fresh, complete, and identifies the
//! requested network mTLS boundary. Admission policy and any claim decision
//! remain in `served_boundary`; this module only runs processes and validates
//! their retained output.

#![allow(dead_code, reason = "the served benchmark runner is a test-only qualification harness")]
#![allow(
    clippy::duration_suboptimal_units,
    clippy::format_push_string,
    clippy::too_many_lines,
    reason = "process setup and certificate arguments mirror the external qualification contract"
)]

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
pub(super) const QUALIFICATION_SERVER_IDENTITY: &str = "tbep-qualification-harness/network-mtls/v1";
pub(super) const QUALIFICATION_SOURCE_IDENTITY: &str = "terrabase-rust-diagnostic-qualification/v1";
pub(super) const ADAPTER_CONTRACT_IDENTITY: &str =
    "terrabase-go/network-mtls/per-slot-connection/v2";
pub(super) const QUALIFICATION_TRANSPORT_IDENTITY: &str =
    "terrabase-go/network-mtls/per-slot-connection/v2 (diagnostic loopback mTLS)";
static COUNTER: AtomicU64 = AtomicU64::new(0);

/// Configuration for one served-boundary workload.
#[derive(Clone, Debug)]
pub(super) struct ServedRunnerConfig {
    /// Exact lane configuration passed to the adapter.
    pub lane: LaneConfig,
    /// Workload selected for this invocation.
    pub workload: Workload,
    /// Existing test-only qualification harness executable.
    pub qualification_harness_executable: PathBuf,
    /// Legacy Go executable setting, retained for configuration compatibility.
    /// Served runs never invoke it; they require `adapter_executable`.
    pub go_executable: PathBuf,
    /// Working directory containing the Go adapter command.
    pub go_workdir: PathBuf,
    /// Optional prebuilt adapter executable.
    pub adapter_executable: Option<PathBuf>,
    /// Directory where immutable attempts are retained.
    pub output_directory: PathBuf,
    /// Total deadline for setup, execution, and cleanup.
    pub timeout: Duration,
    /// Exact server identity/version marker retained in result facts.
    pub server_identity: String,
    /// Bound principal.
    pub principal: String,
    /// Bound tenant.
    pub tenant: String,
    /// Bound project scope.
    pub project: String,
    /// Bound database scope.
    pub database: String,
    /// Source identity retained in result limits.
    pub source_identity: String,
    /// Network mTLS transport identity retained in result facts.
    pub transport_identity: String,
    /// Executable used to create temporary mTLS material.
    pub openssl_executable: PathBuf,
    /// Explicit topology family. Single-node and RF=2 evidence never share
    /// a comparison group.
    pub topology: String,
    /// Immutable digest of the served runtime artifact.
    pub runtime_digest: String,
    /// Immutable digest of the comparator/client artifact.
    pub comparator_digest: String,
}

impl ServedRunnerConfig {
    /// Validates configuration without starting a process.
    pub(super) fn validate(&self) -> Result<(), String> {
        self.lane.validate().map_err(|e| format!("invalid LaneConfig: {e:?}"))?;
        if !matches!(
            self.workload,
            Workload::DocumentRead
                | Workload::DocumentWrite
                | Workload::IndexedQuery
                | Workload::Mixed
        ) {
            return Err(format!("Go adapter does not support {}", self.workload.as_str()));
        }
        if !self.qualification_harness_executable.is_file() {
            return Err(format!(
                "qualification harness executable is missing: {}",
                self.qualification_harness_executable.display()
            ));
        }
        if !self.go_workdir.is_dir() {
            return Err(format!("Go adapter directory is missing: {}", self.go_workdir.display()));
        }
        let Some(path) = &self.adapter_executable else {
            return Err(
                "a prebuilt Go adapter executable is required; `go run` is forbidden because \
                 timeout cleanup cannot reliably reap its child process"
                    .to_owned(),
            );
        };
        if !path.is_file() {
            return Err(format!("adapter executable is missing: {}", path.display()));
        }
        if self.timeout.is_zero() || self.timeout > MAX_TIMEOUT {
            return Err(format!("timeout must be in (0, {MAX_TIMEOUT:?}]"));
        }
        for (name, value) in [
            ("server identity", &self.server_identity),
            ("principal", &self.principal),
            ("tenant", &self.tenant),
            ("project", &self.project),
            ("database", &self.database),
            ("source identity", &self.source_identity),
            ("transport identity", &self.transport_identity),
            ("topology", &self.topology),
            ("runtime digest", &self.runtime_digest),
            ("comparator digest", &self.comparator_digest),
        ] {
            validate_text(name, value)?;
        }
        if !matches!(self.topology.as_str(), "single-node" | "rf2") {
            return Err("topology must be exactly single-node or rf2".to_owned());
        }
        for (name, digest) in [
            ("runtime digest", &self.runtime_digest),
            ("comparator digest", &self.comparator_digest),
        ] {
            if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                return Err(format!("{name} must be an immutable 64-hex digest"));
            }
            if digest.eq_ignore_ascii_case("unassigned") {
                return Err(format!("{name} cannot be unassigned"));
            }
        }
        Ok(())
    }

    /// Resolves the runner from TERRABASE_BENCH_* settings.
    pub(super) fn from_env(workload: Workload) -> Result<Self, String> {
        let repository = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let lane = LaneConfig {
            seed_documents: env_usize("TERRABASE_BENCH_SEED_DOCUMENTS", 1_000)?,
            operations: env_usize("TERRABASE_BENCH_OPERATIONS", 10_000)?,
            samples: env_usize("TERRABASE_BENCH_SAMPLES", 5)?,
            concurrency: env_usize("TERRABASE_BENCH_CONCURRENCY", 16)?,
            warmup_operations: env_usize("TERRABASE_BENCH_WARMUP_OPERATIONS", 1_000)?,
            noise_percent: env_f64("TERRABASE_BENCH_NOISE_PERCENT", 5.0)?,
        };
        Ok(Self {
            lane,
            workload,
            qualification_harness_executable: required_path("TERRABASE_BENCH_HARNESS")?,
            go_executable: env_path("TERRABASE_BENCH_GO").unwrap_or_else(|| PathBuf::from("go")),
            go_workdir: env_path("TERRABASE_BENCH_GO_DIR")
                .unwrap_or_else(|| repository.join("terrabase-go")),
            adapter_executable: env_path("TERRABASE_BENCH_ADAPTER"),
            output_directory: required_path("TERRABASE_BENCH_OUTPUT_DIR")?,
            timeout: env_duration("TERRABASE_BENCH_TIMEOUT", DEFAULT_TIMEOUT)?,
            server_identity: optional_env("TERRABASE_BENCH_SERVER_IDENTITY")
                .unwrap_or_else(|| QUALIFICATION_SERVER_IDENTITY.to_owned()),
            principal: required_string("TERRABASE_BENCH_PRINCIPAL")?,
            tenant: required_string("TERRABASE_BENCH_TENANT")?,
            project: required_string("TERRABASE_BENCH_PROJECT")?,
            database: required_string("TERRABASE_BENCH_DATABASE")?,
            source_identity: optional_env("TERRABASE_BENCH_SOURCE_IDENTITY")
                .unwrap_or_else(|| QUALIFICATION_SOURCE_IDENTITY.to_owned()),
            transport_identity: optional_env("TERRABASE_BENCH_TRANSPORT_IDENTITY")
                .unwrap_or_else(|| QUALIFICATION_TRANSPORT_IDENTITY.to_owned()),
            openssl_executable: env_path("TERRABASE_BENCH_OPENSSL")
                .unwrap_or_else(|| PathBuf::from("openssl")),
            topology: required_string("TERRABASE_BENCH_TOPOLOGY")?,
            runtime_digest: required_digest("TERRABASE_BENCH_RUNTIME_DIGEST")?,
            comparator_digest: required_digest("TERRABASE_BENCH_COMPARATOR_DIGEST")?,
        })
    }
}

/// A validated complete result and the retained immutable path.
#[derive(Debug)]
pub(super) struct ServedRunnerResult {
    /// Facts parsed through the existing Rust result reader.
    pub facts: SideFacts,
    /// Retained complete result path.
    pub output_path: PathBuf,
    /// Endpoint announced by the harness.
    pub endpoint: String,
}

/// A failed attempt. Failure documents always have an empty sample array.
#[derive(Debug)]
pub(super) struct ServedRunnerError {
    reason: String,
    retained_path: Option<PathBuf>,
}

impl ServedRunnerError {
    /// Returns the failure reason.
    pub(super) fn reason(&self) -> &str {
        &self.reason
    }

    /// Returns the retained failed-attempt path, if writing it succeeded.
    pub(super) fn retained_path(&self) -> Option<&Path> {
        self.retained_path.as_deref()
    }
}

impl std::fmt::Display for ServedRunnerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.reason)
    }
}

impl std::error::Error for ServedRunnerError {}

/// Runs one complete served-boundary workload.
pub(super) fn run_served_runner(
    config: &ServedRunnerConfig,
) -> Result<ServedRunnerResult, ServedRunnerError> {
    if let Err(reason) = config.validate() {
        return Err(ServedRunnerError { reason, retained_path: None });
    }
    let run_directory = match unique_directory() {
        Ok(path) => path,
        Err(reason) => return retain_failure(config, reason),
    };
    let output_path = match unique_output(&config.output_directory, config.workload.as_str()) {
        Ok(path) => path,
        Err(reason) => {
            let _ = fs::remove_dir_all(&run_directory);
            return retain_failure(config, reason);
        }
    };
    let started = SystemTime::now();
    let result = run_inner(config, &run_directory, &output_path, started);
    let cleanup = fs::remove_dir_all(&run_directory);
    match result {
        Ok(result) if cleanup.is_ok() => Ok(result),
        Ok(result) => Err(ServedRunnerError {
            reason: format!(
                "served run completed but temporary cleanup failed: {}",
                cleanup.unwrap_err()
            ),
            retained_path: Some(result.output_path),
        }),
        Err(reason) => {
            // The adapter writes into this unique path, but it is not
            // publishable until every admission check succeeds. Never leave
            // a complete-looking loopback/partial document for the history
            // renderer to discover.
            let _ = fs::remove_file(&output_path);
            let mut error = retain_failure(config, reason).unwrap_err();
            if let Err(cleanup_error) = cleanup {
                error.reason.push_str(&format!("; temporary cleanup failed: {cleanup_error}"));
            }
            Err(error)
        }
    }
}

fn run_inner(
    config: &ServedRunnerConfig,
    run_directory: &Path,
    output_path: &Path,
    started: SystemTime,
) -> Result<ServedRunnerResult, String> {
    let deadline =
        Instant::now().checked_add(config.timeout).ok_or("benchmark deadline overflowed")?;
    let pki = make_pki(config, run_directory, deadline)?;
    let mut harness = start_harness(config, &pki, run_directory)?;
    let port = match wait_ready(&mut harness, deadline) {
        Ok(port) => port,
        Err(reason) => {
            stop_harness(&mut harness, deadline);
            return Err(reason);
        }
    };
    let endpoint = format!("127.0.0.1:{port}");
    let adapter = run_adapter(config, &pki, output_path, &endpoint, deadline);
    let evidence = capture_resource_evidence(harness.pid);
    stop_harness(&mut harness, deadline);
    adapter?;
    let evidence = evidence?;
    augment_output(output_path, &evidence, config)?;
    validate_output(output_path, started, config, &endpoint)
}

struct Pki {
    ca: PathBuf,
    server: PathBuf,
    server_key: PathBuf,
    client: PathBuf,
    client_key: PathBuf,
}

fn make_pki(config: &ServedRunnerConfig, dir: &Path, deadline: Instant) -> Result<Pki, String> {
    let ca = dir.join("ca.pem");
    let ca_key = dir.join("ca.key");
    let server = dir.join("server.pem");
    let server_key = dir.join("server.key");
    let server_csr = dir.join("server.csr");
    let client = dir.join("client.pem");
    let client_key = dir.join("client.key");
    let client_csr = dir.join("client.csr");
    let server_ext = dir.join("server.ext");
    let client_ext = dir.join("client.ext");
    let serial = dir.join("ca.srl");
    fs::write(
        &server_ext,
        "subjectAltName=DNS:terrabase-bench,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n",
    )
    .map_err(|e| format!("write server certificate extensions: {e}"))?;
    let client_extensions =
        format!("subjectAltName=URI:{}\nextendedKeyUsage=clientAuth\n", config.principal);
    fs::write(&client_ext, client_extensions)
        .map_err(|e| format!("write client certificate extensions: {e}"))?;
    openssl(
        config,
        deadline,
        &[
            "req",
            "-x509",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path(&ca_key),
            "-out",
            path(&ca),
            "-days",
            "2",
            "-subj",
            "/CN=terrabase-bench-ca",
        ],
    )?;
    openssl(
        config,
        deadline,
        &[
            "req",
            "-new",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path(&server_key),
            "-out",
            path(&server_csr),
            "-subj",
            "/CN=terrabase-bench",
        ],
    )?;
    openssl(
        config,
        deadline,
        &[
            "x509",
            "-req",
            "-in",
            path(&server_csr),
            "-CA",
            path(&ca),
            "-CAkey",
            path(&ca_key),
            "-CAcreateserial",
            "-CAserial",
            path(&serial),
            "-out",
            path(&server),
            "-days",
            "2",
            "-extfile",
            path(&server_ext),
        ],
    )?;
    openssl(
        config,
        deadline,
        &[
            "req",
            "-new",
            "-newkey",
            "ec",
            "-pkeyopt",
            "ec_paramgen_curve:prime256v1",
            "-nodes",
            "-keyout",
            path(&client_key),
            "-out",
            path(&client_csr),
            "-subj",
            "/CN=terrabase-bench-client",
        ],
    )?;
    openssl(
        config,
        deadline,
        &[
            "x509",
            "-req",
            "-in",
            path(&client_csr),
            "-CA",
            path(&ca),
            "-CAkey",
            path(&ca_key),
            "-CAserial",
            path(&serial),
            "-out",
            path(&client),
            "-days",
            "2",
            "-extfile",
            path(&client_ext),
        ],
    )?;
    Ok(Pki { ca, server, server_key, client, client_key })
}

fn path(path: &Path) -> &str {
    path.to_str().expect("temporary path is UTF-8")
}

fn openssl(config: &ServedRunnerConfig, deadline: Instant, args: &[&str]) -> Result<(), String> {
    let mut child = Command::new(&config.openssl_executable)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("start openssl: {e}"))?;
    wait_child(&mut child, deadline, "openssl")
}

struct Harness {
    child: Child,
    pid: u32,
    stdin: Option<ChildStdin>,
    events: mpsc::Receiver<ReadEvent>,
}

enum ReadEvent {
    Line(String),
    Eof,
    Error(String),
}

#[derive(Clone, Debug)]
struct ResourceEvidence {
    cgroup: String,
    cpu_max: String,
    cpu_stat: String,
    cpuset: String,
    memory_max: String,
    memory_peak: String,
    rss_kib: String,
    io_stat: String,
}

impl ResourceEvidence {
    fn json(&self, config: &ServedRunnerConfig) -> String {
        format!(
            "{{\"envelope_spec\":\"2ocpu/12GiB\",\"topology\":{},\"cgroup\":{},\"cpu\":{{\"max\":{},\"stat\":{},\"cpuset\":{}}},\"rss\":{{\"kib\":{},\"memory_max\":{},\"memory_peak\":{}}},\"io\":{},\"runtime_digest\":{},\"comparator_digest\":{}}}",
            json_string(&config.topology),
            json_string(&self.cgroup),
            json_string(&self.cpu_max),
            json_string(&self.cpu_stat),
            json_string(&self.cpuset),
            json_string(&self.rss_kib),
            json_string(&self.memory_max),
            json_string(&self.memory_peak),
            json_string(&self.io_stat),
            json_string(&config.runtime_digest),
            json_string(&config.comparator_digest),
        )
    }
}

/// The fixed benchmark resource envelope (GOAL.md D-65): one launch member,
/// exactly 2 OCPU / 12 GiB. A gating run cannot opt out or run unconstrained.
fn envelope_command(harness: &Path) -> Result<Command, String> {
    let mut command = Command::new(harness);
    if std::env::var_os("TERRABASE_BENCH_ENVELOPE")
        .is_some_and(|value| value == std::ffi::OsStr::new("0"))
    {
        return Err("TERRABASE_BENCH_ENVELOPE=0 is forbidden for gating runs".to_owned());
    }
    // The scope holds the 12-GiB memory ceiling; taskset inside it
    // pins exactly two CPUs, which children inherit and
    // available_parallelism follows, so the harness runtime sees
    // exactly the envelope. Host-outage fallback (2026-08-31): the user
    // manager can be down machine-wide (dev-vm-storage refused after a
    // config switch and user@1000 Requires it), which kills every --user
    // scope; a system scope under `sudo -nE` enforces the identical
    // cgroup limits without the user bus, and -E preserves the
    // subject's environment through sudo.
    let Ok(systemd_run) = which_host_bin("systemd-run") else {
        return Err("systemd-run is required to enforce the benchmark cgroup".to_owned());
    };
    let mut wrapped = if user_manager_reachable() {
        let mut scope = Command::new(systemd_run);
        scope.arg("--user").arg("--scope").arg("--quiet");
        scope
    } else if let Some(sudo) = passwordless_sudo()
        && let Some((uid, gid)) = current_uid_gid()
    {
        let mut scope = Command::new(sudo);
        scope
            .arg("-nE")
            .arg(systemd_run)
            .arg("--scope")
            .arg("--quiet")
            .arg("--uid")
            .arg(uid.to_string())
            .arg("--gid")
            .arg(gid.to_string());
        scope
    } else {
        return Err(
            "user systemd manager unavailable and sudo -n cannot create a system scope".to_owned()
        );
    };
    let cpus = current_two_cpu_set()?;
    wrapped
        .arg("-p")
        .arg("MemoryMax=12G")
        .arg("-p")
        .arg("CPUQuota=200%")
        .arg("-p")
        .arg(format!("AllowedCPUs={cpus}"))
        .arg("-p")
        .arg("TasksMax=4096")
        .arg("--")
        .arg("taskset")
        .arg("-c")
        .arg(&cpus)
        .arg(harness);
    command = wrapped;
    command.env("TERRABASE_BENCH_ENVELOPE_SPEC", "2ocpu/12GiB");
    Ok(command)
}

fn current_two_cpu_set() -> Result<String, String> {
    let taskset = which_host_bin("taskset")?;
    let output = Command::new(taskset)
        .arg("-pc")
        .arg(std::process::id().to_string())
        .output()
        .map_err(|error| format!("inspect current CPU affinity: {error}"))?;
    if !output.status.success() {
        return Err("taskset could not inspect current CPU affinity".to_owned());
    }
    let text = String::from_utf8_lossy(&output.stdout);
    let list = text.rsplit_once(':').map(|(_, value)| value.trim()).unwrap_or_default();
    let mut cpus = Vec::new();
    for item in list.split(',') {
        if let Some((first, last)) = item.split_once('-') {
            let first: u32 = first.trim().parse().map_err(|_| "invalid CPU affinity")?;
            let last: u32 = last.trim().parse().map_err(|_| "invalid CPU affinity")?;
            cpus.extend(first..=last);
        } else if !item.trim().is_empty() {
            cpus.push(item.trim().parse().map_err(|_| "invalid CPU affinity")?);
        }
    }
    if cpus.len() < 2 {
        return Err("fewer than two CPUs are available for the launch envelope".to_owned());
    }
    Ok(format!("{}-{}", cpus[0], cpus[1]))
}

/// Whether `systemctl --user` answers — false when the user manager is
/// down, so no --user scope can be created anywhere on the host.
fn user_manager_reachable() -> bool {
    static REACHABLE: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *REACHABLE.get_or_init(|| {
        which_host_bin("systemctl").is_ok_and(|systemctl| {
            Command::new(systemctl)
                .arg("--user")
                .arg("show")
                .arg("-p")
                .arg("ActiveState")
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .is_ok_and(|status| status.success())
        })
    })
}

/// The `sudo` binary, if `sudo -n` works — the system-scope fallback
/// needs both.
fn passwordless_sudo() -> Option<PathBuf> {
    static SUDO: std::sync::OnceLock<Option<PathBuf>> = std::sync::OnceLock::new();
    SUDO.get_or_init(|| {
        let sudo = which_host_bin("sudo").ok()?;
        let works = Command::new(&sudo)
            .arg("-n")
            .arg("true")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_ok_and(|status| status.success());
        works.then_some(sudo)
    })
    .clone()
}

/// The invoking user's real uid/gid, for dropping back to it inside a
/// sudo-started system scope.
fn current_uid_gid() -> Option<(u32, u32)> {
    let status = fs::read_to_string("/proc/self/status").ok()?;
    let mut uid = None;
    let mut gid = None;
    for line in status.lines() {
        if let Some(rest) = line.strip_prefix("Uid:")
            && let Some(first) = rest.split_whitespace().next()
        {
            uid = first.parse().ok();
        }
        if let Some(rest) = line.strip_prefix("Gid:")
            && let Some(first) = rest.split_whitespace().next()
        {
            gid = first.parse().ok();
        }
    }
    Some((uid?, gid?))
}

fn which_host_bin(name: &str) -> Result<PathBuf, String> {
    let path = std::env::var_os("PATH").ok_or("PATH is unset")?;
    let mut candidates = std::env::split_paths(&path).map(|dir| dir.join(name)).collect::<Vec<_>>();
    candidates.push(PathBuf::from("/run/current-system/sw/bin").join(name));
    candidates
        .into_iter()
        .find(|candidate| candidate.is_file())
        .ok_or_else(|| format!("{name} is unavailable"))
}

fn start_harness(config: &ServedRunnerConfig, pki: &Pki, dir: &Path) -> Result<Harness, String> {
    let storage = dir.join("storage");
    fs::create_dir(&storage).map_err(|e| format!("create fresh harness storage: {e}"))?;
    let mut child = envelope_command(&config.qualification_harness_executable)?
        .arg(&pki.server)
        .arg(&pki.server_key)
        .arg(&pki.ca)
        .arg(&storage)
        .arg(&config.principal)
        .arg(&config.tenant)
        .arg(&config.project)
        .arg(&config.database)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("start qualification harness: {e}"))?;
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
                Err(e) => {
                    let _ = sender.send(ReadEvent::Error(e.to_string()));
                    break;
                }
            }
        }
    });
    let stdin = child.stdin.take();
    let pid = child.id();
    Ok(Harness { child, pid, stdin, events })
}

fn wait_ready(harness: &mut Harness, deadline: Instant) -> Result<u16, String> {
    loop {
        let timeout =
            deadline.checked_duration_since(Instant::now()).ok_or("harness readiness timed out")?;
        match harness.events.recv_timeout(timeout) {
            Ok(ReadEvent::Line(line)) => {
                if let Some(port) = ready_port(&line) {
                    return Ok(port);
                }
            }
            Ok(ReadEvent::Eof) => {
                return Err("qualification harness exited before readiness".to_owned());
            }
            Ok(ReadEvent::Error(e)) => return Err(format!("read harness readiness: {e}")),
            Err(RecvTimeoutError::Timeout) => {
                return Err("qualification harness readiness timed out".to_owned());
            }
            Err(RecvTimeoutError::Disconnected) => {
                return Err("harness readiness channel closed".to_owned());
            }
        }
    }
}

fn ready_port(line: &str) -> Option<u16> {
    let port = line.strip_prefix(READY_PREFIX)?.parse::<u16>().ok()?;
    (port != 0).then_some(port)
}

fn run_adapter(
    config: &ServedRunnerConfig,
    pki: &Pki,
    output: &Path,
    endpoint: &str,
    deadline: Instant,
) -> Result<(), String> {
    let executable =
        config.adapter_executable.as_ref().ok_or("a prebuilt Go adapter executable is required")?;
    let mut command = Command::new(executable);
    command.current_dir(&config.go_workdir);
    command
        .env("TERRABASE_BENCH_TOPOLOGY", &config.topology)
        .env("TERRABASE_BENCH_RUNTIME_DIGEST", &config.runtime_digest)
        .env("TERRABASE_BENCH_COMPARATOR_DIGEST", &config.comparator_digest)
        .env("TERRABASE_BENCH_ENVELOPE_SPEC", "2ocpu/12GiB");
    let timeout = duration_argument(
        deadline.checked_duration_since(Instant::now()).ok_or("adapter deadline expired")?,
    );
    let args = [
        ("--endpoint", endpoint.to_owned()),
        ("--root-cert", path(&pki.ca).to_owned()),
        ("--client-cert", path(&pki.client).to_owned()),
        ("--client-key", path(&pki.client_key).to_owned()),
        ("--authority", DEFAULT_AUTHORITY.to_owned()),
        ("--principal", config.principal.clone()),
        ("--tenant", config.tenant.clone()),
        ("--project", config.project.clone()),
        ("--database", config.database.clone()),
        ("--server-identity", config.server_identity.clone()),
        ("--source-identity", config.source_identity.clone()),
        ("--transport-identity", config.transport_identity.clone()),
        ("--workload", config.workload.as_str().to_owned()),
        ("--out", output.to_string_lossy().into_owned()),
        ("--timeout", timeout),
        ("--seed-documents", config.lane.seed_documents.to_string()),
        ("--operations", config.lane.operations.to_string()),
        ("--samples", config.lane.samples.to_string()),
        ("--concurrency", config.lane.concurrency.to_string()),
        ("--warmup", config.lane.warmup_operations.to_string()),
        ("--noise-percent", config.lane.noise_percent.to_string()),
    ];
    for (name, value) in args {
        command.arg(name).arg(value);
    }
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("start Go benchmark adapter: {e}"))?;
    wait_child(&mut child, deadline, "Go benchmark adapter")
}

fn capture_resource_evidence(pid: u32) -> Result<ResourceEvidence, String> {
    let proc = PathBuf::from(format!("/proc/{pid}"));
    let cgroup_text = fs::read_to_string(proc.join("cgroup"))
        .map_err(|error| format!("read member cgroup: {error}"))?;
    let cgroup = cgroup_text
        .lines()
        .find_map(|line| line.strip_prefix("0::"))
        .ok_or("member is not running in a cgroup-v2 scope")?;
    let root = PathBuf::from("/sys/fs/cgroup").join(cgroup.trim_start_matches('/'));
    let read = |name: &str| {
        fs::read_to_string(root.join(name)).map(|value| value.trim().replace('\n', ";"))
    };
    let cpu_max = read("cpu.max").map_err(|error| format!("read cgroup cpu.max: {error}"))?;
    let cpu_stat = read("cpu.stat").map_err(|error| format!("read cgroup cpu.stat: {error}"))?;
    let cpuset =
        read("cpuset.cpus.effective").map_err(|error| format!("read cgroup cpuset: {error}"))?;
    let memory_max =
        read("memory.max").map_err(|error| format!("read cgroup memory.max: {error}"))?;
    let memory_peak =
        read("memory.peak").map_err(|error| format!("read cgroup memory.peak: {error}"))?;
    let io_stat = read("io.stat").map_err(|error| format!("read cgroup io.stat: {error}"))?;
    let status = fs::read_to_string(proc.join("status"))
        .map_err(|error| format!("read member RSS: {error}"))?;
    let rss_kib = status
        .lines()
        .find_map(|line| line.strip_prefix("VmHWM:").map(str::trim).map(str::to_owned))
        .ok_or("member RSS peak is missing from /proc status")?;
    if memory_max != "12884901888" {
        return Err(format!("member cgroup memory.max is {memory_max}, expected 12884901888"));
    }
    if !cpu_max.starts_with("200000 ") {
        return Err(format!("member cgroup cpu.max is {cpu_max}, expected 200000 quota"));
    }
    if cpuset.split(',').flat_map(|part| part.split('-')).count() < 2 {
        return Err(format!("member cgroup cpuset is not a two-CPU set: {cpuset}"));
    }
    Ok(ResourceEvidence {
        cgroup: cgroup.to_owned(),
        cpu_max,
        cpu_stat,
        cpuset,
        memory_max,
        memory_peak,
        rss_kib,
        io_stat,
    })
}

fn augment_output(
    output: &Path,
    evidence: &ResourceEvidence,
    config: &ServedRunnerConfig,
) -> Result<(), String> {
    let text =
        fs::read_to_string(output).map_err(|error| format!("read adapter result: {error}"))?;
    let trimmed = text.trim_end();
    let Some(prefix) = trimmed.strip_suffix('}') else {
        return Err("adapter result is not a JSON object".to_owned());
    };
    let augmented = format!("{prefix},\"evidence\":{} }}\n", evidence.json(config));
    let mut file = OpenOptions::new()
        .write(true)
        .truncate(true)
        .open(output)
        .map_err(|error| format!("open adapter result for evidence: {error}"))?;
    file.write_all(augmented.as_bytes())
        .and_then(|_| file.sync_all())
        .map_err(|error| format!("persist benchmark evidence: {error}"))
}

fn validate_output(
    output: &Path,
    started: SystemTime,
    config: &ServedRunnerConfig,
    endpoint: &str,
) -> Result<ServedRunnerResult, String> {
    let metadata = fs::metadata(output).map_err(|e| format!("adapter result is missing: {e}"))?;
    if !metadata.is_file() {
        return Err("adapter result is not a regular file".to_owned());
    }
    if metadata.len() > MAX_RESULT_BYTES {
        return Err(format!("adapter result exceeds {MAX_RESULT_BYTES} bytes"));
    }
    if metadata.modified().map_err(|e| format!("read adapter result timestamp: {e}"))? < started {
        return Err("adapter result is stale from before this invocation".to_owned());
    }
    let mut text = String::new();
    File::open(output)
        .map_err(|e| format!("open adapter result: {e}"))?
        .read_to_string(&mut text)
        .map_err(|e| format!("read adapter result: {e}"))?;
    let value =
        lane_run::json::parse(&text).map_err(|e| format!("malformed adapter result: {e}"))?;
    if lane_run::json::status(&value).map_err(|e| e.to_string())? != "complete" {
        return Err("adapter result reports a failed or incomplete run".to_owned());
    }
    let facts = lane_run::json::reference_side(&value)
        .map_err(|e| format!("invalid adapter result: {e}"))?;
    if facts.target != "terrabase" || facts.workload != config.workload.as_str() {
        return Err("adapter result target or workload does not match the request".to_owned());
    }
    if facts.config != config.lane {
        return Err("adapter result LaneConfig does not match request".to_owned());
    }
    if facts.endpoint != endpoint {
        return Err("adapter result endpoint does not match harness endpoint".to_owned());
    }
    if facts.observed_identity != config.server_identity {
        return Err(
            "adapter result server identity/version marker does not match request".to_owned()
        );
    }
    if facts.client_transport != config.transport_identity
        || facts.client_transport.contains("in-process")
        || !facts.client_transport.contains(ADAPTER_CONTRACT_IDENTITY)
    {
        return Err(
            "adapter result does not identify the requested network mTLS boundary".to_owned()
        );
    }
    if !has_source_identity(&value, &config.source_identity) {
        return Err("adapter result does not retain the requested source identity".to_owned());
    }
    validate_evidence(&value, config)?;
    if endpoint.starts_with("127.") || endpoint.starts_with("[::1]") || endpoint == "::1" {
        return Err(
            "loopback diagnostic result cannot be labelled complete; use a production Member boundary"
                .to_owned(),
        );
    }
    Ok(ServedRunnerResult { facts, output_path: output.to_owned(), endpoint: endpoint.to_owned() })
}

fn validate_evidence(
    value: &lane_run::json::Value,
    config: &ServedRunnerConfig,
) -> Result<(), String> {
    let lane_run::json::Value::Obj(fields) = value else {
        return Err("complete adapter result is not an object".to_owned());
    };
    let lane_run::json::Value::Obj(evidence) =
        fields.get("evidence").ok_or("complete adapter result is missing resource evidence")?
    else {
        return Err("complete adapter result evidence is not an object".to_owned());
    };
    let string = |name: &str| -> Result<&str, String> {
        match evidence.get(name) {
            Some(lane_run::json::Value::Str(value)) if !value.is_empty() => Ok(value),
            _ => Err(format!("complete adapter result evidence.{name} is missing")),
        }
    };
    if string("envelope_spec")? != "2ocpu/12GiB" {
        return Err("complete adapter result does not carry the 2 OCPU/12 GiB envelope".to_owned());
    }
    if string("topology")? != config.topology {
        return Err(
            "complete adapter result topology does not match the requested family".to_owned()
        );
    }
    if string("cgroup")? == "unknown" || string("io")? == "unknown" {
        return Err("complete adapter result is missing cgroup or I/O evidence".to_owned());
    }
    for name in ["cpu", "rss"] {
        if !matches!(evidence.get(name), Some(lane_run::json::Value::Obj(_))) {
            return Err(format!("complete adapter result evidence.{name} is missing"));
        }
    }
    if string("runtime_digest")? != config.runtime_digest {
        return Err("complete adapter runtime digest does not match the admitted immutable digest"
            .to_owned());
    }
    if string("comparator_digest")? != config.comparator_digest {
        return Err(
            "complete adapter comparator digest does not match the admitted immutable digest"
                .to_owned(),
        );
    }
    Ok(())
}

fn has_source_identity(value: &lane_run::json::Value, source: &str) -> bool {
    let lane_run::json::Value::Obj(fields) = value else { return false };
    let Some(lane_run::json::Value::Arr(limits)) = fields.get("limits") else { return false };
    limits.iter().any(|item| matches!(item, lane_run::json::Value::Str(value) if value == &format!("source identity: {source}")))
}

fn stop_harness(harness: &mut Harness, deadline: Instant) {
    if let Some(mut stdin) = harness.stdin.take() {
        let _ = stdin.write_all(b"shutdown\n");
        let _ = stdin.flush();
    }
    if wait_child(&mut harness.child, deadline, "qualification harness").is_err() {
        let _ = harness.child.kill();
        let _ = harness.child.wait();
    }
}

fn wait_child(child: &mut Child, deadline: Instant, name: &str) -> Result<(), String> {
    loop {
        match child.try_wait().map_err(|e| format!("wait for {name}: {e}"))? {
            Some(status) if status.success() => return Ok(()),
            Some(status) => return Err(format!("{name} exited with status {status}")),
            None if Instant::now() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!("{name} timed out"));
            }
            None => thread::sleep(Duration::from_millis(10)),
        }
    }
}

fn duration_argument(duration: Duration) -> String {
    if duration.as_secs() > 0 {
        format!("{}s", duration.as_secs())
    } else {
        format!("{}ms", duration.as_millis().max(1))
    }
}

fn unique_directory() -> Result<PathBuf, String> {
    let base = std::env::temp_dir();
    for _ in 0..32 {
        let id = COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos =
            SystemTime::now().duration_since(UNIX_EPOCH).map_err(|e| e.to_string())?.as_nanos();
        let path = base.join(format!("terrabase-served-bench-{}-{nanos}-{id}", std::process::id()));
        match fs::create_dir(&path) {
            Ok(()) => return Ok(path),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(e) => return Err(format!("create temporary benchmark directory: {e}")),
        }
    }
    Err("could not allocate unique temporary benchmark directory".to_owned())
}

fn unique_output(directory: &Path, workload: &str) -> Result<PathBuf, String> {
    fs::create_dir_all(directory).map_err(|e| format!("create benchmark output directory: {e}"))?;
    for _ in 0..32 {
        let id = COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos =
            SystemTime::now().duration_since(UNIX_EPOCH).map_err(|e| e.to_string())?.as_nanos();
        let path = directory.join(format!("terrabase-{workload}-{nanos}-{id}.json"));
        if !path.exists() {
            return Ok(path);
        }
    }
    Err("could not allocate unique benchmark output path".to_owned())
}

fn retain_failure(
    config: &ServedRunnerConfig,
    reason: String,
) -> Result<ServedRunnerResult, ServedRunnerError> {
    let retained_path =
        unique_output(&config.output_directory, config.workload.as_str()).ok().and_then(|path| {
            write_exclusive(&path, failure_document(config, &reason).as_bytes()).ok().map(|()| path)
        });
    Err(ServedRunnerError { reason, retained_path })
}

fn failure_document(config: &ServedRunnerConfig, reason: &str) -> String {
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
        "  \"environment\": {{\"observed_identity\": {}, \"endpoint\": \"unbound\", \"client_transport\": {}}},",
        json_string(&config.server_identity),
        json_string(&config.transport_identity)
    );
    let _ = writeln!(text, "  \"error\": {},", json_string(reason));
    let _ = writeln!(text, "  \"samples\": [],");
    let _ = writeln!(
        text,
        "  \"limits\": [{}, {}, {}, {}]",
        json_string(&format!("source identity: {}", config.source_identity)),
        json_string(&format!("topology family: {}", config.topology)),
        json_string(&format!("runtime digest: {}", config.runtime_digest)),
        json_string(&format!("comparator digest: {}", config.comparator_digest))
    );
    let _ = writeln!(text, "}}");
    text
}

fn write_exclusive(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| format!("create retained benchmark attempt: {e}"))?;
    file.write_all(bytes).map_err(|e| format!("write retained benchmark attempt: {e}"))?;
    file.sync_all().map_err(|e| format!("sync retained benchmark attempt: {e}"))
}

fn validate_text(name: &str, value: &str) -> Result<(), String> {
    if value.trim().is_empty() {
        return Err(format!("{name} is required"));
    }
    if value.len() > 4096 || value.bytes().any(|byte| byte == 0 || byte < 0x20) {
        return Err(format!("{name} contains a control character or is too long"));
    }
    Ok(())
}

fn required_string(name: &str) -> Result<String, String> {
    optional_env(name).ok_or_else(|| format!("{name} is required"))
}

fn required_digest(name: &str) -> Result<String, String> {
    let digest = required_string(name)?;
    if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(format!("{name} must be an immutable 64-hex digest"));
    }
    if digest.eq_ignore_ascii_case("unassigned") {
        return Err(format!("{name} cannot be unassigned"));
    }
    Ok(digest)
}

fn required_path(name: &str) -> Result<PathBuf, String> {
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
        value.parse().map_err(|e| format!("{name} is not a valid integer: {e}"))
    })
}

fn env_f64(name: &str, default: f64) -> Result<f64, String> {
    optional_env(name).map_or(Ok(default), |value| {
        value.parse().map_err(|e| format!("{name} is not a valid number: {e}"))
    })
}

fn env_duration(name: &str, default: Duration) -> Result<Duration, String> {
    let Some(value) = optional_env(name) else { return Ok(default) };
    let (number, multiplier) = if let Some(value) = value.strip_suffix("ms") {
        (value, 1_000_000_u64)
    } else if let Some(value) = value.strip_suffix('s') {
        (value, 1_000_000_000_u64)
    } else {
        return Err(format!("{name} must use ms or s"));
    };
    let number: u64 = number.parse().map_err(|e| format!("{name} is invalid: {e}"))?;
    Ok(Duration::from_nanos(
        number.checked_mul(multiplier).ok_or_else(|| format!("{name} is too large"))?,
    ))
}

fn json_string(value: &str) -> String {
    let mut result = String::with_capacity(value.len() + 2);
    result.push('"');
    for character in value.chars() {
        match character {
            '"' => result.push_str("\\\""),
            '\\' => result.push_str("\\\\"),
            '\n' => result.push_str("\\n"),
            '\r' => result.push_str("\\r"),
            '\t' => result.push_str("\\t"),
            other => result.push(other),
        }
    }
    result.push('"');
    result
}

#[cfg(test)]
mod tests {
    use super::super::lane_run::SampleFacts;
    use super::*;

    fn config(workload: Workload) -> ServedRunnerConfig {
        // The Go adapter tree is deleted (D-34 removed the gateway; the
        // benchmark client regenerates from the reduced proto at S-12), so
        // the config-mechanics tests point the workdir at a directory they
        // create: validate() checks mechanics, not the adapter's presence.
        let go_workdir =
            std::env::temp_dir().join(format!("terrabase-served-runner-go-{}", std::process::id()));
        std::fs::create_dir_all(&go_workdir).expect("test go workdir");
        ServedRunnerConfig {
            lane: LaneConfig::COMMISSIONING_TRIAL,
            workload,
            qualification_harness_executable: std::env::current_exe().expect("test executable"),
            go_executable: PathBuf::from("go"),
            go_workdir,
            adapter_executable: std::env::current_exe().ok(),
            output_directory: std::env::temp_dir().join("terrabase-served-runner-tests"),
            timeout: Duration::from_secs(1),
            server_identity: QUALIFICATION_SERVER_IDENTITY.to_owned(),
            principal: "spiffe://terrascale.tech/benchmark".to_owned(),
            tenant: "benchmark-tenant".to_owned(),
            project: "benchmark-project".to_owned(),
            database: "benchmark-database".to_owned(),
            source_identity: QUALIFICATION_SOURCE_IDENTITY.to_owned(),
            transport_identity: QUALIFICATION_TRANSPORT_IDENTITY.to_owned(),
            openssl_executable: PathBuf::from("openssl"),
            topology: "single-node".to_owned(),
            runtime_digest: "a".repeat(64),
            comparator_digest: "b".repeat(64),
        }
    }

    #[test]
    fn readiness_requires_the_exact_nonzero_port_line() {
        assert_eq!(ready_port(READY_PREFIX), None);
        assert_eq!(ready_port("TBEP_PRODUCTION_LISTENER_READY 0"), None);
        assert_eq!(ready_port("TBEP_PRODUCTION_LISTENER_READY 8432"), Some(8432));
        assert_eq!(ready_port("prefix TBEP_PRODUCTION_LISTENER_READY 8432"), None);
        assert_eq!(ready_port("TBEP_PRODUCTION_LISTENER_READY 8432 extra"), None);
    }

    #[test]
    fn runner_leaves_mutation_claim_admission_to_the_boundary() {
        config(Workload::DocumentWrite).validate().expect("runner mechanics support writes");
        config(Workload::Mixed).validate().expect("runner mechanics support mixed work");
    }

    /// The envelope must never lie: when the wrapped subject reports the
    /// spec env, the memory ceiling and CPU pin are actually in force;
    /// when the host cannot fence at all, the spec env is absent.
    #[test]
    fn envelope_command_fences_only_what_it_stamps() {
        use std::os::unix::fs::PermissionsExt;
        let probe_dir = std::env::temp_dir().join("terrabase-served-runner-envelope");
        fs::create_dir_all(&probe_dir).expect("probe directory");
        let probe = probe_dir.join("probe.sh");
        fs::write(
            &probe,
            "#!/usr/bin/env bash\n\
             CG=$(cut -d: -f3 /proc/self/cgroup)\n\
             echo \"spec=${TERRABASE_BENCH_ENVELOPE_SPEC:-}\"\n\
             echo \"memory=$(cat /sys/fs/cgroup$CG/memory.max 2>/dev/null || echo unknown)\"\n\
             echo \"nproc=$(nproc)\"\n",
        )
        .expect("probe script");
        fs::set_permissions(&probe, fs::Permissions::from_mode(0o755)).expect("probe executable");
        let output = envelope_command(&probe)
            .expect("a cgroup-capable test host is required")
            .output()
            .expect("probe under the envelope");
        let stdout = String::from_utf8_lossy(&output.stdout);
        let mut spec = "";
        let mut memory = "";
        let mut nproc = "";
        for line in stdout.lines() {
            if let Some(value) = line.strip_prefix("spec=") {
                spec = value;
            }
            if let Some(value) = line.strip_prefix("memory=") {
                memory = value;
            }
            if let Some(value) = line.strip_prefix("nproc=") {
                nproc = value;
            }
        }
        assert_eq!(spec, "2ocpu/12GiB", "the stamp must name the envelope");
        assert_eq!(nproc, "2", "the CPU pin must shrink the visible width");
        let ceiling = memory.parse::<u64>().unwrap_or(u64::MAX);
        assert_eq!(
            ceiling,
            12 * 1024 * 1024 * 1024,
            "the memory ceiling must be enforced (reported {memory})"
        );
    }

    #[test]
    fn runner_rejects_go_run_fallback() {
        let mut config = config(Workload::DocumentRead);
        config.adapter_executable = None;
        let error = config.validate().expect_err("go run cannot be reaped safely");
        assert!(error.contains("prebuilt Go adapter executable"));
        assert!(error.contains("go run"));
    }

    #[test]
    fn runner_rejects_unassigned_or_wrong_digest_values() {
        let mut digest_config = config(Workload::DocumentRead);
        digest_config.runtime_digest = "unassigned".to_owned();
        let error = digest_config.validate().expect_err("runtime identity must be immutable");
        assert!(error.contains("runtime digest"));
        let mut topology_config = config(Workload::DocumentRead);
        topology_config.topology = "rf3".to_owned();
        let error = topology_config.validate().expect_err("topology family is closed");
        assert!(error.contains("single-node or rf2"));
    }

    #[test]
    fn failed_attempt_document_has_no_samples() {
        let config = config(Workload::DocumentRead);
        let document = failure_document(&config, "adapter timed out");
        let parsed = lane_run::json::parse(&document).expect("failure JSON");
        assert_eq!(lane_run::json::status(&parsed).expect("status"), "failed");
        let lane_run::json::Value::Obj(fields) = parsed else {
            panic!("failure document is not an object");
        };
        assert!(
            matches!(fields.get("samples"), Some(lane_run::json::Value::Arr(samples)) if samples.is_empty())
        );
    }

    fn complete_document(config: &ServedRunnerConfig, endpoint: &str) -> String {
        let operations = u64::try_from(config.lane.operations).expect("test operation count fits");
        let throughput = f64::from(
            u32::try_from(config.lane.operations).expect("test operation count fits exactly"),
        );
        let side = SideFacts {
            target: "terrabase".to_owned(),
            workload: config.workload.as_str().to_owned(),
            config: config.lane,
            observed_identity: config.server_identity.clone(),
            endpoint: endpoint.to_owned(),
            client_transport: config.transport_identity.clone(),
            samples: vec![
                SampleFacts {
                    operations,
                    wall_seconds: 1.0,
                    throughput_ops_per_s: throughput,
                    p50_us: 1.0,
                    p95_us: 2.0,
                    p99_us: 3.0,
                };
                config.lane.samples
            ],
        };
        let rendered = lane_run::render_facts_json(
            &side,
            1,
            "test-kernel",
            1,
            &[format!("source identity: {}", config.source_identity)],
        );
        let evidence = ResourceEvidence {
            cgroup: "/test.scope".to_owned(),
            cpu_max: "200000 100000".to_owned(),
            cpu_stat: "usage_usec 1".to_owned(),
            cpuset: "0-1".to_owned(),
            memory_max: "12884901888".to_owned(),
            memory_peak: "1".to_owned(),
            rss_kib: "1 kB".to_owned(),
            io_stat: "8:0 rbytes=1".to_owned(),
        };
        let prefix = rendered.trim_end().strip_suffix('}').expect("object");
        format!("{prefix},\"evidence\":{} }}\n", evidence.json(config))
    }

    fn temporary_result(config: &ServedRunnerConfig, text: &str) -> PathBuf {
        let path = unique_output(&config.output_directory, "test").expect("unique result path");
        write_exclusive(&path, text.as_bytes()).expect("write test result");
        path
    }

    #[test]
    fn malformed_output_is_rejected_without_parsing_partial_samples() {
        let config = config(Workload::DocumentRead);
        let path = temporary_result(&config, "{not json");
        let error = validate_output(&path, UNIX_EPOCH, &config, "127.0.0.1:8421")
            .expect_err("malformed output must fail");
        assert!(error.contains("malformed"));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn stale_output_is_rejected_before_boundary_validation() {
        let config = config(Workload::DocumentRead);
        let path = temporary_result(&config, &complete_document(&config, "127.0.0.1:8421"));
        let error = validate_output(
            &path,
            SystemTime::now() + Duration::from_secs(1),
            &config,
            "127.0.0.1:8421",
        )
        .expect_err("stale output must fail");
        assert!(error.contains("stale"));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn boundary_endpoint_mismatch_is_rejected() {
        let config = config(Workload::DocumentRead);
        let path = temporary_result(&config, &complete_document(&config, "127.0.0.1:8421"));
        let error = validate_output(&path, UNIX_EPOCH, &config, "127.0.0.1:8422")
            .expect_err("wrong endpoint must fail");
        assert!(error.contains("endpoint"));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn exact_lane_configuration_is_required() {
        let config = config(Workload::DocumentRead);
        let path = temporary_result(&config, &complete_document(&config, "127.0.0.1:8421"));
        let mut changed = config.clone();
        changed.lane = changed.lane.with_operations(63);
        let error = validate_output(&path, UNIX_EPOCH, &changed, "127.0.0.1:8421")
            .expect_err("changed lane config must fail");
        assert!(error.contains("LaneConfig"));
        let _ = fs::remove_file(path);
    }
}
