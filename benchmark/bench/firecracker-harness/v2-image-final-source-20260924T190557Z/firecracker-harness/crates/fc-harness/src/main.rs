use std::collections::BTreeMap;
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::fd::AsRawFd;
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::os::unix::net::UnixStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

mod image_profile;
mod network;
use image_profile::ImageProfile;
use network::{NetworkLease, NetworkReceipt};

const PINS: &str = include_str!("../../../pins.toml");
const GUEST_VSOCK_PORT: u32 = 10_077;
const INSTANCE_MEMORY_MIB: u64 = 8_192;
const INSTANCE_VCPU_COUNT: usize = 4;
const GUEST_AGENT_PATH: &str = "/root/fc-harness-agent";
const GUEST_PAYLOAD_PATH: &str = "/root/fc-harness-payload";
const GUEST_NETWORK_UNIT: &str = "/etc/systemd/network/10-fc-harness.network";
const GUEST_AGENT_UNIT: &str = "/etc/systemd/system/fc-harness-agent.service";
const VSOCK_CONNECT_TIMEOUT: Duration = Duration::from_secs(120);
const RUN_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const DEFAULT_WORKLOAD_TIMEOUT_SECS: u64 = 1_800;
const MAX_WORKLOAD_TIMEOUT_SECS: u64 = 3_500;
const MAX_PRIMARY_PAYLOAD_BYTES: u64 = 128 * 1024 * 1024;
const MAX_SUPPLEMENTAL_PAYLOAD_BYTES: u64 = 128 * 1024 * 1024;
const MAX_GUEST_AGENT_BYTES: u64 = 32 * 1024 * 1024;
const MAX_CONTROLLER_BYTES: u64 = 128 * 1024 * 1024;
const MAX_FIRECRACKER_BYTES: u64 = 128 * 1024 * 1024;
const MAX_KERNEL_BYTES: u64 = 512 * 1024 * 1024;
const MAX_GUEST_ARCHIVE_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Debug, Deserialize, Serialize)]
#[expect(
    clippy::struct_excessive_bools,
    reason = "receipt records independent VM configuration facts"
)]
struct RunManifest {
    schema_version: u32,
    run_id: String,
    status: String,
    started_unix_ms: u128,
    finished_unix_ms: Option<u128>,
    purpose: String,
    source_provenance: String,
    image_profile: Option<ImageProfile>,
    benchmark_repository: RepositoryIdentity,
    subject_repository: RepositoryIdentity,
    program_path: String,
    program_sha256: String,
    guest_agent_path: String,
    guest_agent_sha256: String,
    guest_agent_size_bytes: u64,
    controller_executable: String,
    controller_sha256: String,
    controller_size_bytes: u64,
    supplemental_payloads: Vec<SupplementalPayload>,
    kernel_path: String,
    kernel_launch_path: String,
    kernel_sha256: String,
    kernel_size_bytes: u64,
    kernel_release: String,
    rootfs_path: String,
    rootfs_sha256: String,
    preboot_rootfs_sha256: Option<String>,
    rootfs_release: String,
    rootfs_source_manifest: String,
    firecracker_version: String,
    firecracker_executable: String,
    firecracker_launch_path: String,
    firecracker_sha256: String,
    firecracker_size_bytes: u64,
    kvm_api_version: i32,
    guest_vcpu_count: usize,
    guest_mem_size_mib: u64,
    guest_smt_enabled: bool,
    guest_dynamic_memory: bool,
    guest_swap_disabled: bool,
    host_cpu_model: String,
    host_cpu_affinity: Vec<usize>,
    host_available_memory_bytes: u64,
    host_swap_active: bool,
    output_filesystem: String,
    guest_network: String,
    network_fixture: Option<NetworkReceipt>,
    guest_report: Option<GuestReport>,
    guest_exit_code: Option<i32>,
    guest_elapsed_ms: Option<u128>,
    workload_timeout_seconds: u64,
    elapsed_ms: Option<u128>,
    error: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct GuestReport {
    kernel_release: String,
    online_cpus: usize,
    mem_total_bytes: u64,
    swap_entries: usize,
    ipv4_addresses: Vec<String>,
}

#[derive(Debug, Deserialize)]
struct GuestRunResponse {
    status: String,
    exit_code: Option<i32>,
    elapsed_ms: u128,
    guest: GuestReport,
    archive_bytes: u64,
    error: Option<String>,
}

struct RunArgs {
    benchmark_repo: PathBuf,
    subject_repo: PathBuf,
    program: PathBuf,
    run_id: String,
    output: Option<PathBuf>,
    kernel: Option<PathBuf>,
    rootfs: Option<PathBuf>,
    guest_args: Vec<String>,
    guest_program: Option<String>,
    timeout_seconds: u64,
    isolated_network: bool,
    image_profile: Option<PathBuf>,
    payload_files: Vec<(PathBuf, String)>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct SupplementalPayload {
    host_path: String,
    guest_path: String,
    size_bytes: u64,
    sha256: String,
    guest_mode: String,
}

struct ProgramSnapshot {
    staged: FrozenPath,
    sha256: String,
}

struct AgentSnapshot {
    staged: FrozenPath,
    sha256: String,
    size_bytes: u64,
    source_path: PathBuf,
}

struct HostArtifactSnapshot {
    staged: FrozenPath,
    sha256: String,
    size_bytes: u64,
    source_path: PathBuf,
}

impl HostArtifactSnapshot {
    fn controller() -> Result<Self, Box<dyn std::error::Error>> {
        let source_path = env::current_exe()?;
        // /proc/self/exe names the inode executing this process even if a
        // concurrent build replaces the original pathname.
        let source = File::open("/proc/self/exe")?;
        let (staged, sha256, size_bytes) =
            snapshot_opened_file(source, MAX_CONTROLLER_BYTES, true, 0o500)?;
        Ok(Self {
            staged,
            sha256,
            size_bytes,
            source_path,
        })
    }

    fn firecracker() -> Result<Self, Box<dyn std::error::Error>> {
        let source_path = find_executable("firecracker")?;
        let resolved = fs::canonicalize(&source_path)?;
        let (staged, sha256, size_bytes) =
            snapshot_file(&resolved, MAX_FIRECRACKER_BYTES, true, 0o500)?;
        Ok(Self {
            staged,
            sha256,
            size_bytes,
            source_path,
        })
    }

    fn kernel(path: &Path, expected_sha: &str) -> Result<Self, Box<dyn std::error::Error>> {
        let (staged, sha256, size_bytes) = snapshot_file(path, MAX_KERNEL_BYTES, false, 0o400)?;
        if sha256 != expected_sha {
            return Err("snapshotted kernel differs from the pinned SHA-256".into());
        }
        Ok(Self {
            staged,
            sha256,
            size_bytes,
            source_path: path.to_path_buf(),
        })
    }
}

impl AgentSnapshot {
    fn create() -> Result<Self, Box<dyn std::error::Error>> {
        let source_path = guest_agent_path()?;
        let (staged, sha256, size_bytes) =
            snapshot_executable(&source_path, MAX_GUEST_AGENT_BYTES)?;
        let mut file = File::open(staged.path())?;
        let mut prefix = [0_u8; 4];
        file.read_exact(&mut prefix)?;
        if prefix != *b"\x7fELF" {
            return Err("snapshotted guest agent is not an ELF executable".into());
        }
        Ok(Self {
            staged,
            sha256,
            size_bytes,
            source_path,
        })
    }
}

impl ProgramSnapshot {
    fn create(path: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        let (staged, sha256, _) = snapshot_executable(path, MAX_PRIMARY_PAYLOAD_BYTES)?;
        Ok(Self { staged, sha256 })
    }
}

struct SupplementalSnapshot {
    staged: FrozenPath,
    receipt: SupplementalPayload,
}

struct FrozenPath(tempfile::TempPath);

impl FrozenPath {
    fn path(&self) -> &Path {
        self.0.as_ref()
    }
}

fn snapshot_executable(
    path: &Path,
    max_bytes: u64,
) -> Result<(FrozenPath, String, u64), Box<dyn std::error::Error>> {
    snapshot_file(path, max_bytes, true, 0o400)
}

fn snapshot_file(
    path: &Path,
    max_bytes: u64,
    require_executable: bool,
    staged_mode: u32,
) -> Result<(FrozenPath, String, u64), Box<dyn std::error::Error>> {
    let source = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    snapshot_opened_file(source, max_bytes, require_executable, staged_mode)
}

fn snapshot_opened_file(
    source: File,
    max_bytes: u64,
    require_executable: bool,
    staged_mode: u32,
) -> Result<(FrozenPath, String, u64), Box<dyn std::error::Error>> {
    let metadata = source.metadata()?;
    if !metadata.file_type().is_file()
        || (require_executable && metadata.permissions().mode() & 0o111 == 0)
    {
        return Err("input must be a regular file with the required executable mode".into());
    }
    let mut staged = tempfile::Builder::new()
        .prefix("fc-payload-")
        .tempfile_in("/tmp")?;
    let copied = io::copy(&mut source.take(max_bytes + 1), staged.as_file_mut())?;
    if copied > max_bytes {
        return Err(format!("payload exceeds its {max_bytes}-byte size limit").into());
    }
    staged.as_file_mut().sync_all()?;
    staged
        .as_file()
        .set_permissions(fs::Permissions::from_mode(staged_mode))?;
    let sha256 = hash_file(staged.path())?;
    // Close the writable tempfile descriptor before the frozen controller or
    // Firecracker executable is passed to execve (Linux otherwise returns ETXTBSY).
    Ok((FrozenPath(staged.into_temp_path()), sha256, copied))
}

struct RemoveFileOnDrop(PathBuf);

impl Drop for RemoveFileOnDrop {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn main() {
    if let Err(error) = entry() {
        eprintln!("firecracker-harness: {error}");
        std::process::exit(1);
    }
}

fn entry() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args().skip(1);
    let command = args
        .next()
        .ok_or("expected preflight, smoke, network-smoke, cleanup-network, or run")?;
    match command.as_str() {
        "preflight" => {
            let option = args.next();
            match option.as_deref() {
                None => preflight(None, None, true, None, None, None),
                Some("--image-profile") => {
                    let path = args
                        .next()
                        .ok_or("preflight --image-profile needs a path")?;
                    if args.next().is_some() {
                        return Err("preflight accepts one image profile path".into());
                    }
                    let profile =
                        ImageProfile::load(Path::new(&path), &benchmark_repo_from_env_or_cwd()?)?;
                    preflight(
                        Some(&profile.kernel_path),
                        Some(&profile.rootfs_path),
                        true,
                        Some(&profile),
                        None,
                        None,
                    )
                }
                _ => Err("preflight accepts only --image-profile MANIFEST".into()),
            }
        }
        "smoke" => run_smoke(),
        "image-smoke" => {
            let path = args
                .next()
                .ok_or("image-smoke requires a profile manifest")?;
            if args.next().is_some() {
                return Err("image-smoke accepts exactly one profile manifest".into());
            }
            run_image_smoke(Path::new(&path))
        }
        "network-smoke" => run_network_smoke(),
        "--internal-vmm-launch" => NetworkLease::launch_vmm(&args.collect::<Vec<_>>()),
        "--internal-signal-vmm" => {
            let path = args
                .next()
                .ok_or("internal VMM signal requires receipt path")?;
            let signal = args.next().ok_or("internal VMM signal requires signal")?;
            if args.next().is_some() {
                return Err("internal VMM signal accepts exactly two arguments".into());
            }
            NetworkLease::signal_vmm_record(Path::new(&path), &signal)
        }
        "cleanup-network" => {
            let path = args
                .next()
                .ok_or("cleanup-network requires a receipt path")?;
            if args.next().is_some() {
                return Err("cleanup-network accepts exactly one receipt path".into());
            }
            NetworkLease::cleanup_record(Path::new(&path))
        }
        "run" => run_subject(&parse_run_args(&args.collect::<Vec<_>>())?),
        "help" | "--help" | "-h" => {
            print_help();
            Ok(())
        }
        other => Err(format!("unknown command {other:?}; run --help for usage").into()),
    }
}

fn print_help() {
    println!(
        "Usage:\n  fc-harness preflight [--image-profile IMAGE-MANIFEST.json]\n  fc-harness smoke\n  fc-harness image-smoke IMAGE-MANIFEST.json\n  fc-harness network-smoke\n  fc-harness cleanup-network RECEIPT_PATH\n  fc-harness run --benchmark-repo PATH --subject-repo PATH --program PATH --run-id ID [--timeout-seconds N] [--network-mode isolated] [--image-profile IMAGE-MANIFEST.json] [--payload-file HOST_PATH:BASENAME] [--output DIR] [--kernel PATH] [--rootfs PATH] -- [guest args...]\n\nWorkload timeout defaults to 1800 seconds (maximum 3500). Smoke commands find the benchmark checkout from FC_BENCHMARK_REPO or the current directory. All workload commands run inside a pinned Firecracker guest. There is no host fallback."
    );
}

fn parse_run_args(args: &[String]) -> Result<RunArgs, Box<dyn std::error::Error>> {
    let separator = args
        .iter()
        .position(|arg| arg == "--")
        .unwrap_or(args.len());
    let (options, guest_args) = args.split_at(separator);
    let guest_args = if separator < args.len() {
        guest_args[1..].to_vec()
    } else {
        Vec::new()
    };
    let mut benchmark_repo = None;
    let mut subject_repo = None;
    let mut program = None;
    let mut run_id = None;
    let mut output = None;
    let mut kernel = None;
    let mut rootfs = None;
    let mut timeout_seconds = DEFAULT_WORKLOAD_TIMEOUT_SECS;
    let mut isolated_network = false;
    let mut image_profile = None;
    let mut payload_files = Vec::new();
    let mut index = 0;
    while index < options.len() {
        let key = options[index].as_str();
        let value = options
            .get(index + 1)
            .ok_or_else(|| format!("missing value for {key}"))?;
        match key {
            "--benchmark-repo" => benchmark_repo = Some(PathBuf::from(value)),
            "--subject-repo" => subject_repo = Some(PathBuf::from(value)),
            "--program" => program = Some(PathBuf::from(value)),
            "--run-id" => run_id = Some(value.clone()),
            "--output" => output = Some(PathBuf::from(value)),
            "--kernel" => kernel = Some(PathBuf::from(value)),
            "--rootfs" => rootfs = Some(PathBuf::from(value)),
            "--timeout-seconds" => timeout_seconds = value.parse()?,
            "--network-mode" => {
                if value != "isolated" {
                    return Err("--network-mode currently supports only isolated".into());
                }
                isolated_network = true;
            }
            "--image-profile" => image_profile = Some(PathBuf::from(value)),
            "--payload-file" => {
                let (host, basename) = value
                    .rsplit_once(':')
                    .ok_or("--payload-file expects HOST_PATH:BASENAME")?;
                if host.is_empty()
                    || basename.is_empty()
                    || !basename
                        .bytes()
                        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
                    || basename == "fc-harness-payload"
                    || basename == "fc-harness-agent"
                {
                    return Err(
                        "--payload-file requires a safe, non-reserved guest basename".into(),
                    );
                }
                if payload_files.iter().any(|(_, name)| name == basename) {
                    return Err("duplicate --payload-file guest basename".into());
                }
                payload_files.push((PathBuf::from(host), basename.to_owned()));
                if payload_files.len() > 4 {
                    return Err("at most four supplemental payload files are allowed".into());
                }
            }
            _ => return Err(format!("unknown option {key:?}").into()),
        }
        index += 2;
    }
    let run_id = run_id.ok_or("--run-id is required and must be unique")?;
    if !run_id
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err("--run-id may contain only ASCII letters, digits, '-' and '_'".into());
    }
    if !(1..=MAX_WORKLOAD_TIMEOUT_SECS).contains(&timeout_seconds) {
        return Err(
            format!("--timeout-seconds must be between 1 and {MAX_WORKLOAD_TIMEOUT_SECS}").into(),
        );
    }
    Ok(RunArgs {
        benchmark_repo: validate_benchmark_repo(
            &benchmark_repo.ok_or("--benchmark-repo is required")?,
        )?,
        subject_repo: subject_repo.ok_or("--subject-repo is required")?,
        program: program.ok_or("--program is required")?,
        run_id,
        output,
        kernel,
        rootfs,
        guest_args,
        guest_program: None,
        timeout_seconds,
        isolated_network,
        image_profile,
        payload_files,
    })
}

fn validate_benchmark_repo(path: &Path) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let root = fs::canonicalize(path)?;
    if !root.join("firecracker-harness/Cargo.toml").is_file() || !root.join("bench").is_dir() {
        return Err(format!(
            "{} is not a benchmark checkout with firecracker-harness/ and bench/",
            root.display()
        )
        .into());
    }
    Ok(root)
}

fn benchmark_repo_from_env_or_cwd() -> Result<PathBuf, Box<dyn std::error::Error>> {
    if let Some(path) = env::var_os("FC_BENCHMARK_REPO") {
        return validate_benchmark_repo(Path::new(&path));
    }
    let cwd = env::current_dir()?;
    for ancestor in cwd.ancestors() {
        if ancestor.join("firecracker-harness/Cargo.toml").is_file()
            && ancestor.join("bench").is_dir()
        {
            return validate_benchmark_repo(ancestor);
        }
    }
    Err("benchmark checkout not found; set FC_BENCHMARK_REPO".into())
}

fn sibling_subject_repo(benchmark_repo: &Path) -> Result<PathBuf, Box<dyn std::error::Error>> {
    Ok(benchmark_repo
        .parent()
        .ok_or("benchmark checkout has no parent directory")?
        .join("terrabase"))
}

fn preflight(
    kernel_override: Option<&Path>,
    rootfs_override: Option<&Path>,
    verify_images: bool,
    profile: Option<&ImageProfile>,
    firecracker_override: Option<&Path>,
    agent_override: Option<&Path>,
) -> Result<(), Box<dyn std::error::Error>> {
    let kernel = kernel_override.map_or_else(|| pin_path("kernel_path"), Path::to_path_buf);
    let rootfs = rootfs_override.map_or_else(|| pin_path("rootfs_path"), Path::to_path_buf);
    let firecracker = if let Some(path) = firecracker_override {
        path.to_path_buf()
    } else {
        find_executable("firecracker")?
    };
    if let Some(expected_sha) =
        profile.and_then(|candidate| candidate.firecracker_sha256.as_deref())
    {
        if hash_file(&firecracker)? != expected_sha {
            return Err("Firecracker executable SHA-256 differs from the image profile".into());
        }
    }
    let version_output = Command::new(&firecracker).arg("--version").output()?;
    let version_text = String::from_utf8_lossy(&version_output.stdout);
    let expected_version = profile.map_or_else(
        || pin_string("firecracker_version"),
        |candidate| candidate.firecracker_version.clone(),
    );
    if !version_output.status.success()
        || !version_text.contains(&format!("Firecracker v{expected_version}"))
    {
        return Err(format!(
            "Firecracker version mismatch: wanted {expected_version}, got {version_text:?}"
        )
        .into());
    }
    let kvm_api_version = kvm_api_version()?;
    // A run verifies both source images while creating its initial receipt.
    // Standalone preflight verifies them here instead.
    let expected_kernel_sha = profile.map_or_else(
        || pin_string("kernel_sha256"),
        |profile| profile.kernel_sha256.clone(),
    );
    let expected_rootfs_sha = profile.map_or_else(
        || pin_string("rootfs_sha256"),
        |profile| profile.rootfs_sha256.clone(),
    );
    let (kernel_sha, rootfs_sha) = if verify_images {
        (
            verify_file(&kernel, &expected_kernel_sha)?,
            verify_file(&rootfs, &expected_rootfs_sha)?,
        )
    } else {
        (expected_kernel_sha, expected_rootfs_sha)
    };
    let fstab = debugfs_read(&rootfs, "/etc/fstab")?;
    if fstab.lines().any(|line| {
        let content = line.split('#').next().unwrap_or_default();
        let fields = content.split_whitespace().collect::<Vec<_>>();
        fields.len() >= 3 && fields[2] == "swap"
    }) {
        return Err("pinned rootfs enables swap in /etc/fstab".into());
    }
    let cpu_affinity = allowed_cpus()?;
    if cpu_affinity.len() < INSTANCE_VCPU_COUNT {
        return Err(format!(
            "only {} host CPUs are available; Firecracker requires four",
            cpu_affinity.len()
        )
        .into());
    }
    let available_bytes = host_available_memory_bytes()?;
    let required_bytes = (INSTANCE_MEMORY_MIB + 1024) * 1024 * 1024;
    if available_bytes < required_bytes {
        return Err(format!("host has {available_bytes} available bytes; need at least {required_bytes} for an 8 GiB guest and host overhead").into());
    }
    let debugfs = find_executable("debugfs")?;
    let _ = debugfs;
    let taskset = find_executable("taskset")?;
    let _ = taskset;
    let musl_agent = agent_override.map_or_else(guest_agent_path, |path| Ok(path.to_path_buf()))?;
    if !musl_agent.is_file() {
        return Err(format!(
            "guest agent is not built: {}; run `make -C firecracker-harness build-guest`",
            musl_agent.display()
        )
        .into());
    }
    let agent_file = File::open(&musl_agent)?;
    let mut agent_bytes = agent_file;
    let mut prefix = [0_u8; 4];
    agent_bytes.read_exact(&mut prefix)?;
    if prefix != *b"\x7fELF" {
        return Err("guest agent is not an ELF executable".into());
    }
    println!("Firecracker {expected_version}: OK");
    println!("KVM_GET_API_VERSION: {kvm_api_version}");
    println!("guest: {INSTANCE_VCPU_COUNT} vCPUs / {INSTANCE_MEMORY_MIB} MiB / swap disabled");
    println!("kernel {} sha256={kernel_sha}", kernel.display());
    println!("rootfs {} sha256={rootfs_sha}", rootfs.display());
    println!(
        "guest agent {} sha256={}",
        musl_agent.display(),
        hash_file(&musl_agent)?
    );
    println!("host CPU affinity candidates: {cpu_affinity:?}");
    println!("guest image upstream provenance: unqualified (checksum-pinned local artifact only)");
    Ok(())
}

fn run_smoke() -> Result<(), Box<dyn std::error::Error>> {
    let benchmark_repo = benchmark_repo_from_env_or_cwd()?;
    let subject_repo = sibling_subject_repo(&benchmark_repo)?;
    let smoke_id = format!("vm-smoke-{}-{}", unix_millis(), std::process::id());
    let pass_id = format!("{smoke_id}-pass");
    run_vm(
        &RunArgs {
            benchmark_repo: benchmark_repo.clone(),
            subject_repo,
            program: PathBuf::from("/usr/bin/uname"),
            run_id: pass_id,
            output: None,
            kernel: None,
            rootfs: None,
            guest_args: vec!["-a".to_owned()],
            guest_program: Some("/usr/bin/uname".to_owned()),
            timeout_seconds: 30,
            isolated_network: false,
            image_profile: None,
            payload_files: Vec::new(),
        },
        true,
    )
    .map_err(|error| format!("positive guest smoke failed: {error}"))?;

    let fail_id = format!("{smoke_id}-expected-failure");
    let fail_output = default_output_path(&benchmark_repo, &fail_id);
    let expected_failure = run_vm(
        &RunArgs {
            subject_repo: sibling_subject_repo(&benchmark_repo)?,
            benchmark_repo,
            program: PathBuf::from("/usr/bin/false"),
            run_id: fail_id.clone(),
            output: None,
            kernel: None,
            rootfs: None,
            guest_args: Vec::new(),
            guest_program: Some("/usr/bin/false".to_owned()),
            timeout_seconds: 30,
            isolated_network: false,
            image_profile: None,
            payload_files: Vec::new(),
        },
        true,
    );
    let manifest_path = fail_output.join("run-manifest.json");
    let failure_manifest: RunManifest = serde_json::from_slice(&fs::read(&manifest_path)?)?;
    if expected_failure.is_ok()
        || failure_manifest.status != "failed"
        || failure_manifest.guest_exit_code != Some(1)
    {
        return Err(format!(
            "failing guest command was not recorded fail-closed: {failure_manifest:?}"
        )
        .into());
    }
    println!(
        "expected guest failure recorded at {}",
        manifest_path.display()
    );
    Ok(())
}

fn run_network_smoke() -> Result<(), Box<dyn std::error::Error>> {
    let benchmark_repo = benchmark_repo_from_env_or_cwd()?;
    let run_id = format!("vm-network-smoke-{}-{}", unix_millis(), std::process::id());
    run_vm(
        &RunArgs {
            subject_repo: sibling_subject_repo(&benchmark_repo)?,
            benchmark_repo,
            program: PathBuf::from("/usr/bin/uname"),
            run_id,
            output: None,
            kernel: None,
            rootfs: None,
            guest_args: vec!["-a".to_owned()],
            guest_program: Some("/usr/bin/uname".to_owned()),
            timeout_seconds: 30,
            isolated_network: true,
            image_profile: None,
            payload_files: Vec::new(),
        },
        true,
    )
}

fn run_image_smoke(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    // Verify the candidate manifest and bytes before reserving an output run.
    let benchmark_repo = benchmark_repo_from_env_or_cwd()?;
    let profile = ImageProfile::load(path, &benchmark_repo)?;
    let run_id = format!("vm-image-smoke-{}-{}", unix_millis(), std::process::id());
    run_vm(
        &RunArgs {
            subject_repo: sibling_subject_repo(&benchmark_repo)?,
            benchmark_repo,
            program: PathBuf::from("/usr/bin/uname"),
            run_id,
            output: None,
            kernel: None,
            rootfs: None,
            guest_args: vec!["-a".to_owned()],
            guest_program: Some("/usr/bin/uname".to_owned()),
            timeout_seconds: 30,
            isolated_network: false,
            image_profile: Some(profile.manifest_path),
            payload_files: Vec::new(),
        },
        true,
    )
}

fn run_subject(args: &RunArgs) -> Result<(), Box<dyn std::error::Error>> {
    run_vm(args, false)
}

fn supplemental_snapshots(
    args: &RunArgs,
) -> Result<Vec<SupplementalSnapshot>, Box<dyn std::error::Error>> {
    let mut result = Vec::new();
    let mut total_bytes = 0_u64;
    for (host, basename) in &args.payload_files {
        let remaining = MAX_SUPPLEMENTAL_PAYLOAD_BYTES - total_bytes;
        let (staged, sha256, size_bytes) = snapshot_executable(host, remaining)?;
        total_bytes = total_bytes
            .checked_add(size_bytes)
            .ok_or("supplemental payload size overflow")?;
        result.push(SupplementalSnapshot {
            staged,
            receipt: SupplementalPayload {
                host_path: host.display().to_string(),
                guest_path: format!("/root/fc-harness-payloads/{basename}"),
                size_bytes,
                sha256,
                guest_mode: "0755".to_owned(),
            },
        });
    }
    Ok(result)
}

#[expect(
    clippy::too_many_lines,
    reason = "the run receipt and its VM lifetime share one failure boundary"
)]
fn run_vm(args: &RunArgs, smoke: bool) -> Result<(), Box<dyn std::error::Error>> {
    let profile = args
        .image_profile
        .as_deref()
        .map(|path| ImageProfile::load(path, &args.benchmark_repo))
        .transpose()?;
    if profile.is_some() && (args.kernel.is_some() || args.rootfs.is_some()) {
        return Err("--kernel/--rootfs cannot override --image-profile artifacts".into());
    }
    let started = unix_millis();
    let output = args
        .output
        .clone()
        .unwrap_or_else(|| default_output_path(&args.benchmark_repo, &args.run_id));
    if output.exists() {
        return Err(format!(
            "run output already exists at {}; refusing to duplicate or overwrite work",
            output.display()
        )
        .into());
    }
    fs::create_dir_all(&output)?;
    let program_snapshot = if args.guest_program.is_some() {
        None
    } else {
        Some(ProgramSnapshot::create(&args.program)?)
    };
    let agent_snapshot = AgentSnapshot::create()?;
    let supplemental_snapshots = supplemental_snapshots(args)?;

    let kernel = profile.as_ref().map_or_else(
        || {
            args.kernel
                .clone()
                .unwrap_or_else(|| pin_path("kernel_path"))
        },
        |profile| profile.kernel_path.clone(),
    );
    let rootfs = profile.as_ref().map_or_else(
        || {
            args.rootfs
                .clone()
                .unwrap_or_else(|| pin_path("rootfs_path"))
        },
        |profile| profile.rootfs_path.clone(),
    );
    let controller_snapshot = HostArtifactSnapshot::controller()?;
    let firecracker_snapshot = HostArtifactSnapshot::firecracker()?;
    let kernel_expected = profile.as_ref().map_or_else(
        || pin_string("kernel_sha256"),
        |candidate| candidate.kernel_sha256.clone(),
    );
    let kernel_snapshot = HostArtifactSnapshot::kernel(&kernel, &kernel_expected)?;
    let mut manifest = initial_manifest(
        args,
        profile.as_ref(),
        &kernel,
        &rootfs,
        &output,
        started,
        smoke,
        program_snapshot.as_ref(),
        &agent_snapshot,
        &controller_snapshot,
        &firecracker_snapshot,
        &kernel_snapshot,
        &supplemental_snapshots,
    )?;
    write_manifest(&output, &manifest)?;

    let operation = run_vm_inner(
        args,
        &rootfs,
        &output,
        &mut manifest,
        program_snapshot.as_ref(),
        &agent_snapshot,
        &controller_snapshot,
        &firecracker_snapshot,
        &kernel_snapshot,
        &supplemental_snapshots,
    );
    manifest.finished_unix_ms = Some(unix_millis());
    let operation = operation.and_then(|()| {
        if manifest.status == "passed" && manifest.guest_exit_code == Some(0) {
            Ok(())
        } else {
            Err(manifest
                .error
                .clone()
                .unwrap_or_else(|| {
                    format!(
                        "guest run did not pass: status={}, exit_code={:?}",
                        manifest.status, manifest.guest_exit_code
                    )
                })
                .into())
        }
    });
    manifest.status = if operation.is_ok() {
        "passed".to_owned()
    } else {
        "failed".to_owned()
    };
    if let Err(error) = &operation {
        if manifest.error.is_none() {
            manifest.error = Some(error.to_string());
        }
        write_manifest(&output, &manifest)?;
        return Err(format!(
            "VM run failed; evidence directory: {}: {error}",
            output.display()
        )
        .into());
    }
    write_manifest(&output, &manifest)?;
    println!(
        "run {} completed in {} ms; receipt: {}",
        args.run_id,
        manifest.elapsed_ms.unwrap_or_default(),
        output.join("run-manifest.json").display()
    );
    Ok(())
}

fn default_output_path(benchmark_repo: &Path, run_id: &str) -> PathBuf {
    benchmark_repo
        .join("bench/firecracker-harness")
        .join(run_id)
}

#[expect(
    clippy::too_many_arguments,
    reason = "the receipt records the exact image and staged input snapshots selected for this run"
)]
fn initial_manifest(
    args: &RunArgs,
    profile: Option<&ImageProfile>,
    kernel: &Path,
    rootfs: &Path,
    output: &Path,
    started: u128,
    smoke: bool,
    program_snapshot: Option<&ProgramSnapshot>,
    agent_snapshot: &AgentSnapshot,
    controller_snapshot: &HostArtifactSnapshot,
    firecracker_snapshot: &HostArtifactSnapshot,
    kernel_snapshot: &HostArtifactSnapshot,
    supplemental_snapshots: &[SupplementalSnapshot],
) -> Result<RunManifest, Box<dyn std::error::Error>> {
    if let Some(expected_sha) =
        profile.and_then(|candidate| candidate.firecracker_sha256.as_deref())
    {
        if firecracker_snapshot.sha256 != expected_sha {
            return Err(
                "snapshotted Firecracker executable SHA-256 differs from the image profile".into(),
            );
        }
    }
    let version_output = Command::new(firecracker_snapshot.staged.path())
        .arg("--version")
        .output()?;
    let version_text = String::from_utf8_lossy(&version_output.stdout);
    let firecracker_version = profile.map_or_else(
        || pin_string("firecracker_version"),
        |candidate| candidate.firecracker_version.clone(),
    );
    if !version_output.status.success()
        || !version_text.contains(&format!("Firecracker v{firecracker_version}"))
    {
        return Err(format!(
            "Firecracker version mismatch: wanted {firecracker_version}, got {version_text:?}"
        )
        .into());
    }
    Ok(RunManifest {
        schema_version: 1,
        run_id: args.run_id.clone(),
        status: "preflight".to_owned(),
        started_unix_ms: started,
        finished_unix_ms: None,
        purpose: if smoke { "environment-smoke".to_owned() } else { "guest-program".to_owned() },
        source_provenance: profile.map_or_else(|| "kernel and rootfs SHA-256 pinned from historical local manifest; upstream artifact URLs/version metadata are not available, so provenance is unqualified".to_owned(), ImageProfile::source_provenance),
        image_profile: profile.cloned(),
        benchmark_repository: repository_identity(&args.benchmark_repo)?,
        subject_repository: repository_identity(&args.subject_repo)?,
        program_path: args.program.display().to_string(),
        program_sha256: program_snapshot.map_or_else(
            || "guest-base-image-binary; verified in guest at execution".to_owned(),
            |snapshot| snapshot.sha256.clone(),
        ),
        guest_agent_path: agent_snapshot.source_path.display().to_string(),
        guest_agent_sha256: agent_snapshot.sha256.clone(),
        guest_agent_size_bytes: agent_snapshot.size_bytes,
        controller_executable: controller_snapshot.source_path.display().to_string(),
        controller_sha256: controller_snapshot.sha256.clone(),
        controller_size_bytes: controller_snapshot.size_bytes,
        supplemental_payloads: supplemental_snapshots
            .iter()
            .map(|snapshot| snapshot.receipt.clone())
            .collect(),
        kernel_path: kernel.display().to_string(),
        kernel_launch_path: kernel_snapshot.staged.path().display().to_string(),
        kernel_sha256: kernel_snapshot.sha256.clone(),
        kernel_size_bytes: kernel_snapshot.size_bytes,
        kernel_release: profile.map_or_else(|| pin_string("kernel_release"), |profile| profile.kernel_release.clone()),
        rootfs_path: rootfs.display().to_string(),
        rootfs_sha256: verify_file(rootfs, profile.map_or_else(|| pin_string("rootfs_sha256"), |profile| profile.rootfs_sha256.clone()).as_str())?,
        preboot_rootfs_sha256: None,
        rootfs_release: profile.map_or_else(|| pin_string("rootfs_release"), |profile| profile.rootfs_release.clone()),
        rootfs_source_manifest: profile.map_or_else(|| pin_string("source_manifest"), |profile| profile.manifest_path.display().to_string()),
        firecracker_version,
        firecracker_executable: firecracker_snapshot.source_path.display().to_string(),
        firecracker_launch_path: firecracker_snapshot.staged.path().display().to_string(),
        firecracker_sha256: firecracker_snapshot.sha256.clone(),
        firecracker_size_bytes: firecracker_snapshot.size_bytes,
        kvm_api_version: kvm_api_version()?,
        guest_vcpu_count: INSTANCE_VCPU_COUNT,
        guest_mem_size_mib: INSTANCE_MEMORY_MIB,
        guest_smt_enabled: false,
        guest_dynamic_memory: false,
        guest_swap_disabled: true,
        host_cpu_model: host_cpu_model()?,
        host_cpu_affinity: allowed_cpus()?
            .into_iter()
            .take(INSTANCE_VCPU_COUNT)
            .collect(),
        host_available_memory_bytes: host_available_memory_bytes()?,
        host_swap_active: host_swap_active()?,
        output_filesystem: filesystem_type(output)?,
        guest_network: if args.isolated_network {
            "isolated run-owned namespace and TAP; no host uplink or service fixture".to_owned()
        } else {
            "disabled; no host network or unisolated service fixture is attached".to_owned()
        },
        network_fixture: None,
        guest_report: None,
        guest_exit_code: None,
        guest_elapsed_ms: None,
        workload_timeout_seconds: args.timeout_seconds,
        elapsed_ms: None,
        error: None,
    })
}

#[expect(
    clippy::too_many_lines,
    reason = "ordered VM setup and teardown share one failure scope"
)]
#[expect(
    clippy::too_many_arguments,
    reason = "VM launch uses explicit frozen source snapshots and the mutable run receipt"
)]
fn run_vm_inner(
    args: &RunArgs,
    original_rootfs: &Path,
    output: &Path,
    manifest: &mut RunManifest,
    program_snapshot: Option<&ProgramSnapshot>,
    agent_snapshot: &AgentSnapshot,
    controller_snapshot: &HostArtifactSnapshot,
    firecracker_snapshot: &HostArtifactSnapshot,
    kernel_snapshot: &HostArtifactSnapshot,
    supplemental_snapshots: &[SupplementalSnapshot],
) -> Result<(), Box<dyn std::error::Error>> {
    preflight(
        Some(kernel_snapshot.staged.path()),
        Some(original_rootfs),
        false,
        manifest.image_profile.as_ref(),
        Some(firecracker_snapshot.staged.path()),
        Some(agent_snapshot.staged.path()),
    )?;
    let rootfs_pin = manifest.rootfs_sha256.clone();
    verify_file(kernel_snapshot.staged.path(), &manifest.kernel_sha256)?;

    let working_rootfs = output.join("guest-rootfs.ext4");
    let _working_rootfs_cleanup = RemoveFileOnDrop(working_rootfs.clone());
    fs::copy(original_rootfs, &working_rootfs)?;
    File::open(&working_rootfs)?.sync_all()?;
    verify_file(&working_rootfs, &rootfs_pin)?;

    let unit_path = PathBuf::from(format!(
        "/tmp/fc-guest-agent-{}.service",
        std::process::id()
    ));
    fs::write(
        &unit_path,
        "[Unit]\nDescription=Firecracker benchmark guest agent\nAfter=local-fs.target\n\n[Service]\nType=simple\nExecStart=/root/fc-harness-agent\nStandardOutput=journal+console\nStandardError=journal+console\nRestart=no\n\n[Install]\nWantedBy=multi-user.target\n",
    )?;

    verify_file(agent_snapshot.staged.path(), &manifest.guest_agent_sha256)?;
    debugfs_write(
        &working_rootfs,
        agent_snapshot.staged.path(),
        GUEST_AGENT_PATH,
    )?;
    debugfs_set_mode(&working_rootfs, GUEST_AGENT_PATH)?;
    let agent_check = tempfile::Builder::new()
        .prefix("fc-agent-verify-")
        .tempdir_in("/tmp")?;
    let copied_agent = agent_check.path().join("agent");
    debugfs_dump(&working_rootfs, GUEST_AGENT_PATH, &copied_agent)?;
    if fs::metadata(&copied_agent)?.len() != manifest.guest_agent_size_bytes {
        return Err("guest agent ext4 copy has the wrong size".into());
    }
    verify_file(&copied_agent, &manifest.guest_agent_sha256)?;
    debugfs_write(&working_rootfs, &unit_path, GUEST_AGENT_UNIT)?;
    debugfs_symlink(
        &working_rootfs,
        "/etc/systemd/system/multi-user.target.wants/fc-harness-agent.service",
        "../fc-harness-agent.service",
    )?;
    if let Some(snapshot) = program_snapshot {
        verify_file(snapshot.staged.path(), &manifest.program_sha256)?;
        debugfs_write(&working_rootfs, snapshot.staged.path(), GUEST_PAYLOAD_PATH)?;
        debugfs_set_mode(&working_rootfs, GUEST_PAYLOAD_PATH)?;
        let copied = tempfile::Builder::new()
            .prefix("fc-program-verify-")
            .tempdir_in("/tmp")?;
        let copied_path = copied.path().join("payload");
        debugfs_dump(&working_rootfs, GUEST_PAYLOAD_PATH, &copied_path)?;
        verify_file(&copied_path, &manifest.program_sha256)?;
    }
    if !manifest.supplemental_payloads.is_empty() {
        debugfs_command(
            &working_rootfs,
            "mkdir /root/fc-harness-payloads".to_owned(),
        )?;
    }
    for snapshot in supplemental_snapshots {
        let payload = &snapshot.receipt;
        // debugfs parses host paths as command text. The frozen snapshot has
        // a private, space-free path and is the source of the receipt hash.
        verify_file(snapshot.staged.path(), &payload.sha256)?;
        debugfs_write(&working_rootfs, snapshot.staged.path(), &payload.guest_path)?;
        debugfs_set_mode(&working_rootfs, &payload.guest_path)?;
        let staging = tempfile::Builder::new()
            .prefix("fc-payload-verify-")
            .tempdir_in("/tmp")?;
        let copied = staging.path().join("copied");
        debugfs_dump(&working_rootfs, &payload.guest_path, &copied)?;
        verify_file(&copied, &payload.sha256)?;
    }
    let _ = fs::remove_file(&unit_path);

    let mut network = if args.isolated_network {
        let helper_path = output.join("network-helper.bin");
        fs::copy(controller_snapshot.staged.path(), &helper_path)?;
        fs::set_permissions(&helper_path, fs::Permissions::from_mode(0o500))?;
        verify_file(&helper_path, &manifest.controller_sha256)?;
        File::open(&helper_path)?.sync_all()?;
        File::open(output)?.sync_all()?;
        Some(NetworkLease::create(
            output,
            &args.run_id,
            &manifest.controller_sha256,
        )?)
    } else {
        None
    };
    if let Some(lease) = &network {
        let config_path = PathBuf::from(format!("/tmp/fc-network-{}.network", std::process::id()));
        let _config_cleanup = RemoveFileOnDrop(config_path.clone());
        fs::write(&config_path, lease.guest_network_file())?;
        debugfs_write(&working_rootfs, &config_path, GUEST_NETWORK_UNIT)?;
        manifest.network_fixture = Some(lease.receipt().clone());
    }

    File::open(&working_rootfs)?.sync_all()?;
    manifest.preboot_rootfs_sha256 = Some(hash_file(&working_rootfs)?);
    verify_file(
        firecracker_snapshot.staged.path(),
        &manifest.firecracker_sha256,
    )?;
    verify_file(
        controller_snapshot.staged.path(),
        &manifest.controller_sha256,
    )?;
    write_manifest(output, manifest)?;

    let firecracker = firecracker_snapshot.staged.path();
    let affinity = manifest
        .host_cpu_affinity
        .iter()
        .map(usize::to_string)
        .collect::<Vec<_>>()
        .join(",");
    // Firecracker's Unix socket paths must fit sockaddr_un.sun_path. A private
    // short-lived directory keeps both endpoints short, unique, and off the
    // long evidence path without an external compatibility symlink.
    let socket_dir = tempfile::Builder::new().prefix("fch-").tempdir_in("/tmp")?;
    let api_socket = socket_dir.path().join("api.sock");
    let vsock_uds = socket_dir.path().join("vsock.sock");
    let firecracker_log = File::create(output.join("firecracker.log"))?;
    let firecracker_stderr = File::create(output.join("firecracker.stderr.log"))?;
    let mut command = if let Some(lease) = &network {
        lease.firecracker_command(&affinity, firecracker)?
    } else {
        let mut command = Command::new("taskset");
        command.args(["-c", &affinity]).arg(firecracker);
        command
    };
    let mut child = command
        .args(["--id", &args.run_id, "--api-sock"])
        .arg(&api_socket)
        .args(["--log-path"])
        .arg(output.join("firecracker-internal.log"))
        .args(["--level", "Info", "--show-level"])
        .stdin(Stdio::null())
        .stdout(Stdio::from(firecracker_log))
        .stderr(Stdio::from(firecracker_stderr))
        .spawn()?;
    let run_started = Instant::now();
    let vm_result = (|| -> Result<GuestRunResponse, Box<dyn std::error::Error>> {
        write_pid(output, child.id())?;
        "firecracker-starting".clone_into(&mut manifest.status);
        write_manifest(output, manifest)?;
        wait_for_socket(&api_socket, &mut child, Duration::from_secs(20))?;
        api_request(
            &api_socket,
            "PUT",
            "/machine-config",
            &serde_json::json!({
                "vcpu_count": INSTANCE_VCPU_COUNT,
                "mem_size_mib": INSTANCE_MEMORY_MIB,
                "smt": false
            }),
        )?;
        let effective_machine = api_get(&api_socket, "/machine-config")?;
        if effective_machine["vcpu_count"].as_u64() != Some(INSTANCE_VCPU_COUNT as u64)
            || effective_machine["mem_size_mib"].as_u64() != Some(INSTANCE_MEMORY_MIB)
            || effective_machine["smt"].as_bool() != Some(false)
        {
            return Err(format!(
                "Firecracker effective machine configuration differs from the pinned 4-vCPU/8192-MiB/smt=false configuration: {effective_machine}"
            )
            .into());
        }
        api_request(
            &api_socket,
            "PUT",
            "/boot-source",
            &serde_json::json!({
                "kernel_image_path": kernel_snapshot.staged.path(),
                "boot_args": "console=ttyS0 root=/dev/vda rootfstype=ext4 rootwait rw panic=1 systemd.show_status=0 systemd.mask=cloud-init-local.service systemd.mask=cloud-init.service systemd.mask=cloud-config.service systemd.mask=cloud-final.service"
            }),
        )?;
        api_request(
            &api_socket,
            "PUT",
            "/drives/rootfs",
            &serde_json::json!({
                "drive_id": "rootfs",
                "path_on_host": working_rootfs,
                "is_root_device": true,
                "is_read_only": false,
                "cache_type": "Writeback",
                "io_engine": "Sync"
            }),
        )?;
        api_request(
            &api_socket,
            "PUT",
            "/vsock",
            &serde_json::json!({
                "guest_cid": 3,
                "uds_path": vsock_uds
            }),
        )?;
        if let Some(lease) = &network {
            api_request(
                &api_socket,
                "PUT",
                "/network-interfaces/bench0",
                &serde_json::json!({
                    "iface_id": "bench0",
                    "host_dev_name": lease.receipt().tap,
                    "guest_mac": lease.receipt().guest_mac,
                }),
            )?;
        }
        api_request(
            &api_socket,
            "PUT",
            "/actions",
            &serde_json::json!({ "action_type": "InstanceStart" }),
        )?;
        "guest-booting".clone_into(&mut manifest.status);
        write_manifest(output, manifest)?;
        let mut stream = connect_guest_vsock(&vsock_uds, &mut child, VSOCK_CONNECT_TIMEOUT)?;
        // The guest owns the workload deadline. The host also bounds a silent
        // or wedged agent and then tears down Firecracker through this scope.
        let response_timeout =
            Duration::from_secs(args.timeout_seconds.saturating_add(120)).min(RUN_TIMEOUT);
        stream.set_read_timeout(Some(response_timeout))?;
        stream.set_write_timeout(Some(Duration::from_secs(30)))?;
        let mut reader = BufReader::new(stream.try_clone()?);
        let mut readiness_line = String::new();
        reader.read_line(&mut readiness_line)?;
        if readiness_line.is_empty() {
            return Err(
                "guest agent closed before reporting its CPU/memory/swap configuration".into(),
            );
        }
        let guest: GuestReport = serde_json::from_str(&readiness_line)?;
        manifest.guest_report = Some(guest.clone());
        "guest-ready".clone_into(&mut manifest.status);
        write_manifest(output, manifest)?;
        if guest.online_cpus != INSTANCE_VCPU_COUNT
            || guest.mem_total_bytes < 7 * 1024 * 1024 * 1024
            || guest.swap_entries != 0
        {
            return Err(format!("guest configuration check failed: {guest:?}").into());
        }
        let guest_program = args.guest_program.as_deref().unwrap_or(GUEST_PAYLOAD_PATH);
        let request = serde_json::json!({
            "operation": "run",
            "program": guest_program,
            "args": args.guest_args,
            "timeout_seconds": args.timeout_seconds,
            "manifest": {
                "run_id": manifest.run_id,
                "firecracker_version": manifest.firecracker_version,
                "vcpu_count": effective_machine["vcpu_count"],
                "mem_size_mib": effective_machine["mem_size_mib"],
                "smt_enabled": effective_machine["smt"],
                "machine_config_source": "Firecracker GET /machine-config before InstanceStart",
                "kernel_sha256": manifest.kernel_sha256,
                "rootfs_sha256": manifest.rootfs_sha256,
                "controller_sha256": manifest.controller_sha256,
                "controller_size_bytes": manifest.controller_size_bytes,
                "firecracker_sha256": manifest.firecracker_sha256,
                "firecracker_size_bytes": manifest.firecracker_size_bytes,
                "guest_agent_sha256": manifest.guest_agent_sha256,
                "guest_agent_size_bytes": manifest.guest_agent_size_bytes,
                "source_provenance": manifest.source_provenance,
                "image_profile": manifest.image_profile,
                "supplemental_payloads": manifest.supplemental_payloads,
                "benchmark_repository": manifest.benchmark_repository,
                "subject_repository": manifest.subject_repository,
                "program_sha256": manifest.program_sha256,
                "host_cpu_affinity": manifest.host_cpu_affinity,
                "output_filesystem": manifest.output_filesystem,
                "guest_network": manifest.guest_network,
                "network_fixture": manifest.network_fixture,
            },
            "env": [
                ["TERRABASE_BENCH_RUN_ID", &args.run_id],
                ["TERRABASE_SOURCE_REVISION", &manifest.subject_repository.head]
            ]
        });
        serde_json::to_writer(&mut stream, &request)?;
        stream.write_all(b"\n")?;
        stream.flush()?;
        let mut response_line = String::new();
        reader.read_line(&mut response_line)?;
        if response_line.is_empty() {
            return Err("guest agent closed before returning the workload result".into());
        }
        let response: GuestRunResponse = serde_json::from_str(&response_line)?;
        if let Some(lease) = &network {
            if !response
                .guest
                .ipv4_addresses
                .contains(&lease.receipt().guest_ipv4)
            {
                return Err(format!(
                    "guest did not observe expected network address {}: {:?}",
                    lease.receipt().guest_ipv4,
                    response.guest.ipv4_addresses
                )
                .into());
            }
        }
        if response.archive_bytes > MAX_GUEST_ARCHIVE_BYTES {
            return Err(format!(
                "guest output archive exceeds the {MAX_GUEST_ARCHIVE_BYTES}-byte host limit"
            )
            .into());
        }
        if response.archive_bytes > 0 {
            let mut archive = File::create(output.join("guest-output.tar.gz"))?;
            let mut limited = reader.take(response.archive_bytes);
            let received = io::copy(&mut limited, &mut archive)?;
            if received != response.archive_bytes {
                return Err(format!(
                    "guest output archive truncated: expected {}, received {received}",
                    response.archive_bytes
                )
                .into());
            }
            archive.sync_all()?;
        }
        Ok(response)
    })();

    let namespace_stop = network
        .as_ref()
        .map(NetworkLease::stop_processes)
        .transpose();
    let stop_result = match child.try_wait() {
        Ok(Some(_)) => Ok(()),
        Ok(None) => child.kill().and_then(|()| child.wait().map(|_| ())),
        Err(error) => Err(error),
    };
    let _ = fs::remove_file(output.join("guest-rootfs.ext4"));
    let network_cleanup = network.as_mut().map(NetworkLease::cleanup).transpose();
    manifest.network_fixture = network.as_ref().map(|lease| lease.receipt().clone());
    manifest.elapsed_ms = Some(run_started.elapsed().as_millis());
    if let Err(error) = stop_result {
        return Err(format!("could not stop and reap Firecracker: {error}").into());
    }
    namespace_stop?;
    network_cleanup?;
    match vm_result {
        Ok(response) => {
            manifest.guest_report = Some(response.guest);
            manifest.guest_exit_code = response.exit_code;
            manifest.guest_elapsed_ms = Some(response.elapsed_ms);
            let passed = response.status == "passed"
                && response.exit_code == Some(0)
                && response.error.is_none();
            if passed { "passed" } else { "failed" }.clone_into(&mut manifest.status);
            if !passed {
                manifest.error = Some(response.error.unwrap_or_else(|| {
                    format!(
                        "guest reported status={} and exit_code={:?}",
                        response.status, response.exit_code
                    )
                }));
            }
            Ok(())
        }
        Err(error) => Err(error),
    }
}

fn api_request(
    socket: &Path,
    method: &str,
    path: &str,
    body: &serde_json::Value,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut stream = UnixStream::connect(socket)?;
    stream.set_read_timeout(Some(Duration::from_secs(10)))?;
    let body = serde_json::to_vec(body)?;
    write!(
        stream,
        "{method} {path} HTTP/1.1\r\nHost: localhost\r\nAccept: application/json\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        body.len()
    )?;
    stream.write_all(&body)?;
    stream.flush()?;
    let mut response = BufReader::new(stream);
    let mut status_line = String::new();
    response.read_line(&mut status_line)?;
    let status = status_line.split_whitespace().nth(1).unwrap_or_default();
    if !status.starts_with('2') {
        let mut remainder = String::new();
        let _ = response.read_to_string(&mut remainder);
        return Err(
            format!("Firecracker API {method} {path} returned {status_line}{remainder}").into(),
        );
    }
    Ok(())
}

fn api_get(socket: &Path, path: &str) -> Result<serde_json::Value, Box<dyn std::error::Error>> {
    let mut stream = UnixStream::connect(socket)?;
    stream.set_read_timeout(Some(Duration::from_secs(10)))?;
    write!(
        stream,
        "GET {path} HTTP/1.1\r\nHost: localhost\r\nAccept: application/json\r\nConnection: close\r\n\r\n"
    )?;
    stream.flush()?;
    let mut response = BufReader::new(stream);
    let mut status_line = String::new();
    response.read_line(&mut status_line)?;
    let status = status_line.split_whitespace().nth(1).unwrap_or_default();
    let mut content_length = None;
    let mut header_bytes = status_line.len();
    loop {
        let mut header = String::new();
        if response.read_line(&mut header)? == 0 {
            return Err("Firecracker API response ended before HTTP headers".into());
        }
        header_bytes += header.len();
        if header_bytes > 8 * 1024 {
            return Err("Firecracker API response headers exceed 8 KiB".into());
        }
        if header == "\r\n" {
            break;
        }
        if let Some((name, value)) = header.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                content_length = Some(value.trim().parse::<usize>()?);
            }
        }
    }
    let content_length = content_length.ok_or("Firecracker API omitted Content-Length")?;
    if content_length > 64 * 1024 {
        return Err("Firecracker API response body exceeds 64 KiB".into());
    }
    let mut body = vec![0_u8; content_length];
    response.read_exact(&mut body)?;
    if status != "200" {
        return Err(format!(
            "Firecracker API GET {path} returned {status}: {}",
            String::from_utf8_lossy(&body)
        )
        .into());
    }
    Ok(serde_json::from_slice(&body)?)
}

fn wait_for_socket(
    socket: &Path,
    child: &mut Child,
    timeout: Duration,
) -> Result<(), Box<dyn std::error::Error>> {
    let started = Instant::now();
    while started.elapsed() < timeout {
        if socket.exists() {
            return Ok(());
        }
        if let Some(status) = child.try_wait()? {
            return Err(
                format!("Firecracker exited before creating its API socket: {status}").into(),
            );
        }
        thread::sleep(Duration::from_millis(50));
    }
    Err(format!(
        "timed out waiting for Firecracker API socket {}",
        socket.display()
    )
    .into())
}

fn connect_guest_vsock(
    socket: &Path,
    child: &mut Child,
    timeout: Duration,
) -> Result<UnixStream, Box<dyn std::error::Error>> {
    let started = Instant::now();
    let mut last_error = None;
    while started.elapsed() < timeout {
        if let Some(status) = child.try_wait()? {
            return Err(
                format!("Firecracker exited before guest agent readiness: {status}").into(),
            );
        }
        match try_connect_guest_vsock(socket) {
            Ok(stream) => return Ok(stream),
            Err(error) => last_error = Some(error.to_string()),
        }
        thread::sleep(Duration::from_millis(100));
    }
    Err(format!(
        "timed out connecting to guest vsock {}; last error: {last_error:?}",
        socket.display()
    )
    .into())
}

fn try_connect_guest_vsock(socket: &Path) -> io::Result<UnixStream> {
    // Host-initiated Firecracker connections use the base UDS path. The
    // uds_path_PORT socket is for guest-initiated connections to a host server.
    let mut stream = UnixStream::connect(socket)?;
    stream.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream.set_write_timeout(Some(Duration::from_secs(2)))?;
    writeln!(stream, "CONNECT {GUEST_VSOCK_PORT}")?;
    stream.flush()?;
    let mut response = Vec::with_capacity(32);
    loop {
        if response.len() >= 64 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Firecracker vsock acknowledgement is too long",
            ));
        }
        let mut byte = [0_u8; 1];
        stream.read_exact(&mut byte)?;
        if byte[0] == b'\n' {
            break;
        }
        response.push(byte[0]);
    }
    let response = String::from_utf8(response).map_err(io::Error::other)?;
    if response
        .strip_prefix("OK ")
        .and_then(|port| port.parse::<u32>().ok())
        .is_none()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("unexpected Firecracker vsock acknowledgement: {response:?}"),
        ));
    }
    stream.set_read_timeout(None)?;
    stream.set_write_timeout(None)?;
    Ok(stream)
}

fn debugfs_read(image: &Path, guest_path: &str) -> Result<String, Box<dyn std::error::Error>> {
    let output = Command::new("debugfs")
        .arg("-R")
        .arg(format!("cat {guest_path}"))
        .arg(image)
        .output()?;
    if !output.status.success() {
        return Err(format!(
            "debugfs could not read {guest_path}: {}",
            String::from_utf8_lossy(&output.stderr)
        )
        .into());
    }
    Ok(String::from_utf8(output.stdout)?)
}

fn debugfs_write(
    image: &Path,
    host_path: &Path,
    guest_path: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    debugfs_command(image, format!("write {} {guest_path}", host_path.display()))
}

fn debugfs_dump(
    image: &Path,
    guest_path: &str,
    host_path: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    debugfs_command(image, format!("dump {guest_path} {}", host_path.display()))
}

fn debugfs_set_mode(image: &Path, guest_path: &str) -> Result<(), Box<dyn std::error::Error>> {
    debugfs_command(image, format!("set_inode_field {guest_path} mode 0100755"))
}

fn debugfs_symlink(
    image: &Path,
    guest_path: &str,
    target: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    debugfs_command(image, format!("symlink {guest_path} {target}"))
}

fn debugfs_command(image: &Path, operation: String) -> Result<(), Box<dyn std::error::Error>> {
    let output = Command::new("debugfs")
        .args(["-w", "-R"])
        .arg(operation)
        .arg(image)
        .output()?;
    if !output.status.success() {
        return Err(format!(
            "debugfs failed: {}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )
        .into());
    }
    Ok(())
}

fn find_executable(name: &str) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let path = env::var_os("PATH").ok_or("PATH is unset")?;
    for directory in env::split_paths(&path) {
        let candidate = directory.join(name);
        if candidate.is_file() {
            return Ok(candidate);
        }
    }
    Err(format!("required executable {name:?} is not on PATH").into())
}

fn verify_file(path: &Path, expected: &str) -> Result<String, Box<dyn std::error::Error>> {
    if !path.is_file() {
        return Err(format!(
            "pinned artifact is missing: {} (expected sha256 {expected})",
            path.display()
        )
        .into());
    }
    let actual = hash_file(path)?;
    if actual != expected {
        return Err(format!(
            "artifact checksum mismatch for {}: expected {expected}, found {actual}",
            path.display()
        )
        .into());
    }
    Ok(actual)
}

fn hash_file(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn pin_string(name: &str) -> String {
    PINS.lines()
        .filter_map(|line| line.split_once('='))
        .find_map(|(key, value)| {
            (key.trim() == name).then(|| value.trim().trim_matches('"').to_owned())
        })
        .unwrap_or_else(|| panic!("missing required pin {name:?}"))
}

fn pin_path(name: &str) -> PathBuf {
    PathBuf::from(pin_string(name))
}

fn guest_agent_path() -> Result<PathBuf, Box<dyn std::error::Error>> {
    if let Some(path) = env::var_os("FC_GUEST_AGENT_PATH") {
        return Ok(PathBuf::from(path));
    }
    let sibling = env::current_exe()?.with_file_name("fc-guest-agent");
    if sibling.is_file() {
        return Ok(sibling);
    }
    Ok(benchmark_repo_from_env_or_cwd()?
        .join("firecracker-harness/target/x86_64-unknown-linux-musl/release/fc-guest-agent"))
}

#[expect(
    unsafe_code,
    reason = "KVM_GET_API_VERSION is a Linux ioctl on an open KVM descriptor"
)]
fn kvm_api_version() -> io::Result<i32> {
    let device = OpenOptions::new().read(true).write(true).open("/dev/kvm")?;
    // SAFETY: KVM_GET_API_VERSION takes no pointer argument and only reads the open KVM fd.
    let result = unsafe { libc::ioctl(device.as_raw_fd(), 0xAE00) };
    if result < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(result)
    }
}

#[expect(
    unsafe_code,
    reason = "Linux affinity query writes an initialized cpu_set_t through libc FFI"
)]
fn allowed_cpus() -> io::Result<Vec<usize>> {
    // SAFETY: zero-initialized cpu_set_t is a valid destination for sched_getaffinity.
    let mut set = unsafe { std::mem::zeroed::<libc::cpu_set_t>() };
    // SAFETY: `set` is writable for the exact cpu_set_t size and pid 0 means this process.
    let result =
        unsafe { libc::sched_getaffinity(0, std::mem::size_of::<libc::cpu_set_t>(), &raw mut set) };
    if result != 0 {
        return Err(io::Error::last_os_error());
    }
    let mut cpus = Vec::new();
    for cpu in 0..libc::CPU_SETSIZE as usize {
        // SAFETY: cpu is bounded by CPU_SETSIZE and `set` is initialized by sched_getaffinity.
        if unsafe { libc::CPU_ISSET(cpu, &set) } {
            cpus.push(cpu);
        }
    }
    Ok(cpus)
}

fn host_available_memory_bytes() -> io::Result<u64> {
    let contents = fs::read_to_string("/proc/meminfo")?;
    let kib = contents
        .lines()
        .find_map(|line| {
            line.strip_prefix("MemAvailable:")?
                .split_whitespace()
                .next()?
                .parse::<u64>()
                .ok()
        })
        .ok_or_else(|| io::Error::other("MemAvailable is missing from /proc/meminfo"))?;
    Ok(kib.saturating_mul(1024))
}

fn host_swap_active() -> io::Result<bool> {
    let contents = fs::read_to_string("/proc/swaps")?;
    Ok(contents.lines().skip(1).any(|line| !line.trim().is_empty()))
}

fn host_cpu_model() -> io::Result<String> {
    let contents = fs::read_to_string("/proc/cpuinfo")?;
    Ok(contents
        .lines()
        .find_map(|line| {
            line.strip_prefix("model name\t: ")
                .or_else(|| line.strip_prefix("Hardware\t: "))
        })
        .unwrap_or("unknown")
        .to_owned())
}

fn filesystem_type(path: &Path) -> io::Result<String> {
    let output = Command::new("stat")
        .args(["-f", "-c", "%T"])
        .arg(path)
        .output()?;
    if output.status.success() {
        Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
    } else {
        Ok("unknown".to_owned())
    }
}

#[derive(Debug, Deserialize, Serialize)]
struct RepositoryIdentity {
    path: String,
    head: String,
    dirty: bool,
    tracked_diff_sha256: String,
    untracked_files: BTreeMap<String, String>,
    identity_sha256: String,
}

fn repository_identity(path: &Path) -> Result<RepositoryIdentity, Box<dyn std::error::Error>> {
    let head = git_output(path, &["rev-parse", "HEAD"])?;
    let tracked_diff = git_bytes(path, &["diff", "HEAD", "--binary"])?;
    let status = git_bytes(
        path,
        &["status", "--porcelain=v1", "-z", "--untracked-files=all"],
    )?;
    let mut untracked_files = BTreeMap::new();
    for entry in status
        .split(|byte| *byte == 0)
        .filter(|entry| !entry.is_empty())
    {
        if entry.starts_with(b"?? ") {
            let relative = std::str::from_utf8(&entry[3..])?;
            untracked_files.insert(relative.to_owned(), hash_file(&path.join(relative))?);
        }
    }
    let tracked_hash = format!("{:x}", Sha256::digest(&tracked_diff));
    let dirty = !status.is_empty();
    let mut identity = Sha256::new();
    identity.update(head.as_bytes());
    identity.update(tracked_hash.as_bytes());
    for (relative, digest) in &untracked_files {
        identity.update(relative.as_bytes());
        identity.update(digest.as_bytes());
    }
    Ok(RepositoryIdentity {
        path: path.canonicalize()?.display().to_string(),
        head,
        dirty,
        tracked_diff_sha256: tracked_hash,
        untracked_files,
        identity_sha256: format!("{:x}", identity.finalize()),
    })
}

fn git_output(path: &Path, args: &[&str]) -> Result<String, Box<dyn std::error::Error>> {
    let output = git_bytes(path, args)?;
    Ok(String::from_utf8(output)?.trim().to_owned())
}

fn git_bytes(path: &Path, args: &[&str]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let output = Command::new("git")
        .arg("-C")
        .arg(path)
        .args(args)
        .output()?;
    if !output.status.success() {
        return Err(format!(
            "git -C {} {} failed: {}",
            path.display(),
            args.join(" "),
            String::from_utf8_lossy(&output.stderr)
        )
        .into());
    }
    Ok(output.stdout)
}

fn write_manifest(output: &Path, manifest: &RunManifest) -> io::Result<()> {
    let temporary = output.join("run-manifest.json.tmp");
    let destination = output.join("run-manifest.json");
    let mut file = File::create(temporary)?;
    serde_json::to_writer_pretty(&mut file, manifest).map_err(io::Error::other)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    fs::rename(output.join("run-manifest.json.tmp"), destination)
}

fn write_pid(output: &Path, pid: u32) -> io::Result<()> {
    fs::write(output.join("firecracker.pid"), format!("{pid}\n"))
}

fn unix_millis() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_millis())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::net::UnixListener;
    use std::sync::mpsc;

    #[test]
    fn supplemental_payload_cli_rejects_unsafe_and_duplicate_guest_names() {
        let benchmark_repo = tempfile::tempdir().unwrap();
        fs::create_dir_all(benchmark_repo.path().join("firecracker-harness")).unwrap();
        fs::create_dir_all(benchmark_repo.path().join("bench")).unwrap();
        fs::write(
            benchmark_repo.path().join("firecracker-harness/Cargo.toml"),
            "[workspace]\n",
        )
        .unwrap();
        let base = [
            "--benchmark-repo",
            benchmark_repo.path().to_str().unwrap(),
            "--subject-repo",
            "/tmp/subject",
            "--program",
            "/tmp/driver",
            "--run-id",
            "payload-test",
        ];
        let parse = |extra: &[&str]| {
            base.iter()
                .chain(extra)
                .map(|value| (*value).to_owned())
                .collect::<Vec<_>>()
        };
        let accepted =
            parse_run_args(&parse(&["--payload-file", "/tmp/server binary:tb-server"])).unwrap();
        assert_eq!(accepted.payload_files[0].1, "tb-server");
        assert_eq!(
            accepted.benchmark_repo,
            fs::canonicalize(benchmark_repo.path()).unwrap()
        );
        assert_eq!(
            default_output_path(&accepted.benchmark_repo, &accepted.run_id),
            benchmark_repo
                .path()
                .join("bench/firecracker-harness/payload-test")
        );
        assert!(parse_run_args(&parse(&["--payload-file", "/tmp/server:../tb-server"])).is_err());
        assert!(
            parse_run_args(&parse(&[
                "--payload-file",
                "/tmp/server:tb-server",
                "--payload-file",
                "/tmp/other:tb-server",
            ]))
            .is_err()
        );
    }

    #[test]
    fn primary_program_snapshot_is_independent_of_later_source_changes() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("program");
        fs::write(&source, b"original executable bytes").unwrap();
        fs::set_permissions(&source, fs::Permissions::from_mode(0o755)).unwrap();
        let snapshot = ProgramSnapshot::create(&source).unwrap();
        fs::write(&source, b"replacement bytes").unwrap();
        assert_eq!(snapshot.sha256, hash_file(snapshot.staged.path()).unwrap());
        assert_ne!(snapshot.sha256, hash_file(&source).unwrap());
        assert!(snapshot_executable(&source, 4).is_err());
        let symlink = directory.path().join("symlink");
        std::os::unix::fs::symlink(&source, &symlink).unwrap();
        assert!(snapshot_executable(&symlink, MAX_PRIMARY_PAYLOAD_BYTES).is_err());
    }

    #[test]
    fn v2_preflight_rejects_unpinned_firecracker_before_execution() {
        let directory = tempfile::tempdir().unwrap();
        let executable = directory.path().join("firecracker");
        fs::write(&executable, b"not the reviewed Firecracker executable").unwrap();
        fs::set_permissions(&executable, fs::Permissions::from_mode(0o755)).unwrap();
        let profile = ImageProfile {
            manifest_path: directory.path().join("image-manifest.json"),
            manifest_sha256: String::new(),
            identity: "terrabase-noble-podman-v2".to_owned(),
            recipe_version: "2.0.0".to_owned(),
            recipe_sha256: String::new(),
            qualification_status: "verified-candidate".to_owned(),
            reproducibility_status: "single-build-unqualified".to_owned(),
            kernel_path: PathBuf::new(),
            kernel_sha256: String::new(),
            kernel_release: String::new(),
            rootfs_path: PathBuf::new(),
            rootfs_sha256: String::new(),
            rootfs_release: String::new(),
            ubuntu_signer: String::new(),
            apt_snapshot: String::new(),
            package_lock_sha256: String::new(),
            builder_derived_digest: String::new(),
            firecracker_version: "1.17.0".to_owned(),
            firecracker_sha256: Some(
                "99ad0f5cd0514a88aad0e9ae8cfdb3cc3b4ab9d190e1194602406c786b5de7a5".to_owned(),
            ),
        };
        let error =
            preflight(None, None, false, Some(&profile), Some(&executable), None).unwrap_err();
        assert!(error.to_string().contains("executable SHA-256"));
    }

    #[test]
    fn frozen_running_controller_can_be_executed() {
        let snapshot = HostArtifactSnapshot::controller().unwrap();
        assert_eq!(
            snapshot.sha256,
            hash_file(Path::new("/proc/self/exe")).unwrap()
        );
        let child = Command::new(snapshot.staged.path())
            .arg("--list")
            .output()
            .unwrap();
        assert!(child.status.success());
    }

    #[test]
    fn api_get_reads_content_length_without_waiting_for_eof() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("api.sock");
        let listener = UnixListener::bind(&path).unwrap();
        let (done_tx, done_rx) = mpsc::channel();
        let server = thread::spawn(move || {
            let (mut peer, _) = listener.accept().unwrap();
            let body = r#"{"vcpu_count":4,"mem_size_mib":8192,"smt":false}"#;
            write!(
                peer,
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n{body}",
                body.len()
            )
            .unwrap();
            peer.flush().unwrap();
            done_rx.recv_timeout(Duration::from_secs(2)).unwrap();
        });
        let config = api_get(&path, "/machine-config").unwrap();
        assert_eq!(config["vcpu_count"], 4);
        assert_eq!(config["mem_size_mib"], 8192);
        assert_eq!(config["smt"], false);
        done_tx.send(()).unwrap();
        server.join().unwrap();
    }

    #[test]
    fn host_initiates_vsock_on_base_socket_and_preserves_guest_data() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("vsock.sock");
        let listener = UnixListener::bind(&path).unwrap();
        let server = thread::spawn(move || {
            let (mut peer, _) = listener.accept().unwrap();
            let mut command = String::new();
            BufReader::new(peer.try_clone().unwrap())
                .read_line(&mut command)
                .unwrap();
            assert_eq!(command, "CONNECT 10077\n");
            peer.write_all(b"OK 49152\nready\n").unwrap();
        });
        let mut stream = try_connect_guest_vsock(&path).unwrap();
        let mut readiness = String::new();
        BufReader::new(&mut stream)
            .read_line(&mut readiness)
            .unwrap();
        assert_eq!(readiness, "ready\n");
        server.join().unwrap();
    }
}
