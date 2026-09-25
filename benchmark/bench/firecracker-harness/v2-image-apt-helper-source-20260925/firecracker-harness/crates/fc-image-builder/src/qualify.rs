use std::collections::BTreeMap;
use std::error::Error;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use crate::manifest::{
    AptIndexReceipt, AptPackageOrigin, AptPackageReceipt, CommandReceipt,
    FirecrackerReleaseReceipt, HostPodmanReceipt, KernelSourceSignatureReceipt, ResourceLimits,
    RustCliBuilderReceipt, SignatureReceipt, SourceArtifactReceipt,
};
use crate::oci_archive::inspect_oci_archive;
use crate::package_lock::{
    AptSolverReceipt, BuilderToolReceipt, BuilderToolchainReceipt, CandidatePackage,
    CandidatePackageLock, canonical_inventory_sha256,
};
use crate::recipe::{EXPECTED_INITIAL_APT_HELPER_PATH, EXPECTED_INITIAL_APT_HELPER_SHA256};
use crate::workflow::{
    invalid, now_unix_ms, read_command_stdout, sha256_file, verify_canonical_checksum,
};
use crate::{
    ArtifactPin, ContainerMount, NetworkMode, PackageIndexPin, PodmanExecutor, RecipeFile,
    RunDirectory, SignatureRequest, VerifiedLocalImage, verify_package_index,
    verify_signature_in_podman,
};
use serde::Serialize;

const INITIAL_GPGV_SHA256: &str =
    "f14d026b9eae172c432e015bce227483293b4966f2f3fdcfa582f71d3dbb2ae8";
const INITIAL_KEYRING_SHA256: &str =
    "80a36b0a6de2f69f49d2df75ef473ccde121e9e190b9ea01d20a4f63778d5c31";
#[derive(Serialize)]
#[serde(deny_unknown_fields)]
struct AptQualificationReceipt {
    schema_version: u32,
    recipe_identity: String,
    recipe_version: String,
    candidate_recipe_sha256: String,
    qualified_recipe_path: String,
    qualified_recipe_sha256: String,
    created_at_unix_ms: u128,
    initial_trust_source: String,
    initial_gpgv_path: String,
    initial_gpgv_sha256: String,
    initial_apt_helper_path: String,
    initial_apt_helper_sha256: String,
    initial_keyring_path: String,
    initial_keyring_sha256: String,
    canonical_signature: SignatureReceipt,
    base_image_digest: String,
    base_index_digest: String,
    derived_image_digest: String,
    derived_image_archive_path: String,
    derived_image_archive_size_bytes: u64,
    derived_image_archive_sha256: String,
    derived_image_manifest_digest: String,
    derived_image_config_digest: String,
    derived_image_platform: String,
    builder_containerfile_sha256: String,
    host_podman: HostPodmanReceipt,
    guest_apt_indexes: Vec<AptIndexReceipt>,
    builder_apt_indexes: Vec<AptIndexReceipt>,
    guest_packages: Vec<AptPackageReceipt>,
    builder_packages: Vec<AptPackageReceipt>,
    package_origins: Vec<AptPackageOrigin>,
    derived_builder_inventory_sha256: String,
    downloads: Vec<crate::DownloadReceipt>,
    guest_solver: AptSolverReceipt,
    builder_solver: AptSolverReceipt,
    builder_tools: Vec<BuilderToolReceipt>,
    rust_cli_builder: RustCliBuilderReceipt,
    v2_kernel: Option<V2KernelQualificationReceipt>,
    offline_smoke_archive_path: String,
    offline_smoke_archive_size_bytes: u64,
    offline_smoke_archive_sha256: String,
    guest_lock_path: String,
    guest_lock_sha256: String,
    builder_lock_path: String,
    builder_lock_sha256: String,
    commands: Vec<CommandReceipt>,
    limitations: Vec<String>,
}

#[derive(Serialize)]
#[serde(deny_unknown_fields)]
struct V2KernelQualificationReceipt {
    kernel_signing_key: SourceArtifactReceipt,
    kernel_archive: SourceArtifactReceipt,
    kernel_archive_compressed: SourceArtifactReceipt,
    kernel_source_signature: KernelSourceSignatureReceipt,
    firecracker_config_seed: SourceArtifactReceipt,
    firecracker_kernel_policy: SourceArtifactReceipt,
    source_date_epoch: u64,
    source_date_utc: String,
    resolved_config_path: String,
    resolved_config_sha256: String,
    config_diff_path: String,
    config_diff_sha256: String,
    firecracker_release: FirecrackerReleaseReceipt,
    downloads: Vec<crate::DownloadReceipt>,
}

struct V2KernelDownloads {
    signing_key: crate::DownloadReceipt,
    compressed: crate::DownloadReceipt,
    detached_signature: crate::DownloadReceipt,
    config_seed: crate::DownloadReceipt,
    policy: crate::DownloadReceipt,
    release_archive: crate::DownloadReceipt,
}

struct V2KernelSource {
    archive_path: PathBuf,
    archive_size_bytes: u64,
    archive_sha256: String,
    signature: SignatureReceipt,
}

struct V2KernelConfig {
    resolved_config_path: String,
    resolved_config_sha256: String,
    config_diff_path: String,
    config_diff_sha256: String,
}

struct V2KernelContext<'a, 'run> {
    recipe: &'a RecipeFile,
    podman: &'a PodmanExecutor<'run>,
    image: &'a VerifiedLocalImage,
    tools: &'a [BuilderToolReceipt],
    run: &'a RunDirectory,
    commands: &'a mut Vec<CommandReceipt>,
}

fn qualify_v2_kernel(
    recipe: &RecipeFile,
    fetcher: &crate::HttpsFetcher,
    podman: &PodmanExecutor<'_>,
    image: &VerifiedLocalImage,
    tools: &[BuilderToolReceipt],
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<V2KernelQualificationReceipt, Box<dyn Error>> {
    let downloads = fetch_v2_kernel_inputs(recipe, fetcher, run)?;
    let kernel_context = V2KernelContext {
        recipe,
        podman,
        image,
        tools,
        run,
        commands,
    };
    let source = verify_v2_kernel_source(&kernel_context, &downloads)?;
    let config = configure_v2_kernel(&kernel_context, &downloads)?;
    let firecracker_release =
        inspect_v2_firecracker_release(recipe, run, &downloads.release_archive)?;
    v2_kernel_qualification_receipt(recipe, downloads, source, config, firecracker_release)
}

fn fetch_v2_kernel_inputs(
    recipe: &RecipeFile,
    fetcher: &crate::HttpsFetcher,
    run: &RunDirectory,
) -> Result<V2KernelDownloads, Box<dyn Error>> {
    let kernel = &recipe.recipe().kernel;
    let release = recipe
        .recipe()
        .firecracker
        .as_ref()
        .ok_or_else(|| invalid("v2 candidate omits its Firecracker release pin"))?;
    let download = |id: &str, url: &str, sha256: &str, max_bytes: u64| {
        fetcher.fetch_verified(
            &ArtifactPin {
                id: id.to_owned(),
                url: url.to_owned(),
                sha256: sha256.to_owned(),
                max_bytes,
            },
            run,
        )
    };
    let signing_key = download(
        "kernel_signing_key",
        &kernel.signing_key_url,
        &kernel.signing_key_sha256,
        16 * 1024 * 1024,
    )?;
    let compressed = download(
        "kernel-source.tar.xz",
        &kernel.source_archive_url,
        &kernel.source_archive_sha256,
        1024 * 1024 * 1024,
    )?;
    let detached_signature = download(
        "kernel-source.tar.sign",
        &kernel.source_signature_url,
        &kernel.source_signature_sha256,
        16 * 1024 * 1024,
    )?;
    let config_seed = download(
        "firecracker_kernel_config",
        &kernel.firecracker_config_url,
        &kernel.firecracker_config_sha256,
        16 * 1024 * 1024,
    )?;
    let policy = download(
        "firecracker_kernel_policy",
        &kernel.kernel_policy_url,
        &kernel.kernel_policy_sha256,
        16 * 1024 * 1024,
    )?;
    let release_archive = fetcher.fetch_verified_firecracker_release(
        &ArtifactPin {
            id: "firecracker-release-archive".to_owned(),
            url: release.archive_url.clone(),
            sha256: release.archive_sha256.clone(),
            max_bytes: release.archive_size_bytes,
        },
        release.archive_size_bytes,
        run,
    )?;
    Ok(V2KernelDownloads {
        signing_key,
        compressed,
        detached_signature,
        config_seed,
        policy,
        release_archive,
    })
}

fn verify_v2_kernel_source(
    context: &V2KernelContext<'_, '_>,
    downloads: &V2KernelDownloads,
) -> Result<V2KernelSource, Box<dyn Error>> {
    let kernel = &context.recipe.recipe().kernel;
    let signing_key_path = context.run.path().join(&downloads.signing_key.cache_path);
    let signature_path = context
        .run
        .path()
        .join(&downloads.detached_signature.cache_path);
    let xz = builder_tool_receipt(context.tools, "xz")?;
    let decompress = run_candidate_tool(
        context.podman,
        context.image,
        context.run,
        "qualification_kernel_source_xz_decompress",
        xz,
        &[
            "--decompress".to_owned(),
            "--keep".to_owned(),
            "--memlimit-decompress=4096MiB".to_owned(),
            format!("/work/{}", downloads.compressed.cache_path),
        ],
        &[],
    )?;
    context.commands.push(decompress);
    let archive_path = context.run.path().join("kernel-source.tar");
    let metadata = fs::symlink_metadata(&archive_path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || !(1024 * 1024..=8 * 1024 * 1024 * 1024).contains(&metadata.len())
    {
        return Err(invalid(
            "decompressed kernel source tar has unsafe size or type",
        ));
    }
    let archive_sha256 = sha256_file(&archive_path)?;
    let gpgv = builder_tool_receipt(context.tools, "gpgv")?;
    let signature = crate::signature::verify_signature_in_verified_local(
        context.podman,
        context.image,
        SignatureRequest {
            keyring_path: &signing_key_path,
            keyring_source_path: &kernel.signing_key_url,
            keyring_sha256: &kernel.signing_key_sha256,
            signed_path: &archive_path,
            signed_sha256: &archive_sha256,
            detached_signature: Some((&signature_path, &downloads.detached_signature.sha256)),
            expected_fingerprint: &kernel.signing_fingerprint,
            command_id: "qualification_kernel_source_signature",
        },
        &gpgv.path,
        &gpgv.sha256,
        context.run,
    )?;
    context.commands.push(signature.command_receipt().clone());
    unpack_and_check_v2_kernel_source(context)?;
    Ok(V2KernelSource {
        archive_path,
        archive_size_bytes: metadata.len(),
        archive_sha256,
        signature: signature.receipt().clone(),
    })
}

fn unpack_and_check_v2_kernel_source(
    context: &V2KernelContext<'_, '_>,
) -> Result<(), Box<dyn Error>> {
    let source_dir = context.run.path().join("kernel-source");
    fs::create_dir(&source_dir)?;
    let tar = builder_tool_receipt(context.tools, "tar")?;
    let unpack = run_candidate_tool(
        context.podman,
        context.image,
        context.run,
        "qualification_kernel_source_unpack",
        tar,
        &[
            "--extract".to_owned(),
            "--file=/work/kernel-source.tar".to_owned(),
            "--directory=/work/kernel-source".to_owned(),
            "--no-same-owner".to_owned(),
            "--no-same-permissions".to_owned(),
            "--strip-components=1".to_owned(),
        ],
        &[],
    )?;
    context.commands.push(unpack);
    let make = builder_tool_receipt(context.tools, "make")?;
    let version = run_candidate_tool(
        context.podman,
        context.image,
        context.run,
        "qualification_kernel_source_version",
        make,
        &[
            "-s".to_owned(),
            "-C".to_owned(),
            "/work/kernel-source".to_owned(),
            "kernelversion".to_owned(),
        ],
        &[],
    )?;
    if read_command_output(context.run, &version)?.trim()
        != context.recipe.recipe().kernel.source_version
    {
        return Err(invalid(
            "kernel source tar version differs from the v2 recipe",
        ));
    }
    context.commands.push(version);
    let makefile = fs::symlink_metadata(source_dir.join("Makefile"))?;
    if makefile.file_type().is_symlink() || !makefile.is_file() {
        return Err(invalid(
            "verified kernel source tar omits a regular root Makefile",
        ));
    }
    Ok(())
}

fn configure_v2_kernel(
    context: &V2KernelContext<'_, '_>,
    downloads: &V2KernelDownloads,
) -> Result<V2KernelConfig, Box<dyn Error>> {
    let kernel = &context.recipe.recipe().kernel;
    let source_dir = context.run.path().join("kernel-source");
    let config_seed_path = context.run.path().join(&downloads.config_seed.cache_path);
    if !sha256_file(&config_seed_path)?.eq_ignore_ascii_case(&kernel.firecracker_config_sha256) {
        return Err(invalid(
            "Firecracker kernel config seed differs from its recipe",
        ));
    }
    let seed_bytes = fs::read(&config_seed_path)?;
    fs::copy(&config_seed_path, source_dir.join(".config"))?;
    let environment = vec![
        (
            "SOURCE_DATE_EPOCH".to_owned(),
            kernel.release_source_date_epoch.to_string(),
        ),
        (
            "KBUILD_BUILD_TIMESTAMP".to_owned(),
            kernel.release_source_date_utc.clone(),
        ),
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
    ];
    let make = builder_tool_receipt(context.tools, "make")?;
    let configured = run_candidate_tool(
        context.podman,
        context.image,
        context.run,
        "qualification_kernel_olddefconfig",
        make,
        &[
            "-C".to_owned(),
            "/work/kernel-source".to_owned(),
            "-j4".to_owned(),
            "olddefconfig".to_owned(),
        ],
        &environment,
    )?;
    context.commands.push(configured);
    let resolved_path = source_dir.join(".config");
    let resolved_bytes = fs::read(&resolved_path)?;
    crate::build::validate_firecracker_config(std::str::from_utf8(&resolved_bytes)?)?;
    let resolved_config_sha256 = sha256_file(&resolved_path)?;
    let config_diff = crate::build::kernel_config_diff(&seed_bytes, &resolved_bytes);
    let config_diff_sha256 = crate::build::sha256_bytes(&config_diff);
    let resolved_config_path = "kernel-config-generated".to_owned();
    let config_diff_path = "kernel-config.diff".to_owned();
    context
        .run
        .write_new(&resolved_config_path, &resolved_bytes)?;
    context.run.write_new(&config_diff_path, &config_diff)?;
    Ok(V2KernelConfig {
        resolved_config_path,
        resolved_config_sha256,
        config_diff_path,
        config_diff_sha256,
    })
}

fn inspect_v2_firecracker_release(
    recipe: &RecipeFile,
    run: &RunDirectory,
    release_archive: &crate::DownloadReceipt,
) -> Result<FirecrackerReleaseReceipt, Box<dyn Error>> {
    let release = recipe
        .recipe()
        .firecracker
        .as_ref()
        .ok_or_else(|| invalid("v2 candidate omits its Firecracker release pin"))?;
    let member = if release.executable_member.is_empty() {
        "release-v1.17.0-x86_64/firecracker-v1.17.0-x86_64"
    } else {
        &release.executable_member
    };
    let expected_hash =
        (!release.executable_sha256.is_empty()).then_some(release.executable_sha256.as_str());
    let evidence = crate::firecracker_release::inspect_firecracker_release_archive(
        &run.path().join(&release_archive.cache_path),
        &release.archive_url,
        release.archive_size_bytes,
        &release.archive_sha256,
        member,
        expected_hash,
        run,
    )?;
    Ok(FirecrackerReleaseReceipt {
        archive: evidence.archive,
        download: release_archive.clone(),
        executable_member: evidence.executable_member,
        executable: SourceArtifactReceipt {
            source_url: release.archive_url.clone(),
            path: "firecracker-release-executable".to_owned(),
            size_bytes: evidence.executable_size_bytes,
            sha256: evidence.executable_sha256,
        },
        archive_metadata_source: evidence.archive_metadata_source,
    })
}

fn v2_kernel_qualification_receipt(
    recipe: &RecipeFile,
    downloads: V2KernelDownloads,
    source: V2KernelSource,
    config: V2KernelConfig,
    firecracker_release: FirecrackerReleaseReceipt,
) -> Result<V2KernelQualificationReceipt, Box<dyn Error>> {
    let source_artifact = |receipt: &crate::DownloadReceipt| SourceArtifactReceipt {
        source_url: receipt.source_url.clone(),
        path: receipt.cache_path.clone(),
        size_bytes: receipt.size_bytes,
        sha256: receipt.sha256.clone(),
    };
    Ok(V2KernelQualificationReceipt {
        kernel_signing_key: source_artifact(&downloads.signing_key),
        kernel_archive: SourceArtifactReceipt {
            source_url: recipe.recipe().kernel.source_archive_url.clone(),
            path: source
                .archive_path
                .file_name()
                .ok_or_else(|| invalid("decompressed kernel tar has no filename"))?
                .to_string_lossy()
                .into_owned(),
            size_bytes: source.archive_size_bytes,
            sha256: source.archive_sha256,
        },
        kernel_archive_compressed: source_artifact(&downloads.compressed),
        kernel_source_signature: KernelSourceSignatureReceipt {
            artifact: source_artifact(&downloads.detached_signature),
            verification: source.signature,
        },
        firecracker_config_seed: source_artifact(&downloads.config_seed),
        firecracker_kernel_policy: source_artifact(&downloads.policy),
        source_date_epoch: recipe.recipe().kernel.release_source_date_epoch,
        source_date_utc: recipe.recipe().kernel.release_source_date_utc.clone(),
        resolved_config_path: config.resolved_config_path,
        resolved_config_sha256: config.resolved_config_sha256,
        config_diff_path: config.config_diff_path,
        config_diff_sha256: config.config_diff_sha256,
        firecracker_release,
        downloads: vec![
            downloads.signing_key,
            downloads.compressed,
            downloads.detached_signature,
            downloads.config_seed,
            downloads.policy,
            downloads.release_archive,
        ],
    })
}

fn builder_tool_receipt<'a>(
    tools: &'a [BuilderToolReceipt],
    name: &str,
) -> Result<&'a BuilderToolReceipt, Box<dyn Error>> {
    tools
        .iter()
        .find(|tool| tool.name == name)
        .ok_or_else(|| invalid(&format!("derived builder omits the {name} tool receipt")))
}

fn run_candidate_tool(
    podman: &PodmanExecutor<'_>,
    image: &VerifiedLocalImage,
    run: &RunDirectory,
    command_id: &str,
    tool: &BuilderToolReceipt,
    arguments: &[String],
    environment: &[(String, String)],
) -> Result<CommandReceipt, Box<dyn Error>> {
    let command = podman.run_container_with_verified_local(
        image,
        command_id,
        NetworkMode::None,
        &tool.path,
        &[ContainerMount {
            host_path: run.path().to_path_buf(),
            container_path: "/work".to_owned(),
            read_only: false,
        }],
        &[],
        &[],
        environment,
        arguments,
    )?;
    ensure_success(&command, "run a pinned offline kernel qualification tool")?;
    Ok(command)
}

/// Verify both APT closures and build an offline derived Noble builder from
/// the signed candidate package bytes. For the v2 profile, also generate and
/// receipt the resolved kernel config and inspect the pinned Firecracker
/// executable, leaving both review states pending.
///
/// # Errors
///
/// Returns an error before writing a qualified lock unless every index,
/// package, solver result, tool identity and derived image matches the
/// immutable candidate inputs.
pub fn qualify_apt_inputs(
    recipe: &RecipeFile,
    output_root: &Path,
    run_id: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    validate_candidate_bootstrap(recipe)?;
    let rust_cli_builder = capture_rust_cli_builder(recipe)?;
    let packet_path =
        recipe.resolve_recipe_path(&recipe.recipe().guest_apt.candidate_packet_path)?;
    let mut guest = CandidatePackageLock::from_research_packet(recipe, &packet_path, "guest")?;
    let mut builder = CandidatePackageLock::from_research_packet(recipe, &packet_path, "builder")?;
    let output_root = canonical_directory(output_root, "qualification output root")?;
    let run = RunDirectory::create(&output_root, run_id)?;
    let mut commands = Vec::new();
    let podman = PodmanExecutor::new(recipe, &run)?;
    let host_podman = record_host_podman(recipe, &podman, &run, &mut commands)?;
    let ubuntu = &recipe.recipe().ubuntu_build_builder;
    let base_image = format!("{}@{}", ubuntu.base_image, ubuntu.base_image_digest);
    verify_base_image(recipe, &podman, &run, &mut commands)?;
    let (keyring, apt_helper_sha256) =
        copy_initial_qualification_tools(recipe, &podman, &run, &base_image, &mut commands)?;
    let fetcher = crate::HttpsFetcher::new()?;
    let mut services = AptQualificationServices {
        recipe,
        fetcher: &fetcher,
        podman: &podman,
        base_image: &base_image,
        keyring: &keyring,
        initial_apt_helper_path: ubuntu.initial_apt_helper_path.clone(),
        initial_apt_helper_sha256: apt_helper_sha256,
        run: &run,
        commands: &mut commands,
    };
    run_apt_qualification(
        &mut services,
        &mut guest,
        &mut builder,
        rust_cli_builder,
        host_podman,
    )
}

fn copy_initial_qualification_tools(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    base_image: &str,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(PathBuf, String), Box<dyn Error>> {
    let (keyring, keyring_commands) = podman.copy_from_image(
        "qualification_initial_keyring",
        base_image,
        &recipe.recipe().ubuntu_base.initial_keyring_path,
        "qualification-initial-keyring.gpg",
    )?;
    commands.extend(keyring_commands);
    let (gpgv, gpgv_commands) = podman.copy_from_image(
        "qualification_initial_gpgv",
        base_image,
        &recipe.recipe().ubuntu_base.initial_verifier_path,
        "qualification-initial-gpgv",
    )?;
    commands.extend(gpgv_commands);
    if sha256_file(&keyring)? != INITIAL_KEYRING_SHA256
        || sha256_file(&gpgv)? != INITIAL_GPGV_SHA256
    {
        return Err(invalid("pinned Ubuntu-child bootstrap trust bytes changed"));
    }
    let builder = &recipe.recipe().ubuntu_build_builder;
    if builder.initial_apt_helper_path != EXPECTED_INITIAL_APT_HELPER_PATH
        || builder.initial_apt_helper_sha256 != EXPECTED_INITIAL_APT_HELPER_SHA256
    {
        return Err(invalid("initial apt-helper differs from its reviewed pin"));
    }
    let (apt_helper, apt_helper_commands) = podman.copy_from_image(
        "qualification_initial_apt_helper",
        base_image,
        &builder.initial_apt_helper_path,
        "qualification-initial-apt-helper",
    )?;
    commands.extend(apt_helper_commands);
    let helper_metadata = fs::symlink_metadata(&apt_helper)?;
    if helper_metadata.file_type().is_symlink()
        || !helper_metadata.is_file()
        || helper_metadata.len() != 43_656
    {
        return Err(invalid(
            "pinned Ubuntu-child apt-helper has an invalid file type or size",
        ));
    }
    let apt_helper_sha256 = sha256_file(&apt_helper)?;
    if !apt_helper_sha256.eq_ignore_ascii_case(&builder.initial_apt_helper_sha256) {
        return Err(invalid("pinned Ubuntu-child apt-helper hash changed"));
    }
    Ok((keyring, apt_helper_sha256))
}

fn run_apt_qualification(
    services: &mut AptQualificationServices<'_, '_>,
    guest: &mut CandidatePackageLock,
    builder: &mut CandidatePackageLock,
    rust_cli_builder: RustCliBuilderReceipt,
    host_podman: HostPodmanReceipt,
) -> Result<PathBuf, Box<dyn Error>> {
    let pipeline = qualify_candidate_apt(services, guest, builder)?;
    promote_candidate_lock(guest, pipeline.solvers.guest.clone(), None)?;
    promote_candidate_lock(
        builder,
        pipeline.solvers.builder.clone(),
        Some(BuilderToolchainReceipt {
            derived_oci_digest: pipeline.qualified_builder.build.digest.clone(),
            tools: pipeline.qualified_builder.tool_receipts.clone(),
            build_argv: pipeline.qualified_builder.build.build_argv.clone(),
        }),
    )?;
    let recipe_paths = write_qualified_lock_inputs(services.run, guest, builder)?;
    let proposal = write_qualified_recipe_proposal(
        services.recipe,
        services.run,
        &recipe_paths,
        &pipeline.qualified_builder.build,
        &pipeline.qualified_builder.tool_receipts,
        &pipeline.artifacts.smoke.sha256,
        pipeline.v2_kernel.as_ref(),
    )?;
    write_apt_qualification_receipt(
        services,
        guest,
        builder,
        AptQualificationReceiptInput {
            host_podman,
            rust_cli_builder,
            recipe_paths,
            proposal,
            pipeline,
        },
    )
}

fn write_apt_qualification_receipt(
    services: &mut AptQualificationServices<'_, '_>,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    input: AptQualificationReceiptInput,
) -> Result<PathBuf, Box<dyn Error>> {
    let AptQualificationReceiptInput {
        host_podman,
        rust_cli_builder,
        recipe_paths,
        proposal,
        pipeline,
    } = input;
    let proposal_sha256 = sha256_file(&proposal)?;
    let lock_hashes = (sha256_file(&recipe_paths.0)?, sha256_file(&recipe_paths.1)?);
    let mut downloads = pipeline.artifacts.canonical_downloads;
    downloads.extend(pipeline.indexes.downloads);
    downloads.extend(pipeline.artifacts.package_downloads);
    if let Some(kernel) = &pipeline.v2_kernel {
        downloads.extend(kernel.downloads.clone());
    }
    let is_v2_kernel = pipeline.v2_kernel.is_some();
    let ubuntu = &services.recipe.recipe().ubuntu_build_builder;
    let build = pipeline.qualified_builder.build;
    let receipt = AptQualificationReceipt {
        schema_version: 1,
        recipe_identity: services.recipe.recipe().identity.clone(),
        recipe_version: services.recipe.recipe().recipe_version.clone(),
        candidate_recipe_sha256: services.recipe.sha256().to_owned(),
        qualified_recipe_path: proposal.to_string_lossy().into_owned(),
        qualified_recipe_sha256: proposal_sha256,
        created_at_unix_ms: now_unix_ms()?,
        initial_trust_source: services
            .recipe
            .recipe()
            .ubuntu_build_builder
            .initial_trust_source
            .clone(),
        initial_gpgv_path: services
            .recipe
            .recipe()
            .ubuntu_base
            .initial_verifier_path
            .clone(),
        initial_gpgv_sha256: INITIAL_GPGV_SHA256.to_owned(),
        initial_apt_helper_path: services.initial_apt_helper_path.clone(),
        initial_apt_helper_sha256: services.initial_apt_helper_sha256.clone(),
        initial_keyring_path: services
            .recipe
            .recipe()
            .ubuntu_base
            .initial_keyring_path
            .clone(),
        initial_keyring_sha256: INITIAL_KEYRING_SHA256.to_owned(),
        canonical_signature: pipeline.artifacts.canonical_signature,
        base_image_digest: ubuntu.base_image_digest.clone(),
        base_index_digest: ubuntu.base_image_index_digest.clone(),
        derived_image_digest: build.digest,
        derived_image_archive_path: build.archive_path,
        derived_image_archive_size_bytes: build.archive_size_bytes,
        derived_image_archive_sha256: build.archive_sha256,
        derived_image_manifest_digest: build.manifest_digest,
        derived_image_config_digest: build.config_digest,
        derived_image_platform: build.platform,
        builder_containerfile_sha256: build.containerfile_sha256,
        host_podman,
        guest_apt_indexes: pipeline.indexes.guest,
        builder_apt_indexes: pipeline.indexes.builder,
        guest_packages: package_receipts(guest),
        builder_packages: package_receipts(builder),
        package_origins: pipeline.package_origins,
        derived_builder_inventory_sha256: pipeline.qualified_builder.inventory_sha256,
        downloads,
        guest_solver: pipeline.solvers.guest,
        builder_solver: pipeline.solvers.builder,
        builder_tools: pipeline.qualified_builder.tool_receipts,
        rust_cli_builder,
        v2_kernel: pipeline.v2_kernel,
        offline_smoke_archive_path: pipeline.artifacts.smoke.path,
        offline_smoke_archive_size_bytes: pipeline.artifacts.smoke.size_bytes,
        offline_smoke_archive_sha256: pipeline.artifacts.smoke.sha256,
        guest_lock_path: recipe_paths.0.to_string_lossy().into_owned(),
        guest_lock_sha256: lock_hashes.0,
        builder_lock_path: recipe_paths.1.to_string_lossy().into_owned(),
        builder_lock_sha256: lock_hashes.1,
        commands: std::mem::take(services.commands),
        limitations: qualification_limitations(is_v2_kernel),
    };
    services
        .run
        .write_new(
            "apt-qualification-receipt.json",
            &serde_json::to_vec_pretty(&receipt)?,
        )
        .map_err(Into::into)
}

fn qualify_candidate_apt(
    services: &mut AptQualificationServices<'_, '_>,
    guest: &mut CandidatePackageLock,
    builder: &mut CandidatePackageLock,
) -> Result<AptQualificationPipeline, Box<dyn Error>> {
    let indexes = fetch_and_verify_indexes(AptIndexQualificationContext {
        recipe: services.recipe,
        guest,
        builder,
        fetcher: services.fetcher,
        podman: services.podman,
        base_image: services.base_image,
        keyring_path: services.keyring,
        run: services.run,
        commands: services.commands,
    })?;
    validate_equivalent_package_origins(&[guest, builder], &indexes.origins)?;
    apply_index_receipts(guest, builder, &indexes);
    let artifacts = fetch_apt_qualification_artifacts(services, guest, builder)?;
    let solvers = solve_apt_roles(services, guest, builder, &artifacts)?;
    let qualified_builder = build_qualified_builder(services, builder, &artifacts, &solvers)?;
    let v2_kernel = if services.recipe.recipe().identity == "terrabase-noble-podman-v2" {
        Some(qualify_v2_kernel(
            services.recipe,
            services.fetcher,
            services.podman,
            &qualified_builder.build.local_image,
            &qualified_builder.tool_receipts,
            services.run,
            services.commands,
        )?)
    } else {
        None
    };
    let package_origins = indexes.origins.clone();
    Ok(AptQualificationPipeline {
        indexes,
        package_origins,
        artifacts,
        solvers,
        qualified_builder,
        v2_kernel,
    })
}

fn apply_index_receipts(
    guest: &mut CandidatePackageLock,
    builder: &mut CandidatePackageLock,
    indexes: &QualifiedAptIndexes,
) {
    guest.indexes = guest
        .indexes
        .iter()
        .zip(&indexes.guest)
        .map(|(candidate, receipt)| candidate_index_from_receipt(candidate, receipt))
        .collect();
    builder.indexes = builder
        .indexes
        .iter()
        .zip(&indexes.builder)
        .map(|(candidate, receipt)| candidate_index_from_receipt(candidate, receipt))
        .collect();
}

fn fetch_apt_qualification_artifacts(
    services: &mut AptQualificationServices<'_, '_>,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<AptQualificationArtifacts, Box<dyn Error>> {
    let (canonical, canonical_downloads, canonical_signature) = fetch_and_verify_ubuntu_base(
        services.recipe,
        services.fetcher,
        services.podman,
        services.base_image,
        services.keyring,
        services.run,
        services.commands,
    )?;
    let fetched_packages =
        fetch_candidate_packages(&[guest, builder], services.fetcher, services.run)?;
    let package_downloads = fetched_packages
        .values()
        .map(|(_, download)| download.clone())
        .collect();
    let smoke = fetch_offline_smoke_image(
        services.recipe,
        services.podman,
        services.run,
        services.commands,
    )?;
    let base_status =
        copy_builder_base_status(services.podman, services.base_image, services.commands)?;
    let guest_base_status = extract_guest_base_status(
        services.recipe,
        services.base_image,
        &canonical,
        services.podman,
        services.run,
        services.commands,
    )?;
    let apt_executable = inspect_base_apt(
        services.base_image,
        services.podman,
        services.run,
        services.commands,
    )?;
    verify_apt_version(&apt_executable, builder)?;
    Ok(AptQualificationArtifacts {
        canonical_downloads,
        canonical_signature,
        fetched_packages,
        package_downloads,
        smoke,
        base_status,
        guest_base_status,
        apt_executable,
    })
}

fn solve_apt_roles(
    services: &mut AptQualificationServices<'_, '_>,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    artifacts: &AptQualificationArtifacts,
) -> Result<AptRoleSolvers, Box<dyn Error>> {
    let guest = solve_package_role(AptSolverContext {
        recipe: services.recipe,
        lock: guest,
        image: services.base_image,
        base_status: &artifacts.guest_base_status,
        keyring_path: services.keyring,
        apt_executable: &artifacts.apt_executable,
        role: "guest",
        podman: services.podman,
        run: services.run,
        commands: services.commands,
    })?;
    let builder = solve_package_role(AptSolverContext {
        recipe: services.recipe,
        lock: builder,
        image: services.base_image,
        base_status: &artifacts.base_status,
        keyring_path: services.keyring,
        apt_executable: &artifacts.apt_executable,
        role: "builder",
        podman: services.podman,
        run: services.run,
        commands: services.commands,
    })?;
    Ok(AptRoleSolvers { guest, builder })
}

fn build_qualified_builder(
    services: &mut AptQualificationServices<'_, '_>,
    builder: &CandidatePackageLock,
    artifacts: &AptQualificationArtifacts,
    solvers: &AptRoleSolvers,
) -> Result<QualifiedBuilderImage, Box<dyn Error>> {
    let build = build_offline_builder(
        services.recipe,
        builder,
        &artifacts.fetched_packages,
        services.podman,
        services.base_image,
        services.run,
        services.commands,
    )?;
    let tool_receipts = inspect_builder_tools(
        services.recipe,
        &build.local_image,
        services.podman,
        services.run,
        services.commands,
    )?;
    let derived_apt = tool_receipts
        .iter()
        .find(|tool| tool.name == "apt-get")
        .ok_or_else(|| invalid("derived image has no apt-get executable receipt"))?;
    verify_apt_version(derived_apt, builder)?;
    let inventory_sha256 = verify_derived_builder_inventory(
        builder,
        &artifacts.base_status,
        &solvers.builder,
        &build.local_image,
        services.podman,
        services.commands,
    )?;
    Ok(QualifiedBuilderImage {
        build,
        tool_receipts,
        inventory_sha256,
    })
}

#[derive(Clone)]
struct DerivedImage {
    digest: String,
    archive_path: String,
    archive_size_bytes: u64,
    archive_sha256: String,
    manifest_digest: String,
    config_digest: String,
    platform: String,
    containerfile_sha256: String,
    build_argv: Vec<String>,
    local_image: VerifiedLocalImage,
}

#[derive(Clone)]
struct SmokeArchive {
    path: String,
    size_bytes: u64,
    sha256: String,
}

type CandidatePackageIdentity = (String, String, String);
type FetchedCandidatePackage = (String, crate::DownloadReceipt);
type FetchedCandidatePackages = BTreeMap<CandidatePackageIdentity, FetchedCandidatePackage>;

struct QualifiedAptIndexes {
    guest: Vec<AptIndexReceipt>,
    builder: Vec<AptIndexReceipt>,
    downloads: Vec<crate::DownloadReceipt>,
    origins: Vec<AptPackageOrigin>,
}

struct VerifiedAptIndex {
    receipt: AptIndexReceipt,
    downloads: Vec<crate::DownloadReceipt>,
    origins: Vec<AptPackageOrigin>,
}

struct AptIndexQualificationContext<'a, 'run> {
    recipe: &'a RecipeFile,
    guest: &'a CandidatePackageLock,
    builder: &'a CandidatePackageLock,
    fetcher: &'a crate::HttpsFetcher,
    podman: &'a PodmanExecutor<'run>,
    base_image: &'a str,
    keyring_path: &'a Path,
    run: &'a RunDirectory,
    commands: &'a mut Vec<CommandReceipt>,
}

struct AptQualificationServices<'a, 'run> {
    recipe: &'a RecipeFile,
    fetcher: &'a crate::HttpsFetcher,
    podman: &'a PodmanExecutor<'run>,
    base_image: &'a str,
    keyring: &'a Path,
    initial_apt_helper_path: String,
    initial_apt_helper_sha256: String,
    run: &'a RunDirectory,
    commands: &'a mut Vec<CommandReceipt>,
}

struct AptQualificationArtifacts {
    canonical_downloads: Vec<crate::DownloadReceipt>,
    canonical_signature: SignatureReceipt,
    fetched_packages: FetchedCandidatePackages,
    package_downloads: Vec<crate::DownloadReceipt>,
    smoke: SmokeArchive,
    base_status: PathBuf,
    guest_base_status: PathBuf,
    apt_executable: BuilderToolReceipt,
}

struct AptRoleSolvers {
    guest: AptSolverReceipt,
    builder: AptSolverReceipt,
}

struct AptSolverContext<'a, 'run> {
    recipe: &'a RecipeFile,
    lock: &'a CandidatePackageLock,
    image: &'a str,
    base_status: &'a Path,
    keyring_path: &'a Path,
    apt_executable: &'a BuilderToolReceipt,
    role: &'a str,
    podman: &'a PodmanExecutor<'run>,
    run: &'a RunDirectory,
    commands: &'a mut Vec<CommandReceipt>,
}

struct QualifiedBuilderImage {
    build: DerivedImage,
    tool_receipts: Vec<BuilderToolReceipt>,
    inventory_sha256: String,
}

struct AptQualificationPipeline {
    indexes: QualifiedAptIndexes,
    package_origins: Vec<AptPackageOrigin>,
    artifacts: AptQualificationArtifacts,
    solvers: AptRoleSolvers,
    qualified_builder: QualifiedBuilderImage,
    v2_kernel: Option<V2KernelQualificationReceipt>,
}

struct AptQualificationReceiptInput {
    host_podman: HostPodmanReceipt,
    rust_cli_builder: RustCliBuilderReceipt,
    recipe_paths: (PathBuf, PathBuf),
    proposal: PathBuf,
    pipeline: AptQualificationPipeline,
}

fn validate_candidate_bootstrap(recipe: &RecipeFile) -> Result<(), Box<dyn Error>> {
    let candidate = recipe.recipe();
    if !["terrabase-noble-podman-v1", "terrabase-noble-podman-v2"]
        .contains(&candidate.identity.as_str())
        || candidate.recipe_version != "2.0.0"
        || candidate.qualification.status != "candidate-lock-incomplete"
        || candidate.ubuntu_base.initial_verifier_sha256 != INITIAL_GPGV_SHA256
        || candidate.ubuntu_base.initial_keyring_sha256 != INITIAL_KEYRING_SHA256
        || candidate.guest_apt.initial_keyring_sha256 != INITIAL_KEYRING_SHA256
        || candidate.ubuntu_build_builder.initial_gpgv_sha256 != INITIAL_GPGV_SHA256
        || candidate.ubuntu_build_builder.initial_keyring_sha256 != INITIAL_KEYRING_SHA256
    {
        return Err(invalid(
            "APT qualification only accepts the reviewed incomplete Noble candidate and pinned base trust bytes",
        ));
    }
    if candidate.guest_apt.snapshot_url != candidate.ubuntu_build_builder.snapshot_url
        || candidate.guest_apt.snapshot_timestamp
            != candidate.ubuntu_build_builder.snapshot_timestamp
        || candidate.guest_apt.architecture != candidate.ubuntu_build_builder.architecture
        || candidate.guest_apt.suites != candidate.ubuntu_build_builder.suites
        || candidate.guest_apt.components != candidate.ubuntu_build_builder.components
    {
        return Err(invalid("guest and builder APT universes are not identical"));
    }
    Ok(())
}

fn qualification_limitations(is_v2_kernel: bool) -> Vec<String> {
    let mut limitations = vec![
        "Rust CLI executable hash and pinned-image compilation receipt remain unresolved".to_owned(),
        "the derived builder and package closures remain unqualified until an operator reviews and exercises this receipt".to_owned(),
    ];
    if is_v2_kernel {
        limitations.push("generated kernel config is pending independent review; Firecracker release tag is unsigned and the archive is not a source-build attestation".to_owned());
    } else {
        limitations.push("kernel tag signer and public-key source are not pinned; normal Fetch/Build remain disabled".to_owned());
    }
    limitations
}

fn capture_rust_cli_builder(recipe: &RecipeFile) -> Result<RustCliBuilderReceipt, Box<dyn Error>> {
    let pin = &recipe.recipe().rust_cli_builder;
    let source_tree =
        crate::recipe::source_tree_sha256_at_workspace_input(recipe, &pin.source_root)
            .ok_or_else(|| invalid("Rust CLI source tree is not a safe readable directory"))?;
    let cargo_lock_path = recipe.resolve_workspace_input(&pin.cargo_lock_path)?;
    let rust_toolchain_path = recipe.resolve_workspace_input(&pin.rust_toolchain_path)?;
    let executable = std::env::current_exe()?;
    let executable_metadata = fs::symlink_metadata(&executable)?;
    if executable_metadata.file_type().is_symlink() || !executable_metadata.is_file() {
        return Err(invalid("running Rust CLI is not a regular executable file"));
    }
    let executable_name = executable
        .file_name()
        .ok_or_else(|| invalid("running Rust CLI executable has no filename"))?
        .to_string_lossy()
        .into_owned();
    let executable_sha256 = sha256_file(&executable)?;
    if !pin.output_executable_sha256.is_empty()
        && !executable_sha256.eq_ignore_ascii_case(&pin.output_executable_sha256)
    {
        return Err(invalid(
            "running Rust CLI executable differs from the recipe pin",
        ));
    }
    let host_abi = crate::build::verify_static_x86_64_elf(&executable)?;
    Ok(RustCliBuilderReceipt {
        base_image: pin.base_image.clone(),
        base_oci_digest: pin.base_image_digest.clone(),
        base_index_oci_digest: pin.base_image_index_digest.clone(),
        source_tree_sha256: source_tree,
        cargo_lock_path: pin.cargo_lock_path.clone(),
        cargo_lock_sha256: sha256_file(&cargo_lock_path)?,
        rust_toolchain_path: pin.rust_toolchain_path.clone(),
        rust_toolchain_sha256: sha256_file(&rust_toolchain_path)?,
        rust_toolchain_channel: pin.rust_toolchain_channel.clone(),
        target: pin.target.clone(),
        executable_path: executable_name,
        executable_sha256,
        rust_version: env!("FC_IMAGE_BUILDER_RUSTC_VERSION").to_owned(),
        cargo_version: env!("FC_IMAGE_BUILDER_CARGO_VERSION").to_owned(),
        host_abi,
    })
}

fn record_host_podman(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<HostPodmanReceipt, Box<dyn Error>> {
    let command = podman.invoke(
        "qualification_host_podman_version",
        &["--version".to_owned()],
    )?;
    ensure_success(&command, "read the pinned host Podman version")?;
    let output = read_command_stdout(run, &command)?;
    let observed_version = output
        .split_ascii_whitespace()
        .last()
        .ok_or_else(|| invalid("pinned host Podman omitted its version"))?;
    let pin = &recipe.recipe().host_podman;
    if observed_version != pin.version {
        return Err(invalid(
            "host Podman version differs from its pinned recipe identity",
        ));
    }
    commands.push(command);
    Ok(HostPodmanReceipt {
        path: pin.path.clone(),
        version: observed_version.to_owned(),
        sha256: pin.sha256.clone(),
        rootless: pin.rootless,
        uid: pin.uid,
        gid: pin.gid,
        resource_limits: ResourceLimits {
            cpu_count: pin.cpu_count,
            memory_mib: pin.memory_mib,
            pids_limit: pin.pids_limit,
            workspace_limit_bytes: pin.workspace_limit_bytes,
        },
    })
}

fn verify_base_image(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<(), Box<dyn Error>> {
    let pin = &recipe.recipe().ubuntu_build_builder;
    let index_reference = format!("{}@{}", pin.base_image, pin.base_image_index_digest);
    let pull = podman.invoke(
        "qualification_ubuntu_base_pull",
        &[
            "pull".to_owned(),
            "--quiet".to_owned(),
            "--platform=linux/amd64".to_owned(),
            index_reference.clone(),
        ],
    )?;
    ensure_success(&pull, "pull the pinned Ubuntu multi-arch index")?;
    commands.push(pull);

    let child_pull = podman.invoke(
        "qualification_ubuntu_base_child_pull",
        &[
            "pull".to_owned(),
            "--quiet".to_owned(),
            "--platform=linux/amd64".to_owned(),
            format!("{}@{}", pin.base_image, pin.base_image_digest),
        ],
    )?;
    ensure_success(&child_pull, "pull the pinned Ubuntu amd64 child")?;
    commands.push(child_pull);

    let inspect = podman.invoke(
        "qualification_ubuntu_base_platform",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Os}}/{{.Architecture}} {{.Digest}}".to_owned(),
            format!("{}@{}", pin.base_image, pin.base_image_digest),
        ],
    )?;
    ensure_success(&inspect, "inspect the pinned Ubuntu child image")?;
    let observed = read_command_stdout(run, &inspect)?.trim().to_owned();
    if observed != format!("linux/amd64 {}", pin.base_image_digest) {
        return Err(invalid(
            "pulled Ubuntu child digest or platform differs from the recipe",
        ));
    }
    commands.push(inspect);

    let index = podman.invoke(
        "qualification_ubuntu_index_manifest",
        &["manifest".to_owned(), "inspect".to_owned(), index_reference],
    )?;
    ensure_success(&index, "inspect the pinned Ubuntu image index")?;
    let document: serde_json::Value =
        serde_json::from_slice(&fs::read(run.path().join(&index.stdout_path))?)?;
    let descriptors = document
        .get("manifests")
        .and_then(serde_json::Value::as_array)
        .ok_or_else(|| invalid("Ubuntu image index has no platform descriptors"))?;
    let child_matches = descriptors.iter().any(|descriptor| {
        descriptor.get("digest").and_then(serde_json::Value::as_str)
            == Some(pin.base_image_digest.as_str())
            && descriptor.get("platform").is_some_and(|platform| {
                platform.get("os").and_then(serde_json::Value::as_str) == Some("linux")
                    && platform
                        .get("architecture")
                        .and_then(serde_json::Value::as_str)
                        == Some("amd64")
            })
    });
    if !child_matches {
        return Err(invalid(
            "Ubuntu index does not bind the locked amd64 child digest",
        ));
    }
    commands.push(index);
    Ok(())
}

fn fetch_and_verify_indexes(
    mut context: AptIndexQualificationContext<'_, '_>,
) -> Result<QualifiedAptIndexes, Box<dyn Error>> {
    if context.guest.indexes.len() != 6 || context.builder.indexes.len() != 6 {
        return Err(invalid(
            "candidate locks must contain the six Noble signed indexes",
        ));
    }
    let mut receipts = Vec::with_capacity(6);
    let mut artifacts = Vec::with_capacity(12);
    let mut origins = Vec::new();
    let apt_repository = context.run.path().join("verified-apt-snapshot");
    fs::create_dir(&apt_repository)?;
    for (index_number, index) in context.guest.indexes.iter().enumerate() {
        let matching = context.builder.indexes.iter().find(|candidate| {
            candidate.suite == index.suite
                && candidate.component == index.component
                && candidate.architecture == index.architecture
        });
        if matching.is_none_or(|candidate| {
            candidate.inrelease_url != index.inrelease_url
                || candidate.inrelease_sha256 != index.inrelease_sha256
                || candidate.packages_index_url != index.packages_index_url
                || candidate.packages_index_sha256 != index.packages_index_sha256
        }) {
            return Err(invalid(
                "guest and builder research graphs disagree on a signed index",
            ));
        }
        let receipt =
            fetch_and_verify_one_index(&mut context, index_number, index, &apt_repository)?;
        receipts.push(receipt.receipt);
        artifacts.extend(receipt.downloads);
        origins.extend(receipt.origins);
    }
    let guest_receipts = receipts
        .iter()
        .cloned()
        .map(|mut receipt| {
            receipt
                .trusted_key_source
                .clone_from(&context.recipe.recipe().guest_apt.initial_keyring_source);
            receipt
        })
        .collect();
    Ok(QualifiedAptIndexes {
        guest: guest_receipts,
        builder: receipts,
        downloads: artifacts,
        origins,
    })
}

fn fetch_and_verify_one_index(
    context: &mut AptIndexQualificationContext<'_, '_>,
    index_number: usize,
    index: &crate::CandidateIndex,
    apt_repository: &Path,
) -> Result<VerifiedAptIndex, Box<dyn Error>> {
    let inrelease = context.fetcher.fetch_verified(
        &ArtifactPin {
            id: format!("qualify_apt_{index_number:02}_inrelease"),
            url: index.inrelease_url.clone(),
            sha256: index.inrelease_sha256.clone(),
            max_bytes: index.inrelease_size_bytes,
        },
        context.run,
    )?;
    let inrelease_path = context.run.path().join(&inrelease.cache_path);
    let signature = verify_signature_in_podman(
        context.podman,
        context.base_image,
        SignatureRequest {
            keyring_path: context.keyring_path,
            keyring_source_path: &context.recipe.recipe().ubuntu_base.initial_keyring_path,
            keyring_sha256: &context.recipe.recipe().ubuntu_base.initial_keyring_sha256,
            signed_path: &inrelease_path,
            signed_sha256: &inrelease.sha256,
            detached_signature: None,
            expected_fingerprint: &context.recipe.recipe().guest_apt.signing_fingerprint,
            command_id: &format!("qualify_apt_{index_number:02}_gpgv"),
        },
        &context.recipe.recipe().ubuntu_base.initial_verifier_path,
        &context.recipe.recipe().ubuntu_base.initial_verifier_sha256,
        context.run,
    )?;
    context.commands.push(signature.command_receipt().clone());
    let compressed = context.fetcher.fetch_verified(
        &ArtifactPin {
            id: format!("qualify_apt_{index_number:02}_packages_xz"),
            url: index.packages_index_url.clone(),
            sha256: index.packages_index_sha256.clone(),
            max_bytes: index.packages_index_size_bytes,
        },
        context.run,
    )?;
    let compressed_path = context.run.path().join(&compressed.cache_path);
    let verified_index = verify_package_index(
        &PackageIndexPin {
            snapshot_url: context.recipe.recipe().guest_apt.snapshot_url.clone(),
            suite: index.suite.clone(),
            component: index.component.clone(),
            architecture: index.architecture.clone(),
            trusted_keyring_sha256: context
                .recipe
                .recipe()
                .ubuntu_base
                .initial_keyring_sha256
                .clone(),
            inrelease_sha256: index.inrelease_sha256.clone(),
            packages_index_sha256: index.packages_index_sha256.clone(),
            packages_index_size: index.packages_index_size_bytes,
        },
        &inrelease_path,
        &compressed_path,
        &signature,
    )?;
    let suite_root = apt_repository.join("dists").join(&index.suite);
    let package_index_root = suite_root
        .join(&index.component)
        .join(format!("binary-{}", index.architecture));
    copy_verified_repository_input(
        &inrelease_path,
        &suite_root.join("InRelease"),
        &inrelease.sha256,
        inrelease.size_bytes,
    )?;
    let compressed_repository_path = package_index_root.join("Packages.xz");
    copy_verified_repository_input(
        &compressed_path,
        &compressed_repository_path,
        &compressed.sha256,
        compressed.size_bytes,
    )?;
    let builder = &context.recipe.recipe().ubuntu_build_builder;
    let decompressed = crate::apt::decompress_package_index_with_apt_helper_in_podman(
        context.podman,
        context.base_image,
        &builder.initial_apt_helper_path,
        &builder.initial_apt_helper_sha256,
        verified_index,
        &compressed_repository_path,
        &format!("qualify_apt_{index_number:02}_apt_helper"),
        context.run,
    )?;
    context
        .commands
        .push(decompressed.command_receipt().clone());
    let origins =
        crate::verify_locked_package_origins(&[context.guest, context.builder], &decompressed)?;
    let receipt =
        build_apt_index_receipt(context, index, &inrelease, &compressed, signature.receipt());
    Ok(VerifiedAptIndex {
        receipt,
        downloads: vec![inrelease, compressed],
        origins,
    })
}

fn build_apt_index_receipt(
    context: &AptIndexQualificationContext<'_, '_>,
    index: &crate::CandidateIndex,
    inrelease: &crate::DownloadReceipt,
    compressed: &crate::DownloadReceipt,
    signature: &SignatureReceipt,
) -> AptIndexReceipt {
    AptIndexReceipt {
        suite: index.suite.clone(),
        component: index.component.clone(),
        architecture: index.architecture.clone(),
        inrelease_url: index.inrelease_url.clone(),
        inrelease_sha256: index.inrelease_sha256.clone(),
        inrelease_size_bytes: inrelease.size_bytes,
        packages_index_url: index.packages_index_url.clone(),
        packages_index_sha256: index.packages_index_sha256.clone(),
        packages_index_size_bytes: compressed.size_bytes,
        trusted_key_source: context
            .recipe
            .recipe()
            .ubuntu_build_builder
            .initial_trust_source
            .clone(),
        trusted_key_sha256: context
            .recipe
            .recipe()
            .ubuntu_base
            .initial_keyring_sha256
            .clone(),
        gpgv_verified: true,
        signature: signature.clone(),
    }
}

fn validate_equivalent_package_origins(
    locks: &[&CandidatePackageLock],
    origins: &[AptPackageOrigin],
) -> Result<(), Box<dyn Error>> {
    type PackageIdentity = (String, String, String);
    type PackageBytes = (String, u64, String);

    let mut expected = BTreeMap::<PackageIdentity, Vec<&CandidatePackage>>::new();
    for lock in locks {
        for package in &lock.packages {
            expected
                .entry((
                    package.name.clone(),
                    package.version.clone(),
                    package.architecture.clone(),
                ))
                .or_default()
                .push(package);
        }
    }

    let mut observed_bytes = BTreeMap::<PackageIdentity, PackageBytes>::new();
    for origin in origins {
        let identity = (
            origin.name.clone(),
            origin.version.clone(),
            origin.architecture.clone(),
        );
        if !expected.contains_key(&identity) {
            return Err(invalid(
                "signed APT origin does not correspond to a selected package tuple",
            ));
        }
        let bytes = (
            origin.filename.clone(),
            origin.size_bytes,
            origin.deb_sha256.to_ascii_lowercase(),
        );
        if observed_bytes
            .get(&identity)
            .is_some_and(|previous| previous != &bytes)
        {
            return Err(invalid(
                "duplicate signed APT package origins disagree on filename, size, or SHA-256",
            ));
        }
        observed_bytes.insert(identity, bytes);
    }

    for (identity, packages) in expected {
        let Some(observed) = observed_bytes.get(&identity) else {
            return Err(invalid(
                "selected APT package tuple has no signed repository origin",
            ));
        };
        for package in packages {
            if observed.0 != package.filename
                || observed.1 != package.size_bytes
                || !observed.2.eq_ignore_ascii_case(&package.sha256)
                || package.source_url
                    != format!(
                        "https://snapshot.ubuntu.com/ubuntu/20260910T164400Z/{}",
                        package.filename
                    )
            {
                return Err(invalid(
                    "signed package origin differs from its selected lock tuple",
                ));
            }
        }
    }
    Ok(())
}

fn candidate_index_from_receipt(
    candidate: &crate::CandidateIndex,
    receipt: &AptIndexReceipt,
) -> crate::CandidateIndex {
    let mut qualified = candidate.clone();
    qualified
        .signer_primary_fingerprint
        .clone_from(&receipt.signature.signer_primary_fingerprint);
    qualified
        .trusted_key_source
        .clone_from(&receipt.trusted_key_source);
    qualified
        .trusted_key_sha256
        .clone_from(&receipt.trusted_key_sha256);
    qualified.gpgv_verified = receipt.gpgv_verified;
    qualified
}

fn copy_verified_repository_input(
    source: &Path,
    destination: &Path,
    expected_sha256: &str,
    expected_size: u64,
) -> Result<(), Box<dyn Error>> {
    let metadata = fs::symlink_metadata(source)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() != expected_size
        || !sha256_file(source)?.eq_ignore_ascii_case(expected_sha256)
    {
        return Err(invalid(
            "APT repository source changed after signature verification",
        ));
    }
    let parent = destination
        .parent()
        .ok_or_else(|| invalid("APT repository destination has no parent"))?;
    fs::create_dir_all(parent)?;
    if fs::symlink_metadata(destination).is_ok() {
        if fs::symlink_metadata(destination)?.file_type().is_symlink()
            || !fs::metadata(destination)?.is_file()
            || fs::metadata(destination)?.len() != expected_size
            || !sha256_file(destination)?.eq_ignore_ascii_case(expected_sha256)
        {
            return Err(invalid(
                "APT signed suite metadata conflicts across components",
            ));
        }
        return Ok(());
    }
    copy_regular_verified(source, destination, expected_sha256, expected_size)
}

fn fetch_and_verify_ubuntu_base(
    recipe: &RecipeFile,
    fetcher: &crate::HttpsFetcher,
    podman: &PodmanExecutor<'_>,
    base_image: &str,
    keyring: &Path,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<
    (
        crate::DownloadReceipt,
        Vec<crate::DownloadReceipt>,
        SignatureReceipt,
    ),
    Box<dyn Error>,
> {
    let sums = fetcher.fetch_observed(
        "qualify_ubuntu_sha256sums",
        &recipe.recipe().ubuntu_base.sha256sums_url,
        4 * 1024 * 1024,
        None,
        run,
    )?;
    let signature_file = fetcher.fetch_observed(
        "qualify_ubuntu_sha256sums_signature",
        &recipe.recipe().ubuntu_base.signature_url,
        1024 * 1024,
        None,
        run,
    )?;
    let sums_path = run.path().join(&sums.cache_path);
    let detached_path = run.path().join(&signature_file.cache_path);
    let verified = verify_signature_in_podman(
        podman,
        base_image,
        SignatureRequest {
            keyring_path: keyring,
            keyring_source_path: &recipe.recipe().ubuntu_base.initial_keyring_path,
            keyring_sha256: &recipe.recipe().ubuntu_base.initial_keyring_sha256,
            signed_path: &sums_path,
            signed_sha256: &sums.sha256,
            detached_signature: Some((&detached_path, &signature_file.sha256)),
            expected_fingerprint: &recipe.recipe().ubuntu_base.primary_signing_fingerprint,
            command_id: "qualify_ubuntu_base_sha256sums_gpgv",
        },
        &recipe.recipe().ubuntu_base.initial_verifier_path,
        &recipe.recipe().ubuntu_base.initial_verifier_sha256,
        run,
    )?;
    commands.push(verified.command_receipt().clone());
    let signature_receipt = verified.receipt().clone();
    verify_canonical_checksum(
        &sums_path,
        "ubuntu-base-24.04.5-base-amd64.tar.gz",
        &recipe.recipe().ubuntu_base.sha256,
    )?;
    let base = fetcher.fetch_verified(
        &ArtifactPin {
            id: "qualify_ubuntu_base_tar_gz".to_owned(),
            url: recipe.recipe().ubuntu_base.url.clone(),
            sha256: recipe.recipe().ubuntu_base.sha256.clone(),
            max_bytes: 4 * 1024 * 1024 * 1024,
        },
        run,
    )?;
    Ok((
        base.clone(),
        vec![sums, signature_file, base],
        signature_receipt,
    ))
}

fn fetch_candidate_packages(
    locks: &[&CandidatePackageLock],
    fetcher: &crate::HttpsFetcher,
    run: &RunDirectory,
) -> Result<FetchedCandidatePackages, Box<dyn Error>> {
    let mut packages = BTreeMap::new();
    for lock in locks {
        for package in &lock.packages {
            let key = package_key(package);
            if packages.contains_key(&key) {
                continue;
            }
            let downloaded = fetcher.fetch_verified(
                &ArtifactPin {
                    id: format!("qualify_deb_{:04}", packages.len()),
                    url: package.source_url.clone(),
                    sha256: package.sha256.clone(),
                    max_bytes: package.size_bytes,
                },
                run,
            )?;
            let path = run.path().join(&downloaded.cache_path);
            if fs::metadata(&path)?.len() != package.size_bytes
                || !sha256_file(&path)?.eq_ignore_ascii_case(&package.sha256)
            {
                return Err(invalid(
                    "candidate .deb changed after HTTPS checksum verification",
                ));
            }
            packages.insert(key, (downloaded.cache_path.clone(), downloaded));
        }
    }
    Ok(packages)
}

fn fetch_offline_smoke_image(
    recipe: &RecipeFile,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<SmokeArchive, Box<dyn Error>> {
    let smoke = &recipe.recipe().offline_smoke;
    let reference = format!("{}@{}", smoke.image, smoke.digest);
    let pull = podman.invoke(
        "qualification_smoke_pull",
        &[
            "pull".to_owned(),
            "--platform=linux/amd64".to_owned(),
            reference.clone(),
        ],
    )?;
    ensure_success(&pull, "pull the pinned BusyBox digest")?;
    commands.push(pull);
    let inspect = podman.invoke(
        "qualification_smoke_inspect",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Os}}/{{.Architecture}} {{.Digest}}".to_owned(),
            reference.clone(),
        ],
    )?;
    ensure_success(&inspect, "inspect the pinned BusyBox child")?;
    if read_command_stdout(run, &inspect)?.trim() != format!("linux/amd64 {}", smoke.digest) {
        return Err(invalid(
            "BusyBox child digest or platform differs from the recipe",
        ));
    }
    commands.push(inspect);
    let archive_path = run.path().join("busybox.oci.tar");
    let save = podman.invoke(
        "qualification_smoke_save",
        &[
            "save".to_owned(),
            "--format=oci-archive".to_owned(),
            format!("--output={}", archive_path.display()),
            reference,
        ],
    )?;
    ensure_success(&save, "save the pinned BusyBox OCI archive")?;
    let archive_size_bytes = fs::metadata(&archive_path)?.len();
    let archive_sha256 = sha256_file(&archive_path)?;
    commands.push(save);
    Ok(SmokeArchive {
        path: archive_path.to_string_lossy().into_owned(),
        size_bytes: archive_size_bytes,
        sha256: archive_sha256,
    })
}

fn copy_builder_base_status(
    podman: &PodmanExecutor<'_>,
    base_image: &str,
    commands: &mut Vec<CommandReceipt>,
) -> Result<PathBuf, Box<dyn Error>> {
    let (path, receipts) = podman.copy_from_image(
        "qualification_builder_base_status",
        base_image,
        "/var/lib/dpkg/status",
        "builder-base-status",
    )?;
    commands.extend(receipts);
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid(
            "pinned Ubuntu child dpkg status is not a regular file",
        ));
    }
    parse_dpkg_status(&fs::read_to_string(&path)?)?;
    Ok(path)
}

fn inspect_base_apt(
    base_image: &str,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<BuilderToolReceipt, Box<dyn Error>> {
    let (executable, copy_commands) = podman.copy_from_image(
        "qualification_base_apt_get",
        base_image,
        "/usr/bin/apt-get",
        "base-apt-get-executable",
    )?;
    commands.extend(copy_commands);
    let metadata = fs::symlink_metadata(&executable)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() == 0 {
        return Err(invalid(
            "pinned Ubuntu child apt-get executable is not a regular file",
        ));
    }
    let executable_sha256 = sha256_file(&executable)?;
    let mounts = [ContainerMount {
        host_path: run.path().to_path_buf(),
        container_path: "/work".to_owned(),
        read_only: true,
    }];
    let environment = [("LC_ALL".to_owned(), "C".to_owned())];
    let version_command = podman.run_container_with_options(
        "qualification_base_apt_version",
        base_image,
        NetworkMode::None,
        "/usr/bin/apt-get",
        &mounts,
        &[],
        &[],
        &environment,
        &["--version".to_owned()],
    )?;
    ensure_success(&version_command, "read the pinned Ubuntu child APT version")?;
    let version_output = read_command_output(run, &version_command)?;
    let version = parse_apt_version(&version_output)
        .ok_or_else(|| invalid("pinned Ubuntu child apt-get omitted its version"))?;
    commands.push(version_command);
    Ok(BuilderToolReceipt {
        name: "apt-get".to_owned(),
        path: "/usr/bin/apt-get".to_owned(),
        version,
        sha256: executable_sha256,
    })
}

fn parse_apt_version(output: &str) -> Option<String> {
    let first_line = output.lines().find(|line| !line.trim().is_empty())?.trim();
    first_line
        .strip_prefix("apt ")
        .and_then(|version| version.split_ascii_whitespace().next())
        .filter(|version| !version.is_empty())
        .map(str::to_owned)
}

fn verify_apt_version(
    executable: &BuilderToolReceipt,
    builder: &CandidatePackageLock,
) -> Result<(), Box<dyn Error>> {
    let observed_version =
        parse_apt_version(&executable.version).unwrap_or_else(|| executable.version.clone());
    let package = builder
        .packages
        .iter()
        .find(|package| package.name == "apt" && package.architecture == "amd64")
        .ok_or_else(|| invalid("builder lock omits the amd64 apt package archive"))?;
    if executable.name != "apt-get"
        || executable.path != "/usr/bin/apt-get"
        || observed_version != builder.apt_version
        || package.version != builder.apt_version
        || !package.sha256.eq_ignore_ascii_case(&builder.apt_sha256)
    {
        return Err(invalid(
            "APT version or locked apt .deb archive identity differs from the recipe",
        ));
    }
    if !is_sha256(&executable.sha256) {
        return Err(invalid("measured apt-get executable hash is invalid"));
    }
    Ok(())
}

fn build_offline_builder(
    recipe: &RecipeFile,
    builder: &CandidatePackageLock,
    package_files: &FetchedCandidatePackages,
    podman: &PodmanExecutor<'_>,
    base_image: &str,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<DerivedImage, Box<dyn Error>> {
    let context = run.path().join("builder-context");
    let containerfile_path =
        prepare_offline_builder_context(builder, package_files, run, base_image)?;
    let tag = format!(
        "localhost/fc-image-builder-derived:{}",
        run_id_from_path(run.path())?
    );
    let limits = &recipe.recipe().ubuntu_build_builder;
    let build = podman.invoke(
        "qualification_derived_builder_build",
        &[
            "build".to_owned(),
            "--pull=never".to_owned(),
            "--network=none".to_owned(),
            "--platform=linux/amd64".to_owned(),
            format!("--cpus={}", limits.cpu_count),
            format!("--memory={}m", limits.memory_mib),
            format!("--pids-limit={}", limits.pids_limit),
            "--storage-opt".to_owned(),
            format!("overlay.size={}", limits.workspace_limit_bytes),
            format!("--tag={tag}"),
            format!("--file={}", containerfile_path.display()),
            context.to_string_lossy().into_owned(),
        ],
    )?;
    ensure_success(&build, "build the offline derived Noble image")?;
    let argv = command_argv(&build);
    if !argv.iter().any(|argument| argument == "--network=none") {
        return Err(invalid(
            "derived builder build receipt omitted network isolation",
        ));
    }
    commands.push(build);

    let inspect = podman.invoke(
        "qualification_derived_builder_inspect",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Os}}/{{.Architecture}} {{.Digest}}".to_owned(),
            tag.clone(),
        ],
    )?;
    ensure_success(&inspect, "inspect the derived Noble image")?;
    let observed = read_command_stdout(run, &inspect)?.trim().to_owned();
    let Some((platform, digest)) = observed.split_once(' ') else {
        return Err(invalid(
            "derived image inspection omitted platform or digest",
        ));
    };
    if platform != "linux/amd64" || !digest.starts_with("sha256:") {
        return Err(invalid("derived image platform or digest is invalid"));
    }
    let inspected_digest = digest.to_owned();
    commands.push(inspect);
    let archive_path = run.path().join("derived-builder.oci.tar");
    let save = podman.invoke(
        "qualification_derived_builder_save",
        &[
            "save".to_owned(),
            "--format=oci-archive".to_owned(),
            format!("--output={}", archive_path.display()),
            tag.clone(),
        ],
    )?;
    ensure_success(&save, "save the derived Noble image OCI archive")?;
    commands.push(save);
    let identity = inspect_oci_archive(&archive_path, "linux/amd64")?;
    if !inspected_digest.eq_ignore_ascii_case(&identity.manifest_digest) {
        return Err(invalid(
            "derived image Podman digest differs from its saved OCI manifest",
        ));
    }
    let (local_image, local_commands) =
        podman.verify_built_local_image(&tag, &identity, "qualification_derived_builder_local")?;
    commands.extend(local_commands);
    Ok(DerivedImage {
        digest: identity.manifest_digest.clone(),
        archive_path: archive_path
            .file_name()
            .ok_or_else(|| invalid("derived OCI archive has no filename"))?
            .to_string_lossy()
            .into_owned(),
        archive_size_bytes: identity.archive_size_bytes,
        archive_sha256: identity.archive_sha256,
        manifest_digest: identity.manifest_digest,
        config_digest: identity.config_digest,
        platform: identity.platform,
        containerfile_sha256: sha256_file(&containerfile_path)?,
        build_argv: argv,
        local_image,
    })
}

fn prepare_offline_builder_context(
    builder: &CandidatePackageLock,
    package_files: &FetchedCandidatePackages,
    run: &RunDirectory,
    base_image: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    let context = run.path().join("builder-context");
    let debs = context.join("debs");
    fs::create_dir(&context)?;
    fs::create_dir(&debs)?;
    let mut run_arguments = vec!["/usr/bin/dpkg".to_owned(), "--unpack".to_owned()];
    for (index, package) in builder.packages.iter().enumerate() {
        let key = package_key(package);
        let (relative, download) = package_files
            .get(&key)
            .ok_or_else(|| invalid("verified builder .deb is missing"))?;
        let source = run.path().join(relative);
        let name = format!("package-{index:04}.deb");
        let destination = debs.join(&name);
        copy_regular_verified(&source, &destination, &package.sha256, package.size_bytes)?;
        run_arguments.push(format!("/tmp/fc-image-builder-debs/{name}"));
        if download.sha256 != package.sha256 {
            return Err(invalid("builder .deb fetch receipt differs from its lock"));
        }
    }
    let mut containerfile = format!("FROM {base_image}\nCOPY debs/ /tmp/fc-image-builder-debs/\n");
    containerfile.push_str("RUN ");
    containerfile.push_str(&serde_json::to_string(&run_arguments)?);
    containerfile.push('\n');
    containerfile.push_str("RUN [\"/usr/bin/dpkg\",\"--configure\",\"--pending\"]\n");
    let containerfile_path = context.join("Containerfile");
    write_new_regular(&containerfile_path, containerfile.as_bytes())?;
    Ok(containerfile_path)
}

fn inspect_builder_tools(
    recipe: &RecipeFile,
    image: &VerifiedLocalImage,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<Vec<BuilderToolReceipt>, Box<dyn Error>> {
    let specs = recipe.recipe().ubuntu_build_builder.tools.clone();
    let mut receipts = Vec::with_capacity(specs.len());
    let work_mount = ContainerMount {
        host_path: run.path().to_path_buf(),
        container_path: "/work".to_owned(),
        read_only: true,
    };
    for tool in &specs {
        let version_command = podman.run_verified_container(
            &format!("qualification_tool_version_{}", tool.name.replace('-', "_")),
            image,
            NetworkMode::None,
            &tool.path,
            std::slice::from_ref(&work_mount),
            &version_arguments(&tool.name),
        )?;
        ensure_success(&version_command, "record a derived builder tool version")?;
        let version_output = read_command_output(run, &version_command)?;
        let version = version_output
            .lines()
            .find(|line| !line.trim().is_empty())
            .map(str::trim)
            .ok_or_else(|| invalid("builder executable did not report a version"))?
            .to_owned();
        commands.push(version_command);

        let hash_command = podman.run_verified_container(
            &format!("qualification_tool_hash_{}", tool.name.replace('-', "_")),
            image,
            NetworkMode::None,
            "/usr/bin/sha256sum",
            std::slice::from_ref(&work_mount),
            &["--".to_owned(), tool.path.clone()],
        )?;
        ensure_success(&hash_command, "hash a derived builder executable")?;
        let hash_output = read_command_stdout(run, &hash_command)?;
        let observed_hash = hash_output
            .split_ascii_whitespace()
            .next()
            .ok_or_else(|| invalid("sha256sum omitted its digest"))?;
        if !is_sha256(observed_hash)
            || (!tool.sha256.is_empty() && !observed_hash.eq_ignore_ascii_case(&tool.sha256))
            || (!tool.version.is_empty() && version != tool.version)
        {
            return Err(invalid(
                "derived builder tool differs from its pinned version or hash",
            ));
        }
        receipts.push(BuilderToolReceipt {
            name: tool.name.clone(),
            path: tool.path.clone(),
            version,
            sha256: observed_hash.to_owned(),
        });
        commands.push(hash_command);
    }
    Ok(receipts)
}

fn extract_guest_base_status(
    recipe: &RecipeFile,
    image: &str,
    ubuntu_base: &crate::DownloadReceipt,
    podman: &PodmanExecutor<'_>,
    run: &RunDirectory,
    commands: &mut Vec<CommandReceipt>,
) -> Result<PathBuf, Box<dyn Error>> {
    let tar = recipe
        .recipe()
        .ubuntu_build_builder
        .tools
        .iter()
        .find(|tool| tool.name == "tar")
        .ok_or_else(|| invalid("candidate recipe omits the pinned tar role"))?;
    let extraction = podman.run_container(
        "qualification_guest_base_status",
        image,
        NetworkMode::None,
        &tar.path,
        &[ContainerMount {
            host_path: run.path().to_path_buf(),
            container_path: "/work".to_owned(),
            read_only: true,
        }],
        &[
            "--extract".to_owned(),
            "--gzip".to_owned(),
            "--to-stdout".to_owned(),
            format!("--file=/work/{}", ubuntu_base.cache_path),
            "var/lib/dpkg/status".to_owned(),
        ],
    )?;
    ensure_success(
        &extraction,
        "read dpkg status from the signed Ubuntu Base archive",
    )?;
    let status_bytes = fs::read(run.path().join(&extraction.stdout_path))?;
    let status = String::from_utf8(status_bytes)
        .map_err(|_| invalid("Ubuntu Base dpkg status is not UTF-8"))?;
    parse_dpkg_status(&status)?;
    let output = write_new_run_file(run, "guest-base-status", status.as_bytes())?;
    commands.push(extraction);
    Ok(output)
}

fn verify_derived_builder_inventory(
    builder: &CandidatePackageLock,
    base_status: &Path,
    solver: &AptSolverReceipt,
    image: &VerifiedLocalImage,
    podman: &PodmanExecutor<'_>,
    commands: &mut Vec<CommandReceipt>,
) -> Result<String, Box<dyn Error>> {
    let (status_path, receipts) = podman.copy_from_verified_image(
        "qualification_derived_builder_status",
        image,
        "/var/lib/dpkg/status",
        "derived-builder-status",
    )?;
    commands.extend(receipts);
    let metadata = fs::symlink_metadata(&status_path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid(
            "derived Ubuntu builder dpkg status is not a regular file",
        ));
    }
    let base = fs::read_to_string(base_status)?;
    let expected = expected_inventory(builder, &base)?;
    let actual_text = fs::read_to_string(&status_path)?;
    let actual = parse_dpkg_status(&actual_text)?;
    if actual != expected {
        return Err(invalid(
            "derived builder installed dpkg inventory differs from pinned Ubuntu baseline plus builder lock",
        ));
    }
    let actual_sha256 = inventory_sha256(&actual);
    if actual_sha256 != solver.selected_inventory_sha256 {
        return Err(invalid(
            "derived builder dpkg inventory differs from the APT solver selection",
        ));
    }
    Ok(actual_sha256)
}

fn solve_package_role(
    context: AptSolverContext<'_, '_>,
) -> Result<AptSolverReceipt, Box<dyn Error>> {
    let AptSolverContext {
        recipe,
        lock,
        image,
        base_status,
        keyring_path,
        apt_executable,
        role,
        podman,
        run,
        commands,
    } = context;
    let apt = &recipe.recipe().guest_apt;
    if apt_executable.version != lock.apt_version {
        return Err(invalid(
            "pinned Ubuntu child APT executable version differs from this role lock",
        ));
    }
    let source_name = format!("{role}-apt-sources.list");
    let source_text = apt_sources_text(apt);
    let _source_path = run.write_new(&source_name, source_text.as_bytes())?;
    let lists_path = run.path().join(format!("apt-lists-{role}"));
    fs::create_dir(&lists_path)?;
    fs::create_dir(lists_path.join("partial"))?;
    let mount = ContainerMount {
        host_path: run.path().to_path_buf(),
        container_path: "/work".to_owned(),
        read_only: false,
    };
    let keyring_name = keyring_path
        .file_name()
        .ok_or_else(|| invalid("initial APT trust keyring has no filename"))?
        .to_string_lossy();
    let common = apt_options(role, &source_name, base_status, &lists_path, &keyring_name);
    let apt_get = "/usr/bin/apt-get";
    let mut update_args = common.clone();
    update_args.push("update".to_owned());
    let locale_environment = [("LC_ALL".to_owned(), "C".to_owned())];
    let update = podman.run_container_with_options(
        &format!("qualification_{role}_apt_update"),
        image,
        NetworkMode::None,
        apt_get,
        std::slice::from_ref(&mount),
        &[],
        &[],
        &locale_environment,
        &update_args,
    )?;
    ensure_success(&update, "refresh the exact signed Noble snapshot indexes")?;
    commands.push(update);

    let mut solver_args = common;
    solver_args.push("-s".to_owned());
    solver_args.push("--no-install-recommends".to_owned());
    solver_args.push("--no-install-suggests".to_owned());
    solver_args.push("install".to_owned());
    let seeds = lock
        .groups
        .get(role)
        .ok_or_else(|| invalid("candidate package lock omits its seed set"))?;
    if seeds.is_empty() {
        return Err(invalid("APT solver seed set cannot be empty"));
    }
    for seed in seeds {
        let package = lock
            .packages
            .iter()
            .find(|package| package.name == *seed)
            .ok_or_else(|| invalid("APT solver seed is not present in the candidate lock"))?;
        solver_args.push(format!("{}={}", package.name, package.version));
    }
    let command_argv = std::iter::once(apt_get.to_owned())
        .chain(solver_args.iter().cloned())
        .collect::<Vec<_>>();
    let solver = podman.run_container_with_options(
        &format!("qualification_{role}_apt_solver"),
        image,
        NetworkMode::None,
        apt_get,
        std::slice::from_ref(&mount),
        &[],
        &[],
        &locale_environment,
        &solver_args,
    )?;
    ensure_success(&solver, "simulate the exact candidate APT closure")?;
    let stdout = read_command_stdout(run, &solver)?;
    let status = fs::read_to_string(base_status)?;
    let selected = validate_solver_selection(lock, &status, &stdout)?;
    let receipt = AptSolverReceipt {
        command_argv,
        stdout_sha256: solver.stdout_sha256.clone(),
        stderr_sha256: solver.stderr_sha256.clone(),
        exit_status: solver.status_code,
        apt_version: apt_executable.version.clone(),
        apt_executable_path: apt_executable.path.clone(),
        apt_executable_sha256: apt_executable.sha256.clone(),
        locale: "C".to_owned(),
        baseline_status_sha256: sha256_file(base_status)?,
        baseline_inventory_sha256: inventory_sha256(&parse_dpkg_status(&status)?),
        selected_inventory_sha256: inventory_sha256(&selected),
    };
    commands.push(solver);
    Ok(receipt)
}

fn apt_options(
    role: &str,
    source_name: &str,
    base_status: &Path,
    lists_path: &Path,
    keyring_name: &str,
) -> Vec<String> {
    let status_name = base_status
        .file_name()
        .expect("base status has a filename")
        .to_string_lossy();
    let lists_name = lists_path
        .file_name()
        .expect("apt lists path has a filename")
        .to_string_lossy();
    let mut arguments = Vec::new();
    for option in [
        format!("Dir::Etc::sourcelist=/work/{source_name}"),
        "Dir::Etc::sourceparts=-".to_owned(),
        format!("Dir::State::status=/work/{status_name}"),
        format!("Dir::State::lists=/work/{lists_name}/"),
        format!("Dir::Cache::pkgcache=/work/apt-{role}-pkgcache"),
        format!("Dir::Cache::srcpkgcache=/work/apt-{role}-srcpkgcache"),
        format!("Dir::Etc::trusted=/work/{keyring_name}"),
        "Dir::Etc::trustedparts=-".to_owned(),
        "Acquire::Languages=none".to_owned(),
        "APT::Architecture=amd64".to_owned(),
        "APT::Architectures=amd64".to_owned(),
        "APT::Get::AllowUnauthenticated=false".to_owned(),
        "APT::Install-Recommends=false".to_owned(),
        "APT::Install-Suggests=false".to_owned(),
        "Acquire::Check-Valid-Until=true".to_owned(),
    ] {
        arguments.push("-o".to_owned());
        arguments.push(option);
    }
    arguments
}

fn apt_sources_text(apt: &crate::recipe::GuestAptRecipe) -> String {
    let mut text = String::new();
    let components = apt.components.join(" ");
    for suite in &apt.suites {
        text.push_str("deb [arch=");
        text.push_str(&apt.architecture);
        text.push_str(" check-valid-until=");
        text.push_str(if apt.check_valid_until { "yes" } else { "no" });
        text.push_str("] file:/work/verified-apt-snapshot ");
        text.push_str(suite);
        text.push(' ');
        text.push_str(&components);
        text.push('\n');
    }
    text
}

fn validate_solver_selection(
    lock: &CandidatePackageLock,
    base_status: &str,
    solver_output: &str,
) -> Result<BTreeMap<(String, String), String>, Box<dyn Error>> {
    let mut selected = parse_dpkg_status(base_status)?;
    for line in solver_output.lines() {
        if let Some(rest) = line.strip_prefix("Inst ") {
            let fields = rest.split_ascii_whitespace().collect::<Vec<_>>();
            if fields.len() < 3 {
                return Err(invalid("APT simulation emitted a malformed Inst record"));
            }
            let package_field = fields[0];
            let raw_name = package_field.split('[').next().unwrap_or(package_field);
            let (name, architecture_hint) = raw_name
                .split_once(':')
                .map_or((raw_name, None), |(name, architecture)| {
                    (name, Some(architecture))
                });
            let left_parenthesis = fields
                .iter()
                .position(|field| field.starts_with('('))
                .ok_or_else(|| invalid("APT simulation Inst record omits its selected version"))?;
            let version = fields[left_parenthesis]
                .trim_start_matches('(')
                .trim_end_matches(')');
            if version.is_empty() {
                return Err(invalid("APT simulation Inst record has an empty version"));
            }
            let architecture = architecture_hint
                .or_else(|| {
                    fields.iter().find_map(|field| {
                        field
                            .strip_prefix('[')
                            .and_then(|value| value.strip_suffix(']'))
                            .filter(|value| ["amd64", "all"].contains(value))
                    })
                })
                .or_else(|| {
                    lock.packages
                        .iter()
                        .find(|package| package.name == name && package.version == version)
                        .map(|package| package.architecture.as_str())
                })
                .ok_or_else(|| invalid("APT simulation package architecture is ambiguous"))?;
            selected.insert(
                (name.to_owned(), architecture.to_owned()),
                version.to_owned(),
            );
        } else if let Some(rest) = line.strip_prefix("Remv ") {
            let package = rest.split_ascii_whitespace().next().unwrap_or_default();
            let (name, architecture) = package
                .split_once(':')
                .map_or((package, "amd64"), |(name, architecture)| {
                    (name, architecture)
                });
            selected.remove(&(name.to_owned(), architecture.to_owned()));
        }
    }
    let expected = expected_inventory(lock, base_status)?;
    if selected != expected {
        return Err(invalid(
            "APT simulation package closure differs from the candidate lock",
        ));
    }
    Ok(selected)
}

fn expected_inventory(
    lock: &CandidatePackageLock,
    base_status: &str,
) -> Result<BTreeMap<(String, String), String>, Box<dyn Error>> {
    let mut expected = parse_dpkg_status(base_status)?;
    for package in &lock.packages {
        expected.insert(
            (package.name.clone(), package.architecture.clone()),
            package.version.clone(),
        );
    }
    Ok(expected)
}

fn inventory_sha256(inventory: &BTreeMap<(String, String), String>) -> String {
    canonical_inventory_sha256(inventory)
}

fn parse_dpkg_status(status: &str) -> Result<BTreeMap<(String, String), String>, Box<dyn Error>> {
    let mut installed = BTreeMap::new();
    for stanza in status.split("\n\n") {
        let mut name = None;
        let mut version = None;
        let mut architecture = None;
        let mut package_status = None;
        for line in stanza.lines() {
            if let Some(value) = line.strip_prefix("Package: ") {
                name = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Version: ") {
                version = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Architecture: ") {
                architecture = Some(value.to_owned());
            } else if let Some(value) = line.strip_prefix("Status: ") {
                package_status = Some(value.to_owned());
            }
        }
        if package_status.as_deref() == Some("install ok installed") {
            let name = name.ok_or_else(|| invalid("dpkg status stanza omits package name"))?;
            let version =
                version.ok_or_else(|| invalid("dpkg status stanza omits package version"))?;
            let architecture = architecture
                .ok_or_else(|| invalid("dpkg status stanza omits package architecture"))?;
            if !["amd64", "all"].contains(&architecture.as_str())
                || installed.insert((name, architecture), version).is_some()
            {
                return Err(invalid(
                    "dpkg status has an unsupported or duplicate package",
                ));
            }
        }
    }
    Ok(installed)
}

fn version_arguments(name: &str) -> Vec<String> {
    match name {
        "mke2fs" | "e2fsck" => vec!["-V".to_owned()],
        _ => vec!["--version".to_owned()],
    }
}

fn promote_candidate_lock(
    lock: &mut CandidatePackageLock,
    solver: AptSolverReceipt,
    builder_tools: Option<BuilderToolchainReceipt>,
) -> Result<(), Box<dyn Error>> {
    "apt-solver-verified".clone_into(&mut lock.qualification_status);
    lock.research_packet_sha256 = None;
    lock.apt_solver_receipt = Some(solver);
    lock.builder_toolchain_receipt = builder_tools;
    lock.limitations.clear();
    for index in &mut lock.indexes {
        let expected_source = "pinned-ubuntu-child";
        if !index.gpgv_verified
            || index.trusted_key_source != expected_source
            || index.trusted_key_sha256 != INITIAL_KEYRING_SHA256
        {
            return Err(invalid(
                "verified package lock lost signed-index trust evidence",
            ));
        }
    }
    lock.validate()?;
    Ok(())
}

fn write_qualified_lock_inputs(
    run: &RunDirectory,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<(PathBuf, PathBuf), Box<dyn Error>> {
    let guest_path = run.write_new("packages.lock.json", &serde_json::to_vec_pretty(guest)?)?;
    let builder_path = run.write_new(
        "builder-packages.lock.json",
        &serde_json::to_vec_pretty(builder)?,
    )?;
    Ok((guest_path, builder_path))
}

fn write_qualified_recipe_proposal(
    recipe: &RecipeFile,
    run: &RunDirectory,
    locks: &(PathBuf, PathBuf),
    derived: &DerivedImage,
    tools: &[BuilderToolReceipt],
    smoke_archive_sha256: &str,
    v2_kernel: Option<&V2KernelQualificationReceipt>,
) -> Result<PathBuf, Box<dyn Error>> {
    let mut document: toml::Value = toml::from_str(std::str::from_utf8(recipe.bytes())?)?;
    write_qualified_lock_pins(&mut document, locks)?;
    write_qualified_builder_pins(&mut document, derived, smoke_archive_sha256, tools)?;
    if let Some(kernel) = v2_kernel {
        write_v2_kernel_pins(&mut document, kernel)?;
    }
    let bytes = toml::to_string_pretty(&document)?.into_bytes();
    let proposal = run.write_new("recipe.toml.apt-qualified", &bytes)?;
    Ok(proposal)
}

fn write_qualified_lock_pins(
    document: &mut toml::Value,
    locks: &(PathBuf, PathBuf),
) -> Result<(), Box<dyn Error>> {
    let guest_lock_name = locks.0.file_name().unwrap().to_string_lossy();
    let builder_lock_name = locks.1.file_name().unwrap().to_string_lossy();
    set_string(
        document,
        &["guest_apt", "packages_lock_path"],
        &guest_lock_name,
    )?;
    set_string(
        document,
        &["guest_apt", "packages_lock_sha256"],
        &sha256_file(&locks.0)?,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "packages_lock_path"],
        &builder_lock_name,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "packages_lock_sha256"],
        &sha256_file(&locks.1)?,
    )?;
    Ok(())
}

fn write_qualified_builder_pins(
    document: &mut toml::Value,
    derived: &DerivedImage,
    smoke_archive_sha256: &str,
    tools: &[BuilderToolReceipt],
) -> Result<(), Box<dyn Error>> {
    set_string(
        document,
        &["ubuntu_build_builder", "derived_image_digest"],
        &derived.digest,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "derived_image_archive_sha256"],
        &derived.archive_sha256,
    )?;
    set_integer(
        document,
        &["ubuntu_build_builder", "derived_image_archive_size_bytes"],
        i64::try_from(derived.archive_size_bytes)?,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "derived_image_manifest_digest"],
        &derived.manifest_digest,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "derived_image_config_digest"],
        &derived.config_digest,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "derived_image_platform"],
        &derived.platform,
    )?;
    set_string(
        document,
        &["ubuntu_base", "initial_verifier_sha256"],
        INITIAL_GPGV_SHA256,
    )?;
    set_string(
        document,
        &["ubuntu_base", "initial_keyring_sha256"],
        INITIAL_KEYRING_SHA256,
    )?;
    set_string(
        document,
        &["guest_apt", "initial_keyring_sha256"],
        INITIAL_KEYRING_SHA256,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "initial_gpgv_sha256"],
        INITIAL_GPGV_SHA256,
    )?;
    set_string(
        document,
        &["ubuntu_build_builder", "initial_keyring_sha256"],
        INITIAL_KEYRING_SHA256,
    )?;
    set_string(
        document,
        &["offline_smoke", "archive_sha256"],
        smoke_archive_sha256,
    )?;
    for tool in tools {
        set_tool_string(document, &tool.name, "version", &tool.version)?;
        set_tool_string(document, &tool.name, "sha256", &tool.sha256)?;
    }
    Ok(())
}

fn write_v2_kernel_pins(
    document: &mut toml::Value,
    kernel: &V2KernelQualificationReceipt,
) -> Result<(), Box<dyn Error>> {
    set_string(
        document,
        &["kernel", "custom_config_sha256"],
        &kernel.resolved_config_sha256,
    )?;
    set_string(
        document,
        &["kernel", "custom_config_diff_sha256"],
        &kernel.config_diff_sha256,
    )?;
    set_string(
        document,
        &["kernel", "custom_config_review_status"],
        "generated-pending-independent-review",
    )?;
    set_string(
        document,
        &["firecracker", "executable_member"],
        &kernel.firecracker_release.executable_member,
    )?;
    set_string(
        document,
        &["firecracker", "executable_sha256"],
        &kernel.firecracker_release.executable.sha256,
    )?;
    set_string(
        document,
        &["firecracker", "executable_review_status"],
        "candidate-observed",
    )?;
    Ok(())
}

fn set_string(
    document: &mut toml::Value,
    path: &[&str],
    value: &str,
) -> Result<(), Box<dyn Error>> {
    let mut current = document;
    for component in &path[..path.len() - 1] {
        current = current
            .get_mut(*component)
            .ok_or_else(|| invalid("candidate recipe update path is missing"))?;
    }
    let target = current
        .as_table_mut()
        .and_then(|table| table.get_mut(path[path.len() - 1]))
        .ok_or_else(|| invalid("candidate recipe update field is missing"))?;
    *target = toml::Value::String(value.to_owned());
    Ok(())
}

fn set_integer(
    document: &mut toml::Value,
    path: &[&str],
    value: i64,
) -> Result<(), Box<dyn Error>> {
    let mut current = document;
    for component in &path[..path.len() - 1] {
        current = current
            .get_mut(*component)
            .ok_or_else(|| invalid("qualified recipe proposal field is absent"))?;
    }
    let field = current
        .get_mut(path[path.len() - 1])
        .ok_or_else(|| invalid("qualified recipe proposal field is absent"))?;
    *field = toml::Value::Integer(value);
    Ok(())
}

fn set_tool_string(
    document: &mut toml::Value,
    name: &str,
    field: &str,
    value: &str,
) -> Result<(), Box<dyn Error>> {
    let tool = document
        .get_mut("ubuntu_build_builder")
        .and_then(|builder| builder.get_mut("tools"))
        .and_then(toml::Value::as_array_mut)
        .and_then(|tools| {
            tools
                .iter_mut()
                .find(|tool| tool.get("name").and_then(toml::Value::as_str) == Some(name))
        })
        .ok_or_else(|| invalid("candidate recipe tool list omits an observed executable"))?;
    let target = tool
        .as_table_mut()
        .and_then(|table| table.get_mut(field))
        .ok_or_else(|| invalid("candidate recipe tool field is missing"))?;
    *target = toml::Value::String(value.to_owned());
    Ok(())
}

fn copy_regular_verified(
    source: &Path,
    destination: &Path,
    expected_sha256: &str,
    expected_size: u64,
) -> Result<(), Box<dyn Error>> {
    let metadata = fs::symlink_metadata(source)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() != expected_size {
        return Err(invalid(
            "verified package input has an invalid file type or size",
        ));
    }
    if !sha256_file(source)?.eq_ignore_ascii_case(expected_sha256) {
        return Err(invalid(
            "verified package input hash changed before image build",
        ));
    }
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination)?;
    let mut input = fs::File::open(source)?;
    std::io::copy(&mut input, &mut output)?;
    output.sync_all()?;
    if fs::metadata(destination)?.len() != expected_size
        || !sha256_file(destination)?.eq_ignore_ascii_case(expected_sha256)
    {
        return Err(invalid(
            "builder context package copy does not match its lock",
        ));
    }
    Ok(())
}

fn write_new_run_file(
    run: &RunDirectory,
    name: &str,
    bytes: &[u8],
) -> Result<PathBuf, Box<dyn Error>> {
    run.write_new(name, bytes).map_err(Into::into)
}

fn write_new_regular(path: &Path, bytes: &[u8]) -> Result<(), Box<dyn Error>> {
    let mut output = OpenOptions::new().write(true).create_new(true).open(path)?;
    output.write_all(bytes)?;
    output.sync_all()?;
    Ok(())
}

fn package_key(package: &CandidatePackage) -> (String, String, String) {
    (
        package.name.clone(),
        package.version.clone(),
        package.architecture.clone(),
    )
}

fn package_receipts(lock: &CandidatePackageLock) -> Vec<AptPackageReceipt> {
    lock.packages
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
        .collect()
}

fn command_argv(command: &CommandReceipt) -> Vec<String> {
    std::iter::once(command.program.clone())
        .chain(command.args.iter().cloned())
        .collect()
}

fn ensure_success(command: &CommandReceipt, purpose: &str) -> Result<(), Box<dyn Error>> {
    if command.status_code != 0 {
        return Err(invalid(&format!(
            "{purpose} exited with status {}",
            command.status_code
        )));
    }
    Ok(())
}

fn read_command_output(
    run: &RunDirectory,
    command: &CommandReceipt,
) -> Result<String, Box<dyn Error>> {
    let stdout = read_command_log(run, &command.stdout_path, &command.stdout_sha256)?;
    if !stdout.trim().is_empty() {
        return Ok(stdout);
    }
    read_command_log(run, &command.stderr_path, &command.stderr_sha256)
}

fn read_command_log(
    run: &RunDirectory,
    name: &str,
    expected_sha256: &str,
) -> Result<String, Box<dyn Error>> {
    let path = run.path().join(name);
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > 512 * 1024 * 1024
    {
        return Err(invalid("builder command log is not a bounded regular file"));
    }
    if !sha256_file(&path)?.eq_ignore_ascii_case(expected_sha256) {
        return Err(invalid("builder command log hash differs from its receipt"));
    }
    fs::read_to_string(path).map_err(|_| invalid("builder tool output is not UTF-8"))
}

fn canonical_directory(path: &Path, purpose: &str) -> Result<PathBuf, Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(invalid(&format!(
            "{purpose} must be an existing non-symlink directory"
        )));
    }
    Ok(fs::canonicalize(path)?)
}

fn run_id_from_path(path: &Path) -> Result<&str, Box<dyn Error>> {
    path.file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid("run directory has no safe UTF-8 identity"))
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}
