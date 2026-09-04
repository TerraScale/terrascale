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
#[derive(Clone, Copy, Debug, PartialEq)]
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

    /// Validates counts before a worker barrier is constructed. A remainder
    /// after slot fan-out would otherwise disappear from the measured run.
    pub(crate) fn validate(&self) -> Result<(), LaneError> {
        if self.seed_documents == 0 {
            return Err(LaneError::Invalid("seed documents must be greater than zero"));
        }
        if self.operations == 0 {
            return Err(LaneError::Invalid("operations per sample must be greater than zero"));
        }
        if self.samples == 0 {
            return Err(LaneError::Invalid("samples must be greater than zero"));
        }
        if self.concurrency == 0 {
            return Err(LaneError::Invalid("concurrency must be greater than zero"));
        }
        if !self.operations.is_multiple_of(self.concurrency) {
            return Err(LaneError::Invalid(
                "operations per sample must be divisible by concurrency",
            ));
        }
        if !self.noise_percent.is_finite() || !(0.0..=100.0).contains(&self.noise_percent) {
            return Err(LaneError::Invalid("noise percentage must be finite and in 0..=100"));
        }
        Ok(())
    }

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
    /// The configuration cannot produce a complete, comparable window.
    Invalid(&'static str),
}

/// The owner accountable for closing the sync-path workload slots
/// (PD-2026-08-26-05 D4, PD-2026-08-26-13 D1): the lane owner with the
/// SE-07 lane adapters that already measure the class's deterministic
/// quantities in the sibling crates.
const SYNC_PATH_OWNER: &str = "SE-15 follow-up owner: the benchmark lane owner, with the SE-07 \
                               sync-core and member-transport lane owners";

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
    /// Commit-to-delivery latency on the client and member links
    /// (PD-2026-08-26-05 D4).
    SyncCommitToDeliveryLatency,
    /// Delivery throughput under load on the client and member links
    /// (PD-2026-08-26-05 D4).
    SyncDeliveryThroughput,
    /// Bytes-on-wire per delivered change on the client and member links
    /// (PD-2026-08-26-13 D1).
    SyncBytesOnWirePerChange,
    /// Encoded trigger-notification size (PD-2026-08-26-13 D1/D3).
    SyncTriggerNotificationSize,
}

impl Workload {
    /// Every workload of the objective: the five CRUD-class workloads and
    /// the four sync-path workloads the charter mandates beside them.
    pub(crate) const ALL: [Self; 9] = [
        Self::DocumentRead,
        Self::DocumentWrite,
        Self::IndexedQuery,
        Self::Mixed,
        Self::SessionCycle,
        Self::SyncCommitToDeliveryLatency,
        Self::SyncDeliveryThroughput,
        Self::SyncBytesOnWirePerChange,
        Self::SyncTriggerNotificationSize,
    ];

    /// The sync-path workload class (PD-2026-08-26-05 D4,
    /// PD-2026-08-26-13 D1): commit-to-delivery latency, delivery
    /// throughput under load, and bandwidth — bytes-on-wire per delivered
    /// change and trigger-notification size — on the client and member
    /// links, documented and run alongside the PostgreSQL and V1 baselines.
    ///
    /// Every workload in this class is an **open adapter slot** here: the
    /// sync plane lives in the sibling crates (`terrabase-sync-core`,
    /// `terrabase-member-transport`, the TBEP host) that the
    /// zero-dependency engine crate cannot call, and the wall-clock
    /// quantities additionally need both reference baselines to have run.
    /// Each slot is declared through [`Self::driver_status`] with a reason
    /// and a named owner — the same pattern the `session` workload uses —
    /// and appears in the receipts as an owed measurement, never as a
    /// silent skip.
    pub(crate) const SYNC_PATH: [Self; 4] = [
        Self::SyncCommitToDeliveryLatency,
        Self::SyncDeliveryThroughput,
        Self::SyncBytesOnWirePerChange,
        Self::SyncTriggerNotificationSize,
    ];

    /// The receipt token, matching V1's workload names.
    pub(crate) const fn as_str(self) -> &'static str {
        match self {
            Self::DocumentRead => "read",
            Self::DocumentWrite => "write",
            Self::IndexedQuery => "query",
            Self::Mixed => "mixed",
            Self::SessionCycle => "session",
            Self::SyncCommitToDeliveryLatency => "sync-commit-to-delivery-latency",
            Self::SyncDeliveryThroughput => "sync-delivery-throughput",
            Self::SyncBytesOnWirePerChange => "sync-bytes-on-wire",
            Self::SyncTriggerNotificationSize => "sync-trigger-notification-size",
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
            Self::SyncCommitToDeliveryLatency => 5,
            Self::SyncDeliveryThroughput => 6,
            Self::SyncBytesOnWirePerChange => 7,
            Self::SyncTriggerNotificationSize => 8,
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

    /// The TerraBase operation this workload measures.
    pub(crate) const fn terrabase_operation(self) -> &'static str {
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
            Self::SyncCommitToDeliveryLatency => {
                "the interval from a commit's acknowledgement to the consumer's delivery \
                 observation, per link: client link — commit publication, the content-free \
                 trigger, and the checkpoint-bounded delta the triggered client pulls \
                 (sync-core apply/serve through the member's public API); member link — commit to the \
                 pushed or catch-up-delivered batch a member applies (member-transport \
                 push/catch-up and apply)"
            }
            Self::SyncDeliveryThroughput => {
                "the same two delivery paths under sustained load: changes delivered per \
                 round while a backlog drains on the member link (bounded by the validated \
                 catch-up batch) and delta batches served per budget window on the client \
                 link"
            }
            Self::SyncBytesOnWirePerChange => {
                "encoded frame bytes at each link's delivery boundary divided by changes \
                 delivered: client link — delta-batch and snapshot wire bytes per delivered \
                 change (the E-11 counter in the sync-core delta-efficiency adapter); member \
                 link — the per-direction envelope byte counters over push, catch-up, and \
                 acknowledgement frames (the SE-06 bytes-on-wire definition)"
            }
            Self::SyncTriggerNotificationSize => {
                "the encoded, content-free trigger notification: the terrascale.sync.v2 \
                 trigger frame on the client link (collection scope, storage sequence, \
                 operation id — nothing else), from the same wire shapes the SE-04 golden \
                 corpus records; the member link carries no trigger-notification frame \
                 class by design — members are pushed the data envelopes themselves, whose \
                 size the sync-bytes-on-wire workload covers"
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
                 the un-pooled session churn cost, matching TerraBase's per-session \
                 engine binding"
            }
            Self::SyncCommitToDeliveryLatency => {
                "-- no PostgreSQL equivalent: PostgreSQL has no push-based client delivery \
                 path, and its nearest surface (LISTEN/NOTIFY) is a payload-carrying channel \
                 notification with no checkpointed delta pull, so it is not a comparable \
                 contract; this workload compares TerraBase against its own written \
                 budgets and the TerraBase V1 baseline"
            }
            Self::SyncDeliveryThroughput => {
                "-- no PostgreSQL equivalent: PostgreSQL replication (WAL streaming, logical \
                 decoding) is a different wire contract with no shared budget or delivery \
                 acknowledgement level; this workload compares TerraBase against its own \
                 written budgets and the TerraBase V1 baseline"
            }
            Self::SyncBytesOnWirePerChange => {
                "-- no PostgreSQL equivalent: PostgreSQL's replication byte counts measure a \
                 different wire contract (WAL frames, not budgeted delta envelopes), so no \
                 shared budget exists; this workload compares TerraBase against the \
                 PD-2026-08-26-13 D1 written budgets and the TerraBase V1 baseline"
            }
            Self::SyncTriggerNotificationSize => {
                "-- no PostgreSQL equivalent: the nearest surface, LISTEN/NOTIFY, carries a \
                 payload string and no delta path, so it is not comparable; the trigger \
                 frame is held against its own written budget (256 bytes) and the TerraBase \
                 V1 baseline"
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
                 private TBEP server and Go client beside the engine; this in-process lane drives the engine \
                 core only, so the session workload waits for the host-mode lane run",
                "SE-15 follow-up owner: the benchmark lane owner, with the SE-08 host \
                 surface",
            )),
            Self::SyncCommitToDeliveryLatency | Self::SyncDeliveryThroughput => Some((
                "a wall-clock sync-path quantity needs the SE-15 statistical gate with both \
                 reference baselines run and the served sync plane beside the engine — the \
                 private TBEP/Go client link and the member-transport lane; this in-process \
                 lane drives the engine core only, and the sync plane lives in the sibling \
                 crates the zero-dependency engine crate cannot call, so the workload waits \
                 for the served-lane run beside the references",
                SYNC_PATH_OWNER,
            )),
            Self::SyncBytesOnWirePerChange => Some((
                "the deterministic TerraBase-side counters already exist and are pinned \
                 (client link: the sync-core delta-efficiency adapter's per-change byte \
                 counts; member link: the SE-06 lane bytes-on-wire counters); the owed run \
                 is the receipted comparison alongside the PostgreSQL and TerraBase V1 \
                 baselines under the SE-15 gate, whose lane adapters live in those sibling \
                 crates — re-deriving the counters inside the zero-dependency engine crate \
                 would duplicate them, not measure them, so the slot stays open",
                SYNC_PATH_OWNER,
            )),
            Self::SyncTriggerNotificationSize => Some((
                "the deterministic measurement already exists and is pinned: the \
                 content-free trigger frame is recorded in the SE-04 golden corpus and \
                 bounded by its written budget in the sync-core delta-efficiency adapter; \
                 the owed run is the receipted comparison alongside the baselines under the \
                 SE-15 gate, whose lane adapter lives in the sync-core crate the engine \
                 crate cannot call",
                SYNC_PATH_OWNER,
            )),
            _ => None,
        }
    }
}
