use std::error::Error;
use std::fmt;
use std::fs;
use std::path::{Component, Path, PathBuf};

use serde::Deserialize;
use sha2::{Digest, Sha256};

const EXPECTED_IDENTITY: &str = "terrabase-noble-podman-v1";
const EXPECTED_TARBALL_IDENTITY: &str = "terrabase-noble-podman-v2";
const EXPECTED_KERNEL_COMMIT: &str = "069c93d2b7bddda87c472453c38c450faf86f999";
const EXPECTED_KERNEL_TAG_OBJECT: &str = "4b0d5e9b869e49681addca8cf22cc83a340c44ff";
const EXPECTED_KERNEL_TARBALL_SHA256: &str =
    "4d6fba95c2244b08a7b4144a4d38b9be4fb31abb5e7682ae40bb5cb11374cfe0";
const EXPECTED_KERNEL_TARBALL_SIGNATURE_SHA256: &str =
    "7e897372a7e0b0400176ea4065a33f4417119398bdaf9baa48a2e1030812ee6d";
const EXPECTED_KERNEL_KEY_SHA256: &str =
    "61b758f0d4a386fc83565b92cfd6c05879573df0e96d44b71a5a3e19e1a8581a";
const EXPECTED_KERNEL_FINGERPRINT: &str = "647F28654894E3BD457199BE38DBBDC86092693E";
const EXPECTED_KERNEL_RELEASE_EPOCH: u64 = 1_789_996_005;
const EXPECTED_FIRECRACKER_V2_ARCHIVE_SHA256: &str =
    "06094a1108ae9e82aa4c23a775aa92758f53f1175d422270d9d6162cb9ade558";
const EXPECTED_FIRECRACKER_V2_EXECUTABLE_MEMBER: &str =
    "release-v1.17.0-x86_64/firecracker-v1.17.0-x86_64";
const EXPECTED_FIRECRACKER_V2_EXECUTABLE_SHA256: &str =
    "99ad0f5cd0514a88aad0e9ae8cfdb3cc3b4ab9d190e1194602406c786b5de7a5";
const EXPECTED_FIRECRACKER_V2_RELEASE_TAG_OBJECT: &str = "29d66eb25954dbe3608eb91c180a6079f54d2ee8";
const EXPECTED_FIRECRACKER_V2_COMMIT: &str = "95f868c8e345b1cc8faccd1a3c910b4989dc3f58";
const EXPECTED_FIRECRACKER_V2_CONFIG_SHA256: &str =
    "ba22401a0c7292a4c024ebcd10a562d4a1f1bfd2faed671406d3b159c0cf5215";
const EXPECTED_FIRECRACKER_V2_POLICY_URL: &str = "https://raw.githubusercontent.com/firecracker-microvm/firecracker/95f868c8e345b1cc8faccd1a3c910b4989dc3f58/docs/kernel-policy.md";
const EXPECTED_FIRECRACKER_V2_POLICY_SHA256: &str =
    "cdc062c6cfe5766acbddaff3f200dfe0a50b6edd4c75b0786f573bb2a2f1d68e";
const EXPECTED_UBUNTU_SHA256: &str =
    "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86";
const EXPECTED_CANONICAL_FINGERPRINT: &str = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";
const EXPECTED_UBUNTU_KEYRING_PATH: &str = "/usr/share/keyrings/ubuntu-archive-keyring.gpg";
const EXPECTED_SNAPSHOT: &str = "20260910T164400Z";
const EXPECTED_APT_SIGNER: &str = "F6ECB3762474EDA9D21B7022871920D1991BC93C";
const EXPECTED_RESEARCH_PACKET_SHA256: &str =
    "00a430ce9dc7e793cf05a81581ffdc75dab495ee74cbd5c21f9e2d567af3632d";
const EXPECTED_RUST_BUILDER_DIGEST: &str =
    "sha256:c49256cbe5ea0188bc658a689500d70c41eb51f009a7a7be209caf60a944f3ec";
const EXPECTED_RUST_BUILDER_INDEX_DIGEST: &str =
    "sha256:93ce27a88655056a51dbdd8f5f2d7ddc071c7b0070fb288a37b5a285fc83971e";
const EXPECTED_DEBIAN_SNAPSHOT_URL: &str =
    "https://snapshot.debian.org/archive/debian/20260924T144048Z/";
const EXPECTED_UBUNTU_BUILDER_DIGEST: &str =
    "sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4";
const EXPECTED_UBUNTU_BUILDER_INDEX_DIGEST: &str =
    "sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3";
const EXPECTED_PODMAN_SHA256: &str =
    "402d507d68ed889dec999e688bd242b5ddb634b3bdc9f03ef9243e66baa367fc";

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Recipe {
    pub schema_version: u32,
    pub recipe_version: String,
    pub identity: String,
    pub architecture: String,
    pub source_date_epoch: String,
    pub kernel: KernelRecipe,
    #[serde(default)]
    pub firecracker: Option<FirecrackerReleaseRecipe>,
    pub ubuntu_base: UbuntuBaseRecipe,
    pub guest_apt: GuestAptRecipe,
    pub rust_cli_builder: RustCliBuilderRecipe,
    pub ubuntu_build_builder: UbuntuBuildBuilderRecipe,
    pub host_podman: HostPodmanRecipe,
    pub filesystem: FilesystemRecipe,
    pub offline_smoke: OfflineSmokeRecipe,
    pub qualification: QualificationRecipe,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FirecrackerReleaseRecipe {
    pub version: String,
    pub release_tag_object: String,
    pub release_tag_signature_status: String,
    pub archive_metadata_source: String,
    pub archive_url: String,
    pub archive_size_bytes: u64,
    pub archive_sha256: String,
    pub executable_member: String,
    pub executable_sha256: String,
    pub executable_review_status: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct KernelRecipe {
    #[serde(default = "default_kernel_source_kind")]
    pub source_kind: String,
    #[serde(default)]
    pub source_version: String,
    #[serde(default)]
    pub source_archive_url: String,
    #[serde(default)]
    pub source_archive_sha256: String,
    #[serde(default)]
    pub source_signature_url: String,
    #[serde(default)]
    pub source_signature_sha256: String,
    #[serde(default)]
    pub release_source_date_epoch: u64,
    #[serde(default)]
    pub release_source_date_utc: String,
    #[serde(default)]
    pub custom_config_sha256: String,
    #[serde(default)]
    pub custom_config_diff_sha256: String,
    #[serde(default)]
    pub custom_config_review_status: String,
    pub repository: String,
    pub annotated_tag: String,
    pub annotated_tag_object: String,
    pub commit: String,
    pub signature_policy: String,
    pub signing_fingerprint: String,
    pub signing_key_url: String,
    pub signing_key_sha256: String,
    pub firecracker_version: String,
    pub firecracker_commit: String,
    pub firecracker_config_url: String,
    pub firecracker_config_sha256: String,
    pub kernel_policy_url: String,
    pub kernel_policy_sha256: String,
    pub expected_kernel_release_prefix: String,
    pub build_user: String,
    pub build_host: String,
    pub build_version: String,
    pub local_version: String,
    pub build_salt: String,
    pub locale: String,
    pub timezone: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum KernelSourceVariant {
    SignedGitTag {
        repository: String,
        annotated_tag: String,
        annotated_tag_object: String,
        commit: String,
    },
    SignedTarball {
        version: String,
        archive_url: String,
        archive_sha256: String,
        signature_url: String,
        signature_sha256: String,
        release_source_date_epoch: u64,
    },
}

fn default_kernel_source_kind() -> String {
    "signed-git-tag".to_owned()
}

impl KernelRecipe {
    /// Return the pinned source variant represented by this recipe.
    ///
    /// # Errors
    ///
    /// Returns an error when `source_kind` is not one of the supported,
    /// signature-verified kernel source variants.
    pub fn source_variant(&self) -> Result<KernelSourceVariant, RecipeError> {
        match self.source_kind.as_str() {
            "signed-git-tag" => Ok(KernelSourceVariant::SignedGitTag {
                repository: self.repository.clone(),
                annotated_tag: self.annotated_tag.clone(),
                annotated_tag_object: self.annotated_tag_object.clone(),
                commit: self.commit.clone(),
            }),
            "kernel.org-signed-tarball" => Ok(KernelSourceVariant::SignedTarball {
                version: self.source_version.clone(),
                archive_url: self.source_archive_url.clone(),
                archive_sha256: self.source_archive_sha256.clone(),
                signature_url: self.source_signature_url.clone(),
                signature_sha256: self.source_signature_sha256.clone(),
                release_source_date_epoch: self.release_source_date_epoch,
            }),
            _ => Err(RecipeError::Invalid(
                "kernel source variant is unsupported".to_owned(),
            )),
        }
    }
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct UbuntuBaseRecipe {
    pub url: String,
    pub sha256: String,
    pub sha256sums_url: String,
    pub signature_url: String,
    pub primary_signing_fingerprint: String,
    pub initial_verifier_source: String,
    pub initial_verifier_path: String,
    pub initial_verifier_sha256: String,
    pub initial_keyring_path: String,
    pub initial_keyring_sha256: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
#[allow(clippy::struct_excessive_bools)] // These are independent APT safety switches, not domain states.
pub struct GuestAptRecipe {
    pub snapshot_url: String,
    pub snapshot_timestamp: String,
    pub suites: Vec<String>,
    pub components: Vec<String>,
    pub architecture: String,
    pub apt_version: String,
    pub allow_unauthenticated: bool,
    pub install_recommends: bool,
    pub install_suggests: bool,
    pub check_valid_until: bool,
    pub initial_keyring_source: String,
    pub initial_keyring_path: String,
    pub initial_keyring_sha256: String,
    pub signing_fingerprint: String,
    pub packages_lock_path: String,
    pub packages_lock_sha256: String,
    pub candidate_packet_path: String,
    pub candidate_packet_sha256: String,
    pub known_noble_updates_inrelease_sha256: String,
    pub known_noble_updates_packages_index_sha256: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RustCliBuilderRecipe {
    pub base_image: String,
    pub base_image_digest: String,
    pub base_image_index_digest: String,
    pub debian_snapshot_url: String,
    pub source_root: String,
    pub source_tree_sha256: String,
    pub workspace_manifest_path: String,
    pub workspace_manifest_sha256: String,
    pub cargo_lock_path: String,
    pub cargo_lock_sha256: String,
    pub rust_toolchain_path: String,
    pub rust_toolchain_sha256: String,
    pub rust_toolchain_channel: String,
    pub target: String,
    pub output_executable_sha256: String,
    pub output_executable_size_bytes: u64,
    pub provision_receipt_sha256: String,
    pub compile_receipt_sha256: String,
    pub network: String,
    pub rootless: bool,
    pub cpu_count: u32,
    pub memory_mib: u32,
    pub pids_limit: u32,
    pub workspace_limit_bytes: u64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
#[allow(clippy::struct_excessive_bools)] // APT solver switches and rootless resource limits are independent pins.
pub struct UbuntuBuildBuilderRecipe {
    pub base_image: String,
    pub base_image_digest: String,
    pub base_image_index_digest: String,
    pub derived_image_digest: String,
    pub derived_image_archive_sha256: String,
    pub derived_image_archive_size_bytes: u64,
    pub derived_image_manifest_digest: String,
    pub derived_image_config_digest: String,
    pub derived_image_platform: String,
    pub snapshot_url: String,
    pub snapshot_timestamp: String,
    pub suites: Vec<String>,
    pub components: Vec<String>,
    pub architecture: String,
    pub apt_version: String,
    pub allow_unauthenticated: bool,
    pub install_recommends: bool,
    pub install_suggests: bool,
    pub check_valid_until: bool,
    pub initial_trust_source: String,
    pub initial_gpgv_path: String,
    pub initial_gpgv_sha256: String,
    pub initial_keyring_path: String,
    pub initial_keyring_sha256: String,
    pub signing_fingerprint: String,
    pub packages_lock_path: String,
    pub packages_lock_sha256: String,
    pub network: String,
    pub rootless: bool,
    pub cpu_count: u32,
    pub memory_mib: u32,
    pub pids_limit: u32,
    pub workspace_limit_bytes: u64,
    pub tools: Vec<BuilderToolPin>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BuilderToolPin {
    pub name: String,
    pub path: String,
    pub version: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HostPodmanRecipe {
    pub path: String,
    pub version: String,
    pub sha256: String,
    pub rootless: bool,
    pub uid: u32,
    pub gid: u32,
    pub cpu_count: u32,
    pub memory_mib: u32,
    pub pids_limit: u32,
    pub workspace_limit_bytes: u64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FilesystemRecipe {
    pub size_bytes: u64,
    pub block_size: u32,
    pub inode_size: u32,
    pub reserved_percent: u8,
    pub label: String,
    pub uuid_strategy: String,
    pub hash_seed_strategy: String,
    pub lazy_itable_init: bool,
    pub lazy_journal_init: bool,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct OfflineSmokeRecipe {
    pub image: String,
    pub digest: String,
    pub platform: String,
    pub network: String,
    pub archive_sha256: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QualificationRecipe {
    pub status: String,
    pub two_clean_builds_required_for_reproducibility: bool,
    pub service_images_qualified: bool,
    pub profile_integration_complete: bool,
}

/// Parsed recipe retaining the exact bytes whose digest determines ext4 UUID.
#[derive(Clone, Debug)]
pub struct RecipeFile {
    path: PathBuf,
    bytes: Vec<u8>,
    sha256: String,
    recipe: Recipe,
}

impl RecipeFile {
    /// Read a regular recipe file without following a symlink, parse it, and
    /// check all immutable candidate identities and resource invariants.
    ///
    /// # Errors
    ///
    /// Returns an error if the path, TOML document, or candidate invariants
    /// are invalid.
    pub fn read(path: &Path) -> Result<Self, RecipeError> {
        let metadata = fs::symlink_metadata(path).map_err(RecipeError::Io)?;
        if !metadata.file_type().is_file() {
            return Err(RecipeError::Invalid(
                "recipe path must name a regular non-symlink file".to_owned(),
            ));
        }
        let bytes = fs::read(path).map_err(RecipeError::Io)?;
        let parsed = Self::parse(path, bytes)?;
        Ok(Self {
            path: fs::canonicalize(path).map_err(RecipeError::Io)?,
            bytes: parsed.bytes,
            sha256: parsed.sha256,
            recipe: parsed.recipe,
        })
    }

    /// Parse a recipe from caller-provided bytes while retaining its intended
    /// path for safe recipe-relative lock resolution.
    ///
    /// # Errors
    ///
    /// Returns an error if the bytes are not valid TOML or violate the
    /// candidate recipe invariants.
    pub fn parse(path: &Path, bytes: Vec<u8>) -> Result<Self, RecipeError> {
        let text = std::str::from_utf8(&bytes)
            .map_err(|_| RecipeError::Invalid("recipe is not valid UTF-8".to_owned()))?;
        let recipe: Recipe = toml::from_str(text).map_err(RecipeError::Toml)?;
        recipe.validate_candidate()?;
        let sha256 = sha256_hex(&bytes);
        Ok(Self {
            path: path.to_path_buf(),
            bytes,
            sha256,
            recipe,
        })
    }

    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    #[must_use]
    pub fn recipe(&self) -> &Recipe {
        &self.recipe
    }

    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    #[must_use]
    pub fn sha256(&self) -> &str {
        &self.sha256
    }

    /// Identify recipe-relative lock files without accepting absolute paths,
    /// parent traversal, or an existing symlink component.
    ///
    /// # Errors
    ///
    /// Returns an error if the relative path traverses or a path component is
    /// a symlink.
    pub fn resolve_recipe_path(&self, relative: &str) -> Result<PathBuf, RecipeError> {
        validate_relative_path(relative)?;
        let parent = self
            .path
            .parent()
            .ok_or_else(|| RecipeError::Invalid("recipe has no parent directory".to_owned()))?;
        let resolved = parent.join(relative);
        reject_symlink_components(parent, relative)?;
        Ok(resolved)
    }

    /// Resolve one of the four pinned Rust workspace inputs below the
    /// canonical Firecracker harness root.
    ///
    /// # Errors
    ///
    /// Returns an error if the input is not one of the exact recipe pins, the
    /// recipe is not at `images/<identity>/recipe.toml`, any path component is
    /// a symlink, the resolved input escapes the workspace, or its type is
    /// wrong.
    pub(crate) fn resolve_workspace_input(&self, relative: &str) -> Result<PathBuf, RecipeError> {
        let (workspace_relative, expect_directory) = match relative {
            "../../crates/fc-image-builder" => ("crates/fc-image-builder", true),
            "../../Cargo.toml" => ("Cargo.toml", false),
            "../../Cargo.lock" => ("Cargo.lock", false),
            "../../rust-toolchain.toml" => ("rust-toolchain.toml", false),
            _ => {
                return Err(RecipeError::Invalid(
                    "Rust workspace input differs from its exact recipe pin".to_owned(),
                ));
            }
        };
        let workspace_root = self.canonical_workspace_root()?;
        let relative_path = Path::new(workspace_relative);
        let component_count = relative_path.components().count();
        let mut resolved = workspace_root.clone();
        for (index, component) in relative_path.components().enumerate() {
            let Component::Normal(part) = component else {
                return Err(RecipeError::Invalid(
                    "Rust workspace input contains a non-normal component".to_owned(),
                ));
            };
            resolved.push(part);
            let metadata = fs::symlink_metadata(&resolved).map_err(RecipeError::Io)?;
            if metadata.file_type().is_symlink()
                || (index + 1 != component_count && !metadata.is_dir())
            {
                return Err(RecipeError::Invalid(format!(
                    "Rust workspace input contains an unsafe component: {}",
                    resolved.display()
                )));
            }
            let canonical = fs::canonicalize(&resolved).map_err(RecipeError::Io)?;
            if !canonical.starts_with(&workspace_root) {
                return Err(RecipeError::Invalid(
                    "Rust workspace input escaped its harness root".to_owned(),
                ));
            }
            if index + 1 == component_count
                && ((expect_directory && !metadata.is_dir())
                    || (!expect_directory && !metadata.is_file()))
            {
                return Err(RecipeError::Invalid(format!(
                    "Rust workspace input has the wrong file type: {}",
                    resolved.display()
                )));
            }
        }
        Ok(resolved)
    }

    fn canonical_workspace_root(&self) -> Result<PathBuf, RecipeError> {
        let metadata = fs::symlink_metadata(&self.path).map_err(RecipeError::Io)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(RecipeError::Invalid(
                "recipe path must be a regular non-symlink file".to_owned(),
            ));
        }
        let recipe_path = fs::canonicalize(&self.path).map_err(RecipeError::Io)?;
        if recipe_path.file_name().and_then(|name| name.to_str()) != Some("recipe.toml") {
            return Err(RecipeError::Invalid(
                "Rust workspace recipe must be named recipe.toml".to_owned(),
            ));
        }
        let identity_dir = recipe_path
            .parent()
            .ok_or_else(|| RecipeError::Invalid("recipe has no parent directory".to_owned()))?;
        if identity_dir.file_name().and_then(|name| name.to_str())
            != Some(self.recipe.identity.as_str())
        {
            return Err(RecipeError::Invalid(
                "Rust workspace recipe directory differs from its identity".to_owned(),
            ));
        }
        let images_dir = identity_dir
            .parent()
            .ok_or_else(|| RecipeError::Invalid("recipe has no images directory".to_owned()))?;
        if images_dir.file_name().and_then(|name| name.to_str()) != Some("images") {
            return Err(RecipeError::Invalid(
                "Rust workspace recipe must live under images/<identity>".to_owned(),
            ));
        }
        let workspace_path = images_dir.parent().ok_or_else(|| {
            RecipeError::Invalid("images directory has no workspace root".to_owned())
        })?;
        let workspace_root = fs::canonicalize(workspace_path).map_err(RecipeError::Io)?;
        let workspace_metadata = fs::symlink_metadata(&workspace_root).map_err(RecipeError::Io)?;
        if workspace_metadata.file_type().is_symlink() || !workspace_metadata.is_dir() {
            return Err(RecipeError::Invalid(
                "Firecracker harness workspace root is not a regular directory".to_owned(),
            ));
        }
        if recipe_path
            != workspace_root
                .join("images")
                .join(&self.recipe.identity)
                .join("recipe.toml")
        {
            return Err(RecipeError::Invalid(
                "recipe path does not resolve under its canonical harness workspace".to_owned(),
            ));
        }
        Ok(workspace_root)
    }

    /// List trust, package and tool inputs that must be filled before resolve
    /// or build can cross the corresponding verification boundary.
    #[must_use]
    #[allow(clippy::too_many_lines)] // One gate inventory makes every fail-closed input visible together.
    pub fn unresolved_inputs(&self) -> RecipeGaps {
        let mut missing = Vec::new();
        if !is_sha256(&self.recipe.kernel.signing_key_sha256)
            || !is_https_url(&self.recipe.kernel.signing_key_url)
            || !valid_fingerprint(&self.recipe.kernel.signing_fingerprint)
        {
            missing.push("verified kernel tag signer and pinned public key".to_owned());
        }
        if self.recipe.identity == EXPECTED_TARBALL_IDENTITY
            && (self.recipe.kernel.custom_config_review_status != "reviewed"
                || !is_sha256(&self.recipe.kernel.custom_config_sha256)
                || !is_sha256(&self.recipe.kernel.custom_config_diff_sha256))
        {
            missing
                .push("reviewed upstream-kernel config output and config-diff hashes".to_owned());
        }
        if self.recipe.identity == EXPECTED_TARBALL_IDENTITY
            && self.recipe.firecracker.as_ref().is_none_or(|release| {
                release.executable_review_status != "reviewed"
                    || release.executable_member.is_empty()
                    || !is_sha256(&release.executable_sha256)
            })
        {
            missing.push("reviewed Firecracker release executable member and hash".to_owned());
        }
        if !is_sha256(&self.recipe.ubuntu_base.initial_verifier_sha256)
            || !is_sha256(&self.recipe.ubuntu_base.initial_keyring_sha256)
        {
            missing.push("pinned Ubuntu-child gpgv and initial archive keyring hashes".to_owned());
        }
        for (name, hash) in [
            (
                "guest archive keyring",
                self.recipe.guest_apt.initial_keyring_sha256.as_str(),
            ),
            (
                "Ubuntu builder initial archive keyring",
                self.recipe
                    .ubuntu_build_builder
                    .initial_keyring_sha256
                    .as_str(),
            ),
            (
                "Rust CLI source tree",
                self.recipe.rust_cli_builder.source_tree_sha256.as_str(),
            ),
            (
                "Rust CLI executable",
                self.recipe
                    .rust_cli_builder
                    .output_executable_sha256
                    .as_str(),
            ),
        ] {
            if !is_sha256(hash) {
                missing.push(format!("pinned {name} SHA-256"));
            }
        }
        if !matches_workspace_input_hash(
            self,
            &self.recipe.rust_cli_builder.cargo_lock_path,
            &self.recipe.rust_cli_builder.cargo_lock_sha256,
        ) {
            missing.push("Rust CLI Cargo.lock pin does not match the workspace lock".to_owned());
        }
        if !matches_workspace_input_hash(
            self,
            &self.recipe.rust_cli_builder.workspace_manifest_path,
            &self.recipe.rust_cli_builder.workspace_manifest_sha256,
        ) {
            missing.push("Rust workspace manifest pin does not match Cargo.toml".to_owned());
        }
        if !matches_workspace_input_hash(
            self,
            &self.recipe.rust_cli_builder.rust_toolchain_path,
            &self.recipe.rust_cli_builder.rust_toolchain_sha256,
        ) {
            missing.push("Rust CLI toolchain pin does not match rust-toolchain.toml".to_owned());
        }
        if !is_sha256(&self.recipe.rust_cli_builder.source_tree_sha256)
            || source_tree_sha256_at_workspace_input(
                self,
                &self.recipe.rust_cli_builder.source_root,
            )
            .as_deref()
                != Some(self.recipe.rust_cli_builder.source_tree_sha256.as_str())
        {
            missing.push("Rust CLI source tree digest does not match its locked input".to_owned());
        }
        for (name, hash) in [
            (
                "Rust CLI provision receipt",
                self.recipe
                    .rust_cli_builder
                    .provision_receipt_sha256
                    .as_str(),
            ),
            (
                "Rust CLI compile receipt",
                self.recipe.rust_cli_builder.compile_receipt_sha256.as_str(),
            ),
        ] {
            if !is_sha256(hash) {
                missing.push(format!("pinned {name} SHA-256"));
            }
        }
        if self.recipe.rust_cli_builder.output_executable_size_bytes == 0 {
            missing.push("Rust CLI executable byte length".to_owned());
        }
        if !is_sha256(&self.recipe.guest_apt.packages_lock_sha256) {
            missing.push("apt-solver-verified guest package lock SHA-256".to_owned());
        }
        if !is_sha256(&self.recipe.ubuntu_build_builder.packages_lock_sha256) {
            missing.push("apt-solver-verified builder package lock SHA-256".to_owned());
        }
        for (relative, expected_sha256) in [
            (
                self.recipe.guest_apt.packages_lock_path.as_str(),
                self.recipe.guest_apt.packages_lock_sha256.as_str(),
            ),
            (
                self.recipe.ubuntu_build_builder.packages_lock_path.as_str(),
                self.recipe
                    .ubuntu_build_builder
                    .packages_lock_sha256
                    .as_str(),
            ),
        ] {
            match self.resolve_recipe_path(relative) {
                Ok(path) if path.is_file() => {
                    if !matches_path_hash(&path, expected_sha256) {
                        missing.push(format!("{relative} SHA-256 mismatch or unpinned hash"));
                    }
                }
                Ok(_) => missing.push(format!("complete locked input file {relative}")),
                Err(_) => missing.push(format!("safe recipe-relative path {relative}")),
            }
        }
        if !self
            .recipe
            .ubuntu_build_builder
            .derived_image_digest
            .strip_prefix("sha256:")
            .is_some_and(is_sha256)
        {
            missing.push("derived Ubuntu builder OCI image digest".to_owned());
        }
        if !is_sha256(
            &self
                .recipe
                .ubuntu_build_builder
                .derived_image_archive_sha256,
        ) {
            missing.push("pinned derived Ubuntu builder archive SHA-256".to_owned());
        }
        for (name, digest) in [
            (
                "manifest",
                self.recipe
                    .ubuntu_build_builder
                    .derived_image_manifest_digest
                    .as_str(),
            ),
            (
                "config",
                self.recipe
                    .ubuntu_build_builder
                    .derived_image_config_digest
                    .as_str(),
            ),
        ] {
            if !digest.strip_prefix("sha256:").is_some_and(is_sha256) {
                missing.push(format!("pinned derived Ubuntu builder OCI {name} digest"));
            }
        }
        if self
            .recipe
            .ubuntu_build_builder
            .derived_image_archive_size_bytes
            == 0
        {
            missing.push("derived Ubuntu builder OCI archive byte length".to_owned());
        }
        for tool in &self.recipe.ubuntu_build_builder.tools {
            if tool.version.is_empty() || !is_sha256(&tool.sha256) {
                missing.push(format!(
                    "locked {} version and executable SHA-256",
                    tool.name
                ));
            }
        }
        if self.recipe.host_podman.sha256 != EXPECTED_PODMAN_SHA256
            || sha256_file_at_absolute_path(&self.recipe.host_podman.path).as_deref()
                != Some(self.recipe.host_podman.sha256.as_str())
        {
            missing.push("pinned rootless host Podman executable identity".to_owned());
        }
        if !is_sha256(&self.recipe.offline_smoke.archive_sha256) {
            missing.push("offline BusyBox OCI archive SHA-256".to_owned());
        }
        match self.resolve_recipe_path(&self.recipe.guest_apt.candidate_packet_path) {
            Ok(path) if path.is_file() => match fs::read(&path) {
                Ok(bytes)
                    if sha256_hex(&bytes) == self.recipe.guest_apt.candidate_packet_sha256 => {}
                _ => missing.push("candidate dependency packet digest mismatch".to_owned()),
            },
            Ok(_) => missing.push("candidate dependency research packet".to_owned()),
            Err(_) => missing.push("safe candidate dependency packet path".to_owned()),
        }
        RecipeGaps(missing)
    }

    /// Deterministic RFC 4122 variant/version-8 UUID derived from the exact
    /// versioned recipe bytes.
    #[must_use]
    pub fn filesystem_uuid(&self) -> String {
        let digest = Sha256::digest(&self.bytes);
        let mut uuid = [0_u8; 16];
        uuid.copy_from_slice(&digest[..16]);
        uuid[6] = (uuid[6] & 0x0f) | 0x80;
        uuid[8] = (uuid[8] & 0x3f) | 0x80;
        format!(
            "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
            uuid[0],
            uuid[1],
            uuid[2],
            uuid[3],
            uuid[4],
            uuid[5],
            uuid[6],
            uuid[7],
            uuid[8],
            uuid[9],
            uuid[10],
            uuid[11],
            uuid[12],
            uuid[13],
            uuid[14],
            uuid[15]
        )
    }
}

impl Recipe {
    #[allow(clippy::too_many_lines)] // One fail-closed check keeps candidate identities auditable together.
    fn validate_candidate(&self) -> Result<(), RecipeError> {
        if self.schema_version != 2 || self.recipe_version != "2.0.0" {
            return Err(RecipeError::Invalid(
                "unsupported recipe schema or version".to_owned(),
            ));
        }
        let profile_is_git =
            self.identity == EXPECTED_IDENTITY && self.kernel.source_kind == "signed-git-tag";
        let profile_is_tarball = self.identity == EXPECTED_TARBALL_IDENTITY
            && self.kernel.source_kind == "kernel.org-signed-tarball";
        if (!profile_is_git && !profile_is_tarball) || self.architecture != "amd64" {
            return Err(RecipeError::Invalid(
                "recipe identity or architecture does not match the candidate".to_owned(),
            ));
        }
        let expected_epoch_mode = if profile_is_git {
            "kernel-git-commit-time"
        } else {
            "kernel-release-time"
        };
        if self.source_date_epoch != expected_epoch_mode {
            return Err(RecipeError::Invalid(
                "SOURCE_DATE_EPOCH derivation does not match the locked kernel source variant"
                    .to_owned(),
            ));
        }
        validate_https_url(&self.kernel.firecracker_config_url)?;
        validate_sha256(&self.kernel.firecracker_commit)?;
        validate_sha256(&self.kernel.firecracker_config_sha256)?;
        validate_optional_sha256(&self.kernel.custom_config_sha256)?;
        validate_optional_sha256(&self.kernel.custom_config_diff_sha256)?;
        validate_https_url(&self.kernel.kernel_policy_url)?;
        validate_optional_sha256(&self.kernel.source_archive_sha256)?;
        validate_optional_sha256(&self.kernel.source_signature_sha256)?;
        if profile_is_git {
            validate_https_url(&self.kernel.repository)?;
            validate_git_object(&self.kernel.annotated_tag_object)?;
            validate_sha256(&self.kernel.commit)?;
            validate_sha256(&self.kernel.kernel_policy_sha256)?;
            if self.kernel.commit != EXPECTED_KERNEL_COMMIT
                || self.kernel.annotated_tag != "microvm-kernel-6.1.155-13.282.amzn2023"
                || self.kernel.annotated_tag_object != EXPECTED_KERNEL_TAG_OBJECT
            {
                return Err(RecipeError::Invalid(
                    "kernel tag object or commit differs from the reviewed candidate".to_owned(),
                ));
            }
        } else {
            validate_https_url(&self.kernel.source_archive_url)?;
            validate_https_url(&self.kernel.source_signature_url)?;
            validate_sha256(&self.kernel.signing_key_sha256)?;
            validate_fingerprint(&self.kernel.signing_fingerprint)?;
            if self.kernel.source_version != "6.18.53"
                || self.kernel.source_archive_url
                    != "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.18.53.tar.xz"
                || self.kernel.source_archive_sha256 != EXPECTED_KERNEL_TARBALL_SHA256
                || self.kernel.source_signature_url
                    != "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.18.53.tar.sign"
                || self.kernel.source_signature_sha256 != EXPECTED_KERNEL_TARBALL_SIGNATURE_SHA256
                || self.kernel.signing_key_url
                    != "https://kernel.org/.well-known/openpgpkey/hu/e3n9xnm94c5apezqnj1pmrfuaoyfm8cf?l=gregkh"
                || self.kernel.signing_key_sha256 != EXPECTED_KERNEL_KEY_SHA256
                || self.kernel.signing_fingerprint != EXPECTED_KERNEL_FINGERPRINT
                || self.kernel.release_source_date_epoch != EXPECTED_KERNEL_RELEASE_EPOCH
                || self.kernel.signature_policy != "required-before-extract"
                || self.kernel.firecracker_version != "1.17.0"
                || self.kernel.firecracker_commit != EXPECTED_FIRECRACKER_V2_COMMIT
                || self.kernel.firecracker_config_sha256 != EXPECTED_FIRECRACKER_V2_CONFIG_SHA256
                || self.kernel.kernel_policy_url != EXPECTED_FIRECRACKER_V2_POLICY_URL
                || self.kernel.kernel_policy_sha256 != EXPECTED_FIRECRACKER_V2_POLICY_SHA256
                || self.kernel.release_source_date_utc != "2026-09-21T13:06:45Z"
                || ![
                    "pending-upstream-config-review",
                    "generated-pending-independent-review",
                    "reviewed",
                ]
                .contains(&self.kernel.custom_config_review_status.as_str())
                || (self.kernel.custom_config_review_status != "pending-upstream-config-review"
                    && (!is_sha256(&self.kernel.custom_config_sha256)
                        || !is_sha256(&self.kernel.custom_config_diff_sha256)))
                || (self.kernel.custom_config_review_status == "pending-upstream-config-review"
                    && (!self.kernel.custom_config_sha256.is_empty()
                        || !self.kernel.custom_config_diff_sha256.is_empty()))
            {
                return Err(RecipeError::Invalid(
                    "kernel.org tarball, Greg KH signer, release epoch, Firecracker policy, or custom-config review state differs from the reviewed candidate".to_owned(),
                ));
            }
        }
        if self.kernel.build_user != "builder"
            || self.kernel.build_host != "terrabase"
            || self.kernel.build_version != "1"
            || self.kernel.local_version != "-terrabase"
            || self.kernel.build_salt != self.identity
            || self.kernel.locale != "C.UTF-8"
            || self.kernel.timezone != "UTC"
        {
            return Err(RecipeError::Invalid(
                "kernel build environment differs from the fixed candidate environment".to_owned(),
            ));
        }
        match (&self.firecracker, profile_is_tarball) {
            (Some(release), true) => {
                validate_git_object(&release.release_tag_object)?;
                validate_https_url(&release.archive_url)?;
                validate_optional_sha256(&release.executable_sha256)?;
                if !release.executable_member.is_empty() {
                    validate_relative_path(&release.executable_member)?;
                }
                if release.version != "1.17.0"
                    || release.release_tag_object != EXPECTED_FIRECRACKER_V2_RELEASE_TAG_OBJECT
                    || release.release_tag_signature_status != "unsigned"
                    || release.archive_metadata_source != "github-release-assets-api"
                    || release.archive_url
                        != "https://github.com/firecracker-microvm/firecracker/releases/download/v1.17.0/firecracker-v1.17.0-x86_64.tgz"
                    || release.archive_size_bytes != 7_464_385
                    || release.archive_sha256 != EXPECTED_FIRECRACKER_V2_ARCHIVE_SHA256
                    || (release.executable_review_status != "pending-observation"
                        && (release.executable_member != EXPECTED_FIRECRACKER_V2_EXECUTABLE_MEMBER
                            || release.executable_sha256
                                != EXPECTED_FIRECRACKER_V2_EXECUTABLE_SHA256))
                    || !["pending-observation", "candidate-observed", "reviewed"]
                        .contains(&release.executable_review_status.as_str())
                    || (release.executable_review_status == "pending-observation"
                        && (!release.executable_member.is_empty()
                            || !release.executable_sha256.is_empty()))
                    || (release.executable_review_status != "pending-observation"
                        && (release.executable_member.is_empty()
                            || !is_sha256(&release.executable_sha256)))
                {
                    return Err(RecipeError::Invalid(
                        "Firecracker release archive identity or unsigned-tag provenance changed"
                            .to_owned(),
                    ));
                }
            }
            (None, false) => {}
            _ => {
                return Err(RecipeError::Invalid(
                    "Firecracker release binary pin is required only for the v2 candidate"
                        .to_owned(),
                ));
            }
        }
        if profile_is_git
            && (!self.kernel.custom_config_sha256.is_empty()
                || !self.kernel.custom_config_diff_sha256.is_empty()
                || !self.kernel.custom_config_review_status.is_empty())
        {
            return Err(RecipeError::Invalid(
                "historical Git-kernel recipe cannot acquire v2 custom-config metadata".to_owned(),
            ));
        }
        validate_optional_sha256(&self.kernel.signing_key_sha256)?;
        if !self.kernel.signing_key_url.is_empty() {
            validate_https_url(&self.kernel.signing_key_url)?;
        }
        if !self.kernel.signing_fingerprint.is_empty() {
            validate_fingerprint(&self.kernel.signing_fingerprint)?;
        }
        let expected_signature_policy = if profile_is_git {
            "required-before-fetch"
        } else {
            "required-before-extract"
        };
        if self.kernel.signature_policy != expected_signature_policy {
            return Err(RecipeError::Invalid(
                "kernel source signature policy differs from the accepted candidate".to_owned(),
            ));
        }
        validate_https_url(&self.ubuntu_base.url)?;
        validate_https_url(&self.ubuntu_base.sha256sums_url)?;
        validate_https_url(&self.ubuntu_base.signature_url)?;
        validate_sha256(&self.ubuntu_base.sha256)?;
        validate_optional_sha256(&self.ubuntu_base.initial_verifier_sha256)?;
        validate_optional_sha256(&self.ubuntu_base.initial_keyring_sha256)?;
        validate_fingerprint(&self.ubuntu_base.primary_signing_fingerprint)?;
        validate_absolute_path(&self.ubuntu_base.initial_verifier_path)?;
        validate_absolute_path(&self.ubuntu_base.initial_keyring_path)?;
        if self.ubuntu_base.sha256 != EXPECTED_UBUNTU_SHA256
            || self.ubuntu_base.primary_signing_fingerprint != EXPECTED_CANONICAL_FINGERPRINT
            || self.ubuntu_base.initial_verifier_source != "pinned-ubuntu-child"
            || self.ubuntu_base.initial_verifier_path != "/usr/bin/gpgv"
            || self.ubuntu_base.initial_keyring_path != EXPECTED_UBUNTU_KEYRING_PATH
        {
            return Err(RecipeError::Invalid(
                "Ubuntu base digest or signer differs from the reviewed candidate".to_owned(),
            ));
        }
        validate_apt_snapshot(
            &self.guest_apt.snapshot_url,
            &self.guest_apt.snapshot_timestamp,
            &self.guest_apt.suites,
            &self.guest_apt.components,
            &self.guest_apt.architecture,
            &self.guest_apt.apt_version,
            &self.guest_apt.signing_fingerprint,
        )?;
        validate_absolute_path(&self.guest_apt.initial_keyring_path)?;
        validate_optional_sha256(&self.guest_apt.initial_keyring_sha256)?;
        validate_relative_path(&self.guest_apt.packages_lock_path)?;
        validate_optional_sha256(&self.guest_apt.packages_lock_sha256)?;
        validate_relative_path(&self.guest_apt.candidate_packet_path)?;
        validate_sha256(&self.guest_apt.candidate_packet_sha256)?;
        validate_sha256(&self.guest_apt.known_noble_updates_inrelease_sha256)?;
        validate_sha256(&self.guest_apt.known_noble_updates_packages_index_sha256)?;
        if self.guest_apt.candidate_packet_sha256 != EXPECTED_RESEARCH_PACKET_SHA256
            || self.guest_apt.initial_keyring_source != "pinned-ubuntu-child"
            || self.guest_apt.initial_keyring_path != self.ubuntu_base.initial_keyring_path
            || self.guest_apt.initial_keyring_sha256 != self.ubuntu_base.initial_keyring_sha256
            || self.guest_apt.allow_unauthenticated
            || self.guest_apt.install_recommends
            || self.guest_apt.install_suggests
            || !self.guest_apt.check_valid_until
        {
            return Err(RecipeError::Invalid(
                "guest apt trust or solver settings differ from the accepted candidate".to_owned(),
            ));
        }
        validate_oci_digest(&self.rust_cli_builder.base_image_digest)?;
        validate_oci_digest(&self.rust_cli_builder.base_image_index_digest)?;
        validate_sha256(&self.rust_cli_builder.source_tree_sha256)?;
        validate_https_url(&self.rust_cli_builder.debian_snapshot_url)?;
        if self.rust_cli_builder.debian_snapshot_url != EXPECTED_DEBIAN_SNAPSHOT_URL {
            return Err(RecipeError::Invalid(
                "Rust CLI support packages must use the candidate immutable Bookworm snapshot"
                    .to_owned(),
            ));
        }
        validate_optional_sha256(&self.rust_cli_builder.workspace_manifest_sha256)?;
        validate_sha256(&self.rust_cli_builder.cargo_lock_sha256)?;
        validate_sha256(&self.rust_cli_builder.rust_toolchain_sha256)?;
        validate_optional_sha256(&self.rust_cli_builder.output_executable_sha256)?;
        validate_optional_sha256(&self.rust_cli_builder.provision_receipt_sha256)?;
        validate_optional_sha256(&self.rust_cli_builder.compile_receipt_sha256)?;
        if self.rust_cli_builder.base_image != "docker.io/library/rust:1.98.1-bookworm"
            || self.rust_cli_builder.base_image_digest != EXPECTED_RUST_BUILDER_DIGEST
            || self.rust_cli_builder.base_image_index_digest != EXPECTED_RUST_BUILDER_INDEX_DIGEST
            || self.rust_cli_builder.source_root != "../../crates/fc-image-builder"
            || self.rust_cli_builder.workspace_manifest_path != "../../Cargo.toml"
            || self.rust_cli_builder.cargo_lock_path != "../../Cargo.lock"
            || self.rust_cli_builder.rust_toolchain_path != "../../rust-toolchain.toml"
            || self.rust_cli_builder.rust_toolchain_channel != "1.98.1"
            || self.rust_cli_builder.target != "x86_64-unknown-linux-musl"
            || self.rust_cli_builder.network != "none"
            || !self.rust_cli_builder.rootless
            || !valid_limits(
                self.rust_cli_builder.cpu_count,
                self.rust_cli_builder.memory_mib,
                self.rust_cli_builder.pids_limit,
                self.rust_cli_builder.workspace_limit_bytes,
            )
        {
            return Err(RecipeError::Invalid(
                "Rust CLI builder image, source or resource boundary is invalid".to_owned(),
            ));
        }
        validate_https_url(&self.ubuntu_build_builder.snapshot_url)?;
        validate_apt_snapshot(
            &self.ubuntu_build_builder.snapshot_url,
            &self.ubuntu_build_builder.snapshot_timestamp,
            &self.ubuntu_build_builder.suites,
            &self.ubuntu_build_builder.components,
            &self.ubuntu_build_builder.architecture,
            &self.ubuntu_build_builder.apt_version,
            &self.ubuntu_build_builder.signing_fingerprint,
        )?;
        validate_oci_digest(&self.ubuntu_build_builder.base_image_digest)?;
        validate_oci_digest(&self.ubuntu_build_builder.base_image_index_digest)?;
        validate_optional_oci_digest(&self.ubuntu_build_builder.derived_image_digest)?;
        validate_optional_sha256(&self.ubuntu_build_builder.derived_image_archive_sha256)?;
        validate_optional_oci_digest(&self.ubuntu_build_builder.derived_image_manifest_digest)?;
        validate_optional_oci_digest(&self.ubuntu_build_builder.derived_image_config_digest)?;
        if self.ubuntu_build_builder.derived_image_platform != "linux/amd64" {
            return Err(RecipeError::Invalid(
                "derived Ubuntu builder archive platform must be linux/amd64".to_owned(),
            ));
        }
        validate_optional_sha256(&self.ubuntu_build_builder.initial_keyring_sha256)?;
        validate_optional_sha256(&self.ubuntu_build_builder.initial_gpgv_sha256)?;
        validate_sha256(&self.ubuntu_build_builder.initial_gpgv_sha256)?;
        validate_absolute_path(&self.ubuntu_build_builder.initial_gpgv_path)?;
        validate_absolute_path(&self.ubuntu_build_builder.initial_keyring_path)?;
        validate_relative_path(&self.ubuntu_build_builder.packages_lock_path)?;
        validate_optional_sha256(&self.ubuntu_build_builder.packages_lock_sha256)?;
        validate_builder_tools(&self.ubuntu_build_builder.tools)?;
        if self.ubuntu_build_builder.base_image != "docker.io/library/ubuntu:24.04"
            || self.ubuntu_build_builder.base_image_digest != EXPECTED_UBUNTU_BUILDER_DIGEST
            || self.ubuntu_build_builder.base_image_index_digest
                != EXPECTED_UBUNTU_BUILDER_INDEX_DIGEST
            || self.ubuntu_build_builder.network != "none"
            || !self.ubuntu_build_builder.rootless
            || self.ubuntu_build_builder.initial_trust_source != "pinned-ubuntu-child"
            || self.ubuntu_build_builder.initial_gpgv_path != self.ubuntu_base.initial_verifier_path
            || self.ubuntu_build_builder.initial_gpgv_sha256
                != self.ubuntu_base.initial_verifier_sha256
            || self.ubuntu_build_builder.initial_keyring_path
                != self.ubuntu_base.initial_keyring_path
            || self.ubuntu_build_builder.initial_keyring_sha256
                != self.ubuntu_base.initial_keyring_sha256
            || self.ubuntu_build_builder.allow_unauthenticated
            || self.ubuntu_build_builder.install_recommends
            || self.ubuntu_build_builder.install_suggests
            || !self.ubuntu_build_builder.check_valid_until
            || self.ubuntu_build_builder.packages_lock_path == self.guest_apt.packages_lock_path
            || !valid_limits(
                self.ubuntu_build_builder.cpu_count,
                self.ubuntu_build_builder.memory_mib,
                self.ubuntu_build_builder.pids_limit,
                self.ubuntu_build_builder.workspace_limit_bytes,
            )
        {
            return Err(RecipeError::Invalid(
                "Ubuntu builder identity, trust root or resource boundary is invalid".to_owned(),
            ));
        }
        validate_absolute_path(&self.host_podman.path)?;
        validate_sha256(&self.host_podman.sha256)?;
        if self.host_podman.path
            != "/nix/store/mi7484cckkxv8hgiawq1cg89sxknc3bh-podman-5.8.6/bin/podman"
            || self.host_podman.version != "5.8.6"
            || self.host_podman.sha256 != EXPECTED_PODMAN_SHA256
            || !self.host_podman.rootless
            || self.host_podman.uid != 1000
            || self.host_podman.gid != 1000
            || !valid_limits(
                self.host_podman.cpu_count,
                self.host_podman.memory_mib,
                self.host_podman.pids_limit,
                self.host_podman.workspace_limit_bytes,
            )
        {
            return Err(RecipeError::Invalid(
                "host Podman executable or rootless limits differ from the inspected candidate"
                    .to_owned(),
            ));
        }
        if self.filesystem.size_bytes != 16 * 1024 * 1024 * 1024
            || self.filesystem.block_size != 4096
            || self.filesystem.inode_size != 256
            || self.filesystem.reserved_percent != 0
            || self.filesystem.label.len() > 16
            || self.filesystem.uuid_strategy
                != "sha256(recipe-bytes)[0..16], RFC4122 variant and version 8 bits"
            || self.filesystem.hash_seed_strategy != "filesystem UUID"
            || self.filesystem.lazy_itable_init
            || self.filesystem.lazy_journal_init
        {
            return Err(RecipeError::Invalid(
                "ext4 identity or deterministic metadata differs from the accepted recipe"
                    .to_owned(),
            ));
        }
        validate_oci_digest(&self.offline_smoke.digest)?;
        validate_optional_sha256(&self.offline_smoke.archive_sha256)?;
        if self.offline_smoke.image != "docker.io/library/busybox"
            || self.offline_smoke.platform != "linux/amd64"
            || self.offline_smoke.network != "none"
        {
            return Err(RecipeError::Invalid(
                "offline OCI smoke image identity is invalid".to_owned(),
            ));
        }
        if !["candidate-lock-incomplete", "inputs-locked"]
            .contains(&self.qualification.status.as_str())
            || !self
                .qualification
                .two_clean_builds_required_for_reproducibility
            || self.qualification.service_images_qualified
            || self.qualification.profile_integration_complete
        {
            return Err(RecipeError::Invalid(
                "candidate qualification status must remain explicitly unqualified".to_owned(),
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecipeGaps(Vec<String>);

impl RecipeGaps {
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    #[must_use]
    pub fn as_slice(&self) -> &[String] {
        &self.0
    }
}

impl fmt::Display for RecipeGaps {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.0.is_empty() {
            return formatter.write_str("all lock inputs are present");
        }
        formatter.write_str("unresolved recipe inputs: ")?;
        for (index, gap) in self.0.iter().enumerate() {
            if index != 0 {
                formatter.write_str(", ")?;
            }
            formatter.write_str(gap)?;
        }
        Ok(())
    }
}

#[derive(Debug)]
pub enum RecipeError {
    Io(std::io::Error),
    Toml(toml::de::Error),
    Invalid(String),
}

impl fmt::Display for RecipeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "recipe I/O error: {error}"),
            Self::Toml(error) => write!(formatter, "invalid recipe TOML: {error}"),
            Self::Invalid(error) => write!(formatter, "invalid image recipe: {error}"),
        }
    }
}

impl Error for RecipeError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Toml(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

fn validate_https_url(value: &str) -> Result<(), RecipeError> {
    let url = reqwest::Url::parse(value)
        .map_err(|error| RecipeError::Invalid(format!("invalid locked URL: {error}")))?;
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err(RecipeError::Invalid(
            "external input URL must use HTTPS without credentials or fragments".to_owned(),
        ));
    }
    Ok(())
}

fn validate_sha256(value: &str) -> Result<(), RecipeError> {
    if !value.is_empty() && !is_sha256(value) {
        return Err(RecipeError::Invalid(
            "SHA-256 pins must contain exactly 64 hexadecimal digits".to_owned(),
        ));
    }
    Ok(())
}

/// Permit an empty candidate placeholder; `unresolved_inputs` keeps it out
/// of every fetch or build path until the recipe pins a verified value.
fn validate_optional_sha256(value: &str) -> Result<(), RecipeError> {
    validate_sha256(value)
}

fn validate_apt_snapshot(
    snapshot_url: &str,
    timestamp: &str,
    suites: &[String],
    components: &[String],
    architecture: &str,
    apt_version: &str,
    signer: &str,
) -> Result<(), RecipeError> {
    validate_https_url(snapshot_url)?;
    validate_fingerprint(signer)?;
    if snapshot_url != format!("https://snapshot.ubuntu.com/ubuntu/{EXPECTED_SNAPSHOT}/")
        || timestamp != EXPECTED_SNAPSHOT
        || architecture != "amd64"
        || suites != ["noble", "noble-updates", "noble-security"]
        || components != ["main", "universe"]
        || apt_version != "2.8.3"
        || signer != EXPECTED_APT_SIGNER
    {
        return Err(RecipeError::Invalid(
            "apt snapshot identity, suites, signer or architecture differs from the reviewed candidate"
                .to_owned(),
        ));
    }
    Ok(())
}

fn validate_builder_tools(tools: &[BuilderToolPin]) -> Result<(), RecipeError> {
    const EXPECTED: [(&str, &str); 12] = [
        ("gpgv", "/usr/bin/gpgv"),
        ("apt-get", "/usr/bin/apt-get"),
        ("apt-cache", "/usr/bin/apt-cache"),
        ("dpkg", "/usr/bin/dpkg"),
        ("dpkg-deb", "/usr/bin/dpkg-deb"),
        ("gcc", "/usr/bin/gcc"),
        ("make", "/usr/bin/make"),
        ("mke2fs", "/usr/sbin/mke2fs"),
        ("e2fsck", "/usr/sbin/e2fsck"),
        ("xz", "/usr/bin/xz"),
        ("git", "/usr/bin/git"),
        ("tar", "/usr/bin/tar"),
    ];
    if tools.len() != EXPECTED.len() {
        return Err(RecipeError::Invalid(
            "Ubuntu builder must pin each required tool exactly once".to_owned(),
        ));
    }
    for (name, path) in EXPECTED {
        let mut matching = tools.iter().filter(|tool| tool.name == name);
        let tool = matching.next().ok_or_else(|| {
            RecipeError::Invalid(format!("Ubuntu builder is missing the {name} tool pin"))
        })?;
        validate_absolute_path(&tool.path)?;
        validate_optional_sha256(&tool.sha256)?;
        if matching.next().is_some()
            || tool.path != path
            || (!tool.version.is_empty() && tool.version.trim() != tool.version)
        {
            return Err(RecipeError::Invalid(format!(
                "Ubuntu builder {name} tool identity is invalid"
            )));
        }
    }
    Ok(())
}

fn valid_limits(cpu: u32, memory_mib: u32, pids: u32, workspace_bytes: u64) -> bool {
    cpu == 4 && memory_mib == 8192 && pids == 2048 && workspace_bytes == 68_719_476_736
}

fn matches_workspace_input_hash(recipe: &RecipeFile, relative: &str, expected: &str) -> bool {
    recipe
        .resolve_workspace_input(relative)
        .is_ok_and(|path| matches_path_hash(&path, expected))
}

fn matches_path_hash(path: &Path, expected: &str) -> bool {
    is_sha256(expected)
        && fs::symlink_metadata(path)
            .is_ok_and(|metadata| !metadata.file_type().is_symlink() && metadata.is_file())
        && fs::read(path).is_ok_and(|bytes| sha256_hex(&bytes) == expected)
}

fn sha256_file_at_absolute_path(path: &str) -> Option<String> {
    let path = Path::new(path);
    let metadata = fs::symlink_metadata(path).ok()?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return None;
    }
    fs::read(path).ok().map(|bytes| sha256_hex(&bytes))
}

pub(crate) fn source_tree_sha256_at_workspace_input(
    recipe: &RecipeFile,
    relative: &str,
) -> Option<String> {
    let root = recipe.resolve_workspace_input(relative).ok()?;
    let mut paths = Vec::new();
    collect_source_files(&root, &root, &mut paths).ok()?;
    paths.sort_by(|left, right| left.0.cmp(&right.0));
    let mut digest = Sha256::new();
    for (relative, path) in paths {
        let bytes = fs::read(path).ok()?;
        let path_bytes = relative.as_bytes();
        digest.update(u64::try_from(path_bytes.len()).ok()?.to_be_bytes());
        digest.update(path_bytes);
        digest.update(u64::try_from(bytes.len()).ok()?.to_be_bytes());
        digest.update(bytes);
    }
    let digest = digest.finalize();
    Some(hex_bytes(&digest))
}

fn collect_source_files(
    root: &Path,
    directory: &Path,
    files: &mut Vec<(String, PathBuf)>,
) -> std::io::Result<()> {
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() {
            return Err(std::io::Error::other("source tree contains a symlink"));
        }
        if metadata.is_dir() {
            collect_source_files(root, &path, files)?;
        } else if metadata.is_file() {
            let relative = path
                .strip_prefix(root)
                .map_err(|_| std::io::Error::other("source path escaped source root"))?
                .to_string_lossy()
                .replace('\\', "/");
            files.push((relative, path));
        } else {
            return Err(std::io::Error::other(
                "source tree contains a non-regular file",
            ));
        }
    }
    Ok(())
}

fn valid_fingerprint(value: &str) -> bool {
    [40, 64].contains(&value.len()) && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn validate_fingerprint(value: &str) -> Result<(), RecipeError> {
    if !valid_fingerprint(value) {
        return Err(RecipeError::Invalid(
            "OpenPGP fingerprints must contain 40 or 64 hexadecimal digits".to_owned(),
        ));
    }
    Ok(())
}

fn validate_git_object(value: &str) -> Result<(), RecipeError> {
    if value.len() != 40 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(RecipeError::Invalid(
            "annotated tag object must use a full 40-digit Git object ID".to_owned(),
        ));
    }
    Ok(())
}

fn validate_absolute_path(value: &str) -> Result<(), RecipeError> {
    let path = Path::new(value);
    if !path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::RootDir | Component::Normal(_)))
    {
        return Err(RecipeError::Invalid(
            "tool paths must be absolute and contain no traversal".to_owned(),
        ));
    }
    Ok(())
}

fn validate_oci_digest(value: &str) -> Result<(), RecipeError> {
    let hash = value.strip_prefix("sha256:").ok_or_else(|| {
        RecipeError::Invalid("OCI content identities must use sha256 digests".to_owned())
    })?;
    if !is_sha256(hash) {
        return Err(RecipeError::Invalid(
            "OCI sha256 digest must contain 64 hexadecimal digits".to_owned(),
        ));
    }
    Ok(())
}

fn validate_optional_oci_digest(value: &str) -> Result<(), RecipeError> {
    if !value.is_empty() {
        validate_oci_digest(value)?;
    }
    Ok(())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn is_https_url(value: &str) -> bool {
    reqwest::Url::parse(value).is_ok_and(|url| {
        url.scheme() == "https"
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && url.fragment().is_none()
    })
}

fn validate_relative_path(value: &str) -> Result<(), RecipeError> {
    let path = Path::new(value);
    if value.is_empty()
        || path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(RecipeError::Invalid(
            "recipe file paths must be nonempty relative paths without traversal".to_owned(),
        ));
    }
    Ok(())
}

fn reject_symlink_components(root: &Path, relative: &str) -> Result<(), RecipeError> {
    let mut current = root.to_path_buf();
    for component in Path::new(relative).components() {
        let Component::Normal(part) = component else {
            return Err(RecipeError::Invalid(
                "recipe path contains a non-normal component".to_owned(),
            ));
        };
        current.push(part);
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(RecipeError::Invalid(format!(
                    "recipe path contains a symlink: {}",
                    current.display()
                )));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(error) => return Err(RecipeError::Io(error)),
        }
    }
    Ok(())
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    hex_bytes(&digest)
}

fn hex_bytes(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use fmt::Write as _;
        write!(&mut text, "{byte:02x}").expect("writing into a String cannot fail");
    }
    text
}

#[cfg(test)]
mod tests {
    use super::{RecipeFile, RecipeGaps, source_tree_sha256_at_workspace_input};
    use std::error::Error;
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::time::{SystemTime, UNIX_EPOCH};

    const RECIPE: &str = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../images/terrabase-noble-podman-v1/recipe.toml"
    );

    #[test]
    fn candidate_recipe_is_bounded_and_remains_unqualified() {
        let recipe = RecipeFile::parse(
            Path::new(RECIPE),
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml").to_vec(),
        )
        .expect("candidate recipe is valid");
        assert_eq!(recipe.recipe().identity, "terrabase-noble-podman-v1");
        assert_eq!(recipe.recipe().schema_version, 2);
        assert_eq!(recipe.recipe().recipe_version, "2.0.0");
        assert_ne!(
            recipe.recipe().rust_cli_builder.base_image_digest,
            recipe.recipe().ubuntu_build_builder.base_image_digest
        );
        assert_eq!(
            recipe.recipe().filesystem.size_bytes,
            16 * 1024 * 1024 * 1024
        );
        assert_eq!(recipe.recipe().filesystem.reserved_percent, 0);
        assert_eq!(recipe.filesystem_uuid().len(), 36);
        assert!(!recipe.unresolved_inputs().is_empty());
    }

    #[test]
    fn historical_single_builder_recipe_is_not_accepted_as_v2() {
        let error = RecipeFile::parse(
            Path::new(RECIPE),
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe-v1.0.0.toml").to_vec(),
        )
        .expect_err("schema 1 has the ambiguous builder role");
        assert!(
            error
                .to_string()
                .contains("unsupported recipe schema or version")
        );
    }

    #[test]
    fn recipe_uuid_is_deterministic_version_eight_and_changes_with_bytes() {
        let first = RecipeFile::parse(
            Path::new(RECIPE),
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml").to_vec(),
        )
        .expect("candidate recipe parses");
        let second = RecipeFile::parse(
            Path::new(RECIPE),
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml").to_vec(),
        )
        .expect("same recipe parses");
        assert_eq!(first.filesystem_uuid(), second.filesystem_uuid());
        let uuid = first.filesystem_uuid().into_bytes();
        assert_eq!(uuid[14], b'8');
        assert!(matches!(uuid[19], b'8'..=b'b'));
        assert_eq!(first.sha256().len(), 64);
    }

    #[test]
    fn unresolved_inputs_are_reported_without_claiming_build_readiness() {
        let recipe = RecipeFile::parse(
            Path::new(RECIPE),
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml").to_vec(),
        )
        .expect("candidate recipe parses");
        let gaps: RecipeGaps = recipe.unresolved_inputs();
        assert!(
            gaps.as_slice()
                .iter()
                .any(|gap| gap.contains("package lock"))
        );
        assert!(gaps.as_slice().iter().any(|gap| gap.contains("keyring")));
        assert!(
            gaps.as_slice()
                .iter()
                .any(|gap| gap.contains("derived Ubuntu builder"))
        );
    }

    #[test]
    fn real_candidate_recipe_resolves_pinned_workspace_inputs() -> Result<(), Box<dyn Error>> {
        let (directory, recipe) = temporary_workspace_recipe()?;
        let pin = &recipe.recipe().rust_cli_builder;
        let workspace = fs::canonicalize(&directory.0)?;

        assert!(
            recipe
                .resolve_recipe_path(&pin.workspace_manifest_path)
                .is_err()
        );
        assert_eq!(
            recipe.resolve_workspace_input(&pin.workspace_manifest_path)?,
            workspace.join("Cargo.toml")
        );
        assert_eq!(
            recipe.resolve_workspace_input(&pin.cargo_lock_path)?,
            workspace.join("Cargo.lock")
        );
        assert_eq!(
            recipe.resolve_workspace_input(&pin.rust_toolchain_path)?,
            workspace.join("rust-toolchain.toml")
        );
        assert_eq!(
            recipe.resolve_workspace_input(&pin.source_root)?,
            workspace.join("crates/fc-image-builder")
        );
        assert!(source_tree_sha256_at_workspace_input(&recipe, &pin.source_root).is_some());
        Ok(())
    }

    #[test]
    fn workspace_resolver_rejects_unpinned_paths_wrong_types_and_symlinks()
    -> Result<(), Box<dyn Error>> {
        let (directory, recipe) = temporary_workspace_recipe()?;
        assert!(
            recipe
                .resolve_workspace_input("../../../../etc/passwd")
                .is_err()
        );

        let lock_path = directory.0.join("Cargo.lock");
        fs::remove_file(&lock_path)?;
        fs::create_dir(&lock_path)?;
        assert!(recipe.resolve_workspace_input("../../Cargo.lock").is_err());

        #[cfg(unix)]
        {
            use std::os::unix::fs::symlink;

            let workspace_crates = directory.0.join("crates");
            fs::remove_dir_all(&workspace_crates)?;
            let outside = directory.0.join("outside");
            fs::create_dir_all(outside.join("fc-image-builder"))?;
            symlink(&outside, &workspace_crates)?;
            assert!(
                recipe
                    .resolve_workspace_input("../../crates/fc-image-builder")
                    .is_err()
            );
        }
        Ok(())
    }

    struct TemporaryWorkspace(PathBuf);

    impl Drop for TemporaryWorkspace {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn temporary_workspace_recipe() -> Result<(TemporaryWorkspace, RecipeFile), Box<dyn Error>> {
        let nonce = SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos();
        let root = std::env::temp_dir().join(format!(
            "fc-image-builder-workspace-input-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&root)?;
        let directory = TemporaryWorkspace(root);
        let root = &directory.0;
        let recipe_directory = root.join("images/terrabase-noble-podman-v1");
        fs::create_dir_all(&recipe_directory)?;
        fs::create_dir_all(root.join("crates/fc-image-builder"))?;
        fs::write(root.join("Cargo.toml"), b"[workspace]\n")?;
        fs::write(root.join("Cargo.lock"), b"version = 4\n")?;
        fs::write(root.join("rust-toolchain.toml"), b"[toolchain]\n")?;
        let recipe_path = recipe_directory.join("recipe.toml");
        fs::write(
            &recipe_path,
            include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml"),
        )?;
        let recipe = RecipeFile::read(&recipe_path)?;
        Ok((directory, recipe))
    }
}
