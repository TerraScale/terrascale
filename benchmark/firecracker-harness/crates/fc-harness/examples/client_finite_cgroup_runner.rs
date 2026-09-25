//! Guest-only finite-memory cgroup wrapper for the client process test.
//!
//! The harness stages this executable as the primary payload and the test,
//! server, and keygen executables as supplemental payloads. No host cgroup is touched.

use std::env;
use std::fs;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::Serialize;

const CGROUP_MOUNT: &str = "/sys/fs/cgroup";
const AGENT_CGROUP: &str = "/system.slice/fc-harness-agent.service";
const PARENT_CGROUP: &str = "/sys/fs/cgroup/system.slice";
const TEST_BINARY: &str = "/root/fc-harness-payloads/client_process";
const SERVER_BINARY: &str = "/root/fc-harness-payloads/tb-server";
const KEYGEN_BINARY: &str = "/root/fc-harness-payloads/terrabase-keygen";
const INIT_BINARY: &str = "/root/fc-harness-payloads/terrabase-init";
const LIMIT_BYTES: u64 = 6 * 1024 * 1024 * 1024;
const TEST_TIMEOUT: Duration = Duration::from_secs(240);

#[derive(Serialize)]
struct Receipt {
    schema_version: u32,
    status: String,
    limit_bytes: u64,
    agent_cgroup: Option<String>,
    parent_cgroup: Option<String>,
    parent_controllers: Option<String>,
    parent_subtree_control: Option<String>,
    parent_processes: Option<String>,
    child_cgroup: Option<String>,
    child_memory_max: Option<String>,
    wrapper_membership_in_child: Option<String>,
    test_pid: Option<u32>,
    test_membership: Option<String>,
    test_exit_code: Option<i32>,
    test_timed_out: bool,
    memory_current_after_test: Option<String>,
    memory_max_after_test: Option<String>,
    memory_events_after_test: Option<String>,
    wrapper_membership_after_return: Option<String>,
    remaining_child_processes: Option<String>,
    child_removed: bool,
    errors: Vec<String>,
}

impl Receipt {
    fn new() -> Self {
        Self {
            schema_version: 1,
            status: "preflight".to_owned(),
            limit_bytes: LIMIT_BYTES,
            agent_cgroup: None,
            parent_cgroup: None,
            parent_controllers: None,
            parent_subtree_control: None,
            parent_processes: None,
            child_cgroup: None,
            child_memory_max: None,
            wrapper_membership_in_child: None,
            test_pid: None,
            test_membership: None,
            test_exit_code: None,
            test_timed_out: false,
            memory_current_after_test: None,
            memory_max_after_test: None,
            memory_events_after_test: None,
            wrapper_membership_after_return: None,
            remaining_child_processes: None,
            child_removed: false,
            errors: Vec::new(),
        }
    }
}

fn read(path: &Path) -> Result<String, String> {
    fs::read_to_string(path)
        .map(|value| value.trim_end_matches('\n').to_owned())
        .map_err(|error| format!("{}: {error}", path.display()))
}

fn write(path: &Path, value: &str) -> Result<(), String> {
    fs::write(path, value).map_err(|error| format!("{}: {error}", path.display()))
}

fn has_word(value: &str, word: &str) -> bool {
    value.split_whitespace().any(|item| item == word)
}

fn membership() -> Result<String, String> {
    let value = read(Path::new("/proc/self/cgroup"))?;
    let mut unified = value.lines().filter_map(|line| line.strip_prefix("0::"));
    match (unified.next(), unified.next()) {
        (Some(path), None) if path.starts_with('/') => Ok(path.to_owned()),
        _ => Err(format!(
            "expected one unified cgroup membership, got {value:?}"
        )),
    }
}

fn require_mount() -> Result<(), String> {
    let mountinfo = read(Path::new("/proc/self/mountinfo"))?;
    let mut matches = 0;
    for line in mountinfo.lines() {
        let Some((left, right)) = line.split_once(" - ") else {
            continue;
        };
        let left: Vec<_> = left.split_whitespace().collect();
        let right: Vec<_> = right.split_whitespace().collect();
        if left.len() < 6 || right.len() < 3 || right[0] != "cgroup2" {
            continue;
        }
        if left[4] != CGROUP_MOUNT {
            continue;
        }
        matches += 1;
        if left[3] != "/"
            || !left[5].split(',').any(|option| option == "rw")
            || !right[2].split(',').any(|option| option == "rw")
        {
            return Err(format!(
                "cgroup2 mount is not the writable hierarchy root: {line}"
            ));
        }
    }
    if matches != 1 {
        return Err(format!(
            "expected one cgroup2 mount at {CGROUP_MOUNT}, found {matches}"
        ));
    }
    Ok(())
}

fn effective_uid() -> Result<u32, String> {
    let status = read(Path::new("/proc/self/status"))?;
    let uid_line = status
        .lines()
        .find(|line| line.starts_with("Uid:"))
        .ok_or("/proc/self/status has no Uid line")?;
    uid_line
        .split_whitespace()
        .nth(2)
        .ok_or("/proc/self/status has no effective UID")?
        .parse()
        .map_err(|error| format!("invalid effective UID: {error}"))
}

fn verify_executable(path: &Path) -> Result<(), String> {
    let meta =
        fs::symlink_metadata(path).map_err(|error| format!("{}: {error}", path.display()))?;
    if !meta.file_type().is_file() || meta.permissions().mode() & 0o111 == 0 {
        return Err(format!(
            "{} is not a regular executable file",
            path.display()
        ));
    }
    Ok(())
}

fn preflight(receipt: &mut Receipt) -> Result<PathBuf, String> {
    verify_executable(Path::new(TEST_BINARY))?;
    verify_executable(Path::new(SERVER_BINARY))?;
    verify_executable(Path::new(KEYGEN_BINARY))?;
    verify_executable(Path::new(INIT_BINARY))?;
    require_mount()?;
    let current = membership()?;
    receipt.agent_cgroup = Some(current.clone());
    if current != AGENT_CGROUP {
        return Err(format!("unexpected agent cgroup {current:?}"));
    }
    if effective_uid()? != 0 {
        return Err("guest wrapper must run with effective UID 0".to_owned());
    }
    let parent = PathBuf::from(PARENT_CGROUP);
    let meta =
        fs::symlink_metadata(&parent).map_err(|error| format!("{}: {error}", parent.display()))?;
    let mode = meta.permissions().mode();
    if !meta.file_type().is_dir() || meta.uid() != 0 || mode & 0o300 != 0o300 {
        return Err(format!(
            "parent cgroup is not a root-owned writable/searchable directory: uid={} mode={:04o}",
            meta.uid(),
            mode & 0o7777
        ));
    }
    receipt.parent_cgroup = Some(parent.display().to_string());
    if read(&parent.join("cgroup.type"))? != "domain" {
        return Err("parent cgroup is not a domain cgroup".to_owned());
    }
    let controllers = read(&parent.join("cgroup.controllers"))?;
    let subtree = read(&parent.join("cgroup.subtree_control"))?;
    let procs = read(&parent.join("cgroup.procs"))?;
    receipt.parent_controllers = Some(controllers.clone());
    receipt.parent_subtree_control = Some(subtree.clone());
    receipt.parent_processes = Some(procs.clone());
    if !has_word(&controllers, "memory") || !has_word(&subtree, "memory") {
        return Err(
            "memory controller is not already enabled for system.slice children".to_owned(),
        );
    }
    if !procs.is_empty() {
        return Err("system.slice unexpectedly contains internal processes".to_owned());
    }
    Ok(parent)
}

struct CgroupLease {
    child: PathBuf,
    original: PathBuf,
    moved: bool,
    closed: bool,
}

impl CgroupLease {
    fn create(parent: &Path, receipt: &mut Receipt) -> Result<Self, String> {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| error.to_string())?
            .as_nanos();
        let child = parent.join(format!("terrabase-test-{}-{nanos}", std::process::id()));
        fs::create_dir(&child).map_err(|error| format!("{}: {error}", child.display()))?;
        receipt.child_cgroup = Some(child.display().to_string());
        Ok(Self {
            child,
            original: PathBuf::from(CGROUP_MOUNT).join(AGENT_CGROUP.trim_start_matches('/')),
            moved: false,
            closed: false,
        })
    }

    fn apply_limit(&self, receipt: &mut Receipt) -> Result<(), String> {
        if read(&self.child.join("cgroup.type"))? != "domain" {
            return Err("new child cgroup is not a domain cgroup".to_owned());
        }
        let limit = LIMIT_BYTES.to_string();
        write(&self.child.join("memory.max"), &limit)?;
        let observed = read(&self.child.join("memory.max"))?;
        receipt.child_memory_max = Some(observed.clone());
        if observed != limit {
            return Err(format!("child memory.max readback {observed:?} != {limit}"));
        }
        Ok(())
    }

    fn move_self(&mut self, receipt: &mut Receipt) -> Result<(), String> {
        write(
            &self.child.join("cgroup.procs"),
            &std::process::id().to_string(),
        )?;
        self.moved = true;
        let current = membership()?;
        receipt.wrapper_membership_in_child = Some(current.clone());
        let expected = format!(
            "/system.slice/{}",
            self.child
                .file_name()
                .ok_or("child cgroup has no basename")?
                .to_string_lossy()
        );
        if current != expected {
            return Err(format!("wrapper membership {current:?} != {expected:?}"));
        }
        Ok(())
    }

    fn observe_after_test(&self, receipt: &mut Receipt) {
        for (name, slot) in [
            ("memory.current", &mut receipt.memory_current_after_test),
            ("memory.max", &mut receipt.memory_max_after_test),
            ("memory.events", &mut receipt.memory_events_after_test),
        ] {
            match read(&self.child.join(name)) {
                Ok(value) => *slot = Some(value),
                Err(error) => receipt.errors.push(error),
            }
        }
    }

    fn close(&mut self, receipt: &mut Receipt) {
        if self.moved {
            if let Err(error) = write(
                &self.original.join("cgroup.procs"),
                &std::process::id().to_string(),
            ) {
                receipt
                    .errors
                    .push(format!("return to agent cgroup failed: {error}"));
                self.closed = true;
                return;
            }
            match membership() {
                Ok(value) => {
                    receipt.wrapper_membership_after_return = Some(value.clone());
                    if value != AGENT_CGROUP {
                        receipt
                            .errors
                            .push(format!("return membership {value:?} != {AGENT_CGROUP:?}"));
                        self.closed = true;
                        return;
                    }
                }
                Err(error) => {
                    receipt.errors.push(error);
                    self.closed = true;
                    return;
                }
            }
            self.moved = false;
        }
        match read(&self.child.join("cgroup.procs")) {
            Ok(procs) => {
                receipt.remaining_child_processes = Some(procs.clone());
                if procs.is_empty() {
                    match fs::remove_dir(&self.child) {
                        Ok(()) => receipt.child_removed = true,
                        Err(error) => receipt
                            .errors
                            .push(format!("{}: {error}", self.child.display())),
                    }
                }
            }
            Err(error) => receipt.errors.push(error),
        }
        self.closed = true;
    }
}

impl Drop for CgroupLease {
    fn drop(&mut self) {
        if !self.closed {
            if self.moved {
                let _ = write(
                    &self.original.join("cgroup.procs"),
                    &std::process::id().to_string(),
                );
            }
            let _ = fs::remove_dir(&self.child);
        }
    }
}

fn run_test(receipt: &mut Receipt, child: &Path) -> Result<(), String> {
    let mut command = Command::new(TEST_BINARY);
    command
        .args(env::args().skip(1))
        .env("TB_SERVER_TEST_BINARY", SERVER_BINARY)
        .env("TB_KEYGEN_TEST_BINARY", KEYGEN_BINARY)
        .env("TB_INIT_TEST_BINARY", INIT_BINARY)
        .stdin(Stdio::null())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit());
    let mut test = command
        .spawn()
        .map_err(|error| format!("spawn test: {error}"))?;
    receipt.test_pid = Some(test.id());
    let expected = format!(
        "/system.slice/{}",
        child
            .file_name()
            .ok_or("child cgroup has no basename")?
            .to_string_lossy()
    );
    let test_membership = read(&PathBuf::from(format!("/proc/{}/cgroup", test.id())))?;
    receipt.test_membership = Some(test_membership.clone());
    if test_membership != format!("0::{expected}") {
        let _ = test.kill();
        let _ = test.wait();
        return Err(format!(
            "test membership {test_membership:?} != 0::{expected}"
        ));
    }
    let started = Instant::now();
    loop {
        if let Some(status) = test
            .try_wait()
            .map_err(|error| format!("wait for test: {error}"))?
        {
            receipt.test_exit_code = status.code();
            if status.success() {
                return Ok(());
            }
            return Err(format!("test exited with {status}"));
        }
        if started.elapsed() >= TEST_TIMEOUT {
            receipt.test_timed_out = true;
            test.kill()
                .map_err(|error| format!("kill timed-out test: {error}"))?;
            let status = test
                .wait()
                .map_err(|error| format!("reap timed-out test: {error}"))?;
            receipt.test_exit_code = status.code();
            return Err(format!("test exceeded {} seconds", TEST_TIMEOUT.as_secs()));
        }
        thread::sleep(Duration::from_millis(50));
    }
}

fn run(receipt: &mut Receipt) -> Result<(), String> {
    let parent = preflight(receipt)?;
    let mut lease = CgroupLease::create(&parent, receipt)?;
    let result = lease
        .apply_limit(receipt)
        .and_then(|()| lease.move_self(receipt))
        .and_then(|()| run_test(receipt, &lease.child));
    lease.observe_after_test(receipt);
    lease.close(receipt);
    result?;
    if !receipt.child_removed || !receipt.errors.is_empty() {
        return Err("test cgroup cleanup was incomplete".to_owned());
    }
    Ok(())
}

fn main() {
    let mut receipt = Receipt::new();
    match run(&mut receipt) {
        Ok(()) => "passed".clone_into(&mut receipt.status),
        Err(error) => {
            "failed".clone_into(&mut receipt.status);
            receipt.errors.push(error);
        }
    }
    let output = env::var_os("TERRABASE_BENCH_OUTPUT_DIR")
        .map_or_else(|| PathBuf::from("/root/fc-harness-output"), PathBuf::from);
    let path = output.join("finite-cgroup-receipt.json");
    match serde_json::to_vec_pretty(&receipt) {
        Ok(json) => {
            if let Err(error) = fs::write(&path, json) {
                eprintln!("could not write {}: {error}", path.display());
                std::process::exit(1);
            }
        }
        Err(error) => {
            eprintln!("could not serialize finite cgroup receipt: {error}");
            std::process::exit(1);
        }
    }
    if receipt.status != "passed" {
        eprintln!("finite cgroup runner failed: {}", receipt.errors.join("; "));
        std::process::exit(1);
    }
}
