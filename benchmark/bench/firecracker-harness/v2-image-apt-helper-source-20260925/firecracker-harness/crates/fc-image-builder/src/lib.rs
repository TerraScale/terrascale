#![forbid(unsafe_code)]

pub mod apt;
pub mod build;
pub mod cli_compiler;
pub(crate) mod firecracker_release;
pub mod input;
pub mod manifest;
pub(crate) mod oci_archive;
pub mod package_lock;
pub mod podman;
pub mod qualify;
pub mod recipe;
pub mod run_dir;
pub mod signature;
pub mod tool;
pub mod workflow;

pub use apt::{
    AptIndexError, PackageIndexPin, VerifiedDecompressedIndex, VerifiedPackageIndex,
    decompress_package_index, decompress_package_index_in_podman, verify_package_index,
};
pub use build::{build_qualified_image, write_root_tree_inventory};
pub use cli_compiler::{compile_pinned_cli, prepare_cli_inputs, verify_cli_compile_receipt};
pub use input::{ArtifactPin, DownloadReceipt, FetchError, HttpsFetcher};
pub use manifest::{
    AptIndexReceipt, AptPackageOrigin, AptPackageReceipt, AptReceipt, ArtifactOutput,
    BenchSourceReceipt, CommandReceipt, Ext4Receipt, HostPodmanReceipt, ImageManifest,
    KernelReceipt, ManifestError, OciSmokeReceipt, RecipeIdentityReceipt,
    ReproducibilityDifference, ReproducibilityReceipt, ReproducibilityStatus, ResourceLimits,
    RootfsReceipt, RustCliBuilderReceipt, SignatureReceipt, SourceFileReceipt,
    UbuntuBuildBuilderReceipt, UbuntuToolReceipt, resolve_output_path,
};
pub use package_lock::{
    AptSettingsCandidate, AptSolverReceipt, BuilderToolReceipt, BuilderToolchainReceipt,
    CandidateIndex, CandidatePackage, CandidatePackageLock, DependencyEdge, PackageLockError,
    verify_locked_package_origins,
};
pub use podman::{ContainerMount, NetworkMode, PodmanError, PodmanExecutor, VerifiedLocalImage};
pub use qualify::qualify_apt_inputs;
pub use recipe::{Recipe, RecipeError, RecipeFile, RecipeGaps};
pub use run_dir::{RunDirectory, RunDirectoryError, StagedFile};
pub use signature::{
    SignatureError, SignatureRequest, VerifiedSignature, verify_clearsigned_file,
    verify_detached_signature, verify_signature_in_podman,
};
pub use tool::{CommandRunner, CommandSpec, ToolError, ToolPin};
pub use workflow::{fetch_qualified_inputs, validate_qualified_inputs};
