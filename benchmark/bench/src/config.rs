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
}

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
