//! Constrained `RustFS` service/client fixture. All guest communication is over
//! per-VM private vsock; no host-side S3 endpoint or host-service fallback.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::os::unix::net::UnixStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use object::Endianness;
use object::elf;
use object::read::elf::{Dyn, ElfFile64, FileHeader, ProgramHeader};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use super::{
    AgentSnapshot, DEFAULT_WORKLOAD_TIMEOUT_SECS, FrozenPath, GUEST_AGENT_PATH, GUEST_AGENT_UNIT,
    GUEST_NETWORK_UNIT, GUEST_PAYLOAD_PATH, GuestReport, GuestRunResponse, HostArtifactSnapshot,
    INSTANCE_MEMORY_MIB, INSTANCE_VCPU_COUNT, ImageProfile, MAX_CONTROLLER_BYTES,
    MAX_FIRECRACKER_BYTES, MAX_GUEST_AGENT_BYTES, MAX_GUEST_ARCHIVE_BYTES, MAX_KERNEL_BYTES,
    MAX_PRIMARY_PAYLOAD_BYTES, MAX_WORKLOAD_TIMEOUT_SECS, ProgramSnapshot, RUN_TIMEOUT, RunArgs,
    RunManifest, SupplementalPayload, VSOCK_CONNECT_TIMEOUT, allowed_cpus, api_get, api_request,
    connect_guest_vsock, debugfs_command, debugfs_dump, debugfs_set_mode, debugfs_symlink,
    debugfs_write, default_output_path, filesystem_type, hash_file, host_available_memory_bytes,
    initial_manifest, preflight, snapshot_file, unix_millis, validate_benchmark_repo, verify_file,
    wait_for_socket,
};
use crate::pair_network::{PairNetwork, PairReceipt, PairRole};

const PINNED_RUSTFS_OCI: &str =
    "sha256:ba0a1b53e36f321c0d46f3867104abef169f7bc59c467c664ddac87e7ddc9a8b";
// Independently reviewed extraction of the exact regular layer-4 RustFS 1.0.0
// amd64 member from PINNED_RUSTFS_OCI. A receipt cannot pin its own binary.
const TRUSTED_RUSTFS_BINARY_SHA256: &str =
    "222eedc3d9baabf6516702d9fbf230270c3ca49b50f562d3461c96e2cc6ae6ad";
const TRUSTED_RUSTFS_BINARY_SIZE_BYTES: u64 = 264_596_736;
const MIN_PAIR_MEMORY_BYTES: u64 = 19_327_352_832;
const MAX_RUSTFS_BINARY_BYTES: u64 = 512 * 1024 * 1024;
const GIB: u64 = 1024 * 1024 * 1024;
// Covers archives, bounded /tmp snapshots on a shared filesystem, metadata, and slack.
const FIXTURE_OUTPUT_DISK_MARGIN_BYTES: u64 = 5 * GIB;
const FIXTURE_OUTPUT_ARCHIVE_BYTES: u64 = 3 * MAX_GUEST_ARCHIVE_BYTES;
const FIXTURE_OUTPUT_POST_STAGE_HEADROOM_BYTES: u64 = GIB / 2;
const FIXTURE_OUTPUT_POST_STAGE_BYTES: u64 =
    FIXTURE_OUTPUT_ARCHIVE_BYTES + FIXTURE_OUTPUT_POST_STAGE_HEADROOM_BYTES;
// Bound concurrent snapshots plus the largest temporary verification dump.
const FIXTURE_TMP_VERIFICATION_DUMP_BYTES: u64 = MAX_RUSTFS_BINARY_BYTES;
const FIXTURE_TMP_HEADROOM_BYTES: u64 = GIB / 2;
const FIXTURE_TMP_PRE_STAGE_BYTES: u64 = MAX_RUSTFS_BINARY_BYTES
    + 2 * MAX_PRIMARY_PAYLOAD_BYTES
    + MAX_GUEST_AGENT_BYTES
    + MAX_CONTROLLER_BYTES
    + MAX_FIRECRACKER_BYTES
    + MAX_KERNEL_BYTES
    + FIXTURE_TMP_VERIFICATION_DUMP_BYTES
    + FIXTURE_TMP_HEADROOM_BYTES;
const FIXTURE_TMP_POST_STAGE_BYTES: u64 = GIB / 2;
const MAX_OCI_BLOB_BYTES: u64 = 4 * 1024 * 1024 * 1024;
const MAX_OCI_METADATA_BYTES: u64 = 4 * 1024 * 1024;
const MAX_EXTRACTION_EVIDENCE_BYTES: u64 = 4 * 1024 * 1024;
const MAX_CANDIDATE_RECEIPT_BYTES: u64 = 4 * 1024 * 1024;
const CLIENT_FILTER: &str =
    "object_slow::rustfs_proves_strict_range_and_abort_after_one_uploaded_part";
const ADMIN_GUEST_PATH: &str = "/root/fc-harness-payloads/rustfs-bucket-admin";

#[derive(Debug)]
struct FixtureArgs {
    benchmark_repo: PathBuf,
    subject_repo: PathBuf,
    client_program: PathBuf,
    admin_program: PathBuf,
    artifact_receipt: PathBuf,
    image_profile: PathBuf,
    run_id: String,
    output: Option<PathBuf>,
    timeout_seconds: u64,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct PinnedRustfsCandidate {
    schema_version: u32,
    status: String,
    source_oci_digest: String,
    platform_manifest_digest: String,
    platform_manifest_media_type: String,
    platform_os: String,
    platform_architecture: String,
    config_digest: String,
    layer_digests: Vec<String>,
    oci_layout_path: PathBuf,
    oci_layout_inventory_path: PathBuf,
    oci_layout_inventory_sha256: String,
    extraction_member_path: String,
    extraction_command: Vec<String>,
    extraction_tool_path: PathBuf,
    extraction_tool_sha256: String,
    binary_path: PathBuf,
    binary_sha256: String,
    binary_size_bytes: u64,
    service_port: u16,
    service_args: Vec<String>,
    service_invocation_status: String,
    service_config_evidence_path: PathBuf,
    service_config_evidence_sha256: String,
    access_key_env: String,
    secret_key_env: String,
    evidence_path: PathBuf,
    evidence_sha256: String,
}

#[allow(
    clippy::struct_excessive_bools,
    reason = "the persisted receipt keeps independent caller, network, and cleanup stage facts explicit"
)]
#[derive(Serialize)]
struct FixtureManifest {
    schema_version: u32,
    run_id: String,
    status: String,
    started_unix_ms: u128,
    finished_unix_ms: Option<u128>,
    base: RunManifest,
    artifact_receipt_path: String,
    artifact_receipt_snapshot_path: String,
    artifact_receipt_sha256: String,
    artifact_binary_sha256: String,
    bucket_admin_sha256: String,
    bucket_name: Option<String>,
    service_preboot_rootfs_sha256: Option<String>,
    client_preboot_rootfs_sha256: Option<String>,
    pair_network: Option<PairReceipt>,
    service_guest: Option<GuestReport>,
    client_guest: Option<GuestReport>,
    tcp_reachable: bool,
    authenticated_s3_ready: bool,
    bucket_created: bool,
    bucket_deleted: bool,
    client_exit_code: Option<i32>,
    error: Option<String>,
}

#[expect(
    clippy::struct_excessive_bools,
    reason = "the fixture receipt records independent S3, caller, and cleanup observations"
)]
#[derive(Serialize)]
struct ObservedRustfsReceipt {
    schema_version: u32,
    status: &'static str,
    run_id: String,
    fixture_manifest_sha256: String,
    candidate_receipt_sha256: String,
    staged_binary_sha256: String,
    image_profile_sha256: String,
    image_qualification_status: String,
    image_reproducibility_status: String,
    kernel_sha256: String,
    rootfs_sha256: String,
    service_preboot_rootfs_sha256: String,
    client_preboot_rootfs_sha256: String,
    controller_sha256: String,
    firecracker_sha256: String,
    guest_agent_sha256: String,
    client_program_sha256: String,
    bucket_admin_sha256: String,
    real_caller_archive_sha256: String,
    bucket_create_archive_sha256: String,
    bucket_delete_archive_sha256: String,
    service_guest: GuestReport,
    client_guest: GuestReport,
    pair_network_status: String,
    authenticated_s3_ready: bool,
    bucket_created: bool,
    bucket_deleted: bool,
    tcp_reachable: bool,
    client_exit_code: i32,
    finished_unix_ms: u128,
}

#[derive(Deserialize)]
struct FixtureOperationResponse {
    status: String,
    exit_code: Option<i32>,
}

struct RunningVm {
    child: Child,
    vsock_socket: PathBuf,
    guest: Option<GuestReport>,
}

impl Drop for RunningVm {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
        }
        let _ = self.child.wait();
    }
}

#[allow(
    clippy::too_many_lines,
    reason = "setup persists each captured prerequisite before entering the ordered two-VM lifecycle"
)]
pub fn run(raw: &[String]) -> Result<(), Box<dyn std::error::Error>> {
    let args = parse_args(raw)?;
    let (artifact, artifact_receipt_bytes, artifact_receipt_sha256) =
        load_artifact(&args.artifact_receipt)?;
    let profile = ImageProfile::load(&args.image_profile, &args.benchmark_repo)?;
    let output = args
        .output
        .clone()
        .unwrap_or_else(|| default_output_path(&args.benchmark_repo, &args.run_id));
    if output.exists() {
        return Err(format!("fixture output already exists: {}", output.display()).into());
    }

    let cpus = allowed_cpus()?;
    if cpus.len() < 8 {
        return Err("RustFS fixture requires eight distinct allowed host CPUs".into());
    }
    let service_cpus: [usize; 4] = cpus[0..4].try_into()?;
    let client_cpus: [usize; 4] = cpus[4..8].try_into()?;
    let rootfs_bytes = fs::metadata(&profile.rootfs_path)?.len();
    let output_parent = output
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let output_filesystem_path = existing_filesystem_directory(output_parent)?;
    check_initial_fixture_disk_space(&profile.rootfs_path, &output_filesystem_path, rootfs_bytes)?;
    ensure_pair_memory(MIN_PAIR_MEMORY_BYTES, "before fixture staging")?;

    let client_snapshot = ProgramSnapshot::create(&args.client_program)?;
    let admin_snapshot = ProgramSnapshot::create(&args.admin_program)?;
    let (service_staged, service_sha, service_bytes) =
        snapshot_file(&artifact.binary_path, MAX_RUSTFS_BINARY_BYTES, true, 0o400)?;
    if service_sha != artifact.binary_sha256 || service_bytes != artifact.binary_size_bytes {
        return Err("RustFS service binary differs from pinned candidate".into());
    }
    verify_guest_elf(service_staged.path())?;
    let agent_snapshot = AgentSnapshot::create()?;
    let controller_snapshot = HostArtifactSnapshot::controller()?;
    let firecracker_snapshot = HostArtifactSnapshot::firecracker()?;
    let kernel_snapshot =
        HostArtifactSnapshot::kernel(&profile.kernel_path, &profile.kernel_sha256)?;
    verify_file(&profile.rootfs_path, &profile.rootfs_sha256)?;
    preflight(
        Some(kernel_snapshot.staged.path()),
        Some(&profile.rootfs_path),
        false,
        Some(&profile),
        Some(firecracker_snapshot.staged.path()),
        Some(agent_snapshot.staged.path()),
    )?;

    fs::create_dir_all(output_parent)?;
    fs::create_dir(&output)?;
    let output = fs::canonicalize(output)?;
    let artifact_receipt_snapshot = output.join("rustfs-candidate-receipt.json");
    let mut receipt_file = File::create_new(&artifact_receipt_snapshot)?;
    receipt_file.write_all(&artifact_receipt_bytes)?;
    receipt_file.sync_all()?;
    File::open(&output)?.sync_all()?;
    let started = unix_millis();
    let base_args = RunArgs {
        benchmark_repo: args.benchmark_repo.clone(),
        subject_repo: args.subject_repo.clone(),
        program: args.client_program.clone(),
        run_id: args.run_id.clone(),
        output: Some(output.clone()),
        kernel: None,
        rootfs: None,
        guest_args: vec![CLIENT_FILTER.to_owned()],
        guest_program: None,
        timeout_seconds: args.timeout_seconds,
        isolated_network: true,
        image_profile: Some(args.image_profile.clone()),
        payload_files: Vec::new(),
    };
    let mut base = initial_manifest(
        &base_args,
        Some(&profile),
        &profile.kernel_path,
        &profile.rootfs_path,
        &output,
        started,
        false,
        Some(&client_snapshot),
        &agent_snapshot,
        &controller_snapshot,
        &firecracker_snapshot,
        &kernel_snapshot,
        &[],
    )?;
    "rustfs-two-vm-real-s3-caller".clone_into(&mut base.purpose);
    "private two-VM RustFS service/client bridge; no host uplink"
        .clone_into(&mut base.guest_network);
    base.host_cpu_affinity = client_cpus.to_vec();
    let mut manifest = FixtureManifest {
        schema_version: 1,
        run_id: args.run_id.clone(),
        status: "planned".to_owned(),
        started_unix_ms: started,
        finished_unix_ms: None,
        base,
        artifact_receipt_path: args.artifact_receipt.display().to_string(),
        artifact_receipt_snapshot_path: artifact_receipt_snapshot.display().to_string(),
        artifact_receipt_sha256,
        artifact_binary_sha256: service_sha.clone(),
        bucket_admin_sha256: admin_snapshot.sha256.clone(),
        bucket_name: None,
        service_preboot_rootfs_sha256: None,
        client_preboot_rootfs_sha256: None,
        pair_network: None,
        service_guest: None,
        client_guest: None,
        tcp_reachable: false,
        authenticated_s3_ready: false,
        bucket_created: false,
        bucket_deleted: false,
        client_exit_code: None,
        error: None,
    };
    write_fixture_manifest(&output, &manifest)?;
    let result = run_inner(
        &args,
        &artifact,
        &profile,
        &output,
        &mut manifest,
        &service_staged,
        &service_sha,
        &client_snapshot,
        &admin_snapshot,
        &agent_snapshot,
        &controller_snapshot,
        &firecracker_snapshot,
        &kernel_snapshot,
        service_cpus,
        client_cpus,
    );
    manifest.finished_unix_ms = Some(unix_millis());
    let status = if result.is_ok() { "passed" } else { "failed" };
    status.clone_into(&mut manifest.status);
    if let Err(error) = &result {
        manifest.error = Some(error.to_string());
    }
    write_fixture_manifest(&output, &manifest)?;
    result?;
    if let Err(error) = write_observed_receipt(&output, &manifest) {
        "failed".clone_into(&mut manifest.status);
        manifest.error = Some(error.to_string());
        write_fixture_manifest(&output, &manifest)?;
        return Err(error);
    }
    println!(
        "RustFS fixture {} passed; receipt: {}",
        args.run_id,
        output.join("rustfs-fixture-manifest.json").display()
    );
    Ok(())
}

#[derive(Debug)]
struct FixtureFilesystemSpace {
    filesystem_type: String,
    available_bytes: u64,
}

fn existing_filesystem_directory(path: &Path) -> io::Result<PathBuf> {
    let mut candidate = path;
    loop {
        match fs::metadata(candidate) {
            Ok(metadata) if metadata.is_dir() => return fs::canonicalize(candidate),
            Ok(_) => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    format!(
                        "filesystem path ancestor is not a directory: {}",
                        candidate.display()
                    ),
                ));
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                candidate = candidate
                    .parent()
                    .filter(|parent| !parent.as_os_str().is_empty())
                    .unwrap_or(Path::new("."));
            }
            Err(error) => return Err(error),
        }
    }
}

fn fixture_filesystem_space(
    path: &Path,
    purpose: &str,
) -> Result<FixtureFilesystemSpace, Box<dyn std::error::Error>> {
    let helper_filesystem_type = filesystem_type(path)?;
    if helper_filesystem_type.trim().is_empty()
        || helper_filesystem_type.eq_ignore_ascii_case("unknown")
    {
        return Err(format!(
            "cannot identify the filesystem backing {purpose} path {}",
            path.display()
        )
        .into());
    }

    let output = Command::new("stat")
        .args(["-f", "-c", "%T %a %S", "--"])
        .arg(path)
        .env("LC_ALL", "C")
        .output()
        .map_err(|error| {
            io::Error::other(format!(
                "cannot query available space for {purpose} path {}: {error}",
                path.display()
            ))
        })?;
    if !output.status.success() {
        return Err(format!(
            "stat failed for {purpose} path {} with {}: {}",
            path.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )
        .into());
    }

    let stdout = String::from_utf8(output.stdout)?;
    let mut fields = stdout.split_whitespace();
    let reported_filesystem_type = fields
        .next()
        .ok_or("stat filesystem report has no filesystem type")?;
    let available_blocks = fields
        .next()
        .ok_or("stat filesystem report has no available-block count")?
        .parse::<u64>()?;
    let block_size = fields
        .next()
        .ok_or("stat filesystem report has no block size")?
        .parse::<u64>()?;
    if fields.next().is_some() || block_size == 0 {
        return Err(format!(
            "stat filesystem report is malformed for {purpose} path {}",
            path.display()
        )
        .into());
    }
    if !reported_filesystem_type.eq_ignore_ascii_case(&helper_filesystem_type) {
        return Err(format!(
            "filesystem identity changed while checking {purpose} path {}: {} versus {}",
            path.display(),
            helper_filesystem_type,
            reported_filesystem_type
        )
        .into());
    }

    // Reject known memory-backed/pseudo types; the filesystem type does not
    // prove physical backing (for example, a conventional FS on zram).
    let normalized_type = reported_filesystem_type.to_ascii_lowercase();
    if matches!(
        normalized_type.as_str(),
        "unknown"
            | "tmpfs"
            | "ramfs"
            | "hugetlbfs"
            | "devtmpfs"
            | "overlay"
            | "overlayfs"
            | "proc"
            | "sysfs"
            | "cgroup"
            | "cgroup2"
            | "bpf"
            | "debugfs"
            | "securityfs"
            | "tracefs"
            | "pstore"
            | "fusectl"
    ) {
        return Err(format!(
            "RustFS fixture requires disk-backed storage; {purpose} path {} is on {reported_filesystem_type}",
            path.display()
        )
        .into());
    }

    let available_bytes = available_blocks
        .checked_mul(block_size)
        .ok_or("filesystem available-space byte count overflow")?;
    Ok(FixtureFilesystemSpace {
        filesystem_type: reported_filesystem_type.to_owned(),
        available_bytes,
    })
}

fn require_fixture_space(
    filesystem: &FixtureFilesystemSpace,
    path: &Path,
    purpose: &str,
    required_bytes: u64,
) -> Result<(), Box<dyn std::error::Error>> {
    if filesystem.available_bytes < required_bytes {
        return Err(format!(
            "RustFS fixture needs {required_bytes} free bytes on {purpose} filesystem {} at {}; only {} are available",
            filesystem.filesystem_type,
            path.display(),
            filesystem.available_bytes
        )
        .into());
    }
    Ok(())
}

fn check_initial_fixture_disk_space(
    rootfs_path: &Path,
    output_filesystem_path: &Path,
    rootfs_bytes: u64,
) -> Result<(), Box<dyn std::error::Error>> {
    let rootfs_path = fs::canonicalize(rootfs_path)?;
    fixture_filesystem_space(&rootfs_path, "rootfs source")?;
    let output_space = fixture_filesystem_space(output_filesystem_path, "output")?;
    let temporary_path = fs::canonicalize("/tmp")?;
    let temporary_space = fixture_filesystem_space(&temporary_path, "temporary staging")?;

    let rootfs_copy_bytes = rootfs_bytes
        .checked_mul(2)
        .ok_or("RustFS fixture rootfs disk requirement overflow")?;
    let output_required_bytes = rootfs_copy_bytes
        .checked_add(FIXTURE_OUTPUT_DISK_MARGIN_BYTES)
        .ok_or("RustFS fixture output disk requirement overflow")?;
    require_fixture_space(
        &output_space,
        output_filesystem_path,
        "output",
        output_required_bytes,
    )?;
    require_fixture_space(
        &temporary_space,
        &temporary_path,
        "temporary staging",
        FIXTURE_TMP_PRE_STAGE_BYTES,
    )
}

fn ensure_pair_memory(required_bytes: u64, stage: &str) -> Result<(), Box<dyn std::error::Error>> {
    let available_bytes = host_available_memory_bytes()?;
    if available_bytes < required_bytes {
        return Err(format!(
            "RustFS fixture needs {required_bytes} MemAvailable bytes {stage}; host has {available_bytes}"
        )
        .into());
    }
    Ok(())
}

fn recheck_fixture_capacity_before_boot(output: &Path) -> Result<(), Box<dyn std::error::Error>> {
    ensure_pair_memory(MIN_PAIR_MEMORY_BYTES, "before the first VM boot")?;
    let output_space = fixture_filesystem_space(output, "output")?;
    require_fixture_space(
        &output_space,
        output,
        "output",
        FIXTURE_OUTPUT_POST_STAGE_BYTES,
    )?;
    let temporary_path = fs::canonicalize("/tmp")?;
    let temporary_space = fixture_filesystem_space(&temporary_path, "temporary staging")?;
    require_fixture_space(
        &temporary_space,
        &temporary_path,
        "temporary staging",
        FIXTURE_TMP_POST_STAGE_BYTES,
    )
}

fn parse_args(raw: &[String]) -> Result<FixtureArgs, Box<dyn std::error::Error>> {
    if raw.len() % 2 != 0 {
        return Err("rustfs-fixture options require values".into());
    }
    let mut values = std::collections::BTreeMap::new();
    for pair in raw.chunks_exact(2) {
        if !pair[0].starts_with("--") || values.insert(pair[0].as_str(), pair[1].as_str()).is_some()
        {
            return Err("invalid or duplicate rustfs-fixture option".into());
        }
    }
    let take = |name: &str| {
        values
            .get(name)
            .copied()
            .ok_or_else(|| format!("missing {name}"))
    };
    let benchmark_repo = validate_benchmark_repo(Path::new(take("--benchmark-repo")?))?;
    let run_id = take("--run-id")?.to_owned();
    if run_id.is_empty()
        || !run_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err("invalid RustFS fixture run ID".into());
    }
    let timeout_seconds = values
        .get("--timeout-seconds")
        .map_or(Ok(DEFAULT_WORKLOAD_TIMEOUT_SECS), |value| value.parse())?;
    if !(1..=MAX_WORKLOAD_TIMEOUT_SECS).contains(&timeout_seconds) {
        return Err("invalid RustFS caller timeout".into());
    }
    for key in values.keys() {
        if ![
            "--benchmark-repo",
            "--subject-repo",
            "--client-program",
            "--bucket-admin-program",
            "--artifact-receipt",
            "--image-profile",
            "--run-id",
            "--output",
            "--timeout-seconds",
        ]
        .contains(key)
        {
            return Err(format!("unknown rustfs-fixture option {key}").into());
        }
    }
    Ok(FixtureArgs {
        benchmark_repo,
        subject_repo: PathBuf::from(take("--subject-repo")?),
        client_program: PathBuf::from(take("--client-program")?),
        admin_program: PathBuf::from(take("--bucket-admin-program")?),
        artifact_receipt: PathBuf::from(take("--artifact-receipt")?),
        image_profile: PathBuf::from(take("--image-profile")?),
        run_id,
        output: values.get("--output").map(|value| PathBuf::from(*value)),
        timeout_seconds,
    })
}

#[expect(
    clippy::too_many_lines,
    reason = "candidate admission validates all provenance and service-input fields together"
)]
fn load_artifact(
    path: &Path,
) -> Result<(PinnedRustfsCandidate, Vec<u8>, String), Box<dyn std::error::Error>> {
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.file_type().is_file()
        || metadata.len() == 0
        || metadata.len() > MAX_CANDIDATE_RECEIPT_BYTES
    {
        return Err("RustFS artifact receipt must be a bounded regular file".into());
    }
    let mut bytes = Vec::new();
    file.take(MAX_CANDIDATE_RECEIPT_BYTES + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_CANDIDATE_RECEIPT_BYTES {
        return Err("RustFS candidate receipt changed beyond its bounded size".into());
    }
    let receipt_sha256 = format!("{:x}", Sha256::digest(&bytes));
    let receipt: PinnedRustfsCandidate = serde_json::from_slice(&bytes)?;
    if receipt.schema_version != 1
        || receipt.status != "pinned-candidate"
        || receipt.source_oci_digest != PINNED_RUSTFS_OCI
        || receipt.platform_manifest_digest != PINNED_RUSTFS_OCI
        || receipt.platform_manifest_media_type != "application/vnd.oci.image.manifest.v1+json"
        || receipt.platform_os != "linux"
        || receipt.platform_architecture != "amd64"
        || !valid_oci_digest(&receipt.config_digest)
        || receipt.layer_digests.len() != 7
        || !receipt
            .layer_digests
            .iter()
            .all(|digest| valid_oci_digest(digest))
        || !valid_sha256(&receipt.oci_layout_inventory_sha256)
        || !valid_sha256(&receipt.extraction_tool_sha256)
        || !valid_sha256(&receipt.binary_sha256)
        || !valid_sha256(&receipt.evidence_sha256)
        || !valid_sha256(&receipt.service_config_evidence_sha256)
        || receipt.binary_size_bytes == 0
        || receipt.binary_size_bytes > MAX_RUSTFS_BINARY_BYTES
        || receipt.service_port != 9000
        || receipt.service_args != ["/root/fc-bench-data"]
        || receipt.service_invocation_status != "untested-direct-elf"
        || receipt.access_key_env != "RUSTFS_ACCESS_KEY"
        || receipt.secret_key_env != "RUSTFS_SECRET_KEY"
        || receipt.service_args.len() > 64
        || receipt
            .service_args
            .iter()
            .any(|arg| arg.len() > 4_096 || arg.chars().any(char::is_control))
    {
        return Err("RustFS candidate lacks pinned platform and extraction identity".into());
    }
    if receipt.binary_sha256 != TRUSTED_RUSTFS_BINARY_SHA256
        || receipt.binary_size_bytes != TRUSTED_RUSTFS_BINARY_SIZE_BYTES
    {
        return Err("RustFS candidate binary differs from independently reviewed pin".into());
    }
    let member = Path::new(&receipt.extraction_member_path);
    if receipt.extraction_member_path.is_empty()
        || receipt.extraction_member_path.len() > 512
        || member.is_absolute()
        || member.components().any(|component| {
            matches!(
                component,
                std::path::Component::ParentDir | std::path::Component::Prefix(_)
            )
        })
        || receipt.extraction_command.is_empty()
        || receipt.extraction_command.len() > 32
        || !receipt
            .extraction_command
            .iter()
            .any(|arg| Some(arg.as_str()) == receipt.extraction_tool_path.to_str())
        || receipt
            .extraction_command
            .iter()
            .any(|arg| arg.is_empty() || arg.len() > 4_096 || arg.chars().any(char::is_control))
    {
        return Err("RustFS extraction command or member path is invalid".into());
    }
    for name in [&receipt.access_key_env, &receipt.secret_key_env] {
        if name.is_empty()
            || !name
                .bytes()
                .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || byte == b'_')
        {
            return Err("RustFS candidate credential environment key is invalid".into());
        }
    }
    if receipt.access_key_env == receipt.secret_key_env {
        return Err("RustFS credential environment keys overlap".into());
    }
    if [&receipt.access_key_env, &receipt.secret_key_env]
        .iter()
        .any(|key| ["PATH", "HOME", "TMPDIR"].contains(&key.as_str()))
        || receipt.service_args.iter().any(|arg| {
            let lower = arg.to_ascii_lowercase();
            ["access", "secret", "password", "credential"]
                .iter()
                .any(|word| lower.contains(word))
        })
    {
        return Err("RustFS service receipt attempts credential argv or reserved env use".into());
    }
    verify_oci_layout(&receipt)?;
    let binary_layer = oci_blob_path(&receipt.oci_layout_path, &receipt.layer_digests[3])?;
    let expected_command = [
        receipt.extraction_tool_path.to_string_lossy().into_owned(),
        "-xOzf".to_owned(),
        binary_layer.display().to_string(),
        receipt.extraction_member_path.clone(),
    ];
    if receipt.extraction_member_path != "usr/bin/rustfs"
        || !receipt
            .extraction_command
            .windows(expected_command.len())
            .any(|window| window == expected_command)
    {
        return Err("RustFS extraction argv does not select pinned layer-4 member".into());
    }
    verify_candidate_file(
        &receipt.extraction_tool_path,
        &receipt.extraction_tool_sha256,
        MAX_RUSTFS_BINARY_BYTES,
    )?;
    verify_candidate_file(
        &receipt.evidence_path,
        &receipt.evidence_sha256,
        MAX_EXTRACTION_EVIDENCE_BYTES,
    )?;
    verify_candidate_file(
        &receipt.service_config_evidence_path,
        &receipt.service_config_evidence_sha256,
        MAX_EXTRACTION_EVIDENCE_BYTES,
    )?;
    let binary_size = verify_candidate_file(
        &receipt.binary_path,
        &receipt.binary_sha256,
        MAX_RUSTFS_BINARY_BYTES,
    )?;
    if binary_size != receipt.binary_size_bytes {
        return Err("RustFS candidate binary size differs from extraction receipt".into());
    }
    Ok((receipt, bytes, receipt_sha256))
}

fn valid_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
}

fn valid_oci_digest(value: &str) -> bool {
    value.strip_prefix("sha256:").is_some_and(valid_sha256)
}

fn oci_blob_path(layout: &Path, digest: &str) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let hash = digest
        .strip_prefix("sha256:")
        .filter(|value| valid_sha256(value))
        .ok_or("invalid OCI blob digest")?;
    Ok(layout.join("blobs/sha256").join(hash))
}

fn verify_oci_layout(receipt: &PinnedRustfsCandidate) -> Result<(), Box<dyn std::error::Error>> {
    let layout = &receipt.oci_layout_path;
    if !layout.is_absolute() || !fs::symlink_metadata(layout)?.file_type().is_dir() {
        return Err("RustFS candidate OCI layout must be an absolute regular directory".into());
    }
    verify_candidate_file(
        &receipt.oci_layout_inventory_path,
        &receipt.oci_layout_inventory_sha256,
        MAX_OCI_METADATA_BYTES,
    )?;
    let manifest_path = oci_blob_path(layout, PINNED_RUSTFS_OCI)?;
    let manifest_bytes = read_verified_metadata(
        &manifest_path,
        PINNED_RUSTFS_OCI
            .strip_prefix("sha256:")
            .ok_or("invalid pinned OCI manifest digest")?,
    )?;
    let manifest: Value = serde_json::from_slice(&manifest_bytes)?;
    if manifest["schemaVersion"].as_u64() != Some(2)
        || manifest["mediaType"].as_str() != Some(receipt.platform_manifest_media_type.as_str())
        || manifest["config"]["digest"].as_str() != Some(receipt.config_digest.as_str())
    {
        return Err("RustFS candidate descriptors differ from pinned OCI manifest".into());
    }
    let layers = manifest["layers"]
        .as_array()
        .ok_or("pinned OCI manifest has no layer descriptors")?;
    if layers.len() != receipt.layer_digests.len()
        || layers
            .iter()
            .zip(&receipt.layer_digests)
            .any(|(layer, digest)| layer["digest"].as_str() != Some(digest.as_str()))
    {
        return Err("RustFS candidate layer order differs from pinned OCI manifest".into());
    }
    let config_path = oci_blob_path(layout, &receipt.config_digest)?;
    let config_bytes = read_verified_metadata(
        &config_path,
        receipt
            .config_digest
            .strip_prefix("sha256:")
            .ok_or("invalid OCI config digest")?,
    )?;
    let config: Value = serde_json::from_slice(&config_bytes)?;
    if config["os"].as_str() != Some(receipt.platform_os.as_str())
        || config["architecture"].as_str() != Some(receipt.platform_architecture.as_str())
    {
        return Err("RustFS candidate platform differs from pinned OCI config".into());
    }
    for digest in &receipt.layer_digests {
        let path = oci_blob_path(layout, digest)?;
        verify_candidate_file(
            &path,
            digest
                .strip_prefix("sha256:")
                .ok_or("invalid OCI layer digest")?,
            MAX_OCI_BLOB_BYTES,
        )?;
    }
    Ok(())
}

fn read_verified_metadata(
    path: &Path,
    expected_sha256: &str,
) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.file_type().is_file()
        || metadata.len() == 0
        || metadata.len() > MAX_OCI_METADATA_BYTES
    {
        return Err("OCI metadata blob is not a bounded regular file".into());
    }
    let mut bytes = Vec::new();
    file.take(MAX_OCI_METADATA_BYTES + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() as u64 != metadata.len()
        || format!("{:x}", Sha256::digest(&bytes)) != expected_sha256
    {
        return Err("OCI metadata blob differs from pinned digest or size".into());
    }
    Ok(bytes)
}

fn verify_candidate_file(
    path: &Path,
    expected_sha256: &str,
    max_bytes: u64,
) -> Result<u64, Box<dyn std::error::Error>> {
    if !path.is_absolute() {
        return Err("RustFS candidate evidence paths must be absolute".into());
    }
    let mut file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.file_type().is_file() || metadata.len() == 0 || metadata.len() > max_bytes {
        return Err("RustFS candidate evidence is not a bounded regular file".into());
    }
    let mut hash = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024];
    let mut read_bytes = 0_u64;
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        read_bytes = read_bytes.saturating_add(count as u64);
        if read_bytes > max_bytes {
            return Err("RustFS candidate evidence grew beyond its size bound".into());
        }
        hash.update(&buffer[..count]);
    }
    if read_bytes != metadata.len() || format!("{:x}", hash.finalize()) != expected_sha256 {
        return Err("RustFS candidate evidence hash or size differs from receipt".into());
    }
    Ok(metadata.len())
}

fn verify_guest_elf(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let bytes = fs::read(path)?;
    let file = ElfFile64::<Endianness>::parse(bytes.as_slice())?;
    let header = file.elf_header();
    let endian = file.endian();
    if header.e_ident().class != elf::ELFCLASS64
        || header.e_ident().data != elf::ELFDATA2LSB
        || header.e_machine(endian) != elf::EM_X86_64
        || ![elf::ET_EXEC, elf::ET_DYN].contains(&header.e_type(endian))
    {
        return Err("RustFS candidate is not a little-endian x86_64 ELF executable".into());
    }
    let segments = file.elf_program_headers();
    if segments.is_empty() || segments.len() > 256 {
        return Err("RustFS ELF program header count is invalid".into());
    }
    let mut executable_load = false;
    for segment in segments {
        let (offset, file_size) = segment.file_range(endian);
        if offset
            .checked_add(file_size)
            .is_none_or(|end| end > bytes.len() as u64)
            || segment.p_filesz(endian) > segment.p_memsz(endian)
        {
            return Err("RustFS ELF program header exceeds snapshotted binary".into());
        }
        if segment.p_type(endian) == elf::PT_INTERP {
            return Err("RustFS candidate requires a guest dynamic interpreter".into());
        }
        if segment.p_type(endian) == elf::PT_LOAD
            && segment.p_flags(endian) & elf::PF_X != 0
            && file_size > 0
        {
            executable_load = true;
        }
        if segment
            .dynamic(endian, bytes.as_slice())?
            .is_some_and(|entries| {
                entries
                    .iter()
                    .any(|entry| entry.tag32(endian) == Some(elf::DT_NEEDED))
            })
        {
            return Err("RustFS candidate requires an external guest shared object".into());
        }
    }
    if !executable_load {
        return Err("RustFS ELF has no executable load segment".into());
    }
    Ok(())
}

fn write_fixture_manifest(output: &Path, manifest: &FixtureManifest) -> io::Result<()> {
    let path = output.join("rustfs-fixture-manifest.json");
    let temporary = output.join("rustfs-fixture-manifest.json.tmp");
    let mut file = File::create(&temporary)?;
    serde_json::to_writer_pretty(&mut file, manifest).map_err(io::Error::other)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    fs::rename(temporary, path)?;
    File::open(output)?.sync_all()
}

fn write_observed_receipt(
    output: &Path,
    manifest: &FixtureManifest,
) -> Result<(), Box<dyn std::error::Error>> {
    let pair = manifest
        .pair_network
        .as_ref()
        .ok_or("RustFS observation lacks pair ownership receipt")?;
    if pair.status != "cleaned"
        || !manifest.tcp_reachable
        || !manifest.authenticated_s3_ready
        || !manifest.bucket_created
        || !manifest.bucket_deleted
        || manifest.client_exit_code != Some(0)
    {
        return Err(
            "RustFS functional candidate lacks complete caller and cleanup outcomes".into(),
        );
    }
    let profile = manifest
        .base
        .image_profile
        .as_ref()
        .ok_or("RustFS observation lacks verified image profile")?;
    let observed = ObservedRustfsReceipt {
        schema_version: 1,
        status: "functional-candidate-observed",
        run_id: manifest.run_id.clone(),
        fixture_manifest_sha256: hash_file(&output.join("rustfs-fixture-manifest.json"))?,
        candidate_receipt_sha256: manifest.artifact_receipt_sha256.clone(),
        staged_binary_sha256: manifest.artifact_binary_sha256.clone(),
        image_profile_sha256: profile.manifest_sha256.clone(),
        image_qualification_status: profile.qualification_status.clone(),
        image_reproducibility_status: profile.reproducibility_status.clone(),
        kernel_sha256: manifest.base.kernel_sha256.clone(),
        rootfs_sha256: manifest.base.rootfs_sha256.clone(),
        service_preboot_rootfs_sha256: manifest
            .service_preboot_rootfs_sha256
            .clone()
            .ok_or("service preboot rootfs hash missing")?,
        client_preboot_rootfs_sha256: manifest
            .client_preboot_rootfs_sha256
            .clone()
            .ok_or("client preboot rootfs hash missing")?,
        controller_sha256: manifest.base.controller_sha256.clone(),
        firecracker_sha256: manifest.base.firecracker_sha256.clone(),
        guest_agent_sha256: manifest.base.guest_agent_sha256.clone(),
        client_program_sha256: manifest.base.program_sha256.clone(),
        bucket_admin_sha256: manifest.bucket_admin_sha256.clone(),
        real_caller_archive_sha256: hash_file(&output.join("real-s3-caller.tar.gz"))?,
        bucket_create_archive_sha256: hash_file(&output.join("bucket-create.tar.gz"))?,
        bucket_delete_archive_sha256: hash_file(&output.join("bucket-delete.tar.gz"))?,
        service_guest: manifest
            .service_guest
            .clone()
            .ok_or("service guest report missing")?,
        client_guest: manifest
            .client_guest
            .clone()
            .ok_or("client guest report missing")?,
        pair_network_status: pair.status.clone(),
        authenticated_s3_ready: manifest.authenticated_s3_ready,
        bucket_created: manifest.bucket_created,
        bucket_deleted: manifest.bucket_deleted,
        tcp_reachable: manifest.tcp_reachable,
        client_exit_code: manifest
            .client_exit_code
            .ok_or("client exit code missing")?,
        finished_unix_ms: manifest.finished_unix_ms.ok_or("finish time missing")?,
    };
    let path = output.join("rustfs-observed-receipt.json");
    let temporary = output.join("rustfs-observed-receipt.json.tmp");
    let mut file = File::create_new(&temporary)?;
    serde_json::to_writer_pretty(&mut file, &observed)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    fs::rename(temporary, path)?;
    File::open(output)?.sync_all()?;
    Ok(())
}

#[allow(
    clippy::too_many_lines,
    reason = "the run advances two VM, health, caller, and cleanup stages while persisting each receipt transition"
)]
#[expect(
    clippy::too_many_arguments,
    reason = "the pair supervisor receives its frozen artifacts explicitly"
)]
fn run_inner(
    args: &FixtureArgs,
    artifact: &PinnedRustfsCandidate,
    profile: &ImageProfile,
    output: &Path,
    manifest: &mut FixtureManifest,
    service_staged: &FrozenPath,
    service_sha: &str,
    client_snapshot: &ProgramSnapshot,
    admin_snapshot: &ProgramSnapshot,
    agent_snapshot: &AgentSnapshot,
    controller_snapshot: &HostArtifactSnapshot,
    firecracker_snapshot: &HostArtifactSnapshot,
    kernel_snapshot: &HostArtifactSnapshot,
    service_cpus: [usize; 4],
    client_cpus: [usize; 4],
) -> Result<(), Box<dyn std::error::Error>> {
    let service_rootfs = output.join("service-rootfs.ext4");
    let client_rootfs = output.join("client-rootfs.ext4");
    let sockets = tempfile::Builder::new().prefix("fcp-").tempdir_in("/tmp")?;
    let service_api = sockets.path().join("service-api.sock");
    let service_vsock = sockets.path().join("service-vsock.sock");
    let client_api = sockets.path().join("client-api.sock");
    let client_vsock = sockets.path().join("client-vsock.sock");

    let helper_path = output.join("pair-network-helper.bin");
    fs::copy(controller_snapshot.staged.path(), &helper_path)?;
    fs::set_permissions(&helper_path, fs::Permissions::from_mode(0o500))?;
    verify_file(&helper_path, &controller_snapshot.sha256)?;
    File::open(&helper_path)?.sync_all()?;
    File::open(output)?.sync_all()?;
    let mut pair = PairNetwork::create(
        output,
        &args.run_id,
        &controller_snapshot.sha256,
        service_cpus,
        client_cpus,
        &service_rootfs,
        &client_rootfs,
        &service_api,
        &client_api,
        &service_vsock,
        &client_vsock,
    )?;
    let _socket_directory = sockets.keep();
    manifest.pair_network = Some(pair.receipt().clone());
    "network-ready".clone_into(&mut manifest.status);
    write_fixture_manifest(output, manifest)?;

    let service_role = pair.role("service")?.clone();
    let client_role = pair.role("client")?.clone();
    manifest.service_preboot_rootfs_sha256 = Some(stage_rootfs(
        &profile.rootfs_path,
        &profile.rootfs_sha256,
        &service_rootfs,
        &agent_snapshot.staged,
        service_staged,
        None,
        &pair.guest_network_file("service")?,
        output,
        "service",
    )?);
    manifest.client_preboot_rootfs_sha256 = Some(stage_rootfs(
        &profile.rootfs_path,
        &profile.rootfs_sha256,
        &client_rootfs,
        &agent_snapshot.staged,
        &client_snapshot.staged,
        Some(&admin_snapshot.staged),
        &pair.guest_network_file("client")?,
        output,
        "client",
    )?);
    "images-staged".clone_into(&mut manifest.status);
    write_fixture_manifest(output, manifest)?;
    recheck_fixture_capacity_before_boot(output)?;

    let access_key = format!("TB{}", random_hex(16)?);
    let secret_key = random_hex(32)?;
    let bucket = format!("tbfc-{}", random_hex(16)?);
    manifest.bucket_name = Some(bucket.clone());
    write_fixture_manifest(output, manifest)?;
    let endpoint = format!("http://10.78.0.2:{}", artifact.service_port);
    let client_env = vec![
        ("TERRABASE_RUSTFS_TEST_ENDPOINT".to_owned(), endpoint),
        ("TERRABASE_RUSTFS_TEST_UNIQUE_BUCKET".to_owned(), bucket),
        (
            "TERRABASE_RUSTFS_TEST_ACCESS_KEY".to_owned(),
            access_key.clone(),
        ),
        (
            "TERRABASE_RUSTFS_TEST_SECRET_KEY".to_owned(),
            secret_key.clone(),
        ),
    ];
    let service_env = vec![
        (artifact.access_key_env.clone(), access_key),
        (artifact.secret_key_env.clone(), secret_key),
    ];

    let mut service_vm = boot_vm(
        &pair,
        &service_role,
        firecracker_snapshot.staged.path(),
        kernel_snapshot.staged.path(),
        output,
        &args.run_id,
    )?;
    let (service_start, service_guest) = exchange(
        &mut service_vm,
        &service_role,
        &json!({"operation":"service_start", "program":GUEST_PAYLOAD_PATH,
            "program_sha256":service_sha, "args":&artifact.service_args, "env":service_env}),
        Duration::from_secs(30),
    )?;
    manifest.service_guest = Some(service_guest);
    require_status(&service_start, "running", "RustFS service start")?;
    let service_address_deadline = Instant::now() + Duration::from_secs(20);
    loop {
        let (status, report) = exchange(
            &mut service_vm,
            &service_role,
            &json!({"operation":"service_status"}),
            Duration::from_secs(10),
        )?;
        require_status(&status, "running", "RustFS service address check")?;
        if report.ipv4_addresses.contains(&service_role.ipv4) {
            manifest.service_guest = Some(report);
            break;
        }
        if Instant::now() >= service_address_deadline {
            return Err("RustFS service guest did not acquire its private IPv4 address".into());
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    "service-started".clone_into(&mut manifest.status);
    write_fixture_manifest(output, manifest)?;

    let mut client_vm = boot_vm(
        &pair,
        &client_role,
        firecracker_snapshot.staged.path(),
        kernel_snapshot.staged.path(),
        output,
        &args.run_id,
    )?;
    let admin_payload = SupplementalPayload {
        host_path: args.admin_program.display().to_string(),
        guest_path: ADMIN_GUEST_PATH.to_owned(),
        size_bytes: fs::metadata(admin_snapshot.staged.path())?.len(),
        sha256: admin_snapshot.sha256.clone(),
        guest_mode: "0755".to_owned(),
    };
    let client_context = guest_context(
        &manifest.base,
        &client_role,
        &client_snapshot.sha256,
        &[admin_payload],
    );
    let (probe, client_guest) = exchange(
        &mut client_vm,
        &client_role,
        &json!({"operation":"tcp_probe", "ipv4":"10.78.0.2", "port":artifact.service_port,
            "timeout_seconds":60}),
        Duration::from_secs(70),
    )?;
    manifest.client_guest = Some(client_guest);
    require_status(&probe, "reachable", "client-to-RustFS TCP prerequisite")?;
    if !manifest
        .client_guest
        .as_ref()
        .is_some_and(|guest| guest.ipv4_addresses.contains(&client_role.ipv4))
    {
        let (probe, report) = exchange(
            &mut client_vm,
            &client_role,
            &json!({"operation":"tcp_probe", "ipv4":"10.78.0.2", "port":artifact.service_port,
                "timeout_seconds":1}),
            Duration::from_secs(10),
        )?;
        require_status(&probe, "reachable", "client private address check")?;
        if !report.ipv4_addresses.contains(&client_role.ipv4) {
            return Err("client guest did not report its private IPv4 address".into());
        }
        manifest.client_guest = Some(report);
    }
    manifest.tcp_reachable = true;
    let (service_status, _) = exchange(
        &mut service_vm,
        &service_role,
        &json!({"operation":"service_status"}),
        Duration::from_secs(10),
    )?;
    require_status(&service_status, "running", "RustFS service status")?;
    "peer-reachable".clone_into(&mut manifest.status);
    write_fixture_manifest(output, manifest)?;

    let mut create_attempted = false;
    let workload = (|| -> Result<(), Box<dyn std::error::Error>> {
        create_attempted = true;
        let create = fixture_run(
            &mut client_vm,
            &client_role,
            ADMIN_GUEST_PATH,
            &["create".to_owned()],
            &client_env,
            &client_context,
            120,
            &output.join("bucket-create.tar.gz"),
        )?;
        require_run_passed(&create, "authenticated S3 bucket create")?;
        manifest.authenticated_s3_ready = true;
        manifest.bucket_created = true;
        "authenticated-s3-ready".clone_into(&mut manifest.status);
        write_fixture_manifest(output, manifest)?;

        let stop = std::sync::atomic::AtomicBool::new(false);
        let (caller, monitor_result) = std::thread::scope(|scope| {
            let stop_ref = &stop;
            let service_vm_ref = &mut service_vm;
            let service_role_ref = &service_role;
            let monitor = scope.spawn(move || {
                while !stop_ref.load(std::sync::atomic::Ordering::Relaxed) {
                    let healthy = exchange(
                        service_vm_ref,
                        service_role_ref,
                        &json!({"operation":"service_status"}),
                        Duration::from_secs(10),
                    )
                    .and_then(|(status, _)| {
                        require_status(&status, "running", "RustFS service monitor")
                    });
                    if healthy.is_err() {
                        return false;
                    }
                    std::thread::sleep(Duration::from_secs(2));
                }
                true
            });
            let caller = fixture_run(
                &mut client_vm,
                &client_role,
                GUEST_PAYLOAD_PATH,
                &[
                    CLIENT_FILTER.to_owned(),
                    "--exact".to_owned(),
                    "--nocapture".to_owned(),
                ],
                &client_env,
                &client_context,
                args.timeout_seconds,
                &output.join("real-s3-caller.tar.gz"),
            );
            stop.store(true, std::sync::atomic::Ordering::Relaxed);
            (caller, monitor.join())
        });
        if !matches!(monitor_result, Ok(true)) {
            return Err("RustFS service stopped or lost health during real caller".into());
        }
        let caller = caller?;
        manifest.client_exit_code = caller.exit_code;
        require_run_passed(&caller, "real S3 Slow caller")?;
        verify_one_test(&output.join("real-s3-caller.tar.gz"))?;
        Ok(())
    })();

    let bucket_cleanup = if create_attempted {
        fixture_run(
            &mut client_vm,
            &client_role,
            ADMIN_GUEST_PATH,
            &["delete".to_owned()],
            &client_env,
            &client_context,
            120,
            &output.join("bucket-delete.tar.gz"),
        )
        .and_then(|response| require_run_passed(&response, "owned S3 bucket deletion"))
    } else {
        Ok(())
    };
    if bucket_cleanup.is_ok() && create_attempted {
        manifest.bucket_deleted = true;
    }
    let service_stop = exchange(
        &mut service_vm,
        &service_role,
        &json!({"operation":"service_stop"}),
        Duration::from_secs(30),
    );
    let service_stop_result = match service_stop {
        Ok((response, _)) => require_status(&response, "stopped", "RustFS service stop"),
        Err(error) => Err(error),
    };
    pair.stop_processes()?;
    drop(client_vm);
    drop(service_vm);
    pair.cleanup()?;
    pair.cleanup_staged_resources()?;
    manifest.pair_network = Some(pair.receipt().clone());
    write_fixture_manifest(output, manifest)?;
    workload?;
    bucket_cleanup?;
    service_stop_result?;
    Ok(())
}

#[allow(
    clippy::too_many_arguments,
    reason = "the rootfs staging boundary lists each independently verified guest payload and destination"
)]
fn stage_rootfs(
    source: &Path,
    expected_rootfs_sha256: &str,
    destination: &Path,
    agent: &FrozenPath,
    program: &FrozenPath,
    admin: Option<&FrozenPath>,
    network_file: &str,
    output: &Path,
    name: &str,
) -> Result<String, Box<dyn std::error::Error>> {
    fs::copy(source, destination)?;
    verify_file(destination, expected_rootfs_sha256)?;
    let agent_unit = output.join(format!("{name}-guest-agent.service"));
    fs::write(
        &agent_unit,
        "[Unit]\nDescription=Firecracker fixture guest agent\nAfter=local-fs.target\n\n[Service]\nType=simple\nExecStart=/root/fc-harness-agent\nStandardOutput=journal+console\nStandardError=journal+console\nRestart=no\n\n[Install]\nWantedBy=multi-user.target\n",
    )?;
    let network_path = output.join(format!("{name}-network.network"));
    fs::write(&network_path, network_file)?;
    debugfs_write(destination, agent.path(), GUEST_AGENT_PATH)?;
    debugfs_set_mode(destination, GUEST_AGENT_PATH)?;
    verify_guest_copy(destination, GUEST_AGENT_PATH, agent.path())?;
    debugfs_write(destination, &agent_unit, GUEST_AGENT_UNIT)?;
    debugfs_symlink(
        destination,
        "/etc/systemd/system/multi-user.target.wants/fc-harness-agent.service",
        "../fc-harness-agent.service",
    )?;
    debugfs_write(destination, program.path(), GUEST_PAYLOAD_PATH)?;
    debugfs_set_mode(destination, GUEST_PAYLOAD_PATH)?;
    verify_guest_copy(destination, GUEST_PAYLOAD_PATH, program.path())?;
    if let Some(admin) = admin {
        debugfs_command(destination, "mkdir /root/fc-harness-payloads".to_owned())?;
        debugfs_write(destination, admin.path(), ADMIN_GUEST_PATH)?;
        debugfs_set_mode(destination, ADMIN_GUEST_PATH)?;
        verify_guest_copy(destination, ADMIN_GUEST_PATH, admin.path())?;
    }
    debugfs_write(destination, &network_path, GUEST_NETWORK_UNIT)?;
    File::open(destination)?.sync_all()?;
    Ok(hash_file(destination)?)
}

fn verify_guest_copy(
    rootfs: &Path,
    guest_path: &str,
    snapshot: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let staging = tempfile::Builder::new()
        .prefix("fcp-verify-")
        .tempdir_in("/tmp")?;
    let copied = staging.path().join("copy");
    debugfs_dump(rootfs, guest_path, &copied)?;
    verify_file(&copied, &hash_file(snapshot)?)?;
    Ok(())
}

fn boot_vm(
    pair: &PairNetwork,
    role: &PairRole,
    firecracker: &Path,
    kernel: &Path,
    output: &Path,
    run_id: &str,
) -> Result<RunningVm, Box<dyn std::error::Error>> {
    let mut command = pair.firecracker_command(&role.name, firecracker)?;
    let child = command
        .args(["--id", &format!("{run_id}-{}", role.name), "--api-sock"])
        .arg(&role.api_socket)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()?;
    let mut vm = RunningVm {
        child,
        vsock_socket: PathBuf::from(&role.vsock_socket),
        guest: None,
    };
    fs::write(
        output.join(format!("{}-firecracker.pid", role.name)),
        format!("{}\n", vm.child.id()),
    )?;
    let api = Path::new(&role.api_socket);
    wait_for_socket(api, &mut vm.child, Duration::from_secs(20))?;
    api_request(
        api,
        "PUT",
        "/machine-config",
        &json!({
            "vcpu_count": INSTANCE_VCPU_COUNT, "mem_size_mib": INSTANCE_MEMORY_MIB, "smt": false
        }),
    )?;
    let effective = api_get(api, "/machine-config")?;
    if effective["vcpu_count"].as_u64() != Some(4)
        || effective["mem_size_mib"].as_u64() != Some(8_192)
        || effective["smt"].as_bool() != Some(false)
    {
        return Err(
            "pair VMM effective machine configuration differs from 4 vCPU/8192 MiB/smt=false"
                .into(),
        );
    }
    api_request(
        api,
        "PUT",
        "/boot-source",
        &json!({
            "kernel_image_path":kernel,
            "boot_args":"console=ttyS0 root=/dev/vda rootfstype=ext4 rootwait rw panic=1 systemd.show_status=0 systemd.mask=cloud-init-local.service systemd.mask=cloud-init.service systemd.mask=cloud-config.service systemd.mask=cloud-final.service"
        }),
    )?;
    api_request(
        api,
        "PUT",
        "/drives/rootfs",
        &json!({
            "drive_id":"rootfs", "path_on_host":role.rootfs, "is_root_device":true,
            "is_read_only":false, "cache_type":"Writeback", "io_engine":"Sync"
        }),
    )?;
    api_request(
        api,
        "PUT",
        "/vsock",
        &json!({
            "guest_cid":role.vsock_cid, "uds_path":role.vsock_socket
        }),
    )?;
    api_request(
        api,
        "PUT",
        "/network-interfaces/bench0",
        &json!({
            "iface_id":"bench0", "host_dev_name":role.tap, "guest_mac":role.mac
        }),
    )?;
    api_request(
        api,
        "PUT",
        "/actions",
        &json!({"action_type":"InstanceStart"}),
    )?;
    Ok(vm)
}

fn read_bounded_line(stream: &mut UnixStream) -> io::Result<Vec<u8>> {
    let mut line = Vec::new();
    for _ in 0..65_536 {
        let mut byte = [0_u8; 1];
        stream.read_exact(&mut byte)?;
        if byte[0] == b'\n' {
            return Ok(line);
        }
        line.push(byte[0]);
    }
    Err(io::Error::other("guest vsock response line exceeds 64 KiB"))
}

fn open_guest(
    vm: &mut RunningVm,
    role: &PairRole,
    timeout: Duration,
) -> Result<UnixStream, Box<dyn std::error::Error>> {
    let mut stream = connect_guest_vsock(&vm.vsock_socket, &mut vm.child, VSOCK_CONNECT_TIMEOUT)?;
    stream.set_read_timeout(Some(timeout))?;
    stream.set_write_timeout(Some(Duration::from_secs(30)))?;
    let guest: GuestReport = serde_json::from_slice(&read_bounded_line(&mut stream)?)?;
    if guest.online_cpus != INSTANCE_VCPU_COUNT
        || guest.mem_total_bytes < 7 * 1024 * 1024 * 1024
        || guest.swap_entries != 0
    {
        return Err(format!("{} guest violates CPU/memory/swap contract", role.name).into());
    }
    vm.guest = Some(guest);
    Ok(stream)
}

fn exchange(
    vm: &mut RunningVm,
    role: &PairRole,
    request: &Value,
    timeout: Duration,
) -> Result<(Value, GuestReport), Box<dyn std::error::Error>> {
    let mut stream = open_guest(vm, role, timeout)?;
    serde_json::to_writer(&mut stream, request)?;
    stream.write_all(b"\n")?;
    stream.flush()?;
    let response = serde_json::from_slice(&read_bounded_line(&mut stream)?)?;
    Ok((response, vm.guest.clone().ok_or("guest report missing")?))
}

fn guest_context(
    base: &RunManifest,
    role: &PairRole,
    program_sha256: &str,
    supplements: &[SupplementalPayload],
) -> Value {
    json!({
        "run_id":base.run_id, "firecracker_version":base.firecracker_version,
        "vcpu_count":INSTANCE_VCPU_COUNT, "mem_size_mib":INSTANCE_MEMORY_MIB,
        "smt_enabled":false, "machine_config_source":"Firecracker GET /machine-config before InstanceStart",
        "kernel_sha256":base.kernel_sha256, "rootfs_sha256":base.rootfs_sha256,
        "controller_sha256":base.controller_sha256, "controller_size_bytes":base.controller_size_bytes,
        "firecracker_sha256":base.firecracker_sha256, "firecracker_size_bytes":base.firecracker_size_bytes,
        "guest_agent_sha256":base.guest_agent_sha256, "guest_agent_size_bytes":base.guest_agent_size_bytes,
        "source_provenance":base.source_provenance, "image_profile":base.image_profile,
        "supplemental_payloads":supplements, "benchmark_repository":base.benchmark_repository,
        "subject_repository":base.subject_repository, "program_sha256":program_sha256,
        "host_cpu_affinity":role.host_cpus, "output_filesystem":base.output_filesystem,
        "guest_network":base.guest_network, "network_fixture":{"guest_ipv4":role.ipv4}
    })
}

#[allow(
    clippy::too_many_arguments,
    reason = "the guest-run wire request carries the VM, role, executable, argv, environment, manifest, timeout, and archive path"
)]
fn fixture_run(
    vm: &mut RunningVm,
    role: &PairRole,
    program: &str,
    args: &[String],
    env: &[(String, String)],
    manifest: &Value,
    timeout_seconds: u64,
    archive_path: &Path,
) -> Result<GuestRunResponse, Box<dyn std::error::Error>> {
    let timeout = Duration::from_secs(timeout_seconds.saturating_add(120)).min(RUN_TIMEOUT);
    let mut stream = open_guest(vm, role, timeout)?;
    let request = json!({"operation":"fixture_run", "program":program, "args":args,
        "env":env, "timeout_seconds":timeout_seconds, "manifest":manifest});
    serde_json::to_writer(&mut stream, &request)?;
    stream.write_all(b"\n")?;
    stream.flush()?;
    let response: GuestRunResponse = serde_json::from_slice(&read_bounded_line(&mut stream)?)?;
    if !response.guest.ipv4_addresses.contains(&role.ipv4) {
        return Err(format!(
            "{} guest did not report its expected private address",
            role.name
        )
        .into());
    }
    if response.archive_bytes > MAX_GUEST_ARCHIVE_BYTES {
        return Err("fixture guest archive exceeds host size limit".into());
    }
    if response.archive_bytes > 0 {
        let mut archive = File::create(archive_path)?;
        let received = io::copy(
            &mut (&mut stream).take(response.archive_bytes),
            &mut archive,
        )?;
        if received != response.archive_bytes {
            return Err("fixture guest archive was truncated".into());
        }
        archive.sync_all()?;
    }
    Ok(response)
}

fn require_status(
    value: &Value,
    expected: &str,
    operation: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let response: FixtureOperationResponse = serde_json::from_value(value.clone())?;
    if response.status != expected {
        return Err(format!(
            "{operation} returned status {} exit {:?}",
            response.status, response.exit_code
        )
        .into());
    }
    Ok(())
}

fn require_run_passed(
    response: &GuestRunResponse,
    operation: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    if response.status == "passed" && response.exit_code == Some(0) && response.error.is_none() {
        Ok(())
    } else {
        Err(format!(
            "{operation} returned status {} exit {:?}: {:?}",
            response.status, response.exit_code, response.error
        )
        .into())
    }
}

fn verify_one_test(archive: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let output = Command::new("tar")
        .args(["-xOzf"])
        .arg(archive)
        .arg("./stdout.log")
        .output()?;
    if !output.status.success() || output.stdout.len() > 64 * 1024 * 1024 {
        return Err("could not inspect bounded Slow test stdout".into());
    }
    let stdout = String::from_utf8(output.stdout)?;
    if !stdout.contains("running 1 test") || !stdout.contains("test result: ok. 1 passed;") {
        return Err("real S3 Slow caller did not execute exactly one passing test".into());
    }
    Ok(())
}

fn random_hex(length: usize) -> io::Result<String> {
    use std::fmt::Write as _;
    let mut random = vec![0_u8; length];
    File::open("/dev/urandom")?.read_exact(&mut random)?;
    let mut result = String::with_capacity(length * 2);
    for byte in random {
        write!(&mut result, "{byte:02x}").map_err(io::Error::other)?;
    }
    Ok(result)
}
