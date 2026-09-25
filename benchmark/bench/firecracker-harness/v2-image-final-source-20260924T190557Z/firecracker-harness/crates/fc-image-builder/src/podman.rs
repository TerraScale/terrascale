use std::error::Error;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

use crate::manifest::CommandReceipt;
use crate::oci_archive::OciArchiveIdentity;
use crate::recipe::RecipeFile;
use crate::run_dir::{RunDirectory, RunDirectoryError};
use crate::tool::{CommandRunner, CommandSpec, ToolError, ToolPin};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NetworkMode {
    None,
    Slirp4netns,
}

#[derive(Debug)]
pub enum PodmanError {
    Io(std::io::Error),
    Invalid(String),
    RunDirectory(RunDirectoryError),
    Tool(ToolError),
}

impl fmt::Display for PodmanError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "Podman setup failed: {error}"),
            Self::Invalid(error) => write!(formatter, "Podman operation rejected: {error}"),
            Self::RunDirectory(error) => write!(formatter, "Podman run directory unsafe: {error}"),
            Self::Tool(error) => write!(formatter, "pinned Podman command failed: {error}"),
        }
    }
}

impl Error for PodmanError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::RunDirectory(error) => Some(error),
            Self::Tool(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

impl From<std::io::Error> for PodmanError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<RunDirectoryError> for PodmanError {
    fn from(error: RunDirectoryError) -> Self {
        Self::RunDirectory(error)
    }
}

impl From<ToolError> for PodmanError {
    fn from(error: ToolError) -> Self {
        Self::Tool(error)
    }
}

/// Rootless Podman invocation with its graph storage and runtime state confined
/// to one caller-created run directory.
#[derive(Debug)]
pub struct PodmanExecutor<'a> {
    recipe: &'a RecipeFile,
    run: &'a RunDirectory,
    runner: CommandRunner,
    tool: ToolPin,
    storage: PathBuf,
    runroot: PathBuf,
}

impl<'a> PodmanExecutor<'a> {
    /// Bind the exact host Podman executable and private storage paths.
    ///
    /// # Errors
    ///
    /// Returns an error if the host user differs from the rootless identity in
    /// the recipe or private storage directories cannot be created safely.
    pub fn new(recipe: &'a RecipeFile, run: &'a RunDirectory) -> Result<Self, PodmanError> {
        let host = &recipe.recipe().host_podman;
        if !host.rootless {
            return Err(PodmanError::Invalid(
                "image builder requires rootless host Podman".to_owned(),
            ));
        }
        validate_effective_identity(host.uid, host.gid)?;

        let storage = run.path().join("podman-storage");
        let runroot = run.path().join("podman-runroot");
        create_private_directory(&storage)?;
        create_private_directory(&runroot)?;

        let executable = PathBuf::from(&host.path);
        let parent = executable.parent().ok_or_else(|| {
            PodmanError::Invalid("pinned Podman executable has no parent directory".to_owned())
        })?;
        let base_path = parent.to_string_lossy().into_owned();
        let tool = ToolPin {
            id: "podman".to_owned(),
            path: executable,
            sha256: host.sha256.clone(),
        };
        Ok(Self {
            recipe,
            run,
            runner: CommandRunner::new(base_path, 0),
            tool,
            storage,
            runroot,
        })
    }

    /// Invoke a Podman subcommand using this run's isolated storage database.
    /// The caller checks whether nonzero exit status is expected.
    ///
    /// # Errors
    ///
    /// Returns an error if executable identity, argv, logging or process I/O
    /// validation fails.
    pub fn invoke(
        &self,
        command_id: &str,
        arguments: &[String],
    ) -> Result<CommandReceipt, PodmanError> {
        let mut full_argv = vec![
            "--root".to_owned(),
            self.storage.to_string_lossy().into_owned(),
            "--runroot".to_owned(),
            self.runroot.to_string_lossy().into_owned(),
            "--events-backend=file".to_owned(),
        ];
        full_argv.extend_from_slice(arguments);
        let receipt = self.runner.run(
            &CommandSpec {
                id: command_id.to_owned(),
                tool: self.tool.clone(),
                args: full_argv,
                working_directory: self.run.path().to_path_buf(),
                environment: Vec::new(),
            },
            self.run,
        )?;
        Ok(receipt)
    }

    /// Load a previously verified OCI archive, tag it by its config digest,
    /// and confirm the loaded image's platform and config identity.
    ///
    /// # Errors
    ///
    /// Returns an error if Podman cannot load/tag/inspect the image or its
    /// observed config identity differs from the archive receipt.
    pub(crate) fn load_verified_archive(
        &self,
        archive_path: &Path,
        identity: &OciArchiveIdentity,
        command_prefix: &str,
    ) -> Result<(VerifiedLocalImage, Vec<CommandReceipt>), PodmanError> {
        validate_archive_identity(identity)?;
        let load = self.invoke(
            &format!("{command_prefix}_load"),
            &[
                "load".to_owned(),
                format!("--input={}", archive_path.display()),
            ],
        )?;
        ensure_success(&load, "load verified offline OCI archive")?;
        let reference = deterministic_derived_reference(&identity.config_digest)?;
        let tag = self.invoke(
            &format!("{command_prefix}_tag"),
            &[
                "image".to_owned(),
                "tag".to_owned(),
                identity.config_digest.clone(),
                reference.clone(),
            ],
        )?;
        ensure_success(&tag, "tag verified local OCI image by config digest")?;
        let inspect = self.inspect_local_reference(
            &reference,
            &identity.config_digest,
            &identity.platform,
            &format!("{command_prefix}_inspect"),
        )?;
        let image = self.local_image(identity, reference)?;
        Ok((image, vec![load, tag, inspect]))
    }

    /// Bind a just-built local image to its fully checked OCI archive receipt.
    /// The in-store config ID and platform must match before a verified local
    /// image token can be used to run tools from it.
    ///
    /// # Errors
    ///
    /// Returns an error if the source reference is outside the private
    /// `localhost/fc-image-builder-derived` namespace or inspection differs
    /// from the archive identity.
    pub(crate) fn verify_built_local_image(
        &self,
        source_reference: &str,
        identity: &OciArchiveIdentity,
        command_id: &str,
    ) -> Result<(VerifiedLocalImage, Vec<CommandReceipt>), PodmanError> {
        validate_archive_identity(identity)?;
        validate_derived_local_reference(source_reference)?;
        let source_inspect = self.inspect_local_reference(
            source_reference,
            &identity.config_digest,
            &identity.platform,
            command_id,
        )?;
        let reference = deterministic_derived_reference(&identity.config_digest)?;
        let mut commands = vec![source_inspect];
        if source_reference != reference {
            let tag = self.invoke(
                &format!("{command_id}_tag"),
                &[
                    "image".to_owned(),
                    "tag".to_owned(),
                    source_reference.to_owned(),
                    reference.clone(),
                ],
            )?;
            ensure_success(&tag, "assign deterministic verified derived-image tag")?;
            commands.push(tag);
        }
        Ok((self.local_image(identity, reference)?, commands))
    }

    fn inspect_local_reference(
        &self,
        reference: &str,
        config_digest: &str,
        platform: &str,
        command_id: &str,
    ) -> Result<CommandReceipt, PodmanError> {
        validate_derived_local_reference(reference)?;
        let inspect = self.invoke(
            command_id,
            &[
                "image".to_owned(),
                "inspect".to_owned(),
                "--format={{.Os}}/{{.Architecture}} {{.Id}}".to_owned(),
                reference.to_owned(),
            ],
        )?;
        ensure_success(&inspect, "inspect verified local OCI image")?;
        let output = fs::read_to_string(self.run.path().join(&inspect.stdout_path))?;
        let (observed_platform, observed_config) = output.trim().split_once(' ').ok_or_else(|| {
            PodmanError::Invalid("Podman image inspect output is incomplete".to_owned())
        })?;
        if observed_platform != platform
            || !observed_config.eq_ignore_ascii_case(config_digest)
        {
            return Err(PodmanError::Invalid(
                "loaded local image platform or config digest differs from its OCI receipt"
                    .to_owned(),
            ));
        }
        Ok(inspect)
    }

    fn local_image(
        &self,
        identity: &OciArchiveIdentity,
        reference: String,
    ) -> Result<VerifiedLocalImage, PodmanError> {
        validate_archive_identity(identity)?;
        validate_derived_local_reference(&reference)?;
        Ok(VerifiedLocalImage {
            reference,
            manifest_digest: identity.manifest_digest.clone(),
            config_digest: identity.config_digest.clone(),
            platform: identity.platform.clone(),
            run_directory: self.run.path().to_path_buf(),
        })
    }

    /// Start one rootless container with explicit network, bind mounts and
    /// resource limits, then execute a fixed image entry point.
    ///
    /// `mounts` entries are argv pairs `(host_path, container_path, read_only)`;
    /// no shell parsing or implicit host path expansion is used.
    ///
    /// # Errors
    ///
    /// Returns an error if the image identity or mount paths are malformed, or
    /// if pinned Podman cannot record the command.
    pub fn run_container(
        &self,
        command_id: &str,
        image: &str,
        network: NetworkMode,
        entrypoint: &str,
        mounts: &[ContainerMount],
        entry_arguments: &[String],
    ) -> Result<CommandReceipt, PodmanError> {
        self.run_container_with_options(
            command_id,
            image,
            network,
            entrypoint,
            mounts,
            &[],
            &[],
            &[],
            entry_arguments,
        )
    }

    /// Run one bounded container with an explicit rootless capability set and
    /// caller-approved read-only source mounts. External source paths are
    /// matched by canonical identity before Podman receives them.
    ///
    /// # Errors
    ///
    /// Returns an error for an unapproved capability or mount, unsafe path,
    /// malformed image, or failed host Podman invocation.
    pub fn run_container_with_options(
        &self,
        command_id: &str,
        image: &str,
        network: NetworkMode,
        entrypoint: &str,
        mounts: &[ContainerMount],
        allowed_external_sources: &[PathBuf],
        capabilities: &[&str],
        environment: &[(String, String)],
        entry_arguments: &[String],
    ) -> Result<CommandReceipt, PodmanError> {
        validate_image_reference(image)?;
        self.execute_container(
            command_id,
            image,
            network,
            entrypoint,
            mounts,
            allowed_external_sources,
            capabilities,
            environment,
            entry_arguments,
            false,
        )
    }

    /// Run an image loaded from a recipe-verified offline OCI archive.
    ///
    /// This path always adds `--pull=never`; callers cannot pass arbitrary
    /// local tags because the reference is held by `VerifiedLocalImage`.
    ///
    /// # Errors
    ///
    /// Returns an error if the verified image token, run bounds, mounts, or
    /// Podman command is invalid.
    pub fn run_container_with_verified_local(
        &self,
        image: &VerifiedLocalImage,
        command_id: &str,
        network: NetworkMode,
        entrypoint: &str,
        mounts: &[ContainerMount],
        allowed_external_sources: &[PathBuf],
        capabilities: &[&str],
        environment: &[(String, String)],
        entry_arguments: &[String],
    ) -> Result<CommandReceipt, PodmanError> {
        validate_local_image(image, self.run.path())?;
        self.execute_container(
            command_id,
            &image.reference,
            network,
            entrypoint,
            mounts,
            allowed_external_sources,
            capabilities,
            environment,
            entry_arguments,
            true,
        )
    }

    /// Run one verified local image with no extra capabilities, environment,
    /// or external mount approvals.
    pub fn run_verified_container(
        &self,
        command_id: &str,
        image: &VerifiedLocalImage,
        network: NetworkMode,
        entrypoint: &str,
        mounts: &[ContainerMount],
        entry_arguments: &[String],
    ) -> Result<CommandReceipt, PodmanError> {
        self.run_container_with_verified_local(
            image,
            command_id,
            network,
            entrypoint,
            mounts,
            &[],
            &[],
            &[],
            entry_arguments,
        )
    }

    fn execute_container(
        &self,
        command_id: &str,
        image: &str,
        network: NetworkMode,
        entrypoint: &str,
        mounts: &[ContainerMount],
        allowed_external_sources: &[PathBuf],
        capabilities: &[&str],
        environment: &[(String, String)],
        entry_arguments: &[String],
        pull_never: bool,
    ) -> Result<CommandReceipt, PodmanError> {
        validate_container_path(entrypoint)?;
        if !mounts.iter().any(|mount| mount.container_path == "/work") {
            return Err(PodmanError::Invalid(
                "container execution requires an explicit /work bind mount".to_owned(),
            ));
        }
        let limits = &self.recipe.recipe().ubuntu_build_builder;
        let mut complete_argv = vec![
            "run".to_owned(),
            "--rm".to_owned(),
            "--userns=keep-id".to_owned(),
            "--read-only".to_owned(),
            "--cap-drop=all".to_owned(),
            format!("--cpus={}", limits.cpu_count),
            format!("--memory={}m", limits.memory_mib),
            format!("--pids-limit={}", limits.pids_limit),
            "--storage-opt".to_owned(),
            format!("overlay.size={}", limits.workspace_limit_bytes),
            match network {
                NetworkMode::None => "--network=none".to_owned(),
                NetworkMode::Slirp4netns => {
                    "--network=slirp4netns:allow_host_loopback=false".to_owned()
                }
            },
        ];
        if pull_never {
            complete_argv.push("--pull=never".to_owned());
        }
        for capability in capabilities {
            if !matches!(
                *capability,
                "CHOWN" | "DAC_OVERRIDE" | "FOWNER" | "SETGID" | "SETUID"
            ) {
                return Err(PodmanError::Invalid(format!(
                    "container capability {capability} is not in the rootless build allowlist"
                )));
            }
            complete_argv.push(format!("--cap-add={capability}"));
        }
        for (name, value) in environment {
            if !matches!(
                name.as_str(),
                "SOURCE_DATE_EPOCH"
                    | "KBUILD_BUILD_TIMESTAMP"
                    | "KBUILD_BUILD_USER"
                    | "KBUILD_BUILD_HOST"
                    | "KBUILD_BUILD_VERSION"
                    | "LOCALVERSION"
                    | "BUILD_SALT"
                    | "TZ"
                    | "LANG"
                    | "LC_ALL"
                    | "E2FSPROGS_FAKE_TIME"
                    | "DEBIAN_FRONTEND"
            ) || value.contains('\0')
            {
                return Err(PodmanError::Invalid(format!(
                    "container environment variable {name} is not in the build allowlist"
                )));
            }
            complete_argv.push("--env".to_owned());
            complete_argv.push(format!("{name}={value}"));
        }
        for mount in mounts {
            let source = fs::canonicalize(&mount.host_path)?;
            if source.starts_with(self.run.path()) {
                validate_mount(mount, self.run.path())?;
            } else if mount.read_only
                && allowed_external_sources.iter().any(|allowed| {
                    fs::canonicalize(allowed).is_ok_and(|canonical| canonical == source)
                })
            {
                validate_external_readonly_mount(mount)?;
            } else {
                return Err(PodmanError::Invalid(
                    "external container mounts must be approved and read-only".to_owned(),
                ));
            }
            let mut value = format!(
                "type=bind,src={},dst={}",
                source.display(),
                mount.container_path
            );
            if mount.read_only {
                value.push_str(",ro=true");
            }
            complete_argv.push("--mount".to_owned());
            complete_argv.push(value);
        }
        complete_argv.push("--tmpfs".to_owned());
        complete_argv.push("/tmp:rw,nodev,nosuid,size=1073741824".to_owned());
        complete_argv.push("--workdir=/work".to_owned());
        complete_argv.push(format!("--entrypoint={entrypoint}"));
        complete_argv.push(image.to_owned());
        complete_argv.extend_from_slice(entry_arguments);
        self.invoke(command_id, &complete_argv)
    }

    /// Copy one regular file from the pinned, already-addressed image into the
    /// private run directory without starting a process inside that image.
    ///
    /// # Errors
    ///
    /// Returns an error if create/copy/remove fails or the copied file is not a
    /// regular non-symlink file inside the run directory.
    pub fn copy_from_image(
        &self,
        command_id: &str,
        image: &str,
        source_path: &str,
        destination_name: &str,
    ) -> Result<(PathBuf, Vec<CommandReceipt>), PodmanError> {
        validate_image_reference(image)?;
        self.copy_from_image_inner(command_id, image, source_path, destination_name, false)
    }

    pub fn copy_from_verified_image(
        &self,
        command_id: &str,
        image: &VerifiedLocalImage,
        source_path: &str,
        destination_name: &str,
    ) -> Result<(PathBuf, Vec<CommandReceipt>), PodmanError> {
        validate_local_image(image, self.run.path())?;
        self.copy_from_image_inner(
            command_id,
            &image.reference,
            source_path,
            destination_name,
            true,
        )
    }

    fn copy_from_image_inner(
        &self,
        command_id: &str,
        image: &str,
        source_path: &str,
        destination_name: &str,
        pull_never: bool,
    ) -> Result<(PathBuf, Vec<CommandReceipt>), PodmanError> {
        validate_container_path(source_path)?;
        validate_run_file_name(destination_name)?;
        let container_name = format!("fcib-{}-{command_id}", self.run_id());
        if !is_safe_id(&container_name) {
            return Err(PodmanError::Invalid(
                "derived Podman container name is invalid".to_owned(),
            ));
        }
        let mut receipts = Vec::new();
        let mut create_arguments = vec![
            "create".to_owned(),
            "--name".to_owned(),
            container_name.clone(),
            "--network=none".to_owned(),
        ];
        if pull_never {
            create_arguments.push("--pull=never".to_owned());
        }
        create_arguments.extend([image.to_owned(), "/bin/true".to_owned()]);
        let create = self.invoke(&format!("{command_id}_create"), &create_arguments)?;
        ensure_success(&create, "create pinned image container")?;
        receipts.push(create);

        let destination = self.run.path().join(destination_name);
        if fs::symlink_metadata(&destination).is_ok() {
            let _ = self.invoke(
                &format!("{command_id}_cleanup"),
                &["rm".to_owned(), "--force".to_owned(), container_name],
            );
            return Err(PodmanError::Invalid(
                "copied image input destination already exists".to_owned(),
            ));
        }
        let copy = self.invoke(
            &format!("{command_id}_copy"),
            &[
                "cp".to_owned(),
                format!("{container_name}:{source_path}"),
                destination.to_string_lossy().into_owned(),
            ],
        );
        match copy {
            Ok(receipt) if receipt.status_code == 0 => receipts.push(receipt),
            Ok(receipt) => {
                let _ = self.invoke(
                    &format!("{command_id}_cleanup"),
                    &["rm".to_owned(), "--force".to_owned(), container_name],
                );
                return Err(PodmanError::Invalid(format!(
                    "Podman copy exited with status {}",
                    receipt.status_code
                )));
            }
            Err(error) => {
                let _ = self.invoke(
                    &format!("{command_id}_cleanup"),
                    &["rm".to_owned(), "--force".to_owned(), container_name],
                );
                return Err(error);
            }
        }

        let remove = self.invoke(
            &format!("{command_id}_remove"),
            &["rm".to_owned(), container_name],
        )?;
        ensure_success(&remove, "remove copied-from image container")?;
        receipts.push(remove);
        let metadata = fs::symlink_metadata(&destination)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(PodmanError::Invalid(
                "copied image input is not a regular file".to_owned(),
            ));
        }
        Ok((destination, receipts))
    }

    fn run_id(&self) -> &str {
        self.run
            .path()
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("run")
    }
}

#[derive(Clone, Debug)]
pub struct ContainerMount {
    pub host_path: PathBuf,
    pub container_path: String,
    pub read_only: bool,
}

/// Capability to execute an image that was loaded and inspected in one
/// private Podman store from an OCI archive whose descriptor graph was checked.
/// Its fields are private so callers cannot turn an arbitrary tag into a
/// verified image token.
#[derive(Clone, Debug)]
pub struct VerifiedLocalImage {
    reference: String,
    manifest_digest: String,
    config_digest: String,
    platform: String,
    run_directory: PathBuf,
}

fn validate_archive_identity(identity: &OciArchiveIdentity) -> Result<(), PodmanError> {
    let valid_oci_digest = |digest: &str| {
        digest
            .strip_prefix("sha256:")
            .is_some_and(|hash| hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit()))
    };
    if identity.archive_size_bytes == 0
        || identity.archive_sha256.len() != 64
        || !identity.archive_sha256.bytes().all(|b| b.is_ascii_hexdigit())
        || !valid_oci_digest(&identity.manifest_digest)
        || !valid_oci_digest(&identity.config_digest)
        || identity.platform != "linux/amd64"
    {
        return Err(PodmanError::Invalid(
            "OCI archive descriptor identity is malformed or unsupported".to_owned(),
        ));
    }
    Ok(())
}

fn deterministic_derived_reference(config_digest: &str) -> Result<String, PodmanError> {
    let hash = config_digest
        .strip_prefix("sha256:")
        .filter(|hash| hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit()))
        .ok_or_else(|| PodmanError::Invalid("derived image config digest is malformed".to_owned()))?;
    Ok(format!(
        "localhost/fc-image-builder-derived:cfg-{}",
        &hash[..24].to_ascii_lowercase()
    ))
}

fn validate_derived_local_reference(reference: &str) -> Result<(), PodmanError> {
    let Some(tag) = reference.strip_prefix("localhost/fc-image-builder-derived:") else {
        return Err(PodmanError::Invalid(
            "derived image reference is outside the private namespace".to_owned(),
        ));
    };
    if tag.is_empty()
        || !tag
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(PodmanError::Invalid(
            "derived image local tag contains unsupported characters".to_owned(),
        ));
    }
    Ok(())
}

fn validate_local_image(
    image: &VerifiedLocalImage,
    run_directory: &Path,
) -> Result<(), PodmanError> {
    validate_derived_local_reference(&image.reference)?;
    let expected_reference = deterministic_derived_reference(&image.config_digest)?;
    if image.run_directory != run_directory
        || image.reference != expected_reference
        || image.platform != "linux/amd64"
        || !image
            .manifest_digest
            .strip_prefix("sha256:")
            .is_some_and(|hash| hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit()))
    {
        return Err(PodmanError::Invalid(
            "verified local image token does not belong to this Podman store".to_owned(),
        ));
    }
    Ok(())
}

fn validate_effective_identity(uid: u32, gid: u32) -> Result<(), PodmanError> {
    #[cfg(target_os = "linux")]
    {
        let status = fs::read_to_string("/proc/self/status")?;
        let effective_user_ids = proc_status_ids(&status, "Uid:")?;
        let effective_group_ids = proc_status_ids(&status, "Gid:")?;
        if effective_user_ids.first() != Some(&uid)
            || effective_user_ids.get(1) != Some(&uid)
            || effective_group_ids.first() != Some(&gid)
            || effective_group_ids.get(1) != Some(&gid)
        {
            return Err(PodmanError::Invalid(format!(
                "real/effective uid/gid {:?}:{:?} differ from pinned rootless identity {uid}:{gid}",
                effective_user_ids.get(..2),
                effective_group_ids.get(..2)
            )));
        }
        Ok(())
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = (uid, gid);
        Err(PodmanError::Invalid(
            "rootless image building requires a Linux host".to_owned(),
        ))
    }
}

#[cfg(target_os = "linux")]
fn proc_status_ids(status: &str, label: &str) -> Result<Vec<u32>, PodmanError> {
    let value = status
        .lines()
        .find_map(|line| line.strip_prefix(label))
        .ok_or_else(|| PodmanError::Invalid(format!("{label} is absent from process status")))?;
    value
        .split_ascii_whitespace()
        .map(|identity| {
            identity
                .parse()
                .map_err(|_| PodmanError::Invalid(format!("{label} is not a numeric identity")))
        })
        .collect()
}

fn create_private_directory(path: &Path) -> Result<(), PodmanError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => Err(
            PodmanError::Invalid("Podman state path must be a regular directory".to_owned()),
        ),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            fs::create_dir(path)?;
            #[cfg(unix)]
            fs::set_permissions(path, std::os::unix::fs::PermissionsExt::from_mode(0o700))?;
            Ok(())
        }
        Err(error) => Err(PodmanError::Io(error)),
    }
}

fn validate_image_reference(value: &str) -> Result<(), PodmanError> {
    if !value.starts_with("docker.io/library/")
        || !value.contains("@sha256:")
        || value
            .bytes()
            .any(|byte| byte.is_ascii_whitespace() || byte == 0)
    {
        return Err(PodmanError::Invalid(
            "container image must use its exact docker.io/library sha256 digest".to_owned(),
        ));
    }
    let digest = value
        .split_once("@sha256:")
        .map(|(_, hash)| hash)
        .unwrap_or_default();
    if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(PodmanError::Invalid(
            "container image digest must contain 64 hexadecimal digits".to_owned(),
        ));
    }
    Ok(())
}

fn validate_container_path(value: &str) -> Result<(), PodmanError> {
    let path = Path::new(value);
    if !path.is_absolute()
        || path.components().any(|component| {
            !matches!(
                component,
                std::path::Component::RootDir | std::path::Component::Normal(_)
            )
        })
    {
        return Err(PodmanError::Invalid(
            "container path must be absolute and traversal-free".to_owned(),
        ));
    }
    Ok(())
}

fn validate_mount(mount: &ContainerMount, run_root: &Path) -> Result<(), PodmanError> {
    validate_container_path(&mount.container_path)?;
    let metadata = fs::symlink_metadata(&mount.host_path)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(PodmanError::Invalid(
            "container bind source must be a regular directory".to_owned(),
        ));
    }
    let path = fs::canonicalize(&mount.host_path)?;
    if !path.starts_with(run_root) {
        return Err(PodmanError::Invalid(
            "container bind mount must stay inside the private run directory".to_owned(),
        ));
    }
    Ok(())
}

fn validate_external_readonly_mount(mount: &ContainerMount) -> Result<(), PodmanError> {
    validate_container_path(&mount.container_path)?;
    let metadata = fs::symlink_metadata(&mount.host_path)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() || !mount.read_only {
        return Err(PodmanError::Invalid(
            "approved external source must be a regular read-only directory".to_owned(),
        ));
    }
    Ok(())
}

fn validate_run_file_name(value: &str) -> Result<(), PodmanError> {
    if value.is_empty()
        || value.len() > 180
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(PodmanError::Invalid(
            "copied image input filename is unsafe".to_owned(),
        ));
    }
    Ok(())
}

fn ensure_success(receipt: &CommandReceipt, what: &str) -> Result<(), PodmanError> {
    if receipt.status_code == 0 {
        Ok(())
    } else {
        Err(PodmanError::Invalid(format!(
            "failed to {what}: exit status {}",
            receipt.status_code
        )))
    }
}

fn is_safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 140
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}
