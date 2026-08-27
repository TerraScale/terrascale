//! The benchmark lane's configuration, warm-up discipline, order
//! alternation, and the workload mapping as data.
//!
//! The numbers and rules come from V1's server-comparison benchmark at the
//! pinned source `fa99b9257776dbe625e6a2388c74c9ce916637ee`
//! (`BenchmarkOptions.Validate`): a *recorded* run requires at least 1,000
//! seed documents, 10,000 operations per sample, and 5 samples, and it
//! classifies only beyond a 5% median delta with fully separated sample
//! ranges. Trial runs may go smaller, and a trial configuration must report
//! itself as not recordable rather than silently counting as a baseline.

/// The benchmark lane's configuration for one run.
#[derive(Clone, Copy, Debug)]
pub(crate) struct LaneConfig {
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
    /// V1's recorded-run minimums, and this lane's recording default.
    pub(crate) const RECORD_MINIMUM: Self = Self {
        seed_documents: 1_000,
        operations: 10_000,
        samples: 5,
        concurrency: 16,
        warmup_operations: 1_000,
        noise_percent: 5.0,
    };

    /// A small trial configuration used to commission the lane's own
    /// machinery. It is **not** a recordable baseline; see
    /// [`Self::validate_for_record`].
    pub(crate) const COMMISSIONING_TRIAL: Self = Self {
        seed_documents: 32,
        operations: 64,
        samples: 3,
        concurrency: 1,
        warmup_operations: 16,
        noise_percent: 5.0,
    };

    /// Returns the configuration with a different seed-document count.
    pub(crate) const fn with_seed_documents(mut self, seed_documents: usize) -> Self {
        self.seed_documents = seed_documents;
        self
    }

    /// Returns the configuration with a different per-sample operation count.
    pub(crate) const fn with_operations(mut self, operations: usize) -> Self {
        self.operations = operations;
        self
    }

    /// Returns the configuration with a different sample count.
    pub(crate) const fn with_samples(mut self, samples: usize) -> Self {
        self.samples = samples;
        self
    }

    /// Whether this configuration satisfies V1's recorded-run minimums.
    pub(crate) fn is_recorded(&self) -> bool {
        self.validate_for_record().is_ok()
    }

    /// Fails closed unless this configuration meets V1's recorded-run
    /// minimums.
    ///
    /// # Errors
    ///
    /// Returns [`LaneError::BelowRecordedMinimum`] naming the first
    /// unmet minimum, so a trial run can never be mistaken for a baseline.
    pub(crate) fn validate_for_record(&self) -> Result<(), LaneError> {
        if self.seed_documents < 1_000 {
            return Err(LaneError::BelowRecordedMinimum {
                what: "seed documents",
                required: 1_000,
                configured: self.seed_documents,
            });
        }
        if self.operations < 10_000 {
            return Err(LaneError::BelowRecordedMinimum {
                what: "operations per sample",
                required: 10_000,
                configured: self.operations,
            });
        }
        if self.samples < 5 {
            return Err(LaneError::BelowRecordedMinimum {
                what: "samples",
                required: 5,
                configured: self.samples,
            });
        }
        Ok(())
    }
}

/// Why a lane configuration cannot be recorded as a baseline.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LaneError {
    /// A recorded run's minimum was not met.
    BelowRecordedMinimum {
        /// Which minimum.
        what: &'static str,
        /// The recorded-run floor.
        required: usize,
        /// What was configured.
        configured: usize,
    },
}

/// One comparable workload of the objective, mapped on both sides.
///
/// The mapping lives here as data so the mapping document
/// (`docs/benchmarks/se15-workload-mapping.md`) and the driver describe the
/// same workloads, and a workload cannot drift between what is documented
/// and what is measured.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Workload {
    /// Point document read by identifier.
    DocumentRead,
    /// Document create/replace by identifier, acknowledged.
    DocumentWrite,
    /// Equality query on an indexed field over the seeded collection.
    IndexedQuery,
    /// V1's mixed read/write/query ratio.
    Mixed,
    /// Connection and session churn.
    SessionCycle,
}

impl Workload {
    /// Every workload of the objective.
    pub(crate) const ALL: [Self; 5] = [
        Self::DocumentRead,
        Self::DocumentWrite,
        Self::IndexedQuery,
        Self::Mixed,
        Self::SessionCycle,
    ];

    /// The receipt token, matching V1's workload names.
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::DocumentRead => "read",
            Self::DocumentWrite => "write",
            Self::IndexedQuery => "query",
            Self::Mixed => "mixed",
            Self::SessionCycle => "session",
        }
    }

    /// The workload's ordinal, which V1 used to phase the run-order
    /// alternation between workloads.
    pub(crate) const fn index(self) -> usize {
        match self {
            Self::DocumentRead => 0,
            Self::DocumentWrite => 1,
            Self::IndexedQuery => 2,
            Self::Mixed => 3,
            Self::SessionCycle => 4,
        }
    }

    /// V1's mixed ratio: 50% read, 30% write, 20% query. `None` for the
    /// unmixed workloads.
    pub(crate) const fn mixed_ratios(self) -> Option<(u32, u32, u32)> {
        match self {
            Self::Mixed => Some((50, 30, 20)),
            _ => None,
        }
    }

    /// Selects the sub-workload for operation `index` of a mixed run,
    /// exactly as V1 selected it (`index % 10 < 5` read, `< 8` write, else
    /// query).
    pub(crate) const fn select_mixed(self, index: usize) -> Self {
        match self {
            Self::Mixed if index % 10 < 5 => Self::DocumentRead,
            Self::Mixed if index % 10 < 8 => Self::DocumentWrite,
            Self::Mixed => Self::IndexedQuery,
            other => other,
        }
    }

    /// Whether PostgreSQL runs first for this workload's `sample_index`
    /// sample.
    ///
    /// V1 alternated which server ran first inside each sample
    /// (`(workloadIndex + sample) % 2`) so first-run and second-run bias
    /// cancels across a run rather than landing on one side.
    pub(crate) fn postgres_runs_first(self, sample_index: usize) -> bool {
        (self.index() + sample_index).is_multiple_of(2)
    }

    /// The TerraBaseV2 operation this workload measures.
    pub(crate) const fn terrabase_v2_operation(self) -> &'static str {
        match self {
            Self::DocumentRead => {
                "Engine::read_snapshot + Transaction::read — one point read by \
                 tenant/collection/document id on an immutable snapshot"
            }
            Self::DocumentWrite => {
                "Engine::begin + Transaction::put + commit(LocalDurability::Synced) — one \
                 acknowledged document create/replace"
            }
            Self::IndexedQuery => {
                "Engine::queries().compile + execute — one equality query on the seeded \
                 name field, projection and paging fixed"
            }
            Self::Mixed => "the read/write/query operations above at the 50/30/20 ratio",
            Self::SessionCycle => {
                "TBEP UDS host session open/use/close over the engine — the SE-14/SE-08 \
                 host session surface"
            }
        }
    }

    /// The PostgreSQL equivalent this workload measures, as the SQL the
    /// reference side executes.
    pub(crate) const fn postgres_equivalent(self) -> &'static str {
        match self {
            Self::DocumentRead => {
                "-- SELECT payload FROM users WHERE tenant = $1 AND id = $2;\n-- primary \
                 key point read, one prepared statement, one round trip"
            }
            Self::DocumentWrite => {
                "-- INSERT INTO users (tenant, id, name, \"group\", payload) VALUES \
                 ($1,$2,$3,$4,$5)\n-- ON CONFLICT (tenant, id) DO UPDATE SET name = \
                 EXCLUDED.name, \"group\" = EXCLUDED.\"group\", payload = \
                 EXCLUDED.payload;\n-- synchronous_commit = on (the engine's \
                 fsync-per-commit acknowledgement level)"
            }
            Self::IndexedQuery => {
                "-- SELECT id, name FROM users WHERE tenant = $1 AND name = $2 LIMIT 1;\n-- \
                 over the btree index on (tenant, name)"
            }
            Self::Mixed => {
                "-- the three statements above at the 50/30/20 ratio, interleaved as the \
                 V1 mixed workload interleaved them"
            }
            Self::SessionCycle => {
                "-- one connection per cycle: CONNECT; SELECT 1; DISCARD ALL; DISCONNECT;\n-- \
                 the un-pooled session churn cost, matching TerraBaseV2's per-session \
                 engine binding"
            }
        }
    }

    /// The reason this workload has no in-process driver yet, with its
    /// owner, or `None` when the driver runs it.
    ///
    /// An open adapter slot is stated, never silently skipped: a workload
    /// the lane cannot yet drive appears in the receipts as an owed
    /// measurement with a name on it.
    pub(crate) fn driver_status(self) -> Option<(&'static str, &'static str)> {
        match self {
            Self::SessionCycle => Some((
                "the comparable session plane is the TBEP UDS host session, which needs the \
                 C# host process beside the engine; this in-process lane drives the engine \
                 core only, so the session workload waits for the host-mode lane run",
                "SE-15 follow-up owner: the benchmark lane owner, with the SE-08 host \
                 surface",
            )),
            _ => None,
        }
    }
}
