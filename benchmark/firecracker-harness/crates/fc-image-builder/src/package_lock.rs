use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, Read};
use std::path::Path;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{AptPackageOrigin, RecipeFile, VerifiedDecompressedIndex};

const RESEARCH_PACKET_SHA256: &str =
    "00a430ce9dc7e793cf05a81581ffdc75dab495ee74cbd5c21f9e2d567af3632d";
const SNAPSHOT: &str = "20260910T164400Z";
const ARCHIVE_FINGERPRINT: &str = "F6ECB3762474EDA9D21B7022871920D1991BC93C";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CandidatePackageLock {
    pub schema_version: u32,
    pub qualification_status: String,
    pub package_role: String,
    pub recipe_identity: String,
    pub recipe_version: String,
    pub research_packet_sha256: Option<String>,
    pub snapshot_timestamp: String,
    pub apt_version: String,
    pub apt_sha256: String,
    pub apt_settings_candidate: AptSettingsCandidate,
    #[serde(default)]
    pub apt_solver_receipt: Option<AptSolverReceipt>,
    #[serde(default)]
    pub builder_toolchain_receipt: Option<BuilderToolchainReceipt>,
    pub indexes: Vec<CandidateIndex>,
    pub packages: Vec<CandidatePackage>,
    pub groups: BTreeMap<String, Vec<String>>,
    pub group_packages: BTreeMap<String, Vec<String>>,
    pub dependency_edges: BTreeMap<String, Vec<DependencyEdge>>,
    pub limitations: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
#[allow(clippy::struct_excessive_bools)] // These fields directly encode independent APT safety settings.
pub struct AptSettingsCandidate {
    pub architecture: String,
    pub components: Vec<String>,
    pub suites: Vec<String>,
    pub snapshot_timestamp: String,
    pub apt_version: String,
    pub apt_sha256: String,
    pub allow_unauthenticated: bool,
    pub install_recommends: bool,
    pub install_suggests: bool,
    pub check_valid_until: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CandidateIndex {
    pub suite: String,
    pub component: String,
    pub architecture: String,
    pub inrelease_url: String,
    pub inrelease_sha256: String,
    pub inrelease_size_bytes: u64,
    pub packages_index_url: String,
    pub packages_index_sha256: String,
    pub packages_index_size_bytes: u64,
    pub signer_primary_fingerprint: String,
    pub trusted_key_source: String,
    pub trusted_key_sha256: String,
    pub gpgv_verified: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AptSolverReceipt {
    pub command_argv: Vec<String>,
    pub stdout_sha256: String,
    pub stderr_sha256: String,
    pub exit_status: i32,
    pub apt_version: String,
    pub apt_executable_path: String,
    pub apt_executable_sha256: String,
    pub locale: String,
    pub baseline_status_sha256: String,
    pub baseline_inventory_sha256: String,
    pub selected_inventory_sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct BuilderToolchainReceipt {
    pub derived_oci_digest: String,
    pub tools: Vec<BuilderToolReceipt>,
    pub build_argv: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct BuilderToolReceipt {
    pub name: String,
    pub path: String,
    pub version: String,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CandidatePackage {
    pub name: String,
    pub version: String,
    pub architecture: String,
    pub suite: String,
    pub component: String,
    pub filename: String,
    pub source_url: String,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DependencyEdge {
    pub from: String,
    pub from_version: String,
    pub to: String,
    pub to_architecture: String,
    pub to_version: String,
    pub field: String,
    pub expression: String,
}

#[derive(Debug)]
pub enum PackageLockError {
    Io(io::Error),
    Json(serde_json::Error),
    Invalid(String),
}

impl fmt::Display for PackageLockError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "package lock I/O failed: {error}"),
            Self::Json(error) => write!(formatter, "candidate package packet is invalid: {error}"),
            Self::Invalid(error) => write!(formatter, "candidate package lock rejected: {error}"),
        }
    }
}

impl Error for PackageLockError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Json(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

impl From<io::Error> for PackageLockError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for PackageLockError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

impl CandidatePackageLock {
    /// Read a size-bounded package lock tied to the exact recipe bytes.
    ///
    /// # Errors
    ///
    /// Returns an error if the lock path, JSON, candidate identities, or
    /// recipe provenance are invalid.
    pub fn read(path: &Path, recipe: &RecipeFile) -> Result<Self, PackageLockError> {
        let metadata = fs::symlink_metadata(path)?;
        if metadata.file_type().is_symlink()
            || !metadata.is_file()
            || metadata.len() > 16 * 1024 * 1024
        {
            return Err(PackageLockError::Invalid(
                "candidate lock must be a regular file no larger than 16 MiB".to_owned(),
            ));
        }
        let bytes = fs::read(path)?;
        let lock: Self = serde_json::from_slice(&bytes)?;
        lock.validate()?;
        if lock.recipe_identity != recipe.recipe().identity
            || lock.recipe_version != recipe.recipe().recipe_version
        {
            return Err(PackageLockError::Invalid(
                "package lock belongs to a different recipe identity or version".to_owned(),
            ));
        }
        if lock.is_apt_solver_verified() {
            let (relative, expected_sha256) = match lock.package_role.as_str() {
                "guest" => (
                    recipe.recipe().guest_apt.packages_lock_path.as_str(),
                    recipe.recipe().guest_apt.packages_lock_sha256.as_str(),
                ),
                "builder" => (
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
                _ => unreachable!("package role was validated"),
            };
            let expected_path = recipe
                .resolve_recipe_path(relative)
                .map_err(|error| PackageLockError::Invalid(error.to_string()))?;
            let actual_path = fs::canonicalize(path)?;
            if !is_sha256(expected_sha256)
                || actual_path != expected_path
                || sha256_bytes(&bytes) != expected_sha256
            {
                return Err(PackageLockError::Invalid(
                    "verified package lock does not match its recipe path and pinned hash"
                        .to_owned(),
                ));
            }
            if lock.package_role == "builder" {
                let expected = &recipe.recipe().ubuntu_build_builder;
                let receipt = lock.builder_toolchain_receipt.as_ref().ok_or_else(|| {
                    PackageLockError::Invalid(
                        "builder lock lacks its derived image and tool receipt".to_owned(),
                    )
                })?;
                if receipt.derived_oci_digest != expected.derived_image_digest
                    || receipt.tools.len() != expected.tools.len()
                    || expected.tools.iter().any(|tool| {
                        !receipt.tools.iter().any(|recorded| {
                            recorded.name == tool.name
                                && recorded.path == tool.path
                                && recorded.version == tool.version
                                && recorded.sha256 == tool.sha256
                        })
                    })
                {
                    return Err(PackageLockError::Invalid(
                        "builder toolchain lock differs from the recipe's image or tool pins"
                            .to_owned(),
                    ));
                }
            }
        }
        Ok(lock)
    }

    /// Normalize the fixed external research packet into a candidate lock.
    /// This validates graph completeness but deliberately does not promote
    /// unverified signatures or a non-APT resolver result to a trusted lock.
    ///
    /// # Errors
    ///
    /// Returns an error if the packet path, digest, schema, or dependency graph
    /// differs from the reviewed candidate.
    pub fn from_research_packet(
        recipe: &RecipeFile,
        packet_path: &Path,
        package_role: &str,
    ) -> Result<Self, PackageLockError> {
        let metadata = fs::symlink_metadata(packet_path)?;
        if metadata.file_type().is_symlink()
            || !metadata.is_file()
            || metadata.len() > 16 * 1024 * 1024
        {
            return Err(PackageLockError::Invalid(
                "research packet must be a regular file no larger than 16 MiB".to_owned(),
            ));
        }
        let bytes = fs::read(packet_path)?;
        Self::from_research_packet_bytes(recipe, &bytes, package_role)
    }

    fn from_research_packet_bytes(
        recipe: &RecipeFile,
        bytes: &[u8],
        package_role: &str,
    ) -> Result<Self, PackageLockError> {
        if !["guest", "builder"].contains(&package_role) {
            return Err(PackageLockError::Invalid(
                "package role must be guest or builder".to_owned(),
            ));
        }
        if bytes.len() > 16 * 1024 * 1024 {
            return Err(PackageLockError::Invalid(
                "research packet must be no larger than 16 MiB".to_owned(),
            ));
        }
        let packet_sha256 = sha256_bytes(bytes);
        if packet_sha256 != RESEARCH_PACKET_SHA256
            || packet_sha256 != recipe.recipe().guest_apt.candidate_packet_sha256
        {
            return Err(PackageLockError::Invalid(
                "research packet digest differs from the candidate recipe".to_owned(),
            ));
        }
        let packet: ResearchPacket = serde_json::from_slice(bytes)?;
        let lock = Self::from_packet(packet, recipe, packet_sha256, package_role)?;
        lock.validate()?;
        Ok(lock)
    }

    /// Validate the fixed candidate graph without claiming signed or APT proof.
    ///
    /// # Errors
    ///
    /// Returns an error if any candidate lock identity, index, package, or
    /// dependency edge is incomplete or inconsistent.
    pub fn validate(&self) -> Result<(), PackageLockError> {
        let is_candidate = self.qualification_status == "candidate-unverified";
        let is_verified = self.qualification_status == "apt-solver-verified";
        if self.schema_version != 2
            || (!is_candidate && !is_verified)
            || !["guest", "builder"].contains(&self.package_role.as_str())
            || self.recipe_identity != "terrabase-noble-podman-v2"
            || self.recipe_version != "2.0.0"
            || self.snapshot_timestamp != SNAPSHOT
            || self.apt_version != "2.8.3"
            || self.apt_settings_candidate.architecture != "amd64"
            || self.apt_settings_candidate.snapshot_timestamp != SNAPSHOT
            || self.apt_settings_candidate.apt_version != self.apt_version
            || self.apt_settings_candidate.apt_sha256 != self.apt_sha256
            || self.apt_settings_candidate.allow_unauthenticated
            || self.apt_settings_candidate.install_recommends
            || self.apt_settings_candidate.install_suggests
            || !self.apt_settings_candidate.check_valid_until
            || self.indexes.len() != 6
            || self.packages.is_empty()
            || self.groups.len() != 1
            || self.group_packages.len() != 1
            || self.dependency_edges.len() != 1
        {
            return Err(PackageLockError::Invalid(
                "package lock lacks the fixed snapshot, role or complete dependency group"
                    .to_owned(),
            ));
        }
        if is_candidate
            && (self.apt_solver_receipt.is_some()
                || self.limitations.is_empty()
                || !self
                    .limitations
                    .iter()
                    .any(|item| item.contains("not apt-solver verified"))
                || !self
                    .limitations
                    .iter()
                    .any(|item| item.contains("signature")))
        {
            return Err(PackageLockError::Invalid(
                "candidate lock must retain explicit signature and solver limitations".to_owned(),
            ));
        }
        if is_verified {
            if !self.limitations.is_empty() {
                return Err(PackageLockError::Invalid(
                    "apt-solver-verified lock cannot retain candidate limitations".to_owned(),
                ));
            }
            self.validate_solver_receipt()?;
            self.validate_builder_toolchain_receipt()?;
        }
        if is_candidate && self.research_packet_sha256.as_deref() != Some(RESEARCH_PACKET_SHA256) {
            return Err(PackageLockError::Invalid(
                "candidate research packet identity changed".to_owned(),
            ));
        }
        if is_verified && self.research_packet_sha256.is_some() {
            return Err(PackageLockError::Invalid(
                "solver-verified lock cannot substitute the research graph for solver output"
                    .to_owned(),
            ));
        }
        validate_sha256(&self.apt_sha256, "apt package archive")?;
        if !self.apt_settings_candidate.suites.iter().eq([
            "noble",
            "noble-updates",
            "noble-security",
        ]
        .iter()
        .copied())
            || !self
                .apt_settings_candidate
                .components
                .iter()
                .eq(["main", "universe"].iter().copied())
        {
            return Err(PackageLockError::Invalid(
                "apt settings differ from the candidate snapshot selection".to_owned(),
            ));
        }
        validate_indexes(&self.indexes, is_verified)?;
        validate_packages(&self.packages)?;
        validate_groups(self)?;
        Ok(())
    }

    /// Return whether both signed indexes and the pinned apt simulation receipt
    /// qualify this role-specific closure for fetch/build.
    #[must_use]
    pub fn is_apt_solver_verified(&self) -> bool {
        self.qualification_status == "apt-solver-verified"
    }

    fn validate_solver_receipt(&self) -> Result<(), PackageLockError> {
        let receipt = self.apt_solver_receipt.as_ref().ok_or_else(|| {
            PackageLockError::Invalid(
                "apt-solver-verified lock is missing its simulation receipt".to_owned(),
            )
        })?;
        if receipt.exit_status != 0
            || receipt
                .command_argv
                .first()
                .is_none_or(|program| !program.ends_with("apt-get") && program != "apt-get")
            || !receipt
                .command_argv
                .iter()
                .any(|argument| argument == "-s" || argument == "--simulate")
            || receipt.apt_version != self.apt_version
            || receipt.apt_executable_path != "/usr/bin/apt-get"
            || receipt.locale != "C"
        {
            return Err(PackageLockError::Invalid(
                "apt simulation argv or exit status is invalid".to_owned(),
            ));
        }
        validate_sha256(&receipt.stdout_sha256, "apt simulation stdout")?;
        validate_sha256(&receipt.stderr_sha256, "apt simulation stderr")?;
        validate_sha256(&receipt.apt_executable_sha256, "apt-get executable")?;
        validate_sha256(&receipt.baseline_status_sha256, "APT baseline dpkg status")?;
        validate_sha256(&receipt.baseline_inventory_sha256, "APT baseline inventory")?;
        validate_sha256(&receipt.selected_inventory_sha256, "APT selected inventory")?;
        Ok(())
    }

    fn validate_builder_toolchain_receipt(&self) -> Result<(), PackageLockError> {
        let Some(receipt) = &self.builder_toolchain_receipt else {
            if self.package_role == "builder" {
                return Err(PackageLockError::Invalid(
                    "builder lock is missing derived-image toolchain provenance".to_owned(),
                ));
            }
            return Ok(());
        };
        if self.package_role != "builder"
            || receipt.build_argv.is_empty()
            || receipt
                .build_argv
                .first()
                .is_none_or(|program| !program.ends_with("podman") && !program.ends_with("buildah"))
            || !receipt
                .build_argv
                .iter()
                .any(|argument| argument == "build")
            || !receipt
                .build_argv
                .iter()
                .any(|argument| argument == "--network=none")
        {
            return Err(PackageLockError::Invalid(
                "builder toolchain image digest or offline build argv is invalid".to_owned(),
            ));
        }
        validate_oci_digest(&receipt.derived_oci_digest)?;
        validate_builder_tool_receipts(&receipt.tools)?;
        Ok(())
    }

    #[allow(clippy::too_many_lines)] // This is a direct field-preserving import from one fixed research schema.
    fn from_packet(
        mut packet: ResearchPacket,
        recipe: &RecipeFile,
        packet_sha256: String,
        package_role: &str,
    ) -> Result<Self, PackageLockError> {
        if packet.schema_version != 1
            || packet.snapshot_timestamp != SNAPSHOT
            || !packet.status.contains("candidate")
            || !packet.missing_seeds.is_empty()
            || !packet.unresolved.is_empty()
            || packet.apt_settings_candidate.status.is_empty()
        {
            return Err(PackageLockError::Invalid(
                "research packet is not the expected complete candidate graph".to_owned(),
            ));
        }
        let indexes = packet
            .sources
            .iter()
            .flat_map(|source| {
                source
                    .packages_indexes
                    .iter()
                    .map(move |index| CandidateIndex {
                        suite: source.suite.clone(),
                        component: index.component.clone(),
                        architecture: index.architecture.clone(),
                        inrelease_url: source.inrelease_url.clone(),
                        inrelease_sha256: source.inrelease_sha256.clone(),
                        inrelease_size_bytes: source.inrelease_size,
                        packages_index_url: index.url.clone(),
                        packages_index_sha256: index.sha256.clone(),
                        packages_index_size_bytes: index.size,
                        signer_primary_fingerprint: source.issuer_key_fingerprint.clone(),
                        trusted_key_source: String::new(),
                        trusted_key_sha256: String::new(),
                        gpgv_verified: false,
                    })
            })
            .collect();
        let member_names = packet
            .group_packages
            .get(package_role)
            .ok_or_else(|| {
                PackageLockError::Invalid(format!("research packet lacks {package_role} closure"))
            })?
            .iter()
            .cloned()
            .collect::<BTreeSet<_>>();
        let packages = packet
            .packages
            .into_iter()
            .filter(|package| member_names.contains(&package.name))
            .map(|package| CandidatePackage {
                name: package.name,
                version: package.version,
                architecture: package.arch,
                suite: package.suite,
                component: package.component,
                filename: package.filename,
                source_url: package.url,
                size_bytes: package.size,
                sha256: package.sha256,
            })
            .collect();
        let edges = packet
            .dependency_edges
            .remove(package_role)
            .ok_or_else(|| {
                PackageLockError::Invalid(format!("research packet lacks {package_role} edges"))
            })?
            .into_iter()
            .map(|edge| DependencyEdge {
                from: edge.from,
                from_version: edge.from_version,
                to: edge.to,
                to_architecture: edge.to_arch,
                to_version: edge.to_version,
                field: edge.field,
                expression: edge.expression,
            })
            .collect();
        let groups = BTreeMap::from([(
            package_role.to_owned(),
            packet.groups.remove(package_role).ok_or_else(|| {
                PackageLockError::Invalid(format!("research packet lacks {package_role} roots"))
            })?,
        )]);
        let group_packages =
            BTreeMap::from([(package_role.to_owned(), member_names.into_iter().collect())]);
        let dependency_edges = BTreeMap::from([(package_role.to_owned(), edges)]);
        let settings = packet.apt_settings_candidate;
        Ok(Self {
            schema_version: 2,
            qualification_status: "candidate-unverified".to_owned(),
            package_role: package_role.to_owned(),
            recipe_identity: recipe.recipe().identity.clone(),
            recipe_version: recipe.recipe().recipe_version.clone(),
            research_packet_sha256: Some(packet_sha256),
            snapshot_timestamp: settings.snapshot_timestamp.clone(),
            apt_version: settings.apt_version.clone(),
            apt_sha256: settings.apt_sha256.clone(),
            apt_settings_candidate: AptSettingsCandidate {
                architecture: settings.architecture,
                components: settings.components,
                suites: settings.suites,
                snapshot_timestamp: settings.snapshot_timestamp,
                apt_version: settings.apt_version,
                apt_sha256: settings.apt_sha256,
                allow_unauthenticated: settings.allow_unauthenticated,
                install_recommends: settings.install_recommends,
                install_suggests: settings.install_suggests,
                check_valid_until: settings.check_valid_until,
            },
            apt_solver_receipt: None,
            builder_toolchain_receipt: None,
            indexes,
            packages,
            groups,
            group_packages,
            dependency_edges,
            limitations: vec![
                "research graph is not apt-solver verified".to_owned(),
                "InRelease and Canonical signatures have not been verified by pinned gpgv"
                    .to_owned(),
                "candidate package hashes must be matched to downloaded signed Packages indexes"
                    .to_owned(),
            ],
        })
    }
}

impl CandidatePackageLock {
    /// Write this explicitly unverified candidate graph to a new run-owned file.
    ///
    /// # Errors
    ///
    /// Returns an error if the graph fails validation or the run-owned output
    /// cannot be written.
    pub fn write_new(&self, run: &crate::RunDirectory) -> Result<String, PackageLockError> {
        self.validate()?;
        let bytes = serde_json::to_vec_pretty(self)?;
        let path = run
            .write_new(
                &format!("{}-packages.lock.candidate.json", self.package_role),
                &bytes,
            )
            .map_err(|error| PackageLockError::Invalid(error.to_string()))?;
        Ok(path.display().to_string())
    }

    /// Check every candidate package row for one suite/component against the
    /// authenticated, decompressed Packages index produced by the pinned xz
    /// tool. The index's signature, URL, compressed-byte hash and size must
    /// already have passed `verify_package_index`.
    ///
    /// # Errors
    ///
    /// Returns an error if any expected package row is absent or differs from
    /// the authenticated index.
    pub fn verify_packages_from_index(
        &self,
        decompressed: &VerifiedDecompressedIndex,
    ) -> Result<usize, PackageLockError> {
        self.validate()?;
        let index = decompressed.index();
        let packages_text_path = decompressed.path();
        let metadata = fs::symlink_metadata(packages_text_path)?;
        if metadata.file_type().is_symlink()
            || !metadata.is_file()
            || metadata.len() > 512 * 1024 * 1024
        {
            return Err(PackageLockError::Invalid(
                "decompressed Packages index must be a regular file no larger than 512 MiB"
                    .to_owned(),
            ));
        }
        let expected = self
            .packages
            .iter()
            .filter(|package| {
                package.suite == index.suite() && package.component == index.component()
            })
            .collect::<Vec<_>>();
        if index.architecture() != "amd64" {
            return Err(PackageLockError::Invalid(
                "authenticated Packages index has an unexpected architecture".to_owned(),
            ));
        }
        verify_expected_package_records(
            &expected,
            index.suite(),
            index.component(),
            BufReader::new(File::open(packages_text_path)?),
        )
    }
}

/// Recheck both role locks against one signed Packages index and collect every
/// signed origin for locked name/version/architecture tuples.
///
/// # Errors
///
/// Returns an error if an expected source row is absent or differs from its
/// signed index, or if role locks disagree about bytes for the same tuple.
pub fn verify_locked_package_origins(
    locks: &[&CandidatePackageLock],
    decompressed: &VerifiedDecompressedIndex,
) -> Result<Vec<AptPackageOrigin>, PackageLockError> {
    type PackageIdentity = (String, String, String);
    type SourceIdentity = (String, String, PackageIdentity);

    let index = decompressed.index();
    let metadata = fs::symlink_metadata(decompressed.path())?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() > 512 * 1024 * 1024
        || index.architecture() != "amd64"
    {
        return Err(PackageLockError::Invalid(
            "authenticated Packages index is not a bounded regular amd64 file".to_owned(),
        ));
    }
    let mut locked = BTreeMap::<PackageIdentity, Vec<&CandidatePackage>>::new();
    let mut required_origins = BTreeSet::<SourceIdentity>::new();
    for lock in locks {
        for package in &lock.packages {
            let identity = (
                package.name.clone(),
                package.version.clone(),
                package.architecture.clone(),
            );
            let records = locked.entry(identity.clone()).or_default();
            if records.iter().any(|existing| {
                existing.filename != package.filename
                    || existing.size_bytes != package.size_bytes
                    || !existing.sha256.eq_ignore_ascii_case(&package.sha256)
            }) {
                return Err(PackageLockError::Invalid(
                    "guest and builder locks disagree on selected package bytes".to_owned(),
                ));
            }
            records.push(package);
            required_origins.insert((package.suite.clone(), package.component.clone(), identity));
        }
    }

    let mut found_origins = BTreeSet::<SourceIdentity>::new();
    let mut origins = Vec::new();
    parse_packages_index(File::open(decompressed.path())?, |record| {
        let identity = (
            record.name.clone(),
            record.version.clone(),
            record.architecture.clone(),
        );
        let Some(expected_records) = locked.get(&identity) else {
            return Ok(());
        };
        for expected in expected_records.iter().filter(|package| {
            package.suite == index.suite() && package.component == index.component()
        }) {
            if expected.filename != record.filename
                || expected.size_bytes != record.size
                || !expected.sha256.eq_ignore_ascii_case(&record.sha256)
            {
                return Err(PackageLockError::Invalid(format!(
                    "candidate package metadata differs in signed index for {} {}",
                    expected.name, expected.version
                )));
            }
            found_origins.insert((
                expected.suite.clone(),
                expected.component.clone(),
                identity.clone(),
            ));
        }
        origins.push(AptPackageOrigin {
            name: record.name,
            version: record.version,
            architecture: record.architecture,
            suite: index.suite().to_owned(),
            component: index.component().to_owned(),
            source_url: format!(
                "https://snapshot.ubuntu.com/ubuntu/{SNAPSHOT}/{}",
                record.filename
            ),
            filename: record.filename,
            size_bytes: record.size,
            deb_sha256: record.sha256,
        });
        Ok(())
    })?;
    let expected_here = required_origins
        .iter()
        .filter(|(suite, component, _)| suite == index.suite() && component == index.component())
        .cloned()
        .collect::<BTreeSet<_>>();
    validate_index_origin_set(
        index.suite(),
        index.component(),
        &expected_here,
        &found_origins,
    )?;
    Ok(origins)
}

fn verify_expected_package_records(
    expected: &[&CandidatePackage],
    suite: &str,
    component: &str,
    records: impl BufRead,
) -> Result<usize, PackageLockError> {
    let mut found = BTreeSet::new();
    parse_packages_index(records, |record| {
        let Some(package) = expected.iter().find(|package| {
            package.name == record.name
                && package.version == record.version
                && package.architecture == record.architecture
                && package.filename == record.filename
        }) else {
            return Ok(());
        };
        if package.size_bytes != record.size
            || !package.sha256.eq_ignore_ascii_case(&record.sha256)
            || !found.insert((
                package.name.as_str(),
                package.version.as_str(),
                package.architecture.as_str(),
            ))
        {
            return Err(PackageLockError::Invalid(format!(
                "candidate package metadata differs in signed index for {} {}",
                package.name, package.version
            )));
        }
        Ok(())
    })?;
    if found.len() != expected.len() {
        return Err(PackageLockError::Invalid(format!(
            "signed {suite} {component} index contains {} of {} locked package rows",
            found.len(),
            expected.len()
        )));
    }
    Ok(found.len())
}

fn validate_index_origin_set(
    suite: &str,
    component: &str,
    expected: &BTreeSet<(String, String, (String, String, String))>,
    found: &BTreeSet<(String, String, (String, String, String))>,
) -> Result<(), PackageLockError> {
    if found == expected {
        return Ok(());
    }
    Err(PackageLockError::Invalid(format!(
        "signed {suite} {component} index does not contain every locked package source row"
    )))
}

fn validate_indexes(
    indexes: &[CandidateIndex],
    require_gpgv_proof: bool,
) -> Result<(), PackageLockError> {
    let mut seen = BTreeSet::new();
    for index in indexes {
        let key = (&index.suite, &index.component, &index.architecture);
        if !seen.insert(key)
            || !["noble", "noble-updates", "noble-security"].contains(&index.suite.as_str())
            || !["main", "universe"].contains(&index.component.as_str())
            || index.architecture != "amd64"
            || index.signer_primary_fingerprint != ARCHIVE_FINGERPRINT
            || index.gpgv_verified != require_gpgv_proof
            || index.inrelease_size_bytes == 0
            || index.packages_index_size_bytes == 0
        {
            return Err(PackageLockError::Invalid(
                "signed apt index candidate identity is incomplete or unexpected".to_owned(),
            ));
        }
        validate_sha256(&index.inrelease_sha256, "InRelease")?;
        validate_sha256(&index.packages_index_sha256, "Packages index")?;
        if require_gpgv_proof {
            if index.trusted_key_source.is_empty() {
                return Err(PackageLockError::Invalid(
                    "verified apt index lacks its initial trusted-key source".to_owned(),
                ));
            }
            validate_sha256(&index.trusted_key_sha256, "apt trusted keyring")?;
        } else if !index.trusted_key_source.is_empty() || !index.trusted_key_sha256.is_empty() {
            return Err(PackageLockError::Invalid(
                "candidate apt index cannot claim a trusted keyring".to_owned(),
            ));
        }
        validate_snapshot_url(&index.inrelease_url, &index.suite, "InRelease")?;
        validate_snapshot_url(
            &index.packages_index_url,
            &index.suite,
            &format!("{}/binary-amd64/Packages.xz", index.component),
        )?;
    }
    Ok(())
}

fn validate_packages(packages: &[CandidatePackage]) -> Result<(), PackageLockError> {
    let mut identities = BTreeSet::new();
    for package in packages {
        if package.name.is_empty()
            || package.version.is_empty()
            || !["amd64", "all"].contains(&package.architecture.as_str())
            || !["noble", "noble-updates", "noble-security"].contains(&package.suite.as_str())
            || !["main", "universe"].contains(&package.component.as_str())
            || package.size_bytes == 0
            || !package.filename.starts_with("pool/")
            || package
                .filename
                .split('/')
                .any(|part| part.is_empty() || part == "." || part == "..")
            || package.source_url
                != format!(
                    "https://snapshot.ubuntu.com/ubuntu/{SNAPSHOT}/{}",
                    package.filename
                )
            || !identities.insert((
                package.name.as_str(),
                package.version.as_str(),
                package.architecture.as_str(),
            ))
        {
            return Err(PackageLockError::Invalid(
                "candidate .deb identity or URL is invalid".to_owned(),
            ));
        }
        validate_sha256(&package.sha256, ".deb")?;
    }
    Ok(())
}

fn validate_builder_tool_receipts(tools: &[BuilderToolReceipt]) -> Result<(), PackageLockError> {
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
        return Err(PackageLockError::Invalid(
            "builder toolchain receipt must inventory every required executable".to_owned(),
        ));
    }
    let names = tools
        .iter()
        .map(|tool| tool.name.as_str())
        .collect::<BTreeSet<_>>();
    if names.len() != REQUIRED.len() || REQUIRED.iter().any(|name| !names.contains(name)) {
        return Err(PackageLockError::Invalid(
            "builder toolchain receipt has missing or duplicate executable names".to_owned(),
        ));
    }
    for tool in tools {
        if tool.version.is_empty() || !Path::new(&tool.path).is_absolute() {
            return Err(PackageLockError::Invalid(format!(
                "builder tool {} lacks a version or absolute path",
                tool.name
            )));
        }
        validate_sha256(&tool.sha256, "builder tool executable")?;
    }
    Ok(())
}

fn validate_oci_digest(value: &str) -> Result<(), PackageLockError> {
    let digest = value.strip_prefix("sha256:").ok_or_else(|| {
        PackageLockError::Invalid("builder image identity must use sha256".to_owned())
    })?;
    validate_sha256(digest, "builder OCI image")
}

fn validate_groups(lock: &CandidatePackageLock) -> Result<(), PackageLockError> {
    let package_identities = lock
        .packages
        .iter()
        .map(|package| {
            (
                package.name.as_str(),
                package.version.as_str(),
                package.architecture.as_str(),
            )
        })
        .collect::<BTreeSet<_>>();
    let package_names = lock
        .packages
        .iter()
        .map(|package| package.name.as_str())
        .collect::<BTreeSet<_>>();
    let mut all_group_names = BTreeSet::new();
    let group_name = lock.package_role.as_str();
    let seeds = lock.groups.get(group_name).ok_or_else(|| {
        PackageLockError::Invalid(format!("missing {group_name} seed package group"))
    })?;
    let members = lock.group_packages.get(group_name).ok_or_else(|| {
        PackageLockError::Invalid(format!("missing {group_name} transitive closure"))
    })?;
    let edges = lock.dependency_edges.get(group_name).ok_or_else(|| {
        PackageLockError::Invalid(format!("missing {group_name} dependency edges"))
    })?;
    let member_names = members.iter().map(String::as_str).collect::<BTreeSet<_>>();
    if member_names.len() != members.len() || seeds.is_empty() || edges.is_empty() {
        return Err(PackageLockError::Invalid(format!(
            "{group_name} group has duplicate or missing closure data"
        )));
    }
    all_group_names.extend(member_names.iter().copied());
    for seed in seeds {
        if !member_names.contains(seed.as_str()) {
            return Err(PackageLockError::Invalid(format!(
                "{group_name} seed {seed} is missing from its package closure"
            )));
        }
    }
    for member in &member_names {
        if !package_names.contains(member) {
            return Err(PackageLockError::Invalid(format!(
                "{group_name} closure package {member} has no locked .deb row"
            )));
        }
    }
    for edge in edges {
        if !member_names.contains(edge.from.as_str())
            || !member_names.contains(edge.to.as_str())
            || edge.expression.is_empty()
            || !["depends", "predepends"].contains(&edge.field.as_str())
            || !package_identities.contains(&(
                edge.to.as_str(),
                edge.to_version.as_str(),
                edge.to_architecture.as_str(),
            ))
            || !lock
                .packages
                .iter()
                .any(|package| package.name == edge.from && package.version == edge.from_version)
        {
            return Err(PackageLockError::Invalid(format!(
                "{group_name} dependency edge does not resolve to locked package rows"
            )));
        }
    }
    if all_group_names != package_names {
        return Err(PackageLockError::Invalid(
            "candidate package rows and the union of group closures differ".to_owned(),
        ));
    }
    Ok(())
}

#[derive(Default)]
struct PackageRecordBuilder {
    name: Option<String>,
    version: Option<String>,
    architecture: Option<String>,
    filename: Option<String>,
    size: Option<u64>,
    sha256: Option<String>,
}

struct PackageRecord {
    name: String,
    version: String,
    architecture: String,
    filename: String,
    size: u64,
    sha256: String,
}

impl PackageRecordBuilder {
    fn set(&mut self, key: &str, value: &str) -> Result<(), PackageLockError> {
        let target = match key {
            "Package" => Some(&mut self.name),
            "Version" => Some(&mut self.version),
            "Architecture" => Some(&mut self.architecture),
            "Filename" => Some(&mut self.filename),
            "SHA256" => {
                if self.sha256.replace(value.to_owned()).is_some() {
                    return Err(PackageLockError::Invalid(
                        "duplicate SHA256 field in Debian package index".to_owned(),
                    ));
                }
                None
            }
            "Size" => {
                let size = value.parse::<u64>().map_err(|_| {
                    PackageLockError::Invalid(
                        "invalid Size field in Debian package index".to_owned(),
                    )
                })?;
                if self.size.replace(size).is_some() {
                    return Err(PackageLockError::Invalid(
                        "duplicate Size field in Debian package index".to_owned(),
                    ));
                }
                None
            }
            _ => None,
        };
        if let Some(target) = target {
            if value.is_empty() || target.replace(value.to_owned()).is_some() {
                return Err(PackageLockError::Invalid(format!(
                    "empty or duplicate {key} field in Debian package index"
                )));
            }
        }
        Ok(())
    }

    fn finish(self) -> Result<Option<PackageRecord>, PackageLockError> {
        if self.name.is_none()
            && self.version.is_none()
            && self.architecture.is_none()
            && self.filename.is_none()
            && self.size.is_none()
            && self.sha256.is_none()
        {
            return Ok(None);
        }
        let record = PackageRecord {
            name: take_required(self.name, "Package")?,
            version: take_required(self.version, "Version")?,
            architecture: take_required(self.architecture, "Architecture")?,
            filename: take_required(self.filename, "Filename")?,
            size: self.size.filter(|size| *size > 0).ok_or_else(|| {
                PackageLockError::Invalid(
                    "missing or zero Size field in Debian package index".to_owned(),
                )
            })?,
            sha256: take_required(self.sha256, "SHA256")?,
        };
        if !["amd64", "all"].contains(&record.architecture.as_str()) || !is_sha256(&record.sha256) {
            return Err(PackageLockError::Invalid(
                "Debian package index row has unsupported architecture or digest".to_owned(),
            ));
        }
        Ok(Some(record))
    }
}

fn parse_packages_index<R, F>(reader: R, mut visit: F) -> Result<(), PackageLockError>
where
    R: Read,
    F: FnMut(PackageRecord) -> Result<(), PackageLockError>,
{
    const MAX_TEXT_BYTES: u64 = 512 * 1024 * 1024;
    let mut reader = BufReader::new(reader);
    let mut record = PackageRecordBuilder::default();
    let mut line = String::new();
    let mut total_bytes = 0_u64;
    loop {
        line.clear();
        let read = reader.read_line(&mut line)?;
        if read == 0 {
            break;
        }
        total_bytes = total_bytes
            .checked_add(u64::try_from(read).map_err(|_| {
                PackageLockError::Invalid("Packages index length exceeds u64".to_owned())
            })?)
            .ok_or_else(|| {
                PackageLockError::Invalid("Packages index length overflows".to_owned())
            })?;
        if total_bytes > MAX_TEXT_BYTES {
            return Err(PackageLockError::Invalid(
                "decompressed Packages index exceeds its byte limit".to_owned(),
            ));
        }
        let normalized = line.trim_end_matches(['\r', '\n']);
        if normalized.is_empty() {
            if let Some(entry) = std::mem::take(&mut record).finish()? {
                visit(entry)?;
            }
            continue;
        }
        if normalized.starts_with(' ') || normalized.starts_with('\t') {
            continue;
        }
        let (key, value) = normalized.split_once(':').ok_or_else(|| {
            PackageLockError::Invalid("malformed Debian package index field".to_owned())
        })?;
        record.set(key, value.trim_start())?;
    }
    if let Some(entry) = record.finish()? {
        visit(entry)?;
    }
    Ok(())
}

fn take_required(value: Option<String>, field: &str) -> Result<String, PackageLockError> {
    value.filter(|value| !value.is_empty()).ok_or_else(|| {
        PackageLockError::Invalid(format!("missing {field} field in Debian package index"))
    })
}

fn validate_snapshot_url(url: &str, suite: &str, suffix: &str) -> Result<(), PackageLockError> {
    let expected = format!("https://snapshot.ubuntu.com/ubuntu/{SNAPSHOT}/dists/{suite}/{suffix}");
    if url != expected {
        return Err(PackageLockError::Invalid(
            "apt index URL differs from its immutable snapshot identity".to_owned(),
        ));
    }
    Ok(())
}

fn validate_sha256(value: &str, label: &str) -> Result<(), PackageLockError> {
    if !is_sha256(value) {
        return Err(PackageLockError::Invalid(format!(
            "{label} digest is not a full SHA-256"
        )));
    }
    Ok(())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_bytes(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(64);
    for byte in Sha256::digest(bytes) {
        use fmt::Write as _;
        write!(&mut output, "{byte:02x}").expect("writing into String cannot fail");
    }
    output
}

pub(crate) fn canonical_inventory_sha256(inventory: &BTreeMap<(String, String), String>) -> String {
    let mut canonical = String::new();
    for ((name, architecture), version) in inventory {
        canonical.push_str(name);
        canonical.push('\t');
        canonical.push_str(architecture);
        canonical.push('\t');
        canonical.push_str(version);
        canonical.push('\n');
    }
    sha256_bytes(canonical.as_bytes())
}

#[derive(Deserialize)]
struct ResearchPacket {
    schema_version: u32,
    status: String,
    snapshot_timestamp: String,
    apt_settings_candidate: ResearchAptSettings,
    sources: Vec<ResearchSource>,
    packages: Vec<ResearchPackage>,
    groups: BTreeMap<String, Vec<String>>,
    group_packages: BTreeMap<String, Vec<String>>,
    dependency_edges: BTreeMap<String, Vec<ResearchDependencyEdge>>,
    missing_seeds: Vec<String>,
    unresolved: Vec<String>,
}

#[derive(Deserialize)]
#[allow(clippy::struct_excessive_bools)] // Mirrors independent APT configuration values from the research packet.
struct ResearchAptSettings {
    architecture: String,
    components: Vec<String>,
    suites: Vec<String>,
    snapshot_timestamp: String,
    apt_version: String,
    apt_sha256: String,
    #[serde(rename = "APT::Get::AllowUnauthenticated")]
    allow_unauthenticated: bool,
    #[serde(rename = "APT::Install-Recommends")]
    install_recommends: bool,
    #[serde(rename = "APT::Install-Suggests")]
    install_suggests: bool,
    #[serde(rename = "Acquire::Check-Valid-Until")]
    check_valid_until: bool,
    status: String,
}

#[derive(Deserialize)]
struct ResearchSource {
    suite: String,
    inrelease_url: String,
    inrelease_sha256: String,
    inrelease_size: u64,
    issuer_key_fingerprint: String,
    packages_indexes: Vec<ResearchPackagesIndex>,
}

#[derive(Deserialize)]
struct ResearchPackagesIndex {
    component: String,
    architecture: String,
    url: String,
    sha256: String,
    size: u64,
}

#[derive(Deserialize)]
struct ResearchPackage {
    name: String,
    version: String,
    arch: String,
    suite: String,
    component: String,
    filename: String,
    url: String,
    size: u64,
    sha256: String,
}

#[derive(Deserialize)]
struct ResearchDependencyEdge {
    from: String,
    from_version: String,
    to: String,
    to_arch: String,
    to_version: String,
    field: String,
    expression: String,
}

#[cfg(test)]
mod tests {
    use super::{
        CandidatePackageLock, PackageRecord, parse_packages_index, validate_index_origin_set,
        verify_expected_package_records,
    };
    use crate::RecipeFile;
    use std::io::Cursor;
    use std::path::Path;

    const CANDIDATE_RECIPE: &[u8] =
        include_bytes!("../../../images/terrabase-noble-podman-v2/recipe.toml");
    const CANDIDATE_PACKET: &[u8] =
        include_bytes!("../../../images/terrabase-noble-podman-v2/candidate-package-research.json");

    #[test]
    fn research_packet_has_a_complete_candidate_graph_without_trust_promotion() {
        let recipe = RecipeFile::parse(Path::new("recipe.toml"), CANDIDATE_RECIPE.to_vec())
            .expect("candidate recipe parses");
        let guest_lock =
            CandidatePackageLock::from_research_packet_bytes(&recipe, CANDIDATE_PACKET, "guest")
                .expect("candidate package graph validates");
        let builder_lock =
            CandidatePackageLock::from_research_packet_bytes(&recipe, CANDIDATE_PACKET, "builder")
                .expect("candidate builder package graph validates");
        assert_eq!(guest_lock.packages.len(), 126);
        assert_eq!(builder_lock.packages.len(), 175);
        assert_eq!(guest_lock.qualification_status, "candidate-unverified");
        assert_eq!(guest_lock.indexes.len(), 6);
        assert_eq!(guest_lock.package_role, "guest");
        assert_eq!(builder_lock.package_role, "builder");
        assert!(!guest_lock.is_apt_solver_verified());
        assert!(
            guest_lock
                .limitations
                .iter()
                .any(|limitation| limitation.contains("not apt-solver verified"))
        );
    }

    #[test]
    fn signed_packages_parser_extracts_only_complete_deb_identity_fields() {
        let index = concat!(
            "Package: podman\n",
            "Version: 4.9.3+ds1-1ubuntu0.2\n",
            "Architecture: amd64\n",
            "Filename: pool/universe/p/podman/podman_4.9.3+ds1-1ubuntu0.2_amd64.deb\n",
            "Size: 12345\n",
            "SHA256: ",
            "e5c1c37e387ed14c352a744a75fbb79fb2f82573ca7bf36886e3b7333fc9ef1a\n",
            "Description: Podman\n",
            " multiline description\n\n",
        );
        let mut records = Vec::<PackageRecord>::new();
        parse_packages_index(Cursor::new(index), |record| {
            records.push(record);
            Ok(())
        })
        .expect("Debian package record parses");
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].name, "podman");
        assert_eq!(records[0].size, 12_345);
    }

    #[test]
    fn candidate_packet_has_valid_empty_signed_index_groups() {
        let recipe = RecipeFile::parse(Path::new("recipe.toml"), CANDIDATE_RECIPE.to_vec())
            .expect("candidate recipe parses");
        let guest =
            CandidatePackageLock::from_research_packet_bytes(&recipe, CANDIDATE_PACKET, "guest")
                .expect("candidate guest package graph validates");
        let builder =
            CandidatePackageLock::from_research_packet_bytes(&recipe, CANDIDATE_PACKET, "builder")
                .expect("candidate builder package graph validates");

        assert!(!guest.packages.iter().any(|package| {
            package.suite == "noble-security" && package.component == "universe"
        }));
        assert!(!builder.packages.iter().any(|package| {
            package.suite == "noble-updates" && package.component == "universe"
        }));
        assert_eq!(
            verify_expected_package_records(&[], "noble-security", "universe", Cursor::new(""),)
                .expect("a valid signed index may have no selected package rows"),
            0
        );
        validate_index_origin_set(
            "noble-updates",
            "universe",
            &std::collections::BTreeSet::default(),
            &std::collections::BTreeSet::default(),
        )
        .expect("an empty selected origin subset is valid");
    }
}
