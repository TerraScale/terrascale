use std::collections::BTreeSet;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

const CANDIDATE_ID: &str = "terrabase-noble-podman-v1";
const TARBALL_CANDIDATE_ID: &str = "terrabase-noble-podman-v2";
const CANDIDATE_RECIPE_VERSION: &str = "2.0.0";
const KERNEL_COMMIT: &str = "069c93d2b7bddda87c472453c38c450faf86f999";
const KERNEL_TAG: &str = "microvm-kernel-6.1.155-13.282.amzn2023";
const KERNEL_EPOCH: u64 = 1_760_348_006;
const FIRECRACKER_COMMIT: &str = "f82c0bd0f0a74015642a0d452880f3ad10147b14";
const KERNEL_CONFIG_URL: &str = "https://raw.githubusercontent.com/firecracker-microvm/firecracker/v1.15.1/resources/guest_configs/microvm-kernel-ci-x86_64-6.1.config";
const KERNEL_CONFIG_SHA: &str = "391d8d498f89e56b391be5ea4a7501ebc44e6d094331ecaabdefed1da6b28a96";
const KERNEL_POLICY_URL: &str = "https://raw.githubusercontent.com/firecracker-microvm/firecracker/v1.15.1/docs/kernel-policy.md";
const KERNEL_POLICY_SHA: &str = "f6d05719c129d8370b581ddc838de2bd113d1f35127f4da4236853ca9aa6f2fb";
const UBUNTU_BASE_SHA: &str = "e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86";
const UBUNTU_SIGNER: &str = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";
const SNAPSHOT: &str = "20260910T164400Z";
const KERNEL_URL: &str = "https://github.com/amazonlinux/linux.git";
const UBUNTU_URL: &str = "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.5-base-amd64.tar.gz";
const UBUNTU_SHA256SUMS_URL: &str =
    "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/SHA256SUMS";
const UBUNTU_SIGNATURE_URL: &str =
    "https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/SHA256SUMS.gpg";
const APT_SNAPSHOT_URL: &str = "https://snapshot.ubuntu.com/ubuntu/20260910T164400Z/";
const APT_SIGNER: &str = "F6ECB3762474EDA9D21B7022871920D1991BC93C";
const BUILDER_BASE_IMAGE: &str = "docker.io/library/rust:1.98.1-bookworm";
const BUILDER_BASE_INDEX: &str =
    "sha256:93ce27a88655056a51dbdd8f5f2d7ddc071c7b0070fb288a37b5a285fc83971e";
const BUSYBOX_REFERENCE: &str = "docker.io/library/busybox";
const BUILDER_BASE: &str =
    "sha256:c49256cbe5ea0188bc658a689500d70c41eb51f009a7a7be209caf60a944f3ec";
const UBUNTU_BUILDER_BASE_IMAGE: &str = "docker.io/library/ubuntu:24.04";
const UBUNTU_BUILDER_BASE_INDEX: &str =
    "sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3";
const UBUNTU_BUILDER_BASE: &str =
    "sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4";
const HOST_PODMAN_SHA256: &str = "402d507d68ed889dec999e688bd242b5ddb634b3bdc9f03ef9243e66baa367fc";
const BUSYBOX_IMAGE: &str =
    "sha256:dc88b80842580654294472735332ec7c7789a4010ec9a68b69f9b88c19abede6";
const NOBLE_UPDATES_INRELEASE: &str =
    "9f03de549c67af423447b5204dcd46298d23f69fe22bf30c76e9ee01548d0246";
const NOBLE_UPDATES_INDEX: &str =
    "cf06d0b20daa7eea4d4a4f4abc0fd8b29aed8389367681c6f6653c5068155265";
const PODMAN_DEB: &str = "e5c1c37e387ed14c352a744a75fbb79fb2f82573ca7bf36886e3b7333fc9ef1a";
const TARBALL_KERNEL_SHA: &str = "4d6fba95c2244b08a7b4144a4d38b9be4fb31abb5e7682ae40bb5cb11374cfe0";
const TARBALL_SIGNATURE_SHA: &str =
    "7e897372a7e0b0400176ea4065a33f4417119398bdaf9baa48a2e1030812ee6d";
const TARBALL_CONFIG_SHA: &str = "ba22401a0c7292a4c024ebcd10a562d4a1f1bfd2faed671406d3b159c0cf5215";
const TARBALL_POLICY_SHA: &str = "cdc062c6cfe5766acbddaff3f200dfe0a50b6edd4c75b0786f573bb2a2f1d68e";
const TARBALL_SIGNER: &str = "647F28654894E3BD457199BE38DBBDC86092693E";
const TARBALL_FIRECRACKER_COMMIT: &str = "95f868c8e345b1cc8faccd1a3c910b4989dc3f58";
const FIRECRACKER_ARCHIVE_SHA: &str =
    "06094a1108ae9e82aa4c23a775aa92758f53f1175d422270d9d6162cb9ade558";
const FIRECRACKER_ARCHIVE_SIZE: u64 = 7_464_385;
const FIRECRACKER_EXECUTABLE_SHA: &str =
    "99ad0f5cd0514a88aad0e9ae8cfdb3cc3b4ab9d190e1194602406c786b5de7a5";
const FIRECRACKER_EXECUTABLE_SIZE: u64 = 3_676_680;
const FIRECRACKER_RELEASE_URL: &str = "https://github.com/firecracker-microvm/firecracker/releases/download/v1.17.0/firecracker-v1.17.0-x86_64.tgz";
const FIRECRACKER_MEMBER: &str = "release-v1.17.0-x86_64/firecracker-v1.17.0-x86_64";

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ImageProfile {
    pub manifest_path: PathBuf,
    pub manifest_sha256: String,
    pub identity: String,
    pub recipe_version: String,
    pub recipe_sha256: String,
    pub qualification_status: String,
    pub reproducibility_status: String,
    pub kernel_path: PathBuf,
    pub kernel_sha256: String,
    pub kernel_release: String,
    pub rootfs_path: PathBuf,
    pub rootfs_sha256: String,
    pub rootfs_release: String,
    pub ubuntu_signer: String,
    pub apt_snapshot: String,
    pub package_lock_sha256: String,
    pub builder_derived_digest: String,
    pub firecracker_version: String,
    pub firecracker_sha256: Option<String>,
}

impl ImageProfile {
    #[expect(
        clippy::too_many_lines,
        reason = "one profile load validates the complete signed-image contract before returning paths"
    )]
    pub fn load(path: &Path, benchmark_repo: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        if !fs::symlink_metadata(path)?.file_type().is_file() {
            return Err("image profile manifest must be a regular non-symlink file".into());
        }
        let manifest_path = fs::canonicalize(path)?;
        let bytes = fs::read(&manifest_path)?;
        let value: Value = serde_json::from_slice(&bytes)?;
        if value["schema_version"].as_u64() != Some(2) {
            return Err("unsupported image profile schema version".into());
        }
        let identity = field(&value, "/recipe/identity")?;
        let recipe_version = field(&value, "/recipe/version")?;
        let recipe_sha256 = digest(&value, "/recipe/sha256")?;
        let tarball_candidate = identity == TARBALL_CANDIDATE_ID;
        if ![CANDIDATE_ID, TARBALL_CANDIDATE_ID].contains(&identity)
            || recipe_version != CANDIDATE_RECIPE_VERSION
        {
            return Err("image profile identity or recipe version is unsupported".into());
        }
        let recipe_path = benchmark_repo
            .join("firecracker-harness/images")
            .join(identity)
            .join("recipe.toml");
        if !fs::symlink_metadata(&recipe_path)?.file_type().is_file() {
            return Err("image profile recipe must be a regular non-symlink file".into());
        }
        let recipe_bytes = fs::read(&recipe_path)?;
        if sha256_bytes(&recipe_bytes) != recipe_sha256 {
            return Err("image profile recipe SHA-256 differs from repository recipe".into());
        }
        let recipe: toml::Value = toml::from_str(std::str::from_utf8(&recipe_bytes)?)?;
        validate_recipe_receipts(&recipe, &value, identity)?;
        let qualification_status = field(&value, "/qualification_status")?;
        if qualification_status != "verified-candidate" {
            return Err("image profile has not passed source/package verification".into());
        }
        if value["service_images_qualified"].as_bool() != Some(false) {
            return Err("image profile service qualification field is invalid".into());
        }
        let reproducibility_status = field(&value, "/reproducibility_status")?;
        if ![
            "single-build-unqualified",
            "identical-two-builds",
            "controlled-nondeterminism",
        ]
        .contains(&reproducibility_status)
        {
            return Err("invalid image reproducibility status".into());
        }
        if value["verified_at_unix_ms"]
            .as_u64()
            .is_none_or(|time| time == 0)
        {
            return Err("image profile lacks a verification timestamp".into());
        }
        validate_reproducibility(&value, reproducibility_status)?;
        revision(field(&value, "/bench_source/head")?)?;
        if value["bench_source"]["dirty"].as_bool().is_none() {
            return Err("image profile lacks source dirty-state provenance".into());
        }
        if value["bench_source"]["dirty"] == true {
            let _ = digest(&value, "/bench_source/diff_sha256")?;
        }
        if value["bench_source"]["untracked_files"]
            .as_array()
            .is_none()
        {
            return Err("image profile lacks untracked source inventory".into());
        }
        for source in value["bench_source"]["untracked_files"]
            .as_array()
            .ok_or("untracked source inventory is missing")?
        {
            relative_path(source["path"].as_str().unwrap_or_default())?;
            valid_digest(source["sha256"].as_str().unwrap_or_default())?;
        }
        if digest(&value, "/rootfs/source_sha256")? != UBUNTU_BASE_SHA
            || field(&value, "/rootfs/source_url")? != UBUNTU_URL
            || field(&value, "/rootfs/sha256sums_url")? != UBUNTU_SHA256SUMS_URL
            || field(&value, "/rootfs/signature_url")? != UBUNTU_SIGNATURE_URL
            || field(&value, "/rootfs/signature/signer_primary_fingerprint")? != UBUNTU_SIGNER
            || field(&value, "/guest_apt/snapshot_timestamp")? != SNAPSHOT
            || field(&value, "/guest_apt/snapshot_url")? != APT_SNAPSHOT_URL
            || field(&value, "/guest_apt/signer_primary_fingerprint")? != APT_SIGNER
        {
            return Err("image profile immutable source identity mismatch".into());
        }
        if tarball_candidate {
            validate_tarball_kernel(
                &value,
                &recipe,
                manifest_path
                    .parent()
                    .ok_or("image manifest has no parent")?,
            )?;
        } else {
            validate_git_kernel(&value)?;
            if !value["firecracker_release"].is_null() {
                return Err("v1 image profile unexpectedly includes a Firecracker release".into());
            }
        }
        let kernel_builder_lock = digest(&value, "/kernel/builder_toolchain_lock_sha256")?;
        let _ = digest(&value, "/rootfs/sha256sums_sha256")?;
        let _ = digest(&value, "/rootfs/signature/signature_sha256")?;
        let _ = digest(&value, "/rootfs/signature/keyring_sha256")?;
        let _ = digest(&value, "/rootfs/signature/verifier_sha256")?;
        let _ = nonempty(&value, "/rootfs/signature/verifier")?;
        if field(&value, "/rootfs/signature/status")? != "validsig"
            || field(&value, "/rootfs/signature/keyring_path")?
                != field(&value, "/builder_apt/initial_keyring_path")?
            || field(&value, "/rootfs/signature/keyring_sha256")?
                != field(&value, "/builder_apt/initial_keyring_sha256")?
            || field(&value, "/rootfs/signature/verifier")?
                != field(&value, "/builder_apt/initial_gpgv_path")?
            || field(&value, "/rootfs/signature/verifier_sha256")?
                != field(&value, "/builder_apt/initial_gpgv_sha256")?
        {
            return Err("Ubuntu Base signature was not verified".into());
        }
        let package_lock_sha256 = digest(&value, "/rootfs/package_lock_sha256")?;
        let parent = manifest_path
            .parent()
            .ok_or("image manifest has no parent")?;
        validate_apt_profile(&value["guest_apt"], "guest", parent)?;
        validate_apt_profile(&value["builder_apt"], "builder", parent)?;
        if field(&value, "/guest_apt/initial_keyring_sha256")?
            != field(&value, "/builder_apt/initial_keyring_sha256")?
            || field(&value, "/guest_apt/initial_keyring_path")?
                != field(&value, "/builder_apt/initial_keyring_path")?
            || field(&value, "/builder_apt/initial_keyring_path")?
                != field(&value, "/ubuntu_build_builder/initial_keyring_path")?
            || field(&value, "/guest_apt/initial_gpgv_path")?
                != field(&value, "/builder_apt/initial_gpgv_path")?
            || field(&value, "/guest_apt/initial_gpgv_sha256")?
                != field(&value, "/builder_apt/initial_gpgv_sha256")?
            || field(&value, "/builder_apt/initial_gpgv_path")?
                != field(&value, "/ubuntu_build_builder/initial_gpgv_path")?
            || field(&value, "/builder_apt/initial_gpgv_sha256")?
                != field(&value, "/ubuntu_build_builder/initial_gpgv_sha256")?
        {
            return Err("guest and builder apt trust roots differ".into());
        }
        if field(&value, "/rootfs/package_lock_path")?
            != field(&value, "/guest_apt/package_lock_path")?
            || package_lock_sha256 != digest(&value, "/guest_apt/package_lock_sha256")?
            || field(&value, "/builder_apt/package_lock_path")?
                != field(&value, "/ubuntu_build_builder/package_lock_path")?
            || digest(&value, "/builder_apt/package_lock_sha256")?
                != digest(&value, "/ubuntu_build_builder/package_lock_sha256")?
            || kernel_builder_lock != digest(&value, "/builder_apt/package_lock_sha256")?
        {
            return Err("image profile package locks do not match their image roles".into());
        }
        if field(&value, "/rust_cli_builder/base_image")? != BUILDER_BASE_IMAGE
            || digest(&value, "/rust_cli_builder/base_index_oci_digest")? != BUILDER_BASE_INDEX
            || digest(&value, "/rust_cli_builder/base_oci_digest")? != BUILDER_BASE
        {
            return Err("image profile Rust CLI base differs from the candidate lock".into());
        }
        if field(&value, "/ubuntu_build_builder/base_image")? != UBUNTU_BUILDER_BASE_IMAGE
            || digest(&value, "/ubuntu_build_builder/base_index_oci_digest")?
                != UBUNTU_BUILDER_BASE_INDEX
            || digest(&value, "/ubuntu_build_builder/base_oci_digest")? != UBUNTU_BUILDER_BASE
        {
            return Err("image profile Ubuntu builder base differs from the candidate lock".into());
        }
        validate_rust_builder_profile(&value["rust_cli_builder"])?;
        validate_ubuntu_builder_profile(&value["ubuntu_build_builder"])?;
        validate_host_podman_profile(&value["host_podman"])?;
        let builder_derived_digest = digest(&value, "/ubuntu_build_builder/derived_oci_digest")?;
        if digest(&value, "/oci_smoke/image_digest")? != BUSYBOX_IMAGE {
            return Err("image profile OCI smoke digest differs from the candidate lock".into());
        }
        if field(&value, "/oci_smoke/image_reference")? != BUSYBOX_REFERENCE
            || field(&value, "/oci_smoke/platform")? != "linux/amd64"
        {
            return Err("image profile OCI smoke source differs from the candidate lock".into());
        }
        let _ = digest(&value, "/oci_smoke/archive_sha256")?;
        if value["rootfs"]["filesystem"]["e2fsck_clean"].as_bool() != Some(true)
            || value["rootfs"]["filesystem"]["e2fsck_exit_code"].as_i64() != Some(0)
            || value["rootfs"]["filesystem"]["size_bytes"].as_u64() != Some(16 * 1024 * 1024 * 1024)
            || value["rootfs"]["filesystem"]["block_size"].as_u64() != Some(4096)
            || value["rootfs"]["filesystem"]["inode_size"].as_u64() != Some(256)
            || value["rootfs"]["filesystem"]["reserved_percent"].as_u64() != Some(0)
            || field(&value, "/rootfs/filesystem/label")?
                != if tarball_candidate {
                    "TSNOBLE2"
                } else {
                    "TSNOBLE1"
                }
        {
            return Err("image profile ext4 settings or clean check differ from the recipe".into());
        }
        let _ = digest(&value, "/rootfs/filesystem/root_tree_inventory_sha256")?;
        let uuid = nonempty(&value, "/rootfs/filesystem/uuid")?;
        if uuid != derived_uuid(&recipe_sha256)?
            || field(&value, "/rootfs/filesystem/hash_seed")? != uuid
        {
            return Err("image profile ext4 UUID or hash seed is invalid".into());
        }
        let packages = value["guest_apt"]["packages"]
            .as_array()
            .ok_or("apt packages are missing")?;
        for (name, version) in [
            ("podman", "4.9.3+ds1-1ubuntu0.2"),
            ("crun", "1.14.1-1"),
            ("conmon", "2.1.10+ds1-1build2"),
        ] {
            if !packages
                .iter()
                .any(|package| package["name"] == name && package["version"] == version)
            {
                return Err(format!("image profile lacks locked {name} {version}").into());
            }
        }
        if !packages
            .iter()
            .any(|package| package["name"] == "podman" && package["deb_sha256"] == PODMAN_DEB)
        {
            return Err(
                "image profile Podman package digest differs from the candidate lock".into(),
            );
        }
        let inventory_sha = digest(&value, "/rootfs/filesystem/root_tree_inventory_sha256")?;
        verify_receipt_file(
            parent,
            field(&value, "/rootfs/filesystem/root_tree_inventory_path")?,
            &inventory_sha,
        )?;
        nonempty_array(&value, "/commands")?;
        for command in value["commands"].as_array().ok_or("commands are missing")? {
            if command["status_code"].as_i64() != Some(0) {
                return Err("image profile contains a failed build command".into());
            }
            let _ = nonempty(command, "/program")?;
            let _ = digest(command, "/program_sha256")?;
            for name in ["stdout", "stderr"] {
                let path = field(command, &format!("/{name}_path"))?;
                let sha = digest(command, &format!("/{name}_sha256"))?;
                verify_receipt_file(parent, path, &sha)?;
            }
        }
        if reproducibility_status != "identical-two-builds"
            && value["limitations"].as_array().is_none_or(Vec::is_empty)
        {
            return Err("unqualified reproducibility requires explicit limitations".into());
        }
        let (kernel_path, kernel_sha256) = artifact(&value, "/kernel/output", parent)?;
        let (rootfs_path, rootfs_sha256) = artifact(&value, "/rootfs/output", parent)?;
        let firecracker_sha256 = if tarball_candidate {
            Some(validate_firecracker_release(&value, &recipe, parent)?)
        } else {
            None
        };
        Ok(Self {
            manifest_path,
            manifest_sha256: sha256_bytes(&bytes),
            identity: identity.to_owned(),
            recipe_version: recipe_version.to_owned(),
            recipe_sha256: recipe_sha256.clone(),
            qualification_status: qualification_status.to_owned(),
            reproducibility_status: reproducibility_status.to_owned(),
            kernel_path,
            kernel_sha256,
            kernel_release: nonempty(&value, "/kernel/release")?.to_owned(),
            rootfs_path,
            rootfs_sha256,
            rootfs_release: nonempty(&value, "/rootfs/release")?.to_owned(),
            ubuntu_signer: UBUNTU_SIGNER.to_owned(),
            apt_snapshot: SNAPSHOT.to_owned(),
            package_lock_sha256,
            builder_derived_digest,
            firecracker_version: if tarball_candidate {
                "1.17.0"
            } else {
                "1.15.1"
            }
            .to_owned(),
            firecracker_sha256,
        })
    }

    pub fn source_provenance(&self) -> String {
        format!(
            "verified image profile {} recipe {} sha256={} manifest sha256={} Ubuntu signer {} apt snapshot {}",
            self.identity,
            self.recipe_version,
            self.recipe_sha256,
            self.manifest_sha256,
            self.ubuntu_signer,
            self.apt_snapshot
        )
    }
}

fn validate_git_kernel(value: &Value) -> Result<(), Box<dyn std::error::Error>> {
    if field(value, "/kernel/source_revision")? != KERNEL_COMMIT
        || field(value, "/kernel/source_url")? != KERNEL_URL
        || field(value, "/kernel/annotated_tag")? != KERNEL_TAG
        || field(value, "/kernel/firecracker_version")? != "1.15.1"
        || field(value, "/kernel/firecracker_commit")? != FIRECRACKER_COMMIT
        || value["kernel"]["source_date_epoch"].as_u64() != Some(KERNEL_EPOCH)
        || field(value, "/kernel/config_url")? != KERNEL_CONFIG_URL
        || digest(value, "/kernel/config_sha256")? != KERNEL_CONFIG_SHA
        || field(value, "/kernel/kernel_policy_url")? != KERNEL_POLICY_URL
        || digest(value, "/kernel/kernel_policy_sha256")? != KERNEL_POLICY_SHA
    {
        return Err("historical image profile kernel identity mismatch".into());
    }
    revision(field(value, "/kernel/tag_object")?)?;
    Ok(())
}

fn validate_tarball_kernel(
    value: &Value,
    recipe: &toml::Value,
    parent: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let kernel = &recipe["kernel"];
    let matching = [
        ("source_version", "/kernel/source_revision"),
        ("source_archive_url", "/kernel/source_url"),
        ("signing_key_url", "/kernel/signing_key_url"),
        ("signing_key_sha256", "/kernel/signing_key_sha256"),
        ("signing_fingerprint", "/kernel/signing_fingerprint"),
        ("firecracker_version", "/kernel/firecracker_version"),
        ("firecracker_commit", "/kernel/firecracker_commit"),
        ("firecracker_config_url", "/kernel/config_url"),
        ("firecracker_config_sha256", "/kernel/config_sha256"),
        ("kernel_policy_url", "/kernel/kernel_policy_url"),
        ("kernel_policy_sha256", "/kernel/kernel_policy_sha256"),
        ("custom_config_sha256", "/kernel/custom_config_sha256"),
        (
            "custom_config_diff_sha256",
            "/kernel/custom_config_diff_sha256",
        ),
    ];
    for (pin, receipt) in matching {
        if kernel[pin].as_str() != Some(field(value, receipt)?) {
            return Err(format!("v2 kernel receipt differs from recipe at {receipt}").into());
        }
    }
    if field(value, "/kernel/source_kind")? != "kernel.org-signed-tarball"
        || kernel["custom_config_review_status"].as_str() != Some("reviewed")
        || field(value, "/kernel/source_revision")? != "6.18.53"
        || field(value, "/kernel/signing_fingerprint")? != TARBALL_SIGNER
        || field(value, "/kernel/firecracker_version")? != "1.17.0"
        || field(value, "/kernel/firecracker_commit")? != TARBALL_FIRECRACKER_COMMIT
        || digest(value, "/kernel/config_sha256")? != TARBALL_CONFIG_SHA
        || digest(value, "/kernel/kernel_policy_sha256")? != TARBALL_POLICY_SHA
        || value["kernel"]["source_date_epoch"].as_u64() != Some(1_789_996_005)
        || !field(value, "/kernel/annotated_tag")?.is_empty()
        || !field(value, "/kernel/tag_object")?.is_empty()
        || !value["kernel"]["tag_signature"].is_null()
    {
        return Err("v2 signed kernel source identity mismatch".into());
    }
    let compressed = &value["kernel"]["source_archive_compressed"];
    let signature = &value["kernel"]["source_signature"];
    let gpgv = value["ubuntu_build_builder"]["tools"]
        .as_array()
        .ok_or("Ubuntu builder tool receipts are missing")?
        .iter()
        .find(|tool| tool["name"] == "gpgv")
        .ok_or("Ubuntu builder gpgv receipt is missing")?;
    if field(compressed, "/source_url")? != field(value, "/kernel/source_url")?
        || digest(compressed, "/sha256")? != TARBALL_KERNEL_SHA
        || field(&signature["artifact"], "/source_url")?
            != kernel["source_signature_url"]
                .as_str()
                .ok_or("recipe lacks source signature URL")?
        || digest(&signature["artifact"], "/sha256")? != TARBALL_SIGNATURE_SHA
        || field(&signature["verification"], "/status")? != "validsig"
        || field(&signature["verification"], "/signer_primary_fingerprint")? != TARBALL_SIGNER
        || digest(&signature["verification"], "/signature_sha256")? != TARBALL_SIGNATURE_SHA
        || digest(&signature["verification"], "/keyring_sha256")?
            != field(value, "/kernel/signing_key_sha256")?
        || field(&signature["verification"], "/keyring_path")?
            != field(value, "/kernel/signing_key_url")?
        || field(&signature["verification"], "/verifier")? != field(gpgv, "/path")?
        || digest(&signature["verification"], "/verifier_sha256")? != digest(gpgv, "/sha256")?
        || field(
            &value["kernel"]["source_archive_uncompressed"],
            "/source_url",
        )? != field(value, "/kernel/source_url")?
    {
        return Err("v2 signed kernel tarball receipt differs from source pins".into());
    }
    for source in [
        compressed,
        &signature["artifact"],
        &value["kernel"]["source_archive_uncompressed"],
    ] {
        let size = source["size_bytes"]
            .as_u64()
            .ok_or("kernel source receipt lacks size")?;
        if size == 0 {
            return Err("kernel source receipt has zero size".into());
        }
        let path = field(source, "/path")?;
        let sha = digest(source, "/sha256")?;
        verify_receipt_file(parent, path, &sha)?;
    }
    for (path_key, sha_key) in [
        ("resolved_config_path", "custom_config_sha256"),
        ("resolved_config_diff_path", "custom_config_diff_sha256"),
    ] {
        let path = nonempty(value, &format!("/kernel/{path_key}"))?;
        let sha = digest(value, &format!("/kernel/{sha_key}"))?;
        verify_receipt_file(parent, path, &sha)?;
    }
    Ok(())
}

fn validate_firecracker_release(
    value: &Value,
    recipe: &toml::Value,
    parent: &Path,
) -> Result<String, Box<dyn std::error::Error>> {
    let release = &value["firecracker_release"];
    let pin = &recipe["firecracker"];
    if pin["version"].as_str() != Some("1.17.0")
        || pin["executable_review_status"].as_str() != Some("reviewed")
        || pin["release_tag_object"].as_str() != Some("29d66eb25954dbe3608eb91c180a6079f54d2ee8")
        || pin["release_tag_signature_status"].as_str() != Some("unsigned")
        || pin["archive_url"].as_str() != Some(FIRECRACKER_RELEASE_URL)
        || pin["archive_size_bytes"].as_integer() != Some(7_464_385)
        || pin["archive_sha256"].as_str() != Some(FIRECRACKER_ARCHIVE_SHA)
        || pin["executable_member"].as_str() != Some(FIRECRACKER_MEMBER)
        || pin["executable_sha256"].as_str() != Some(FIRECRACKER_EXECUTABLE_SHA)
        || pin["archive_metadata_source"].as_str() != Some("github-release-assets-api")
        || field(release, "/archive_metadata_source")? != "github-release-assets-api"
        || field(release, "/executable_member")? != FIRECRACKER_MEMBER
    {
        return Err("v2 Firecracker release differs from reviewed recipe pins".into());
    }
    let archive = &release["archive"];
    let executable = &release["executable"];
    let download = &release["download"];
    if field(archive, "/source_url")? != FIRECRACKER_RELEASE_URL
        || field(archive, "/path")? != "firecracker-release-archive"
        || archive["size_bytes"].as_u64() != Some(FIRECRACKER_ARCHIVE_SIZE)
        || digest(archive, "/sha256")? != FIRECRACKER_ARCHIVE_SHA
        || field(executable, "/source_url")? != FIRECRACKER_RELEASE_URL
        || field(executable, "/path")? != "firecracker-release-executable"
        || executable["size_bytes"].as_u64() != Some(FIRECRACKER_EXECUTABLE_SIZE)
        || digest(executable, "/sha256")? != FIRECRACKER_EXECUTABLE_SHA
        || field(download, "/id")? != "firecracker-release-archive"
        || field(download, "/source_url")? != FIRECRACKER_RELEASE_URL
        || field(download, "/cache_path")? != "firecracker-release-archive"
        || download["size_bytes"].as_u64() != Some(FIRECRACKER_ARCHIVE_SIZE)
        || digest(download, "/sha256")? != FIRECRACKER_ARCHIVE_SHA
        || download["http_status"].as_u64() != Some(200)
    {
        return Err("v2 Firecracker release receipt differs from reviewed bytes".into());
    }
    let redirects = download["redirect_count"]
        .as_u64()
        .ok_or("release redirect count is missing")?;
    let final_url = field(download, "/final_url")?;
    if !((redirects == 0
        && final_url == FIRECRACKER_RELEASE_URL
        && field(download, "/origin")? == "https://github.com")
        || (redirects == 1
            && final_url.starts_with("https://release-assets.githubusercontent.com/")
            && !final_url.contains('#')
            && field(download, "/origin")? == "https://release-assets.githubusercontent.com"))
    {
        return Err("v2 Firecracker release redirect evidence is invalid".into());
    }
    for source in [archive, executable] {
        verify_receipt_file(parent, field(source, "/path")?, field(source, "/sha256")?)?;
    }
    Ok(FIRECRACKER_EXECUTABLE_SHA.to_owned())
}

fn validate_recipe_receipts(
    recipe: &toml::Value,
    manifest: &Value,
    identity: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    if recipe["schema_version"].as_integer() != Some(2)
        || recipe["identity"].as_str() != Some(identity)
        || recipe["recipe_version"].as_str() != Some(CANDIDATE_RECIPE_VERSION)
    {
        return Err("image profile repository recipe is not the v2 candidate".into());
    }
    for (recipe_path, manifest_path) in [
        (
            &["guest_apt", "packages_lock_sha256"][..],
            "/guest_apt/package_lock_sha256",
        ),
        (
            &["guest_apt", "initial_keyring_sha256"][..],
            "/guest_apt/initial_keyring_sha256",
        ),
        (
            &["guest_apt", "initial_keyring_path"][..],
            "/guest_apt/initial_keyring_path",
        ),
        (
            &["ubuntu_build_builder", "packages_lock_sha256"][..],
            "/builder_apt/package_lock_sha256",
        ),
        (
            &["ubuntu_build_builder", "initial_keyring_sha256"][..],
            "/builder_apt/initial_keyring_sha256",
        ),
        (
            &["ubuntu_build_builder", "initial_keyring_path"][..],
            "/builder_apt/initial_keyring_path",
        ),
        (
            &["ubuntu_build_builder", "initial_gpgv_path"][..],
            "/builder_apt/initial_gpgv_path",
        ),
        (
            &["ubuntu_build_builder", "initial_gpgv_sha256"][..],
            "/builder_apt/initial_gpgv_sha256",
        ),
        (
            &["ubuntu_base", "initial_verifier_path"][..],
            "/rootfs/signature/verifier",
        ),
        (
            &["ubuntu_base", "initial_verifier_sha256"][..],
            "/rootfs/signature/verifier_sha256",
        ),
        (
            &["ubuntu_base", "initial_keyring_sha256"][..],
            "/rootfs/signature/keyring_sha256",
        ),
        (
            &["ubuntu_base", "initial_keyring_path"][..],
            "/rootfs/signature/keyring_path",
        ),
        (
            &["ubuntu_build_builder", "derived_image_digest"][..],
            "/ubuntu_build_builder/derived_oci_digest",
        ),
        (
            &["rust_cli_builder", "source_tree_sha256"][..],
            "/rust_cli_builder/source_tree_sha256",
        ),
        (
            &["rust_cli_builder", "cargo_lock_sha256"][..],
            "/rust_cli_builder/cargo_lock_sha256",
        ),
        (
            &["rust_cli_builder", "rust_toolchain_sha256"][..],
            "/rust_cli_builder/rust_toolchain_sha256",
        ),
        (
            &["rust_cli_builder", "output_executable_sha256"][..],
            "/rust_cli_builder/executable_sha256",
        ),
        (&["host_podman", "path"][..], "/host_podman/path"),
        (&["host_podman", "sha256"][..], "/host_podman/sha256"),
        (
            &["offline_smoke", "archive_sha256"][..],
            "/oci_smoke/archive_sha256",
        ),
    ] {
        let value = recipe_path
            .iter()
            .try_fold(recipe, |current, key| current.get(*key))
            .and_then(toml::Value::as_str)
            .ok_or("image profile recipe lacks a v2 pin")?;
        if value != field(manifest, manifest_path)? {
            return Err(
                format!("image profile receipt differs from recipe at {manifest_path}").into(),
            );
        }
    }
    validate_recipe_tools(recipe, manifest)?;
    Ok(())
}

fn validate_recipe_tools(
    recipe: &toml::Value,
    manifest: &Value,
) -> Result<(), Box<dyn std::error::Error>> {
    let pins = recipe["ubuntu_build_builder"]["tools"]
        .as_array()
        .ok_or("recipe lacks Ubuntu builder tool pins")?;
    let receipts = manifest["ubuntu_build_builder"]["tools"]
        .as_array()
        .ok_or("manifest lacks Ubuntu builder tool receipts")?;
    if pins.len() != receipts.len() {
        return Err("Ubuntu builder tool count differs from the recipe".into());
    }
    for pin in pins {
        let name = pin["name"].as_str().ok_or("recipe tool lacks a name")?;
        let receipt = receipts
            .iter()
            .find(|tool| tool["name"] == name)
            .ok_or("manifest lacks a recipe-pinned Ubuntu tool")?;
        for key in ["path", "version", "sha256"] {
            if pin[key].as_str() != receipt[key].as_str() {
                return Err(format!("Ubuntu builder tool {name} {key} differs from recipe").into());
            }
        }
    }
    Ok(())
}

fn validate_apt_profile(
    apt: &Value,
    role: &str,
    parent: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let trust_source = if role == "guest" {
        "pinned-ubuntu-build-builder-child"
    } else {
        "pinned-ubuntu-child"
    };
    if field(apt, "/role")? != role
        || field(apt, "/initial_keyring_source")? != trust_source
        || field(apt, "/initial_keyring_path")? != "/usr/share/keyrings/ubuntu-archive-keyring.gpg"
        || field(apt, "/initial_gpgv_path")? != "/usr/bin/gpgv"
        || field(apt, "/snapshot_url")? != APT_SNAPSHOT_URL
        || field(apt, "/snapshot_timestamp")? != SNAPSHOT
        || field(apt, "/signer_primary_fingerprint")? != APT_SIGNER
        || field(apt, "/architecture")? != "amd64"
        || field(apt, "/apt_version")? != "2.8.3"
        || apt["suites"] != serde_json::json!(["noble", "noble-updates", "noble-security"])
        || apt["components"] != serde_json::json!(["main", "universe"])
        || apt["allow_unauthenticated"] != false
        || apt["install_recommends"] != false
        || apt["install_suggests"] != false
        || apt["check_valid_until"] != true
        || apt["solver_exit_status"].as_i64() != Some(0)
    {
        return Err(format!("{role} apt receipt does not match the locked snapshot").into());
    }
    for key in [
        "apt_sha256",
        "keyring_sha256",
        "initial_keyring_sha256",
        "initial_gpgv_sha256",
        "settings_sha256",
        "solver_stdout_sha256",
        "solver_stderr_sha256",
        "package_lock_sha256",
    ] {
        let _ = digest(apt, &format!("/{key}"))?;
    }
    for (path_key, hash_key) in [
        ("settings_path", "settings_sha256"),
        ("package_lock_path", "package_lock_sha256"),
    ] {
        verify_receipt_file(
            parent,
            field(apt, &format!("/{path_key}"))?,
            field(apt, &format!("/{hash_key}"))?,
        )?;
    }
    let argv = apt["solver_command_argv"]
        .as_array()
        .ok_or("apt solver command is missing")?;
    if argv
        .first()
        .and_then(Value::as_str)
        .is_none_or(|program| !program.ends_with("apt-get"))
        || !argv.iter().any(|arg| arg == "-s" || arg == "--simulate")
    {
        return Err("apt solver receipt does not record a simulation".into());
    }
    nonempty_array(apt, "/indexes")?;
    nonempty_array(apt, "/packages")?;
    validate_apt_indexes(apt)?;
    validate_apt_packages(apt)?;
    Ok(())
}

fn validate_apt_indexes(apt: &Value) -> Result<(), Box<dyn std::error::Error>> {
    let mut seen = BTreeSet::new();
    for index in apt["indexes"].as_array().ok_or("apt indexes are missing")? {
        let suite = field(index, "/suite")?;
        let component = field(index, "/component")?;
        if field(index, "/architecture")? != "amd64"
            || !seen.insert((suite, component))
            || field(index, "/inrelease_sha256")?
                != candidate_inrelease_sha256(suite).unwrap_or_default()
            || field(index, "/packages_index_sha256")?
                != candidate_packages_sha256(suite, component).unwrap_or_default()
        {
            return Err("apt index differs from the signed candidate snapshot".into());
        }
        let base = format!("{APT_SNAPSHOT_URL}dists/{suite}/");
        if field(index, "/inrelease_url")? != format!("{base}InRelease")
            || field(index, "/packages_index_url")?
                != format!("{base}{component}/binary-amd64/Packages.xz")
        {
            return Err("apt index URL differs from the locked snapshot".into());
        }
        let signature = &index["signature"];
        if field(signature, "/status")? != "validsig"
            || field(signature, "/signer_primary_fingerprint")? != APT_SIGNER
            || index["gpgv_verified"] != true
            || field(index, "/trusted_key_source")? != field(apt, "/initial_keyring_source")?
            || field(index, "/trusted_key_sha256")? != field(apt, "/initial_keyring_sha256")?
            || field(signature, "/keyring_path")? != field(apt, "/initial_keyring_path")?
            || field(signature, "/keyring_sha256")? != field(apt, "/initial_keyring_sha256")?
            || field(signature, "/verifier")? != field(apt, "/initial_gpgv_path")?
            || field(signature, "/verifier_sha256")? != field(apt, "/initial_gpgv_sha256")?
            || field(signature, "/signature_sha256")? != field(index, "/inrelease_sha256")?
        {
            return Err("apt signature does not bind to the initial trust root".into());
        }
        let _ = digest(signature, "/verifier_sha256")?;
        let _ = nonempty(signature, "/verifier")?;
    }
    if seen.len() != 6 {
        return Err("apt receipt lacks six signed indexes".into());
    }
    Ok(())
}

fn validate_apt_packages(apt: &Value) -> Result<(), Box<dyn std::error::Error>> {
    let mut packages = BTreeSet::new();
    for package in apt["packages"]
        .as_array()
        .ok_or("apt packages are missing")?
    {
        let name = nonempty(package, "/name")?;
        let architecture = field(package, "/architecture")?;
        if !packages.insert((name, architecture))
            || !["amd64", "all"].contains(&architecture)
            || !["main", "universe"].contains(&field(package, "/component")?)
            || !["noble", "noble-updates", "noble-security"].contains(&field(package, "/suite")?)
            || package["size_bytes"].as_u64().is_none_or(|size| size == 0)
        {
            return Err("apt package identity is incomplete or duplicated".into());
        }
        let _ = nonempty(package, "/version")?;
        https_url(field(package, "/source_url")?)?;
        let _ = digest(package, "/deb_sha256")?;
    }
    Ok(())
}

fn validate_rust_builder_profile(builder: &Value) -> Result<(), Box<dyn std::error::Error>> {
    if field(builder, "/rust_toolchain_channel")? != "1.98.1"
        || field(builder, "/target")? != "x86_64-unknown-linux-musl"
        || field(builder, "/host_abi")? != "x86_64-linux-musl-static"
    {
        return Err("Rust CLI builder toolchain or host ABI differs from the lock".into());
    }
    let _ = nonempty(builder, "/rust_version")?;
    let _ = nonempty(builder, "/cargo_version")?;
    for key in [
        "source_tree_sha256",
        "cargo_lock_sha256",
        "rust_toolchain_sha256",
        "executable_sha256",
    ] {
        let _ = digest(builder, &format!("/{key}"))?;
    }
    for key in ["cargo_lock_path", "rust_toolchain_path", "executable_path"] {
        relative_path(field(builder, &format!("/{key}"))?)?;
    }
    Ok(())
}

fn validate_ubuntu_builder_profile(builder: &Value) -> Result<(), Box<dyn std::error::Error>> {
    let _ = digest(builder, "/derived_oci_digest")?;
    if field(builder, "/initial_keyring_path")? != "/usr/share/keyrings/ubuntu-archive-keyring.gpg"
    {
        return Err("Ubuntu builder keyring path differs from the pinned child".into());
    }
    if field(builder, "/initial_gpgv_path")? != "/usr/bin/gpgv" {
        return Err("Ubuntu builder gpgv path differs from the pinned child".into());
    }
    let _ = digest(builder, "/initial_gpgv_sha256")?;
    let _ = digest(builder, "/package_lock_sha256")?;
    relative_path(field(builder, "/package_lock_path")?)?;
    let argv = builder["build_argv"]
        .as_array()
        .ok_or("Ubuntu builder command is missing")?;
    if argv
        .first()
        .and_then(Value::as_str)
        .is_none_or(|program| !program.ends_with("podman"))
        || !argv.iter().any(|arg| arg == "build")
        || !argv.iter().any(|arg| arg == "--network=none")
    {
        return Err("Ubuntu builder did not record an offline Podman build".into());
    }
    let tools = builder["tools"]
        .as_array()
        .ok_or("Ubuntu builder tools are missing")?;
    let required = [
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
    let mut seen = BTreeSet::new();
    for tool in tools {
        let name = field(tool, "/name")?;
        if !seen.insert(name)
            || !required.contains(&name)
            || !Path::new(field(tool, "/path")?).is_absolute()
        {
            return Err("Ubuntu builder tool inventory is invalid".into());
        }
        let _ = nonempty(tool, "/version")?;
        let _ = digest(tool, "/sha256")?;
    }
    if seen.len() != required.len() {
        return Err("Ubuntu builder tool inventory is incomplete".into());
    }
    Ok(())
}

fn validate_host_podman_profile(host: &Value) -> Result<(), Box<dyn std::error::Error>> {
    if field(host, "/path")?
        != "/nix/store/mi7484cckkxv8hgiawq1cg89sxknc3bh-podman-5.8.6/bin/podman"
        || field(host, "/version")? != "5.8.6"
        || digest(host, "/sha256")? != HOST_PODMAN_SHA256
        || host["rootless"] != true
        || host["uid"].as_u64() != Some(1000)
        || host["gid"].as_u64() != Some(1000)
        || host["resource_limits"]["cpu_count"].as_u64() != Some(4)
        || host["resource_limits"]["memory_mib"].as_u64() != Some(8192)
        || host["resource_limits"]["pids_limit"].as_u64() != Some(2048)
        || host["resource_limits"]["workspace_limit_bytes"].as_u64() != Some(68_719_476_736)
    {
        return Err("host Podman identity or limits differ from the recipe".into());
    }
    Ok(())
}

fn field<'a>(value: &'a Value, pointer: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    value
        .pointer(pointer)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("image profile missing {pointer}").into())
}

fn nonempty<'a>(value: &'a Value, pointer: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    let result = field(value, pointer)?;
    if result.is_empty() {
        Err(format!("image profile field {pointer} is empty").into())
    } else {
        Ok(result)
    }
}

fn digest(value: &Value, pointer: &str) -> Result<String, Box<dyn std::error::Error>> {
    let result = field(value, pointer)?;
    valid_digest(result)?;
    Ok(result.to_owned())
}

fn valid_digest(value: &str) -> Result<(), Box<dyn std::error::Error>> {
    let hex = value.strip_prefix("sha256:").unwrap_or(value);
    if hex.len() != 64 || !hex.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("image profile contains an invalid SHA-256 digest".into());
    }
    Ok(())
}

fn revision(value: &str) -> Result<(), Box<dyn std::error::Error>> {
    if value.len() != 40 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("image profile contains an invalid Git revision".into());
    }
    Ok(())
}

fn https_url(value: &str) -> Result<(), Box<dyn std::error::Error>> {
    if !value.starts_with("https://") || value.len() <= "https://".len() {
        return Err("image profile contains an invalid HTTPS source URL".into());
    }
    Ok(())
}

fn relative_path(value: &str) -> Result<(), Box<dyn std::error::Error>> {
    if value.is_empty()
        || !Path::new(value)
            .components()
            .all(|part| matches!(part, Component::Normal(_)))
    {
        return Err("image profile contains an unsafe relative path".into());
    }
    Ok(())
}

fn validate_reproducibility(value: &Value, status: &str) -> Result<(), Box<dyn std::error::Error>> {
    let receipt = &value["reproducibility"];
    if status == "single-build-unqualified" {
        if !receipt.is_null() {
            return Err("single-build profile must not claim a comparison receipt".into());
        }
        return Ok(());
    }
    let first_run = nonempty(receipt, "/first_run_id")?;
    let second_run = nonempty(receipt, "/second_run_id")?;
    if first_run == second_run {
        return Err("repeat-build receipt must identify distinct runs".into());
    }
    for key in [
        "first_kernel_sha256",
        "second_kernel_sha256",
        "first_rootfs_sha256",
        "second_rootfs_sha256",
    ] {
        let _ = digest(receipt, &format!("/{key}"))?;
    }
    let differences = receipt["differences"]
        .as_array()
        .ok_or("repeat-build differences are missing")?;
    for difference in differences {
        if nonempty(difference, "/first_value")? == nonempty(difference, "/second_value")?
            || nonempty(difference, "/field")?.is_empty()
            || nonempty(difference, "/impact")?.is_empty()
        {
            return Err("repeat-build difference is invalid".into());
        }
    }
    let first_kernel = field(receipt, "/first_kernel_sha256")?;
    let second_kernel = field(receipt, "/second_kernel_sha256")?;
    let first_rootfs = field(receipt, "/first_rootfs_sha256")?;
    let second_rootfs = field(receipt, "/second_rootfs_sha256")?;
    if status == "identical-two-builds" {
        if first_kernel != second_kernel
            || first_rootfs != second_rootfs
            || !differences.is_empty()
            || second_kernel != field(value, "/kernel/output/sha256")?
            || second_rootfs != field(value, "/rootfs/output/sha256")?
        {
            return Err("identical-build status does not match recorded outputs".into());
        }
    } else if differences.is_empty()
        || (first_kernel == second_kernel && first_rootfs == second_rootfs)
    {
        return Err("controlled nondeterminism lacks differing outputs and metadata".into());
    }
    Ok(())
}

fn derived_uuid(recipe_sha256: &str) -> Result<String, Box<dyn std::error::Error>> {
    valid_digest(recipe_sha256)?;
    let hex = recipe_sha256
        .strip_prefix("sha256:")
        .unwrap_or(recipe_sha256);
    let mut bytes = [0_u8; 16];
    for (index, byte) in bytes.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&hex[index * 2..index * 2 + 2], 16)?;
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

fn candidate_inrelease_sha256(suite: &str) -> Option<&'static str> {
    match suite {
        "noble" => Some("cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0"),
        "noble-updates" => Some(NOBLE_UPDATES_INRELEASE),
        "noble-security" => {
            Some("7e344732ccaf448e1d98e70ea4d76c8d0b4d2d6e563888483c07b69690b6c165")
        }
        _ => None,
    }
}

fn candidate_packages_sha256(suite: &str, component: &str) -> Option<&'static str> {
    match (suite, component) {
        ("noble", "main") => {
            Some("2a6a199e1031a5c279cb346646d594993f35b1c03dd4a82aaa0323980dd92451")
        }
        ("noble", "universe") => {
            Some("ba9057fa1b91438cc8a1d26808d00c85389fe101d0c1496254df97236405599a")
        }
        ("noble-updates", "main") => {
            Some("fda21cfbb147b8445d3e37e60745ab8202d40e0f4fd7010d029c57fe1458708e")
        }
        ("noble-updates", "universe") => Some(NOBLE_UPDATES_INDEX),
        ("noble-security", "main") => {
            Some("b885088b1c8da2fec85d189005b63a9e642eb893a77069ef28f81d72a3d7a26e")
        }
        ("noble-security", "universe") => {
            Some("823c10e7329f3f43b3c50db9de0d7ae8333765b4e18f4707c3357e8467d2d8f9")
        }
        _ => None,
    }
}

fn verify_receipt_file(
    parent: &Path,
    relative: &str,
    expected_sha: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    relative_path(relative)?;
    let mut path = parent.to_path_buf();
    for part in Path::new(relative).components() {
        path.push(part.as_os_str());
        if fs::symlink_metadata(&path)?.file_type().is_symlink() {
            return Err("image receipt path contains a symlink".into());
        }
    }
    if !fs::symlink_metadata(&path)?.file_type().is_file() || sha256_file(&path)? != expected_sha {
        return Err("image receipt file is not regular or its SHA-256 differs".into());
    }
    Ok(())
}

fn nonempty_array(value: &Value, pointer: &str) -> Result<(), Box<dyn std::error::Error>> {
    if value
        .pointer(pointer)
        .and_then(Value::as_array)
        .is_none_or(Vec::is_empty)
    {
        Err(format!("image profile requires nonempty {pointer}").into())
    } else {
        Ok(())
    }
}

fn artifact(
    value: &Value,
    pointer: &str,
    parent: &Path,
) -> Result<(PathBuf, String), Box<dyn std::error::Error>> {
    let relative = Path::new(nonempty(value, &format!("{pointer}/path"))?);
    relative_path(
        relative
            .to_str()
            .ok_or("image artifact path is not UTF-8")?,
    )?;
    let expected_size = value
        .pointer(&format!("{pointer}/size_bytes"))
        .and_then(Value::as_u64)
        .ok_or("image artifact lacks size")?;
    let expected_sha = digest(value, &format!("{pointer}/sha256"))?;
    let mut path = parent.to_path_buf();
    for part in relative.components() {
        path.push(part.as_os_str());
        if fs::symlink_metadata(&path)?.file_type().is_symlink() {
            return Err("image artifact path contains a symlink".into());
        }
    }
    let metadata = fs::symlink_metadata(&path)?;
    if !metadata.file_type().is_file() || metadata.len() != expected_size {
        return Err("image artifact is not a regular file of the locked size".into());
    }
    if sha256_file(&path)? != expected_sha {
        return Err("image artifact SHA-256 does not match the profile".into());
    }
    Ok((path, expected_sha))
}

fn sha256_file(path: &Path) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hash = Sha256::new();
    let mut buf = vec![0_u8; 64 * 1024];
    loop {
        let size = file.read(&mut buf)?;
        if size == 0 {
            break;
        }
        hash.update(&buf[..size]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

fn sha256_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
