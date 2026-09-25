//! Pinned Rust CLI provisioning, offline compilation, and output verification.
//!
//! Provisioning is candidate evidence. A recipe pins the reviewed provision and
//! compile receipt hashes only after their exact bytes have been inspected.

use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::path::Component;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::manifest::CommandReceipt;
use crate::recipe::{RecipeFile, source_tree_sha256_at_workspace_input};
use crate::workflow::{read_command_stdout, sha256_file};
use crate::{PodmanExecutor, RunDirectory};

const PLATFORM: &str = "linux/amd64";
const TARGET: &str = "x86_64-unknown-linux-musl";
const ABI: &str = "x86_64-linux-musl-static";
const RUST_DIST: &str = "https://static.rust-lang.org/dist";
const BOOKWORM_INRELEASE_SHA256: &str =
    "77737fa4b34f2693e982cc9ee35736816c35a7778fc2d326cc1bbf5b301fe1aa";
const BOOKWORM_PACKAGES_XZ_SHA256: &str =
    "9e0b5aabb2465b3d2e7a7fe27f9913846277833f7a2826e7767acccff5b588c5";
const BOOKWORM_PACKAGES_XZ_SIZE: u64 = 8_790_396;
const MUSL_TOOLS_VERSION: &str = "1.2.3-1";
const CARGO_VENDOR_CONFIG: &str = "[source.crates-io]\nreplace-with = 'vendored-sources'\n[source.vendored-sources]\ndirectory = '/vendor'\n[net]\noffline = true\n";
const MAX_SOURCE_FILE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_TREE_BYTES: u64 = 64 * 1024 * 1024 * 1024;
const MAX_REGISTRY_ARCHIVE_BYTES: u64 = 128 * 1024 * 1024;
type PackageIndexRecords = BTreeMap<(String, String, String), BTreeMap<String, String>>;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ToolIdentity {
    pub path: String,
    pub version: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CompilerImageArtifact {
    pub image_reference: String,
    pub archive_path: String,
    pub archive_size_bytes: u64,
    pub archive_sha256: String,
    pub image_digest: String,
    pub manifest_digest: String,
    pub config_digest: String,
    pub platform: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RegistryArchiveReceipt {
    pub name: String,
    pub version: String,
    pub source: String,
    pub archive_path: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RustTargetEvidence {
    pub manifest_url: String,
    pub manifest_sha256_url: String,
    pub manifest_path: String,
    pub manifest_sha256: String,
    pub manifest_sha256_sidecar_path: String,
    pub manifest_sha256_sidecar_sha256: String,
    pub target_archive_url: String,
    pub target_archive_path: String,
    pub target_archive_size_bytes: u64,
    pub target_archive_sha256: String,
    pub installed_target_tree_sha256: String,
    pub installed_target_tree_path: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DebianIndexEvidence {
    pub path: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DebianPackageEvidence {
    pub name: String,
    pub version: String,
    pub architecture: String,
    pub source_url: String,
    pub archive_path: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DebianSupportEvidence {
    pub snapshot_url: String,
    pub signing_keyring_path: String,
    pub signing_keyring_container_path: String,
    pub signing_keyring_sha256: String,
    pub apt_version: String,
    pub apt_get: ToolIdentity,
    pub gpgv: ToolIdentity,
    pub apt_lists_archive_path: String,
    pub apt_lists_archive_size_bytes: u64,
    pub apt_lists_archive_sha256: String,
    pub packages_index_path: String,
    pub packages_index_sha256: String,
    pub deb_inventory_path: String,
    pub deb_inventory_sha256: String,
    pub indexes: Vec<DebianIndexEvidence>,
    pub solver_path: String,
    pub solver_sha256: String,
    pub packages: Vec<DebianPackageEvidence>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CliProvisionReceipt {
    pub schema_version: u32,
    pub status: String,
    pub recipe_identity: String,
    pub recipe_version: String,
    pub source_tree_sha256: String,
    pub workspace_manifest_sha256: String,
    pub workspace_tree_sha256: String,
    pub cargo_lock_sha256: String,
    pub rust_toolchain_sha256: String,
    pub base_image: String,
    pub base_index_oci_digest: String,
    pub base_oci_digest: String,
    pub base_platform: String,
    pub base_target_preinstalled: bool,
    pub base_linker_preinstalled: bool,
    pub compiler_image: CompilerImageArtifact,
    pub debian_support: DebianSupportEvidence,
    pub rust_target: RustTargetEvidence,
    pub registry_archives: Vec<RegistryArchiveReceipt>,
    pub vendor_tree_path: String,
    pub vendor_tree_sha256: String,
    pub vendor_inventory_path: String,
    pub vendor_inventory_sha256: String,
    pub rustc: ToolIdentity,
    pub cargo: ToolIdentity,
    pub linker: ToolIdentity,
    pub commands: Vec<CommandReceipt>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CliCompileReceipt {
    pub schema_version: u32,
    pub provision_receipt_path: String,
    pub provision_receipt_sha256: String,
    pub compiler_image: CompilerImageArtifact,
    pub source_tree_sha256: String,
    pub workspace_manifest_sha256: String,
    pub workspace_tree_sha256: String,
    pub cargo_lock_sha256: String,
    pub rust_toolchain_sha256: String,
    pub vendor_tree_sha256: String,
    pub vendor_inventory_sha256: String,
    pub cargo_config_sha256: String,
    pub rustc: ToolIdentity,
    pub cargo: ToolIdentity,
    pub linker: ToolIdentity,
    pub target: String,
    pub host_abi: String,
    pub output_path: String,
    pub output_size_bytes: u64,
    pub output_sha256: String,
    pub build_environment: Vec<(String, String)>,
    pub commands: Vec<CommandReceipt>,
}

#[derive(Clone, Debug)]
pub struct VerifiedCliIdentity {
    pub source_tree_sha256: String,
    pub workspace_manifest_sha256: String,
    pub cargo_lock_sha256: String,
    pub rust_toolchain_sha256: String,
    pub vendor_tree_sha256: String,
    pub output_sha256: String,
    pub output_size_bytes: u64,
    pub base_index_oci_digest: String,
    pub base_oci_digest: String,
    pub compiler_image: CompilerImageArtifact,
    pub rustc: ToolIdentity,
    pub cargo: ToolIdentity,
    pub linker: ToolIdentity,
    pub target: String,
    pub host_abi: String,
}

#[derive(Deserialize)]
struct CargoLock {
    package: Vec<CargoPackage>,
}

#[derive(Deserialize)]
struct CargoPackage {
    name: String,
    version: String,
    source: Option<String>,
    checksum: Option<String>,
}

fn invalid(message: impl Into<String>) -> Box<dyn Error> {
    std::io::Error::other(message.into()).into()
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn regular_file(path: &Path) -> Result<u64, Box<dyn Error>> {
    let meta = fs::symlink_metadata(path)?;
    if meta.file_type().is_symlink() || !meta.is_file() || meta.len() == 0 {
        return Err(invalid(format!(
            "{} must be a nonempty regular file",
            path.display()
        )));
    }
    Ok(meta.len())
}

fn verify_hash(path: &Path, expected: &str) -> Result<u64, Box<dyn Error>> {
    let size = regular_file(path)?;
    if !is_sha256(expected) || !sha256_file(path)?.eq_ignore_ascii_case(expected) {
        return Err(invalid(format!(
            "{} SHA-256 differs from pin",
            path.display()
        )));
    }
    Ok(size)
}

fn ensure_success(receipt: &CommandReceipt, purpose: &str) -> Result<(), Box<dyn Error>> {
    if receipt.status_code == 0 {
        Ok(())
    } else {
        Err(invalid(format!(
            "{purpose} exited with {}",
            receipt.status_code
        )))
    }
}

fn record_command(
    podman: &PodmanExecutor<'_>,
    commands: &mut Vec<CommandReceipt>,
    id: &str,
    args: &[String],
) -> Result<CommandReceipt, Box<dyn Error>> {
    let receipt = podman.invoke(id, args)?;
    commands.push(receipt.clone());
    Ok(receipt)
}

fn write_receipt<T: Serialize>(
    run: &RunDirectory,
    name: &str,
    receipt: &T,
) -> Result<PathBuf, Box<dyn Error>> {
    let mut bytes = serde_json::to_vec_pretty(receipt)?;
    bytes.push(b'\n');
    Ok(run.write_new(name, &bytes)?)
}

fn read_receipt<T: for<'de> Deserialize<'de>>(path: &Path) -> Result<T, Box<dyn Error>> {
    regular_file(path)?;
    Ok(serde_json::from_slice(&fs::read(path)?)?)
}

fn tree_files(root: &Path) -> Result<Vec<(String, PathBuf)>, Box<dyn Error>> {
    let meta = fs::symlink_metadata(root)?;
    if meta.file_type().is_symlink() || !meta.is_dir() {
        return Err(invalid("tree root must be a regular directory"));
    }
    let mut pending = vec![root.to_path_buf()];
    let mut files = Vec::new();
    while let Some(directory) = pending.pop() {
        for entry in fs::read_dir(directory)? {
            let path = entry?.path();
            let metadata = fs::symlink_metadata(&path)?;
            if metadata.file_type().is_symlink() {
                return Err(invalid(format!(
                    "tree contains symlink: {}",
                    path.display()
                )));
            }
            if metadata.is_dir() {
                pending.push(path);
            } else if metadata.is_file() && metadata.len() <= MAX_SOURCE_FILE_BYTES {
                let relative = path
                    .strip_prefix(root)?
                    .to_string_lossy()
                    .replace('\\', "/");
                files.push((relative, path));
            } else {
                return Err(invalid(format!("tree has unsafe file: {}", path.display())));
            }
        }
    }
    files.sort_by(|left, right| left.0.cmp(&right.0));
    Ok(files)
}

fn tree_sha256(root: &Path) -> Result<String, Box<dyn Error>> {
    let files = tree_files(root)?;
    let mut digest = Sha256::new();
    let mut total = 0_u64;
    for (relative, path) in files {
        let bytes = fs::read(path)?;
        let len = u64::try_from(bytes.len())?;
        total = total
            .checked_add(len)
            .ok_or_else(|| invalid("tree size overflow"))?;
        if total > MAX_TREE_BYTES {
            return Err(invalid("tree exceeds 64 GiB limit"));
        }
        let path_bytes = relative.as_bytes();
        digest.update(u64::try_from(path_bytes.len())?.to_be_bytes());
        digest.update(path_bytes);
        digest.update(len.to_be_bytes());
        digest.update(bytes);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn copy_tree(source: &Path, destination: &Path) -> Result<(), Box<dyn Error>> {
    fs::create_dir(destination)?;
    for (relative, path) in tree_files(source)? {
        let target = destination.join(relative);
        fs::create_dir_all(
            target
                .parent()
                .ok_or_else(|| invalid("copy target has no parent"))?,
        )?;
        fs::copy(path, target)?;
    }
    if tree_sha256(source)? != tree_sha256(destination)? {
        return Err(invalid("copied tree differs from source"));
    }
    Ok(())
}

fn workspace_root(recipe: &RecipeFile) -> Result<PathBuf, Box<dyn Error>> {
    let manifest = recipe
        .resolve_workspace_input(&recipe.recipe().rust_cli_builder.workspace_manifest_path)?;
    Ok(manifest
        .parent()
        .ok_or_else(|| invalid("workspace manifest has no parent"))?
        .to_path_buf())
}

fn stage_workspace(
    recipe: &RecipeFile,
    run: &RunDirectory,
) -> Result<(PathBuf, String), Box<dyn Error>> {
    let root = workspace_root(recipe)?;
    let pin = &recipe.recipe().rust_cli_builder;
    let destination = run.path().join("workspace");
    fs::create_dir(&destination)?;
    for (relative, name, expected) in [
        (
            pin.workspace_manifest_path.as_str(),
            "Cargo.toml",
            pin.workspace_manifest_sha256.as_str(),
        ),
        (
            pin.cargo_lock_path.as_str(),
            "Cargo.lock",
            pin.cargo_lock_sha256.as_str(),
        ),
        (
            pin.rust_toolchain_path.as_str(),
            "rust-toolchain.toml",
            pin.rust_toolchain_sha256.as_str(),
        ),
    ] {
        let source = recipe.resolve_workspace_input(relative)?;
        regular_file(&source)?;
        if !expected.is_empty() {
            verify_hash(&source, expected)?;
        }
        fs::copy(source, destination.join(name))?;
    }
    let pins = root.join("pins.toml");
    regular_file(&pins)?;
    fs::copy(pins, destination.join("pins.toml"))?;
    let source_root = recipe.resolve_workspace_input(&pin.source_root)?;
    let crates_root = source_root
        .parent()
        .ok_or_else(|| invalid("Rust CLI source directory has no crates parent"))?;
    copy_tree(crates_root, &destination.join("crates"))?;
    let tree_hash = tree_sha256(&destination)?;
    let source_hash = source_tree_sha256_at_workspace_input(recipe, &pin.source_root)
        .ok_or_else(|| invalid("Rust CLI source tree cannot be read safely"))?;
    if source_hash != tree_sha256(&destination.join("crates/fc-image-builder"))? {
        return Err(invalid("staged Rust CLI source differs from checkout"));
    }
    for (path, expected, label) in [
        (
            destination.join("Cargo.toml"),
            pin.workspace_manifest_sha256.as_str(),
            "workspace Cargo.toml",
        ),
        (
            destination.join("Cargo.lock"),
            pin.cargo_lock_sha256.as_str(),
            "Cargo.lock",
        ),
        (
            destination.join("rust-toolchain.toml"),
            pin.rust_toolchain_sha256.as_str(),
            "rust-toolchain.toml",
        ),
    ] {
        if !expected.is_empty() && !sha256_file(&path)?.eq_ignore_ascii_case(expected) {
            return Err(invalid(format!("staged {label} differs from recipe pin")));
        }
    }
    if !pin.source_tree_sha256.is_empty() && source_hash != pin.source_tree_sha256 {
        return Err(invalid("Rust CLI source tree differs from recipe pin"));
    }
    Ok((destination, tree_hash))
}

fn locked_registry_packages(lock_path: &Path) -> Result<Vec<CargoPackage>, Box<dyn Error>> {
    let lock: CargoLock = toml::from_str(&fs::read_to_string(lock_path)?)?;
    let mut registry = Vec::new();
    let mut seen = BTreeSet::new();
    for package in lock.package {
        match package.source.as_deref() {
            Some(source)
                if source.starts_with("registry+https://github.com/rust-lang/crates.io-index") =>
            {
                let checksum = package
                    .checksum
                    .as_deref()
                    .ok_or_else(|| invalid("registry package lacks checksum"))?;
                if !is_sha256(checksum)
                    || !seen.insert((package.name.clone(), package.version.clone()))
                {
                    return Err(invalid(
                        "registry package has invalid checksum or duplicate identity",
                    ));
                }
                registry.push(package);
            }
            None => {}
            Some(source) => {
                return Err(invalid(format!(
                    "non-crates.io Cargo source is not supported: {source}"
                )));
            }
        }
    }
    if registry.is_empty() {
        return Err(invalid(
            "Cargo.lock has no checksummed registry dependencies",
        ));
    }
    registry.sort_by(|left, right| (&left.name, &left.version).cmp(&(&right.name, &right.version)));
    Ok(registry)
}

fn registry_archives(
    cargo_home: &Path,
    packages: &[CargoPackage],
) -> Result<Vec<RegistryArchiveReceipt>, Box<dyn Error>> {
    let cache = cargo_home.join("registry/cache");
    let roots = fs::read_dir(&cache)?.collect::<Result<Vec<_>, _>>()?;
    if roots.len() != 1 {
        return Err(invalid(
            "Cargo registry cache has an unexpected number of sources",
        ));
    }
    let root = roots[0].path();
    let mut receipts = Vec::with_capacity(packages.len());
    for package in packages {
        let name = format!("{}-{}.crate", package.name, package.version);
        let archive = root.join(name);
        let checksum = package
            .checksum
            .as_deref()
            .ok_or_else(|| invalid("registry checksum absent"))?;
        let size = verify_hash(&archive, checksum)?;
        if size > MAX_REGISTRY_ARCHIVE_BYTES {
            return Err(invalid("registry crate archive exceeds bound"));
        }
        receipts.push(RegistryArchiveReceipt {
            name: package.name.clone(),
            version: package.version.clone(),
            source: package.source.clone().unwrap_or_default(),
            archive_path: archive.display().to_string(),
            size_bytes: size,
            sha256: checksum.to_owned(),
        });
    }
    let observed_count = fs::read_dir(root)?.count();
    if observed_count != packages.len() {
        return Err(invalid(
            "Cargo registry cache has extra or missing archives",
        ));
    }
    Ok(receipts)
}

fn vendor_inventory_content(vendor: &Path) -> Result<String, Box<dyn Error>> {
    let mut inventory = String::new();
    for (relative, path) in tree_files(vendor)? {
        let size = fs::symlink_metadata(&path)?.len();
        let hash = sha256_file(&path)?;
        writeln!(&mut inventory, "{relative}\t{size}\t{hash}")?;
    }
    Ok(inventory)
}

fn vendor_inventory(vendor: &Path, run: &RunDirectory) -> Result<(String, String), Box<dyn Error>> {
    let inventory = vendor_inventory_content(vendor)?;
    let path = run.write_new("vendor-inventory.tsv", inventory.as_bytes())?;
    Ok((tree_sha256(vendor)?, sha256_file(&path)?))
}

fn verify_vendor_packages(vendor: &Path, packages: &[CargoPackage]) -> Result<(), Box<dyn Error>> {
    if tree_files(vendor)?.is_empty() {
        return Err(invalid("Cargo vendor tree is empty"));
    }
    for package in packages {
        let checksum = vendor
            .join(format!("{}-{}", package.name, package.version))
            .join(".cargo-checksum.json");
        regular_file(&checksum)?;
    }
    Ok(())
}

fn extract_evidence_tar(archive: &Path, destination: &Path) -> Result<(), Box<dyn Error>> {
    fs::create_dir(destination)?;
    let mut tar = tar::Archive::new(File::open(archive)?);
    let mut total = 0_u64;
    let entries = tar.entries()?.raw(true);
    for entry in entries {
        let mut entry = entry?;
        let kind = entry.header().entry_type();
        if kind.is_dir() {
            continue;
        }
        if !kind.is_file() || entry.size() > MAX_SOURCE_FILE_BYTES {
            return Err(invalid("support evidence tar has an unsafe entry"));
        }
        let relative = entry.path()?.into_owned();
        if relative.as_os_str().is_empty()
            || !relative
                .components()
                .all(|part| matches!(part, Component::Normal(_) | Component::CurDir))
        {
            return Err(invalid("support evidence tar entry escapes its root"));
        }
        total = total
            .checked_add(entry.size())
            .ok_or_else(|| invalid("support tar size overflow"))?;
        if total > MAX_TREE_BYTES {
            return Err(invalid("support evidence tar exceeds 64 GiB"));
        }
        let output = destination.join(relative);
        fs::create_dir_all(
            output
                .parent()
                .ok_or_else(|| invalid("support entry lacks parent"))?,
        )?;
        let mut file = fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&output)?;
        let written = std::io::copy(&mut entry, &mut file)?;
        if written != entry.size() {
            return Err(invalid("support evidence tar entry is truncated"));
        }
        file.sync_all()?;
    }
    Ok(())
}

#[expect(
    clippy::too_many_arguments,
    reason = "the support command needs explicit pinned image, mount, and output identities"
)]
fn run_support_command(
    podman: &PodmanExecutor<'_>,
    commands: &mut Vec<CommandReceipt>,
    id: &str,
    image: &str,
    network: &str,
    mounts: &[(&Path, &str, bool)],
    entrypoint: &str,
    arguments: &[String],
    recipe: &RecipeFile,
) -> Result<CommandReceipt, Box<dyn Error>> {
    let pin = &recipe.recipe().rust_cli_builder;
    let mut args = vec![
        "run".to_owned(),
        "--rm".to_owned(),
        "--pull=never".to_owned(),
        "--userns=keep-id".to_owned(),
        "--cap-drop=all".to_owned(),
        format!("--network={network}"),
        format!("--cpus={}", pin.cpu_count),
        format!("--memory={}m", pin.memory_mib),
        format!("--pids-limit={}", pin.pids_limit),
        "--storage-opt".to_owned(),
        format!("overlay.size={}", pin.workspace_limit_bytes),
        "--env=CARGO_HOME=/cargo-home".to_owned(),
    ];
    for (host, guest, read_only) in mounts {
        if host.to_string_lossy().contains(',') || !guest.starts_with('/') {
            return Err(invalid("support container mount is malformed"));
        }
        args.push("--mount".to_owned());
        args.push(format!(
            "type=bind,src={},dst={guest}{}",
            host.display(),
            if *read_only { ",ro" } else { "" }
        ));
    }
    args.push(format!("--entrypoint={entrypoint}"));
    args.push(image.to_owned());
    args.extend_from_slice(arguments);
    let receipt = record_command(podman, commands, id, &args)?;
    ensure_success(&receipt, id)?;
    Ok(receipt)
}

fn parse_rust_target_manifest(manifest: &Path) -> Result<(String, String), Box<dyn Error>> {
    let data: toml::Value = toml::from_str(&fs::read_to_string(manifest)?)?;
    let target = data
        .get("pkg")
        .and_then(|v| v.get("rust-std"))
        .and_then(|v| v.get("target"))
        .and_then(|v| v.get(TARGET))
        .ok_or_else(|| invalid("Rust dist manifest has no musl standard-library target"))?;
    let url = target
        .get("xz_url")
        .and_then(toml::Value::as_str)
        .ok_or_else(|| invalid("Rust dist target lacks xz URL"))?;
    let hash = target
        .get("xz_hash")
        .and_then(toml::Value::as_str)
        .ok_or_else(|| invalid("Rust dist target lacks xz hash"))?;
    if !url.starts_with(&format!("{RUST_DIST}/")) || !is_sha256(hash) {
        return Err(invalid("Rust target URL or digest is invalid"));
    }
    Ok((url.to_owned(), hash.to_owned()))
}

fn fetch_rust_target(run: &RunDirectory) -> Result<RustTargetEvidence, Box<dyn Error>> {
    let fetcher = crate::HttpsFetcher::new()?;
    let manifest_url = format!("{RUST_DIST}/channel-rust-1.98.1.toml");
    let manifest_sha256_url = format!("{manifest_url}.sha256");
    let sidecar = fetcher.fetch_observed(
        "rust-manifest.sha256",
        &manifest_sha256_url,
        4096,
        None,
        run,
    )?;
    let sidecar_path = run.path().join(sidecar.cache_path);
    let sidecar_text = fs::read_to_string(&sidecar_path)?;
    let expected = sidecar_text
        .split_whitespace()
        .next()
        .ok_or_else(|| invalid("Rust manifest checksum sidecar is empty"))?;
    if !is_sha256(expected) {
        return Err(invalid("Rust manifest checksum sidecar is malformed"));
    }
    let manifest = fetcher.fetch_observed(
        "rust-manifest.toml",
        &manifest_url,
        64 * 1024 * 1024,
        Some(expected),
        run,
    )?;
    let manifest_path = run.path().join(manifest.cache_path);
    let (target_archive_url, target_hash) = parse_rust_target_manifest(&manifest_path)?;
    let target = fetcher.fetch_observed(
        "rust-musl-target.tar.xz",
        &target_archive_url,
        512 * 1024 * 1024,
        Some(&target_hash),
        run,
    )?;
    Ok(RustTargetEvidence {
        manifest_url,
        manifest_sha256_url,
        manifest_path: manifest_path.display().to_string(),
        manifest_sha256: expected.to_owned(),
        manifest_sha256_sidecar_path: sidecar_path.display().to_string(),
        manifest_sha256_sidecar_sha256: sidecar.sha256,
        target_archive_url,
        target_archive_path: run.path().join(target.cache_path).display().to_string(),
        target_archive_size_bytes: target.size_bytes,
        target_archive_sha256: target.sha256,
        installed_target_tree_sha256: String::new(),
        installed_target_tree_path: String::new(),
    })
}

#[derive(Deserialize)]
struct OciIndexDescriptor {
    digest: String,
    platform: OciIndexPlatform,
}

#[derive(Deserialize)]
struct OciIndexPlatform {
    os: String,
    architecture: String,
}

#[derive(Deserialize)]
struct OciIndex {
    manifests: Vec<OciIndexDescriptor>,
}

fn pull_pinned_rust_image(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<String, Box<dyn Error>> {
    let pin = &recipe.recipe().rust_cli_builder;
    let repository = pin
        .base_image
        .split(':')
        .next()
        .ok_or_else(|| invalid("base image has no repository"))?;
    let index_ref = format!("docker://{repository}@{}", pin.base_image_index_digest);
    let index = record_command(
        podman,
        commands,
        "cli_base_index",
        &["manifest".to_owned(), "inspect".to_owned(), index_ref],
    )?;
    ensure_success(&index, "inspect pinned Rust base index")?;
    let parsed: OciIndex = serde_json::from_str(&read_command_stdout(run, &index)?)?;
    let selected: Vec<_> = parsed
        .manifests
        .iter()
        .filter(|entry| entry.platform.os == "linux" && entry.platform.architecture == "amd64")
        .collect();
    if selected.len() != 1 || selected[0].digest != pin.base_image_digest {
        return Err(invalid(
            "pinned Rust index does not select the pinned amd64 child",
        ));
    }
    let child = format!("{repository}@{}", pin.base_image_digest);
    let pull = record_command(
        podman,
        commands,
        "cli_base_pull",
        &[
            "pull".to_owned(),
            "--platform=linux/amd64".to_owned(),
            child.clone(),
        ],
    )?;
    ensure_success(&pull, "pull pinned Rust amd64 child")?;
    let inspect = record_command(
        podman,
        commands,
        "cli_base_inspect",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Digest}} {{.Os}}/{{.Architecture}}".to_owned(),
            child.clone(),
        ],
    )?;
    ensure_success(&inspect, "inspect pinned Rust child")?;
    if read_command_stdout(run, &inspect)?.trim() != format!("{} {PLATFORM}", pin.base_image_digest)
    {
        return Err(invalid(
            "pulled Rust child digest or platform differs from pin",
        ));
    }
    Ok(child)
}

fn support_containerfile(base: &str, snapshot: &str) -> Result<String, Box<dyn Error>> {
    let script = r#"
export DEBIAN_FRONTEND=noninteractive LC_ALL=C
mkdir -p /evidence/debs /support/extract
rm -f /etc/apt/sources.list.d/*
printf 'deb [signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] {snapshot} bookworm main\n' > /etc/apt/sources.list
apt-get update
apt-get -s --reinstall --no-install-recommends install musl-tools={musl_tools} > /evidence/apt-solver.txt
apt-get -y --download-only --reinstall --no-install-recommends install musl-tools={musl_tools}
cp /var/cache/apt/archives/*.deb /evidence/debs/
for deb in /evidence/debs/*.deb; do
  printf '%s\t%s\t%s\t%s\n' "$(dpkg-deb -f "$deb" Package)" "$(dpkg-deb -f "$deb" Version)" "$(dpkg-deb -f "$deb" Architecture)" "$(basename "$deb")" >> /evidence/deb-inventory.tsv
done
tar --format=ustar -cf /evidence/debs.tar -C /evidence/debs .
tar --format=ustar -cf /evidence/apt-lists.tar -C /var/lib/apt/lists .
for index in /var/lib/apt/lists/*_Packages*; do
  /usr/lib/apt/apt-helper cat-file "$index" >> /evidence/packages-index.txt
  printf '\n' >> /evidence/packages-index.txt
done
apt-get -y --reinstall --no-install-recommends install musl-tools={musl_tools}
tar -xJf /support/rust-musl-target.tar.xz -C /support/extract
installer=$(find /support/extract -name install.sh -type f)
test -n "$installer"
"$installer" --prefix="$(rustc --print sysroot)" --disable-ldconfig
test -d "$(rustc --print sysroot)/lib/rustlib/x86_64-unknown-linux-musl"
test -n "$(find "$(rustc --print target-libdir --target x86_64-unknown-linux-musl)" -maxdepth 1 -name 'libstd-*.rlib' -print -quit)"
tar --format=ustar -cf /evidence/rust-target.tar -C "$(rustc --print sysroot)/lib/rustlib/x86_64-unknown-linux-musl" .
apt-get --version | head -n 1 > /evidence/apt-version.txt
"#.replace("{snapshot}", snapshot).replace("{musl_tools}", MUSL_TOOLS_VERSION);
    let run = serde_json::to_string(&["/bin/sh", "-eu", "-c", &script])?;
    Ok(format!(
        "FROM {base}\nCOPY rust-musl-target.tar.xz /support/rust-musl-target.tar.xz\nRUN {run}\n"
    ))
}

fn copy_image_evidence(
    run: &RunDirectory,
    podman: &PodmanExecutor<'_>,
    image: &str,
    commands: &mut Vec<CommandReceipt>,
    source: &str,
    name: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    let id = format!("cli_evidence_{}", name.replace(['.', '-'], "_"));
    let run_name = run
        .path()
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| invalid("CLI evidence run has no safe name"))?;
    let container = format!("fccli-{run_name}-{id}");
    if container.len() > 140
        || !container
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return Err(invalid("CLI evidence container name is invalid"));
    }
    let create = record_command(
        podman,
        commands,
        &format!("{id}_create"),
        &[
            "create".to_owned(),
            "--pull=never".to_owned(),
            "--network=none".to_owned(),
            "--name".to_owned(),
            container.clone(),
            image.to_owned(),
            "/bin/true".to_owned(),
        ],
    )?;
    ensure_success(&create, "create support evidence container")?;
    let path = run.path().join(name);
    if fs::symlink_metadata(&path).is_ok() {
        let _ = record_command(
            podman,
            commands,
            &format!("{id}_cleanup"),
            &["rm".to_owned(), "--force".to_owned(), container],
        );
        return Err(invalid("support evidence destination already exists"));
    }
    let copied = record_command(
        podman,
        commands,
        &format!("{id}_copy"),
        &[
            "cp".to_owned(),
            format!("{container}:{source}"),
            path.display().to_string(),
        ],
    )?;
    if copied.status_code != 0 {
        let _ = record_command(
            podman,
            commands,
            &format!("{id}_cleanup"),
            &["rm".to_owned(), "--force".to_owned(), container],
        );
        return Err(invalid("copy support image evidence failed"));
    }
    let remove = record_command(
        podman,
        commands,
        &format!("{id}_remove"),
        &["rm".to_owned(), container],
    )?;
    ensure_success(&remove, "remove support evidence container")?;
    regular_file(&path)?;
    Ok(path)
}

fn index_records(index: &Path) -> Result<PackageIndexRecords, Box<dyn Error>> {
    let content = fs::read_to_string(index)?;
    let mut records = BTreeMap::new();
    for paragraph in content.split("\n\n") {
        let mut fields = BTreeMap::new();
        for line in paragraph.lines() {
            if let Some((name, value)) = line.split_once(": ") {
                if matches!(
                    name,
                    "Package" | "Version" | "Architecture" | "Filename" | "Size" | "SHA256"
                ) {
                    fields.insert(name.to_owned(), value.to_owned());
                }
            }
        }
        if let (Some(name), Some(version), Some(arch)) = (
            fields.get("Package"),
            fields.get("Version"),
            fields.get("Architecture"),
        ) {
            records.insert((name.clone(), version.clone(), arch.clone()), fields);
        }
    }
    if records.is_empty() {
        return Err(invalid("signed Debian Packages index contains no records"));
    }
    Ok(records)
}

fn verify_bookworm_inrelease(path: &Path) -> Result<(), Box<dyn Error>> {
    verify_hash(path, BOOKWORM_INRELEASE_SHA256)?;
    let contents = fs::read_to_string(path)?;
    let sha256_section = contents
        .split("\nSHA256:\n")
        .nth(1)
        .ok_or_else(|| invalid("Bookworm InRelease lacks SHA256 stanza"))?;
    let expected_path = "main/binary-amd64/Packages.xz";
    let mut found = false;
    for line in sha256_section.lines() {
        if !line.starts_with(' ') {
            break;
        }
        let mut fields = line.split_whitespace();
        if let (Some(hash), Some(size), Some(name)) = (fields.next(), fields.next(), fields.next())
        {
            if name == expected_path {
                if hash != BOOKWORM_PACKAGES_XZ_SHA256
                    || size.parse::<u64>()? != BOOKWORM_PACKAGES_XZ_SIZE
                {
                    return Err(invalid(
                        "Bookworm Packages.xz differs from reviewed InRelease anchor",
                    ));
                }
                found = true;
            }
        }
    }
    if !found {
        return Err(invalid("Bookworm InRelease omits amd64 Packages.xz"));
    }
    Ok(())
}

#[expect(
    clippy::too_many_lines,
    reason = "support package evidence is one ordered verification transaction"
)]
fn debian_support_evidence(
    run: &RunDirectory,
    podman: &PodmanExecutor<'_>,
    recipe: &RecipeFile,
    image: &str,
    snapshot: &str,
    commands: &mut Vec<CommandReceipt>,
) -> Result<DebianSupportEvidence, Box<dyn Error>> {
    let tool_script = r#"
for tool in apt-get gpgv; do
  path=$(readlink -f "$(command -v "$tool")")
  version=$("$tool" --version | head -n 1)
  hash=$(sha256sum "$path" | cut -d ' ' -f 1)
  printf '%s\t%s\t%s\t%s\n' "$tool" "$path" "$version" "$hash"
done
"#;
    let tool_command = run_support_command(
        podman,
        commands,
        "cli_debian_tools",
        image,
        "none",
        &[],
        "/bin/sh",
        &["-eu".to_owned(), "-c".to_owned(), tool_script.to_owned()],
        recipe,
    )?;
    let mut support_tools = Vec::new();
    for line in read_command_stdout(run, &tool_command)?.lines() {
        let fields: Vec<&str> = line.split('\t').collect();
        if fields.len() != 4 || !fields[1].starts_with("/usr/bin/") || !is_sha256(fields[3]) {
            return Err(invalid("Debian support tool identity is malformed"));
        }
        support_tools.push((
            fields[0].to_owned(),
            ToolIdentity {
                path: fields[1].to_owned(),
                version: fields[2].to_owned(),
                sha256: fields[3].to_owned(),
            },
        ));
    }
    if support_tools.len() != 2 || support_tools[0].0 != "apt-get" || support_tools[1].0 != "gpgv" {
        return Err(invalid("Debian APT or gpgv identity is missing"));
    }
    let apt_get = support_tools.remove(0).1;
    let gpgv = support_tools.remove(0).1;
    let apt_lists_tar = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/apt-lists.tar",
        "apt-lists.tar",
    )?;
    let debs_tar = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/debs.tar",
        "debs.tar",
    )?;
    let solver = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/apt-solver.txt",
        "apt-solver.txt",
    )?;
    let inventory = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/deb-inventory.tsv",
        "deb-inventory.tsv",
    )?;
    let package_index = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/packages-index.txt",
        "packages-index.txt",
    )?;
    let apt_version = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/evidence/apt-version.txt",
        "apt-version.txt",
    )?;
    let keyring = copy_image_evidence(
        run,
        podman,
        image,
        commands,
        "/usr/share/keyrings/debian-archive-keyring.gpg",
        "debian-archive-keyring.gpg",
    )?;
    let apt_lists = run.path().join("apt-lists");
    let debs = run.path().join("debs");
    extract_evidence_tar(&apt_lists_tar, &apt_lists)?;
    extract_evidence_tar(&debs_tar, &debs)?;
    let mut indexes = Vec::new();
    for (relative, path) in tree_files(&apt_lists)? {
        if !relative.ends_with("_InRelease") && !relative.contains("_Packages") {
            continue;
        }
        indexes.push(DebianIndexEvidence {
            path: path.display().to_string(),
            size_bytes: fs::metadata(&path)?.len(),
            sha256: sha256_file(&path)?,
        });
    }
    if !indexes.iter().any(|index| index.path.contains("InRelease"))
        || !indexes.iter().any(|index| index.path.contains("Packages"))
    {
        return Err(invalid(
            "APT did not retain signed InRelease and Packages metadata",
        ));
    }
    let inrelease = indexes
        .iter()
        .find(|index| index.path.ends_with("_InRelease"))
        .ok_or_else(|| invalid("APT retained no Bookworm InRelease"))?;
    verify_bookworm_inrelease(Path::new(&inrelease.path))?;
    let records = index_records(&package_index)?;
    let mut packages = Vec::new();
    for line in fs::read_to_string(&inventory)?.lines() {
        let columns: Vec<&str> = line.split('\t').collect();
        if columns.len() != 4 || columns[3].contains('/') {
            return Err(invalid("Debian package inventory is malformed"));
        }
        let (name, version, arch, filename) = (columns[0], columns[1], columns[2], columns[3]);
        let fields = records
            .get(&(name.to_owned(), version.to_owned(), arch.to_owned()))
            .ok_or_else(|| {
                invalid("downloaded Debian package absent from signed Packages index")
            })?;
        let relative = fields
            .get("Filename")
            .ok_or_else(|| invalid("signed Packages index lacks Filename"))?;
        if relative.starts_with('/') || relative.contains("..") {
            return Err(invalid("signed package Filename is unsafe"));
        }
        let size: u64 = fields
            .get("Size")
            .ok_or_else(|| invalid("signed Packages index lacks Size"))?
            .parse()?;
        let hash = fields
            .get("SHA256")
            .ok_or_else(|| invalid("signed Packages index lacks SHA256"))?;
        let archive = debs.join(filename);
        if verify_hash(&archive, hash)? != size {
            return Err(invalid("Debian archive differs from signed Packages index"));
        }
        packages.push(DebianPackageEvidence {
            name: name.to_owned(),
            version: version.to_owned(),
            architecture: arch.to_owned(),
            source_url: format!("{snapshot}{relative}"),
            archive_path: archive.display().to_string(),
            size_bytes: size,
            sha256: hash.to_owned(),
        });
    }
    if !packages
        .iter()
        .any(|package| package.name == "musl-tools" && package.version == MUSL_TOOLS_VERSION)
        || !fs::read_to_string(&solver)?.contains("Inst musl-tools ")
    {
        return Err(invalid("snapshot APT solver did not select musl-tools"));
    }
    Ok(DebianSupportEvidence {
        snapshot_url: snapshot.to_owned(),
        signing_keyring_path: keyring.display().to_string(),
        signing_keyring_container_path: "/usr/share/keyrings/debian-archive-keyring.gpg".to_owned(),
        signing_keyring_sha256: sha256_file(&keyring)?,
        apt_version: fs::read_to_string(apt_version)?.trim().to_owned(),
        apt_get,
        gpgv,
        apt_lists_archive_path: apt_lists_tar.display().to_string(),
        apt_lists_archive_size_bytes: regular_file(&apt_lists_tar)?,
        apt_lists_archive_sha256: sha256_file(&apt_lists_tar)?,
        packages_index_path: package_index.display().to_string(),
        packages_index_sha256: sha256_file(&package_index)?,
        deb_inventory_path: inventory.display().to_string(),
        deb_inventory_sha256: sha256_file(&inventory)?,
        indexes,
        solver_path: solver.display().to_string(),
        solver_sha256: sha256_file(&solver)?,
        packages,
    })
}

fn inspect_compiler_tools(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    image: &str,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(ToolIdentity, ToolIdentity, ToolIdentity), Box<dyn Error>> {
    let script = r#"
for tool in rustc cargo x86_64-linux-musl-gcc; do
  case "$tool" in
    rustc|cargo) path=$(rustup which "$tool");;
    *) path=$(command -v "$tool");;
  esac
  resolved=$(readlink -f "$path")
  version=$("$tool" --version | head -n 1)
  hash=$(sha256sum "$resolved" | cut -d ' ' -f 1)
  printf '%s\t%s\t%s\t%s\n' "$tool" "$resolved" "$version" "$hash"
done
"#;
    let receipt = run_support_command(
        podman,
        commands,
        "cli_support_tools",
        image,
        "none",
        &[],
        "/bin/sh",
        &["-eu".to_owned(), "-c".to_owned(), script.to_owned()],
        recipe,
    )?;
    let lines: Vec<String> = read_command_stdout(run, &receipt)?
        .lines()
        .map(str::to_owned)
        .collect();
    if lines.len() != 3 {
        return Err(invalid(
            "compiler tool inspection did not report three tools",
        ));
    }
    let mut parsed = Vec::new();
    for line in lines {
        let fields: Vec<&str> = line.split('\t').collect();
        if fields.len() != 4 || !fields[1].starts_with('/') || !is_sha256(fields[3]) {
            return Err(invalid("compiler tool identity line is malformed"));
        }
        parsed.push((
            fields[0].to_owned(),
            ToolIdentity {
                path: fields[1].to_owned(),
                version: fields[2].to_owned(),
                sha256: fields[3].to_owned(),
            },
        ));
    }
    if parsed[0].0 != "rustc"
        || !parsed[0].1.version.starts_with("rustc 1.98.1 ")
        || parsed[1].0 != "cargo"
        || !parsed[1].1.version.starts_with("cargo 1.98.")
        || parsed[2].0 != "x86_64-linux-musl-gcc"
    {
        return Err(invalid(
            "compiler image tool versions differ from pinned Rust 1.98.1",
        ));
    }
    Ok((parsed.remove(0).1, parsed.remove(0).1, parsed.remove(0).1))
}

/// Resolve signed compiler support inputs and vendor the current workspace
/// lock inside a pinned Rust amd64 image. The returned receipt is a candidate
/// for review and later recipe pinning.
///
/// # Errors
///
/// Fails if any source pin, registry checksum, Debian signed index, Rust dist
/// checksum, support image, or exported OCI archive cannot be verified.
#[expect(
    clippy::too_many_lines,
    reason = "provisioning records one complete offline compiler input receipt"
)]
pub fn prepare_cli_inputs(
    recipe: &RecipeFile,
    output_root: &Path,
    run_id: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    if run_id.len() > 64 {
        return Err(invalid("CLI provision run ID exceeds 64 characters"));
    }
    let run = RunDirectory::create(output_root, &format!("{run_id}-cli-provision"))?;
    let (workspace, workspace_tree_sha256) = stage_workspace(recipe, &run)?;
    let pin = &recipe.recipe().rust_cli_builder;
    let mut rust_target = fetch_rust_target(&run)?;
    let mut commands = Vec::new();
    let podman = PodmanExecutor::new(recipe, &run)?;
    let base = pull_pinned_rust_image(recipe, &podman, &run, &mut commands)?;
    let base_probe = run_support_command(&podman, &mut commands, "cli_base_support_probe", &base, "none", &[], "/bin/sh", &[
        "-eu".to_owned(), "-c".to_owned(),
        "rustc --version; cargo --version; rustup target list --installed; command -v x86_64-linux-musl-gcc || true; test -f /usr/share/keyrings/debian-archive-keyring.gpg".to_owned(),
    ], recipe)?;
    let probe_output = read_command_stdout(&run, &base_probe)?;
    if !probe_output.contains("rustc 1.98.1 ") || !probe_output.contains("cargo 1.98.") {
        return Err(invalid(
            "pinned base image does not provide Rust 1.98.1 tools",
        ));
    }
    let base_target_preinstalled = probe_output.lines().any(|line| line.trim() == TARGET);
    let base_linker_preinstalled = probe_output
        .lines()
        .any(|line| line.trim().starts_with("/usr/bin/x86_64-linux-musl-gcc"));
    let context = run.path().join("support-context");
    fs::create_dir(&context)?;
    fs::copy(
        &rust_target.target_archive_path,
        context.join("rust-musl-target.tar.xz"),
    )?;
    verify_hash(
        &context.join("rust-musl-target.tar.xz"),
        &rust_target.target_archive_sha256,
    )?;
    let containerfile = support_containerfile(&base, &pin.debian_snapshot_url)?;
    fs::write(context.join("Containerfile"), containerfile)?;
    let image_ref = format!("localhost/fc-cli-compiler:{run_id}");
    let build = record_command(
        &podman,
        &mut commands,
        "cli_support_image_build",
        &[
            "build".to_owned(),
            "--pull=never".to_owned(),
            "--platform=linux/amd64".to_owned(),
            "--network=slirp4netns:allow_host_loopback=false".to_owned(),
            format!("--cpus={}", pin.cpu_count),
            format!("--memory={}m", pin.memory_mib),
            format!("--pids-limit={}", pin.pids_limit),
            "--storage-opt".to_owned(),
            format!("overlay.size={}", pin.workspace_limit_bytes),
            "--format=oci".to_owned(),
            format!("--tag={image_ref}"),
            format!("--file={}", context.join("Containerfile").display()),
            context.display().to_string(),
        ],
    )?;
    ensure_success(&build, "build pinned Rust compiler support image")?;
    let (rustc, cargo, linker) =
        inspect_compiler_tools(recipe, &podman, &run, &image_ref, &mut commands)?;
    let rust_target_tar = copy_image_evidence(
        &run,
        &podman,
        &image_ref,
        &mut commands,
        "/evidence/rust-target.tar",
        "rust-target.tar",
    )?;
    let rust_target_tree = run.path().join("rust-target");
    extract_evidence_tar(&rust_target_tar, &rust_target_tree)?;
    rust_target.installed_target_tree_sha256 = tree_sha256(&rust_target_tree)?;
    rust_target.installed_target_tree_path = rust_target_tree.display().to_string();
    let debian_support = debian_support_evidence(
        &run,
        &podman,
        recipe,
        &image_ref,
        &pin.debian_snapshot_url,
        &mut commands,
    )?;
    let cargo_home = run.path().join("cargo-home");
    let vendor = run.path().join("vendor");
    fs::create_dir(&cargo_home)?;
    fs::create_dir(&vendor)?;
    let mounts = [
        (&workspace as &Path, "/workspace", true),
        (&cargo_home as &Path, "/cargo-home", false),
    ];
    run_support_command(
        &podman,
        &mut commands,
        "cli_cargo_fetch",
        &image_ref,
        "slirp4netns:allow_host_loopback=false",
        &mounts,
        "/usr/local/cargo/bin/cargo",
        &[
            "fetch".to_owned(),
            "--locked".to_owned(),
            "--manifest-path".to_owned(),
            "/workspace/Cargo.toml".to_owned(),
        ],
        recipe,
    )?;
    let registry = locked_registry_packages(&workspace.join("Cargo.lock"))?;
    let registry_archives = registry_archives(&cargo_home, &registry)?;
    let vendor_mounts = [
        (&workspace as &Path, "/workspace", true),
        (&cargo_home as &Path, "/cargo-home", false),
        (&vendor as &Path, "/vendor", false),
    ];
    run_support_command(
        &podman,
        &mut commands,
        "cli_cargo_vendor",
        &image_ref,
        "none",
        &vendor_mounts,
        "/usr/local/cargo/bin/cargo",
        &[
            "vendor".to_owned(),
            "--locked".to_owned(),
            "--versioned-dirs".to_owned(),
            "--manifest-path".to_owned(),
            "/workspace/Cargo.toml".to_owned(),
            "/vendor".to_owned(),
        ],
        recipe,
    )?;
    verify_vendor_packages(&vendor, &registry)?;
    let (vendor_tree_sha256, vendor_inventory_sha256) = vendor_inventory(&vendor, &run)?;
    let archive = run.path().join("compiler.oci.tar");
    let save = record_command(
        &podman,
        &mut commands,
        "cli_support_image_save",
        &[
            "save".to_owned(),
            "--format=oci-archive".to_owned(),
            "--output".to_owned(),
            archive.display().to_string(),
            image_ref.clone(),
        ],
    )?;
    ensure_success(&save, "save self-contained Rust compiler OCI archive")?;
    let observed = crate::oci_archive::inspect_oci_archive(&archive, PLATFORM)?;
    if observed
        .reference_name
        .as_deref()
        .is_some_and(|name| name != image_ref.as_str())
    {
        return Err(invalid(
            "saved compiler OCI archive has a different local image reference",
        ));
    }
    crate::oci_archive::verify_oci_archive(
        &archive,
        observed.archive_size_bytes,
        &observed.archive_sha256,
        &observed.manifest_digest,
        &observed.config_digest,
        PLATFORM,
    )?;
    let compiler_image = CompilerImageArtifact {
        image_reference: image_ref,
        archive_path: archive.display().to_string(),
        archive_size_bytes: observed.archive_size_bytes,
        archive_sha256: observed.archive_sha256,
        image_digest: observed.manifest_digest.clone(),
        manifest_digest: observed.manifest_digest,
        config_digest: observed.config_digest,
        platform: observed.platform,
    };
    let receipt = CliProvisionReceipt {
        schema_version: 1,
        status: "candidate-complete".to_owned(),
        recipe_identity: recipe.recipe().identity.clone(),
        recipe_version: recipe.recipe().recipe_version.clone(),
        source_tree_sha256: tree_sha256(&workspace.join("crates/fc-image-builder"))?,
        workspace_manifest_sha256: sha256_file(&workspace.join("Cargo.toml"))?,
        workspace_tree_sha256,
        cargo_lock_sha256: sha256_file(&workspace.join("Cargo.lock"))?,
        rust_toolchain_sha256: sha256_file(&workspace.join("rust-toolchain.toml"))?,
        base_image: pin.base_image.clone(),
        base_index_oci_digest: pin.base_image_index_digest.clone(),
        base_oci_digest: pin.base_image_digest.clone(),
        base_platform: PLATFORM.to_owned(),
        base_target_preinstalled,
        base_linker_preinstalled,
        compiler_image,
        debian_support,
        rust_target,
        registry_archives,
        vendor_tree_path: vendor.display().to_string(),
        vendor_tree_sha256,
        vendor_inventory_path: run
            .path()
            .join("vendor-inventory.tsv")
            .display()
            .to_string(),
        vendor_inventory_sha256,
        rustc,
        cargo,
        linker,
        commands,
    };
    write_receipt(&run, "cli-provision-receipt.json", &receipt)
}

fn expected_hash(observed: &str, pinned: &str, label: &str) -> Result<(), Box<dyn Error>> {
    if !is_sha256(observed) || !is_sha256(pinned) || !observed.eq_ignore_ascii_case(pinned) {
        return Err(invalid(format!("{label} differs from its recipe pin")));
    }
    Ok(())
}

fn receipt_file(parent: &Path, value: &str) -> Result<PathBuf, Box<dyn Error>> {
    let path = Path::new(value);
    if path.file_name().is_none() || !path.is_absolute() {
        return Err(invalid("receipt artifact path escapes the provision run"));
    }
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid("receipt artifact is not a regular file"));
    }
    let resolved = fs::canonicalize(path)?;
    if !resolved.starts_with(fs::canonicalize(parent)?) {
        return Err(invalid("receipt artifact path escapes the provision run"));
    }
    Ok(resolved)
}

fn verify_command_logs(parent: &Path, command: &CommandReceipt) -> Result<(), Box<dyn Error>> {
    for (name, expected) in [
        (&command.stdout_path, &command.stdout_sha256),
        (&command.stderr_path, &command.stderr_sha256),
    ] {
        let path = receipt_file(parent, &parent.join(name).display().to_string())?;
        if !is_sha256(expected) || sha256_file(&path)? != *expected {
            return Err(invalid("recorded command log differs from its receipt"));
        }
    }
    Ok(())
}

#[expect(
    clippy::too_many_lines,
    reason = "receipt verification audits the complete compiler input contract"
)]
fn verify_provision(
    recipe: &RecipeFile,
    provision_path: &Path,
) -> Result<CliProvisionReceipt, Box<dyn Error>> {
    let provision: CliProvisionReceipt = read_receipt(provision_path)?;
    let pin = &recipe.recipe().rust_cli_builder;
    let parent = provision_path
        .parent()
        .ok_or_else(|| invalid("provision receipt has no parent"))?;
    if provision.schema_version != 1
        || provision.status != "candidate-complete"
        || provision.recipe_identity != recipe.recipe().identity
        || provision.recipe_version != recipe.recipe().recipe_version
        || provision.base_image != pin.base_image
        || provision.base_index_oci_digest != pin.base_image_index_digest
        || provision.base_oci_digest != pin.base_image_digest
        || provision.base_platform != PLATFORM
        || provision.debian_support.snapshot_url != pin.debian_snapshot_url
        || provision.debian_support.signing_keyring_container_path
            != "/usr/share/keyrings/debian-archive-keyring.gpg"
        || !provision
            .debian_support
            .apt_get
            .path
            .starts_with("/usr/bin/")
        || !is_sha256(&provision.debian_support.apt_get.sha256)
        || !provision.debian_support.gpgv.path.starts_with("/usr/bin/")
        || !is_sha256(&provision.debian_support.gpgv.sha256)
        || provision.debian_support.apt_version != provision.debian_support.apt_get.version
        || provision.compiler_image.platform != PLATFORM
        || provision.compiler_image.image_reference.is_empty()
        || provision.vendor_tree_path != parent.join("vendor").display().to_string()
        || provision.rust_target.installed_target_tree_path
            != parent.join("rust-target").display().to_string()
        || provision.rust_target.manifest_url != format!("{RUST_DIST}/channel-rust-1.98.1.toml")
        || provision.rust_target.manifest_sha256_url
            != format!("{RUST_DIST}/channel-rust-1.98.1.toml.sha256")
    {
        return Err(invalid("provision receipt identity differs from recipe"));
    }
    expected_hash(
        &provision.source_tree_sha256,
        &pin.source_tree_sha256,
        "source tree",
    )?;
    expected_hash(
        &provision.workspace_manifest_sha256,
        &pin.workspace_manifest_sha256,
        "workspace manifest",
    )?;
    expected_hash(
        &provision.cargo_lock_sha256,
        &pin.cargo_lock_sha256,
        "Cargo.lock",
    )?;
    expected_hash(
        &provision.rust_toolchain_sha256,
        &pin.rust_toolchain_sha256,
        "rust toolchain",
    )?;
    if source_tree_sha256_at_workspace_input(recipe, &pin.source_root).as_deref()
        != Some(provision.source_tree_sha256.as_str())
    {
        return Err(invalid(
            "current CLI source tree differs from provision receipt",
        ));
    }
    for (relative, expected) in [
        (
            &pin.workspace_manifest_path,
            &provision.workspace_manifest_sha256,
        ),
        (&pin.cargo_lock_path, &provision.cargo_lock_sha256),
        (&pin.rust_toolchain_path, &provision.rust_toolchain_sha256),
    ] {
        verify_hash(&recipe.resolve_workspace_input(relative)?, expected)?;
    }
    let workspace = parent.join("workspace");
    if tree_sha256(&workspace)? != provision.workspace_tree_sha256
        || tree_sha256(&workspace.join("crates/fc-image-builder"))? != provision.source_tree_sha256
    {
        return Err(invalid(
            "provision workspace changed after receipt creation",
        ));
    }
    verify_hash(
        &workspace.join("Cargo.toml"),
        &provision.workspace_manifest_sha256,
    )?;
    verify_hash(&workspace.join("Cargo.lock"), &provision.cargo_lock_sha256)?;
    verify_hash(
        &workspace.join("rust-toolchain.toml"),
        &provision.rust_toolchain_sha256,
    )?;
    let vendor = parent.join("vendor");
    if tree_sha256(&vendor)? != provision.vendor_tree_sha256 {
        return Err(invalid("provision vendor tree changed"));
    }
    let vendor_inventory_path = receipt_file(parent, &provision.vendor_inventory_path)?;
    verify_hash(&vendor_inventory_path, &provision.vendor_inventory_sha256)?;
    if fs::read_to_string(&vendor_inventory_path)? != vendor_inventory_content(&vendor)? {
        return Err(invalid("vendor inventory omits or misidentifies a file"));
    }
    let registry = locked_registry_packages(&workspace.join("Cargo.lock"))?;
    verify_vendor_packages(&vendor, &registry)?;
    if registry.len() != provision.registry_archives.len() {
        return Err(invalid(
            "provision registry count differs from current lock",
        ));
    }
    for (package, archive) in registry.iter().zip(&provision.registry_archives) {
        if package.name != archive.name
            || package.version != archive.version
            || package.source.as_deref() != Some(archive.source.as_str())
            || package.checksum.as_deref() != Some(archive.sha256.as_str())
            || verify_hash(
                &receipt_file(parent, &archive.archive_path)?,
                &archive.sha256,
            )? != archive.size_bytes
        {
            return Err(invalid(
                "provision registry archive differs from Cargo.lock",
            ));
        }
    }
    let image = &provision.compiler_image;
    let archive = receipt_file(parent, &image.archive_path)?;
    let verified_image = crate::oci_archive::verify_oci_archive(
        &archive,
        image.archive_size_bytes,
        &image.archive_sha256,
        &image.manifest_digest,
        &image.config_digest,
        &image.platform,
    )?;
    if verified_image
        .reference_name
        .as_deref()
        .is_some_and(|name| name != image.image_reference.as_str())
        || image.image_digest != image.manifest_digest
    {
        return Err(invalid(
            "compiler archive reference differs from provision receipt",
        ));
    }
    if verify_hash(
        &receipt_file(parent, &provision.debian_support.apt_lists_archive_path)?,
        &provision.debian_support.apt_lists_archive_sha256,
    )? != provision.debian_support.apt_lists_archive_size_bytes
    {
        return Err(invalid(
            "APT lists archive size differs from provision receipt",
        ));
    }
    let package_index = receipt_file(parent, &provision.debian_support.packages_index_path)?;
    verify_hash(
        &package_index,
        &provision.debian_support.packages_index_sha256,
    )?;
    verify_hash(
        &receipt_file(parent, &provision.debian_support.deb_inventory_path)?,
        &provision.debian_support.deb_inventory_sha256,
    )?;
    let package_records = index_records(&package_index)?;
    verify_hash(
        &receipt_file(parent, &provision.debian_support.signing_keyring_path)?,
        &provision.debian_support.signing_keyring_sha256,
    )?;
    verify_hash(
        &receipt_file(parent, &provision.rust_target.manifest_path)?,
        &provision.rust_target.manifest_sha256,
    )?;
    verify_hash(
        &receipt_file(parent, &provision.rust_target.manifest_sha256_sidecar_path)?,
        &provision.rust_target.manifest_sha256_sidecar_sha256,
    )?;
    let manifest_path = receipt_file(parent, &provision.rust_target.manifest_path)?;
    let sidecar = fs::read_to_string(receipt_file(
        parent,
        &provision.rust_target.manifest_sha256_sidecar_path,
    )?)?;
    if sidecar.split_whitespace().next() != Some(provision.rust_target.manifest_sha256.as_str()) {
        return Err(invalid("Rust dist checksum sidecar differs from manifest"));
    }
    let (archive_url, archive_hash) = parse_rust_target_manifest(&manifest_path)?;
    if archive_url != provision.rust_target.target_archive_url
        || archive_hash != provision.rust_target.target_archive_sha256
    {
        return Err(invalid(
            "Rust target archive differs from verified dist manifest",
        ));
    }
    for index in &provision.debian_support.indexes {
        if verify_hash(&receipt_file(parent, &index.path)?, &index.sha256)? != index.size_bytes {
            return Err(invalid(
                "Debian signed index size differs from provision receipt",
            ));
        }
    }
    let inrelease = provision
        .debian_support
        .indexes
        .iter()
        .find(|index| index.path.ends_with("_InRelease"))
        .ok_or_else(|| invalid("provision receipt omits Bookworm InRelease"))?;
    verify_bookworm_inrelease(&receipt_file(parent, &inrelease.path)?)?;
    verify_hash(
        &receipt_file(parent, &provision.debian_support.solver_path)?,
        &provision.debian_support.solver_sha256,
    )?;
    for package in &provision.debian_support.packages {
        if verify_hash(
            &receipt_file(parent, &package.archive_path)?,
            &package.sha256,
        )? != package.size_bytes
        {
            return Err(invalid(
                "Debian package size differs from provision receipt",
            ));
        }
        let signed = package_records
            .get(&(
                package.name.clone(),
                package.version.clone(),
                package.architecture.clone(),
            ))
            .ok_or_else(|| invalid("provision .deb absent from signed Packages index"))?;
        let relative = signed
            .get("Filename")
            .ok_or_else(|| invalid("signed package lacks Filename"))?;
        if package.source_url != format!("{}{relative}", provision.debian_support.snapshot_url)
            || signed.get("SHA256") != Some(&package.sha256)
            || signed
                .get("Size")
                .and_then(|value| value.parse::<u64>().ok())
                != Some(package.size_bytes)
        {
            return Err(invalid("Debian package identity differs from signed index"));
        }
    }
    if verify_hash(
        &receipt_file(parent, &provision.rust_target.target_archive_path)?,
        &provision.rust_target.target_archive_sha256,
    )? != provision.rust_target.target_archive_size_bytes
    {
        return Err(invalid(
            "Rust target archive size differs from provision receipt",
        ));
    }
    if tree_sha256(Path::new(&provision.rust_target.installed_target_tree_path))?
        != provision.rust_target.installed_target_tree_sha256
    {
        return Err(invalid(
            "installed Rust target tree differs from provision receipt",
        ));
    }
    for command in &provision.commands {
        if command.status_code != 0 {
            return Err(invalid("provision receipt records a failed command"));
        }
        verify_command_logs(parent, command)?;
    }
    let base_probe = provision
        .commands
        .iter()
        .find(|command| command.stdout_path == "cli_base_support_probe.stdout.log")
        .ok_or_else(|| invalid("provision receipt omits pinned base support inspection"))?;
    let probe_output = fs::read_to_string(parent.join(&base_probe.stdout_path))?;
    if provision.base_target_preinstalled != probe_output.lines().any(|line| line.trim() == TARGET)
        || provision.base_linker_preinstalled
            != probe_output
                .lines()
                .any(|line| line.trim().starts_with("/usr/bin/x86_64-linux-musl-gcc"))
    {
        return Err(invalid(
            "base target/linker inspection differs from provision receipt",
        ));
    }
    Ok(provision)
}

#[expect(
    clippy::too_many_arguments,
    reason = "the compiler command explicitly binds each isolated path and tool"
)]
fn compiler_run_argv(
    recipe: &RecipeFile,
    image: &str,
    workspace: &Path,
    vendor: &Path,
    cargo_home: &Path,
    target: &Path,
    cargo: &str,
    rustc: &str,
    linker: &str,
) -> Result<Vec<String>, Box<dyn Error>> {
    let pin = &recipe.recipe().rust_cli_builder;
    for path in [workspace, vendor, cargo_home, target] {
        if path.to_string_lossy().contains(',') {
            return Err(invalid("compiler mount path contains a comma"));
        }
    }
    if !linker.starts_with("/usr/bin/") || linker.contains(',') {
        return Err(invalid(
            "provision linker path is not a fixed /usr/bin tool",
        ));
    }
    if !cargo.starts_with("/usr/local/rustup/toolchains/") || !cargo.ends_with("/bin/cargo") {
        return Err(invalid(
            "provision cargo path is not a Rust toolchain binary",
        ));
    }
    if !rustc.starts_with("/usr/local/rustup/toolchains/") || !rustc.ends_with("/bin/rustc") {
        return Err(invalid(
            "provision rustc path is not a Rust toolchain binary",
        ));
    }
    let mut args = vec![
        "run".to_owned(),
        "--rm".to_owned(),
        "--pull=never".to_owned(),
        "--userns=keep-id".to_owned(),
        "--network=none".to_owned(),
        "--read-only".to_owned(),
        "--cap-drop=all".to_owned(),
        format!("--cpus={}", pin.cpu_count),
        format!("--memory={}m", pin.memory_mib),
        format!("--pids-limit={}", pin.pids_limit),
        "--storage-opt".to_owned(),
        format!("overlay.size={}", pin.workspace_limit_bytes),
        "--mount".to_owned(),
        format!("type=bind,src={},dst=/workspace,ro", workspace.display()),
        "--mount".to_owned(),
        format!("type=bind,src={},dst=/vendor,ro", vendor.display()),
        "--mount".to_owned(),
        format!("type=bind,src={},dst=/cargo-home", cargo_home.display()),
        "--mount".to_owned(),
        format!("type=bind,src={},dst=/target", target.display()),
        "--tmpfs=/tmp:rw,noexec,nosuid,size=256m".to_owned(),
        "--workdir=/workspace".to_owned(),
        "--env=CARGO_HOME=/cargo-home".to_owned(),
        "--env=CARGO_TARGET_DIR=/target".to_owned(),
        format!(
            "--env=SOURCE_DATE_EPOCH={}",
            recipe.recipe().source_date_epoch
        ),
        "--env=TZ=UTC".to_owned(),
        "--env=LANG=C.UTF-8".to_owned(),
        format!("--env=RUSTC={rustc}"),
        format!("--env=CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER={linker}"),
        format!("--env=CC_x86_64_unknown_linux_musl={linker}"),
        format!("--entrypoint={cargo}"),
        image.to_owned(),
    ];
    args.extend(
        [
            "build",
            "--frozen",
            "--offline",
            "--release",
            "--target",
            TARGET,
            "-p",
            "fc-image-builder",
        ]
        .map(str::to_owned),
    );
    Ok(args)
}

/// Compile the locked image builder from a fresh workspace snapshot and a
/// previously reviewed candidate provision receipt, with no network access.
///
/// # Errors
///
/// Fails if any provision artifact changed, the OCI image is incomplete, the
/// private-store load differs, or the offline executable is not a static ELF.
#[expect(
    clippy::too_many_lines,
    reason = "the offline compile and receipt emission share one failure boundary"
)]
pub fn compile_pinned_cli(
    recipe: &RecipeFile,
    provision_receipt: &Path,
    output_root: &Path,
    run_id: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    if run_id.len() > 64 {
        return Err(invalid("CLI compile run ID exceeds 64 characters"));
    }
    let provision_receipt = fs::canonicalize(provision_receipt)?;
    verify_hash(
        &provision_receipt,
        &recipe.recipe().rust_cli_builder.provision_receipt_sha256,
    )?;
    let provision = verify_provision(recipe, &provision_receipt)?;
    let run = RunDirectory::create(output_root, &format!("{run_id}-cli-compile"))?;
    let (workspace, workspace_tree_sha256) = stage_workspace(recipe, &run)?;
    if workspace_tree_sha256 != provision.workspace_tree_sha256 {
        return Err(invalid(
            "fresh compile workspace differs from provision workspace",
        ));
    }
    let provision_root = provision_receipt
        .parent()
        .ok_or_else(|| invalid("provision receipt has no parent"))?;
    let vendor = run.path().join("vendor");
    copy_tree(&provision_root.join("vendor"), &vendor)?;
    if tree_sha256(&vendor)? != provision.vendor_tree_sha256 {
        return Err(invalid("fresh vendor copy differs from provision receipt"));
    }
    let cargo_home = run.path().join("cargo-home");
    let target_dir = run.path().join("target");
    fs::create_dir(&cargo_home)?;
    fs::create_dir(&target_dir)?;
    fs::write(cargo_home.join("config.toml"), CARGO_VENDOR_CONFIG)?;
    if fs::read_dir(&cargo_home)?.count() != 1 {
        return Err(invalid(
            "offline Cargo home contains unexpected pre-build files",
        ));
    }
    let cargo_config_sha256 = sha256_file(&cargo_home.join("config.toml"))?;
    let mut commands = Vec::new();
    let podman = PodmanExecutor::new(recipe, &run)?;
    let image = &provision.compiler_image;
    let archive = receipt_file(provision_root, &image.archive_path)?;
    let verified_image = crate::oci_archive::verify_oci_archive(
        &archive,
        image.archive_size_bytes,
        &image.archive_sha256,
        &image.manifest_digest,
        &image.config_digest,
        PLATFORM,
    )?;
    if verified_image
        .reference_name
        .as_deref()
        .is_some_and(|name| name != image.image_reference.as_str())
    {
        return Err(invalid(
            "compiler archive reference changed before private-store load",
        ));
    }
    let load = record_command(
        &podman,
        &mut commands,
        "cli_compiler_load",
        &[
            "load".to_owned(),
            "--input".to_owned(),
            archive.display().to_string(),
        ],
    )?;
    ensure_success(&load, "load verified compiler archive")?;
    let tag = record_command(
        &podman,
        &mut commands,
        "cli_compiler_tag",
        &[
            "image".to_owned(),
            "tag".to_owned(),
            image.config_digest.clone(),
            image.image_reference.clone(),
        ],
    )?;
    ensure_success(&tag, "tag verified compiler image by config digest")?;
    let inspect = record_command(
        &podman,
        &mut commands,
        "cli_compiler_inspect",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Id}} {{.Os}}/{{.Architecture}}".to_owned(),
            image.image_reference.clone(),
        ],
    )?;
    ensure_success(&inspect, "inspect loaded compiler image")?;
    let expected_inspect = format!("{} {PLATFORM}", image.config_digest);
    if read_command_stdout(&run, &inspect)?.trim() != expected_inspect {
        return Err(invalid(
            "loaded compiler identity differs from verified OCI archive",
        ));
    }
    let build_argv = compiler_run_argv(
        recipe,
        &image.image_reference,
        &workspace,
        &vendor,
        &cargo_home,
        &target_dir,
        &provision.cargo.path,
        &provision.rustc.path,
        &provision.linker.path,
    )?;
    let build = record_command(&podman, &mut commands, "cli_compiler_build", &build_argv)?;
    ensure_success(&build, "compile pinned Rust CLI offline")?;
    let compiled_executable = target_dir.join(TARGET).join("release/fc-image-builder");
    let output = run.path().join("fc-image-builder");
    regular_file(&compiled_executable)?;
    fs::copy(&compiled_executable, &output)?;
    let output_size_bytes = regular_file(&output)?;
    let output_sha256 = sha256_file(&output)?;
    let host_abi = crate::build::verify_static_x86_64_elf(&output)?;
    if host_abi != ABI || recipe.recipe().rust_cli_builder.target != TARGET {
        return Err(invalid("compiled CLI ABI or target differs from recipe"));
    }
    let pin = &recipe.recipe().rust_cli_builder;
    if !pin.output_executable_sha256.is_empty() {
        expected_hash(
            &output_sha256,
            &pin.output_executable_sha256,
            "compiled executable",
        )?;
    }
    if pin.output_executable_size_bytes != 0
        && pin.output_executable_size_bytes != output_size_bytes
    {
        return Err(invalid("compiled executable size differs from recipe pin"));
    }
    let linker_path = provision.linker.path.clone();
    let rustc_path = provision.rustc.path.clone();
    let receipt = CliCompileReceipt {
        schema_version: 1,
        provision_receipt_path: provision_receipt.display().to_string(),
        provision_receipt_sha256: sha256_file(&provision_receipt)?,
        compiler_image: image.clone(),
        source_tree_sha256: provision.source_tree_sha256,
        workspace_manifest_sha256: provision.workspace_manifest_sha256,
        workspace_tree_sha256,
        cargo_lock_sha256: provision.cargo_lock_sha256,
        rust_toolchain_sha256: provision.rust_toolchain_sha256,
        vendor_tree_sha256: provision.vendor_tree_sha256,
        vendor_inventory_sha256: provision.vendor_inventory_sha256,
        cargo_config_sha256,
        rustc: provision.rustc,
        cargo: provision.cargo,
        linker: provision.linker,
        target: TARGET.to_owned(),
        host_abi,
        output_path: output.display().to_string(),
        output_size_bytes,
        output_sha256,
        build_environment: vec![
            ("CARGO_HOME".to_owned(), "/cargo-home".to_owned()),
            ("CARGO_TARGET_DIR".to_owned(), "/target".to_owned()),
            (
                "SOURCE_DATE_EPOCH".to_owned(),
                recipe.recipe().source_date_epoch.clone(),
            ),
            ("TZ".to_owned(), "UTC".to_owned()),
            ("LANG".to_owned(), "C.UTF-8".to_owned()),
            ("RUSTC".to_owned(), rustc_path),
            (
                "CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER".to_owned(),
                linker_path.clone(),
            ),
            ("CC_x86_64_unknown_linux_musl".to_owned(), linker_path),
        ],
        commands,
    };
    write_receipt(&run, "cli-compile-receipt.json", &receipt)
}

/// Verify a pinned compile receipt against the exact running CLI executable.
///
/// # Errors
///
/// Fails closed for missing recipe pins, changed source/provision artifacts,
/// changed command logs, or a non-static or nonmatching executable.
#[expect(
    clippy::too_many_lines,
    reason = "verification checks the full compiled executable provenance receipt"
)]
pub fn verify_cli_compile_receipt(
    recipe: &RecipeFile,
    compile_receipt: &Path,
    executable: &Path,
) -> Result<VerifiedCliIdentity, Box<dyn Error>> {
    let compile_receipt = fs::canonicalize(compile_receipt)?;
    let pin = &recipe.recipe().rust_cli_builder;
    verify_hash(&compile_receipt, &pin.compile_receipt_sha256)?;
    let compiled: CliCompileReceipt = read_receipt(&compile_receipt)?;
    let provision_path = Path::new(&compiled.provision_receipt_path);
    verify_hash(provision_path, &pin.provision_receipt_sha256)?;
    if sha256_file(provision_path)? != compiled.provision_receipt_sha256 {
        return Err(invalid(
            "compile receipt names a different provision receipt",
        ));
    }
    let provision = verify_provision(recipe, provision_path)?;
    if compiled.schema_version != 1
        || compiled.compiler_image != provision.compiler_image
        || compiled.source_tree_sha256 != provision.source_tree_sha256
        || compiled.workspace_manifest_sha256 != provision.workspace_manifest_sha256
        || compiled.workspace_tree_sha256 != provision.workspace_tree_sha256
        || compiled.cargo_lock_sha256 != provision.cargo_lock_sha256
        || compiled.rust_toolchain_sha256 != provision.rust_toolchain_sha256
        || compiled.vendor_tree_sha256 != provision.vendor_tree_sha256
        || compiled.vendor_inventory_sha256 != provision.vendor_inventory_sha256
        || compiled.rustc != provision.rustc
        || compiled.cargo != provision.cargo
        || compiled.linker != provision.linker
        || compiled.target != TARGET
        || compiled.host_abi != ABI
    {
        return Err(invalid("compile receipt differs from provision identity"));
    }
    let expected_environment = vec![
        ("CARGO_HOME".to_owned(), "/cargo-home".to_owned()),
        ("CARGO_TARGET_DIR".to_owned(), "/target".to_owned()),
        (
            "SOURCE_DATE_EPOCH".to_owned(),
            recipe.recipe().source_date_epoch.clone(),
        ),
        ("TZ".to_owned(), "UTC".to_owned()),
        ("LANG".to_owned(), "C.UTF-8".to_owned()),
        ("RUSTC".to_owned(), compiled.rustc.path.clone()),
        (
            "CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER".to_owned(),
            compiled.linker.path.clone(),
        ),
        (
            "CC_x86_64_unknown_linux_musl".to_owned(),
            compiled.linker.path.clone(),
        ),
    ];
    if compiled.build_environment != expected_environment {
        return Err(invalid(
            "compile receipt build environment differs from recipe",
        ));
    }
    let parent = compile_receipt
        .parent()
        .ok_or_else(|| invalid("compile receipt has no parent"))?;
    if tree_sha256(&parent.join("workspace"))? != compiled.workspace_tree_sha256
        || tree_sha256(&parent.join("vendor"))? != compiled.vendor_tree_sha256
    {
        return Err(invalid(
            "compiled workspace or vendor snapshot changed after build",
        ));
    }
    let cargo_config = parent.join("cargo-home/config.toml");
    verify_hash(&cargo_config, &compiled.cargo_config_sha256)?;
    if fs::read_to_string(&cargo_config)? != CARGO_VENDOR_CONFIG {
        return Err(invalid("offline Cargo source replacement config changed"));
    }
    if Path::new(&compiled.output_path) != parent.join("fc-image-builder") {
        return Err(invalid("compile receipt executable path is not run-owned"));
    }
    if verify_hash(&parent.join("fc-image-builder"), &compiled.output_sha256)?
        != compiled.output_size_bytes
    {
        return Err(invalid(
            "compiled output artifact changed after receipt creation",
        ));
    }
    let size = verify_hash(executable, &pin.output_executable_sha256)?;
    if size != pin.output_executable_size_bytes
        || size != compiled.output_size_bytes
        || sha256_file(executable)? != compiled.output_sha256
        || crate::build::verify_static_x86_64_elf(executable)? != ABI
    {
        return Err(invalid("running CLI executable differs from compiled pin"));
    }
    let current = std::env::current_exe()?;
    if sha256_file(&current)? != compiled.output_sha256 {
        return Err(invalid("current process is not the pinned compiled CLI"));
    }
    let build = compiled
        .commands
        .iter()
        .find(|command| command.stdout_path == "cli_compiler_build.stdout.log")
        .ok_or_else(|| invalid("compile receipt omits offline build command"))?;
    let load = compiled
        .commands
        .iter()
        .find(|command| command.stdout_path == "cli_compiler_load.stdout.log")
        .ok_or_else(|| invalid("compile receipt omits private-store image load"))?;
    let tag = compiled
        .commands
        .iter()
        .find(|command| command.stdout_path == "cli_compiler_tag.stdout.log")
        .ok_or_else(|| invalid("compile receipt omits verified image tag"))?;
    let inspect = compiled
        .commands
        .iter()
        .find(|command| command.stdout_path == "cli_compiler_inspect.stdout.log")
        .ok_or_else(|| invalid("compile receipt omits loaded image inspection"))?;
    let archive_path = &compiled.compiler_image.archive_path;
    if !load.args.ends_with(&[
        "load".to_owned(),
        "--input".to_owned(),
        archive_path.clone(),
    ]) || !tag.args.ends_with(&[
        "image".to_owned(),
        "tag".to_owned(),
        compiled.compiler_image.config_digest.clone(),
        compiled.compiler_image.image_reference.clone(),
    ]) || !inspect.args.ends_with(&[
        "image".to_owned(),
        "inspect".to_owned(),
        "--format={{.Id}} {{.Os}}/{{.Architecture}}".to_owned(),
        compiled.compiler_image.image_reference.clone(),
    ]) {
        return Err(invalid(
            "compile receipt image load/tag/inspect chain is incomplete",
        ));
    }
    let inspected = fs::read_to_string(parent.join(&inspect.stdout_path))?;
    if inspected.trim() != format!("{} {PLATFORM}", compiled.compiler_image.config_digest) {
        return Err(invalid(
            "compile receipt loaded image identity differs from OCI archive",
        ));
    }
    let expected = compiler_run_argv(
        recipe,
        &compiled.compiler_image.image_reference,
        &parent.join("workspace"),
        &parent.join("vendor"),
        &parent.join("cargo-home"),
        &parent.join("target"),
        &compiled.cargo.path,
        &compiled.rustc.path,
        &compiled.linker.path,
    )?;
    if build.status_code != 0 || !build.args.ends_with(&expected) {
        return Err(invalid(
            "compile receipt does not prove an offline frozen build",
        ));
    }
    for command in &compiled.commands {
        if command.status_code != 0 {
            return Err(invalid("compile receipt records a failed command"));
        }
        verify_command_logs(parent, command)?;
    }
    Ok(VerifiedCliIdentity {
        source_tree_sha256: compiled.source_tree_sha256,
        workspace_manifest_sha256: compiled.workspace_manifest_sha256,
        cargo_lock_sha256: compiled.cargo_lock_sha256,
        rust_toolchain_sha256: compiled.rust_toolchain_sha256,
        vendor_tree_sha256: compiled.vendor_tree_sha256,
        output_sha256: compiled.output_sha256,
        output_size_bytes: compiled.output_size_bytes,
        base_index_oci_digest: provision.base_index_oci_digest,
        base_oci_digest: provision.base_oci_digest,
        compiler_image: compiled.compiler_image,
        rustc: compiled.rustc,
        cargo: compiled.cargo,
        linker: compiled.linker,
        target: compiled.target,
        host_abi: compiled.host_abi,
    })
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::Path;
    use std::time::{SystemTime, UNIX_EPOCH};

    use crate::RecipeFile;

    use super::compile_pinned_cli;

    #[test]
    fn changed_provision_receipt_is_rejected_before_run_creation()
    -> Result<(), Box<dyn std::error::Error>> {
        let bytes = include_str!("../../../images/terrabase-noble-podman-v2/recipe.toml");
        let pinned = bytes.replace(
            "provision_receipt_sha256 = \"\"",
            &format!("provision_receipt_sha256 = \"{}\"", "0".repeat(64)),
        );
        assert_ne!(bytes, pinned);
        let recipe = RecipeFile::parse(Path::new("recipe.toml"), pinned.into_bytes())?;
        let nonce = SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos();
        let root = std::env::temp_dir().join(format!(
            "fc-image-builder-provision-pin-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&root)?;
        let provision = root.join("provision.json");
        fs::write(&provision, b"{}")?;
        let output_root = root.join("output");

        let error = compile_pinned_cli(&recipe, &provision, &output_root, "pin-test")
            .expect_err("changed provision receipt must be rejected");
        assert!(error.to_string().contains("SHA-256 differs from pin"));
        assert!(!output_root.exists(), "no compile run may be created");
        fs::remove_dir_all(root)?;
        Ok(())
    }
}
