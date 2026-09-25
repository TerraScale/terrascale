use std::env;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

const BRIDGE_IPV4: &str = "10.77.0.1/24";
const GUEST_IPV4: &str = "10.77.0.2/24";
const GUEST_MAC: &str = "06:00:0a:4d:00:02";

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct NetworkReceipt {
    pub schema_version: u32,
    pub run_id: String,
    pub status: String,
    pub namespace: String,
    pub namespace_inode: Option<u64>,
    pub bridge: String,
    pub tap: String,
    pub bridge_ipv4: String,
    pub guest_ipv4: String,
    pub guest_mac: String,
    pub owner_uid: u32,
    pub owner_gid: u32,
    pub helper_path: String,
    pub helper_sha256: String,
}

pub struct NetworkLease {
    receipt: NetworkReceipt,
    path: PathBuf,
    active: bool,
}

#[derive(Debug, Deserialize, Serialize)]
struct VmmProcess {
    schema_version: u32,
    namespace: String,
    namespace_inode: u64,
    pid: u32,
    start_time_ticks: u64,
}

impl NetworkLease {
    pub fn create(
        output: &Path,
        run_id: &str,
        helper_sha256: &str,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let output = fs::canonicalize(output)?;
        let mut random = [0_u8; 16];
        File::open("/dev/urandom")?.read_exact(&mut random)?;
        let mut token = String::with_capacity(32);
        for byte in random {
            write!(&mut token, "{byte:02x}")?;
        }
        let short = &token[..12];
        let owner = fs::metadata(&output)?;
        let receipt = NetworkReceipt {
            schema_version: 2,
            run_id: run_id.to_owned(),
            status: "planned".to_owned(),
            namespace: format!("fcn-{token}"),
            namespace_inode: None,
            bridge: format!("fcb{short}"),
            tap: format!("fct{short}"),
            bridge_ipv4: BRIDGE_IPV4.to_owned(),
            guest_ipv4: GUEST_IPV4.to_owned(),
            guest_mac: GUEST_MAC.to_owned(),
            owner_uid: owner.uid(),
            owner_gid: owner.gid(),
            helper_path: output.join("network-helper.bin").display().to_string(),
            helper_sha256: helper_sha256.to_owned(),
        };
        let mut lease = Self {
            receipt,
            path: output.join("network-ownership.json"),
            active: false,
        };
        lease.validate_helper()?;
        lease.write()?;
        run_ip(&["netns", "add", &lease.receipt.namespace])?;
        // This opt-in fault injection leaves the durable planned receipt and a
        // bare namespace for explicit crash-recovery verification.
        if env::var("FC_HARNESS_TEST_EXIT_AFTER_NETNS_ADD").as_deref() == Ok("1") {
            std::process::exit(91);
        }
        lease.active = true;
        let namespace_path = lease.namespace_path();
        let namespace_metadata = match fs::metadata(&namespace_path) {
            Ok(metadata) => metadata,
            Err(error) => {
                if run_ip(&["netns", "delete", &lease.receipt.namespace]).is_ok() {
                    lease.active = false;
                    "absent".clone_into(&mut lease.receipt.status);
                    lease.write()?;
                }
                return Err(error.into());
            }
        };
        lease.receipt.namespace_inode = Some(namespace_metadata.ino());
        "allocated".clone_into(&mut lease.receipt.status);
        lease.write()?;

        lease.configure()?;
        "ready".clone_into(&mut lease.receipt.status);
        lease.write()?;
        Ok(lease)
    }

    fn configure(&self) -> Result<(), Box<dyn std::error::Error>> {
        run_ip(&[
            "-n",
            &self.receipt.namespace,
            "link",
            "add",
            &self.receipt.bridge,
            "type",
            "bridge",
        ])?;
        run_ip(&[
            "-n",
            &self.receipt.namespace,
            "addr",
            "add",
            BRIDGE_IPV4,
            "dev",
            &self.receipt.bridge,
        ])?;
        run_ip(&[
            "-n",
            &self.receipt.namespace,
            "link",
            "set",
            &self.receipt.bridge,
            "up",
        ])?;
        let owner_uid = self.receipt.owner_uid.to_string();
        run_ip(&[
            "netns",
            "exec",
            &self.receipt.namespace,
            "ip",
            "tuntap",
            "add",
            "dev",
            &self.receipt.tap,
            "mode",
            "tap",
            "user",
            &owner_uid,
        ])?;
        run_ip(&[
            "-n",
            &self.receipt.namespace,
            "link",
            "set",
            &self.receipt.tap,
            "master",
            &self.receipt.bridge,
        ])?;
        run_ip(&[
            "-n",
            &self.receipt.namespace,
            "link",
            "set",
            &self.receipt.tap,
            "up",
        ])?;
        Ok(())
    }

    pub fn receipt(&self) -> &NetworkReceipt {
        &self.receipt
    }

    pub fn firecracker_command(
        &self,
        affinity: &str,
        executable: &Path,
    ) -> Result<Command, Box<dyn std::error::Error>> {
        let launcher = self.validate_helper()?;
        let mut command = Command::new("sudo");
        command
            .args(["-n", "ip", "netns", "exec", &self.receipt.namespace])
            .arg(&launcher)
            .arg("--internal-vmm-launch")
            .arg(&self.path)
            .arg(affinity)
            .arg(executable)
            .arg("--");
        Ok(command)
    }

    pub fn launch_vmm(args: &[String]) -> Result<(), Box<dyn std::error::Error>> {
        if args.len() < 4 || args[3] != "--" {
            return Err("invalid internal VMM launcher arguments".into());
        }
        let path = Path::new(&args[0]);
        let receipt: NetworkReceipt = serde_json::from_slice(&fs::read(path)?)?;
        let lease = Self {
            receipt,
            path: path.to_path_buf(),
            active: false,
        };
        lease.validate_names()?;
        lease.validate_running_helper()?;
        if lease.receipt.status != "ready" {
            return Err("network is not ready for VMM launch".into());
        }
        let namespace_inode = lease
            .receipt
            .namespace_inode
            .ok_or("network namespace inode is absent")?;
        if fs::metadata("/proc/self/ns/net")?.ino() != namespace_inode
            || fs::metadata(lease.namespace_path())?.ino() != namespace_inode
        {
            return Err("VMM launcher is not in its recorded network namespace".into());
        }
        let pid = std::process::id();
        let process = VmmProcess {
            schema_version: 1,
            namespace: lease.receipt.namespace.clone(),
            namespace_inode,
            pid,
            start_time_ticks: process_start_time(pid)?,
        };
        let process_path = lease.process_path();
        let mut file = File::create_new(&process_path)?;
        serde_json::to_writer_pretty(&mut file, &process)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        File::open(path.parent().ok_or("network receipt has no parent")?)?.sync_all()?;

        let error = Command::new("setpriv")
            .args(["--reuid", &lease.receipt.owner_uid.to_string()])
            .args(["--regid", &lease.receipt.owner_gid.to_string()])
            .args([
                "--init-groups",
                "--pdeathsig",
                "SIGKILL",
                "--",
                "taskset",
                "-c",
            ])
            .arg(&args[1])
            .arg(&args[2])
            .args(&args[4..])
            .exec();
        Err(error.into())
    }

    pub fn guest_network_file(&self) -> String {
        format!(
            "[Match]\nMACAddress={}\n\n[Network]\nAddress={}\n",
            self.receipt.guest_mac, self.receipt.guest_ipv4
        )
    }

    pub fn cleanup(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        if !self.active {
            return Ok(());
        }
        self.validate_names()?;
        let expected_inode = match self.receipt.namespace_inode {
            Some(inode) => inode,
            None if !self.namespace_path().exists() => {
                "absent".clone_into(&mut self.receipt.status);
                self.active = false;
                self.write()?;
                return self.remove_helper();
            }
            None => return self.cleanup_planned_namespace(),
        };
        let metadata = match fs::metadata(self.namespace_path()) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                "absent".clone_into(&mut self.receipt.status);
                self.active = false;
                self.write()?;
                return self.remove_helper();
            }
            Err(error) => return Err(error.into()),
        };
        if metadata.ino() != expected_inode {
            return Err("network namespace inode changed; refusing cleanup".into());
        }
        let pids = namespace_pids(&self.receipt.namespace)?;
        if !pids.is_empty() {
            return Err(format!("network namespace still has processes: {pids:?}").into());
        }
        run_ip(&["netns", "delete", &self.receipt.namespace])?;
        self.active = false;
        "cleaned".clone_into(&mut self.receipt.status);
        self.write()?;
        self.remove_helper()
    }

    fn cleanup_planned_namespace(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        if self.receipt.status != "planned" {
            return Err("namespace inode is missing from a non-planned receipt".into());
        }
        let before = fs::metadata(self.namespace_path())?.ino();
        let pids = namespace_pids(&self.receipt.namespace)?;
        if !pids.is_empty() {
            return Err(format!("planned namespace has processes: {pids:?}").into());
        }
        let links: serde_json::Value = serde_json::from_str(&run_ip(&[
            "-n",
            &self.receipt.namespace,
            "-j",
            "link",
            "show",
        ])?)?;
        let only_loopback = links
            .as_array()
            .is_some_and(|links| links.len() == 1 && links[0]["ifname"] == "lo");
        if !only_loopback {
            return Err("planned namespace is not empty; refusing cleanup".into());
        }
        if fs::metadata(self.namespace_path())?.ino() != before
            || !namespace_pids(&self.receipt.namespace)?.is_empty()
        {
            return Err("planned namespace changed during reconciliation".into());
        }
        run_ip(&["netns", "delete", &self.receipt.namespace])?;
        self.active = false;
        "cleaned".clone_into(&mut self.receipt.status);
        self.write()?;
        self.remove_helper()
    }

    pub fn cleanup_record(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
        let path = fs::canonicalize(path)?;
        let receipt: NetworkReceipt = serde_json::from_slice(&fs::read(&path)?)?;
        let mut lease = Self {
            active: receipt.status != "cleaned" && receipt.status != "absent",
            receipt,
            path,
        };
        lease.validate_names()?;
        lease.stop_processes()?;
        lease.cleanup()?;
        lease.remove_helper()
    }

    pub fn stop_processes(&self) -> Result<(), Box<dyn std::error::Error>> {
        if !self.active {
            return Ok(());
        }
        self.validate_names()?;
        let Some(expected_inode) = self.receipt.namespace_inode else {
            if !self.namespace_path().exists() {
                return Ok(());
            }
            let pids = namespace_pids(&self.receipt.namespace)?;
            if pids.is_empty() {
                return Ok(());
            }
            return Err("planned namespace has unowned processes; refusing signals".into());
        };
        let metadata = match fs::metadata(self.namespace_path()) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
            Err(error) => return Err(error.into()),
        };
        if metadata.ino() != expected_inode {
            return Err("network namespace inode changed; refusing process signals".into());
        }
        let pids = namespace_pids(&self.receipt.namespace)?;
        if pids.is_empty() {
            return Ok(());
        }
        let process: VmmProcess = serde_json::from_slice(&fs::read(self.process_path())?)?;
        if process.schema_version != 1
            || process.namespace != self.receipt.namespace
            || process.namespace_inode != expected_inode
        {
            return Err("VMM process receipt does not match network ownership".into());
        }
        if pids.iter().any(|pid| *pid != process.pid) {
            return Err(format!("network namespace contains unowned processes: {pids:?}").into());
        }
        if !process_matches(&process)? {
            if namespace_pids(&self.receipt.namespace)?.is_empty() {
                return Ok(());
            }
            return Err("VMM PID identity changed; refusing process signal".into());
        }
        if let Err(error) = self.signal_vmm("TERM") {
            if namespace_pids(&self.receipt.namespace)?.is_empty() {
                return Ok(());
            }
            return Err(error);
        }
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(2) {
            if namespace_pids(&self.receipt.namespace)?.is_empty() {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(50));
        }
        let remaining = namespace_pids(&self.receipt.namespace)?;
        if remaining.is_empty() {
            return Ok(());
        }
        if remaining.iter().any(|pid| *pid != process.pid) {
            return Err(
                format!("network namespace contains unowned processes: {remaining:?}").into(),
            );
        }
        if process_matches(&process)? {
            self.signal_vmm("KILL")?;
        } else {
            return Err("VMM PID identity changed; refusing process signal".into());
        }
        if namespace_pids(&self.receipt.namespace)?.is_empty() {
            Ok(())
        } else {
            Err("network namespace still has processes after SIGKILL".into())
        }
    }

    fn signal_vmm(&self, signal: &str) -> Result<(), Box<dyn std::error::Error>> {
        let helper = self.validate_helper()?;
        let output = Command::new("sudo")
            .args(["-n"])
            .arg(helper)
            .arg("--internal-signal-vmm")
            .arg(&self.path)
            .arg(signal)
            .output()?;
        if !output.status.success() {
            return Err(format!(
                "could not send {signal} to run-owned VMM: {}",
                String::from_utf8_lossy(&output.stderr)
            )
            .into());
        }
        Ok(())
    }

    pub fn signal_vmm_record(path: &Path, signal: &str) -> Result<(), Box<dyn std::error::Error>> {
        let signal = match signal {
            "TERM" => libc::SIGTERM,
            "KILL" => libc::SIGKILL,
            _ => return Err("unsupported VMM signal".into()),
        };
        let path = fs::canonicalize(path)?;
        let receipt: NetworkReceipt = serde_json::from_slice(&fs::read(&path)?)?;
        let lease = Self {
            active: false,
            receipt,
            path,
        };
        lease.validate_names()?;
        lease.validate_running_helper()?;
        let expected_inode = lease
            .receipt
            .namespace_inode
            .ok_or("network namespace inode is absent; refusing signal")?;
        if fs::metadata(lease.namespace_path())?.ino() != expected_inode {
            return Err("network namespace inode changed; refusing signal".into());
        }
        let process: VmmProcess = serde_json::from_slice(&fs::read(lease.process_path())?)?;
        if process.schema_version != 1
            || process.namespace != lease.receipt.namespace
            || process.namespace_inode != expected_inode
        {
            return Err("VMM process receipt does not match network ownership".into());
        }
        let pidfd = pidfd_open(process.pid)?;
        if !process_matches(&process)? {
            return Err("VMM PID identity changed after opening pidfd; refusing signal".into());
        }
        if namespace_pids(&lease.receipt.namespace)? != [process.pid] {
            return Err("network namespace contains an unowned process; refusing signal".into());
        }
        pidfd_send_signal(&pidfd, signal)?;
        Ok(())
    }

    fn validate_names(&self) -> Result<(), Box<dyn std::error::Error>> {
        if self.receipt.schema_version != 2 {
            return Err("unsupported network ownership receipt version".into());
        }
        let token = self
            .receipt
            .namespace
            .strip_prefix("fcn-")
            .ok_or("unexpected network namespace prefix")?;
        if token.len() != 32 || !token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("invalid network namespace token".into());
        }
        if self.receipt.bridge != format!("fcb{}", &token[..12])
            || self.receipt.tap != format!("fct{}", &token[..12])
        {
            return Err("network bridge/TAP names do not match the namespace token".into());
        }
        Ok(())
    }

    fn validate_helper(&self) -> Result<PathBuf, Box<dyn std::error::Error>> {
        let expected = self
            .path
            .parent()
            .ok_or("network receipt has no parent")?
            .join("network-helper.bin");
        if self.receipt.helper_path != expected.display().to_string()
            || self.receipt.helper_sha256.len() != 64
            || !self
                .receipt
                .helper_sha256
                .bytes()
                .all(|byte| byte.is_ascii_hexdigit())
        {
            return Err("network helper path or digest is invalid".into());
        }
        let metadata = fs::symlink_metadata(&expected)?;
        if !metadata.file_type().is_file() || metadata.permissions().mode() & 0o111 == 0 {
            return Err("network helper is not a regular executable file".into());
        }
        if super::hash_file(&expected)? != self.receipt.helper_sha256 {
            return Err("network helper digest differs from the ownership receipt".into());
        }
        Ok(expected)
    }

    fn validate_running_helper(&self) -> Result<(), Box<dyn std::error::Error>> {
        self.validate_helper()?;
        if super::hash_file(Path::new("/proc/self/exe"))? != self.receipt.helper_sha256 {
            return Err("running network helper differs from the ownership receipt".into());
        }
        Ok(())
    }

    fn remove_helper(&self) -> Result<(), Box<dyn std::error::Error>> {
        let expected = self
            .path
            .parent()
            .ok_or("network receipt has no parent")?
            .join("network-helper.bin");
        match fs::symlink_metadata(&expected) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
            Err(error) => return Err(error.into()),
            Ok(_) => {}
        }
        self.validate_helper()?;
        fs::remove_file(&expected)?;
        File::open(expected.parent().ok_or("network helper has no parent")?)?.sync_all()?;
        Ok(())
    }

    fn namespace_path(&self) -> PathBuf {
        Path::new("/run/netns").join(&self.receipt.namespace)
    }

    fn process_path(&self) -> PathBuf {
        self.path.with_file_name("vmm-process.json")
    }

    fn write(&self) -> io::Result<()> {
        let temporary = self.path.with_extension("json.tmp");
        let mut file = File::create(&temporary)?;
        serde_json::to_writer_pretty(&mut file, &self.receipt).map_err(io::Error::other)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        fs::rename(&temporary, &self.path)?;
        File::open(
            self.path
                .parent()
                .ok_or_else(|| io::Error::other("no receipt parent"))?,
        )?
        .sync_all()
    }
}

impl Drop for NetworkLease {
    fn drop(&mut self) {
        if self.active {
            if let Err(error) = self.cleanup() {
                eprintln!(
                    "could not clean run-owned network {}; ownership receipt: {}: {error}",
                    self.receipt.namespace,
                    self.path.display()
                );
            }
        }
    }
}

fn run_ip(args: &[&str]) -> Result<String, Box<dyn std::error::Error>> {
    let output = Command::new("sudo")
        .args(["-n", "ip"])
        .args(args)
        .output()?;
    if !output.status.success() {
        return Err(format!(
            "sudo ip {} failed: {}{}",
            args.join(" "),
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )
        .into());
    }
    Ok(String::from_utf8(output.stdout)?)
}

fn namespace_pids(namespace: &str) -> Result<Vec<u32>, Box<dyn std::error::Error>> {
    run_ip(&["netns", "pids", namespace])?
        .split_whitespace()
        .map(|pid| pid.parse::<u32>().map_err(Into::into))
        .collect()
}

#[expect(
    unsafe_code,
    reason = "Linux pidfd_open returns a newly owned file descriptor"
)]
fn pidfd_open(pid: u32) -> io::Result<OwnedFd> {
    // SAFETY: syscall receives the pid as a value and flags=0; it reads no pointers.
    let raw = unsafe { libc::syscall(libc::SYS_pidfd_open, pid, 0) };
    if raw < 0 {
        return Err(io::Error::last_os_error());
    }
    let raw_fd = i32::try_from(raw).map_err(io::Error::other)?;
    // SAFETY: a successful pidfd_open returns one fresh fd, transferred to OwnedFd.
    Ok(unsafe { OwnedFd::from_raw_fd(raw_fd) })
}

#[expect(
    unsafe_code,
    reason = "Linux pidfd_send_signal binds a signal to an open pidfd"
)]
fn pidfd_send_signal(pidfd: &OwnedFd, signal: i32) -> io::Result<()> {
    // SAFETY: pidfd is live; null siginfo means the kernel supplies default signal info.
    let result = unsafe {
        libc::syscall(
            libc::SYS_pidfd_send_signal,
            pidfd.as_raw_fd(),
            signal,
            std::ptr::null::<libc::siginfo_t>(),
            0,
        )
    };
    if result < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn process_start_time(pid: u32) -> io::Result<u64> {
    let stat = fs::read_to_string(format!("/proc/{pid}/stat"))?;
    let after_name = stat
        .rsplit_once(") ")
        .ok_or_else(|| io::Error::other("process stat has no closing name"))?
        .1;
    after_name
        .split_whitespace()
        .nth(19)
        .ok_or_else(|| io::Error::other("process stat has no start time"))?
        .parse()
        .map_err(io::Error::other)
}

fn process_matches(process: &VmmProcess) -> io::Result<bool> {
    let start = match process_start_time(process.pid) {
        Ok(start) => start,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };
    let namespace = match fs::metadata(format!("/proc/{}/ns/net", process.pid)) {
        Ok(metadata) => metadata.ino(),
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };
    Ok(start == process.start_time_ticks && namespace == process.namespace_inode)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn process_identity_requires_start_time_and_namespace() {
        let pid = std::process::id();
        let process = VmmProcess {
            schema_version: 1,
            namespace: "test".to_owned(),
            namespace_inode: fs::metadata("/proc/self/ns/net").unwrap().ino(),
            pid,
            start_time_ticks: process_start_time(pid).unwrap(),
        };
        assert!(process_matches(&process).unwrap());
        let changed = VmmProcess {
            start_time_ticks: process.start_time_ticks + 1,
            ..process
        };
        assert!(!process_matches(&changed).unwrap());
    }

    #[test]
    fn stale_planned_receipt_without_namespace_is_marked_absent() {
        let output = tempfile::tempdir().unwrap();
        let token = "00000000000000000000000000000000";
        let helper = output.path().join("network-helper.bin");
        fs::copy("/proc/self/exe", &helper).unwrap();
        fs::set_permissions(&helper, fs::Permissions::from_mode(0o500)).unwrap();
        let receipt = NetworkReceipt {
            schema_version: 2,
            run_id: "test".to_owned(),
            status: "planned".to_owned(),
            namespace: format!("fcn-{token}"),
            namespace_inode: None,
            bridge: format!("fcb{}", &token[..12]),
            tap: format!("fct{}", &token[..12]),
            bridge_ipv4: BRIDGE_IPV4.to_owned(),
            guest_ipv4: GUEST_IPV4.to_owned(),
            guest_mac: GUEST_MAC.to_owned(),
            owner_uid: 0,
            owner_gid: 0,
            helper_path: helper.display().to_string(),
            helper_sha256: super::super::hash_file(&helper).unwrap(),
        };
        let path = output.path().join("network-ownership.json");
        fs::write(&path, serde_json::to_vec(&receipt).unwrap()).unwrap();
        NetworkLease::cleanup_record(&path).unwrap();
        let reconciled: NetworkReceipt = serde_json::from_slice(&fs::read(path).unwrap()).unwrap();
        assert_eq!(reconciled.status, "absent");
        assert!(!helper.exists());
    }

    #[test]
    fn pidfd_cannot_signal_a_reaped_process() {
        let mut child = Command::new("/usr/bin/sleep").arg("30").spawn().unwrap();
        let pidfd = pidfd_open(child.id()).unwrap();
        pidfd_send_signal(&pidfd, libc::SIGTERM).unwrap();
        child.wait().unwrap();
        assert_eq!(
            pidfd_send_signal(&pidfd, libc::SIGTERM)
                .unwrap_err()
                .raw_os_error(),
            Some(libc::ESRCH)
        );
    }
}
