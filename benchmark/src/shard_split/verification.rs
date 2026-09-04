//! Post-cutover range verification contracts.

/// Stable source/target facts used for range verification.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RangeVerification {
    /// Number of records in the moved range.
    pub records: u64,
    /// Opaque source fingerprint supplied by the storage worker.
    pub fingerprint: [u8; 32],
    /// Highest journal sequence represented by the range scan.
    pub through: u64,
}

/// Result of comparing source and target range facts.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VerificationOutcome {
    /// The target exactly matches source facts.
    Verified,
    /// The target differs and must not be cleaned up.
    Mismatch,
}

/// Detailed verification result retained for audit and recovery.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VerificationReport {
    /// Facts observed on the source.
    pub source: RangeVerification,
    /// Facts observed on the target.
    pub target: RangeVerification,
    /// Final comparison outcome.
    pub outcome: VerificationOutcome,
}

impl VerificationReport {
    /// Compares source and target facts without reading a clock or storage.
    #[must_use]
    pub fn compare(source: RangeVerification, target: RangeVerification) -> Self {
        let outcome = if source.records == target.records
            && source.fingerprint == target.fingerprint
            && target.through >= source.through
        {
            VerificationOutcome::Verified
        } else {
            VerificationOutcome::Mismatch
        };
        Self { source, target, outcome }
    }

    /// Returns whether cleanup of the source range is safe.
    #[must_use]
    pub const fn is_verified(&self) -> bool {
        matches!(self.outcome, VerificationOutcome::Verified)
    }
}

/// Verification failure that must leave routing and source data intact.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VerificationError {
    /// Source and target facts differ.
    Mismatch,
    /// The target checkpoint is behind the source checkpoint.
    TargetBehind,
}

/// Storage implementation seam for obtaining range facts.
pub trait Verifier {
    /// Worker-specific I/O failure.
    type Error;

    /// Reads a stable range fingerprint and checkpoint.
    fn range_facts(&mut self, upper: &[u8]) -> Result<RangeVerification, Self::Error>;
}

/// Runs a source/target comparison and refuses cleanup on any mismatch.
pub fn verify<S, T>(
    source: &mut S,
    target: &mut T,
    upper: &[u8],
) -> Result<VerificationReport, VerificationError>
where
    S: Verifier,
    T: Verifier,
{
    let source_facts = source.range_facts(upper).map_err(|_| VerificationError::Mismatch)?;
    let target_facts = target.range_facts(upper).map_err(|_| VerificationError::Mismatch)?;
    let report = VerificationReport::compare(source_facts, target_facts);
    if report.target.through < report.source.through {
        return Err(VerificationError::TargetBehind);
    }
    if report.is_verified() { Ok(report) } else { Err(VerificationError::Mismatch) }
}
