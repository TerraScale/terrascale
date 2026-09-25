use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::manifest::{
    AptIndexReceipt, AptPackageReceipt, AptReceipt, ArtifactOutput, BenchSourceReceipt,
    Ext4Receipt, FirecrackerReleaseReceipt, HostPodmanReceipt, ImageManifest, KernelReceipt,
    KernelSourceSignatureReceipt, OciSmokeReceipt, RecipeIdentityReceipt, ReproducibilityStatus,
    ResourceLimits, RootfsReceipt, RustCliBuilderReceipt, SignatureReceipt, SourceArtifactReceipt,
    SourceFileReceipt, UbuntuBuildBuilderReceipt, UbuntuToolReceipt,
};
use crate::package_lock::canonical_inventory_sha256;
use crate::workflow::{
    FetchReceipt, invalid, now_unix_ms, read_command_stdout, sha256_file,
    stage_derived_builder_archive, validate_qualified_inputs, verify_canonical_checksum,
    verify_derived_builder_archive,
};
use crate::{
    AptSolverReceipt, CandidateIndex, CandidatePackageLock, CommandReceipt, ContainerMount,
    NetworkMode, PackageIndexPin, PodmanExecutor, RecipeFile, RunDirectory, SignatureRequest,
    VerifiedLocalImage, verify_package_index,
};

const FETCH_RECEIPT_LIMIT: u64 = 64 * 1024 * 1024;

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct RootTreeEntry {
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

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct RootTreeInventory {
    schema_version: u32,
    entries: Vec<RootTreeEntry>,
    installed_packages: Vec<InstalledPackage>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
struct InstalledPackage {
    name: String,
    version: String,
    architecture: String,
}

/// Inputs for building an unqualified single-image candidate.
pub struct BuildRequest<'a> {
    /// Validated immutable build recipe.
    pub recipe: &'a RecipeFile,
    /// Verified guest package lock.
    pub guest: &'a CandidatePackageLock,
    /// Verified builder package lock.
    pub builder: &'a CandidatePackageLock,
    /// Derived builder image archive.
    pub derived_builder_archive: &'a Path,
    /// Receipt for the pinned CLI compiler build.
    pub cli_compile_receipt: &'a Path,
    /// Completed Fetch workflow output directory.
    pub input_dir: &'a Path,
    /// Parent directory for the new build run.
    pub output_root: &'a Path,
    /// Unique build run identifier.
    pub run_id: &'a str,
}

/// Build an unqualified single-image candidate from a completed verified fetch.
/// All guest-controlled scripts and source build commands run in the pinned,
/// rootless Noble builder with networking disabled.
///
/// # Errors
///
/// Returns an error before creating a run or invoking external tools unless
/// the recipe, separate locks, and every file in the Fetch receipt are exact.
pub fn build_qualified_image(request: BuildRequest<'_>) -> Result<PathBuf, Box<dyn Error>> {
    let validated = validate_build_request(&request)?;
    let output_root = canonical_regular_directory(request.output_root, "build output root")?;
    let run = RunDirectory::create(&output_root, request.run_id)?;
    build_image_in_run(request, validated, run)
}

struct ValidatedBuildInputs {
    archive_identity: crate::oci_archive::OciArchiveIdentity,
    input_root: PathBuf,
    fetch_receipt: FetchReceipt,
}

struct BuildContext<'a, 'run> {
    recipe: &'a RecipeFile,
    guest: &'a CandidatePackageLock,
    builder: &'a CandidatePackageLock,
    fetch_receipt: &'a FetchReceipt,
    input_paths: &'a BTreeMap<String, PathBuf>,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    run: &'a RunDirectory,
}

struct VerifiedKernelSourceSignature {
    receipt: SignatureReceipt,
    command: CommandReceipt,
}

struct KernelTarSignatureContext<'a, 'run> {
    recipe: &'a RecipeFile,
    receipt: &'a FetchReceipt,
    staged: &'a BTreeMap<String, PathBuf>,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    run: &'a RunDirectory,
    kernel_key: &'a Path,
    key_hash: &'a str,
    gpgv: &'a crate::recipe::BuilderToolPin,
}

struct BuildArtifacts {
    filesystem: Ext4Receipt,
    rootfs_release: String,
    firecracker_release: Option<FirecrackerReleaseReceipt>,
}

struct ImageManifestInput<'a, 'run> {
    context: &'a BuildContext<'a, 'run>,
    guest_indexes: Vec<AptIndexReceipt>,
    builder_indexes: Vec<AptIndexReceipt>,
    canonical_signature: SignatureReceipt,
    kernel_source_signature: SignatureReceipt,
    artifacts: BuildArtifacts,
    rust_cli_host_abi: String,
    commands: Vec<CommandReceipt>,
}

fn validate_build_request(
    request: &BuildRequest<'_>,
) -> Result<ValidatedBuildInputs, Box<dyn Error>> {
    validate_qualified_inputs(request.recipe, request.guest, request.builder)?;
    validate_pinned_lock_files(request.recipe, request.guest, request.builder)?;
    let archive_identity =
        verify_derived_builder_archive(request.recipe, request.derived_builder_archive)?;
    let executable = std::env::current_exe()?;
    crate::cli_compiler::verify_cli_compile_receipt(
        request.recipe,
        request.cli_compile_receipt,
        &executable,
    )?;
    let input_root = canonical_regular_directory(request.input_dir, "Fetch input directory")?;
    let fetch_receipt = read_fetch_receipt(&input_root)?;
    validate_fetch_receipt(
        request.recipe,
        request.guest,
        request.builder,
        &input_root,
        &archive_identity,
        &fetch_receipt,
    )?;
    Ok(ValidatedBuildInputs {
        archive_identity,
        input_root,
        fetch_receipt,
    })
}

fn build_image_in_run(
    request: BuildRequest<'_>,
    validated: ValidatedBuildInputs,
    run: RunDirectory,
) -> Result<PathBuf, Box<dyn Error>> {
    let BuildRequest {
        recipe,
        guest,
        builder,
        derived_builder_archive,
        ..
    } = request;
    let ValidatedBuildInputs {
        archive_identity,
        input_root,
        fetch_receipt,
    } = validated;
    let rust_cli_host_abi = write_rust_cli_build_inputs(recipe, &run)?;
    let (input_paths, podman, builder_image, keyring_path, mut commands) =
        prepare_build_environment(
            recipe,
            derived_builder_archive,
            &archive_identity,
            &input_root,
            &fetch_receipt,
            &run,
        )?;
    let verified_indexes = reverify_locked_indexes(&IndexVerificationContext {
        recipe,
        guest,
        builder,
        receipt: &fetch_receipt,
        staged: &input_paths,
        podman: &podman,
        builder_image: &builder_image,
        keyring_path: &keyring_path,
        run: &run,
    })?;
    let guest_indexes = verified_indexes.guest;
    let builder_indexes = verified_indexes.builder;
    let apt_commands = verified_indexes.commands;
    let (canonical_signature, canonical_signature_command) = reverify_canonical_base(
        recipe,
        &fetch_receipt,
        &input_paths,
        &podman,
        &builder_image,
        &keyring_path,
        &run,
    )?;
    commands.push(canonical_signature_command);

    let verified_kernel_source_signature = verify_build_kernel_signature(
        recipe,
        &fetch_receipt,
        &input_paths,
        &podman,
        &builder_image,
        &run,
    )?;
    commands.push(verified_kernel_source_signature.command.clone());
    let mut all_commands = commands;
    all_commands.extend(apt_commands);
    all_commands.extend(stage_fetch_command_logs(&input_root, &fetch_receipt, &run)?);

    let artifacts = build_image_artifacts(
        recipe,
        guest,
        &fetch_receipt,
        &input_paths,
        &podman,
        &builder_image,
        &run,
        &mut all_commands,
    )?;
    let context = BuildContext {
        recipe,
        guest,
        builder,
        fetch_receipt: &fetch_receipt,
        input_paths: &input_paths,
        podman: &podman,
        builder_image: &builder_image,
        run: &run,
    };
    write_image_manifest(ImageManifestInput {
        context: &context,
        guest_indexes,
        builder_indexes,
        canonical_signature,
        kernel_source_signature: verified_kernel_source_signature.receipt,
        artifacts,
        rust_cli_host_abi,
        commands: all_commands,
    })
}

fn prepare_build_environment<'a>(
    recipe: &'a RecipeFile,
    derived_builder_archive: &Path,
    archive_identity: &crate::oci_archive::OciArchiveIdentity,
    input_root: &Path,
    fetch_receipt: &FetchReceipt,
    run: &'a RunDirectory,
) -> Result<
    (
        BTreeMap<String, PathBuf>,
        PodmanExecutor<'a>,
        VerifiedLocalImage,
        PathBuf,
        Vec<CommandReceipt>,
    ),
    Box<dyn Error>,
> {
    let input_paths = stage_verified_inputs(input_root, fetch_receipt, run)?;
    let podman = PodmanExecutor::new(recipe, run)?;
    let (archive_path, staged_identity) =
        stage_derived_builder_archive(recipe, derived_builder_archive, archive_identity, run)?;
    let (builder_image, mut commands) =
        podman.load_verified_archive(&archive_path, &staged_identity, "build_derived_builder")?;
    let base_image = format!(
        "{}@{}",
        recipe.recipe().ubuntu_build_builder.base_image,
        recipe.recipe().ubuntu_build_builder.base_image_digest
    );
    let (keyring_path, keyring_commands) = podman.copy_from_image(
        "build_initial_keyring",
        &base_image,
        &recipe.recipe().ubuntu_base.initial_keyring_path,
        "build-initial-keyring.gpg",
    )?;
    commands.extend(keyring_commands);
    if !sha256_file(&keyring_path)?
        .eq_ignore_ascii_case(&recipe.recipe().ubuntu_base.initial_keyring_sha256)
    {
        return Err(invalid("pinned Ubuntu-child keyring hash changed"));
    }
    Ok((input_paths, podman, builder_image, keyring_path, commands))
}

fn verify_build_kernel_signature(
    recipe: &RecipeFile,
    receipt: &FetchReceipt,
    staged: &BTreeMap<String, PathBuf>,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
) -> Result<VerifiedKernelSourceSignature, Box<dyn Error>> {
    let kernel_key = staged
        .get("kernel_signing_key")
        .ok_or_else(|| invalid("Fetch receipt omits the locked kernel signing key"))?;
    let key_hash = sha256_file(kernel_key)?;
    let gpgv = builder_tool(recipe, "gpgv")?;
    let verified = match recipe.recipe().kernel.source_variant()? {
        crate::recipe::KernelSourceVariant::SignedGitTag { .. } => {
            let payload_name = receipt
                .kernel_tag_payload_path
                .as_deref()
                .ok_or_else(|| invalid("Fetch receipt omits the signed kernel tag payload path"))?;
            let signature_name = receipt
                .kernel_tag_signature_path
                .as_deref()
                .ok_or_else(|| invalid("Fetch receipt omits the kernel tag signature path"))?;
            let payload = staged
                .get(payload_name)
                .ok_or_else(|| invalid("staged Fetch inputs omit the signed kernel tag payload"))?;
            let signature = staged
                .get(signature_name)
                .ok_or_else(|| invalid("staged Fetch inputs omit the kernel tag signature"))?;
            let payload_hash = sha256_file(payload)?;
            let signature_hash = sha256_file(signature)?;
            crate::signature::verify_signature_in_verified_local(
                podman,
                builder_image,
                SignatureRequest {
                    keyring_path: kernel_key,
                    keyring_source_path: &recipe.recipe().kernel.signing_key_url,
                    keyring_sha256: &key_hash,
                    signed_path: payload,
                    signed_sha256: &payload_hash,
                    detached_signature: Some((signature, &signature_hash)),
                    expected_fingerprint: &recipe.recipe().kernel.signing_fingerprint,
                    command_id: "build_kernel_tag",
                },
                &gpgv.path,
                &gpgv.sha256,
                run,
            )?
        }
        crate::recipe::KernelSourceVariant::SignedTarball { .. } => {
            verify_build_kernel_tar_signature(&KernelTarSignatureContext {
                recipe,
                receipt,
                staged,
                podman,
                builder_image,
                run,
                kernel_key,
                key_hash: &key_hash,
                gpgv,
            })?
        }
    };
    Ok(VerifiedKernelSourceSignature {
        receipt: verified.receipt().clone(),
        command: verified.command_receipt().clone(),
    })
}

fn verify_build_kernel_tar_signature(
    context: &KernelTarSignatureContext<'_, '_>,
) -> Result<crate::VerifiedSignature, Box<dyn Error>> {
    let signature_name = context
        .receipt
        .kernel_source_signature_path
        .as_deref()
        .ok_or_else(|| invalid("Fetch receipt omits the detached kernel tar signature path"))?;
    let signature = context
        .staged
        .get(signature_name)
        .ok_or_else(|| invalid("staged Fetch inputs omit the kernel tar signature"))?;
    let archive = context
        .staged
        .get(&context.receipt.kernel_source_archive_path)
        .ok_or_else(|| invalid("staged Fetch inputs omit the uncompressed kernel tar"))?;
    let archive_hash = sha256_file(archive)?;
    let signature_hash = sha256_file(signature)?;
    crate::signature::verify_signature_in_verified_local(
        context.podman,
        context.builder_image,
        SignatureRequest {
            keyring_path: context.kernel_key,
            keyring_source_path: &context.recipe.recipe().kernel.signing_key_url,
            keyring_sha256: context.key_hash,
            signed_path: archive,
            signed_sha256: &archive_hash,
            detached_signature: Some((signature, &signature_hash)),
            expected_fingerprint: &context.recipe.recipe().kernel.signing_fingerprint,
            command_id: "build_kernel_source_tar_signature",
        },
        &context.gpgv.path,
        &context.gpgv.sha256,
        context.run,
    )
}

fn build_image_artifacts(
    context: &BuildContext<'_, '_>,
    commands: &mut Vec<CommandReceipt>,
) -> Result<BuildArtifacts, Box<dyn Error>> {
    let config_path = context
        .input_paths
        .get("firecracker_kernel_config")
        .ok_or_else(|| invalid("Fetch receipt omits Firecracker kernel config"))?;
    let base_path = context
        .input_paths
        .get("ubuntu_base_tar_gz")
        .ok_or_else(|| invalid("Fetch receipt omits the Canonical Ubuntu Base archive"))?;
    let smoke_path = context
        .run
        .path()
        .join(&context.fetch_receipt.offline_smoke_archive_path);
    verify_kernel_source_archive(
        context.recipe,
        context.fetch_receipt,
        context.input_paths,
        context.podman,
        context.builder_image,
        context.run,
        commands,
    )?;
    build_kernel(
        context.recipe,
        config_path,
        context.podman,
        context.builder_image,
        context.run,
        commands,
    )?;
    let (filesystem, inventory_path, inventory_sha256, rootfs_release) = build_rootfs(
        RootfsBuildRequest {
            recipe: context.recipe,
            guest: context.guest,
            receipt: context.fetch_receipt,
            staged: context.input_paths,
            base_archive: base_path,
            smoke_archive: &smoke_path,
            podman: context.podman,
            builder_image: context.builder_image,
            run: context.run,
        },
        commands,
    )?;
    verify_root_tree_inventory(
        &context.run.path().join(&inventory_path),
        &inventory_sha256,
        context.guest,
    )?;
    let firecracker_release = verify_build_firecracker_release(context)?;
    Ok(BuildArtifacts {
        filesystem,
        rootfs_release,
        firecracker_release,
    })
}

fn verify_build_firecracker_release(
    context: &BuildContext<'_, '_>,
) -> Result<Option<FirecrackerReleaseReceipt>, Box<dyn Error>> {
    match (
        &context.recipe.recipe().firecracker,
        &context.fetch_receipt.firecracker_release,
    ) {
        (Some(pin), Some(fetched)) => {
            let archive = context
                .input_paths
                .get("firecracker-release-archive")
                .ok_or_else(|| {
                    invalid("staged Fetch inputs omit the Firecracker release archive")
                })?;
            let inspected = crate::firecracker_release::inspect_firecracker_release_archive(
                archive,
                &pin.archive_url,
                pin.archive_size_bytes,
                &pin.archive_sha256,
                &pin.executable_member,
                Some(&pin.executable_sha256),
                context.run,
            )?;
            if inspected.executable_member != fetched.executable_member
                || inspected.executable_size_bytes != fetched.executable_size_bytes
                || !inspected
                    .executable_sha256
                    .eq_ignore_ascii_case(&fetched.executable_sha256)
                || inspected.archive != fetched.archive
            {
                return Err(invalid(
                    "Firecracker release archive or executable changed between Fetch and Build",
                ));
            }
            let download = context
                .fetch_receipt
                .sources
                .iter()
                .find(|source| source.id == "firecracker-release-archive")
                .ok_or_else(|| {
                    invalid("Fetch receipt omits Firecracker download transport evidence")
                })?
                .clone();
            Ok(Some(FirecrackerReleaseReceipt {
                archive: inspected.archive,
                download,
                executable_member: inspected.executable_member,
                executable: SourceArtifactReceipt {
                    source_url: pin.archive_url.clone(),
                    path: "firecracker-release-executable".to_owned(),
                    size_bytes: inspected.executable_size_bytes,
                    sha256: inspected.executable_sha256,
                },
                archive_metadata_source: inspected.archive_metadata_source,
            }))
        }
        (None, None) => Ok(None),
        _ => Err(invalid(
            "Firecracker release evidence does not match the recipe",
        )),
    }
}

struct BuildReceiptFiles {
    package_locks: (String, String, String, String),
    guest_settings: (String, String),
    builder_settings: (String, String),
    kernel_output: ArtifactOutput,
    rootfs_output: ArtifactOutput,
}

fn write_build_receipt_files(
    context: &BuildContext<'_, '_>,
) -> Result<BuildReceiptFiles, Box<dyn Error>> {
    Ok(BuildReceiptFiles {
        package_locks: write_locked_package_files(
            context.recipe,
            context.guest,
            context.builder,
            context.run,
        )?,
        guest_settings: write_apt_settings(context.recipe, "guest", context.run)?,
        builder_settings: write_apt_settings(context.recipe, "builder", context.run)?,
        kernel_output: output_receipt(context.run, "kernel.vmlinux")?,
        rootfs_output: output_receipt(context.run, "rootfs.ext4")?,
    })
}

fn build_kernel_receipt(
    context: &BuildContext<'_, '_>,
    signature: &SignatureReceipt,
    output: ArtifactOutput,
) -> Result<KernelReceipt, Box<dyn Error>> {
    let recipe = context.recipe;
    let receipt = context.fetch_receipt;
    let variant = recipe.recipe().kernel.source_variant()?;
    let source_url = match &variant {
        crate::recipe::KernelSourceVariant::SignedGitTag { repository, .. } => repository.clone(),
        crate::recipe::KernelSourceVariant::SignedTarball { archive_url, .. } => {
            archive_url.clone()
        }
    };
    let revision = match &variant {
        crate::recipe::KernelSourceVariant::SignedGitTag { commit, .. } => commit.clone(),
        crate::recipe::KernelSourceVariant::SignedTarball { version, .. } => version.clone(),
    };
    let source_archive_uncompressed = SourceArtifactReceipt {
        source_url: match &variant {
            crate::recipe::KernelSourceVariant::SignedGitTag { .. } => {
                "generated-from-verified-git-checkout".to_owned()
            }
            crate::recipe::KernelSourceVariant::SignedTarball { archive_url, .. } => {
                archive_url.clone()
            }
        },
        path: receipt.kernel_source_archive_path.clone(),
        size_bytes: receipt.kernel_source_archive_size_bytes,
        sha256: receipt.kernel_source_archive_sha256.clone(),
    };
    let source_archive_compressed = receipt
        .kernel_source_compressed_path
        .as_ref()
        .map(|path| source_artifact_from_download(receipt, path))
        .transpose()?;
    let source_signature = receipt
        .kernel_source_signature_path
        .as_ref()
        .map(|path| {
            Ok::<_, Box<dyn Error>>(KernelSourceSignatureReceipt {
                artifact: source_artifact_from_download(receipt, path)?,
                verification: signature.clone(),
            })
        })
        .transpose()?;
    let resolved_config_path = "kernel-source/.config".to_owned();
    let custom_config_sha256 = sha256_file(&context.run.path().join(&resolved_config_path))?;
    let resolved_config_diff_path = (recipe.recipe().kernel.source_kind
        == "kernel.org-signed-tarball")
        .then(|| "kernel-config.diff".to_owned());
    let custom_config_diff_sha256 = if let Some(path) = &resolved_config_diff_path {
        sha256_file(&context.run.path().join(path))?
    } else {
        String::new()
    };
    let tag_signature = matches!(
        variant,
        crate::recipe::KernelSourceVariant::SignedGitTag { .. }
    )
    .then(|| signature.clone());
    Ok(KernelReceipt {
        source_kind: recipe.recipe().kernel.source_kind.clone(),
        source_url,
        signing_key_url: recipe.recipe().kernel.signing_key_url.clone(),
        signing_key_sha256: recipe.recipe().kernel.signing_key_sha256.clone(),
        signing_fingerprint: recipe.recipe().kernel.signing_fingerprint.clone(),
        annotated_tag: recipe.recipe().kernel.annotated_tag.clone(),
        tag_object: receipt.kernel_tag_object.clone().unwrap_or_default(),
        source_revision: revision,
        tag_signature,
        source_archive_compressed,
        source_archive_uncompressed,
        source_signature,
        firecracker_version: recipe.recipe().kernel.firecracker_version.clone(),
        firecracker_commit: recipe.recipe().kernel.firecracker_commit.clone(),
        config_url: recipe.recipe().kernel.firecracker_config_url.clone(),
        source_date_epoch: source_epoch_from_run(context.run)?,
        config_sha256: recipe.recipe().kernel.firecracker_config_sha256.clone(),
        kernel_policy_url: recipe.recipe().kernel.kernel_policy_url.clone(),
        kernel_policy_sha256: recipe.recipe().kernel.kernel_policy_sha256.clone(),
        custom_config_sha256,
        custom_config_diff_sha256,
        resolved_config_path,
        resolved_config_diff_path,
        builder_toolchain_lock_sha256: recipe
            .recipe()
            .ubuntu_build_builder
            .packages_lock_sha256
            .clone(),
        release: read_file_trimmed(&context.run.path().join("kernel-release"))?,
        output,
    })
}

fn source_artifact_from_download(
    receipt: &FetchReceipt,
    path: &str,
) -> Result<SourceArtifactReceipt, Box<dyn Error>> {
    let download = input_receipt(receipt, path)?;
    Ok(SourceArtifactReceipt {
        source_url: download.source_url.clone(),
        path: download.cache_path.clone(),
        size_bytes: download.size_bytes,
        sha256: download.sha256.clone(),
    })
}

fn build_rootfs_receipt(
    context: &BuildContext<'_, '_>,
    canonical_signature: SignatureReceipt,
    filesystem: Ext4Receipt,
    rootfs_release: String,
    files: &BuildReceiptFiles,
) -> Result<RootfsReceipt, Box<dyn Error>> {
    let recipe = context.recipe;
    Ok(RootfsReceipt {
        source_url: recipe.recipe().ubuntu_base.url.clone(),
        source_sha256: recipe.recipe().ubuntu_base.sha256.clone(),
        sha256sums_url: recipe.recipe().ubuntu_base.sha256sums_url.clone(),
        sha256sums_sha256: input_receipt(context.fetch_receipt, "ubuntu_sha256sums")?
            .sha256
            .clone(),
        signature_url: recipe.recipe().ubuntu_base.signature_url.clone(),
        signature: canonical_signature,
        release: rootfs_release,
        filesystem,
        package_lock_path: files.package_locks.0.clone(),
        package_lock_sha256: files.package_locks.1.clone(),
        output: files.rootfs_output.clone(),
    })
}

fn rust_cli_builder_receipt(recipe: &RecipeFile, host_abi: String) -> RustCliBuilderReceipt {
    let rust = &recipe.recipe().rust_cli_builder;
    RustCliBuilderReceipt {
        base_image: rust.base_image.clone(),
        base_oci_digest: rust.base_image_digest.clone(),
        base_index_oci_digest: rust.base_image_index_digest.clone(),
        source_tree_sha256: rust.source_tree_sha256.clone(),
        cargo_lock_path: "rust-cli-Cargo.lock".to_owned(),
        cargo_lock_sha256: rust.cargo_lock_sha256.clone(),
        rust_toolchain_path: "rust-cli-rust-toolchain.toml".to_owned(),
        rust_toolchain_sha256: rust.rust_toolchain_sha256.clone(),
        rust_toolchain_channel: rust.rust_toolchain_channel.clone(),
        target: rust.target.clone(),
        executable_path: "fc-image-builder".to_owned(),
        executable_sha256: rust.output_executable_sha256.clone(),
        rust_version: env!("FC_IMAGE_BUILDER_RUSTC_VERSION").to_owned(),
        cargo_version: env!("FC_IMAGE_BUILDER_CARGO_VERSION").to_owned(),
        host_abi,
    }
}

fn ubuntu_builder_receipt(
    recipe: &RecipeFile,
    builder: &CandidatePackageLock,
    package_locks: &(String, String, String, String),
) -> Result<UbuntuBuildBuilderReceipt, Box<dyn Error>> {
    let pin = &recipe.recipe().ubuntu_build_builder;
    let toolchain = builder
        .builder_toolchain_receipt
        .as_ref()
        .ok_or_else(|| invalid("builder lock omits its toolchain receipt"))?;
    let tools = toolchain
        .tools
        .iter()
        .map(|tool| UbuntuToolReceipt {
            name: tool.name.clone(),
            path: tool.path.clone(),
            version: tool.version.clone(),
            sha256: tool.sha256.clone(),
        })
        .collect();
    Ok(UbuntuBuildBuilderReceipt {
        base_image: pin.base_image.clone(),
        base_oci_digest: pin.base_image_digest.clone(),
        base_index_oci_digest: pin.base_image_index_digest.clone(),
        derived_oci_digest: toolchain.derived_oci_digest.clone(),
        initial_gpgv_path: pin.initial_gpgv_path.clone(),
        initial_gpgv_sha256: pin.initial_gpgv_sha256.clone(),
        initial_keyring_path: pin.initial_keyring_path.clone(),
        initial_keyring_sha256: pin.initial_keyring_sha256.clone(),
        package_lock_path: package_locks.2.clone(),
        package_lock_sha256: package_locks.3.clone(),
        build_argv: toolchain.build_argv.clone(),
        tools,
    })
}

fn host_podman_receipt(recipe: &RecipeFile) -> HostPodmanReceipt {
    let podman = &recipe.recipe().host_podman;
    HostPodmanReceipt {
        path: podman.path.clone(),
        version: podman.version.clone(),
        sha256: podman.sha256.clone(),
        rootless: podman.rootless,
        uid: podman.uid,
        gid: podman.gid,
        resource_limits: ResourceLimits {
            cpu_count: podman.cpu_count,
            memory_mib: podman.memory_mib,
            pids_limit: podman.pids_limit,
            workspace_limit_bytes: podman.workspace_limit_bytes,
        },
    }
}

fn write_image_manifest(input: ImageManifestInput<'_, '_>) -> Result<PathBuf, Box<dyn Error>> {
    let ImageManifestInput {
        context,
        guest_indexes,
        builder_indexes,
        canonical_signature,
        kernel_source_signature,
        artifacts,
        rust_cli_host_abi,
        mut commands,
    } = input;
    let files = write_build_receipt_files(context)?;
    let kernel = build_kernel_receipt(
        context,
        &kernel_source_signature,
        files.kernel_output.clone(),
    )?;
    let BuildArtifacts {
        filesystem,
        rootfs_release,
        firecracker_release,
    } = artifacts;
    let rootfs = build_rootfs_receipt(
        context,
        canonical_signature,
        filesystem,
        rootfs_release,
        &files,
    )?;
    let guest_solver = context
        .guest
        .apt_solver_receipt
        .as_ref()
        .ok_or_else(|| invalid("guest lock omits its APT solver receipt"))?;
    let builder_solver = context
        .builder
        .apt_solver_receipt
        .as_ref()
        .ok_or_else(|| invalid("builder lock omits its APT solver receipt"))?;
    let manifest = ImageManifest {
        schema_version: 2,
        recipe: RecipeIdentityReceipt {
            identity: context.recipe.recipe().identity.clone(),
            version: context.recipe.recipe().recipe_version.clone(),
            sha256: context.recipe.sha256().to_owned(),
        },
        qualification_status: "verified-candidate".to_owned(),
        reproducibility_status: ReproducibilityStatus::SingleBuildUnqualified,
        reproducibility: None,
        verified_at_unix_ms: now_unix_ms()?,
        bench_source: bench_source_receipt(
            context.recipe,
            context.podman,
            context.builder_image,
            context.run,
            &mut commands,
        )?,
        kernel,
        firecracker_release,
        rootfs,
        guest_apt: apt_receipt(AptReceiptInput {
            recipe: context.recipe,
            lock: context.guest,
            indexes: guest_indexes,
            solver: guest_solver,
            package_lock_path: &files.package_locks.0,
            package_lock_sha256: &files.package_locks.1,
            settings_path: &files.guest_settings.0,
            settings_sha256: &files.guest_settings.1,
        }),
        builder_apt: apt_receipt(AptReceiptInput {
            recipe: context.recipe,
            lock: context.builder,
            indexes: builder_indexes,
            solver: builder_solver,
            package_lock_path: &files.package_locks.2,
            package_lock_sha256: &files.package_locks.3,
            settings_path: &files.builder_settings.0,
            settings_sha256: &files.builder_settings.1,
        }),
        rust_cli_builder: rust_cli_builder_receipt(context.recipe, rust_cli_host_abi),
        ubuntu_build_builder: ubuntu_builder_receipt(
            context.recipe,
            context.builder,
            &files.package_locks,
        )?,
        host_podman: host_podman_receipt(context.recipe),
        oci_smoke: OciSmokeReceipt {
            image_reference: context.recipe.recipe().offline_smoke.image.clone(),
            image_digest: context.recipe.recipe().offline_smoke.digest.clone(),
            platform: context.recipe.recipe().offline_smoke.platform.clone(),
            archive_sha256: context.fetch_receipt.offline_smoke_archive_sha256.clone(),
        },
        commands,
        service_images_qualified: false,
        limitations: vec![
            "single build is not a reproducibility qualification".to_owned(),
            "service OCI images and guest Firecracker behavior remain unqualified".to_owned(),
            "root-tree inventory excludes extended attributes".to_owned(),
        ],
    };
    manifest.validate()?;
    let path = context.run.write_new(
        "image-manifest.json",
        &serde_json::to_vec_pretty(&manifest)?,
    )?;
    manifest.verify_outputs(&path)?;
    Ok(path)
}

fn validate_pinned_lock_files(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<(), Box<dyn Error>> {
    for (lock, role, relative, expected) in [
        (
            guest,
            "guest",
            recipe.recipe().guest_apt.packages_lock_path.as_str(),
            recipe.recipe().guest_apt.packages_lock_sha256.as_str(),
        ),
        (
            builder,
            "builder",
            recipe
                .recipe()
                .ubuntu_build_builder
                .packages_lock_path
                .as_str(),
            recipe
                .recipe()
                .ubuntu_build_builder
                .packages_lock_sha256
                .as_str(),
        ),
    ] {
        let path = recipe.resolve_recipe_path(relative)?;
        if lock.package_role != role || !sha256_file(&path)?.eq_ignore_ascii_case(expected) {
            return Err(invalid("package lock role or pinned file hash changed"));
        }
    }
    Ok(())
}

fn read_fetch_receipt(root: &Path) -> Result<FetchReceipt, Box<dyn Error>> {
    let path = root.join("fetch-receipt.json");
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > FETCH_RECEIPT_LIMIT
    {
        return Err(invalid(
            "Fetch receipt must be a bounded regular non-symlink file",
        ));
    }
    Ok(serde_json::from_slice(&fs::read(path)?)?)
}

fn validate_fetch_receipt(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    root: &Path,
    expected_builder_archive: &crate::oci_archive::OciArchiveIdentity,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    validate_fetch_receipt_identity(recipe, expected_builder_archive, receipt)?;
    validate_kernel_fetch_evidence(recipe, receipt)?;
    validate_firecracker_fetch_evidence(recipe, root, receipt)?;
    validate_fetch_receipt_files(
        recipe,
        guest,
        builder,
        root,
        expected_builder_archive,
        receipt,
    )?;
    validate_fetch_command_receipts(root, &receipt.commands)
}

fn validate_fetch_receipt_identity(
    recipe: &RecipeFile,
    expected_builder_archive: &crate::oci_archive::OciArchiveIdentity,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    let expected_revision = match recipe.recipe().kernel.source_variant()? {
        crate::recipe::KernelSourceVariant::SignedGitTag { commit, .. } => commit,
        crate::recipe::KernelSourceVariant::SignedTarball { version, .. } => version,
    };
    if receipt.schema_version != 3
        || receipt.recipe_identity != recipe.recipe().identity
        || receipt.recipe_version != recipe.recipe().recipe_version
        || receipt.recipe_sha256 != recipe.sha256()
        || receipt.guest_lock_sha256 != recipe.recipe().guest_apt.packages_lock_sha256
        || receipt.builder_lock_sha256 != recipe.recipe().ubuntu_build_builder.packages_lock_sha256
        || receipt.derived_builder_archive_size_bytes != expected_builder_archive.archive_size_bytes
        || !receipt
            .derived_builder_archive_sha256
            .eq_ignore_ascii_case(&expected_builder_archive.archive_sha256)
        || !receipt
            .derived_builder_manifest_digest
            .eq_ignore_ascii_case(&expected_builder_archive.manifest_digest)
        || !receipt
            .derived_builder_config_digest
            .eq_ignore_ascii_case(&expected_builder_archive.config_digest)
        || receipt.derived_builder_platform != expected_builder_archive.platform
        || receipt.kernel_source_kind != recipe.recipe().kernel.source_kind
        || receipt.kernel_source_revision != expected_revision
        || receipt.initial_keyring_path != recipe.recipe().ubuntu_base.initial_keyring_path
        || !receipt
            .initial_keyring_sha256
            .eq_ignore_ascii_case(&recipe.recipe().ubuntu_base.initial_keyring_sha256)
        || receipt.verified_at_unix_ms == 0
    {
        return Err(invalid(
            "Fetch receipt belongs to a different pinned recipe or lock pair",
        ));
    }
    Ok(())
}

fn validate_kernel_fetch_evidence(
    recipe: &RecipeFile,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    match recipe.recipe().kernel.source_variant()? {
        crate::recipe::KernelSourceVariant::SignedGitTag {
            annotated_tag_object,
            ..
        } => {
            if receipt.kernel_tag_object.as_deref() != Some(annotated_tag_object.as_str())
                || receipt.kernel_tag_payload_path.as_deref() != Some("kernel-tag-payload")
                || receipt.kernel_tag_signature_path.as_deref() != Some("kernel-tag-signature")
                || receipt.kernel_tag_signature.is_none()
                || receipt.kernel_source_compressed_path.is_some()
                || receipt.kernel_source_signature_path.is_some()
                || receipt.kernel_source_signature.is_some()
            {
                return Err(invalid("Fetch receipt has incomplete Git-tag evidence"));
            }
        }
        crate::recipe::KernelSourceVariant::SignedTarball {
            archive_sha256,
            signature_sha256,
            ..
        } => {
            let signature = receipt
                .kernel_source_signature
                .as_ref()
                .ok_or_else(|| invalid("Fetch receipt omits the detached kernel tar signature"))?;
            if receipt.kernel_tag_object.is_some()
                || receipt.kernel_tag_payload_path.is_some()
                || receipt.kernel_tag_signature_path.is_some()
                || receipt.kernel_tag_signature.is_some()
                || receipt.kernel_source_compressed_path.as_deref() != Some("kernel-source.tar.xz")
                || receipt
                    .kernel_source_compressed_size_bytes
                    .unwrap_or_default()
                    == 0
                || receipt.kernel_source_compressed_sha256.as_deref()
                    != Some(archive_sha256.as_str())
                || receipt.kernel_source_signature_path.as_deref() != Some("kernel-source.tar.sign")
                || signature.signature_sha256 != signature_sha256
                || signature.signer_primary_fingerprint
                    != recipe.recipe().kernel.signing_fingerprint
                || receipt.kernel_source_archive_path != "kernel-source.tar"
                || receipt.kernel_source_archive_size_bytes == 0
                || receipt.kernel_source_archive_sha256.len() != 64
            {
                return Err(invalid(
                    "Fetch receipt has incomplete signed-tarball evidence",
                ));
            }
        }
    }
    Ok(())
}

fn validate_firecracker_fetch_evidence(
    recipe: &RecipeFile,
    root: &Path,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    match (&recipe.recipe().firecracker, &receipt.firecracker_release) {
        (Some(pin), Some(evidence)) => {
            let archive_download = receipt
                .sources
                .iter()
                .find(|source| source.id == "firecracker-release-archive")
                .ok_or_else(|| invalid("Fetch receipt omits the Firecracker release download"))?;
            if evidence.archive.source_url != pin.archive_url
                || evidence.archive.path != "firecracker-release-archive"
                || evidence.archive.size_bytes != pin.archive_size_bytes
                || !evidence
                    .archive
                    .sha256
                    .eq_ignore_ascii_case(&pin.archive_sha256)
                || archive_download.source_url != pin.archive_url
                || archive_download.cache_path != evidence.archive.path
                || archive_download.size_bytes != evidence.archive.size_bytes
                || !archive_download
                    .sha256
                    .eq_ignore_ascii_case(&evidence.archive.sha256)
                || evidence.executable_member != pin.executable_member
                || evidence.executable_size_bytes == 0
                || !evidence
                    .executable_sha256
                    .eq_ignore_ascii_case(&pin.executable_sha256)
                || evidence.executable_path != "firecracker-release-executable"
                || evidence.archive_metadata_source != pin.archive_metadata_source
            {
                return Err(invalid(
                    "Fetch Firecracker release evidence differs from the recipe pin",
                ));
            }
            let executable_path = confined_file(root, &evidence.executable_path)?;
            let metadata = fs::symlink_metadata(&executable_path)?;
            if metadata.file_type().is_symlink()
                || !metadata.is_file()
                || metadata.len() != evidence.executable_size_bytes
                || !sha256_file(&executable_path)?.eq_ignore_ascii_case(&evidence.executable_sha256)
            {
                return Err(invalid(
                    "Fetch Firecracker executable differs from its archive inspection receipt",
                ));
            }
            crate::input::validate_download_transport(archive_download)?;
        }
        (None, None) => {}
        _ => {
            return Err(invalid(
                "Fetch Firecracker release presence differs from recipe",
            ));
        }
    }
    Ok(())
}

fn validate_fetch_receipt_files(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    root: &Path,
    expected_builder_archive: &crate::oci_archive::OciArchiveIdentity,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    let recorded_archive = confined_file(root, &receipt.derived_builder_archive_path)?;
    let recorded_identity = verify_derived_builder_archive(recipe, &recorded_archive)?;
    if &recorded_identity != expected_builder_archive {
        return Err(invalid(
            "Fetch receipt derived-builder archive differs from Build input",
        ));
    }
    validate_source_receipts(recipe, guest, builder, root, receipt)?;
    validate_fetched_packages(guest, builder, root, receipt)?;
    for (name, expected_hash, expected_size) in [
        (
            receipt.kernel_source_archive_path.as_str(),
            receipt.kernel_source_archive_sha256.as_str(),
            receipt.kernel_source_archive_size_bytes,
        ),
        (
            receipt.offline_smoke_archive_path.as_str(),
            receipt.offline_smoke_archive_sha256.as_str(),
            receipt.offline_smoke_archive_size_bytes,
        ),
        (
            receipt.derived_builder_archive_path.as_str(),
            receipt.derived_builder_archive_sha256.as_str(),
            receipt.derived_builder_archive_size_bytes,
        ),
    ] {
        let path = confined_file(root, name)?;
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(invalid("Fetch auxiliary input is not a regular file"));
        }
        if expected_size != 0 && metadata.len() != expected_size {
            return Err(invalid("Fetch auxiliary input size changed"));
        }
        if !expected_hash.is_empty() && !sha256_file(&path)?.eq_ignore_ascii_case(expected_hash) {
            return Err(invalid("Fetch auxiliary input hash changed"));
        }
    }
    for name in [
        receipt.kernel_tag_payload_path.as_deref(),
        receipt.kernel_tag_signature_path.as_deref(),
    ]
    .into_iter()
    .flatten()
    {
        let path = confined_file(root, name)?;
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(invalid("Fetch signed-tag input is not a regular file"));
        }
    }
    if !sha256_file(&confined_file(root, &receipt.offline_smoke_archive_path)?)?
        .eq_ignore_ascii_case(&recipe.recipe().offline_smoke.archive_sha256)
    {
        return Err(invalid(
            "offline smoke archive no longer matches its recipe pin",
        ));
    }
    Ok(())
}

fn validate_fetch_command_receipts(
    root: &Path,
    commands: &[CommandReceipt],
) -> Result<(), Box<dyn Error>> {
    for command in commands {
        if command.status_code != 0
            || command.program.is_empty()
            || command.program_sha256.len() != 64
        {
            return Err(invalid("Fetch receipt contains an unsuccessful command"));
        }
        for (path, expected) in [
            (&command.stdout_path, &command.stdout_sha256),
            (&command.stderr_path, &command.stderr_sha256),
        ] {
            let log = confined_file(root, path)?;
            if log.metadata()?.len() > 512 * 1024 * 1024
                || !sha256_file(&log)?.eq_ignore_ascii_case(expected)
            {
                return Err(invalid("Fetch command log differs from its hash receipt"));
            }
        }
    }
    Ok(())
}

type ObservedSources<'a> = BTreeMap<&'a str, &'a crate::DownloadReceipt>;

fn validate_source_receipts(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    root: &Path,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    let observed = observe_fetch_sources(root, &receipt.sources)?;
    validate_fixed_source_pins(recipe, &observed)?;
    validate_kernel_source_pins(recipe, &observed)?;
    validate_apt_index_sources(root, guest, builder, &observed)?;
    validate_package_source_receipts(recipe, receipt, &observed)
}

fn observe_fetch_sources<'a>(
    root: &Path,
    sources: &'a [crate::DownloadReceipt],
) -> Result<ObservedSources<'a>, Box<dyn Error>> {
    let mut observed = BTreeMap::new();
    for source in sources {
        crate::input::validate_download_transport(source)?;
        if observed.insert(source.id.as_str(), source).is_some() {
            return Err(invalid("Fetch receipt repeats an artifact identity"));
        }
        let path = confined_file(root, &source.cache_path)?;
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink()
            || !metadata.is_file()
            || metadata.len() != source.size_bytes
            || !sha256_file(&path)?.eq_ignore_ascii_case(&source.sha256)
        {
            return Err(invalid("fetched source bytes differ from their receipt"));
        }
    }
    Ok(observed)
}

fn validate_fixed_source_pins(
    recipe: &RecipeFile,
    observed: &ObservedSources<'_>,
) -> Result<(), Box<dyn Error>> {
    let fixed = [
        (
            "kernel_signing_key",
            recipe.recipe().kernel.signing_key_url.as_str(),
            recipe.recipe().kernel.signing_key_sha256.as_str(),
        ),
        (
            "ubuntu_sha256sums",
            recipe.recipe().ubuntu_base.sha256sums_url.as_str(),
            "",
        ),
        (
            "ubuntu_sha256sums_signature",
            recipe.recipe().ubuntu_base.signature_url.as_str(),
            "",
        ),
        (
            "ubuntu_base_tar_gz",
            recipe.recipe().ubuntu_base.url.as_str(),
            recipe.recipe().ubuntu_base.sha256.as_str(),
        ),
        (
            "firecracker_kernel_config",
            recipe.recipe().kernel.firecracker_config_url.as_str(),
            recipe.recipe().kernel.firecracker_config_sha256.as_str(),
        ),
        (
            "firecracker_kernel_policy",
            recipe.recipe().kernel.kernel_policy_url.as_str(),
            recipe.recipe().kernel.kernel_policy_sha256.as_str(),
        ),
    ];
    for (id, url, hash) in fixed {
        let source = observed
            .get(id)
            .ok_or_else(|| invalid(&format!("Fetch receipt omits {id}")))?;
        if source.source_url != url
            || (!hash.is_empty() && !source.sha256.eq_ignore_ascii_case(hash))
        {
            return Err(invalid(
                "fetched source differs from its immutable recipe pin",
            ));
        }
    }
    Ok(())
}

fn validate_kernel_source_pins(
    recipe: &RecipeFile,
    observed: &ObservedSources<'_>,
) -> Result<(), Box<dyn Error>> {
    match recipe.recipe().kernel.source_variant()? {
        crate::recipe::KernelSourceVariant::SignedGitTag { .. } => {}
        crate::recipe::KernelSourceVariant::SignedTarball {
            archive_url,
            archive_sha256,
            signature_url,
            signature_sha256,
            ..
        } => {
            for (id, url, sha256) in [
                (
                    "kernel-source.tar.xz",
                    archive_url.as_str(),
                    archive_sha256.as_str(),
                ),
                (
                    "kernel-source.tar.sign",
                    signature_url.as_str(),
                    signature_sha256.as_str(),
                ),
            ] {
                let source = observed.get(id).ok_or_else(|| {
                    invalid("Fetch receipt omits a locked kernel source artifact")
                })?;
                if source.source_url != url || !source.sha256.eq_ignore_ascii_case(sha256) {
                    return Err(invalid(
                        "fetched signed kernel source differs from its immutable recipe pin",
                    ));
                }
            }
        }
    }
    Ok(())
}

fn validate_apt_index_sources(
    root: &Path,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    observed: &ObservedSources<'_>,
) -> Result<(), Box<dyn Error>> {
    let mut indexes = BTreeMap::new();
    for index in &guest.indexes {
        indexes.insert(index_key(index), index);
    }
    for index in &builder.indexes {
        if indexes
            .insert(index_key(index), index)
            .is_some_and(|old| !same_index(old, index))
        {
            return Err(invalid("guest and builder lock index identities disagree"));
        }
    }
    if indexes.len() != 6 {
        return Err(invalid(
            "Fetch receipt does not cover all six Noble indexes",
        ));
    }
    for (index_number, index) in indexes.into_values().enumerate() {
        for (suffix, url, sha256) in [
            (
                "inrelease",
                index.inrelease_url.as_str(),
                index.inrelease_sha256.as_str(),
            ),
            (
                "packages_xz",
                index.packages_index_url.as_str(),
                index.packages_index_sha256.as_str(),
            ),
        ] {
            let id = format!("apt_{index_number:02}_{suffix}");
            let source = observed
                .get(id.as_str())
                .ok_or_else(|| invalid("Fetch receipt omits a locked APT artifact"))?;
            if source.source_url != url || !source.sha256.eq_ignore_ascii_case(sha256) {
                return Err(invalid(
                    "fetched APT artifact differs from the package lock",
                ));
            }
            let path = confined_file(root, &source.cache_path)?;
            if path.metadata()?.len() != source.size_bytes {
                return Err(invalid("fetched APT artifact size changed"));
            }
        }
    }
    Ok(())
}

fn validate_package_source_receipts(
    recipe: &RecipeFile,
    receipt: &FetchReceipt,
    observed: &ObservedSources<'_>,
) -> Result<(), Box<dyn Error>> {
    let unique_deb_paths = receipt
        .packages
        .iter()
        .map(|package| package.path.as_str())
        .collect::<BTreeSet<_>>()
        .len();
    let kernel_artifacts = if recipe.recipe().kernel.source_kind == "kernel.org-signed-tarball" {
        2
    } else {
        0
    };
    let release_artifacts = usize::from(recipe.recipe().firecracker.is_some());
    if observed.len() != 18 + kernel_artifacts + release_artifacts + unique_deb_paths {
        return Err(invalid(
            "Fetch receipt contains unexpected or missing source artifacts",
        ));
    }
    for package in &receipt.packages {
        let source = observed
            .get(package.path.as_str())
            .ok_or_else(|| invalid("Fetch receipt omits a package download receipt"))?;
        if source.cache_path != package.path
            || source.source_url != package.source_url
            || source.size_bytes != package.size_bytes
            || !source.sha256.eq_ignore_ascii_case(&package.sha256)
        {
            return Err(invalid(
                "package download receipt differs from its package row",
            ));
        }
    }
    Ok(())
}

fn validate_fetched_packages(
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    root: &Path,
    receipt: &FetchReceipt,
) -> Result<(), Box<dyn Error>> {
    let expected = guest
        .packages
        .iter()
        .map(|package| ("guest", package))
        .chain(builder.packages.iter().map(|package| ("builder", package)))
        .collect::<Vec<_>>();
    if expected.len() != receipt.packages.len() {
        return Err(invalid(
            "Fetch receipt package count differs from the guest and builder locks",
        ));
    }
    let mut seen = BTreeSet::new();
    for (role, lock_package) in expected {
        let matches = receipt.packages.iter().filter(|package| {
            package.role == role
                && package.name == lock_package.name
                && package.version == lock_package.version
                && package.architecture == lock_package.architecture
        });
        let package = matches
            .into_iter()
            .next()
            .ok_or_else(|| invalid("Fetch receipt omits a locked package"))?;
        if !seen.insert((
            role,
            package.name.as_str(),
            package.version.as_str(),
            package.architecture.as_str(),
        )) || package.source_url != lock_package.source_url
            || package.size_bytes != lock_package.size_bytes
            || !package.sha256.eq_ignore_ascii_case(&lock_package.sha256)
        {
            return Err(invalid(
                "fetched package identity differs from its exact lock",
            ));
        }
        let path = confined_file(root, &package.path)?;
        if path.metadata()?.len() != package.size_bytes
            || !sha256_file(&path)?.eq_ignore_ascii_case(&package.sha256)
        {
            return Err(invalid("fetched .deb bytes differ from the package lock"));
        }
    }
    Ok(())
}

fn stage_verified_inputs(
    input_root: &Path,
    receipt: &FetchReceipt,
    run: &RunDirectory,
) -> Result<BTreeMap<String, PathBuf>, Box<dyn Error>> {
    let mut paths = BTreeMap::new();
    for source in &receipt.sources {
        let source_path = confined_file(input_root, &source.cache_path)?;
        let destination = copy_verified_file(
            &source_path,
            &source.cache_path,
            &source.sha256,
            source.size_bytes,
            run,
        )?;
        paths.insert(source.id.clone(), destination);
    }
    let mut copied_package_paths = BTreeSet::new();
    for package in &receipt.packages {
        if !copied_package_paths.insert(package.path.as_str())
            || paths.contains_key(package.path.as_str())
        {
            continue;
        }
        let source_path = confined_file(input_root, &package.path)?;
        let destination = copy_verified_file(
            &source_path,
            &package.path,
            &package.sha256,
            package.size_bytes,
            run,
        )?;
        paths.insert(package.path.clone(), destination);
    }
    for (source_name, destination_name, hash, size) in [
        (
            receipt.kernel_source_archive_path.as_str(),
            receipt.kernel_source_archive_path.as_str(),
            receipt.kernel_source_archive_sha256.as_str(),
            receipt.kernel_source_archive_size_bytes,
        ),
        (
            receipt.offline_smoke_archive_path.as_str(),
            receipt.offline_smoke_archive_path.as_str(),
            receipt.offline_smoke_archive_sha256.as_str(),
            receipt.offline_smoke_archive_size_bytes,
        ),
    ] {
        let source_path = confined_file(input_root, source_name)?;
        let destination = copy_verified_file(&source_path, destination_name, hash, size, run)?;
        paths.insert(source_name.to_owned(), destination);
    }
    for name in [
        receipt.kernel_tag_payload_path.as_deref(),
        receipt.kernel_tag_signature_path.as_deref(),
    ]
    .into_iter()
    .flatten()
    {
        let source = confined_file(input_root, name)?;
        let expected_hash = sha256_file(&source)?;
        let size = source.metadata()?.len();
        let destination = copy_verified_file(&source, name, &expected_hash, size, run)?;
        paths.insert(name.to_owned(), destination);
    }
    Ok(paths)
}

fn stage_fetch_command_logs(
    input_root: &Path,
    receipt: &FetchReceipt,
    run: &RunDirectory,
) -> Result<Vec<CommandReceipt>, Box<dyn Error>> {
    let mut copied = Vec::with_capacity(receipt.commands.len());
    for (index, command) in receipt.commands.iter().enumerate() {
        let stdout_source = confined_file(input_root, &command.stdout_path)?;
        let stderr_source = confined_file(input_root, &command.stderr_path)?;
        let stdout_name = format!("fetch-{index:04}-stdout.log");
        let stderr_name = format!("fetch-{index:04}-stderr.log");
        copy_verified_file(
            &stdout_source,
            &stdout_name,
            &command.stdout_sha256,
            stdout_source.metadata()?.len(),
            run,
        )?;
        copy_verified_file(
            &stderr_source,
            &stderr_name,
            &command.stderr_sha256,
            stderr_source.metadata()?.len(),
            run,
        )?;
        let mut command = command.clone();
        command.stdout_path = stdout_name;
        command.stderr_path = stderr_name;
        copied.push(command);
    }
    Ok(copied)
}

fn copy_verified_file(
    source: &Path,
    destination_name: &str,
    expected_sha256: &str,
    expected_size: u64,
    run: &RunDirectory,
) -> Result<PathBuf, Box<dyn Error>> {
    if !is_safe_filename(destination_name) {
        return Err(invalid("staged input name is unsafe"));
    }
    let metadata = fs::symlink_metadata(source)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() != expected_size {
        return Err(invalid(
            "staged input is not a regular file of the recorded size",
        ));
    }
    let mut input = File::open(source)?;
    let mut staged = run.create_staged_file(destination_name)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 128 * 1024].into_boxed_slice();
    let mut total = 0_u64;
    loop {
        let count = input.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        total = total
            .checked_add(count as u64)
            .ok_or_else(|| invalid("staged input size overflow"))?;
        digest.update(&buffer[..count]);
        staged.write_all(&buffer[..count])?;
    }
    let hash = digest_hex(&digest.finalize());
    if total != expected_size || !hash.eq_ignore_ascii_case(expected_sha256) {
        return Err(invalid(
            "staged input hash or length differs from the verified receipt",
        ));
    }
    Ok(staged.commit()?.clone())
}

fn confined_file(root: &Path, relative: &str) -> Result<PathBuf, Box<dyn Error>> {
    let path = Path::new(relative);
    if path.is_absolute()
        || path.components().count() != 1
        || !matches!(path.components().next(), Some(Component::Normal(_)))
    {
        return Err(invalid(
            "Fetch receipt file path must be a single safe name",
        ));
    }
    let result = root.join(path);
    let metadata = fs::symlink_metadata(&result)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid("Fetch receipt path is not a regular file"));
    }
    Ok(result)
}

fn canonical_regular_directory(path: &Path, label: &str) -> Result<PathBuf, Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid(&format!(
            "{label} must be an existing non-symlink directory"
        )));
    }
    Ok(fs::canonicalize(path)?)
}

fn is_safe_filename(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 180
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

struct IndexVerificationContext<'a, 'run> {
    recipe: &'a RecipeFile,
    guest: &'a CandidatePackageLock,
    builder: &'a CandidatePackageLock,
    receipt: &'a FetchReceipt,
    staged: &'a BTreeMap<String, PathBuf>,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    keyring_path: &'a Path,
    run: &'a RunDirectory,
}

struct VerifiedIndexes {
    guest: Vec<AptIndexReceipt>,
    builder: Vec<AptIndexReceipt>,
    commands: Vec<CommandReceipt>,
}

struct VerifiedIndex {
    guest: AptIndexReceipt,
    builder: AptIndexReceipt,
    commands: Vec<CommandReceipt>,
}

fn reverify_locked_indexes(
    context: &IndexVerificationContext<'_, '_>,
) -> Result<VerifiedIndexes, Box<dyn Error>> {
    let mut indexes = BTreeMap::new();
    for index in &context.guest.indexes {
        indexes.insert(index_key(index), index);
    }
    for index in &context.builder.indexes {
        if indexes
            .insert(index_key(index), index)
            .is_some_and(|old| !same_index(old, index))
        {
            return Err(invalid("guest and builder APT index records differ"));
        }
    }
    if indexes.len() != 6 {
        return Err(invalid(
            "verified package locks must share exactly six indexes",
        ));
    }
    let xz = context
        .recipe
        .recipe()
        .ubuntu_build_builder
        .tools
        .iter()
        .find(|tool| tool.name == "xz")
        .ok_or_else(|| invalid("recipe omits the pinned xz tool"))?;
    let keyring_hash = sha256_file(context.keyring_path)?;
    let mut guest_receipts = Vec::with_capacity(6);
    let mut builder_receipts = Vec::with_capacity(6);
    let mut commands = Vec::new();
    for (number, index) in indexes.into_values().enumerate() {
        let verified = reverify_locked_index(context, index, number, xz, &keyring_hash)?;
        guest_receipts.push(verified.guest);
        builder_receipts.push(verified.builder);
        commands.extend(verified.commands);
    }
    Ok(VerifiedIndexes {
        guest: guest_receipts,
        builder: builder_receipts,
        commands,
    })
}

fn reverify_locked_index(
    context: &IndexVerificationContext<'_, '_>,
    index: &CandidateIndex,
    number: usize,
    xz: &crate::recipe::BuilderToolPin,
    keyring_hash: &str,
) -> Result<VerifiedIndex, Box<dyn Error>> {
    let inrelease_receipt = input_receipt(context.receipt, &format!("apt_{number:02}_inrelease"))?;
    let packages_receipt = input_receipt(context.receipt, &format!("apt_{number:02}_packages_xz"))?;
    let inrelease = context
        .staged
        .get(&inrelease_receipt.id)
        .ok_or_else(|| invalid("staged InRelease input is missing"))?;
    let packages = context
        .staged
        .get(&packages_receipt.id)
        .ok_or_else(|| invalid("staged Packages.xz input is missing"))?;
    let gpgv = builder_tool(context.recipe, "gpgv")?;
    let verified_signature = crate::signature::verify_signature_in_verified_local(
        context.podman,
        context.builder_image,
        SignatureRequest {
            keyring_path: context.keyring_path,
            keyring_source_path: &context.recipe.recipe().ubuntu_base.initial_keyring_path,
            keyring_sha256: keyring_hash,
            signed_path: inrelease,
            signed_sha256: &inrelease_receipt.sha256,
            detached_signature: None,
            expected_fingerprint: &context.recipe.recipe().guest_apt.signing_fingerprint,
            command_id: &format!("build_apt_{number:02}"),
        },
        &gpgv.path,
        &gpgv.sha256,
        context.run,
    )?;
    let index_pin = PackageIndexPin {
        snapshot_url: context.recipe.recipe().guest_apt.snapshot_url.clone(),
        suite: index.suite.clone(),
        component: index.component.clone(),
        architecture: index.architecture.clone(),
        trusted_keyring_sha256: keyring_hash.to_owned(),
        inrelease_sha256: index.inrelease_sha256.clone(),
        packages_index_sha256: index.packages_index_sha256.clone(),
        packages_index_size: index.packages_index_size_bytes,
    };
    let verified_index =
        verify_package_index(&index_pin, inrelease, packages, &verified_signature)?;
    let decompressed = crate::apt::decompress_package_index_with_verified_local(
        context.podman,
        context.builder_image,
        &xz.path,
        &xz.sha256,
        verified_index,
        packages,
        &format!("build_apt_{number:02}_xz"),
        context.run,
    )?;
    context.guest.verify_packages_from_index(&decompressed)?;
    context.builder.verify_packages_from_index(&decompressed)?;
    let base_receipt = AptIndexReceipt {
        suite: index.suite.clone(),
        component: index.component.clone(),
        architecture: index.architecture.clone(),
        inrelease_url: index.inrelease_url.clone(),
        inrelease_sha256: index.inrelease_sha256.clone(),
        inrelease_size_bytes: index.inrelease_size_bytes,
        packages_index_url: index.packages_index_url.clone(),
        packages_index_sha256: index.packages_index_sha256.clone(),
        packages_index_size_bytes: index.packages_index_size_bytes,
        trusted_key_source: String::new(),
        trusted_key_sha256: keyring_hash.to_owned(),
        gpgv_verified: true,
        signature: verified_signature.receipt().clone(),
    };
    let mut guest = base_receipt.clone();
    guest
        .trusted_key_source
        .clone_from(&context.recipe.recipe().guest_apt.initial_keyring_source);
    let mut builder = base_receipt;
    builder.trusted_key_source.clone_from(
        &context
            .recipe
            .recipe()
            .ubuntu_build_builder
            .initial_trust_source,
    );
    Ok(VerifiedIndex {
        guest,
        builder,
        commands: vec![
            verified_signature.command_receipt().clone(),
            decompressed.command_receipt().clone(),
        ],
    })
}

fn reverify_canonical_base(
    recipe: &RecipeFile,
    receipt: &FetchReceipt,
    staged: &BTreeMap<String, PathBuf>,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    keyring_path: &Path,
    run: &RunDirectory,
) -> Result<(crate::SignatureReceipt, CommandReceipt), Box<dyn Error>> {
    let sums_receipt = input_receipt(receipt, "ubuntu_sha256sums")?;
    let signature_receipt = input_receipt(receipt, "ubuntu_sha256sums_signature")?;
    let sums = staged
        .get(&sums_receipt.id)
        .ok_or_else(|| invalid("staged Ubuntu SHA256SUMS is missing"))?;
    let detached = staged
        .get(&signature_receipt.id)
        .ok_or_else(|| invalid("staged Ubuntu SHA256SUMS signature is missing"))?;
    let keyring_hash = sha256_file(keyring_path)?;
    let gpgv = builder_tool(recipe, "gpgv")?;
    let verified = crate::signature::verify_signature_in_verified_local(
        podman,
        builder_image,
        SignatureRequest {
            keyring_path,
            keyring_source_path: &recipe.recipe().ubuntu_base.initial_keyring_path,
            keyring_sha256: &keyring_hash,
            signed_path: sums,
            signed_sha256: &sums_receipt.sha256,
            detached_signature: Some((detached, &signature_receipt.sha256)),
            expected_fingerprint: &recipe.recipe().ubuntu_base.primary_signing_fingerprint,
            command_id: "build_ubuntu_base_sha256sums",
        },
        &gpgv.path,
        &gpgv.sha256,
        run,
    )?;
    verify_canonical_checksum(
        sums,
        "ubuntu-base-24.04.5-base-amd64.tar.gz",
        &recipe.recipe().ubuntu_base.sha256,
    )?;
    Ok((
        verified.receipt().clone(),
        verified.command_receipt().clone(),
    ))
}

fn input_receipt<'a>(
    receipt: &'a FetchReceipt,
    id: &str,
) -> Result<&'a crate::DownloadReceipt, Box<dyn Error>> {
    receipt
        .sources
        .iter()
        .find(|source| source.id == id)
        .ok_or_else(|| invalid(&format!("Fetch receipt omits source {id}")))
}

fn index_key(index: &CandidateIndex) -> (String, String, String) {
    (
        index.suite.clone(),
        index.component.clone(),
        index.architecture.clone(),
    )
}

fn same_index(left: &CandidateIndex, right: &CandidateIndex) -> bool {
    left.suite == right.suite
        && left.component == right.component
        && left.architecture == right.architecture
        && left.inrelease_url == right.inrelease_url
        && left.inrelease_sha256 == right.inrelease_sha256
        && left.inrelease_size_bytes == right.inrelease_size_bytes
        && left.packages_index_url == right.packages_index_url
        && left.packages_index_sha256 == right.packages_index_sha256
        && left.packages_index_size_bytes == right.packages_index_size_bytes
        && left.signer_primary_fingerprint == right.signer_primary_fingerprint
        && left.trusted_key_sha256 == right.trusted_key_sha256
        && left.gpgv_verified == right.gpgv_verified
}

fn verify_kernel_source_archive(
    recipe: &RecipeFile,
    receipt: &FetchReceipt,
    staged: &BTreeMap<String, PathBuf>,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let archive = staged
        .get(&receipt.kernel_source_archive_path)
        .ok_or_else(|| invalid("staged kernel source archive is missing"))?;
    if !sha256_file(archive)?.eq_ignore_ascii_case(&receipt.kernel_source_archive_sha256) {
        return Err(invalid(
            "kernel source archive hash differs from Fetch receipt",
        ));
    }
    let source_dir = run.path().join("kernel-source");
    fs::create_dir(&source_dir)?;
    let tar = builder_tool(recipe, "tar")?;
    let mut unpack_arguments = vec![
        "--extract".to_owned(),
        format!("--file=/work/{}", receipt.kernel_source_archive_path),
        "--directory=/work/kernel-source".to_owned(),
        "--no-same-owner".to_owned(),
        "--no-same-permissions".to_owned(),
    ];
    if receipt.kernel_source_kind == "kernel.org-signed-tarball" {
        unpack_arguments.push("--strip-components=1".to_owned());
    }
    let unpack = run_tool(
        podman,
        builder_image,
        run,
        ToolInvocation::new(
            "kernel_source_unpack",
            &tar.path,
            &unpack_arguments,
            &[],
            &[],
        ),
    )?;
    commands.push(unpack);

    if receipt.kernel_source_kind == "kernel.org-signed-tarball" {
        let makefile = fs::symlink_metadata(source_dir.join("Makefile"))?;
        if makefile.file_type().is_symlink() || !makefile.is_file() {
            return Err(invalid(
                "signed kernel release tar did not extract a regular root Makefile",
            ));
        }
        return Ok(());
    }

    let mut verification = KernelArchiveVerification {
        recipe,
        receipt,
        staged,
        podman,
        builder_image,
        run,
        commands,
    };
    verify_git_kernel_source(&mut verification)
}

struct KernelArchiveVerification<'a, 'run> {
    recipe: &'a RecipeFile,
    receipt: &'a FetchReceipt,
    staged: &'a BTreeMap<String, PathBuf>,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    run: &'a RunDirectory,
    commands: &'a mut Vec<CommandReceipt>,
}

fn verify_git_kernel_source(
    verification: &mut KernelArchiveVerification<'_, '_>,
) -> Result<(), Box<dyn Error>> {
    verify_kernel_tag_object(verification)?;
    verify_kernel_tag_contents(verification)?;
    verify_kernel_tag_commit_and_worktree(verification)?;
    Ok(())
}

fn verify_kernel_tag_object(
    verification: &mut KernelArchiveVerification<'_, '_>,
) -> Result<(), Box<dyn Error>> {
    let git = builder_tool(verification.recipe, "git")?;
    let tag_object = verification
        .receipt
        .kernel_tag_object
        .as_deref()
        .ok_or_else(|| invalid("Git kernel source receipt omits the annotated tag object"))?;
    let tag_ref = format!(
        "refs/tags/{}",
        verification.recipe.recipe().kernel.annotated_tag
    );
    let object_command = run_tool(
        verification.podman,
        verification.builder_image,
        verification.run,
        ToolInvocation::new(
            "build_kernel_tag_object",
            &git.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "rev-parse".to_owned(),
                "--verify".to_owned(),
                tag_ref,
            ],
            &[],
            &[],
        ),
    )?;
    if !read_command_stdout(verification.run, &object_command)?
        .trim()
        .eq_ignore_ascii_case(tag_object)
    {
        return Err(invalid(
            "archived kernel source tag object differs from Fetch receipt",
        ));
    }
    verification.commands.push(object_command);
    let type_command = run_tool(
        verification.podman,
        verification.builder_image,
        verification.run,
        ToolInvocation::new(
            "build_kernel_tag_type",
            &git.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "cat-file".to_owned(),
                "-t".to_owned(),
                tag_object.to_owned(),
            ],
            &[],
            &[],
        ),
    )?;
    if read_command_stdout(verification.run, &type_command)?.trim() != "tag" {
        return Err(invalid("kernel source pin is not an annotated Git tag"));
    }
    verification.commands.push(type_command);
    Ok(())
}

fn verify_kernel_tag_contents(
    verification: &mut KernelArchiveVerification<'_, '_>,
) -> Result<(), Box<dyn Error>> {
    let git = builder_tool(verification.recipe, "git")?;
    let tag_object = verification
        .receipt
        .kernel_tag_object
        .as_deref()
        .ok_or_else(|| invalid("Git kernel source receipt omits the annotated tag object"))?;
    let contents_command = run_tool(
        verification.podman,
        verification.builder_image,
        verification.run,
        ToolInvocation::new(
            "build_kernel_tag_contents",
            &git.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "cat-file".to_owned(),
                "tag".to_owned(),
                tag_object.to_owned(),
            ],
            &[],
            &[],
        ),
    )?;
    let observed_tag = fs::read(verification.run.path().join(&contents_command.stdout_path))?;
    let payload_name = verification
        .receipt
        .kernel_tag_payload_path
        .as_deref()
        .ok_or_else(|| invalid("Fetch receipt omits the kernel tag payload path"))?;
    let signature_name = verification
        .receipt
        .kernel_tag_signature_path
        .as_deref()
        .ok_or_else(|| invalid("Fetch receipt omits the kernel tag signature path"))?;
    let payload_path = verification
        .staged
        .get(payload_name)
        .ok_or_else(|| invalid("staged kernel tag payload is missing"))?;
    let signature_path = verification
        .staged
        .get(signature_name)
        .ok_or_else(|| invalid("staged kernel tag signature is missing"))?;
    let mut expected_tag = fs::read(payload_path)?;
    expected_tag.extend(fs::read(signature_path)?);
    if observed_tag != expected_tag {
        return Err(invalid(
            "kernel tag bytes differ from the signed payload and signature",
        ));
    }
    verification.commands.push(contents_command);
    Ok(())
}

fn verify_kernel_tag_commit_and_worktree(
    verification: &mut KernelArchiveVerification<'_, '_>,
) -> Result<(), Box<dyn Error>> {
    let git = builder_tool(verification.recipe, "git")?;
    let tag_ref = format!(
        "refs/tags/{}",
        verification.recipe.recipe().kernel.annotated_tag
    );
    let peeled_command = run_tool(
        verification.podman,
        verification.builder_image,
        verification.run,
        ToolInvocation::new(
            "build_kernel_peeled_commit",
            &git.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "rev-parse".to_owned(),
                format!("{tag_ref}^{{}}"),
            ],
            &[],
            &[],
        ),
    )?;
    if !read_command_stdout(verification.run, &peeled_command)?
        .trim()
        .eq_ignore_ascii_case(&verification.recipe.recipe().kernel.commit)
    {
        return Err(invalid("signed kernel tag peels to a different commit"));
    }
    verification.commands.push(peeled_command);
    let status_command = run_tool(
        verification.podman,
        verification.builder_image,
        verification.run,
        ToolInvocation::new(
            "build_kernel_worktree_status",
            &git.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "status".to_owned(),
                "--porcelain=v1".to_owned(),
                "--untracked-files=all".to_owned(),
            ],
            &[],
            &[],
        ),
    )?;
    if !read_command_stdout(verification.run, &status_command)?.is_empty() {
        return Err(invalid(
            "kernel checkout contains files outside the signed commit",
        ));
    }
    verification.commands.push(status_command);
    Ok(())
}

fn build_kernel(
    recipe: &RecipeFile,
    config: &Path,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let source_dir = run.path().join("kernel-source");
    let config_destination = source_dir.join(".config");
    if fs::symlink_metadata(&config_destination).is_ok() {
        return Err(invalid(
            "kernel source archive unexpectedly contains .config",
        ));
    }
    if !sha256_file(config)?.eq_ignore_ascii_case(&recipe.recipe().kernel.firecracker_config_sha256)
    {
        return Err(invalid(
            "Firecracker kernel config differs from its recipe hash",
        ));
    }
    fs::copy(config, &config_destination)?;

    let (source_epoch, timestamp) =
        kernel_build_identity(recipe, podman, builder_image, run, commands)?;
    run.write_new("kernel-source-epoch", source_epoch.to_string().as_bytes())?;
    run.write_new("kernel-build-timestamp", timestamp.as_bytes())?;
    let environment = kernel_environment(recipe, source_epoch, &timestamp);
    configure_kernel(
        recipe,
        config,
        podman,
        builder_image,
        run,
        &environment,
        commands,
    )?;
    record_kernel_release(recipe, podman, builder_image, run, &environment, commands)?;
    compile_kernel(recipe, podman, builder_image, run, &environment, commands)
}

fn kernel_build_identity(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(u64, String), Box<dyn Error>> {
    let identity = match recipe.recipe().kernel.source_variant()? {
        crate::recipe::KernelSourceVariant::SignedGitTag { commit, .. } => {
            let git = builder_tool(recipe, "git")?;
            let epoch_command = run_tool(
                podman,
                builder_image,
                run,
                ToolInvocation::new(
                    "kernel_source_epoch",
                    &git.path,
                    &[
                        "-C".to_owned(),
                        "/work/kernel-source".to_owned(),
                        "show".to_owned(),
                        "-s".to_owned(),
                        "--format=%ct".to_owned(),
                        commit.clone(),
                    ],
                    &[],
                    &[],
                ),
            )?;
            let source_epoch = read_command_stdout(run, &epoch_command)?
                .trim()
                .parse::<u64>()?;
            if source_epoch == 0 {
                return Err(invalid("kernel commit has an invalid source epoch"));
            }
            commands.push(epoch_command);
            let timestamp_command = run_tool(
                podman,
                builder_image,
                run,
                ToolInvocation::new(
                    "kernel_source_timestamp",
                    &git.path,
                    &[
                        "-C".to_owned(),
                        "/work/kernel-source".to_owned(),
                        "show".to_owned(),
                        "-s".to_owned(),
                        "--format=%cI".to_owned(),
                        commit,
                    ],
                    &[],
                    &[],
                ),
            )?;
            let timestamp = read_command_stdout(run, &timestamp_command)?
                .trim()
                .to_owned();
            commands.push(timestamp_command);
            (source_epoch, timestamp)
        }
        crate::recipe::KernelSourceVariant::SignedTarball {
            release_source_date_epoch,
            version,
            ..
        } => {
            let make = builder_tool(recipe, "make")?;
            let version_command = run_tool(
                podman,
                builder_image,
                run,
                ToolInvocation::new(
                    "kernel_source_version",
                    &make.path,
                    &[
                        "-s".to_owned(),
                        "-C".to_owned(),
                        "/work/kernel-source".to_owned(),
                        "kernelversion".to_owned(),
                    ],
                    &[],
                    &[],
                ),
            )?;
            let observed_version = read_command_stdout(run, &version_command)?
                .trim()
                .to_owned();
            if observed_version != version {
                return Err(invalid(
                    "kernel source tar version differs from its recipe pin",
                ));
            }
            commands.push(version_command);
            (
                release_source_date_epoch,
                recipe.recipe().kernel.release_source_date_utc.clone(),
            )
        }
    };
    let (source_epoch, timestamp) = identity;
    if source_epoch == 0
        || timestamp.len() < 20
        || (!timestamp.ends_with("+00:00") && !timestamp.ends_with('Z'))
    {
        return Err(invalid(
            "kernel source timestamp or SOURCE_DATE_EPOCH is not a pinned UTC identity",
        ));
    }
    Ok((source_epoch, timestamp))
}

fn configure_kernel(
    recipe: &RecipeFile,
    config: &Path,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    environment: &[(String, String)],
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let source_dir = run.path().join("kernel-source");
    let make = builder_tool(recipe, "make")?;
    let configure = run_tool(
        podman,
        builder_image,
        run,
        ToolInvocation::new(
            "kernel_olddefconfig",
            &make.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "-j4".to_owned(),
                "olddefconfig".to_owned(),
            ],
            &[],
            environment,
        ),
    )?;
    commands.push(configure);
    let final_config = source_dir.join(".config");
    let final_config_sha256 = sha256_file(&final_config)?;
    let config_is_tarball = recipe.recipe().kernel.source_kind == "kernel.org-signed-tarball";
    let expected_final_config_sha256 = if config_is_tarball {
        &recipe.recipe().kernel.custom_config_sha256
    } else {
        &recipe.recipe().kernel.firecracker_config_sha256
    };
    if !final_config_sha256.eq_ignore_ascii_case(expected_final_config_sha256) {
        return Err(invalid(
            "resolved kernel .config differs from its locked final config identity",
        ));
    }
    if config_is_tarball {
        let seed_config = fs::read(config)?;
        let resolved_config = fs::read(&final_config)?;
        let diff = kernel_config_diff(&seed_config, &resolved_config);
        let diff_sha256 = sha256_bytes(&diff);
        if !diff_sha256.eq_ignore_ascii_case(&recipe.recipe().kernel.custom_config_diff_sha256) {
            return Err(invalid(
                "resolved upstream kernel config diff differs from its reviewed pin",
            ));
        }
        run.write_new("kernel-config.diff", &diff)?;
    }
    validate_firecracker_config(&fs::read_to_string(&final_config)?)?;
    Ok(())
}

fn record_kernel_release(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    environment: &[(String, String)],
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let make = builder_tool(recipe, "make")?;
    let release_command = run_tool(
        podman,
        builder_image,
        run,
        ToolInvocation::new(
            "kernel_release",
            &make.path,
            &[
                "-s".to_owned(),
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "kernelrelease".to_owned(),
            ],
            &[],
            environment,
        ),
    )?;
    let release = read_command_stdout(run, &release_command)?
        .trim()
        .to_owned();
    if !release.starts_with(&recipe.recipe().kernel.expected_kernel_release_prefix) {
        return Err(invalid(
            "built kernel release differs from the recipe prefix",
        ));
    }
    run.write_new("kernel-release", release.as_bytes())?;
    commands.push(release_command);
    Ok(())
}

fn compile_kernel(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    environment: &[(String, String)],
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let source_dir = run.path().join("kernel-source");
    let make = builder_tool(recipe, "make")?;
    let compile = run_tool(
        podman,
        builder_image,
        run,
        ToolInvocation::new(
            "kernel_vmlinux_build",
            &make.path,
            &[
                "-C".to_owned(),
                "/work/kernel-source".to_owned(),
                "-j4".to_owned(),
                "vmlinux".to_owned(),
            ],
            &[],
            environment,
        ),
    )?;
    commands.push(compile);
    let built_kernel = source_dir.join("vmlinux");
    let metadata = fs::symlink_metadata(&built_kernel)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() == 0 {
        return Err(invalid(
            "kernel build did not produce a regular vmlinux image",
        ));
    }
    copy_verified_file(
        &built_kernel,
        "kernel.vmlinux",
        &sha256_file(&built_kernel)?,
        metadata.len(),
        run,
    )?;
    Ok(())
}

fn kernel_environment(
    recipe: &RecipeFile,
    source_epoch: u64,
    timestamp: &str,
) -> Vec<(String, String)> {
    let kernel = &recipe.recipe().kernel;
    vec![
        ("SOURCE_DATE_EPOCH".to_owned(), source_epoch.to_string()),
        ("KBUILD_BUILD_TIMESTAMP".to_owned(), timestamp.to_owned()),
        ("KBUILD_BUILD_USER".to_owned(), kernel.build_user.clone()),
        ("KBUILD_BUILD_HOST".to_owned(), kernel.build_host.clone()),
        (
            "KBUILD_BUILD_VERSION".to_owned(),
            kernel.build_version.clone(),
        ),
        ("LOCALVERSION".to_owned(), kernel.local_version.clone()),
        ("BUILD_SALT".to_owned(), kernel.build_salt.clone()),
        ("TZ".to_owned(), kernel.timezone.clone()),
        ("LANG".to_owned(), kernel.locale.clone()),
        ("LC_ALL".to_owned(), kernel.locale.clone()),
    ]
}

pub(crate) fn validate_firecracker_config(config: &str) -> Result<(), Box<dyn Error>> {
    const REQUIRED: [&str; 9] = [
        "CONFIG_VIRTIO=y",
        "CONFIG_VIRTIO_MMIO=y",
        "CONFIG_VIRTIO_BLK=y",
        "CONFIG_VIRTIO_NET=y",
        "CONFIG_EXT4_FS=y",
        "CONFIG_SERIAL_8250=y",
        "CONFIG_SERIAL_8250_CONSOLE=y",
        "CONFIG_DEVTMPFS=y",
        "CONFIG_BINFMT_ELF=y",
    ];
    if REQUIRED
        .iter()
        .any(|setting| !config.lines().any(|line| line == *setting))
    {
        return Err(invalid(
            "kernel config omits a required Firecracker boot setting",
        ));
    }
    Ok(())
}

pub(crate) fn kernel_config_diff(seed: &[u8], resolved: &[u8]) -> Vec<u8> {
    fn settings(bytes: &[u8]) -> BTreeMap<String, String> {
        String::from_utf8_lossy(bytes)
            .lines()
            .filter_map(|line| {
                let setting = line.strip_prefix("# ").unwrap_or(line);
                let key = setting
                    .strip_prefix("CONFIG_")
                    .and_then(|value| value.split(['=', ' ']).next())?;
                if line.starts_with("# CONFIG_") {
                    Some((key.to_owned(), "not-set".to_owned()))
                } else {
                    Some((key.to_owned(), setting.to_owned()))
                }
            })
            .collect()
    }

    let before = settings(seed);
    let after = settings(resolved);
    let keys = before.keys().chain(after.keys()).collect::<BTreeSet<_>>();
    let mut diff = String::new();
    for key in keys {
        let old = before.get(key).map_or("<absent>", String::as_str);
        let new = after.get(key).map_or("<absent>", String::as_str);
        if old != new {
            use std::fmt::Write as _;
            let _ = writeln!(&mut diff, "CONFIG_{key}: {old} -> {new}");
        }
    }
    diff.into_bytes()
}

pub(crate) fn sha256_bytes(bytes: &[u8]) -> String {
    let mut encoded = String::with_capacity(64);
    for byte in Sha256::digest(bytes) {
        use std::fmt::Write as _;
        write!(&mut encoded, "{byte:02x}").expect("writing into String cannot fail");
    }
    encoded
}

struct ToolInvocation<'a> {
    command_id: &'a str,
    executable: &'a str,
    arguments: &'a [String],
    capabilities: &'a [&'a str],
    environment: &'a [(String, String)],
}

impl<'a> ToolInvocation<'a> {
    fn new(
        command_id: &'a str,
        executable: &'a str,
        arguments: &'a [String],
        capabilities: &'a [&'a str],
        environment: &'a [(String, String)],
    ) -> Self {
        Self {
            command_id,
            executable,
            arguments,
            capabilities,
            environment,
        }
    }
}

fn run_tool(
    podman: &PodmanExecutor<'_>,
    image: &VerifiedLocalImage,
    run: &RunDirectory,
    invocation: ToolInvocation<'_>,
) -> Result<CommandReceipt, Box<dyn Error>> {
    let receipt = podman.run_container_with_verified_local(
        image,
        invocation.command_id,
        NetworkMode::None,
        invocation.executable,
        &[ContainerMount {
            host_path: run.path().to_path_buf(),
            container_path: "/work".to_owned(),
            read_only: false,
        }],
        &[],
        invocation.capabilities,
        invocation.environment,
        invocation.arguments,
    )?;
    if receipt.status_code != 0 {
        return Err(invalid(&format!(
            "pinned offline command {} exited with status {}",
            invocation.command_id, receipt.status_code
        )));
    }
    Ok(receipt)
}

fn builder_tool<'a>(
    recipe: &'a RecipeFile,
    name: &str,
) -> Result<&'a crate::recipe::BuilderToolPin, Box<dyn Error>> {
    recipe
        .recipe()
        .ubuntu_build_builder
        .tools
        .iter()
        .find(|tool| tool.name == name)
        .ok_or_else(|| invalid(&format!("recipe omits pinned builder tool {name}")))
}

struct RootfsBuildRequest<'a, 'run> {
    recipe: &'a RecipeFile,
    guest: &'a CandidatePackageLock,
    receipt: &'a FetchReceipt,
    staged: &'a BTreeMap<String, PathBuf>,
    base_archive: &'a Path,
    smoke_archive: &'a Path,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    run: &'a RunDirectory,
}

fn build_rootfs(
    request: RootfsBuildRequest<'_, '_>,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(Ext4Receipt, String, String, String), Box<dyn Error>> {
    let rootfs_path = request.run.path().join("rootfs");
    fs::create_dir(&rootfs_path)?;
    let release = prepare_guest_rootfs(&request, &rootfs_path, commands)?;
    let inventory_hash = write_rootfs_inventory(&request, &rootfs_path, commands)?;
    let filesystem = create_ext4_image(&request, &inventory_hash, commands)?;
    Ok((
        filesystem,
        "root-tree-inventory.json".to_owned(),
        inventory_hash,
        release,
    ))
}

fn prepare_guest_rootfs(
    request: &RootfsBuildRequest<'_, '_>,
    rootfs_path: &Path,
    commands: &mut Vec<CommandReceipt>,
) -> Result<String, Box<dyn Error>> {
    let tar = builder_tool(request.recipe, "tar")?;
    commands.push(run_tool(
        request.podman,
        request.builder_image,
        request.run,
        ToolInvocation::new(
            "ubuntu_base_extract",
            &tar.path,
            &[
                "--extract".to_owned(),
                "--gzip".to_owned(),
                format!(
                    "--file={}",
                    container_path(request.run, request.base_archive)?
                ),
                "--directory=/work/rootfs".to_owned(),
                "--numeric-owner".to_owned(),
            ],
            &["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"],
            &[],
        ),
    )?);

    let baseline_packages = read_installed_packages(rootfs_path)?;
    let expected_inventory = expected_guest_inventory(request.guest, &baseline_packages)?;
    let solver_inventory_sha256 = &request
        .guest
        .apt_solver_receipt
        .as_ref()
        .ok_or_else(|| invalid("guest lock omits its APT solver receipt"))?
        .selected_inventory_sha256;
    if !canonical_inventory_sha256(&expected_inventory)
        .eq_ignore_ascii_case(solver_inventory_sha256)
    {
        return Err(invalid(
            "Ubuntu Base inventory plus guest package lock differs from the APT solver receipt",
        ));
    }

    let mut package_paths = BTreeSet::new();
    for package in request
        .receipt
        .packages
        .iter()
        .filter(|package| package.role == "guest")
    {
        let path = request
            .staged
            .get(&package.path)
            .ok_or_else(|| invalid("staged guest .deb is missing"))?;
        if !sha256_file(path)?.eq_ignore_ascii_case(&package.sha256) {
            return Err(invalid("guest .deb hash changed after index verification"));
        }
        package_paths.insert(format!("/work/{}", package.path));
    }
    if package_paths.len() != request.guest.packages.len() {
        return Err(invalid(
            "guest package payload set differs from its locked closure",
        ));
    }
    let dpkg = builder_tool(request.recipe, "dpkg")?;
    let mut unpack_args = vec!["--root=/work/rootfs".to_owned(), "--unpack".to_owned()];
    unpack_args.extend(package_paths);
    commands.push(run_tool(
        request.podman,
        request.builder_image,
        request.run,
        ToolInvocation::new(
            "guest_packages_unpack",
            &dpkg.path,
            &unpack_args,
            &["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"],
            &[("DEBIAN_FRONTEND".to_owned(), "noninteractive".to_owned())],
        ),
    )?);
    commands.push(run_tool(
        request.podman,
        request.builder_image,
        request.run,
        ToolInvocation::new(
            "guest_packages_configure",
            &dpkg.path,
            &[
                "--root=/work/rootfs".to_owned(),
                "--configure".to_owned(),
                "--pending".to_owned(),
            ],
            &["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"],
            &[("DEBIAN_FRONTEND".to_owned(), "noninteractive".to_owned())],
        ),
    )?);
    commands.push(run_tool(
        request.podman,
        request.builder_image,
        request.run,
        ToolInvocation::new(
            "guest_packages_audit",
            &dpkg.path,
            &["--root=/work/rootfs".to_owned(), "--audit".to_owned()],
            &[],
            &[],
        ),
    )?);
    let installed_packages = read_installed_packages(rootfs_path)?;
    verify_exact_inventory(&expected_inventory, &installed_packages)?;
    parse_os_release(&rootfs_path.join("etc/os-release"))
}

fn write_rootfs_inventory(
    request: &RootfsBuildRequest<'_, '_>,
    rootfs_path: &Path,
    commands: &mut Vec<CommandReceipt>,
) -> Result<String, Box<dyn Error>> {
    normalize_rootfs(rootfs_path)?;
    let smoke_destination = rootfs_path.join("var/lib/terrabase/images/busybox.oci.tar");
    create_directory_chain(rootfs_path, Path::new("var/lib/terrabase/images"))?;
    copy_file_into_rootfs(
        request.smoke_archive,
        &smoke_destination,
        &request.receipt.offline_smoke_archive_sha256,
    )?;

    let inventory_command = run_tool(
        request.podman,
        request.builder_image,
        request.run,
        ToolInvocation::new(
            "root_tree_inventory",
            "/work/fc-image-builder",
            &[
                "inventory-rootfs".to_owned(),
                "--root".to_owned(),
                "/work/rootfs".to_owned(),
                "--output".to_owned(),
                "/work/root-tree-inventory.json".to_owned(),
            ],
            &[],
            &[],
        ),
    )?;
    commands.push(inventory_command);
    let inventory_path = request.run.path().join("root-tree-inventory.json");
    sha256_file(&inventory_path)
}

fn create_ext4_image(
    request: &RootfsBuildRequest<'_, '_>,
    inventory_hash: &str,
    commands: &mut Vec<CommandReceipt>,
) -> Result<Ext4Receipt, Box<dyn Error>> {
    let recipe = request.recipe;
    let run = request.run;
    let uuid = recipe.filesystem_uuid();
    let block_count =
        recipe.recipe().filesystem.size_bytes / u64::from(recipe.recipe().filesystem.block_size);
    if recipe.recipe().filesystem.size_bytes % u64::from(recipe.recipe().filesystem.block_size) != 0
    {
        return Err(invalid(
            "ext4 size is not an integer number of filesystem blocks",
        ));
    }
    let mke2fs = builder_tool(recipe, "mke2fs")?;
    commands.push(run_tool(
        request.podman,
        request.builder_image,
        run,
        ToolInvocation::new(
            "rootfs_mke2fs",
            &mke2fs.path,
            &[
                "-t".to_owned(),
                "ext4".to_owned(),
                "-F".to_owned(),
                "-b".to_owned(),
                recipe.recipe().filesystem.block_size.to_string(),
                "-I".to_owned(),
                recipe.recipe().filesystem.inode_size.to_string(),
                "-m".to_owned(),
                recipe.recipe().filesystem.reserved_percent.to_string(),
                "-L".to_owned(),
                recipe.recipe().filesystem.label.clone(),
                "-U".to_owned(),
                uuid.clone(),
                "-E".to_owned(),
                format!("hash_seed={uuid},root_owner=0:0,lazy_itable_init=0,lazy_journal_init=0"),
                "-d".to_owned(),
                "/work/rootfs".to_owned(),
                "/work/rootfs.ext4".to_owned(),
                block_count.to_string(),
            ],
            &[],
            &[(
                "E2FSPROGS_FAKE_TIME".to_owned(),
                source_epoch_from_run(run)?.to_string(),
            )],
        ),
    )?);
    let e2fsck = builder_tool(recipe, "e2fsck")?;
    let e2fsck_command = run_tool(
        request.podman,
        request.builder_image,
        run,
        ToolInvocation::new(
            "rootfs_e2fsck",
            &e2fsck.path,
            &[
                "-f".to_owned(),
                "-n".to_owned(),
                "/work/rootfs.ext4".to_owned(),
            ],
            &[],
            &[],
        ),
    )?;
    let e2fsck_exit_code = e2fsck_command.status_code;
    if e2fsck_exit_code != 0 {
        return Err(invalid("e2fsck reports that the ext4 image is not clean"));
    }
    commands.push(e2fsck_command);
    Ok(Ext4Receipt {
        size_bytes: recipe.recipe().filesystem.size_bytes,
        block_size: recipe.recipe().filesystem.block_size,
        inode_size: recipe.recipe().filesystem.inode_size,
        reserved_percent: recipe.recipe().filesystem.reserved_percent,
        label: recipe.recipe().filesystem.label.clone(),
        uuid: uuid.clone(),
        hash_seed: uuid,
        root_tree_inventory_sha256: inventory_hash.to_owned(),
        root_tree_inventory_path: "root-tree-inventory.json".to_owned(),
        e2fsck_exit_code,
        e2fsck_clean: true,
    })
}

fn container_path(run: &RunDirectory, path: &Path) -> Result<String, Box<dyn Error>> {
    let relative = path
        .strip_prefix(run.path())
        .map_err(|_| invalid("container input escaped its private run directory"))?;
    let text = relative
        .to_str()
        .ok_or_else(|| invalid("container path is not UTF-8"))?;
    if relative
        .components()
        .any(|part| !matches!(part, Component::Normal(_)))
    {
        return Err(invalid("container input path contains traversal"));
    }
    Ok(format!("/work/{}", text.replace('\\', "/")))
}

fn read_installed_packages(rootfs: &Path) -> Result<Vec<InstalledPackage>, Box<dyn Error>> {
    let status = rootfs.join("var/lib/dpkg/status");
    let metadata = fs::symlink_metadata(&status)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid("guest dpkg status is not a regular file"));
    }
    let text = fs::read_to_string(status)?;
    let mut installed = Vec::new();
    for stanza in text.split("\n\n") {
        let mut package = None;
        let mut version = None;
        let mut architecture = None;
        let mut state = None;
        for line in stanza.lines() {
            if let Some(value) = line.strip_prefix("Package: ") {
                package = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Version: ") {
                version = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Architecture: ") {
                architecture = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Status: ") {
                state = Some(value.to_owned());
            }
        }
        if state.as_deref() == Some("install ok installed") {
            installed.push(InstalledPackage {
                name: package.ok_or_else(|| invalid("dpkg status stanza omits Package"))?,
                version: version.ok_or_else(|| invalid("dpkg status stanza omits Version"))?,
                architecture: architecture
                    .ok_or_else(|| invalid("dpkg status stanza omits Architecture"))?,
            });
        } else if state.as_deref().is_some_and(|value| {
            value.starts_with("install ") && value != "install ok config-files"
        }) {
            return Err(invalid(
                "guest rootfs contains a partially installed package",
            ));
        }
    }
    installed.sort_by(|left, right| {
        (&left.name, &left.architecture).cmp(&(&right.name, &right.architecture))
    });
    Ok(installed)
}

fn inventory_map(
    installed: &[InstalledPackage],
) -> Result<BTreeMap<(String, String), String>, Box<dyn Error>> {
    let mut inventory = BTreeMap::new();
    for package in installed {
        if inventory
            .insert(
                (package.name.clone(), package.architecture.clone()),
                package.version.clone(),
            )
            .is_some()
        {
            return Err(invalid("guest dpkg inventory repeats a package identity"));
        }
    }
    Ok(inventory)
}

fn expected_guest_inventory(
    lock: &CandidatePackageLock,
    baseline: &[InstalledPackage],
) -> Result<BTreeMap<(String, String), String>, Box<dyn Error>> {
    let mut expected = inventory_map(baseline)?;
    for package in &lock.packages {
        expected.insert(
            (package.name.clone(), package.architecture.clone()),
            package.version.clone(),
        );
    }
    Ok(expected)
}

fn verify_exact_inventory(
    expected: &BTreeMap<(String, String), String>,
    installed: &[InstalledPackage],
) -> Result<(), Box<dyn Error>> {
    if inventory_map(installed)? != *expected {
        return Err(invalid(
            "installed guest dpkg inventory differs from Ubuntu Base plus the locked closure",
        ));
    }
    Ok(())
}

fn verify_selected_inventory(
    lock: &CandidatePackageLock,
    installed: &[InstalledPackage],
) -> Result<(), Box<dyn Error>> {
    let solver_inventory_sha256 = &lock
        .apt_solver_receipt
        .as_ref()
        .ok_or_else(|| invalid("guest lock omits its APT solver receipt"))?
        .selected_inventory_sha256;
    if !canonical_inventory_sha256(&inventory_map(installed)?)
        .eq_ignore_ascii_case(solver_inventory_sha256)
    {
        return Err(invalid(
            "guest dpkg inventory differs from the complete APT solver selection",
        ));
    }
    Ok(())
}

fn verify_root_tree_inventory(
    path: &Path,
    expected_sha256: &str,
    guest: &CandidatePackageLock,
) -> Result<(), Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > 256 * 1024 * 1024
    {
        return Err(invalid(
            "root-tree inventory must be a bounded regular file",
        ));
    }
    if !sha256_file(path)?.eq_ignore_ascii_case(expected_sha256) {
        return Err(invalid(
            "root-tree inventory hash changed before manifest creation",
        ));
    }
    let inventory: RootTreeInventory = serde_json::from_slice(&fs::read(path)?)?;
    if inventory.schema_version != 1
        || inventory
            .entries
            .windows(2)
            .any(|entries| entries[0].path >= entries[1].path)
        || inventory.installed_packages.windows(2).any(|packages| {
            (&packages[0].name, &packages[0].architecture)
                >= (&packages[1].name, &packages[1].architecture)
        })
    {
        return Err(invalid("root-tree inventory schema or ordering is invalid"));
    }
    for entry in &inventory.entries {
        let relative = Path::new(&entry.path);
        if entry.path.is_empty()
            || relative.is_absolute()
            || relative
                .components()
                .any(|component| !matches!(component, Component::Normal(_)))
            || !matches!(entry.kind.as_str(), "directory" | "file" | "symlink")
            || (entry.kind == "file") != entry.sha256.is_some()
            || (entry.kind == "symlink") != entry.symlink_target.is_some()
        {
            return Err(invalid("root-tree inventory contains an invalid entry"));
        }
        if let Some(hash) = &entry.sha256 {
            validate_sha256_text(hash, "root-tree file")?;
        }
    }
    verify_selected_inventory(guest, &inventory.installed_packages)
}

fn validate_sha256_text(value: &str, purpose: &str) -> Result<(), Box<dyn Error>> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(invalid(&format!("{purpose} SHA-256 is invalid")));
    }
    Ok(())
}

fn parse_os_release(path: &Path) -> Result<String, Box<dyn Error>> {
    let text = fs::read_to_string(path)?;
    let version = text
        .lines()
        .find_map(|line| {
            line.strip_prefix("VERSION_ID=")
                .map(|value| value.trim_matches('"'))
        })
        .ok_or_else(|| invalid("Ubuntu rootfs os-release omits VERSION_ID"))?;
    if version != "24.04" {
        return Err(invalid(
            "Ubuntu rootfs release differs from the pinned Noble base",
        ));
    }
    Ok(version.to_owned())
}

fn normalize_rootfs(rootfs: &Path) -> Result<(), Box<dyn Error>> {
    let machine_id = rootfs.join("etc/machine-id");
    if fs::symlink_metadata(&machine_id).is_ok() {
        remove_regular_file(&machine_id)?;
    }
    let mut machine = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&machine_id)?;
    machine.write_all(b"")?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&machine_id, fs::Permissions::from_mode(0o644))?;
    }
    for relative in [
        "var/lib/systemd/random-seed",
        "var/lib/apt/lists/lock",
        "var/cache/apt/archives/lock",
    ] {
        let path = rootfs.join(relative);
        if fs::symlink_metadata(&path).is_ok() {
            remove_regular_file(&path)?;
        }
    }
    for relative in ["var/lib/apt/lists", "var/cache/apt/archives", "var/log"] {
        let directory = rootfs.join(relative);
        if fs::symlink_metadata(&directory).is_ok() {
            clear_regular_children(rootfs, &directory)?;
        }
    }
    Ok(())
}

fn clear_regular_children(rootfs: &Path, directory: &Path) -> Result<(), Box<dyn Error>> {
    let metadata = fs::symlink_metadata(directory)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid(
            "normalization directory is not a regular directory",
        ));
    }
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.is_dir() && !metadata.file_type().is_symlink() {
            clear_regular_children(rootfs, &path)?;
        } else if metadata.file_type().is_symlink() {
            return Err(invalid("volatile normalization path contains a symlink"));
        } else if metadata.is_file() {
            remove_regular_file(&path)?;
        } else {
            return Err(invalid(
                "volatile normalization path contains a special file",
            ));
        }
    }
    let _ = rootfs;
    Ok(())
}

fn remove_regular_file(path: &Path) -> Result<(), Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid(
            "normalization refuses to remove a non-regular file",
        ));
    }
    fs::remove_file(path)?;
    Ok(())
}

fn create_directory_chain(rootfs: &Path, relative: &Path) -> Result<PathBuf, Box<dyn Error>> {
    let mut current = rootfs.to_path_buf();
    for component in relative.components() {
        let Component::Normal(name) = component else {
            return Err(invalid("rootfs directory path is not traversal-free"));
        };
        current.push(name);
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => {
                return Err(invalid(
                    "rootfs destination path has a non-directory component",
                ));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                fs::create_dir(&current)?;
            }
            Err(error) => return Err(error.into()),
        }
    }
    Ok(current)
}

fn copy_file_into_rootfs(
    source: &Path,
    destination: &Path,
    expected_hash: &str,
) -> Result<(), Box<dyn Error>> {
    if fs::symlink_metadata(destination).is_ok() {
        return Err(invalid("rootfs OCI archive destination already exists"));
    }
    let metadata = fs::symlink_metadata(source)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid("rootfs OCI archive source is not a regular file"));
    }
    fs::copy(source, destination)?;
    if !sha256_file(destination)?.eq_ignore_ascii_case(expected_hash) {
        return Err(invalid("rootfs OCI archive copy hash changed"));
    }
    Ok(())
}

fn copy_current_builder_into_run(
    recipe: &RecipeFile,
    run: &RunDirectory,
) -> Result<PathBuf, Box<dyn Error>> {
    let executable = std::env::current_exe()?;
    let expected = &recipe.recipe().rust_cli_builder.output_executable_sha256;
    if !sha256_file(&executable)?.eq_ignore_ascii_case(expected) {
        return Err(invalid(
            "running fc-image-builder executable differs from recipe pin",
        ));
    }
    let metadata = fs::symlink_metadata(&executable)?;
    let path = copy_verified_file(
        &executable,
        "fc-image-builder",
        expected,
        metadata.len(),
        run,
    )?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&path, fs::Permissions::from_mode(0o755))?;
    }
    Ok(path)
}

fn write_rust_cli_build_inputs(
    recipe: &RecipeFile,
    run: &RunDirectory,
) -> Result<String, Box<dyn Error>> {
    let rust_cli = &recipe.recipe().rust_cli_builder;
    for (source_relative, output_name, expected_sha256) in [
        (
            rust_cli.cargo_lock_path.as_str(),
            "rust-cli-Cargo.lock",
            rust_cli.cargo_lock_sha256.as_str(),
        ),
        (
            rust_cli.rust_toolchain_path.as_str(),
            "rust-cli-rust-toolchain.toml",
            rust_cli.rust_toolchain_sha256.as_str(),
        ),
    ] {
        let source = recipe.resolve_workspace_input(source_relative)?;
        let metadata = fs::symlink_metadata(&source)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(invalid(
                "pinned Rust CLI build input must be a regular file",
            ));
        }
        copy_verified_file(&source, output_name, expected_sha256, metadata.len(), run)?;
    }
    let executable = std::env::current_exe()?;
    let host_abi = verify_static_x86_64_elf(&executable)?;
    copy_current_builder_into_run(recipe, run)?;
    Ok(host_abi)
}

pub(crate) fn verify_static_x86_64_elf(path: &Path) -> Result<String, Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() < 64 {
        return Err(invalid(
            "running image-builder executable is not a complete regular ELF file",
        ));
    }
    let bytes = fs::read(path)?;
    if bytes.get(..6) != Some(b"\x7fELF\x02\x01")
        || bytes.get(18..20) != Some(&62_u16.to_le_bytes())
    {
        return Err(invalid(
            "running image-builder must be a little-endian x86_64 ELF",
        ));
    }
    let program_header_offset = u64::from_le_bytes(
        bytes
            .get(32..40)
            .ok_or_else(|| invalid("ELF program-header offset is truncated"))?
            .try_into()?,
    );
    let program_header_size = u16::from_le_bytes(
        bytes
            .get(54..56)
            .ok_or_else(|| invalid("ELF program-header size is truncated"))?
            .try_into()?,
    );
    let program_header_count = u16::from_le_bytes(
        bytes
            .get(56..58)
            .ok_or_else(|| invalid("ELF program-header count is truncated"))?
            .try_into()?,
    );
    if program_header_size < 56 {
        return Err(invalid("ELF program-header entry size is invalid"));
    }
    for index in 0..u64::from(program_header_count) {
        let start = program_header_offset
            .checked_add(index * u64::from(program_header_size))
            .ok_or_else(|| invalid("ELF program-header offset overflows"))?;
        let start = usize::try_from(start)?;
        let entry = bytes
            .get(start..start + usize::from(program_header_size))
            .ok_or_else(|| invalid("ELF program-header table is truncated"))?;
        if u32::from_le_bytes(entry[..4].try_into()?) == 3 {
            return Err(invalid(
                "running image-builder ELF requires a dynamic interpreter",
            ));
        }
    }
    Ok("x86_64-linux-musl-static".to_owned())
}

fn output_receipt(run: &RunDirectory, name: &str) -> Result<ArtifactOutput, Box<dyn Error>> {
    let path = run.path().join(name);
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() == 0 {
        return Err(invalid("build output is not a non-empty regular file"));
    }
    Ok(ArtifactOutput {
        path: name.to_owned(),
        size_bytes: metadata.len(),
        sha256: sha256_file(&path)?,
    })
}

fn write_locked_package_files(
    recipe: &RecipeFile,
    _guest: &CandidatePackageLock,
    _builder: &CandidatePackageLock,
    run: &RunDirectory,
) -> Result<(String, String, String, String), Box<dyn Error>> {
    let guest_source = recipe.resolve_recipe_path(&recipe.recipe().guest_apt.packages_lock_path)?;
    let builder_source =
        recipe.resolve_recipe_path(&recipe.recipe().ubuntu_build_builder.packages_lock_path)?;
    let guest_bytes = fs::read(guest_source)?;
    let builder_bytes = fs::read(builder_source)?;
    let guest_hash = digest_hex(&Sha256::digest(&guest_bytes));
    let builder_hash = digest_hex(&Sha256::digest(&builder_bytes));
    if !guest_hash.eq_ignore_ascii_case(&recipe.recipe().guest_apt.packages_lock_sha256)
        || !builder_hash
            .eq_ignore_ascii_case(&recipe.recipe().ubuntu_build_builder.packages_lock_sha256)
    {
        return Err(invalid(
            "package lock changed while preparing the image receipt",
        ));
    }
    run.write_new("packages.lock.json", &guest_bytes)?;
    run.write_new("builder-packages.lock.json", &builder_bytes)?;
    Ok((
        "packages.lock.json".to_owned(),
        guest_hash,
        "builder-packages.lock.json".to_owned(),
        builder_hash,
    ))
}

fn write_apt_settings(
    recipe: &RecipeFile,
    role: &str,
    run: &RunDirectory,
) -> Result<(String, String), Box<dyn Error>> {
    let (
        snapshot_url,
        architecture,
        suites,
        components,
        allow_unauthenticated,
        install_recommends,
        install_suggests,
        check_valid_until,
    ) = if role == "guest" {
        let apt = &recipe.recipe().guest_apt;
        (
            apt.snapshot_url.as_str(),
            apt.architecture.as_str(),
            apt.suites.as_slice(),
            apt.components.as_slice(),
            apt.allow_unauthenticated,
            apt.install_recommends,
            apt.install_suggests,
            apt.check_valid_until,
        )
    } else if role == "builder" {
        let apt = &recipe.recipe().ubuntu_build_builder;
        (
            apt.snapshot_url.as_str(),
            apt.architecture.as_str(),
            apt.suites.as_slice(),
            apt.components.as_slice(),
            apt.allow_unauthenticated,
            apt.install_recommends,
            apt.install_suggests,
            apt.check_valid_until,
        )
    } else {
        return Err(invalid("APT settings role must be guest or builder"));
    };
    use std::fmt::Write as _;
    let mut text = String::new();
    for suite in suites {
        writeln!(
            &mut text,
            "deb [arch={} check-valid-until={}] {} {} {}",
            architecture,
            if check_valid_until { "yes" } else { "no" },
            snapshot_url,
            suite,
            components.join(" ")
        )
        .expect("writing into String cannot fail");
    }
    write!(
        &mut text,
        "APT::Get::AllowUnauthenticated \"{}\";\n",
        if allow_unauthenticated {
            "true"
        } else {
            "false"
        }
    )
    .expect("writing into String cannot fail");
    writeln!(
        &mut text,
        "APT::Install-Recommends \"{}\";",
        if install_recommends { "true" } else { "false" }
    )
    .expect("writing into String cannot fail");
    writeln!(
        &mut text,
        "APT::Install-Suggests \"{}\";",
        if install_suggests { "true" } else { "false" }
    )
    .expect("writing into String cannot fail");
    writeln!(
        &mut text,
        "Acquire::Check-Valid-Until \"{}\";",
        if check_valid_until { "true" } else { "false" }
    )
    .expect("writing into String cannot fail");
    let name = format!("{role}-apt-settings.conf");
    let path = run.write_new(&name, text.as_bytes())?;
    Ok((name, sha256_file(&path)?))
}

struct AptReceiptInput<'a> {
    recipe: &'a RecipeFile,
    lock: &'a CandidatePackageLock,
    indexes: Vec<AptIndexReceipt>,
    solver: &'a AptSolverReceipt,
    package_lock_path: &'a str,
    package_lock_sha256: &'a str,
    settings_path: &'a str,
    settings_sha256: &'a str,
}

fn apt_receipt(input: AptReceiptInput<'_>) -> AptReceipt {
    let AptReceiptInput {
        recipe,
        lock,
        indexes,
        solver,
        package_lock_path,
        package_lock_sha256,
        settings_path,
        settings_sha256,
    } = input;
    let (
        snapshot_url,
        snapshot_timestamp,
        architecture,
        suites,
        components,
        allow_unauthenticated,
        install_recommends,
        install_suggests,
        check_valid_until,
    ) = if lock.package_role == "guest" {
        let apt = &recipe.recipe().guest_apt;
        (
            &apt.snapshot_url,
            &apt.snapshot_timestamp,
            &apt.architecture,
            &apt.suites,
            &apt.components,
            apt.allow_unauthenticated,
            apt.install_recommends,
            apt.install_suggests,
            apt.check_valid_until,
        )
    } else {
        let apt = &recipe.recipe().ubuntu_build_builder;
        (
            &apt.snapshot_url,
            &apt.snapshot_timestamp,
            &apt.architecture,
            &apt.suites,
            &apt.components,
            apt.allow_unauthenticated,
            apt.install_recommends,
            apt.install_suggests,
            apt.check_valid_until,
        )
    };
    AptReceipt {
        role: lock.package_role.clone(),
        snapshot_url: snapshot_url.clone(),
        snapshot_timestamp: snapshot_timestamp.clone(),
        architecture: architecture.clone(),
        suites: suites.clone(),
        components: components.clone(),
        apt_version: lock.apt_version.clone(),
        apt_sha256: lock.apt_sha256.clone(),
        allow_unauthenticated,
        install_recommends,
        install_suggests,
        check_valid_until,
        settings_path: settings_path.to_owned(),
        settings_sha256: settings_sha256.to_owned(),
        keyring_sha256: recipe.recipe().ubuntu_base.initial_keyring_sha256.clone(),
        initial_keyring_source: if lock.package_role == "guest" {
            recipe.recipe().guest_apt.initial_keyring_source.clone()
        } else {
            recipe
                .recipe()
                .ubuntu_build_builder
                .initial_trust_source
                .clone()
        },
        initial_keyring_path: recipe.recipe().ubuntu_base.initial_keyring_path.clone(),
        initial_keyring_sha256: recipe.recipe().ubuntu_base.initial_keyring_sha256.clone(),
        initial_gpgv_path: recipe.recipe().ubuntu_base.initial_verifier_path.clone(),
        initial_gpgv_sha256: recipe.recipe().ubuntu_base.initial_verifier_sha256.clone(),
        signer_primary_fingerprint: recipe.recipe().guest_apt.signing_fingerprint.clone(),
        solver_command_argv: solver.command_argv.clone(),
        solver_stdout_sha256: solver.stdout_sha256.clone(),
        solver_stderr_sha256: solver.stderr_sha256.clone(),
        solver_exit_status: solver.exit_status,
        selected_inventory_sha256: solver.selected_inventory_sha256.clone(),
        package_lock_path: package_lock_path.to_owned(),
        package_lock_sha256: package_lock_sha256.to_owned(),
        indexes,
        packages: lock
            .packages
            .iter()
            .map(|package| AptPackageReceipt {
                name: package.name.clone(),
                version: package.version.clone(),
                architecture: package.architecture.clone(),
                component: package.component.clone(),
                suite: package.suite.clone(),
                source_url: package.source_url.clone(),
                size_bytes: package.size_bytes,
                deb_sha256: package.sha256.clone(),
            })
            .collect(),
    }
}

fn source_epoch_from_run(run: &RunDirectory) -> Result<u64, Box<dyn Error>> {
    Ok(read_file_trimmed(&run.path().join("kernel-source-epoch"))?.parse()?)
}

fn read_file_trimmed(path: &Path) -> Result<String, Box<dyn Error>> {
    Ok(fs::read_to_string(path)?.trim().to_owned())
}

fn digest_hex(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut result, "{byte:02x}").expect("writing into String cannot fail");
    }
    result
}

struct BenchSourceGitContext<'a, 'run> {
    repo_root: &'a PathBuf,
    git_path: &'a str,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    run: &'a RunDirectory,
}

fn run_bench_source_git(
    context: &BenchSourceGitContext<'_, '_>,
    command_id: &str,
    tail: &[&str],
) -> Result<CommandReceipt, Box<dyn Error>> {
    let mut arguments = vec!["-C".to_owned(), "/source".to_owned()];
    arguments.extend(tail.iter().map(|value| (*value).to_owned()));
    context
        .podman
        .run_container_with_verified_local(
            context.builder_image,
            command_id,
            NetworkMode::None,
            context.git_path,
            &[
                ContainerMount {
                    host_path: context.run.path().to_path_buf(),
                    container_path: "/work".to_owned(),
                    read_only: false,
                },
                ContainerMount {
                    host_path: context.repo_root.to_path_buf(),
                    container_path: "/source".to_owned(),
                    read_only: true,
                },
            ],
            std::slice::from_ref(context.repo_root),
            &[],
            &[],
            &arguments,
        )
        .map_err(Into::into)
}

fn bench_source_receipt(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<BenchSourceReceipt, Box<dyn Error>> {
    let repo_root = recipe_repository_root(recipe)?;
    let git = builder_tool(recipe, "git")?;
    let context = BenchSourceGitContext {
        repo_root: &repo_root,
        git_path: &git.path,
        podman,
        builder_image,
        run,
    };
    let head_command = run_bench_source_git(&context, "bench_source_head", &["rev-parse", "HEAD"])?;
    ensure_command_success(&head_command, "read benchmark source HEAD")?;
    let head = read_command_stdout(run, &head_command)?.trim().to_owned();
    commands.push(head_command);

    let diff_command = run_bench_source_git(
        &context,
        "bench_source_diff",
        &["diff", "--binary", "--no-ext-diff", "HEAD", "--"],
    )?;
    ensure_command_success(&diff_command, "record benchmark source diff")?;
    let diff_path = run.path().join(&diff_command.stdout_path);
    let diff_bytes = fs::read(diff_path)?;
    commands.push(diff_command.clone());

    let untracked_command = run_bench_source_git(
        &context,
        "bench_source_untracked",
        &["ls-files", "--others", "--exclude-standard", "-z"],
    )?;
    ensure_command_success(&untracked_command, "record untracked benchmark sources")?;
    let untracked_text = read_command_stdout(run, &untracked_command)?;
    let untracked_files = untracked_source_files(&repo_root, &untracked_text)?;
    commands.push(untracked_command);
    let dirty = !diff_bytes.is_empty() || !untracked_files.is_empty();
    let diff_sha256 = dirty.then(|| {
        let mut digest = Sha256::new();
        digest.update(&diff_bytes);
        for file in &untracked_files {
            digest.update(file.path.as_bytes());
            digest.update([0]);
            digest.update(file.sha256.as_bytes());
            digest.update(*b"\n");
        }
        digest_hex(&digest.finalize())
    });
    Ok(BenchSourceReceipt {
        head,
        dirty,
        diff_sha256,
        untracked_files,
    })
}

fn untracked_source_files(
    repo_root: &Path,
    untracked_text: &str,
) -> Result<Vec<SourceFileReceipt>, Box<dyn Error>> {
    let mut files = Vec::new();
    for relative in untracked_text.split('\0').filter(|name| !name.is_empty()) {
        let path = confined_relative_path(repo_root, relative)?;
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(invalid(
                "untracked benchmark sources must be regular non-symlink files",
            ));
        }
        files.push(SourceFileReceipt {
            path: relative.to_owned(),
            sha256: sha256_file(&path)?,
        });
    }
    files.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(files)
}

fn recipe_repository_root(recipe: &RecipeFile) -> Result<PathBuf, Box<dyn Error>> {
    let mut current = recipe.path();
    for _ in 0..4 {
        current = current
            .parent()
            .ok_or_else(|| invalid("recipe path does not have the expected repository depth"))?;
    }
    let metadata = fs::symlink_metadata(current)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid(
            "benchmark repository root is not a regular directory",
        ));
    }
    let git_metadata = fs::symlink_metadata(current.join(".git"))?;
    if git_metadata.file_type().is_symlink() || !(git_metadata.is_dir() || git_metadata.is_file()) {
        return Err(invalid("benchmark repository metadata path is unsafe"));
    }
    Ok(fs::canonicalize(current)?)
}

fn confined_relative_path(root: &Path, relative: &str) -> Result<PathBuf, Box<dyn Error>> {
    let path = Path::new(relative);
    if path.is_absolute()
        || path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(invalid("repository relative path contains traversal"));
    }
    let mut current = root.to_path_buf();
    for component in path.components() {
        let Component::Normal(name) = component else {
            return Err(invalid("repository relative path contains traversal"));
        };
        current.push(name);
        let metadata = fs::symlink_metadata(&current)?;
        if metadata.file_type().is_symlink() {
            return Err(invalid("repository relative path crosses a symbolic link"));
        }
    }
    if !current.starts_with(root) {
        return Err(invalid("repository relative path escaped the checkout"));
    }
    Ok(current)
}

fn ensure_command_success(command: &CommandReceipt, purpose: &str) -> Result<(), Box<dyn Error>> {
    if command.status_code != 0 {
        return Err(invalid(&format!("pinned Git command failed to {purpose}")));
    }
    Ok(())
}

/// Inventory a root tree from inside the pinned build container and create a
/// new JSON sidecar at the requested output path.
///
/// # Errors
///
/// Returns an error for unsafe roots/output paths, special files, or any read
/// and serialization failure.
pub fn write_root_tree_inventory(root: &Path, output: &Path) -> Result<(), Box<dyn Error>> {
    let root_metadata = fs::symlink_metadata(root)?;
    if root_metadata.file_type().is_symlink() || !root_metadata.is_dir() {
        return Err(invalid(
            "root-tree inventory root must be a regular directory",
        ));
    }
    let root = fs::canonicalize(root)?;
    let parent = output
        .parent()
        .ok_or_else(|| invalid("root-tree inventory output has no parent"))?;
    let parent_metadata = fs::symlink_metadata(parent)?;
    if parent_metadata.file_type().is_symlink() || !parent_metadata.is_dir() {
        return Err(invalid(
            "root-tree inventory output parent must be a regular directory",
        ));
    }
    let canonical_parent = fs::canonicalize(parent)?;
    if Some(canonical_parent.as_path()) != root.parent() {
        return Err(invalid(
            "root-tree inventory output must be beside the root tree",
        ));
    }
    if output.file_name().is_none_or(|name| {
        !matches!(
            Path::new(name).components().next(),
            Some(Component::Normal(_))
        ) || Path::new(name).components().count() != 1
    }) {
        return Err(invalid(
            "root-tree inventory output must be a single filename",
        ));
    }
    if fs::symlink_metadata(output).is_ok() {
        return Err(invalid("root-tree inventory output already exists"));
    }
    let entries = collect_root_tree_entries(&root)?;
    let installed_packages = read_installed_packages(&root)?;
    let bytes = serde_json::to_vec_pretty(&RootTreeInventory {
        schema_version: 1,
        entries,
        installed_packages,
    })?;
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(output)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    Ok(())
}

fn collect_root_tree_entries(root: &Path) -> Result<Vec<RootTreeEntry>, Box<dyn Error>> {
    let mut entries = Vec::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let mut children = fs::read_dir(&directory)?.collect::<Result<Vec<_>, _>>()?;
        children.sort_by_key(std::fs::DirEntry::file_name);
        for child in children {
            let path = child.path();
            let relative = path
                .strip_prefix(&root)
                .map_err(|_| invalid("root-tree path escaped its root"))?;
            let relative_text = relative
                .to_str()
                .ok_or_else(|| invalid("root-tree path is not UTF-8"))?
                .replace('\\', "/");
            let metadata = fs::symlink_metadata(&path)?;
            let kind = if metadata.is_dir() && !metadata.file_type().is_symlink() {
                pending.push(path.clone());
                "directory"
            } else if metadata.is_file() {
                "file"
            } else if metadata.file_type().is_symlink() {
                "symlink"
            } else {
                return Err(invalid("root tree contains a special file"));
            };
            let target = if kind == "symlink" {
                Some(
                    fs::read_link(&path)?
                        .to_str()
                        .ok_or_else(|| invalid("root-tree symlink target is not UTF-8"))?
                        .to_owned(),
                )
            } else {
                None
            };
            let file_hash = (kind == "file").then(|| sha256_file(&path)).transpose()?;
            #[cfg(unix)]
            let (mode, uid, gid, hardlink_group) = {
                use std::os::unix::fs::MetadataExt;
                let hardlink = (metadata.nlink() > 1)
                    .then(|| format!("{}:{}", metadata.dev(), metadata.ino()));
                (metadata.mode(), metadata.uid(), metadata.gid(), hardlink)
            };
            #[cfg(not(unix))]
            let (mode, uid, gid, hardlink_group) = (0, 0, 0, None);
            entries.push(RootTreeEntry {
                path: relative_text,
                kind: kind.to_owned(),
                mode,
                uid,
                gid,
                size_bytes: metadata.len(),
                hardlink_group,
                symlink_target: target,
                sha256: file_hash,
            });
        }
    }
    entries.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(entries)
}

#[cfg(test)]
mod tests {
    use super::{InstalledPackage, expected_guest_inventory, verify_exact_inventory};
    use crate::{CandidatePackageLock, RecipeFile};
    use std::fs;
    use std::path::Path;
    use std::time::{SystemTime, UNIX_EPOCH};

    const CANDIDATE_RECIPE: &[u8] =
        include_bytes!("../../../images/terrabase-noble-podman-v1/recipe.toml");
    const CANDIDATE_PACKET: &[u8] =
        include_bytes!("../../../images/terrabase-noble-podman-v1/candidate-package-research.json");

    #[test]
    fn guest_inventory_keeps_baseline_and_applies_locked_versions_exactly() {
        let recipe = RecipeFile::parse(Path::new("recipe.toml"), CANDIDATE_RECIPE.to_vec())
            .expect("candidate recipe parses");
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock is after Unix epoch")
            .as_nanos();
        let fixture_root = std::env::temp_dir().join(format!(
            "fc-image-builder-research-packet-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&fixture_root).expect("packet fixture directory creates");
        let packet_path = fixture_root.join("candidate-package-research.json");
        fs::write(&packet_path, CANDIDATE_PACKET).expect("embedded packet fixture writes");
        let guest = CandidatePackageLock::from_research_packet(&recipe, &packet_path, "guest")
            .expect("candidate package graph validates");
        fs::remove_dir_all(fixture_root).expect("packet fixture is removed");
        let locked = &guest.packages[0];
        let baseline = vec![
            InstalledPackage {
                name: "ubuntu-minimal".to_owned(),
                architecture: "amd64".to_owned(),
                version: "1.0".to_owned(),
            },
            InstalledPackage {
                name: locked.name.clone(),
                architecture: locked.architecture.clone(),
                version: "baseline-version".to_owned(),
            },
        ];
        let expected = expected_guest_inventory(&guest, &baseline)
            .expect("base packages plus guest lock form an expected inventory");
        assert_eq!(
            expected.get(&("ubuntu-minimal".to_owned(), "amd64".to_owned())),
            Some(&"1.0".to_owned())
        );
        assert_eq!(
            expected.get(&(locked.name.clone(), locked.architecture.clone())),
            Some(&locked.version)
        );

        let installed = expected
            .iter()
            .map(|((name, architecture), version)| InstalledPackage {
                name: name.clone(),
                architecture: architecture.clone(),
                version: version.clone(),
            })
            .collect::<Vec<_>>();
        verify_exact_inventory(&expected, &installed).expect("exact guest inventory passes");

        let mut extra = installed.clone();
        extra.push(InstalledPackage {
            name: "unexpected-package".to_owned(),
            architecture: "amd64".to_owned(),
            version: "1.0".to_owned(),
        });
        assert!(verify_exact_inventory(&expected, &extra).is_err());
    }
}
