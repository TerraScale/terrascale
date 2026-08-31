//! Embedded Litestream supervision for `SQLite` disaster-recovery backups.
//!
//! `TerraBase` stores one `SQLite` file per tenant database under the member's
//! storage location (D-27) and streams those files to a replica for
//! point-in-time recovery. This crate owns the process side of that contract
//! (D-29): it writes a Litestream `replicate` config, spawns Litestream over
//! the watched directory with its own process group and an explicit minimal
//! environment, restarts it with bounded exponential backoff when it exits
//! unexpectedly, and terminates it with a SIGTERM, bounded grace, SIGKILL
//! escalation on drain.
//!
//! Backup supervision is disaster recovery only. It is deliberately never in
//! the serving path: a caller that fails to start the supervisor must stay
//! loud about it (log plus health metric) but must keep serving.

use std::ffi::OsString;
use std::fmt;
use std::net::SocketAddr;
use std::path::Path;
use std::path::PathBuf;
use std::process::Stdio;
use std::sync::Arc;
use std::sync::Mutex;
use std::time::Duration;
use std::time::Instant;

use tokio::process::Child;
use tokio::process::Command;
use tokio::sync::watch;

mod yaml;

/// Default directory watch pattern for tenant `SQLite` files.
pub const DEFAULT_PATTERN: &str = "*.sqlite3";

/// Environment variable that overrides the discovered Litestream binary.
pub const BINARY_OVERRIDE_ENV: &str = "TERRABASE_LITESTREAM_BIN";

/// Credential variables copied to the child when an S3 replica is
/// configured. Nothing else from the parent environment is inherited.
const S3_CREDENTIAL_ENV: [&str; 5] = [
    "AWS_ACCESS_KEY_ID",
    "AWS_SECRET_ACCESS_KEY",
    "AWS_SESSION_TOKEN",
    "LITESTREAM_ACCESS_KEY_ID",
    "LITESTREAM_SECRET_ACCESS_KEY",
];

/// Where the watched databases replicate to.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReplicaConfig {
    /// Local filesystem replica. The default: it needs no credentials and
    /// suits development and tests.
    File {
        /// Directory Litestream writes replica generations under, one
        /// subdirectory per database file.
        path: PathBuf,
    },
    /// S3-compatible replica such as OCI Object Storage.
    S3(S3Replica),
}

/// Connection settings for an S3-compatible replica. Credentials are never
/// held here; the child reads them from the inherited credential variables.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct S3Replica {
    /// S3-compatible endpoint URL, for example
    /// `https://{namespace}.compat.objectstorage.{region}.oraclecloud.com`.
    pub endpoint: String,
    /// Destination bucket.
    pub bucket: String,
    /// Prefix inside the bucket dedicated to this member.
    pub path: String,
    /// Signing region. OCI ignores the value but Litestream requires the
    /// field.
    pub region: String,
}

/// Restart pacing after an unexpected Litestream exit.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackoffConfig {
    /// Delay before the first restart.
    pub initial: Duration,
    /// Ceiling the doubling delay never exceeds.
    pub cap: Duration,
    /// A run that stays up at least this long resets the delay to
    /// `initial`.
    pub reset_after: Duration,
}

impl Default for BackoffConfig {
    fn default() -> Self {
        Self {
            initial: Duration::from_secs(1),
            cap: Duration::from_mins(1),
            reset_after: Duration::from_mins(1),
        }
    }
}

impl BackoffConfig {
    /// The delay to wait after a child that ran for `ran_for` exits, given
    /// the `previous` delay. A stable run resets to `initial`; otherwise the
    /// delay doubles up to `cap`.
    #[must_use]
    pub fn delay_after(&self, ran_for: Duration, previous: Option<Duration>) -> Duration {
        if ran_for >= self.reset_after {
            return self.initial;
        }
        previous.map_or(self.initial, |delay| delay.saturating_mul(2)).clamp(self.initial, self.cap)
    }
}

/// Everything the supervisor needs to run Litestream over one storage
/// location.
#[derive(Clone, Debug)]
pub struct SupervisorConfig {
    /// Directory holding the tenant `SQLite` files to watch.
    pub dir: PathBuf,
    /// Glob Litestream applies inside `dir`.
    pub pattern: String,
    /// Replica every watched database streams to.
    pub replica: ReplicaConfig,
    /// WAL polling interval. Whole seconds; values below one second are
    /// clamped.
    pub sync_interval: Duration,
    /// Prometheus `/metrics` bind address for the child. `None` leaves the
    /// endpoint off.
    pub metrics_addr: Option<SocketAddr>,
    /// Where the generated Litestream config is written. Defaults to
    /// `dir/litestream.yml`.
    pub config_path: Option<PathBuf>,
    /// Time between SIGTERM and the SIGKILL escalation on shutdown.
    pub shutdown_grace: Duration,
    /// Restart pacing.
    pub backoff: BackoffConfig,
}

impl SupervisorConfig {
    /// Builds a config with defaults: the standard pattern, a one second
    /// sync interval, no metrics endpoint, and a ten second shutdown grace.
    #[must_use]
    pub fn new(dir: PathBuf, replica: ReplicaConfig) -> Self {
        Self {
            dir,
            pattern: DEFAULT_PATTERN.to_owned(),
            replica,
            sync_interval: Duration::from_secs(1),
            metrics_addr: None,
            config_path: None,
            shutdown_grace: Duration::from_secs(10),
            backoff: BackoffConfig::default(),
        }
    }

    /// The path the Litestream config is written to.
    #[must_use]
    pub fn effective_config_path(&self) -> PathBuf {
        self.config_path.clone().unwrap_or_else(|| self.dir.join("litestream.yml"))
    }
}

/// How the last supervised child terminated.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ChildExit {
    /// Exit code, when the child exited on its own.
    pub code: Option<i32>,
    /// Terminating signal, when the child was killed by one (Unix).
    #[cfg(unix)]
    pub signal: Option<i32>,
}

impl ChildExit {
    fn record(status: std::process::ExitStatus) -> Self {
        Self {
            code: status.code(),
            #[cfg(unix)]
            signal: {
                use std::os::unix::process::ExitStatusExt as _;
                status.signal()
            },
        }
    }
}

impl fmt::Display for ChildExit {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        #[cfg(unix)]
        if let Some(signal) = self.signal {
            return write!(formatter, "signal {signal}");
        }
        match self.code {
            Some(code) => write!(formatter, "exit code {code}"),
            None => formatter.write_str("unknown termination"),
        }
    }
}

/// A point-in-time view of the supervised child.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SupervisorStatus {
    /// Whether a Litestream child process is currently alive.
    pub running: bool,
    /// Process id of the current child, when one is alive.
    pub pid: Option<u32>,
    /// Unexpected exits since the supervisor started.
    pub restarts: u32,
    /// Termination of the most recent child, when one has ended.
    pub last_exit: Option<ChildExit>,
}

#[derive(Debug, Default)]
struct StatusInner {
    running: bool,
    pid: Option<u32>,
    restarts: u32,
    last_exit: Option<ChildExit>,
}

#[derive(Debug)]
struct Inner {
    shutdown: watch::Sender<bool>,
    done: Mutex<Option<tokio::task::JoinHandle<Result<(), String>>>>,
    status: Mutex<StatusInner>,
    config_path: PathBuf,
}

/// A running Litestream child and the task that keeps it that way.
///
/// [`Self::shutdown`] terminates the child with the bounded SIGTERM then
/// SIGKILL escalation. Dropping every handle also terminates the child the
/// same way, so a crashed or torn-down caller cannot leak a replicating
/// process. The supervisor must be created and shut down inside a Tokio
/// runtime.
#[derive(Debug)]
pub struct LitestreamSupervisor {
    inner: Arc<Inner>,
}

impl LitestreamSupervisor {
    /// Writes the Litestream config, spawns the child, and starts
    /// supervising it.
    ///
    /// # Errors
    ///
    /// Returns an error when the Litestream binary cannot be discovered,
    /// the config file cannot be written, or the first child cannot be
    /// spawned. Later spawn failures are retried by the supervision loop.
    ///
    /// # Panics
    ///
    /// Panics when called outside a Tokio runtime.
    pub fn start(config: SupervisorConfig) -> Result<Self, SupervisionError> {
        let binary = litestream_binary().ok_or(SupervisionError::BinaryNotFound)?;
        match probe_with(&binary) {
            Some(version) => {
                eprintln!("terrabase: backup supervision using litestream {version}");
            }
            None => eprintln!(
                "terrabase: backup supervision could not read a version from {}",
                binary.display()
            ),
        }
        std::fs::create_dir_all(&config.dir).map_err(SupervisionError::WatchDirectory)?;
        let document = yaml::render(
            &config.dir,
            &config.pattern,
            &config.replica,
            config.sync_interval,
            config.metrics_addr,
        );
        let rendered = serde_yaml::to_string(&document).map_err(SupervisionError::RenderConfig)?;
        std::fs::write(config.effective_config_path(), rendered)
            .map_err(SupervisionError::WriteConfig)?;
        let child = build_command(&binary, &config).spawn().map_err(SupervisionError::Spawn)?;

        let (shutdown, shutdown_rx) = watch::channel(false);
        let inner = Arc::new(Inner {
            shutdown,
            done: Mutex::new(None),
            status: Mutex::new(StatusInner::default()),
            config_path: config.effective_config_path(),
        });
        set_running(&inner, true, child.id());
        let task =
            tokio::spawn(supervise(binary, config, shutdown_rx, Arc::downgrade(&inner), child));
        *inner.done.lock().expect("supervisor state unpoisoned") = Some(task);
        Ok(Self { inner })
    }

    /// The path the generated Litestream config was written to.
    #[must_use]
    pub fn config_path(&self) -> &Path {
        &self.inner.config_path
    }

    /// A snapshot of child liveness and restart history.
    ///
    /// # Panics
    ///
    /// Panics when the shared supervisor state is poisoned.
    #[must_use]
    pub fn status(&self) -> SupervisorStatus {
        let status = self.inner.status.lock().expect("supervisor state unpoisoned");
        SupervisorStatus {
            running: status.running,
            pid: status.pid,
            restarts: status.restarts,
            last_exit: status.last_exit,
        }
    }

    /// Signals the child to stop and waits until it is terminated.
    ///
    /// The child receives SIGTERM, gets the configured grace period, and is
    /// then killed with SIGKILL along with its whole process group. Repeated
    /// calls are no-ops once the child is down.
    ///
    /// # Errors
    ///
    /// Returns an error when the supervision task itself failed.
    ///
    /// # Panics
    ///
    /// Panics when the shared supervisor state is poisoned.
    pub async fn shutdown(&self) -> Result<(), SupervisionError> {
        self.inner.shutdown.send_if_modified(|requested| {
            if *requested {
                false
            } else {
                *requested = true;
                true
            }
        });
        let task = self.inner.done.lock().expect("supervisor state unpoisoned").take();
        if let Some(task) = task {
            task.await
                .map_err(|_| SupervisionError::SupervisionTaskFailed)?
                .map_err(SupervisionError::SupervisionTask)?;
        }
        Ok(())
    }
}

/// Runs the supervise-restart loop until shutdown is requested or every
/// supervisor handle is dropped.
async fn supervise(
    binary: PathBuf,
    config: SupervisorConfig,
    mut shutdown_rx: watch::Receiver<bool>,
    inner: std::sync::Weak<Inner>,
    mut child: Child,
) -> Result<(), String> {
    let mut delay: Option<Duration> = None;
    loop {
        if let Some(inner) = inner.upgrade() {
            set_running(&inner, true, child.id());
        }
        let started = Instant::now();
        let exit = tokio::select! {
            exit = child.wait() => Some(exit),
            _ = termination_requested(&mut shutdown_rx) => None,
        };
        let Some(status) = exit else {
            if let Some(inner) = inner.upgrade() {
                record_termination(&inner, terminate(child, config.shutdown_grace).await, false);
                set_running(&inner, false, None);
            } else {
                terminate(child, config.shutdown_grace).await;
            }
            return Ok(());
        };
        let status =
            status.map_err(|error| format!("waiting for the litestream child: {error}"))?;
        if let Some(inner) = inner.upgrade() {
            record_termination(&inner, Some(ChildExit::record(status)), true);
            set_running(&inner, false, None);
        }
        delay = Some(config.backoff.delay_after(started.elapsed(), delay));
        let restart_delay = delay.expect("restart delay is set");
        let restart = tokio::select! {
            () = tokio::time::sleep(restart_delay) => true,
            _ = termination_requested(&mut shutdown_rx) => false,
        };
        if !restart {
            return Ok(());
        }
        loop {
            match build_command(&binary, &config).spawn() {
                Ok(next) => {
                    child = next;
                    break;
                }
                Err(error) => {
                    eprintln!(
                        "terrabase: litestream child spawn failed ({error}); retrying with backoff"
                    );
                    delay = Some(
                        delay.expect("delay is set").saturating_mul(2).min(config.backoff.cap),
                    );
                    let retry = tokio::select! {
                        () = tokio::time::sleep(delay.expect("delay is set")) => true,
                        _ = termination_requested(&mut shutdown_rx) => false,
                    };
                    if !retry {
                        return Ok(());
                    }
                }
            }
        }
    }
}

/// Marks the current child state in the shared status.
fn set_running(inner: &Inner, running: bool, pid: Option<u32>) {
    let mut status = inner.status.lock().expect("supervisor state unpoisoned");
    status.running = running;
    status.pid = pid;
}

/// Records a child termination and whether to count it as a restart.
fn record_termination(inner: &Inner, exit: Option<ChildExit>, unexpected: bool) {
    let mut status = inner.status.lock().expect("supervisor state unpoisoned");
    if unexpected {
        status.restarts = status.restarts.saturating_add(1);
        if let Some(exit) = exit {
            eprintln!("terrabase: litestream child exited unexpectedly ({exit}); restarting");
        }
    }
    if let Some(exit) = exit {
        status.last_exit = Some(exit);
    }
}

/// Resolves to `true` once shutdown has been requested or every supervisor
/// handle (and with them the shutdown sender) has been dropped.
async fn termination_requested(receiver: &mut watch::Receiver<bool>) -> bool {
    if *receiver.borrow() {
        return true;
    }
    match receiver.changed().await {
        Ok(()) => *receiver.borrow(),
        Err(_sender_dropped) => true,
    }
}

/// Stops one child: SIGTERM, bounded grace, then SIGKILL to its whole
/// process group.
async fn terminate(mut child: Child, grace: Duration) -> Option<ChildExit> {
    let pid = child.id();
    #[cfg(unix)]
    if let Some(pid) = pid {
        let _ = nix::sys::signal::kill(
            nix::unistd::Pid::from_raw(pid_to_i32(pid)),
            Some(nix::sys::signal::Signal::SIGTERM),
        );
    }
    #[cfg(not(unix))]
    let _ = pid;
    if let Ok(status) = tokio::time::timeout(grace, child.wait()).await {
        return status.ok().map(ChildExit::record);
    }
    eprintln!("terrabase: litestream child ignored SIGTERM; escalating to SIGKILL");
    #[cfg(unix)]
    if let Some(pid) = pid {
        let _ = nix::sys::signal::killpg(
            nix::unistd::Pid::from_raw(pid_to_i32(pid)),
            nix::sys::signal::Signal::SIGKILL,
        );
    }
    child.wait().await.ok().map(ChildExit::record)
}

#[cfg(unix)]
fn pid_to_i32(pid: u32) -> i32 {
    i32::try_from(pid).unwrap_or(-1)
}

/// Builds the `litestream replicate` command with its own process group and
/// an environment limited to what the replica needs.
fn build_command(binary: &Path, config: &SupervisorConfig) -> Command {
    let mut command = Command::new(binary);
    command.arg("replicate").arg("-config").arg(config.effective_config_path());
    command.env_clear();
    command.envs(child_env(&config.replica, |key| std::env::var(key).ok()));
    command.stdout(Stdio::inherit()).stderr(Stdio::inherit());
    #[cfg(unix)]
    command.process_group(0);
    command
}

/// The exact environment the child receives: nothing for a file replica,
/// and only the credential variables for an S3 replica.
fn child_env<F>(replica: &ReplicaConfig, parent: F) -> Vec<(String, String)>
where
    F: Fn(&str) -> Option<String>,
{
    let ReplicaConfig::S3(_) = replica else {
        return Vec::new();
    };
    S3_CREDENTIAL_ENV
        .iter()
        .filter_map(|key| parent(key).map(|value| ((*key).to_owned(), value)))
        .collect()
}

/// Locates the Litestream binary: the override variable when set, otherwise
/// a PATH search for `litestream`.
#[must_use]
pub fn litestream_binary() -> Option<PathBuf> {
    if let Some(path) = std::env::var_os(BINARY_OVERRIDE_ENV)
        && !path.is_empty()
    {
        return Some(PathBuf::from(path));
    }
    find_in_path("litestream", std::env::var_os("PATH"))
}

/// Runs `litestream version` against the discovered binary and returns the
/// version it printed, for startup logs and availability checks.
#[must_use]
pub fn probe() -> Option<String> {
    probe_with(&litestream_binary()?)
}

fn probe_with(binary: &Path) -> Option<String> {
    let output = std::process::Command::new(binary)
        .arg("version")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let version = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    (!version.is_empty()).then_some(version)
}

/// Searches one `PATH`-style value for an executable `program`.
fn find_in_path(program: &str, path: Option<OsString>) -> Option<PathBuf> {
    let path = path?;
    std::env::split_paths(&path)
        .map(|directory| directory.join(program))
        .find(|candidate| is_executable_file(candidate))
}

fn is_executable_file(path: &Path) -> bool {
    let Ok(metadata) = std::fs::metadata(path) else {
        return false;
    };
    if !metadata.is_file() {
        return false;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        metadata.permissions().mode() & 0o111 != 0
    }
    #[cfg(not(unix))]
    {
        true
    }
}

/// A refused or failed supervision step.
#[derive(Debug)]
pub enum SupervisionError {
    /// No Litestream binary was discovered.
    BinaryNotFound,
    /// The watched directory could not be created.
    WatchDirectory(std::io::Error),
    /// The config document could not be rendered.
    RenderConfig(serde_yaml::Error),
    /// The config file could not be written.
    WriteConfig(std::io::Error),
    /// The first child could not be spawned.
    Spawn(std::io::Error),
    /// The supervision loop reported a failure.
    SupervisionTask(String),
    /// The supervision task panicked or was cancelled.
    SupervisionTaskFailed,
}

impl fmt::Display for SupervisionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BinaryNotFound => write!(
                formatter,
                "no litestream binary found; set {BINARY_OVERRIDE_ENV} or install litestream on PATH"
            ),
            Self::WatchDirectory(error) => {
                write!(formatter, "the watched directory could not be created: {error}")
            }
            Self::RenderConfig(error) => {
                write!(formatter, "the litestream config could not be rendered: {error}")
            }
            Self::WriteConfig(error) => {
                write!(formatter, "the litestream config could not be written: {error}")
            }
            Self::Spawn(error) => {
                write!(formatter, "the litestream child could not be spawned: {error}")
            }
            Self::SupervisionTask(detail) => {
                write!(formatter, "litestream supervision stopped: {detail}")
            }
            Self::SupervisionTaskFailed => {
                formatter.write_str("the litestream supervision task failed")
            }
        }
    }
}

impl std::error::Error for SupervisionError {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backoff_doubles_caps_and_resets() {
        let backoff = BackoffConfig {
            initial: Duration::from_secs(1),
            cap: Duration::from_secs(4),
            reset_after: Duration::from_mins(1),
        };
        assert_eq!(backoff.delay_after(Duration::from_secs(0), None), Duration::from_secs(1));
        assert_eq!(
            backoff.delay_after(Duration::from_secs(0), Some(Duration::from_secs(1))),
            Duration::from_secs(2)
        );
        assert_eq!(
            backoff.delay_after(Duration::from_secs(0), Some(Duration::from_secs(2))),
            Duration::from_secs(4)
        );
        assert_eq!(
            backoff.delay_after(Duration::from_secs(0), Some(Duration::from_secs(4))),
            Duration::from_secs(4)
        );
        assert_eq!(
            backoff.delay_after(Duration::from_mins(1), Some(Duration::from_secs(4))),
            Duration::from_secs(1)
        );
    }

    #[test]
    fn file_replica_child_env_is_empty() {
        assert!(
            child_env(&ReplicaConfig::File { path: PathBuf::from("/backups") }, |key| matches!(
                key,
                "AWS_ACCESS_KEY_ID"
            )
            .then(|| "leak".to_owned()))
            .is_empty()
        );
    }

    #[test]
    fn s3_replica_child_env_copies_only_credentials() {
        let env = child_env(
            &ReplicaConfig::S3(S3Replica {
                endpoint: "https://endpoint".to_owned(),
                bucket: "bucket".to_owned(),
                path: "prefix".to_owned(),
                region: "gru".to_owned(),
            }),
            |key| match key {
                "AWS_ACCESS_KEY_ID" => Some("id".to_owned()),
                "AWS_SECRET_ACCESS_KEY" => Some("secret".to_owned()),
                "LITESTREAM_ACCESS_KEY_ID" => Some("other-id".to_owned()),
                _ => None,
            },
        );
        assert_eq!(
            env,
            vec![
                ("AWS_ACCESS_KEY_ID".to_owned(), "id".to_owned()),
                ("AWS_SECRET_ACCESS_KEY".to_owned(), "secret".to_owned()),
                ("LITESTREAM_ACCESS_KEY_ID".to_owned(), "other-id".to_owned()),
            ]
        );
    }

    #[test]
    fn config_path_defaults_into_the_watched_directory() {
        let config = SupervisorConfig::new(
            PathBuf::from("/var/lib/terrabase"),
            ReplicaConfig::File { path: PathBuf::from("/backups") },
        );
        assert_eq!(
            config.effective_config_path(),
            PathBuf::from("/var/lib/terrabase/litestream.yml")
        );
        assert_eq!(config.pattern, DEFAULT_PATTERN);
    }

    #[test]
    fn path_search_finds_executable_programs_only() {
        let directory =
            std::env::temp_dir().join(format!("terrabase-backup-path-{}", std::process::id()));
        std::fs::create_dir_all(&directory).expect("temp directory creates");
        let program = directory.join("litestream");
        std::fs::write(&program, b"#!/bin/sh\nexit 0\n").expect("program writes");

        let search = |path: Option<OsString>| find_in_path("litestream", path);
        let joined = std::env::join_paths([directory.clone()]).expect("path joins");
        assert!(search(Some(joined)).is_none(), "a non-executable file must not match");

        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(&program, std::fs::Permissions::from_mode(0o755))
                .expect("program permissions");
        }
        let joined = std::env::join_paths([directory.clone()]).expect("path joins");
        assert_eq!(search(Some(joined)), Some(program.clone()));
        assert!(search(None).is_none());
        std::fs::remove_file(&program).expect("program removes");
        std::fs::remove_dir(&directory).expect("temp directory removes");
    }
}
