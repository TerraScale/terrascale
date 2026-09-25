use std::collections::BTreeMap;
use std::error::Error;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::manifest::{AptIndexReceipt, CommandReceipt, SignatureReceipt};
use crate::oci_archive::{OciArchiveIdentity, verify_oci_archive};
use crate::recipe::KernelSourceVariant;
use crate::{
    ArtifactPin, CandidateIndex, CandidatePackageLock, ContainerMount, HttpsFetcher, NetworkMode,
    PackageIndexPin, PodmanExecutor, RecipeFile, RunDirectory, SignatureRequest,
    VerifiedLocalImage, verify_package_index,
};

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct FetchReceipt {
    pub(crate) schema_version: u32,
    pub(crate) recipe_identity: String,
    pub(crate) recipe_version: String,
    pub(crate) recipe_sha256: String,
    pub(crate) guest_lock_sha256: String,
    pub(crate) builder_lock_sha256: String,
    pub(crate) derived_builder_archive_path: String,
    pub(crate) derived_builder_archive_size_bytes: u64,
    pub(crate) derived_builder_archive_sha256: String,
    pub(crate) derived_builder_manifest_digest: String,
    pub(crate) derived_builder_config_digest: String,
    pub(crate) derived_builder_platform: String,
    pub(crate) verified_at_unix_ms: u128,
    pub(crate) initial_keyring_path: String,
    pub(crate) initial_keyring_sha256: String,
    pub(crate) kernel_source_kind: String,
    pub(crate) kernel_source_revision: String,
    pub(crate) kernel_source_compressed_path: Option<String>,
    pub(crate) kernel_source_compressed_size_bytes: Option<u64>,
    pub(crate) kernel_source_compressed_sha256: Option<String>,
    pub(crate) kernel_source_signature_path: Option<String>,
    pub(crate) kernel_source_signature: Option<SignatureReceipt>,
    pub(crate) kernel_source_archive_path: String,
    pub(crate) kernel_source_archive_size_bytes: u64,
    pub(crate) kernel_source_archive_sha256: String,
    #[serde(default)]
    pub(crate) firecracker_release: Option<FetchFirecrackerReleaseEvidence>,
    pub(crate) guest_apt_indexes: Vec<AptIndexReceipt>,
    pub(crate) builder_apt_indexes: Vec<AptIndexReceipt>,
    pub(crate) packages: Vec<FetchedPackage>,
    pub(crate) sources: Vec<crate::DownloadReceipt>,
    pub(crate) signatures: Vec<SignatureReceipt>,
    pub(crate) commands: Vec<CommandReceipt>,
    pub(crate) offline_smoke_archive_path: String,
    pub(crate) offline_smoke_archive_size_bytes: u64,
    pub(crate) offline_smoke_archive_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct FetchFirecrackerReleaseEvidence {
    pub(crate) archive: crate::manifest::SourceArtifactReceipt,
    pub(crate) executable_member: String,
    pub(crate) executable_path: String,
    pub(crate) executable_size_bytes: u64,
    pub(crate) executable_sha256: String,
    pub(crate) archive_metadata_source: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct FetchedPackage {
    pub(crate) role: String,
    pub(crate) name: String,
    pub(crate) version: String,
    pub(crate) architecture: String,
    pub(crate) source_url: String,
    pub(crate) path: String,
    pub(crate) size_bytes: u64,
    pub(crate) sha256: String,
}

pub(crate) struct KernelSourceInputs {
    pub(crate) kind: String,
    pub(crate) revision: String,
    pub(crate) compressed_path: Option<String>,
    pub(crate) compressed_size_bytes: Option<u64>,
    pub(crate) compressed_sha256: Option<String>,
    pub(crate) source_signature_path: Option<String>,
    pub(crate) source_signature: Option<SignatureReceipt>,
    pub(crate) archive_path: String,
    pub(crate) archive_size_bytes: u64,
    pub(crate) archive_sha256: String,
    pub(crate) sources: Vec<crate::DownloadReceipt>,
    pub(crate) commands: Vec<CommandReceipt>,
}

type PackageIdentity = (String, String, String);
type PackageDownloadIdentity = (String, u64, String, String);

struct FetchSession<'a, 'run> {
    recipe: &'a RecipeFile,
    fetcher: &'a HttpsFetcher,
    podman: &'a PodmanExecutor<'run>,
    builder_image: &'a VerifiedLocalImage,
    keyring_path: &'a Path,
    keyring_sha256: &'a str,
    run: &'a RunDirectory,
}

struct OfflineSmokeArchive {
    size_bytes: u64,
    sha256: String,
    commands: Vec<CommandReceipt>,
}

struct FetchedAptIndex {
    guest: AptIndexReceipt,
    builder: AptIndexReceipt,
    sources: Vec<crate::DownloadReceipt>,
    signature: SignatureReceipt,
    commands: Vec<CommandReceipt>,
}

struct FetchedAptIndexes {
    guest: Vec<AptIndexReceipt>,
    builder: Vec<AptIndexReceipt>,
    sources: Vec<crate::DownloadReceipt>,
    signatures: Vec<SignatureReceipt>,
    commands: Vec<CommandReceipt>,
}

struct VerifiedUbuntuBase {
    downloads: Vec<crate::DownloadReceipt>,
    signature: SignatureReceipt,
    command: CommandReceipt,
}

struct FetchedPackageDebs {
    packages: Vec<FetchedPackage>,
    downloads: Vec<crate::DownloadReceipt>,
}

struct KernelTarballDownloads {
    version: String,
    compressed: crate::DownloadReceipt,
    detached_signature: crate::DownloadReceipt,
}

struct FetchReceiptInput<'a> {
    recipe: &'a RecipeFile,
    staged_identity: &'a OciArchiveIdentity,
    keyring_sha256: &'a str,
    kernel_source: KernelSourceInputs,
    firecracker_release: Option<FetchFirecrackerReleaseEvidence>,
    smoke: OfflineSmokeArchive,
    setup_commands: Vec<CommandReceipt>,
}

pub(crate) fn verify_derived_builder_archive(
    recipe: &RecipeFile,
    archive_path: &Path,
) -> Result<OciArchiveIdentity, Box<dyn Error>> {
    let builder = &recipe.recipe().ubuntu_build_builder;
    let identity = verify_oci_archive(
        archive_path,
        builder.derived_image_archive_size_bytes,
        &builder.derived_image_archive_sha256,
        &builder.derived_image_manifest_digest,
        &builder.derived_image_config_digest,
        &builder.derived_image_platform,
    )?;
    if !identity
        .manifest_digest
        .eq_ignore_ascii_case(&builder.derived_image_digest)
    {
        return Err(invalid(
            "derived OCI archive manifest digest differs from the recipe image digest",
        ));
    }
    Ok(identity)
}

pub(crate) fn stage_derived_builder_archive(
    recipe: &RecipeFile,
    source: &Path,
    expected: &OciArchiveIdentity,
    run: &RunDirectory,
) -> Result<(PathBuf, OciArchiveIdentity), Box<dyn Error>> {
    let destination = run.path().join("derived-builder.oci.tar");
    let mut input = File::open(source)?;
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&destination)?;
    let copied = std::io::copy(&mut input, &mut output)?;
    output.flush()?;
    output.sync_all()?;
    if copied != expected.archive_size_bytes {
        return Err(invalid(
            "staged derived builder archive length changed during copy",
        ));
    }
    let staged = verify_derived_builder_archive(recipe, &destination)?;
    if &staged != expected {
        return Err(invalid(
            "staged derived builder archive identity differs from the preflight archive",
        ));
    }
    Ok((destination, staged))
}

/// Download and verify all package-lock inputs, signed APT indexes, and the
/// Canonical Ubuntu Base archive into a new private run directory.
///
/// # Errors
///
/// Returns an error unless the recipe and both role-specific locks are fully
/// qualified, source identities match their locks, and each network or
/// signature check succeeds.
pub fn fetch_qualified_inputs(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    derived_builder_archive: &Path,
    cli_compile_receipt: &Path,
    cache_root: &Path,
    run_id: &str,
) -> Result<PathBuf, Box<dyn Error>> {
    validate_qualified_inputs(recipe, guest, builder)?;
    let archive_identity = verify_derived_builder_archive(recipe, derived_builder_archive)?;
    let executable = std::env::current_exe()?;
    crate::cli_compiler::verify_cli_compile_receipt(recipe, cli_compile_receipt, &executable)?;
    let run = RunDirectory::create(cache_root, run_id)?;
    let fetcher = HttpsFetcher::new()?;
    let podman = PodmanExecutor::new(recipe, &run)?;
    let (archive_path, staged_identity) =
        stage_derived_builder_archive(recipe, derived_builder_archive, &archive_identity, &run)?;
    let (builder_image, load_commands) =
        podman.load_verified_archive(&archive_path, &staged_identity, "fetch_derived_builder")?;
    let mut setup_commands = load_commands;
    let base_image = format!(
        "{}@{}",
        recipe.recipe().ubuntu_build_builder.base_image,
        recipe.recipe().ubuntu_build_builder.base_image_digest
    );

    let (keyring_path, keyring_commands) = podman.copy_from_image(
        "initial_keyring",
        &base_image,
        &recipe.recipe().ubuntu_base.initial_keyring_path,
        "initial-ubuntu-keyring.gpg",
    )?;
    setup_commands.extend(keyring_commands);
    let keyring_sha256 = sha256_file(&keyring_path)?;
    if !keyring_sha256.eq_ignore_ascii_case(&recipe.recipe().ubuntu_base.initial_keyring_sha256) {
        return Err(invalid(
            "copied Ubuntu-child archive keyring hash differs from the recipe",
        ));
    }
    let session = FetchSession {
        recipe,
        fetcher: &fetcher,
        podman: &podman,
        builder_image: &builder_image,
        keyring_path: &keyring_path,
        keyring_sha256: &keyring_sha256,
        run: &run,
    };
    let kernel_key = fetch_kernel_signing_key(&session)?;
    let kernel_key_path = run.path().join(&kernel_key.cache_path);
    let mut kernel_source = fetch_kernel_source_inputs(&session, &kernel_key_path)?;
    let firecracker_release = fetch_firecracker_release(&session, &mut kernel_source)?;

    let smoke = fetch_offline_smoke_archive(&session)?;

    let mut receipt = new_fetch_receipt(FetchReceiptInput {
        recipe,
        staged_identity: &staged_identity,
        keyring_sha256: &keyring_sha256,
        kernel_source,
        firecracker_release,
        smoke,
        setup_commands,
    })?;

    let apt_indexes = fetch_and_verify_apt_indexes(&session, guest, builder)?;
    receipt.guest_apt_indexes = apt_indexes.guest;
    receipt.builder_apt_indexes = apt_indexes.builder;
    receipt.sources.extend(apt_indexes.sources);
    receipt.signatures.extend(apt_indexes.signatures);
    receipt.commands.extend(apt_indexes.commands);

    let canonical = fetch_canonical_ubuntu_base(&session)?;
    receipt.signatures.push(canonical.signature);
    receipt.commands.push(canonical.command);
    receipt.sources.extend(canonical.downloads);
    receipt.sources.push(kernel_key);
    receipt
        .sources
        .extend(fetch_kernel_policy_inputs(&session)?);
    let package_debs = fetch_locked_package_debs(&session, guest, builder)?;
    receipt.packages = package_debs.packages;
    receipt.sources.extend(package_debs.downloads);

    let json = serde_json::to_vec_pretty(&receipt)?;
    let path = run.write_new("fetch-receipt.json", &json)?;
    Ok(path)
}

fn new_fetch_receipt(input: FetchReceiptInput<'_>) -> Result<FetchReceipt, Box<dyn Error>> {
    let FetchReceiptInput {
        recipe,
        staged_identity,
        keyring_sha256,
        kernel_source,
        firecracker_release,
        smoke,
        mut setup_commands,
    } = input;
    let KernelSourceInputs {
        kind: kernel_source_kind,
        revision: kernel_source_revision,
        compressed_path: kernel_source_compressed_path,
        compressed_size_bytes: kernel_source_compressed_size_bytes,
        compressed_sha256: kernel_source_compressed_sha256,
        source_signature_path: kernel_source_signature_path,
        source_signature: kernel_source_signature,
        archive_path: kernel_source_archive_path,
        archive_size_bytes: kernel_source_archive_size_bytes,
        archive_sha256: kernel_source_archive_sha256,
        sources: kernel_source_downloads,
        commands: kernel_source_commands,
    } = kernel_source;
    setup_commands.extend(kernel_source_commands);
    setup_commands.extend(smoke.commands);
    Ok(FetchReceipt {
        schema_version: 3,
        recipe_identity: recipe.recipe().identity.clone(),
        recipe_version: recipe.recipe().recipe_version.clone(),
        recipe_sha256: recipe.sha256().to_owned(),
        guest_lock_sha256: sha256_file(
            &recipe.resolve_recipe_path(&recipe.recipe().guest_apt.packages_lock_path)?,
        )?,
        builder_lock_sha256: sha256_file(
            &recipe
                .resolve_recipe_path(&recipe.recipe().ubuntu_build_builder.packages_lock_path)?,
        )?,
        derived_builder_archive_path: "derived-builder.oci.tar".to_owned(),
        derived_builder_archive_size_bytes: staged_identity.archive_size_bytes,
        derived_builder_archive_sha256: staged_identity.archive_sha256.clone(),
        derived_builder_manifest_digest: staged_identity.manifest_digest.clone(),
        derived_builder_config_digest: staged_identity.config_digest.clone(),
        derived_builder_platform: staged_identity.platform.clone(),
        verified_at_unix_ms: now_unix_ms()?,
        initial_keyring_path: recipe.recipe().ubuntu_base.initial_keyring_path.clone(),
        initial_keyring_sha256: keyring_sha256.to_owned(),
        kernel_source_kind,
        kernel_source_revision,
        kernel_source_compressed_path,
        kernel_source_compressed_size_bytes,
        kernel_source_compressed_sha256,
        kernel_source_signature_path,
        kernel_source_signature,
        kernel_source_archive_path,
        kernel_source_archive_size_bytes,
        kernel_source_archive_sha256,
        firecracker_release,
        guest_apt_indexes: Vec::with_capacity(6),
        builder_apt_indexes: Vec::with_capacity(6),
        packages: Vec::new(),
        sources: kernel_source_downloads,
        signatures: Vec::new(),
        commands: setup_commands,
        offline_smoke_archive_path: "busybox.oci.tar".to_owned(),
        offline_smoke_archive_size_bytes: smoke.size_bytes,
        offline_smoke_archive_sha256: smoke.sha256,
    })
}

fn fetch_kernel_signing_key(
    session: &FetchSession<'_, '_>,
) -> Result<crate::DownloadReceipt, Box<dyn Error>> {
    Ok(session.fetcher.fetch_verified(
        &ArtifactPin {
            id: "kernel_signing_key".to_owned(),
            url: session.recipe.recipe().kernel.signing_key_url.clone(),
            sha256: session.recipe.recipe().kernel.signing_key_sha256.clone(),
            max_bytes: 16 * 1024 * 1024,
        },
        session.run,
    )?)
}

fn fetch_kernel_source_inputs(
    session: &FetchSession<'_, '_>,
    public_key: &Path,
) -> Result<KernelSourceInputs, Box<dyn Error>> {
    session.recipe.recipe().kernel.source_variant()?;
    fetch_kernel_tarball(
        session.recipe,
        session.fetcher,
        session.podman,
        session.builder_image,
        public_key,
        session.run,
    )
}

fn fetch_firecracker_release(
    session: &FetchSession<'_, '_>,
    kernel_source: &mut KernelSourceInputs,
) -> Result<Option<FetchFirecrackerReleaseEvidence>, Box<dyn Error>> {
    let Some(release) = &session.recipe.recipe().firecracker else {
        return Ok(None);
    };
    let archive = session.fetcher.fetch_verified_firecracker_release(
        &ArtifactPin {
            id: "firecracker-release-archive".to_owned(),
            url: release.archive_url.clone(),
            sha256: release.archive_sha256.clone(),
            max_bytes: release.archive_size_bytes,
        },
        release.archive_size_bytes,
        session.run,
    )?;
    kernel_source.sources.push(archive);
    let archive_path = session.run.path().join("firecracker-release-archive");
    let inspected = crate::firecracker_release::inspect_firecracker_release_archive(
        &archive_path,
        &release.archive_url,
        release.archive_size_bytes,
        &release.archive_sha256,
        &release.executable_member,
        Some(&release.executable_sha256),
        session.run,
    )?;
    Ok(Some(FetchFirecrackerReleaseEvidence {
        archive: inspected.archive,
        executable_member: inspected.executable_member,
        executable_path: "firecracker-release-executable".to_owned(),
        executable_size_bytes: inspected.executable_size_bytes,
        executable_sha256: inspected.executable_sha256,
        archive_metadata_source: inspected.archive_metadata_source,
    }))
}

fn fetch_offline_smoke_archive(
    session: &FetchSession<'_, '_>,
) -> Result<OfflineSmokeArchive, Box<dyn Error>> {
    let smoke = &session.recipe.recipe().offline_smoke;
    let reference = format!("{}@{}", smoke.image, smoke.digest);
    let pull = session.podman.invoke(
        "offline_smoke_image_pull",
        &[
            "pull".to_owned(),
            "--platform=linux/amd64".to_owned(),
            reference.clone(),
        ],
    )?;
    if pull.status_code != 0 {
        return Err(invalid(
            "pinned Podman failed to pull the offline smoke image digest",
        ));
    }
    let inspect = session.podman.invoke(
        "offline_smoke_image_inspect",
        &[
            "image".to_owned(),
            "inspect".to_owned(),
            "--format={{.Digest}}".to_owned(),
            reference.clone(),
        ],
    )?;
    if inspect.status_code != 0
        || read_command_stdout(session.run, &inspect)?.trim() != smoke.digest
    {
        return Err(invalid(
            "Podman smoke image child digest differs from the recipe",
        ));
    }
    let archive_path = session.run.path().join("busybox.oci.tar");
    let save = session.podman.invoke(
        "offline_smoke_image_save",
        &[
            "save".to_owned(),
            "--format=oci-archive".to_owned(),
            format!("--output={}", archive_path.display()),
            reference,
        ],
    )?;
    if save.status_code != 0 {
        return Err(invalid(
            "pinned Podman failed to save the offline smoke image",
        ));
    }
    let size_bytes = fs::metadata(&archive_path)?.len();
    let sha256 = sha256_file(&archive_path)?;
    if !sha256.eq_ignore_ascii_case(&smoke.archive_sha256) {
        return Err(invalid(
            "offline smoke OCI archive hash differs from the recipe",
        ));
    }
    Ok(OfflineSmokeArchive {
        size_bytes,
        sha256,
        commands: vec![pull, inspect, save],
    })
}

fn fetch_and_verify_apt_indexes(
    session: &FetchSession<'_, '_>,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<FetchedAptIndexes, Box<dyn Error>> {
    let mut indexes = merge_indexes(guest, builder)?;
    indexes.sort_by(|left, right| {
        (&left.suite, &left.component, &left.architecture).cmp(&(
            &right.suite,
            &right.component,
            &right.architecture,
        ))
    });
    let gpgv = builder_tool(session.recipe, "gpgv")?;
    let xz = builder_tool(session.recipe, "xz")?;
    let mut fetched = FetchedAptIndexes {
        guest: Vec::with_capacity(indexes.len()),
        builder: Vec::with_capacity(indexes.len()),
        sources: Vec::with_capacity(indexes.len() * 2),
        signatures: Vec::with_capacity(indexes.len()),
        commands: Vec::with_capacity(indexes.len() * 2),
    };
    for (index_number, index) in indexes.iter().enumerate() {
        let evidence = fetch_one_apt_index(session, index_number, index, guest, builder, gpgv, xz)?;
        fetched.guest.push(evidence.guest);
        fetched.builder.push(evidence.builder);
        fetched.sources.extend(evidence.sources);
        fetched.signatures.push(evidence.signature);
        fetched.commands.extend(evidence.commands);
    }
    Ok(fetched)
}

fn fetch_one_apt_index(
    session: &FetchSession<'_, '_>,
    index_number: usize,
    index: &CandidateIndex,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
    gpgv: &crate::recipe::BuilderToolPin,
    xz: &crate::recipe::BuilderToolPin,
) -> Result<FetchedAptIndex, Box<dyn Error>> {
    let inrelease = session.fetcher.fetch_verified(
        &ArtifactPin {
            id: format!("apt_{index_number:02}_inrelease"),
            url: index.inrelease_url.clone(),
            sha256: index.inrelease_sha256.clone(),
            max_bytes: index.inrelease_size_bytes,
        },
        session.run,
    )?;
    let inrelease_path = session.run.path().join(&inrelease.cache_path);
    let verified_signature = crate::signature::verify_signature_in_verified_local(
        session.podman,
        session.builder_image,
        SignatureRequest {
            keyring_path: session.keyring_path,
            keyring_source_path: &session.recipe.recipe().ubuntu_base.initial_keyring_path,
            keyring_sha256: session.keyring_sha256,
            signed_path: &inrelease_path,
            signed_sha256: &inrelease.sha256,
            detached_signature: None,
            expected_fingerprint: &session.recipe.recipe().guest_apt.signing_fingerprint,
            command_id: &format!("apt_{index_number:02}_gpgv"),
        },
        &gpgv.path,
        &gpgv.sha256,
        session.run,
    )?;
    let packages = session.fetcher.fetch_verified(
        &ArtifactPin {
            id: format!("apt_{index_number:02}_packages_xz"),
            url: index.packages_index_url.clone(),
            sha256: index.packages_index_sha256.clone(),
            max_bytes: index.packages_index_size_bytes,
        },
        session.run,
    )?;
    let packages_path = session.run.path().join(&packages.cache_path);
    let verified_index = verify_package_index(
        &PackageIndexPin {
            snapshot_url: session.recipe.recipe().guest_apt.snapshot_url.clone(),
            suite: index.suite.clone(),
            component: index.component.clone(),
            architecture: index.architecture.clone(),
            trusted_keyring_sha256: session.keyring_sha256.to_owned(),
            inrelease_sha256: index.inrelease_sha256.clone(),
            packages_index_sha256: index.packages_index_sha256.clone(),
            packages_index_size: index.packages_index_size_bytes,
        },
        &inrelease_path,
        &packages_path,
        &verified_signature,
    )?;
    let decompressed = crate::apt::decompress_package_index_with_verified_local(
        session.podman,
        session.builder_image,
        &xz.path,
        &xz.sha256,
        verified_index,
        &packages_path,
        &format!("apt_{index_number:02}_xz"),
        session.run,
    )?;
    for lock in [guest, builder] {
        if lock
            .indexes
            .iter()
            .any(|candidate| same_index_identity(candidate, index))
        {
            lock.verify_packages_from_index(&decompressed)?;
        }
    }
    let (guest_receipt, builder_receipt) =
        build_apt_index_receipts(session, index, verified_signature.receipt());
    Ok(FetchedAptIndex {
        guest: guest_receipt,
        builder: builder_receipt,
        sources: vec![inrelease, packages],
        signature: verified_signature.receipt().clone(),
        commands: vec![
            verified_signature.command_receipt().clone(),
            decompressed.command_receipt().clone(),
        ],
    })
}

fn build_apt_index_receipts(
    session: &FetchSession<'_, '_>,
    index: &CandidateIndex,
    signature: &SignatureReceipt,
) -> (AptIndexReceipt, AptIndexReceipt) {
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
        trusted_key_sha256: session.keyring_sha256.to_owned(),
        gpgv_verified: true,
        signature: signature.clone(),
    };
    let mut guest = base_receipt.clone();
    guest
        .trusted_key_source
        .clone_from(&session.recipe.recipe().guest_apt.initial_keyring_source);
    let mut builder = base_receipt;
    builder.trusted_key_source.clone_from(
        &session
            .recipe
            .recipe()
            .ubuntu_build_builder
            .initial_trust_source,
    );
    (guest, builder)
}

fn fetch_canonical_ubuntu_base(
    session: &FetchSession<'_, '_>,
) -> Result<VerifiedUbuntuBase, Box<dyn Error>> {
    let ubuntu = &session.recipe.recipe().ubuntu_base;
    let sums = session.fetcher.fetch_observed(
        "ubuntu_sha256sums",
        &ubuntu.sha256sums_url,
        4 * 1024 * 1024,
        None,
        session.run,
    )?;
    let signature = session.fetcher.fetch_observed(
        "ubuntu_sha256sums_signature",
        &ubuntu.signature_url,
        1024 * 1024,
        None,
        session.run,
    )?;
    let sums_path = session.run.path().join(&sums.cache_path);
    let signature_path = session.run.path().join(&signature.cache_path);
    let gpgv = builder_tool(session.recipe, "gpgv")?;
    let verified = crate::signature::verify_signature_in_verified_local(
        session.podman,
        session.builder_image,
        SignatureRequest {
            keyring_path: session.keyring_path,
            keyring_source_path: &ubuntu.initial_keyring_path,
            keyring_sha256: session.keyring_sha256,
            signed_path: &sums_path,
            signed_sha256: &sums.sha256,
            detached_signature: Some((&signature_path, &signature.sha256)),
            expected_fingerprint: &ubuntu.primary_signing_fingerprint,
            command_id: "ubuntu_base_sha256sums_gpgv",
        },
        &gpgv.path,
        &gpgv.sha256,
        session.run,
    )?;
    verify_canonical_checksum(
        &sums_path,
        "ubuntu-base-24.04.5-base-amd64.tar.gz",
        &ubuntu.sha256,
    )?;
    let base = session.fetcher.fetch_verified(
        &ArtifactPin {
            id: "ubuntu_base_tar_gz".to_owned(),
            url: ubuntu.url.clone(),
            sha256: ubuntu.sha256.clone(),
            max_bytes: 4 * 1024 * 1024 * 1024,
        },
        session.run,
    )?;
    Ok(VerifiedUbuntuBase {
        downloads: vec![sums, signature, base],
        signature: verified.receipt().clone(),
        command: verified.command_receipt().clone(),
    })
}

fn fetch_kernel_policy_inputs(
    session: &FetchSession<'_, '_>,
) -> Result<Vec<crate::DownloadReceipt>, Box<dyn Error>> {
    let kernel = &session.recipe.recipe().kernel;
    [
        (
            "firecracker_kernel_config",
            kernel.firecracker_config_url.as_str(),
            kernel.firecracker_config_sha256.as_str(),
        ),
        (
            "firecracker_kernel_policy",
            kernel.kernel_policy_url.as_str(),
            kernel.kernel_policy_sha256.as_str(),
        ),
    ]
    .into_iter()
    .map(|(id, url, sha256)| {
        Ok(session.fetcher.fetch_verified(
            &ArtifactPin {
                id: id.to_owned(),
                url: url.to_owned(),
                sha256: sha256.to_owned(),
                max_bytes: 16 * 1024 * 1024,
            },
            session.run,
        )?)
    })
    .collect()
}

fn fetch_locked_package_debs(
    session: &FetchSession<'_, '_>,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<FetchedPackageDebs, Box<dyn Error>> {
    let mut package_identities: BTreeMap<PackageIdentity, PackageDownloadIdentity> =
        BTreeMap::new();
    let mut packages = Vec::new();
    let mut downloads = Vec::new();
    for lock in [guest, builder] {
        for package in &lock.packages {
            let identity = (
                package.name.clone(),
                package.version.clone(),
                package.architecture.clone(),
            );
            let (path, size_bytes, sha256) =
                if let Some(existing) = package_identities.get(&identity) {
                    let (path, size_bytes, sha256, source_url) = existing;
                    if source_url != &package.source_url
                        || *size_bytes != package.size_bytes
                        || !sha256.eq_ignore_ascii_case(&package.sha256)
                    {
                        return Err(invalid(
                            "guest and builder locks disagree on a shared .deb identity",
                        ));
                    }
                    (path.clone(), *size_bytes, sha256.clone())
                } else {
                    let downloaded = session.fetcher.fetch_verified(
                        &ArtifactPin {
                            id: format!("deb_{:04}", package_identities.len()),
                            url: package.source_url.clone(),
                            sha256: package.sha256.clone(),
                            max_bytes: package.size_bytes,
                        },
                        session.run,
                    )?;
                    let download_identity = (
                        downloaded.cache_path.clone(),
                        downloaded.size_bytes,
                        downloaded.sha256.clone(),
                        package.source_url.clone(),
                    );
                    package_identities.insert(identity, download_identity.clone());
                    downloads.push(downloaded);
                    (
                        download_identity.0,
                        download_identity.1,
                        download_identity.2,
                    )
                };
            packages.push(FetchedPackage {
                role: lock.package_role.clone(),
                name: package.name.clone(),
                version: package.version.clone(),
                architecture: package.architecture.clone(),
                source_url: package.source_url.clone(),
                path,
                size_bytes,
                sha256,
            });
        }
    }
    Ok(FetchedPackageDebs {
        packages,
        downloads,
    })
}

pub(crate) fn fetch_kernel_tarball(
    recipe: &RecipeFile,
    fetcher: &HttpsFetcher,
    podman: &PodmanExecutor<'_>,
    builder_image: &VerifiedLocalImage,
    public_key: &Path,
    run: &RunDirectory,
) -> Result<KernelSourceInputs, Box<dyn Error>> {
    let KernelTarballDownloads {
        version,
        compressed,
        detached_signature,
    } = fetch_kernel_tarball_downloads(recipe, fetcher, run)?;
    let xz = builder_tool(recipe, "xz")?;
    let decompress = podman.run_verified_container(
        "kernel_source_xz_decompress",
        builder_image,
        NetworkMode::None,
        &xz.path,
        &[ContainerMount {
            host_path: run.path().to_path_buf(),
            container_path: "/work".to_owned(),
            read_only: false,
        }],
        &[
            "--decompress".to_owned(),
            "--keep".to_owned(),
            "--memlimit-decompress=4096MiB".to_owned(),
            format!("/work/{}", compressed.cache_path),
        ],
    )?;
    if decompress.status_code != 0 {
        return Err(invalid(
            "pinned xz failed to decompress the hash-verified kernel archive",
        ));
    }
    let archive_path = run.path().join("kernel-source.tar");
    let metadata = fs::symlink_metadata(&archive_path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() < 1024 * 1024
        || metadata.len() > 8 * 1024 * 1024 * 1024
    {
        return Err(invalid(
            "decompressed kernel tar has an unsafe or implausible size",
        ));
    }
    let archive_sha256 = sha256_file(&archive_path)?;
    let signature_path = run.path().join(&detached_signature.cache_path);
    let archive_sha256_for_signature = archive_sha256.clone();
    let signature = crate::signature::verify_signature_in_verified_local(
        podman,
        builder_image,
        SignatureRequest {
            keyring_path: public_key,
            keyring_source_path: &recipe.recipe().kernel.signing_key_url,
            keyring_sha256: &recipe.recipe().kernel.signing_key_sha256,
            signed_path: &archive_path,
            signed_sha256: &archive_sha256_for_signature,
            detached_signature: Some((&signature_path, &detached_signature.sha256)),
            expected_fingerprint: &recipe.recipe().kernel.signing_fingerprint,
            command_id: "kernel_source_tar_signature",
        },
        &builder_tool(recipe, "gpgv")?.path,
        &builder_tool(recipe, "gpgv")?.sha256,
        run,
    )?;
    let archive_size_bytes = metadata.len();
    Ok(KernelSourceInputs {
        kind: "kernel.org-signed-tarball".to_owned(),
        revision: version,
        compressed_path: Some(compressed.cache_path.clone()),
        compressed_size_bytes: Some(compressed.size_bytes),
        compressed_sha256: Some(compressed.sha256.clone()),
        source_signature_path: Some(detached_signature.cache_path.clone()),
        source_signature: Some(signature.receipt().clone()),
        archive_path: "kernel-source.tar".to_owned(),
        archive_size_bytes,
        archive_sha256,
        sources: vec![compressed, detached_signature],
        commands: vec![decompress, signature.command_receipt().clone()],
    })
}

fn fetch_kernel_tarball_downloads(
    recipe: &RecipeFile,
    fetcher: &HttpsFetcher,
    run: &RunDirectory,
) -> Result<KernelTarballDownloads, Box<dyn Error>> {
    let KernelSourceVariant::SignedTarball {
        version,
        archive_url,
        archive_sha256,
        signature_url,
        signature_sha256,
        release_source_date_epoch,
    } = recipe.recipe().kernel.source_variant()?;
    if release_source_date_epoch != recipe.recipe().kernel.release_source_date_epoch {
        return Err(invalid(
            "kernel release epoch changed during variant resolution",
        ));
    }
    let compressed = fetcher.fetch_verified(
        &ArtifactPin {
            id: "kernel-source.tar.xz".to_owned(),
            url: archive_url,
            sha256: archive_sha256,
            max_bytes: 1024 * 1024 * 1024,
        },
        run,
    )?;
    let detached_signature = fetcher.fetch_verified(
        &ArtifactPin {
            id: "kernel-source.tar.sign".to_owned(),
            url: signature_url,
            sha256: signature_sha256,
            max_bytes: 16 * 1024 * 1024,
        },
        run,
    )?;
    Ok(KernelTarballDownloads {
        version,
        compressed,
        detached_signature,
    })
}

pub(crate) fn builder_tool<'a>(
    recipe: &'a RecipeFile,
    name: &str,
) -> Result<&'a crate::recipe::BuilderToolPin, Box<dyn Error>> {
    recipe
        .recipe()
        .ubuntu_build_builder
        .tools
        .iter()
        .find(|tool| tool.name == name)
        .ok_or_else(|| invalid(&format!("recipe omits the pinned builder {name} tool")))
}

pub(crate) fn read_command_stdout(
    run: &RunDirectory,
    command: &CommandReceipt,
) -> Result<String, Box<dyn Error>> {
    let path = run.path().join(&command.stdout_path);
    let output = fs::read_to_string(path)?;
    Ok(output)
}

/// Require the deliberate post-bootstrap recipe state and a pair of
/// independently APT-solver-verified role locks before network or container
/// activity starts.
///
/// # Errors
///
/// Returns an error if the recipe is not `inputs-locked`, unresolved source
/// pins remain, either package lock lacks APT solver proof, or the lock trust
/// roots do not match the recipe.
pub fn validate_qualified_inputs(
    recipe: &RecipeFile,
    guest: &CandidatePackageLock,
    builder: &CandidatePackageLock,
) -> Result<(), Box<dyn Error>> {
    if recipe.recipe().qualification.status != "inputs-locked" {
        return Err(invalid(
            "candidate recipe is not inputs-locked; only the separate bootstrap path may handle it",
        ));
    }
    let gaps = recipe.unresolved_inputs();
    if !gaps.is_empty() {
        return Err(invalid(&format!(
            "fetch/build refused before network access or external tools: {gaps}"
        )));
    }
    if guest.package_role != "guest"
        || builder.package_role != "builder"
        || !guest.is_apt_solver_verified()
        || !builder.is_apt_solver_verified()
    {
        return Err(invalid(
            "fetch/build require independent apt-solver-verified guest and builder locks",
        ));
    }
    let expected_keyring_sha256 = &recipe.recipe().ubuntu_base.initial_keyring_sha256;
    for index in &guest.indexes {
        if !index.gpgv_verified
            || index.trusted_key_source != recipe.recipe().guest_apt.initial_keyring_source
            || !index
                .trusted_key_sha256
                .eq_ignore_ascii_case(expected_keyring_sha256)
            || index.signer_primary_fingerprint != recipe.recipe().guest_apt.signing_fingerprint
        {
            return Err(invalid(
                "qualified package lock has an APT index receipt from a different trust root",
            ));
        }
    }
    for index in &builder.indexes {
        if !index.gpgv_verified
            || index.trusted_key_source != recipe.recipe().ubuntu_build_builder.initial_trust_source
            || !index
                .trusted_key_sha256
                .eq_ignore_ascii_case(expected_keyring_sha256)
            || index.signer_primary_fingerprint != recipe.recipe().guest_apt.signing_fingerprint
        {
            return Err(invalid(
                "qualified builder lock has an APT index receipt from a different trust root",
            ));
        }
    }
    Ok(())
}

fn merge_indexes<'a>(
    guest: &'a CandidatePackageLock,
    builder: &'a CandidatePackageLock,
) -> Result<Vec<&'a CandidateIndex>, Box<dyn Error>> {
    let mut indexes = BTreeMap::new();
    for index in &guest.indexes {
        indexes.insert(
            (
                index.suite.as_str(),
                index.component.as_str(),
                index.architecture.as_str(),
            ),
            index,
        );
    }
    for index in &builder.indexes {
        let key = (
            index.suite.as_str(),
            index.component.as_str(),
            index.architecture.as_str(),
        );
        let Some(existing) = indexes.get(&key) else {
            return Err(invalid(
                "builder lock is missing an index from the guest lock",
            ));
        };
        if !same_index(existing, index) {
            return Err(invalid("guest and builder APT index receipts differ"));
        }
    }
    if indexes.len() != 6 {
        return Err(invalid(
            "qualified locks must cover exactly six APT indexes",
        ));
    }
    Ok(indexes.into_values().collect())
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

fn same_index_identity(left: &CandidateIndex, right: &CandidateIndex) -> bool {
    left.suite == right.suite
        && left.component == right.component
        && left.architecture == right.architecture
}

pub(crate) fn verify_canonical_checksum(
    path: &Path,
    filename: &str,
    expected: &str,
) -> Result<(), Box<dyn Error>> {
    let text = fs::read_to_string(path)?;
    let mut matched = false;
    for line in text.lines() {
        let mut fields = line.split_ascii_whitespace();
        let Some(digest) = fields.next() else {
            continue;
        };
        let Some(name) = fields.next() else {
            continue;
        };
        let name = name.strip_prefix('*').unwrap_or(name);
        if name == filename {
            if matched || !digest.eq_ignore_ascii_case(expected) {
                return Err(invalid(
                    "signed SHA256SUMS entry is duplicate or mismatched",
                ));
            }
            matched = true;
        }
    }
    if !matched {
        return Err(invalid(
            "signed SHA256SUMS omits the locked Ubuntu Base archive",
        ));
    }
    Ok(())
}

pub(crate) fn sha256_file(path: &Path) -> Result<String, Box<dyn Error>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid("hash input must be a regular non-symlink file"));
    }
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 128 * 1024].into_boxed_slice();
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    let mut encoded = String::with_capacity(64);
    for byte in digest.finalize() {
        use std::fmt::Write as _;
        write!(&mut encoded, "{byte:02x}").expect("writing into String cannot fail");
    }
    Ok(encoded)
}

pub(crate) fn now_unix_ms() -> Result<u128, Box<dyn Error>> {
    Ok(SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis())
}

pub(crate) fn invalid(message: &str) -> Box<dyn Error> {
    std::io::Error::other(message.to_owned()).into()
}
