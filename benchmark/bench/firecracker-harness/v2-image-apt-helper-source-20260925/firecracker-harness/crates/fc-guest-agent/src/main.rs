use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::os::fd::{FromRawFd, RawFd};
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const VSOCK_PORT: u32 = 10_077;
const MAX_REQUEST_BYTES: usize = 1024 * 1024;
const MAX_ARCHIVE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_WORKLOAD_TIMEOUT_SECS: u64 = 3_500;
const GUEST_PROGRAM: &str = "/root/fc-harness-payload";
const GUEST_OUTPUT: &str = "/root/fc-harness-output";
const GUEST_DATA: &str = "/root/fc-bench-data";
const GUEST_MANIFEST: &str = "/root/fc-harness-output/guest-manifest.json";

#[derive(Debug, Deserialize)]
#[serde(tag = "operation", rename_all = "snake_case")]
enum Request {
    Run {
        program: String,
        args: Vec<String>,
        env: Vec<(String, String)>,
        timeout_seconds: u64,
        manifest: ManifestContext,
    },
}

#[derive(Debug, Deserialize, Serialize)]
struct ManifestContext {
    run_id: String,
    firecracker_version: String,
    vcpu_count: usize,
    mem_size_mib: u64,
    smt_enabled: bool,
    machine_config_source: String,
    kernel_sha256: String,
    rootfs_sha256: String,
    controller_sha256: String,
    controller_size_bytes: u64,
    firecracker_sha256: String,
    firecracker_size_bytes: u64,
    guest_agent_sha256: String,
    guest_agent_size_bytes: u64,
    source_provenance: String,
    image_profile: Option<serde_json::Value>,
    supplemental_payloads: Vec<SupplementalPayload>,
    benchmark_repository: serde_json::Value,
    subject_repository: serde_json::Value,
    program_sha256: String,
    host_cpu_affinity: Vec<usize>,
    output_filesystem: String,
    guest_network: String,
    network_fixture: Option<NetworkContext>,
}

#[derive(Debug, Deserialize, Serialize)]
struct NetworkContext {
    guest_ipv4: String,
}

#[derive(Debug, Deserialize, Serialize)]
struct SupplementalPayload {
    host_path: String,
    guest_path: String,
    size_bytes: u64,
    sha256: String,
    guest_mode: String,
}

#[derive(Serialize)]
struct GuestManifest<'a> {
    #[serde(flatten)]
    context: &'a ManifestContext,
    swap_disabled: bool,
    guest_online_cpus: usize,
    guest_mem_total_bytes: u64,
    guest_swap_entries: usize,
    guest_kernel_release: &'a str,
    guest_ipv4_addresses: &'a [String],
    guest_data_directory: &'static str,
    guest_output_directory: &'static str,
}

#[derive(Debug, Serialize)]
struct GuestReport {
    kernel_release: String,
    online_cpus: usize,
    mem_total_bytes: u64,
    swap_entries: usize,
    ipv4_addresses: Vec<String>,
}

#[derive(Debug, Serialize)]
struct RunResponse {
    status: String,
    exit_code: Option<i32>,
    elapsed_ms: u128,
    guest: GuestReport,
    archive_bytes: u64,
    error: Option<String>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("fc-guest-agent: {error}");
        std::process::exit(1);
    }
}

#[expect(
    unsafe_code,
    reason = "accept returns an owned Linux vsock descriptor that File must take over"
)]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let listener = bind_vsock(VSOCK_PORT)?;
    loop {
        let fd = accept(listener.as_raw_fd())?;
        // SAFETY: accept returns a fresh owned descriptor on success.
        let mut stream = unsafe { File::from_raw_fd(fd) };
        let guest = guest_report()?;
        write_json_line(&mut stream, &guest)?;
        let request = read_request(&mut stream)?;
        let Request::Run {
            program,
            args,
            env,
            timeout_seconds,
            manifest,
        } = request;
        let result = execute_payload(&program, args, env, timeout_seconds, &manifest, guest)?;
        write_json_line(&mut stream, &result)?;
        if result.archive_bytes > 0 {
            let archive_path = Path::new(GUEST_OUTPUT).with_extension("tar.gz");
            let mut archive = File::open(archive_path)?;
            io::copy(&mut archive, &mut stream)?;
            stream.flush()?;
        }
    }
}

use std::os::fd::AsRawFd;

#[expect(
    unsafe_code,
    reason = "Linux AF_VSOCK socket, bind, listen, and close require libc FFI"
)]
fn bind_vsock(port: u32) -> io::Result<File> {
    // SAFETY: socket is called with the Linux AF_VSOCK domain and valid flags.
    let fd = unsafe { libc::socket(libc::AF_VSOCK, libc::SOCK_STREAM | libc::SOCK_CLOEXEC, 0) };
    if fd < 0 {
        return Err(io::Error::last_os_error());
    }

    let address = libc::sockaddr_vm {
        svm_family: libc::sa_family_t::try_from(libc::AF_VSOCK).map_err(io::Error::other)?,
        svm_reserved1: 0,
        svm_port: port,
        svm_cid: libc::VMADDR_CID_ANY,
        svm_zero: [0; 4],
    };
    // SAFETY: address points to a fully initialized sockaddr_vm with its exact size.
    let bind_result = unsafe {
        libc::bind(
            fd,
            (&raw const address).cast(),
            libc::socklen_t::try_from(std::mem::size_of::<libc::sockaddr_vm>())
                .map_err(io::Error::other)?,
        )
    };
    if bind_result < 0 {
        let error = io::Error::last_os_error();
        // SAFETY: fd is still owned here because File has not been constructed.
        unsafe { libc::close(fd) };
        return Err(error);
    }
    // SAFETY: listen uses the live socket descriptor returned by socket.
    if unsafe { libc::listen(fd, 8) } < 0 {
        let error = io::Error::last_os_error();
        // SAFETY: fd is still owned here because File has not been constructed.
        unsafe { libc::close(fd) };
        return Err(error);
    }
    // SAFETY: fd is a newly created socket and ownership transfers to File.
    Ok(unsafe { File::from_raw_fd(fd) })
}

#[expect(
    unsafe_code,
    reason = "accept4 is Linux socket FFI and returns a newly owned descriptor"
)]
fn accept(listener: RawFd) -> io::Result<RawFd> {
    // SAFETY: listener is a live listening socket owned by the caller.
    let fd = unsafe {
        libc::accept4(
            listener,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            libc::SOCK_CLOEXEC,
        )
    };
    if fd < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(fd)
    }
}

fn guest_report() -> io::Result<GuestReport> {
    let kernel_release = fs::read_to_string("/proc/sys/kernel/osrelease")?
        .trim()
        .to_owned();
    let online_cpus = fs::read_to_string("/sys/devices/system/cpu/online")?
        .trim()
        .split(',')
        .map(|group| {
            if let Some((start, end)) = group.split_once('-') {
                let first = start.parse::<usize>().unwrap_or(0);
                let last = end.parse::<usize>().unwrap_or(first);
                last.saturating_sub(first).saturating_add(1)
            } else {
                usize::from(!group.is_empty())
            }
        })
        .sum();
    let mem_total_kib = fs::read_to_string("/proc/meminfo")?
        .lines()
        .find_map(|line| {
            line.strip_prefix("MemTotal:")?
                .split_whitespace()
                .next()?
                .parse::<u64>()
                .ok()
        })
        .ok_or_else(|| io::Error::other("MemTotal is absent from /proc/meminfo"))?;
    let swap_entries = fs::read_to_string("/proc/swaps")?
        .lines()
        .skip(1)
        .filter(|line| !line.trim().is_empty())
        .count();
    Ok(GuestReport {
        kernel_release,
        online_cpus,
        mem_total_bytes: mem_total_kib.saturating_mul(1024),
        swap_entries,
        ipv4_addresses: ipv4_addresses()?,
    })
}

fn ipv4_addresses() -> io::Result<Vec<String>> {
    let output = Command::new("/usr/bin/ip")
        .args(["-j", "-4", "addr", "show"])
        .output()?;
    if !output.status.success() {
        return Err(io::Error::other("ip could not list guest IPv4 addresses"));
    }
    let interfaces: serde_json::Value =
        serde_json::from_slice(&output.stdout).map_err(io::Error::other)?;
    let mut addresses = Vec::new();
    for interface in interfaces.as_array().into_iter().flatten() {
        for address in interface["addr_info"].as_array().into_iter().flatten() {
            if let (Some(local), Some(prefixlen)) =
                (address["local"].as_str(), address["prefixlen"].as_u64())
            {
                addresses.push(format!("{local}/{prefixlen}"));
            }
        }
    }
    Ok(addresses)
}

fn read_request(reader: &mut impl Read) -> Result<Request, Box<dyn std::error::Error>> {
    let mut bytes = Vec::new();
    let mut byte = [0_u8; 1];
    while bytes.len() < MAX_REQUEST_BYTES {
        if reader.read(&mut byte)? == 0 {
            return Err("host closed the vsock request before sending a newline".into());
        }
        if byte[0] == b'\n' {
            return Ok(serde_json::from_slice(&bytes)?);
        }
        bytes.push(byte[0]);
    }
    Err("vsock request exceeded the 1 MiB limit".into())
}

#[expect(
    clippy::too_many_lines,
    reason = "guest validation, execution, and archival share one ordered failure scope"
)]
fn execute_payload(
    program: &str,
    args: Vec<String>,
    env: Vec<(String, String)>,
    timeout_seconds: u64,
    manifest: &ManifestContext,
    mut guest: GuestReport,
) -> Result<RunResponse, Box<dyn std::error::Error>> {
    if !(1..=MAX_WORKLOAD_TIMEOUT_SECS).contains(&timeout_seconds) {
        return Err(format!(
            "workload timeout must be between 1 and {MAX_WORKLOAD_TIMEOUT_SECS} seconds"
        )
        .into());
    }
    let output_dir = PathBuf::from(GUEST_OUTPUT);
    fs::create_dir_all(&output_dir)?;
    fs::create_dir_all(GUEST_DATA)?;
    if guest.online_cpus != 4
        || guest.mem_total_bytes < 7 * 1024 * 1024 * 1024
        || guest.swap_entries != 0
    {
        return Ok(RunResponse {
            status: "guest_configuration_mismatch".to_owned(),
            exit_code: None,
            elapsed_ms: 0,
            guest,
            archive_bytes: 0,
            error: Some("guest must expose 4 CPUs, at least 7 GiB and no swap".to_owned()),
        });
    }
    let permitted_program =
        program == GUEST_PROGRAM || program == "/usr/bin/uname" || program == "/usr/bin/false";
    if !permitted_program || !Path::new(&program).is_file() {
        return Ok(RunResponse {
            status: "payload_missing".to_owned(),
            exit_code: None,
            elapsed_ms: 0,
            guest,
            archive_bytes: 0,
            error: Some(format!(
                "guest program is absent or outside the runner allowlist: {program}"
            )),
        });
    }

    if let Some(network) = &manifest.network_fixture {
        let started = Instant::now();
        while !guest.ipv4_addresses.contains(&network.guest_ipv4)
            && started.elapsed() < Duration::from_secs(20)
        {
            thread::sleep(Duration::from_millis(100));
            guest = guest_report()?;
        }
        if !guest.ipv4_addresses.contains(&network.guest_ipv4) {
            return Ok(RunResponse {
                status: "guest_network_mismatch".to_owned(),
                exit_code: None,
                elapsed_ms: 0,
                guest,
                archive_bytes: 0,
                error: Some(format!(
                    "guest did not configure expected IPv4 address {}",
                    network.guest_ipv4
                )),
            });
        }
    }
    write_guest_manifest(manifest, &guest)?;
    for payload in &manifest.supplemental_payloads {
        let path = Path::new(&payload.guest_path);
        let metadata = fs::symlink_metadata(path)?;
        if !metadata.file_type().is_file()
            || metadata.len() != payload.size_bytes
            || payload.guest_mode != "0755"
            || metadata.permissions().mode() & 0o777 != 0o755
        {
            return Err(
                format!("supplemental payload metadata mismatch: {}", path.display()).into(),
            );
        }
        if hash_file(path)? != payload.sha256 {
            return Err(
                format!("supplemental payload SHA-256 mismatch: {}", path.display()).into(),
            );
        }
    }

    let stdout = File::create(output_dir.join("stdout.log"))?;
    let stderr = File::create(output_dir.join("stderr.log"))?;
    let started = Instant::now();
    let mut command = Command::new(program);
    command
        .args(args)
        .env_clear()
        .env(
            "PATH",
            "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        )
        .env("HOME", "/root")
        .env("TMPDIR", "/tmp")
        .env("TERRABASE_BENCH_OUTPUT_DIR", output_dir.as_os_str())
        .stdin(Stdio::null())
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr));
    for (key, value) in env {
        if !key
            .bytes()
            .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || byte == b'_')
        {
            return Err(format!("invalid environment variable name: {key:?}").into());
        }
        command.env(key, value);
    }
    if program == GUEST_PROGRAM {
        let path = Path::new(GUEST_PROGRAM);
        let metadata = fs::symlink_metadata(path)?;
        if !metadata.file_type().is_file()
            || metadata.permissions().mode() & 0o777 != 0o755
            || hash_file(path)? != manifest.program_sha256
        {
            return Err(
                "primary guest payload differs from the recorded executable hash or mode".into(),
            );
        }
    }
    // Stdio goes to files, so a stalled payload cannot block on an unread pipe.
    let mut child = command.spawn()?;
    let (status, timed_out) =
        wait_for_payload(&mut child, started, Duration::from_secs(timeout_seconds))?;
    let elapsed_ms = started.elapsed().as_millis();
    let exit_code = if timed_out { None } else { status.code() };
    let result_status = if timed_out {
        "timed_out"
    } else if status.success() {
        "passed"
    } else {
        "program_failed"
    };
    let result_error = timed_out
        .then(|| format!("guest workload exceeded its {timeout_seconds}s wall-clock deadline"));
    let Some(archive_bytes) = archive_output(&output_dir)? else {
        return Ok(RunResponse {
            status: "guest_output_too_large".to_owned(),
            exit_code,
            elapsed_ms,
            guest,
            archive_bytes: 0,
            error: Some(format!(
                "guest output archive exceeded {MAX_ARCHIVE_BYTES} bytes"
            )),
        });
    };
    Ok(RunResponse {
        status: result_status.to_owned(),
        exit_code,
        elapsed_ms,
        guest,
        archive_bytes,
        error: result_error,
    })
}

fn hash_file(path: &Path) -> io::Result<String> {
    let mut hash = Sha256::new();
    let mut file = File::open(path)?;
    let mut buffer = vec![0_u8; 64 * 1024];
    loop {
        let size = file.read(&mut buffer)?;
        if size == 0 {
            break;
        }
        hash.update(&buffer[..size]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

fn write_guest_manifest(manifest: &ManifestContext, guest: &GuestReport) -> io::Result<()> {
    let guest_manifest = GuestManifest {
        context: manifest,
        swap_disabled: guest.swap_entries == 0,
        guest_online_cpus: guest.online_cpus,
        guest_mem_total_bytes: guest.mem_total_bytes,
        guest_swap_entries: guest.swap_entries,
        guest_kernel_release: &guest.kernel_release,
        guest_ipv4_addresses: &guest.ipv4_addresses,
        guest_data_directory: GUEST_DATA,
        guest_output_directory: GUEST_OUTPUT,
    };
    let mut file = File::create_new(GUEST_MANIFEST)?;
    serde_json::to_writer_pretty(&mut file, &guest_manifest)?;
    file.write_all(b"\n")?;
    file.sync_all()
}

fn wait_for_payload(
    child: &mut Child,
    started: Instant,
    timeout: Duration,
) -> io::Result<(ExitStatus, bool)> {
    loop {
        if let Some(status) = child.try_wait()? {
            return Ok((status, false));
        }
        if started.elapsed() >= timeout {
            if let Err(error) = child.kill() {
                if error.kind() != io::ErrorKind::InvalidInput
                    && error.raw_os_error() != Some(libc::ESRCH)
                {
                    return Err(error);
                }
            }
            return Ok((child.wait()?, true));
        }
        thread::sleep(Duration::from_millis(50));
    }
}

fn archive_output(output_dir: &Path) -> Result<Option<u64>, Box<dyn std::error::Error>> {
    let archive_path = output_dir.with_extension("tar.gz");
    let archive_status = Command::new("tar")
        .args(["-czf"])
        .arg(&archive_path)
        .args(["-C"])
        .arg(output_dir)
        .arg(".")
        .status()?;
    if !archive_status.success() {
        return Err(format!("tar failed with status {archive_status}").into());
    }
    let archive_bytes = fs::metadata(&archive_path)?.len();
    if archive_bytes > MAX_ARCHIVE_BYTES {
        fs::remove_file(&archive_path)?;
        Ok(None)
    } else {
        Ok(Some(archive_bytes))
    }
}

fn write_json_line(stream: &mut impl Write, value: &impl Serialize) -> io::Result<()> {
    serde_json::to_writer(&mut *stream, value).map_err(io::Error::other)?;
    stream.write_all(b"\n")?;
    stream.flush()
}
