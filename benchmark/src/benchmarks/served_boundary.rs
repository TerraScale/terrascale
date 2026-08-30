//! Admission and claim policy for the served TerraBase benchmark boundary.
//!
//! The only executable boundary today uses the test-only single-member
//! qualification harness. Its retained samples are diagnostic and can never
//! support a product or production-performance claim. Process lifecycle, mTLS
//! setup, the official Go adapter, and retained-result validation live in
//! `served_runner`.

use std::path::{Path, PathBuf};

use super::lane::Workload;
use super::lane_run::SideFacts;
use super::served_runner::{
    ADAPTER_CONTRACT_IDENTITY, QUALIFICATION_SERVER_IDENTITY, QUALIFICATION_SOURCE_IDENTITY,
    QUALIFICATION_TRANSPORT_IDENTITY, ServedRunnerConfig, ServedRunnerError, run_served_runner,
};

const MAX_PRINCIPAL_BYTES: usize = 128;
const MAX_TENANT_BYTES: usize = 64;
const MAX_PROJECT_BYTES: usize = 255;
const MAX_DATABASE_BYTES: usize = 255;
const DIAGNOSTIC_BOUNDARY_CLASS: &str = "diagnostic-qualification";
const PRODUCTION_BOUNDARY_CLASS: &str = "production-candidate";
const PRODUCTION_ADMISSION_UNAVAILABLE: &str = "production benchmark admission is unavailable: \
    the runner does not launch the packaged real Member CLI or verify the signed release \
    directory, manifest revision and tree, exact Member binary digest, capability digest, and \
    run-configuration digest against the served process";
const DIAGNOSTIC_LIMIT: &str = "diagnostic qualification only: test-only single-member harness; \
    no production, durability, topology, or performance claim";

/// The future production inputs that must all bind to one served process.
///
/// Merely supplying these values never admits a run. The production variant
/// remains a typed rejection until the runner can verify every value and
/// launch the packaged Member rather than the qualification harness.
#[derive(Clone, Debug, Eq, PartialEq)]
struct ProductionCandidateBinding {
    signed_release_directory: PathBuf,
    manifest_revision: String,
    manifest_tree: String,
    member_binary_sha256: String,
    capability_sha256: String,
    run_configuration_sha256: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum BoundaryAdmission {
    DiagnosticQualification,
    ProductionCandidate(ProductionCandidateBinding),
}

/// Configuration for one admitted served-boundary attempt.
#[derive(Clone, Debug)]
pub(crate) struct ServedBoundaryConfig {
    runner: ServedRunnerConfig,
    admission: BoundaryAdmission,
}

impl ServedBoundaryConfig {
    /// Resolves the diagnostic boundary from `TERRABASE_BENCH_*` settings.
    ///
    /// Selecting `production-candidate` fails before any executable starts.
    /// Capability files or production-looking identity strings cannot upgrade
    /// the qualification harness.
    pub(crate) fn from_env(workload: Workload) -> Result<Self, String> {
        let boundary_class = std::env::var("TERRABASE_BENCH_BOUNDARY_CLASS")
            .unwrap_or_else(|_| DIAGNOSTIC_BOUNDARY_CLASS.to_owned());
        match boundary_class.as_str() {
            DIAGNOSTIC_BOUNDARY_CLASS => Ok(Self {
                runner: ServedRunnerConfig::from_env(workload)?,
                admission: BoundaryAdmission::DiagnosticQualification,
            }),
            PRODUCTION_BOUNDARY_CLASS => Err(PRODUCTION_ADMISSION_UNAVAILABLE.to_owned()),
            _ => Err(format!(
                "TERRABASE_BENCH_BOUNDARY_CLASS must equal {DIAGNOSTIC_BOUNDARY_CLASS:?}; \
                 production admission is not implemented"
            )),
        }
    }

    /// Applies admission and claim policy before process orchestration.
    pub(crate) fn validate(&self) -> Result<(), String> {
        match &self.admission {
            BoundaryAdmission::ProductionCandidate(_) => {
                return Err(PRODUCTION_ADMISSION_UNAVAILABLE.to_owned());
            }
            BoundaryAdmission::DiagnosticQualification => {}
        }
        self.runner.validate()?;
        self.validate_diagnostic_policy()
    }

    fn validate_diagnostic_policy(&self) -> Result<(), String> {
        if matches!(self.runner.workload, Workload::DocumentWrite | Workload::Mixed) {
            return Err("single-member qualification cannot substantiate regional quorum writes; \
                 mutation workloads require a packaged RF=2 production Member boundary"
                .to_owned());
        }
        if self.runner.server_identity != QUALIFICATION_SERVER_IDENTITY {
            return Err(format!(
                "diagnostic server identity must exactly equal {QUALIFICATION_SERVER_IDENTITY:?}"
            ));
        }
        if self.runner.source_identity != QUALIFICATION_SOURCE_IDENTITY {
            return Err(format!(
                "diagnostic source identity must exactly equal {QUALIFICATION_SOURCE_IDENTITY:?}"
            ));
        }
        if self.runner.transport_identity != QUALIFICATION_TRANSPORT_IDENTITY
            || self.runner.transport_identity.contains("in-process")
            || !self.runner.transport_identity.contains(ADAPTER_CONTRACT_IDENTITY)
        {
            return Err(format!(
                "diagnostic transport identity must exactly equal \
                 {QUALIFICATION_TRANSPORT_IDENTITY:?}"
            ));
        }
        if !self.runner.principal.starts_with("spiffe://") {
            return Err(
                "principal must be a SPIFFE URI matching the qualification client certificate"
                    .to_owned(),
            );
        }
        if self.runner.principal.len() > MAX_PRINCIPAL_BYTES
            || self.runner.tenant.len() > MAX_TENANT_BYTES
            || self.runner.project.len() > MAX_PROJECT_BYTES
            || self.runner.database.len() > MAX_DATABASE_BYTES
        {
            return Err("binding scope exceeds the TBEP identity or scope bounds".to_owned());
        }
        Ok(())
    }
}

/// Claim classification attached to every accepted boundary result.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ServedBoundaryClaim {
    /// A loopback, single-member, test-harness observation only.
    DiagnosticQualification,
}

impl ServedBoundaryClaim {
    /// Whether this result may enter a decision-grade product claim.
    pub(crate) const fn supports_decision_grade_claim(self) -> bool {
        false
    }

    /// The exact limitation retained with the typed result.
    pub(crate) const fn limitation(self) -> &'static str {
        DIAGNOSTIC_LIMIT
    }
}

/// A validated diagnostic result and its explicit non-claim classification.
#[derive(Debug)]
pub(crate) struct ServedBoundaryResult {
    /// Facts parsed from the official Go adapter's complete result document.
    pub facts: SideFacts,
    /// Immutable JSON path retained for audit and later diagnostic comparison.
    pub output_path: PathBuf,
    /// Exact loopback endpoint announced by the qualification harness.
    pub endpoint: String,
    /// Typed evidence class. It is never inferred from a filename or identity.
    pub claim: ServedBoundaryClaim,
}

/// A refused admission or failed orchestration attempt.
#[derive(Debug)]
pub(crate) struct ServedBoundaryError {
    reason: String,
    retained_path: Option<PathBuf>,
}

impl ServedBoundaryError {
    /// Returns the operator-facing failure reason.
    pub(crate) fn reason(&self) -> &str {
        &self.reason
    }

    /// Returns the retained failed-attempt path when orchestration had begun.
    pub(crate) fn retained_path(&self) -> Option<&Path> {
        self.retained_path.as_deref()
    }
}

impl From<ServedRunnerError> for ServedBoundaryError {
    fn from(error: ServedRunnerError) -> Self {
        Self {
            reason: error.reason().to_owned(),
            retained_path: error.retained_path().map(Path::to_path_buf),
        }
    }
}

impl std::fmt::Display for ServedBoundaryError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.reason)
    }
}

impl std::error::Error for ServedBoundaryError {}

/// Runs the admitted diagnostic boundary through the process runner.
pub(crate) fn run_served_boundary(
    config: &ServedBoundaryConfig,
) -> Result<ServedBoundaryResult, ServedBoundaryError> {
    config.validate().map_err(|reason| ServedBoundaryError { reason, retained_path: None })?;
    let result = run_served_runner(&config.runner).map_err(ServedBoundaryError::from)?;
    Ok(ServedBoundaryResult {
        facts: result.facts,
        output_path: result.output_path,
        endpoint: result.endpoint,
        claim: ServedBoundaryClaim::DiagnosticQualification,
    })
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::super::lane::LaneConfig;
    use super::*;

    fn runner(workload: Workload) -> ServedRunnerConfig {
        ServedRunnerConfig {
            lane: LaneConfig::COMMISSIONING_TRIAL,
            workload,
            qualification_harness_executable: std::env::current_exe().expect("test executable"),
            go_executable: PathBuf::from("go"),
            go_workdir: PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("terrabase-go"),
            adapter_executable: None,
            output_directory: std::env::temp_dir().join("terrabase-served-boundary-tests"),
            timeout: Duration::from_secs(1),
            server_identity: QUALIFICATION_SERVER_IDENTITY.to_owned(),
            principal: "spiffe://terrascale.tech/benchmark".to_owned(),
            tenant: "benchmark-tenant".to_owned(),
            project: "benchmark-project".to_owned(),
            database: "benchmark-database".to_owned(),
            source_identity: QUALIFICATION_SOURCE_IDENTITY.to_owned(),
            transport_identity: QUALIFICATION_TRANSPORT_IDENTITY.to_owned(),
            openssl_executable: PathBuf::from("openssl"),
        }
    }

    fn diagnostic(workload: Workload) -> ServedBoundaryConfig {
        ServedBoundaryConfig {
            runner: runner(workload),
            admission: BoundaryAdmission::DiagnosticQualification,
        }
    }

    fn production_binding() -> ProductionCandidateBinding {
        ProductionCandidateBinding {
            signed_release_directory: PathBuf::from("/candidate/terrabase-server"),
            manifest_revision: "a".repeat(40),
            manifest_tree: "b".repeat(40),
            member_binary_sha256: "c".repeat(64),
            capability_sha256: "d".repeat(64),
            run_configuration_sha256: "e".repeat(64),
        }
    }

    #[test]
    fn diagnostic_results_are_never_decision_grade_claims() {
        let claim = ServedBoundaryClaim::DiagnosticQualification;
        assert!(!claim.supports_decision_grade_claim());
        assert!(claim.limitation().contains("single-member harness"));
        assert!(claim.limitation().contains("no production"));
    }

    #[test]
    fn single_member_admission_rejects_mutation_workloads() {
        for workload in [Workload::DocumentWrite, Workload::Mixed] {
            let error = diagnostic(workload).validate().expect_err("mutation must be refused");
            assert!(error.contains("regional quorum"));
            assert!(error.contains("RF=2"));
        }
    }

    #[test]
    fn diagnostic_identity_cannot_be_relabelled_as_a_candidate() {
        let mut config = diagnostic(Workload::DocumentRead);
        config.runner.server_identity = "terrabase-server/production".to_owned();
        assert!(config.validate().expect_err("server relabel must fail").contains("diagnostic"));

        let mut config = diagnostic(Workload::DocumentRead);
        config.runner.source_identity = "candidate-revision-deadbeef".to_owned();
        assert!(config.validate().expect_err("source relabel must fail").contains("diagnostic"));

        let mut config = diagnostic(Workload::DocumentRead);
        config.runner.transport_identity = "in-process production Member".to_owned();
        assert!(config.validate().expect_err("transport relabel must fail").contains("diagnostic"));
    }

    #[test]
    fn complete_database_scope_is_admitted_and_bounded() {
        diagnostic(Workload::DocumentRead).validate().expect("complete scope");

        let mut missing_project = diagnostic(Workload::DocumentRead);
        missing_project.runner.project.clear();
        assert!(missing_project.validate().expect_err("project is required").contains("project"));

        let mut oversized_database = diagnostic(Workload::DocumentRead);
        oversized_database.runner.database = "d".repeat(MAX_DATABASE_BYTES + 1);
        assert!(oversized_database.validate().expect_err("database bound").contains("scope"));
    }

    #[test]
    fn capability_inputs_never_upgrade_the_qualification_harness() {
        let binding = production_binding();
        assert_eq!(binding.signed_release_directory, PathBuf::from("/candidate/terrabase-server"));
        assert_eq!(binding.manifest_revision.len(), 40);
        assert_eq!(binding.manifest_tree.len(), 40);
        assert_eq!(binding.member_binary_sha256.len(), 64);
        assert_eq!(binding.capability_sha256.len(), 64);
        assert_eq!(binding.run_configuration_sha256.len(), 64);

        let config = ServedBoundaryConfig {
            runner: runner(Workload::DocumentRead),
            admission: BoundaryAdmission::ProductionCandidate(binding),
        };
        let error = run_served_boundary(&config).expect_err("production must fail closed");
        assert!(error.reason().contains("packaged real Member CLI"));
        assert!(error.reason().contains("exact Member binary digest"));
        assert!(error.reason().contains("run-configuration digest"));
        assert!(error.retained_path().is_none());
    }
}
