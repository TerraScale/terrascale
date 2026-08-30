//! The lane configuration this driver shares with the engine-side adapter.
//!
//! The numbers mirror `src/benchmarks/lane.rs` `LaneConfig::RECORD_MINIMUM`
//! — V1's recorded-run minimums — so a run this driver contributes to is a
//! recordable baseline on both sides. The engine-side adapter remains the
//! authority: it re-validates its own copy, and the receipt records both.

use serde::Serialize;

/// One benchmark-lane configuration, identical in shape to the engine
/// adapter's `LaneConfig`.
#[derive(Clone, Copy, Debug, Serialize)]
pub struct LaneConfig {
    /// Documents seeded into each side before any measurement.
    pub seed_documents: usize,
    /// Operations per side, per sample, per workload.
    pub operations: usize,
    /// Samples per side, per workload.
    pub samples: usize,
    /// Concurrent in-flight operations on each side.
    pub concurrency: usize,
    /// Warm-up operations per side, per workload, excluded from every
    /// measured sample.
    pub warmup_operations: usize,
    /// The classification threshold in percent.
    pub noise_percent: f64,
}

impl LaneConfig {
    /// V1's recorded-run minimums, and this driver's default.
    pub const RECORD_MINIMUM: Self = Self {
        seed_documents: 1_000,
        operations: 10_000,
        samples: 5,
        concurrency: 16,
        warmup_operations: 1_000,
        noise_percent: 5.0,
    };

    /// Validates the shape of a run before it can touch a reference server.
    ///
    /// A concurrency window must divide evenly so every slot executes the
    /// same number of operations. Silently dropping the remainder would make
    /// the JSON claim a different workload from the requested one.
    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.seed_documents == 0 {
            return Err(ConfigError::Zero("seed_documents"));
        }
        if self.operations == 0 {
            return Err(ConfigError::Zero("operations"));
        }
        if self.samples == 0 {
            return Err(ConfigError::Zero("samples"));
        }
        if self.concurrency == 0 {
            return Err(ConfigError::Zero("concurrency"));
        }
        if !self.operations.is_multiple_of(self.concurrency) {
            return Err(ConfigError::NotDivisible {
                operations: self.operations,
                concurrency: self.concurrency,
            });
        }
        if !self.noise_percent.is_finite() || !(0.0..=100.0).contains(&self.noise_percent) {
            return Err(ConfigError::Noise(self.noise_percent));
        }
        Ok(())
    }
}

/// Why a requested lane configuration cannot execute safely.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ConfigError {
    /// A required positive count was zero.
    Zero(&'static str),
    /// The operation count would be truncated by slot fan-out.
    NotDivisible { operations: usize, concurrency: usize },
    /// The statistical noise setting is not a finite percentage.
    Noise(f64),
}

impl std::fmt::Display for ConfigError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Zero(field) => write!(formatter, "{field} must be greater than zero"),
            Self::NotDivisible { operations, concurrency } => write!(
                formatter,
                "operation count {operations} is not divisible by concurrency {concurrency}"
            ),
            Self::Noise(value) => {
                write!(formatter, "noise percentage {value} is not finite or in 0..=100")
            }
        }
    }
}

impl std::error::Error for ConfigError {}

/// The four CRUD-class workloads both sides drive, with V1's mixed ratio.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Workload {
    /// Point document read by identifier.
    Read,
    /// Document create/replace by identifier, acknowledged.
    Write,
    /// Equality query on an indexed field over the seeded collection.
    Query,
    /// V1's mixed read/write/query ratio (50/30/20).
    Mixed,
}

impl Workload {
    /// The exact token the engine adapter's workload names use.
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Read => "read",
            Self::Write => "write",
            Self::Query => "query",
            Self::Mixed => "mixed",
        }
    }

    /// V1's mixed selection: `index % 10 < 5` reads, `< 8` writes, else
    /// queries — the same ratio `src/benchmarks/lane.rs` implements.
    pub const fn select_mixed(self, index: usize) -> Self {
        match self {
            Self::Mixed if index % 10 < 5 => Self::Read,
            Self::Mixed if index % 10 < 8 => Self::Write,
            Self::Mixed => Self::Query,
            other => other,
        }
    }

    /// Parses a workload token.
    ///
    /// # Errors
    ///
    /// Returns the token back in the error for the usage message.
    pub fn parse(token: &str) -> Result<Self, String> {
        match token {
            "read" => Ok(Self::Read),
            "write" => Ok(Self::Write),
            "query" => Ok(Self::Query),
            "mixed" => Ok(Self::Mixed),
            other => Err(other.to_owned()),
        }
    }
}

/// The reference target this driver can drive.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Target {
    /// The pinned PostgreSQL 18 reference.
    PostgreSql,
    /// The pinned RavenDB stable reference.
    RavenDb,
}

impl Target {
    /// Parses a target token.
    ///
    /// # Errors
    ///
    /// Returns the token back in the error for the usage message.
    pub fn parse(token: &str) -> Result<Self, String> {
        match token {
            "pg" | "postgres" | "postgresql" => Ok(Self::PostgreSql),
            "raven" | "ravendb" => Ok(Self::RavenDb),
            other => Err(other.to_owned()),
        }
    }

    /// The exact target name recorded in results JSON.
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::PostgreSql => "postgresql",
            Self::RavenDb => "ravendb",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{ConfigError, LaneConfig};

    #[test]
    fn rejects_zero_counts_and_non_divisible_windows() {
        let mut config = LaneConfig::RECORD_MINIMUM;
        config.concurrency = 0;
        assert!(matches!(config.validate(), Err(ConfigError::Zero("concurrency"))));

        let mut config = LaneConfig::RECORD_MINIMUM;
        config.operations += 1;
        assert!(matches!(config.validate(), Err(ConfigError::NotDivisible { .. })));

        let mut config = LaneConfig::RECORD_MINIMUM;
        config.warmup_operations += 1;
        assert!(config.validate().is_ok(), "warm-up remainder is distributed across slots");
    }

    #[test]
    fn rejects_non_finite_or_out_of_range_noise() {
        let mut config = LaneConfig::RECORD_MINIMUM;
        config.noise_percent = f64::NAN;
        assert!(matches!(config.validate(), Err(ConfigError::Noise(_))));

        config.noise_percent = 101.0;
        assert!(matches!(config.validate(), Err(ConfigError::Noise(_))));
        assert!(LaneConfig::RECORD_MINIMUM.validate().is_ok());
    }
}
