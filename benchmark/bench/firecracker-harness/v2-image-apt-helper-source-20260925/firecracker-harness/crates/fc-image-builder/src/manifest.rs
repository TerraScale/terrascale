use std::collections::BTreeMap;
use std::error::Error;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::package_lock::{CandidatePackageLock, canonical_inventory_sha256};

const IDENTITY: &str = "terrabase-noble-podman-v1";
const TARBALL_IDENTITY: &str = "terrabase-noble-podman-v2";
const KERNEL_COMMIT: &str = "069c93d2b7bddda87c472453c38c450faf86f999";
const FIRECRACKER_COMMIT: &str = "f82c0bd0f0a74015642a0d452880f3ad10147b14";
const UBUNTU_BASE_SHA256: &str = "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86";
const CANONICAL_FINGERPRINT: &str = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";
const APT_FINGERPRINT: &str = "F6ECB3762474EDA9D21B7022871920D1991BC93C";
const UBUNTU_ARCHIVE_KEYRING_PATH: &str = "/usr/share/keyrings/ubuntu-archive-keyring.gpg";
const BUSYBOX_DIGEST: &str =
    "sha256:dc88b80842580654294472735332ec7c7789a4010ec9a68b69f9b88c19abede6";
const RUST_BUILDER_DIGEST: &str =
    "sha256:c49256cbe5ea0188bc658a689500d70c41eb51f009a7a7be209caf60a944f3ec";
const RUST_BUILDER_INDEX_DIGEST: &str =
    "sha256:93ce27a88655056a51dbdd8f5f2d7ddc071c7b0070fb288a37b5a285fc83971e";
const UBUNTU_BUILDER_DIGEST: &str =
    "sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4";
const UBUNTU_BUILDER_INDEX_DIGEST: &str =
    "sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3";
const HOST_PODMAN_SHA256: &str = "402d507d68ed889dec999e688bd242b5ddb634b3bdc9f03ef9243e66baa367fc";
const FIRECRACKER_RELEASE_URL: &str = "https://github.com/firecracker-microvm/firecracker/releases/download/v1.17.0/firecracker-v1.17.0-x86_64.tgz";
const FIRECRACKER_RELEASE_ARCHIVE_SHA256: &str =
    "06094a1108ae9e82aa4c23a775aa92758f53f1175d422270d9d6162cb9ade558";
const FIRECRACKER_RELEASE_MEMBER: &str = "release-v1.17.0-x86_64/firecracker-v1.17.0-x86_64";
const FIRECRACKER_RELEASE_EXECUTABLE_SHA256: &str =
    "99ad0f5cd0514a88aad0e9ae8cfdb3cc3b4ab9d190e1194602406c786b5de7a5";
const FIRECRACKER_RELEASE_ARCHIVE_SIZE: u64 = 7_464_385;
const FIRECRACKER_RELEASE_EXECUTABLE_SIZE: u64 = 3_676_680;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ImageManifest {
    pub schema_version: u32,
    pub recipe: RecipeIdentityReceipt,
    pub qualification_status: String,
    pub reproducibility_status: ReproducibilityStatus,
    pub reproducibility: Option<ReproducibilityReceipt>,
    pub verified_at_unix_ms: u128,
    pub bench_source: BenchSourceReceipt,
    pub kernel: KernelReceipt,
    pub firecracker_release: Option<FirecrackerReleaseReceipt>,
    pub rootfs: RootfsReceipt,
    pub guest_apt: AptReceipt,
    pub builder_apt: AptReceipt,
    pub rust_cli_builder: RustCliBuilderReceipt,
    pub ubuntu_build_builder: UbuntuBuildBuilderReceipt,
    pub host_podman: HostPodmanReceipt,
    pub oci_smoke: OciSmokeReceipt,
    pub commands: Vec<CommandReceipt>,
    pub service_images_qualified: bool,
    pub limitations: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeIdentityReceipt {
    pub identity: String,
    pub version: String,
    pub sha256: String,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ReproducibilityStatus {
    SingleBuildUnqualified,
    IdenticalTwoBuilds,
    ControlledNondeterminism,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ReproducibilityReceipt {
    pub first_run_id: String,
    pub second_run_id: String,
    pub first_kernel_sha256: String,
    pub second_kernel_sha256: String,
    pub first_rootfs_sha256: String,
    pub second_rootfs_sha256: String,
    pub differences: Vec<ReproducibilityDifference>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ReproducibilityDifference {
    pub field: String,
    pub first_value: String,
    pub second_value: String,
    pub impact: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct BenchSourceReceipt {
    pub head: String,
    pub dirty: bool,
    pub diff_sha256: Option<String>,
    pub untracked_files: Vec<SourceFileReceipt>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct SourceFileReceipt {
    pub path: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct KernelReceipt {
    pub source_kind: String,
    pub source_url: String,
    pub signing_key_url: String,
    pub signing_key_sha256: String,
    pub signing_fingerprint: String,
    pub annotated_tag: String,
    pub tag_object: String,
    pub source_revision: String,
    pub tag_signature: Option<SignatureReceipt>,
    pub source_archive_compressed: Option<SourceArtifactReceipt>,
    pub source_archive_uncompressed: SourceArtifactReceipt,
    pub source_signature: Option<KernelSourceSignatureReceipt>,
    pub firecracker_version: String,
    pub firecracker_commit: String,
    pub config_url: String,
    pub source_date_epoch: u64,
    pub config_sha256: String,
    pub kernel_policy_url: String,
    pub kernel_policy_sha256: String,
    pub custom_config_sha256: String,
    pub custom_config_diff_sha256: String,
    pub resolved_config_path: String,
    pub resolved_config_diff_path: Option<String>,
    pub builder_toolchain_lock_sha256: String,
    pub release: String,
    pub output: ArtifactOutput,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct SourceArtifactReceipt {
    pub source_url: String,
    pub path: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct KernelSourceSignatureReceipt {
    pub artifact: SourceArtifactReceipt,
    pub verification: SignatureReceipt,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct FirecrackerReleaseReceipt {
    pub archive: SourceArtifactReceipt,
    pub download: crate::DownloadReceipt,
    pub executable_member: String,
    pub executable: SourceArtifactReceipt,
    pub archive_metadata_source: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RootfsReceipt {
    pub source_url: String,
    pub source_sha256: String,
    pub sha256sums_url: String,
    pub sha256sums_sha256: String,
    pub signature_url: String,
    pub signature: SignatureReceipt,
    pub release: String,
    pub filesystem: Ext4Receipt,
    pub package_lock_path: String,
    pub package_lock_sha256: String,
    pub output: ArtifactOutput,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct SignatureReceipt {
    pub signature_sha256: String,
    pub keyring_path: String,
    pub keyring_sha256: String,
    pub signer_primary_fingerprint: String,
    pub verifier: String,
    pub verifier_sha256: String,
    pub status: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Ext4Receipt {
    pub size_bytes: u64,
    pub block_size: u32,
    pub inode_size: u32,
    pub reserved_percent: u8,
    pub label: String,
    pub uuid: String,
    pub hash_seed: String,
    pub root_tree_inventory_sha256: String,
    pub root_tree_inventory_path: String,
    pub e2fsck_exit_code: i32,
    pub e2fsck_clean: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ArtifactOutput {
    /// Relative to the directory containing the manifest; traversal and
    /// symlink components are invalid.
    pub path: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
#[allow(clippy::struct_excessive_bools)] // Independent APT solver settings are serialized as recorded facts.
pub struct AptReceipt {
    pub role: String,
    pub snapshot_url: String,
    pub snapshot_timestamp: String,
    pub architecture: String,
    pub suites: Vec<String>,
    pub components: Vec<String>,
    pub apt_version: String,
    pub apt_sha256: String,
    pub allow_unauthenticated: bool,
    pub install_recommends: bool,
    pub install_suggests: bool,
    pub check_valid_until: bool,
    pub settings_path: String,
    pub settings_sha256: String,
    pub keyring_sha256: String,
    pub initial_keyring_source: String,
    pub initial_keyring_path: String,
    pub initial_keyring_sha256: String,
    pub initial_gpgv_path: String,
    pub initial_gpgv_sha256: String,
    pub signer_primary_fingerprint: String,
    pub solver_command_argv: Vec<String>,
    pub solver_stdout_sha256: String,
    pub solver_stderr_sha256: String,
    pub solver_exit_status: i32,
    pub selected_inventory_sha256: String,
    pub package_lock_path: String,
    pub package_lock_sha256: String,
    pub indexes: Vec<AptIndexReceipt>,
    pub packages: Vec<AptPackageReceipt>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AptIndexReceipt {
    pub suite: String,
    pub component: String,
    pub architecture: String,
    pub inrelease_url: String,
    pub inrelease_sha256: String,
    pub inrelease_size_bytes: u64,
    pub packages_index_url: String,
    pub packages_index_sha256: String,
    pub packages_index_size_bytes: u64,
    pub trusted_key_source: String,
    pub trusted_key_sha256: String,
    pub gpgv_verified: bool,
    pub signature: SignatureReceipt,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AptPackageReceipt {
    pub name: String,
    pub version: String,
    pub architecture: String,
    pub component: String,
    pub suite: String,
    pub source_url: String,
    pub size_bytes: u64,
    pub deb_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AptPackageOrigin {
    pub name: String,
    pub version: String,
    pub architecture: String,
    pub suite: String,
    pub component: String,
    pub source_url: String,
    pub filename: String,
    pub size_bytes: u64,
    pub deb_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RustCliBuilderReceipt {
    pub base_image: String,
    pub base_oci_digest: String,
    pub base_index_oci_digest: String,
    pub source_tree_sha256: String,
    pub cargo_lock_path: String,
    pub cargo_lock_sha256: String,
    pub rust_toolchain_path: String,
    pub rust_toolchain_sha256: String,
    pub rust_toolchain_channel: String,
    pub target: String,
    pub executable_path: String,
    pub executable_sha256: String,
    pub rust_version: String,
    pub cargo_version: String,
    pub host_abi: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct UbuntuBuildBuilderReceipt {
    pub base_image: String,
    pub base_oci_digest: String,
    pub base_index_oci_digest: String,
    pub derived_oci_digest: String,
    pub initial_gpgv_path: String,
    pub initial_gpgv_sha256: String,
    pub initial_keyring_path: String,
    pub initial_keyring_sha256: String,
    pub package_lock_path: String,
    pub package_lock_sha256: String,
    pub build_argv: Vec<String>,
    pub tools: Vec<UbuntuToolReceipt>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct UbuntuToolReceipt {
    pub name: String,
    pub path: String,
    pub version: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct HostPodmanReceipt {
    pub path: String,
    pub version: String,
    pub sha256: String,
    pub rootless: bool,
    pub uid: u32,
    pub gid: u32,
    pub resource_limits: ResourceLimits,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ResourceLimits {
    pub cpu_count: u32,
    pub memory_mib: u32,
    pub pids_limit: u32,
    pub workspace_limit_bytes: u64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct OciSmokeReceipt {
    pub image_reference: String,
    pub image_digest: String,
    pub platform: String,
    pub archive_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CommandReceipt {
    pub program: String,
    pub program_sha256: String,
    pub args: Vec<String>,
    pub status_code: i32,
    pub stdout_path: String,
    pub stdout_sha256: String,
    pub stderr_path: String,
    pub stderr_sha256: String,
    pub elapsed_ms: u128,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RootTreeInventoryFile {
    schema_version: u32,
    entries: Vec<RootTreeInventoryEntry>,
    installed_packages: Vec<RootTreeInstalledPackage>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RootTreeInventoryEntry {
    path: String,
    kind: String,
    mode: u32,
    uid: u32,
    gid: u32,
    size_bytes: u64,
    hardlink_group: Option<String>,
    symlink_target: Option<String>,
    sha256: Option<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RootTreeInstalledPackage {
    name: String,
    version: String,
    architecture: String,
}

#[derive(Debug)]
pub enum ManifestError {
    Io(io::Error),
    Invalid(String),
}

impl fmt::Display for ManifestError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "manifest I/O error: {error}"),
            Self::Invalid(error) => write!(formatter, "invalid image manifest: {error}"),
        }
    }
}

impl Error for ManifestError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

impl From<io::Error> for ManifestError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl ImageManifest {
    /// Read and validate a regular, non-symlink JSON receipt.
    ///
    /// # Errors
    ///
    /// Returns an error when the path is unsafe, JSON is malformed, or any
    /// manifest invariant fails.
    pub fn read(path: &Path) -> Result<Self, ManifestError> {
        let metadata = fs::symlink_metadata(path)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(ManifestError::Invalid(
                "manifest path must name a regular non-symlink file".to_owned(),
            ));
        }
        let manifest: Self = serde_json::from_slice(&fs::read(path)?)
            .map_err(|error| ManifestError::Invalid(format!("invalid JSON receipt: {error}")))?;
        manifest.validate()?;
        Ok(manifest)
    }

    /// Validate completeness and all hashes/paths, then verify both produced
    /// image files against the sidecar manifest.
    ///
    /// # Errors
    ///
    /// Returns an error if the manifest or any referenced output/receipt file
    /// is unsafe, missing, or has a different size or digest.
    pub fn verify_outputs(&self, manifest_path: &Path) -> Result<(), ManifestError> {
        self.validate()?;
        verify_source_artifact(manifest_path, &self.kernel.source_archive_uncompressed)?;
        if let Some(compressed) = &self.kernel.source_archive_compressed {
            verify_source_artifact(manifest_path, compressed)?;
        }
        if let Some(signature) = &self.kernel.source_signature {
            verify_source_artifact(manifest_path, &signature.artifact)?;
        }
        if let Some(release) = &self.firecracker_release {
            verify_source_artifact(manifest_path, &release.archive)?;
            verify_source_artifact(manifest_path, &release.executable)?;
        }
        verify_file_receipt(
            manifest_path,
            &self.kernel.resolved_config_path,
            &self.kernel.custom_config_sha256,
        )?;
        if let Some(path) = &self.kernel.resolved_config_diff_path {
            verify_file_receipt(manifest_path, path, &self.kernel.custom_config_diff_sha256)?;
        }
        for output in [&self.kernel.output, &self.rootfs.output] {
            let path = resolve_output_path(manifest_path, &output.path)?;
            let metadata = fs::symlink_metadata(&path)?;
            if metadata.file_type().is_symlink() || !metadata.is_file() {
                return Err(ManifestError::Invalid(format!(
                    "image output is not a regular file: {}",
                    path.display()
                )));
            }
            if metadata.len() != output.size_bytes {
                return Err(ManifestError::Invalid(format!(
                    "image output size mismatch for {}",
                    output.path
                )));
            }
            if sha256_file(&path)? != output.sha256 {
                return Err(ManifestError::Invalid(format!(
                    "image output SHA-256 mismatch for {}",
                    output.path
                )));
            }
        }
        verify_file_receipt(
            manifest_path,
            &self.rootfs.filesystem.root_tree_inventory_path,
            &self.rootfs.filesystem.root_tree_inventory_sha256,
        )?;
        verify_inventory_package_state(manifest_path, self)?;
        verify_file_receipt(
            manifest_path,
            &self.rootfs.package_lock_path,
            &self.rootfs.package_lock_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.guest_apt.settings_path,
            &self.guest_apt.settings_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.builder_apt.settings_path,
            &self.builder_apt.settings_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.guest_apt.package_lock_path,
            &self.guest_apt.package_lock_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.builder_apt.package_lock_path,
            &self.builder_apt.package_lock_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.ubuntu_build_builder.package_lock_path,
            &self.ubuntu_build_builder.package_lock_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.rust_cli_builder.cargo_lock_path,
            &self.rust_cli_builder.cargo_lock_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.rust_cli_builder.rust_toolchain_path,
            &self.rust_cli_builder.rust_toolchain_sha256,
        )?;
        verify_file_receipt(
            manifest_path,
            &self.rust_cli_builder.executable_path,
            &self.rust_cli_builder.executable_sha256,
        )?;
        for command in &self.commands {
            verify_file_receipt(manifest_path, &command.stdout_path, &command.stdout_sha256)?;
            verify_file_receipt(manifest_path, &command.stderr_path, &command.stderr_sha256)?;
        }
        Ok(())
    }

    /// Check each recorded source, package, output, and reproducibility invariant.
    ///
    /// # Errors
    ///
    /// Returns an error if provenance is incomplete or differs from the
    /// locked candidate identities.
    #[allow(clippy::too_many_lines)] // One fail-closed schema audit keeps cross-field invariants reviewable.
    pub fn validate(&self) -> Result<(), ManifestError> {
        if self.schema_version != 2
            || ![IDENTITY, TARBALL_IDENTITY].contains(&self.recipe.identity.as_str())
            || self.recipe.version != "2.0.0"
            || !is_sha256(&self.recipe.sha256)
            || self.qualification_status != "verified-candidate"
            || self.service_images_qualified
        {
            return Err(ManifestError::Invalid(
                "identity, schema, qualification or service status is incomplete".to_owned(),
            ));
        }
        let is_tarball_candidate = self.recipe.identity == TARBALL_IDENTITY;
        validate_git_revision(&self.bench_source.head)?;
        if self.bench_source.dirty {
            validate_required_sha256(
                self.bench_source.diff_sha256.as_deref().unwrap_or_default(),
                "dirty source diff",
            )?;
        }
        for source in &self.bench_source.untracked_files {
            validate_relative(&source.path)?;
            validate_required_sha256(&source.sha256, "untracked source file")?;
        }
        validate_kernel_receipt(&self.kernel, is_tarball_candidate)?;
        match (&self.firecracker_release, is_tarball_candidate) {
            (Some(release), true) => validate_firecracker_release(release)?,
            (None, false) => {}
            _ => {
                return Err(ManifestError::Invalid(
                    "Firecracker release evidence presence differs from the image candidate"
                        .to_owned(),
                ));
            }
        }
        validate_https(&self.rootfs.source_url)?;
        validate_required_sha256(&self.rootfs.source_sha256, "Ubuntu base source")?;
        validate_https(&self.rootfs.sha256sums_url)?;
        validate_required_sha256(&self.rootfs.sha256sums_sha256, "Ubuntu SHA256SUMS")?;
        validate_https(&self.rootfs.signature_url)?;
        validate_signature(&self.rootfs.signature)?;
        if self.rootfs.source_sha256 != UBUNTU_BASE_SHA256
            || self.rootfs.source_url
                != "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.5-base-amd64.tar.gz"
            || self.rootfs.sha256sums_url
                != "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/SHA256SUMS"
            || self.rootfs.signature_url
                != "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/SHA256SUMS.gpg"
            || self.rootfs.signature.signer_primary_fingerprint != CANONICAL_FINGERPRINT
        {
            return Err(ManifestError::Invalid(
                "Ubuntu base digest or Canonical signer differs from the candidate lock".to_owned(),
            ));
        }
        validate_required_sha256(
            &self.rootfs.signature.signature_sha256,
            "Canonical signature",
        )?;
        if self.rootfs.signature.keyring_path != self.builder_apt.initial_keyring_path
            || self.rootfs.signature.keyring_sha256 != self.builder_apt.initial_keyring_sha256
            || self.rootfs.signature.verifier != self.builder_apt.initial_gpgv_path
            || self.rootfs.signature.verifier_sha256 != self.builder_apt.initial_gpgv_sha256
        {
            return Err(ManifestError::Invalid(
                "Canonical signature did not use the pinned Ubuntu-child trust root".to_owned(),
            ));
        }
        validate_required_sha256(&self.rootfs.package_lock_sha256, "guest package lock")?;
        validate_relative(&self.rootfs.package_lock_path)?;
        if self.rootfs.package_lock_path != self.guest_apt.package_lock_path
            || self.rootfs.package_lock_sha256 != self.guest_apt.package_lock_sha256
        {
            return Err(ManifestError::Invalid(
                "rootfs package provenance differs from the guest apt lock".to_owned(),
            ));
        }
        validate_artifact(&self.rootfs.output)?;
        validate_ext4(
            &self.rootfs.filesystem,
            &derive_filesystem_uuid(&self.recipe.sha256)?,
            if is_tarball_candidate {
                "TSNOBLE2"
            } else {
                "TSNOBLE1"
            },
        )?;
        if self.rootfs.release.is_empty() {
            return Err(ManifestError::Invalid(
                "rootfs release is missing".to_owned(),
            ));
        }
        validate_apt_receipt(&self.guest_apt, "guest", "pinned-ubuntu-child")?;
        validate_apt_receipt(&self.builder_apt, "builder", "pinned-ubuntu-child")?;
        if self.builder_apt.initial_keyring_path != self.guest_apt.initial_keyring_path
            || self.builder_apt.initial_keyring_sha256 != self.guest_apt.initial_keyring_sha256
        {
            return Err(ManifestError::Invalid(
                "guest and builder apt receipts use different initial trust roots".to_owned(),
            ));
        }
        if self.builder_apt.initial_gpgv_path != self.guest_apt.initial_gpgv_path
            || self.builder_apt.initial_gpgv_sha256 != self.guest_apt.initial_gpgv_sha256
        {
            return Err(ManifestError::Invalid(
                "guest and builder apt receipts use different pinned initial gpgv".to_owned(),
            ));
        }
        validate_rust_cli_builder(&self.rust_cli_builder)?;
        validate_ubuntu_build_builder(&self.ubuntu_build_builder)?;
        if self.builder_apt.initial_gpgv_path != self.ubuntu_build_builder.initial_gpgv_path
            || self.builder_apt.initial_gpgv_sha256 != self.ubuntu_build_builder.initial_gpgv_sha256
            || self.builder_apt.initial_keyring_path
                != self.ubuntu_build_builder.initial_keyring_path
            || self.builder_apt.initial_keyring_sha256
                != self.ubuntu_build_builder.initial_keyring_sha256
        {
            return Err(ManifestError::Invalid(
                "apt receipts do not use the pinned Ubuntu-child trust root".to_owned(),
            ));
        }
        validate_host_podman(&self.host_podman)?;
        if self.builder_apt.package_lock_path != self.ubuntu_build_builder.package_lock_path
            || self.builder_apt.package_lock_sha256 != self.ubuntu_build_builder.package_lock_sha256
            || self.kernel.builder_toolchain_lock_sha256
                != self.ubuntu_build_builder.package_lock_sha256
        {
            return Err(ManifestError::Invalid(
                "Ubuntu builder lock provenance differs from the builder apt receipt".to_owned(),
            ));
        }
        validate_oci_digest(&self.oci_smoke.image_digest)?;
        if self.oci_smoke.image_reference.is_empty() {
            return Err(ManifestError::Invalid(
                "offline OCI image reference is missing".to_owned(),
            ));
        }
        validate_required_sha256(&self.oci_smoke.archive_sha256, "offline OCI archive")?;
        if self.oci_smoke.platform != "linux/amd64" || self.commands.is_empty() {
            return Err(ManifestError::Invalid(
                "offline OCI image or external command receipts are missing".to_owned(),
            ));
        }
        if self.oci_smoke.image_digest != BUSYBOX_DIGEST {
            return Err(ManifestError::Invalid(
                "offline OCI digest differs from the candidate smoke image".to_owned(),
            ));
        }
        if self.oci_smoke.image_reference != "docker.io/library/busybox" {
            return Err(ManifestError::Invalid(
                "offline OCI image reference differs from the candidate lock".to_owned(),
            ));
        }
        for command in &self.commands {
            if command.program.is_empty() || command.status_code != 0 {
                return Err(ManifestError::Invalid(
                    "external command did not complete successfully".to_owned(),
                ));
            }
            validate_required_sha256(&command.program_sha256, "external tool")?;
            validate_relative(&command.stdout_path)?;
            validate_relative(&command.stderr_path)?;
            validate_required_sha256(&command.stdout_sha256, "stdout log")?;
            validate_required_sha256(&command.stderr_sha256, "stderr log")?;
        }
        if self.limitations.is_empty()
            && !matches!(
                self.reproducibility_status,
                ReproducibilityStatus::IdenticalTwoBuilds
            )
        {
            return Err(ManifestError::Invalid(
                "single-build and nondeterministic outputs must list their limitations".to_owned(),
            ));
        }
        match (self.reproducibility_status, &self.reproducibility) {
            (ReproducibilityStatus::SingleBuildUnqualified, None) => {}
            (ReproducibilityStatus::IdenticalTwoBuilds, Some(receipt)) => {
                validate_reproducibility_receipt(receipt)?;
                if receipt.first_kernel_sha256 != receipt.second_kernel_sha256
                    || receipt.first_rootfs_sha256 != receipt.second_rootfs_sha256
                    || !receipt.differences.is_empty()
                    || self.kernel.output.sha256 != receipt.second_kernel_sha256
                    || self.rootfs.output.sha256 != receipt.second_rootfs_sha256
                {
                    return Err(ManifestError::Invalid(
                        "identical-build status does not match the recorded outputs".to_owned(),
                    ));
                }
            }
            (ReproducibilityStatus::ControlledNondeterminism, Some(receipt)) => {
                validate_reproducibility_receipt(receipt)?;
                if receipt.differences.is_empty()
                    || (receipt.first_kernel_sha256 == receipt.second_kernel_sha256
                        && receipt.first_rootfs_sha256 == receipt.second_rootfs_sha256)
                {
                    return Err(ManifestError::Invalid(
                        "nondeterminism status lacks differing output hashes and metadata"
                            .to_owned(),
                    ));
                }
            }
            _ => {
                return Err(ManifestError::Invalid(
                    "reproducibility status and comparison receipt disagree".to_owned(),
                ));
            }
        }
        Ok(())
    }
}

/// Resolve one manifest-relative regular output file without following
/// symlinks or accepting absolute paths or traversal components.
///
/// # Errors
///
/// Returns an error if the requested relative path is unsafe or traverses a
/// symbolic link.
pub fn resolve_output_path(manifest_path: &Path, relative: &str) -> Result<PathBuf, ManifestError> {
    validate_relative(relative)?;
    let manifest_parent = manifest_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .ok_or_else(|| {
            ManifestError::Invalid("manifest path has no parent directory".to_owned())
        })?;
    let parent = fs::canonicalize(manifest_parent)?;
    let mut output = parent.clone();
    for component in Path::new(relative).components() {
        let Component::Normal(part) = component else {
            return Err(ManifestError::Invalid(
                "output path contains a non-normal component".to_owned(),
            ));
        };
        output.push(part);
        match fs::symlink_metadata(&output) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(ManifestError::Invalid(format!(
                    "output path contains a symlink: {}",
                    output.display()
                )));
            }
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                return Err(ManifestError::Io(error));
            }
            Err(error) => return Err(ManifestError::Io(error)),
        }
    }
    if !output.starts_with(&parent) {
        return Err(ManifestError::Invalid(
            "output path escaped the manifest directory".to_owned(),
        ));
    }
    Ok(output)
}

fn validate_ext4(
    filesystem: &Ext4Receipt,
    expected_uuid: &str,
    expected_label: &str,
) -> Result<(), ManifestError> {
    if filesystem.size_bytes != 16 * 1024 * 1024 * 1024
        || filesystem.block_size != 4096
        || filesystem.inode_size != 256
        || filesystem.reserved_percent != 0
        || filesystem.label != expected_label
        || filesystem.uuid != expected_uuid
        || filesystem.hash_seed != filesystem.uuid
        || filesystem.e2fsck_exit_code != 0
        || !filesystem.e2fsck_clean
    {
        return Err(ManifestError::Invalid(
            "ext4 settings or clean filesystem check differ from the locked recipe".to_owned(),
        ));
    }
    validate_required_sha256(
        &filesystem.root_tree_inventory_sha256,
        "normalized root-tree inventory",
    )?;
    validate_relative(&filesystem.root_tree_inventory_path)
}

fn validate_kernel_receipt(
    kernel: &KernelReceipt,
    is_tarball_candidate: bool,
) -> Result<(), ManifestError> {
    validate_https(&kernel.source_url)?;
    validate_https(&kernel.signing_key_url)?;
    validate_required_sha256(&kernel.signing_key_sha256, "kernel signing key")?;
    validate_fingerprint(&kernel.signing_fingerprint)?;
    validate_https(&kernel.config_url)?;
    validate_required_sha256(&kernel.config_sha256, "Firecracker kernel config seed")?;
    validate_https(&kernel.kernel_policy_url)?;
    validate_required_sha256(&kernel.kernel_policy_sha256, "Firecracker kernel policy")?;
    validate_required_sha256(&kernel.custom_config_sha256, "resolved kernel config")?;
    validate_relative(&kernel.resolved_config_path)?;
    validate_required_sha256(&kernel.builder_toolchain_lock_sha256, "kernel builder lock")?;
    validate_artifact(&kernel.output)?;
    if kernel.release.is_empty() || kernel.source_date_epoch == 0 {
        return Err(ManifestError::Invalid(
            "kernel release or source epoch is missing".to_owned(),
        ));
    }
    validate_source_artifact_identity(&kernel.source_archive_uncompressed)?;

    if is_tarball_candidate {
        validate_tarball_kernel_receipt(kernel)?;
    } else {
        validate_git_kernel_receipt(kernel)?;
    }
    Ok(())
}

fn validate_tarball_kernel_receipt(kernel: &KernelReceipt) -> Result<(), ManifestError> {
    let compressed = kernel.source_archive_compressed.as_ref().ok_or_else(|| {
        ManifestError::Invalid("v2 kernel receipt omits the compressed source tar".to_owned())
    })?;
    let signature = kernel.source_signature.as_ref().ok_or_else(|| {
        ManifestError::Invalid("v2 kernel receipt omits detached tar-signature evidence".to_owned())
    })?;
    validate_source_artifact_identity(compressed)?;
    validate_source_artifact_identity(&signature.artifact)?;
    validate_signature(&signature.verification)?;
    if kernel.source_kind != "kernel.org-signed-tarball"
        || kernel.source_revision != "6.18.53"
        || kernel.source_url != "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.18.53.tar.xz"
        || kernel.signing_key_url
            != "https://kernel.org/.well-known/openpgpkey/hu/e3n9xnm94c5apezqnj1pmrfuaoyfm8cf?l=gregkh"
        || kernel.signing_key_sha256
            != "61b758f0d4a386fc83565b92cfd6c05879573df0e96d44b71a5a3e19e1a8581a"
        || kernel.signing_fingerprint != "647F28654894E3BD457199BE38DBBDC86092693E"
        || !kernel.annotated_tag.is_empty()
        || !kernel.tag_object.is_empty()
        || kernel.tag_signature.is_some()
        || compressed.source_url != kernel.source_url
        || compressed.sha256 != "4d6fba95c2244b08a7b4144a4d38b9be4fb31abb5e7682ae40bb5cb11374cfe0"
        || signature.artifact.source_url
            != "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.18.53.tar.sign"
        || signature.artifact.sha256
            != "7e897372a7e0b0400176ea4065a33f4417119398bdaf9baa48a2e1030812ee6d"
        || signature.verification.signature_sha256 != signature.artifact.sha256
        || signature.verification.signer_primary_fingerprint != kernel.signing_fingerprint
        || signature.verification.keyring_sha256 != kernel.signing_key_sha256
        || kernel.firecracker_version != "1.17.0"
        || kernel.firecracker_commit != "95f868c8e345b1cc8faccd1a3c910b4989dc3f58"
        || kernel.config_url
            != "https://raw.githubusercontent.com/firecracker-microvm/firecracker/95f868c8e345b1cc8faccd1a3c910b4989dc3f58/resources/guest_configs/microvm-kernel-ci-x86_64-6.18.config"
        || kernel.config_sha256
            != "ba22401a0c7292a4c024ebcd10a562d4a1f1bfd2faed671406d3b159c0cf5215"
        || kernel.kernel_policy_url
            != "https://raw.githubusercontent.com/firecracker-microvm/firecracker/95f868c8e345b1cc8faccd1a3c910b4989dc3f58/docs/kernel-policy.md"
        || kernel.kernel_policy_sha256
            != "cdc062c6cfe5766acbddaff3f200dfe0a50b6edd4c75b0786f573bb2a2f1d68e"
        || kernel.custom_config_diff_sha256.len() != 64
        || kernel.resolved_config_diff_path.is_none()
        || kernel.source_archive_uncompressed.source_url != kernel.source_url
    {
        return Err(ManifestError::Invalid(
            "kernel.org tarball, Greg KH signature, Firecracker policy or upstream config receipt differs from v2 candidate pins".to_owned(),
        ));
    }
    validate_required_sha256(
        &kernel.custom_config_diff_sha256,
        "reviewed upstream kernel config diff",
    )?;
    validate_relative(
        kernel
            .resolved_config_diff_path
            .as_deref()
            .unwrap_or_default(),
    )
}

fn validate_git_kernel_receipt(kernel: &KernelReceipt) -> Result<(), ManifestError> {
    validate_git_revision(&kernel.source_revision)?;
    let tag_signature = kernel.tag_signature.as_ref().ok_or_else(|| {
        ManifestError::Invalid(
            "kernel tag signature must be verified before a manifest can be emitted".to_owned(),
        )
    })?;
    validate_signature(tag_signature)?;
    if kernel.source_kind != "signed-git-tag"
        || kernel.source_revision != KERNEL_COMMIT
        || kernel.source_url != "https://github.com/amazonlinux/linux.git"
        || kernel.annotated_tag != "microvm-kernel-6.1.155-13.282.amzn2023"
        || kernel.tag_object != "4b0d5e9b869e49681addca8cf22cc83a340c44ff"
        || kernel.source_archive_compressed.is_some()
        || kernel.source_signature.is_some()
        || kernel.firecracker_version != "1.15.1"
        || kernel.firecracker_commit != FIRECRACKER_COMMIT
        || kernel.config_url
            != "https://raw.githubusercontent.com/firecracker-microvm/firecracker/v1.15.1/resources/guest_configs/microvm-kernel-ci-x86_64-6.1.config"
        || kernel.config_sha256
            != "391d8d498f89e56b391be5ea4a7501ebc44e6d094331ecaabdefed1da6b28a96"
        || kernel.kernel_policy_url
            != "https://raw.githubusercontent.com/firecracker-microvm/firecracker/v1.15.1/docs/kernel-policy.md"
        || kernel.kernel_policy_sha256
            != "f6d05719c129d8370b581ddc838de2bd113d1f35127f4da4236853ca9aa6f2fb"
        || !kernel.custom_config_diff_sha256.is_empty()
        || kernel.resolved_config_diff_path.is_some()
        || kernel.custom_config_sha256 != kernel.config_sha256
    {
        return Err(ManifestError::Invalid(
            "kernel or Firecracker source differs from the historical v1 candidate lock".to_owned(),
        ));
    }
    Ok(())
}

fn validate_firecracker_release(release: &FirecrackerReleaseReceipt) -> Result<(), ManifestError> {
    validate_source_artifact_identity(&release.archive)?;
    validate_source_artifact_identity(&release.executable)?;
    crate::input::validate_download_transport(&release.download)
        .map_err(|error| ManifestError::Invalid(error.to_string()))?;
    if release.archive.source_url != FIRECRACKER_RELEASE_URL
        || release.archive.size_bytes != FIRECRACKER_RELEASE_ARCHIVE_SIZE
        || release.archive.sha256 != FIRECRACKER_RELEASE_ARCHIVE_SHA256
        || release.archive.path != release.download.cache_path
        || release.download.id != "firecracker-release-archive"
        || release.download.source_url != release.archive.source_url
        || release.download.size_bytes != release.archive.size_bytes
        || release.download.sha256 != release.archive.sha256
        || release.executable_member != FIRECRACKER_RELEASE_MEMBER
        || release.executable.source_url != FIRECRACKER_RELEASE_URL
        || release.executable.path != "firecracker-release-executable"
        || release.executable.size_bytes != FIRECRACKER_RELEASE_EXECUTABLE_SIZE
        || release.executable.sha256 != FIRECRACKER_RELEASE_EXECUTABLE_SHA256
        || release.archive_metadata_source != "github-release-assets-api"
    {
        return Err(ManifestError::Invalid(
            "Firecracker release archive or executable differs from the observed v2 asset pin"
                .to_owned(),
        ));
    }
    Ok(())
}

fn validate_source_artifact_identity(
    artifact: &SourceArtifactReceipt,
) -> Result<(), ManifestError> {
    if artifact.source_url.starts_with("https://") {
        validate_https(&artifact.source_url)?;
    } else if artifact.source_url != "generated-from-verified-git-checkout" {
        return Err(ManifestError::Invalid(
            "kernel source artifact origin is not an accepted identity".to_owned(),
        ));
    }
    validate_relative(&artifact.path)?;
    validate_required_sha256(&artifact.sha256, "kernel source artifact")?;
    if artifact.size_bytes == 0 {
        return Err(ManifestError::Invalid(
            "kernel source artifact has zero length".to_owned(),
        ));
    }
    Ok(())
}

fn verify_source_artifact(
    manifest_path: &Path,
    artifact: &SourceArtifactReceipt,
) -> Result<(), ManifestError> {
    let path = resolve_output_path(manifest_path, &artifact.path)?;
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() != artifact.size_bytes
        || sha256_file(&path)? != artifact.sha256
    {
        return Err(ManifestError::Invalid(format!(
            "kernel source artifact changed: {}",
            artifact.path
        )));
    }
    Ok(())
}

fn derive_filesystem_uuid(recipe_sha256: &str) -> Result<String, ManifestError> {
    if !is_sha256(recipe_sha256) {
        return Err(ManifestError::Invalid(
            "recipe SHA-256 is invalid for filesystem UUID derivation".to_owned(),
        ));
    }
    let mut bytes = [0_u8; 16];
    for (index, byte) in bytes.iter_mut().enumerate() {
        let start = index * 2;
        *byte = u8::from_str_radix(&recipe_sha256[start..start + 2], 16).map_err(|_| {
            ManifestError::Invalid("recipe SHA-256 cannot derive filesystem UUID".to_owned())
        })?;
    }
    bytes[6] = (bytes[6] & 0x0f) | 0x80;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    Ok(format!(
        "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        bytes[0],
        bytes[1],
        bytes[2],
        bytes[3],
        bytes[4],
        bytes[5],
        bytes[6],
        bytes[7],
        bytes[8],
        bytes[9],
        bytes[10],
        bytes[11],
        bytes[12],
        bytes[13],
        bytes[14],
        bytes[15]
    ))
}

#[allow(clippy::too_many_lines)] // Index signatures, package rows and solver evidence are one receipt boundary.
fn validate_apt_receipt(
    receipt: &AptReceipt,
    expected_role: &str,
    expected_trust_source: &str,
) -> Result<(), ManifestError> {
    validate_https(&receipt.snapshot_url)?;
    validate_required_sha256(&receipt.apt_sha256, "apt executable")?;
    validate_required_sha256(&receipt.settings_sha256, "apt settings")?;
    validate_required_sha256(&receipt.selected_inventory_sha256, "APT selected inventory")?;
    validate_relative(&receipt.settings_path)?;
    validate_required_sha256(&receipt.keyring_sha256, "apt archive keyring")?;
    validate_absolute_trust_path(&receipt.initial_keyring_path)?;
    validate_required_sha256(&receipt.initial_keyring_sha256, "initial apt keyring")?;
    validate_required_sha256(&receipt.initial_gpgv_sha256, "initial gpgv")?;
    validate_required_sha256(&receipt.package_lock_sha256, "role-specific package lock")?;
    validate_relative(&receipt.package_lock_path)?;
    validate_fingerprint(&receipt.signer_primary_fingerprint)?;
    validate_required_sha256(&receipt.solver_stdout_sha256, "apt simulation stdout")?;
    validate_required_sha256(&receipt.solver_stderr_sha256, "apt simulation stderr")?;
    if receipt.role != expected_role
        || receipt.initial_keyring_source != expected_trust_source
        || receipt.initial_keyring_path != UBUNTU_ARCHIVE_KEYRING_PATH
        || receipt.initial_gpgv_path != "/usr/bin/gpgv"
        || receipt.snapshot_url != "https://snapshot.ubuntu.com/ubuntu/20260910T164400Z/"
        || receipt.signer_primary_fingerprint != APT_FINGERPRINT
        || receipt.snapshot_timestamp != "20260910T164400Z"
        || receipt.architecture != "amd64"
        || receipt.suites != ["noble", "noble-updates", "noble-security"]
        || receipt.components != ["main", "universe"]
        || receipt.apt_version != "2.8.3"
        || receipt.allow_unauthenticated
        || receipt.install_recommends
        || receipt.install_suggests
        || !receipt.check_valid_until
        || receipt.solver_exit_status != 0
        || receipt
            .solver_command_argv
            .first()
            .is_none_or(|program| !program.ends_with("apt-get"))
        || !receipt
            .solver_command_argv
            .iter()
            .any(|argument| argument == "-s" || argument == "--simulate")
        || receipt.indexes.len() != 6
        || receipt.packages.is_empty()
    {
        return Err(ManifestError::Invalid(format!(
            "{expected_role} apt receipt lacks the fixed snapshot, signed closure or apt simulation proof"
        )));
    }
    let mut indexes = receipt.indexes.iter().collect::<Vec<_>>();
    indexes.sort_by(|left, right| {
        (&left.suite, &left.component, &left.architecture).cmp(&(
            &right.suite,
            &right.component,
            &right.architecture,
        ))
    });
    if indexes.windows(2).any(|window| {
        (
            &window[0].suite,
            &window[0].component,
            &window[0].architecture,
        ) == (
            &window[1].suite,
            &window[1].component,
            &window[1].architecture,
        )
    }) {
        return Err(ManifestError::Invalid(
            "duplicate signed apt package index".to_owned(),
        ));
    }
    for index in &receipt.indexes {
        if !["noble", "noble-updates", "noble-security"].contains(&index.suite.as_str())
            || !["main", "universe"].contains(&index.component.as_str())
            || index.architecture != "amd64"
        {
            return Err(ManifestError::Invalid(
                "apt index is outside the locked suites, components or architecture".to_owned(),
            ));
        }
        validate_required_sha256(&index.inrelease_sha256, "apt InRelease")?;
        validate_https(&index.inrelease_url)?;
        validate_https(&index.packages_index_url)?;
        validate_required_sha256(&index.packages_index_sha256, "apt Packages index")?;
        validate_signature(&index.signature)?;
        if !index.gpgv_verified
            || index.trusted_key_source != receipt.initial_keyring_source
            || index.trusted_key_sha256 != receipt.initial_keyring_sha256
            || index.signature.signer_primary_fingerprint != APT_FINGERPRINT
            || index.signature.keyring_path != receipt.initial_keyring_path
            || index.signature.keyring_sha256 != receipt.initial_keyring_sha256
            || index.signature.verifier != receipt.initial_gpgv_path
            || index.signature.verifier_sha256 != receipt.initial_gpgv_sha256
            || index.signature.signature_sha256 != index.inrelease_sha256
        {
            return Err(ManifestError::Invalid(
                "apt InRelease signature receipt differs from its initial trust root".to_owned(),
            ));
        }
        let expected_inrelease = candidate_inrelease_sha256(&index.suite);
        let expected_packages =
            candidate_packages_sha256(&index.suite, &index.component, &index.architecture);
        let expected_base = format!(
            "https://snapshot.ubuntu.com/ubuntu/20260910T164400Z/dists/{}/",
            index.suite
        );
        let expected_packages_path = format!(
            "{}{}/binary-{}/Packages.xz",
            expected_base, index.component, index.architecture
        );
        if index.inrelease_url != format!("{expected_base}InRelease")
            || index.packages_index_url != expected_packages_path
            || expected_inrelease != Some(index.inrelease_sha256.as_str())
            || expected_packages != Some(index.packages_index_sha256.as_str())
        {
            return Err(ManifestError::Invalid(
                "apt index source or digest differs from the candidate snapshot identity"
                    .to_owned(),
            ));
        }
    }
    let mut packages = receipt.packages.iter().collect::<Vec<_>>();
    packages.sort_by(|left, right| {
        (&left.name, &left.architecture).cmp(&(&right.name, &right.architecture))
    });
    if packages.windows(2).any(|window| {
        (&window[0].name, &window[0].architecture) == (&window[1].name, &window[1].architecture)
    }) {
        return Err(ManifestError::Invalid(
            "duplicate installed package identity".to_owned(),
        ));
    }
    for package in &receipt.packages {
        if package.name.is_empty()
            || package.version.is_empty()
            || !["amd64", "all"].contains(&package.architecture.as_str())
            || !["main", "universe"].contains(&package.component.as_str())
            || !["noble", "noble-updates", "noble-security"].contains(&package.suite.as_str())
            || package.size_bytes == 0
        {
            return Err(ManifestError::Invalid(
                "installed package identity is incomplete".to_owned(),
            ));
        }
        validate_https(&package.source_url)?;
        validate_required_sha256(&package.deb_sha256, "package .deb")?;
    }
    Ok(())
}

fn validate_rust_cli_builder(builder: &RustCliBuilderReceipt) -> Result<(), ManifestError> {
    if builder.base_image != "docker.io/library/rust:1.98.1-bookworm"
        || builder.base_oci_digest != RUST_BUILDER_DIGEST
        || builder.base_index_oci_digest != RUST_BUILDER_INDEX_DIGEST
        || builder.rust_toolchain_channel != "1.98.1"
        || builder.target != "x86_64-unknown-linux-musl"
        || builder.host_abi != "x86_64-linux-musl-static"
        || builder.rust_version.is_empty()
        || builder.cargo_version.is_empty()
    {
        return Err(ManifestError::Invalid(
            "Rust CLI builder identity or host ABI is invalid".to_owned(),
        ));
    }
    validate_oci_digest(&builder.base_oci_digest)?;
    validate_oci_digest(&builder.base_index_oci_digest)?;
    validate_required_sha256(&builder.source_tree_sha256, "Rust CLI source tree")?;
    validate_required_sha256(&builder.cargo_lock_sha256, "Rust CLI Cargo.lock")?;
    validate_required_sha256(&builder.rust_toolchain_sha256, "Rust CLI toolchain")?;
    validate_required_sha256(&builder.executable_sha256, "Rust CLI executable")?;
    validate_relative(&builder.cargo_lock_path)?;
    validate_relative(&builder.rust_toolchain_path)?;
    validate_relative(&builder.executable_path)
}

fn validate_ubuntu_build_builder(builder: &UbuntuBuildBuilderReceipt) -> Result<(), ManifestError> {
    if builder.base_image != "docker.io/library/ubuntu:24.04"
        || builder.base_oci_digest != UBUNTU_BUILDER_DIGEST
        || builder.base_index_oci_digest != UBUNTU_BUILDER_INDEX_DIGEST
        || builder
            .build_argv
            .first()
            .is_none_or(|program| !program.ends_with("podman"))
        || !builder
            .build_argv
            .iter()
            .any(|argument| argument == "build")
        || !builder
            .build_argv
            .iter()
            .any(|argument| argument == "--network=none")
    {
        return Err(ManifestError::Invalid(
            "Ubuntu build builder identity or offline build argv is invalid".to_owned(),
        ));
    }
    validate_oci_digest(&builder.base_oci_digest)?;
    validate_oci_digest(&builder.base_index_oci_digest)?;
    validate_oci_digest(&builder.derived_oci_digest)?;
    if builder.initial_gpgv_path != "/usr/bin/gpgv" {
        return Err(ManifestError::Invalid(
            "Ubuntu builder initial gpgv path differs from the pinned base child".to_owned(),
        ));
    }
    validate_required_sha256(&builder.initial_gpgv_sha256, "Ubuntu-child initial gpgv")?;
    validate_absolute_trust_path(&builder.initial_keyring_path)?;
    if builder.initial_keyring_path != UBUNTU_ARCHIVE_KEYRING_PATH {
        return Err(ManifestError::Invalid(
            "Ubuntu builder initial keyring path differs from the pinned base child".to_owned(),
        ));
    }
    validate_required_sha256(
        &builder.initial_keyring_sha256,
        "Ubuntu-child initial archive keyring",
    )?;
    validate_required_sha256(&builder.package_lock_sha256, "Ubuntu builder package lock")?;
    validate_relative(&builder.package_lock_path)?;
    validate_builder_tool_receipts(&builder.tools)
}

fn validate_builder_tool_receipts(tools: &[UbuntuToolReceipt]) -> Result<(), ManifestError> {
    const REQUIRED: [&str; 12] = [
        "gpgv",
        "apt-get",
        "apt-cache",
        "dpkg",
        "dpkg-deb",
        "gcc",
        "make",
        "mke2fs",
        "e2fsck",
        "xz",
        "git",
        "tar",
    ];
    if tools.len() != REQUIRED.len() {
        return Err(ManifestError::Invalid(
            "Ubuntu builder receipt must include all required executable pins".to_owned(),
        ));
    }
    let mut names = tools
        .iter()
        .map(|tool| tool.name.as_str())
        .collect::<Vec<_>>();
    names.sort_unstable();
    if names.windows(2).any(|pair| pair[0] == pair[1])
        || REQUIRED.iter().any(|required| !names.contains(required))
    {
        return Err(ManifestError::Invalid(
            "Ubuntu builder tool inventory has missing or duplicate entries".to_owned(),
        ));
    }
    for tool in tools {
        if tool.version.is_empty() || !Path::new(&tool.path).is_absolute() {
            return Err(ManifestError::Invalid(format!(
                "Ubuntu builder tool {} lacks its version or absolute path",
                tool.name
            )));
        }
        validate_required_sha256(&tool.sha256, "Ubuntu builder executable")?;
    }
    Ok(())
}

fn validate_host_podman(host: &HostPodmanReceipt) -> Result<(), ManifestError> {
    if host.path != "/nix/store/mi7484cckkxv8hgiawq1cg89sxknc3bh-podman-5.8.6/bin/podman"
        || host.version != "5.8.6"
        || host.sha256 != HOST_PODMAN_SHA256
        || !host.rootless
        || host.uid != 1000
        || host.gid != 1000
        || host.resource_limits.cpu_count != 4
        || host.resource_limits.memory_mib != 8192
        || host.resource_limits.pids_limit != 2048
        || host.resource_limits.workspace_limit_bytes != 68_719_476_736
    {
        return Err(ManifestError::Invalid(
            "host Podman identity or rootless limits differ from the recipe".to_owned(),
        ));
    }
    validate_required_sha256(&host.sha256, "host Podman executable")
}

fn candidate_inrelease_sha256(suite: &str) -> Option<&'static str> {
    match suite {
        "noble" => Some("cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0"),
        "noble-updates" => Some("9f03de549c67af423447b5204dcd46298d23f69fe22bf30c76e9ee01548d0246"),
        "noble-security" => {
            Some("7e344732ccaf448e1d98e70ea4d76c8d0b4d2d6e563888483c07b69690b6c165")
        }
        _ => None,
    }
}

fn candidate_packages_sha256(
    suite: &str,
    component: &str,
    architecture: &str,
) -> Option<&'static str> {
    match (suite, component, architecture) {
        ("noble", "main", "amd64") => {
            Some("2a6a199e1031a5c279cb346646d594993f35b1c03dd4a82aaa0323980dd92451")
        }
        ("noble", "universe", "amd64") => {
            Some("ba9057fa1b91438cc8a1d26808d00c85389fe101d0c1496254df97236405599a")
        }
        ("noble-updates", "main", "amd64") => {
            Some("fda21cfbb147b8445d3e37e60745ab8202d40e0f4fd7010d029c57fe1458708e")
        }
        ("noble-updates", "universe", "amd64") => {
            Some("cf06d0b20daa7eea4d4a4f4abc0fd8b29aed8389367681c6f6653c5068155265")
        }
        ("noble-security", "main", "amd64") => {
            Some("b885088b1c8da2fec85d189005b63a9e642eb893a77069ef28f81d72a3d7a26e")
        }
        ("noble-security", "universe", "amd64") => {
            Some("823c10e7329f3f43b3c50db9de0d7ae8333765b4e18f4707c3357e8467d2d8f9")
        }
        _ => None,
    }
}

fn validate_reproducibility_receipt(receipt: &ReproducibilityReceipt) -> Result<(), ManifestError> {
    if receipt.first_run_id.is_empty()
        || receipt.second_run_id.is_empty()
        || receipt.first_run_id == receipt.second_run_id
    {
        return Err(ManifestError::Invalid(
            "repeat-build receipt must identify two distinct runs".to_owned(),
        ));
    }
    for digest in [
        &receipt.first_kernel_sha256,
        &receipt.second_kernel_sha256,
        &receipt.first_rootfs_sha256,
        &receipt.second_rootfs_sha256,
    ] {
        validate_required_sha256(digest, "repeat-build artifact")?;
    }
    for difference in &receipt.differences {
        if difference.field.is_empty()
            || difference.first_value == difference.second_value
            || difference.impact.is_empty()
        {
            return Err(ManifestError::Invalid(
                "reproducibility differences must identify values and impact".to_owned(),
            ));
        }
    }
    Ok(())
}

fn verify_file_receipt(
    manifest_path: &Path,
    relative: &str,
    expected_sha256: &str,
) -> Result<(), ManifestError> {
    let path = resolve_output_path(manifest_path, relative)?;
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(ManifestError::Invalid(format!(
            "receipt artifact is not a regular file: {}",
            path.display()
        )));
    }
    if sha256_file(&path)? != expected_sha256 {
        return Err(ManifestError::Invalid(format!(
            "receipt artifact SHA-256 mismatch for {relative}"
        )));
    }
    Ok(())
}

fn verify_inventory_package_state(
    manifest_path: &Path,
    manifest: &ImageManifest,
) -> Result<(), ManifestError> {
    let inventory = read_root_tree_inventory(manifest_path, manifest)?;
    validate_root_tree_inventory(&inventory)?;
    let installed_inventory = installed_package_inventory(&inventory)?;
    verify_inventory_package_lock(manifest_path, manifest, &inventory, &installed_inventory)
}

fn read_root_tree_inventory(
    manifest_path: &Path,
    manifest: &ImageManifest,
) -> Result<RootTreeInventoryFile, ManifestError> {
    let relative = &manifest.rootfs.filesystem.root_tree_inventory_path;
    let path = resolve_output_path(manifest_path, relative)?;
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > 256 * 1024 * 1024
    {
        return Err(ManifestError::Invalid(
            "root-tree inventory must be a bounded regular file".to_owned(),
        ));
    }
    serde_json::from_slice(&fs::read(path)?)
        .map_err(|error| ManifestError::Invalid(format!("invalid root-tree inventory: {error}")))
}

fn validate_root_tree_inventory(inventory: &RootTreeInventoryFile) -> Result<(), ManifestError> {
    if inventory.schema_version != 1
        || inventory
            .entries
            .windows(2)
            .any(|pair| pair[0].path >= pair[1].path)
        || inventory.installed_packages.windows(2).any(|pair| {
            (&pair[0].name, &pair[0].architecture) >= (&pair[1].name, &pair[1].architecture)
        })
    {
        return Err(ManifestError::Invalid(
            "root-tree inventory schema or ordering is invalid".to_owned(),
        ));
    }
    for entry in &inventory.entries {
        validate_relative(&entry.path)?;
        let is_file = entry.kind == "file";
        let is_symlink = entry.kind == "symlink";
        if !matches!(entry.kind.as_str(), "directory" | "file" | "symlink")
            || is_file != entry.sha256.is_some()
            || is_symlink != entry.symlink_target.is_some()
            || (entry.hardlink_group.is_some() && !is_file)
            || (entry.mode & !0o177_777) != 0
        {
            return Err(ManifestError::Invalid(
                "root-tree inventory entry metadata is inconsistent".to_owned(),
            ));
        }
        if let Some(hash) = &entry.sha256 {
            validate_required_sha256(hash, "root-tree file")?;
        }
        if let Some(target) = &entry.symlink_target {
            if target.is_empty() || target.contains('\0') {
                return Err(ManifestError::Invalid(
                    "root-tree symlink target is invalid".to_owned(),
                ));
            }
        }
        let _ = (entry.uid, entry.gid, entry.size_bytes);
    }
    Ok(())
}

fn installed_package_inventory(
    inventory: &RootTreeInventoryFile,
) -> Result<BTreeMap<(String, String), String>, ManifestError> {
    let mut installed_inventory = BTreeMap::new();
    for package in &inventory.installed_packages {
        if package.name.is_empty()
            || package.version.is_empty()
            || !["amd64", "all"].contains(&package.architecture.as_str())
        {
            return Err(ManifestError::Invalid(
                "root-tree inventory package state is incomplete".to_owned(),
            ));
        }
        if installed_inventory
            .insert(
                (package.name.clone(), package.architecture.clone()),
                package.version.clone(),
            )
            .is_some()
        {
            return Err(ManifestError::Invalid(
                "root-tree inventory repeats an installed package identity".to_owned(),
            ));
        }
    }
    Ok(installed_inventory)
}

fn verify_inventory_package_lock(
    manifest_path: &Path,
    manifest: &ImageManifest,
    inventory: &RootTreeInventoryFile,
    installed_inventory: &BTreeMap<(String, String), String>,
) -> Result<(), ManifestError> {
    if !canonical_inventory_sha256(installed_inventory)
        .eq_ignore_ascii_case(&manifest.guest_apt.selected_inventory_sha256)
    {
        return Err(ManifestError::Invalid(
            "root-tree inventory differs from the complete guest APT solver selection".to_owned(),
        ));
    }
    let lock_path = resolve_output_path(manifest_path, &manifest.guest_apt.package_lock_path)?;
    let package_lock: CandidatePackageLock = serde_json::from_slice(&fs::read(lock_path)?)
        .map_err(|error| ManifestError::Invalid(format!("invalid guest package lock: {error}")))?;
    package_lock
        .validate()
        .map_err(|error| ManifestError::Invalid(error.to_string()))?;
    let lock_selected_inventory = package_lock
        .apt_solver_receipt
        .as_ref()
        .ok_or_else(|| {
            ManifestError::Invalid("guest package lock omits its APT solver receipt".to_owned())
        })?
        .selected_inventory_sha256
        .as_str();
    if !lock_selected_inventory.eq_ignore_ascii_case(&manifest.guest_apt.selected_inventory_sha256)
    {
        return Err(ManifestError::Invalid(
            "guest APT receipt differs from its package-lock solver inventory".to_owned(),
        ));
    }
    for package in &manifest.guest_apt.packages {
        if !inventory.installed_packages.iter().any(|installed| {
            installed.name == package.name
                && installed.version == package.version
                && installed.architecture == package.architecture
        }) {
            return Err(ManifestError::Invalid(format!(
                "root-tree inventory omits locked guest package {}={}",
                package.name, package.version
            )));
        }
    }
    Ok(())
}

fn validate_artifact(output: &ArtifactOutput) -> Result<(), ManifestError> {
    validate_relative(&output.path)?;
    if output.size_bytes == 0 {
        return Err(ManifestError::Invalid(
            "output artifact size must be nonzero".to_owned(),
        ));
    }
    validate_required_sha256(&output.sha256, "output artifact")
}

fn validate_signature(signature: &SignatureReceipt) -> Result<(), ManifestError> {
    validate_required_sha256(&signature.signature_sha256, "signature")?;
    validate_required_sha256(&signature.keyring_sha256, "signature keyring")?;
    validate_fingerprint(&signature.signer_primary_fingerprint)?;
    validate_required_sha256(&signature.verifier_sha256, "signature verifier")?;
    if signature.keyring_path.is_empty()
        || signature.keyring_path.contains('\0')
        || signature.verifier.is_empty()
        || signature.status != "validsig"
    {
        return Err(ManifestError::Invalid(
            "signature verification receipt is not successful".to_owned(),
        ));
    }
    Ok(())
}

fn validate_absolute_trust_path(value: &str) -> Result<(), ManifestError> {
    let path = Path::new(value);
    if !path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::RootDir | Component::Normal(_)))
    {
        return Err(ManifestError::Invalid(
            "trust-root paths must be absolute and traversal-free".to_owned(),
        ));
    }
    Ok(())
}

fn validate_relative(value: &str) -> Result<(), ManifestError> {
    let path = Path::new(value);
    if value.is_empty()
        || path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(ManifestError::Invalid(
            "manifest paths must be nonempty, relative and traversal-free".to_owned(),
        ));
    }
    Ok(())
}

fn validate_https(value: &str) -> Result<(), ManifestError> {
    let url = reqwest::Url::parse(value)
        .map_err(|_| ManifestError::Invalid("provenance URL is malformed".to_owned()))?;
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err(ManifestError::Invalid(
            "source provenance URL must be HTTPS and contain no credentials".to_owned(),
        ));
    }
    Ok(())
}

fn validate_git_revision(value: &str) -> Result<(), ManifestError> {
    if value.len() != 40 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(ManifestError::Invalid(
            "source revision must be a full 40-digit git object id".to_owned(),
        ));
    }
    Ok(())
}

fn validate_fingerprint(value: &str) -> Result<(), ManifestError> {
    if ![40, 64].contains(&value.len()) || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(ManifestError::Invalid(
            "signer fingerprint must be a full OpenPGP fingerprint".to_owned(),
        ));
    }
    Ok(())
}

fn validate_oci_digest(value: &str) -> Result<(), ManifestError> {
    let Some(digest) = value.strip_prefix("sha256:") else {
        return Err(ManifestError::Invalid(
            "OCI identity must be a SHA-256 digest".to_owned(),
        ));
    };
    validate_required_sha256(digest, "OCI image")
}

fn validate_required_sha256(value: &str, what: &str) -> Result<(), ManifestError> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(ManifestError::Invalid(format!(
            "{what} SHA-256 must contain 64 hexadecimal digits"
        )));
    }
    Ok(())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_file(path: &Path) -> Result<String, ManifestError> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024].into_boxed_slice();
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    let mut text = String::with_capacity(64);
    for byte in digest.finalize() {
        use std::fmt::Write as _;
        write!(&mut text, "{byte:02x}").expect("writing into String cannot fail");
    }
    Ok(text)
}

#[cfg(test)]
mod tests {
    use super::{ManifestError, resolve_output_path};
    use std::path::Path;

    #[test]
    fn manifest_output_paths_reject_traversal_and_absolute_paths() {
        let manifest = Path::new("/tmp/image-fixture/image-manifest.json");
        assert!(matches!(
            resolve_output_path(manifest, "../outside/vmlinux"),
            Err(ManifestError::Invalid(_))
        ));
        assert!(matches!(
            resolve_output_path(manifest, "/tmp/outside/vmlinux"),
            Err(ManifestError::Invalid(_))
        ));
    }
}
