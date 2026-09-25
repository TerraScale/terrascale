//! Separate two-role network owner for the `RustFS` fixture. The historical
//! single-VM `NetworkLease` receipt and launcher are intentionally unchanged.

use std::ffi::OsStr;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::os::unix::fs::{FileTypeExt, MetadataExt, PermissionsExt};
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::network::{namespace_pids, pidfd_open, pidfd_send_signal, process_start_time, run_ip};

const BRIDGE_IPV4: &str = "10.78.0.1/24";

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct PairRole {
    pub name: String,
    pub tap: String,
    pub ipv4: String,
    pub mac: String,
    pub vsock_cid: u32,
    pub host_cpus: [usize; 4],
    pub rootfs: String,
    pub api_socket: String,
    pub vsock_socket: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct PairReceipt {
    pub schema_version: u32,
    pub run_id: String,
    pub status: String,
    pub namespace: String,
    pub namespace_inode: Option<u64>,
    pub bridge: String,
    pub bridge_ipv4: String,
    pub service: PairRole,
    pub client: PairRole,
    pub owner_uid: u32,
    pub owner_gid: u32,
    pub helper_path: String,
    pub helper_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct PairProcess {
    schema_version: u32,
    role: String,
    namespace: String,
    namespace_inode: u64,
    pid: u32,
    start_time_ticks: u64,
}

pub struct PairNetwork {
    receipt: PairReceipt,
    path: PathBuf,
    active: bool,
}

impl PairNetwork {
    #[allow(
        clippy::too_many_arguments,
        reason = "explicit independent per-role endpoints prevent service/client resource swaps"
    )]
    pub fn create(
        output: &Path,
        run_id: &str,
        helper_sha256: &str,
        service_cpus: [usize; 4],
        client_cpus: [usize; 4],
        service_rootfs: &Path,
        client_rootfs: &Path,
        service_api: &Path,
        client_api: &Path,
        service_vsock: &Path,
        client_vsock: &Path,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        if service_cpus.iter().any(|cpu| client_cpus.contains(cpu)) {
            return Err("pair host CPU sets overlap".into());
        }
        let output = fs::canonicalize(output)?;
        let mut random = [0_u8; 16];
        File::open("/dev/urandom")?.read_exact(&mut random)?;
        let mut token = String::with_capacity(32);
        for byte in random {
            write!(&mut token, "{byte:02x}")?;
        }
        let short = &token[..12];
        let owner = fs::metadata(&output)?;
        let role = |name: &str,
                    tap: String,
                    ipv4: &str,
                    mac: &str,
                    cid: u32,
                    cpus,
                    rootfs: &Path,
                    api: &Path,
                    vsock: &Path| PairRole {
            name: name.to_owned(),
            tap,
            ipv4: ipv4.to_owned(),
            mac: mac.to_owned(),
            vsock_cid: cid,
            host_cpus: cpus,
            rootfs: rootfs.display().to_string(),
            api_socket: api.display().to_string(),
            vsock_socket: vsock.display().to_string(),
        };
        let receipt = PairReceipt {
            schema_version: 1,
            run_id: run_id.to_owned(),
            status: "planned".to_owned(),
            namespace: format!("fcp-{token}"),
            namespace_inode: None,
            bridge: format!("fpb{short}"),
            bridge_ipv4: BRIDGE_IPV4.to_owned(),
            service: role(
                "service",
                format!("fps{short}"),
                "10.78.0.2/24",
                "06:00:0a:4e:00:02",
                3,
                service_cpus,
                service_rootfs,
                service_api,
                service_vsock,
            ),
            client: role(
                "client",
                format!("fpc{short}"),
                "10.78.0.3/24",
                "06:00:0a:4e:00:03",
                4,
                client_cpus,
                client_rootfs,
                client_api,
                client_vsock,
            ),
            owner_uid: owner.uid(),
            owner_gid: owner.gid(),
            helper_path: output.join("pair-network-helper.bin").display().to_string(),
            helper_sha256: helper_sha256.to_owned(),
        };
        let mut pair = Self {
            receipt,
            path: output.join("pair-network-ownership.json"),
            active: false,
        };
        pair.validate_helper()?;
        pair.write()?;
        run_ip(&["netns", "add", &pair.receipt.namespace])?;
        pair.active = true;
        pair.receipt.namespace_inode = Some(fs::metadata(pair.namespace_path())?.ino());
        "allocated".clone_into(&mut pair.receipt.status);
        pair.write()?;
        pair.configure()?;
        "ready".clone_into(&mut pair.receipt.status);
        pair.write()?;
        Ok(pair)
    }

    pub fn receipt(&self) -> &PairReceipt {
        &self.receipt
    }

    pub fn role(&self, name: &str) -> Result<&PairRole, Box<dyn std::error::Error>> {
        match name {
            "service" => Ok(&self.receipt.service),
            "client" => Ok(&self.receipt.client),
            _ => Err("unknown pair role".into()),
        }
    }

    fn configure(&self) -> Result<(), Box<dyn std::error::Error>> {
        let namespace = &self.receipt.namespace;
        let bridge = &self.receipt.bridge;
        run_ip(&["-n", namespace, "link", "add", bridge, "type", "bridge"])?;
        run_ip(&["-n", namespace, "addr", "add", BRIDGE_IPV4, "dev", bridge])?;
        run_ip(&["-n", namespace, "link", "set", bridge, "up"])?;
        let uid = self.receipt.owner_uid.to_string();
        for role in [&self.receipt.service, &self.receipt.client] {
            run_ip(&[
                "netns", "exec", namespace, "ip", "tuntap", "add", "dev", &role.tap, "mode", "tap",
                "user", &uid,
            ])?;
            run_ip(&["-n", namespace, "link", "set", &role.tap, "master", bridge])?;
            run_ip(&["-n", namespace, "link", "set", &role.tap, "up"])?;
        }
        Ok(())
    }

    pub fn guest_network_file(&self, name: &str) -> Result<String, Box<dyn std::error::Error>> {
        let role = self.role(name)?;
        Ok(format!(
            "[Match]\nMACAddress={}\n\n[Network]\nAddress={}\n",
            role.mac, role.ipv4
        ))
    }

    pub fn firecracker_command(
        &self,
        name: &str,
        executable: &Path,
    ) -> Result<Command, Box<dyn std::error::Error>> {
        let helper = self.validate_helper()?;
        let role = self.role(name)?;
        let affinity = role.host_cpus.map(|cpu| cpu.to_string()).join(",");
        let mut command = Command::new("sudo");
        command
            .args(["-n", "ip", "netns", "exec", &self.receipt.namespace])
            .arg(helper)
            .arg("--internal-pair-vmm-launch")
            .arg(&self.path)
            .arg(name)
            .arg(affinity)
            .arg(executable)
            .arg("--");
        Ok(command)
    }

    pub fn launch_vmm(args: &[String]) -> Result<(), Box<dyn std::error::Error>> {
        if args.len() < 5 || args[4] != "--" {
            return Err("invalid internal pair VMM launcher arguments".into());
        }
        let path = Path::new(&args[0]);
        let pair = Self::read(path)?;
        pair.validate_names()?;
        pair.validate_running_helper()?;
        if pair.receipt.status != "ready" {
            return Err("pair network is not ready".into());
        }
        let role = pair.role(&args[1])?;
        if args[2] != role.host_cpus.map(|cpu| cpu.to_string()).join(",") {
            return Err("pair VMM CPU affinity differs from receipt".into());
        }
        let inode = pair.check_namespace_inode()?;
        if fs::metadata("/proc/self/ns/net")?.ino() != inode {
            return Err("pair launcher is outside its recorded network namespace".into());
        }
        let pid = std::process::id();
        let process = PairProcess {
            schema_version: 1,
            role: args[1].clone(),
            namespace: pair.receipt.namespace.clone(),
            namespace_inode: inode,
            pid,
            start_time_ticks: process_start_time(pid)?,
        };
        let process_path = pair.process_path(&args[1]);
        let mut file = File::create_new(&process_path)?;
        serde_json::to_writer_pretty(&mut file, &process)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        File::open(path.parent().ok_or("pair receipt has no parent")?)?.sync_all()?;
        let error = Command::new("setpriv")
            .args(["--reuid", &pair.receipt.owner_uid.to_string()])
            .args(["--regid", &pair.receipt.owner_gid.to_string()])
            .args([
                "--init-groups",
                "--pdeathsig",
                "SIGKILL",
                "--",
                "taskset",
                "-c",
            ])
            .arg(&args[2])
            .arg(&args[3])
            .args(&args[5..])
            .exec();
        Err(error.into())
    }

    pub fn signal_vmm_record(
        path: &Path,
        name: &str,
        signal: &str,
    ) -> Result<(), Box<dyn std::error::Error>> {
        let signal = match signal {
            "TERM" => libc::SIGTERM,
            "KILL" => libc::SIGKILL,
            _ => return Err("unsupported pair VMM signal".into()),
        };
        let pair = Self::read(path)?;
        pair.validate_names()?;
        pair.validate_running_helper()?;
        let inode = pair.check_namespace_inode()?;
        let owned = pair.owned_processes(inode)?;
        let pids = namespace_pids(&pair.receipt.namespace)?;
        if pids
            .iter()
            .any(|pid| !owned.iter().any(|process| process.pid == *pid))
        {
            return Err("pair namespace contains an unowned process; refusing signal".into());
        }
        if owned
            .iter()
            .filter(|process| pids.contains(&process.pid))
            .any(|process| !process_matches(process).unwrap_or(false))
        {
            return Err(
                "pair namespace contains a PID with changed identity; refusing signal".into(),
            );
        }
        let process = owned
            .iter()
            .find(|process| process.role == name)
            .ok_or("pair role has no process receipt")?;
        if !pids.contains(&process.pid) {
            return Ok(());
        }
        let pidfd = pidfd_open(process.pid)?;
        if !process_matches(process)? {
            return Err("pair VMM PID identity changed; refusing signal".into());
        }
        pidfd_send_signal(&pidfd, signal)?;
        Ok(())
    }

    pub fn stop_processes(&self) -> Result<(), Box<dyn std::error::Error>> {
        if !self.active {
            return Ok(());
        }
        if !self.namespace_path().exists() {
            return Ok(());
        }
        let Some(inode) = self.receipt.namespace_inode else {
            if !self.namespace_path().exists() {
                return Ok(());
            }
            if namespace_pids(&self.receipt.namespace)?.is_empty() {
                return Ok(());
            }
            return Err("planned pair namespace contains unowned processes".into());
        };
        self.check_namespace_inode()?;
        let owned = self.owned_processes(inode)?;
        let pids = namespace_pids(&self.receipt.namespace)?;
        if pids
            .iter()
            .any(|pid| !owned.iter().any(|process| process.pid == *pid))
        {
            return Err("pair namespace contains unowned processes; refusing cleanup".into());
        }
        for process in owned.iter().filter(|process| pids.contains(&process.pid)) {
            if !process_matches(process)? {
                return Err(
                    "pair namespace contains a PID with changed identity; refusing cleanup".into(),
                );
            }
        }
        for name in ["client", "service"] {
            if owned
                .iter()
                .any(|process| process.role == name && pids.contains(&process.pid))
            {
                self.signal_vmm(name, "TERM")?;
            }
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            if namespace_pids(&self.receipt.namespace)?.is_empty() {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(50));
        }
        let remaining = namespace_pids(&self.receipt.namespace)?;
        if remaining
            .iter()
            .any(|pid| !owned.iter().any(|process| process.pid == *pid))
        {
            return Err("pair namespace acquired an unowned process; refusing SIGKILL".into());
        }
        for name in ["client", "service"] {
            if owned
                .iter()
                .any(|process| process.role == name && remaining.contains(&process.pid))
            {
                self.signal_vmm(name, "KILL")?;
            }
        }
        if namespace_pids(&self.receipt.namespace)?.is_empty() {
            Ok(())
        } else {
            Err("pair namespace still has processes after SIGKILL".into())
        }
    }

    fn signal_vmm(&self, name: &str, signal: &str) -> Result<(), Box<dyn std::error::Error>> {
        let helper = self.validate_helper()?;
        let output = Command::new("sudo")
            .args(["-n"])
            .arg(helper)
            .arg("--internal-pair-signal-vmm")
            .arg(&self.path)
            .arg(name)
            .arg(signal)
            .output()?;
        if !output.status.success() {
            return Err(format!(
                "could not signal run-owned pair VMM: {}",
                String::from_utf8_lossy(&output.stderr)
            )
            .into());
        }
        Ok(())
    }

    pub fn cleanup(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        if !self.active {
            return Ok(());
        }
        self.validate_names()?;
        if !self.namespace_path().exists() {
            "absent".clone_into(&mut self.receipt.status);
            self.active = false;
            self.write()?;
            return self.remove_helper();
        }
        let before = fs::metadata(self.namespace_path())?.ino();
        if self.receipt.namespace_inode.is_some() {
            self.check_namespace_inode()?;
        } else {
            if self.receipt.status != "planned" {
                return Err("pair namespace has no recorded inode".into());
            }
            let links: serde_json::Value = serde_json::from_str(&run_ip(&[
                "-n",
                &self.receipt.namespace,
                "-j",
                "link",
                "show",
            ])?)?;
            if !links
                .as_array()
                .is_some_and(|links| links.len() == 1 && links[0]["ifname"] == "lo")
            {
                return Err("planned pair namespace is not empty".into());
            }
        }
        if !namespace_pids(&self.receipt.namespace)?.is_empty() {
            return Err("pair namespace still has processes".into());
        }
        if fs::metadata(self.namespace_path())?.ino() != before {
            return Err("pair namespace inode changed during cleanup".into());
        }
        run_ip(&["netns", "delete", &self.receipt.namespace])?;
        self.active = false;
        "cleaned".clone_into(&mut self.receipt.status);
        self.write()?;
        self.remove_helper()
    }

    pub fn cleanup_record(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
        let mut pair = Self::read(path)?;
        pair.validate_names()?;
        pair.active = pair.receipt.status != "cleaned" && pair.receipt.status != "absent";
        pair.stop_processes()?;
        pair.cleanup()?;
        pair.remove_helper()?;
        pair.cleanup_staged_resources()
    }

    fn read(path: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        let path = fs::canonicalize(path)?;
        let receipt = serde_json::from_slice(&fs::read(&path)?)?;
        Ok(Self {
            receipt,
            path,
            active: false,
        })
    }

    fn owned_processes(&self, inode: u64) -> Result<Vec<PairProcess>, Box<dyn std::error::Error>> {
        let mut processes = Vec::new();
        for name in ["service", "client"] {
            let path = self.process_path(name);
            let bytes = match fs::read(path) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error.into()),
            };
            let process: PairProcess = serde_json::from_slice(&bytes)?;
            if process.schema_version != 1
                || process.role != name
                || process.namespace != self.receipt.namespace
                || process.namespace_inode != inode
            {
                return Err("pair VMM process receipt differs from network owner".into());
            }
            processes.push(process);
        }
        Ok(processes)
    }

    fn check_namespace_inode(&self) -> Result<u64, Box<dyn std::error::Error>> {
        let inode = self
            .receipt
            .namespace_inode
            .ok_or("pair namespace inode is absent")?;
        if fs::metadata(self.namespace_path())?.ino() != inode {
            return Err("pair namespace inode changed".into());
        }
        if fs::metadata("/proc/self/ns/net")?.ino() == inode && self.receipt.status != "ready" {
            return Err("pair helper entered a namespace with non-ready receipt".into());
        }
        Ok(inode)
    }

    fn validate_names(&self) -> Result<(), Box<dyn std::error::Error>> {
        if self.receipt.schema_version != 1 {
            return Err("unsupported pair ownership version".into());
        }
        let token = self
            .receipt
            .namespace
            .strip_prefix("fcp-")
            .ok_or("unexpected pair namespace prefix")?;
        if token.len() != 32 || !token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("invalid pair namespace token".into());
        }
        let short = &token[..12];
        if self.receipt.bridge != format!("fpb{short}")
            || self.receipt.service.tap != format!("fps{short}")
            || self.receipt.client.tap != format!("fpc{short}")
            || self.receipt.bridge_ipv4 != BRIDGE_IPV4
            || self.receipt.service.name != "service"
            || self.receipt.client.name != "client"
            || self.receipt.service.ipv4 != "10.78.0.2/24"
            || self.receipt.client.ipv4 != "10.78.0.3/24"
            || self.receipt.service.mac != "06:00:0a:4e:00:02"
            || self.receipt.client.mac != "06:00:0a:4e:00:03"
            || self.receipt.service.vsock_cid != 3
            || self.receipt.client.vsock_cid != 4
        {
            return Err("pair network names or role addresses differ from fixed topology".into());
        }
        if self
            .receipt
            .service
            .host_cpus
            .iter()
            .any(|cpu| self.receipt.client.host_cpus.contains(cpu))
        {
            return Err("pair host CPU affinities overlap".into());
        }
        for role in [&self.receipt.service, &self.receipt.client] {
            if role
                .host_cpus
                .iter()
                .enumerate()
                .any(|(index, cpu)| role.host_cpus[index + 1..].contains(cpu))
            {
                return Err("pair role repeats a host CPU".into());
            }
        }
        let output = self.path.parent().ok_or("pair receipt has no parent")?;
        for role in [&self.receipt.service, &self.receipt.client] {
            if role.rootfs
                != output
                    .join(format!("{}-rootfs.ext4", role.name))
                    .display()
                    .to_string()
            {
                return Err("pair rootfs path differs from owned output".into());
            }
        }
        let service_api = Path::new(&self.receipt.service.api_socket);
        let client_api = Path::new(&self.receipt.client.api_socket);
        let service_vsock = Path::new(&self.receipt.service.vsock_socket);
        let client_vsock = Path::new(&self.receipt.client.vsock_socket);
        let socket_dir = service_api
            .parent()
            .ok_or("pair service API socket has no parent")?;
        if !socket_dir.starts_with("/tmp")
            || !socket_dir
                .file_name()
                .is_some_and(|name| name.to_string_lossy().starts_with("fcp-"))
            || [client_api, service_vsock, client_vsock]
                .iter()
                .any(|path| path.parent() != Some(socket_dir))
            || service_api
                .file_name()
                .is_none_or(|name| name != OsStr::new("service-api.sock"))
            || client_api
                .file_name()
                .is_none_or(|name| name != OsStr::new("client-api.sock"))
            || service_vsock
                .file_name()
                .is_none_or(|name| name != OsStr::new("service-vsock.sock"))
            || client_vsock
                .file_name()
                .is_none_or(|name| name != OsStr::new("client-vsock.sock"))
        {
            return Err("pair socket paths differ from fixed private directory layout".into());
        }
        Ok(())
    }

    pub fn cleanup_staged_resources(&self) -> Result<(), Box<dyn std::error::Error>> {
        self.validate_names()?;
        for role in [&self.receipt.service, &self.receipt.client] {
            let rootfs = Path::new(&role.rootfs);
            match fs::symlink_metadata(rootfs) {
                Ok(metadata) if metadata.file_type().is_file() => fs::remove_file(rootfs)?,
                Ok(_) => return Err("owned pair rootfs path is no longer a regular file".into()),
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error.into()),
            }
            for path in [&role.api_socket, &role.vsock_socket] {
                let path = Path::new(path);
                match fs::symlink_metadata(path) {
                    Ok(metadata) if metadata.file_type().is_socket() => fs::remove_file(path)?,
                    Ok(_) => return Err("owned pair socket path is no longer a Unix socket".into()),
                    Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                    Err(error) => return Err(error.into()),
                }
            }
        }
        let socket_dir = Path::new(&self.receipt.service.api_socket)
            .parent()
            .ok_or("pair socket directory missing")?;
        match fs::remove_dir(socket_dir) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error.into()),
        }
    }

    fn validate_helper(&self) -> Result<PathBuf, Box<dyn std::error::Error>> {
        let expected = self
            .path
            .parent()
            .ok_or("pair receipt has no parent")?
            .join("pair-network-helper.bin");
        if self.receipt.helper_path != expected.display().to_string()
            || self.receipt.helper_sha256.len() != 64
        {
            return Err("pair helper path or hash invalid".into());
        }
        let metadata = fs::symlink_metadata(&expected)?;
        if !metadata.file_type().is_file() || metadata.permissions().mode() & 0o111 == 0 {
            return Err("pair helper is not executable regular file".into());
        }
        if crate::hash_file(&expected)? != self.receipt.helper_sha256 {
            return Err("pair helper digest differs from receipt".into());
        }
        Ok(expected)
    }

    fn validate_running_helper(&self) -> Result<(), Box<dyn std::error::Error>> {
        self.validate_helper()?;
        if crate::hash_file(Path::new("/proc/self/exe"))? != self.receipt.helper_sha256 {
            return Err("running pair helper digest differs from receipt".into());
        }
        Ok(())
    }

    fn remove_helper(&self) -> Result<(), Box<dyn std::error::Error>> {
        let expected = self
            .path
            .parent()
            .ok_or("pair receipt has no parent")?
            .join("pair-network-helper.bin");
        if !expected.exists() {
            return Ok(());
        }
        self.validate_helper()?;
        fs::remove_file(&expected)?;
        File::open(expected.parent().ok_or("pair helper has no parent")?)?.sync_all()?;
        Ok(())
    }

    fn namespace_path(&self) -> PathBuf {
        Path::new("/run/netns").join(&self.receipt.namespace)
    }
    fn process_path(&self, name: &str) -> PathBuf {
        self.path.with_file_name(format!("pair-vmm-{name}.json"))
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
                .ok_or_else(|| io::Error::other("pair receipt has no parent"))?,
        )?
        .sync_all()
    }
}

impl Drop for PairNetwork {
    fn drop(&mut self) {
        if self.active {
            if let Err(error) = self
                .stop_processes()
                .and_then(|()| self.cleanup())
                .and_then(|()| self.cleanup_staged_resources())
            {
                eprintln!(
                    "could not clean run-owned pair network; ownership receipt: {}: {error}",
                    self.path.display()
                );
            }
        }
    }
}

fn process_matches(process: &PairProcess) -> io::Result<bool> {
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
