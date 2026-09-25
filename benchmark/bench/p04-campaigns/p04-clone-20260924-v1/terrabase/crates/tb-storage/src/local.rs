use std::collections::{BTreeMap, HashMap};
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use fs2::FileExt;
use sha2::{Digest, Sha256};
use tb_core::{
    AckProfile, Attribution, CommitFrontiers, CommitReceipt, CommitSeq, ItemReservation, Key,
    KeyBound, KeyRange, MemoryBudget, MemoryCategory, ReadSnapshot, Reservation, TenantId,
    TransactionId, WorkerId,
};
use tb_format::{
    FormatLimits, WalOperation, decode_wal_frame, encode_wal_frame_into, wal_frame_len,
};

use crate::floor::{self, FloorAuthority};
use crate::mode::{DistributedDocumentMode, OpenGate};
use crate::run::{self, Manifest, RawRecord, RunMeta};
use crate::work::ScanCounters;
use crate::{Mutation, ScanPage, ScanRow, StorageError, VersionedValue};

const WAL_NAME: &str = "wal.log";
const LOCK_NAME: &str = "shard.lock";
const WAL_HEADER_LEN: usize = 52;
const MAX_COMPLETE_FRAME_BYTES: usize = 2 * 1024 * 1024;
const ENTRY_OVERHEAD: usize = 128;
const DEDUP_OVERHEAD: usize = 128;
const DIRECTORY_FIXED_BYTES: usize = 16;
const RUN_FIXED_BYTES: usize = 36 + 24;

fn directory_record_bytes(key_len: usize) -> Result<usize, StorageError> {
    key_len
        .checked_mul(2)
        .and_then(|bytes| bytes.checked_add(24))
        .ok_or(StorageError::Capacity("run directory bytes"))
}

/// Limits and ownership for one local shard directory.
#[derive(Clone)]
pub struct StorageConfig {
    /// Dedicated shard directory. Its WAL is not shared with another writer.
    pub path: PathBuf,
    /// Selected durability profile; only `LocalDurable` is currently accepted.
    pub profile: AckProfile,
    /// Tenant attribution for memory admission.
    pub tenant: TenantId,
    /// Worker attribution for memory admission.
    pub worker: WorkerId,
    /// Format-level hard caps. These may be lowered, never raised above v1 caps.
    pub format: FormatLimits,
    /// Maximum admitted in-memory mutable versions before a checkpoint.
    pub max_mutable_bytes: usize,
    /// Maximum bytes retained under this shard directory.
    pub max_disk_bytes: u64,
    /// Maximum immutable runs before maintenance applies backpressure.
    pub max_runs: usize,
    /// Maximum number of simultaneous retained idempotency resolutions.
    pub max_live_transactions: usize,
    /// Latest retry deadline accepted relative to checked current time.
    pub max_retry_window_ms: u64,
    /// Maximum returned scan rows.
    pub max_scan_rows: usize,
    /// Maximum returned scan bytes.
    pub max_scan_bytes: usize,
    clock: Arc<dyn Fn() -> u64 + Send + Sync>,
}

impl StorageConfig {
    /// Construct the first shard-local configuration with explicit owner and path.
    #[must_use]
    pub fn local(path: impl Into<PathBuf>, tenant: TenantId, worker: WorkerId) -> Self {
        Self {
            path: path.into(),
            profile: AckProfile::LocalDurable,
            tenant,
            worker,
            format: FormatLimits::local_v1(),
            max_mutable_bytes: 16 * 1024 * 1024,
            max_disk_bytes: 4 * 1024 * 1024 * 1024,
            max_runs: 128,
            max_live_transactions: 65_536,
            max_retry_window_ms: 24 * 60 * 60 * 1_000,
            max_scan_rows: 4_096,
            max_scan_bytes: 16 * 1024 * 1024,
            clock: Arc::new(system_time_ms),
        }
    }

    /// Override the checked wall clock, primarily for deterministic crash tests.
    #[must_use]
    pub fn with_clock(mut self, clock: Arc<dyn Fn() -> u64 + Send + Sync>) -> Self {
        self.clock = clock;
        self
    }

    fn attribution(&self, category: MemoryCategory) -> Attribution {
        Attribution {
            tenant: self.tenant,
            worker: self.worker,
            category,
        }
    }

    fn validate(&self) -> Result<(), StorageError> {
        if !matches!(self.profile, AckProfile::LocalDurable) {
            return Err(StorageError::UnsupportedProfile);
        }
        let hard = FormatLimits::local_v1();
        if self.format.max_frame_bytes > MAX_COMPLETE_FRAME_BYTES
            || self.format.max_operations > hard.max_operations
            || self.format.max_key_bytes > hard.max_key_bytes
            || self.format.max_value_bytes > hard.max_value_bytes
            || self.format.max_block_plain_bytes > hard.max_block_plain_bytes
            || self.format.max_block_stored_bytes > hard.max_block_stored_bytes
            || self.format.max_records_per_block > hard.max_records_per_block
            || self.format.max_segment_blocks > hard.max_segment_blocks
            || self.format.max_directory_bytes > hard.max_directory_bytes
        {
            return Err(StorageError::Capacity("format limit exceeds v1 ceiling"));
        }
        let largest_record = self
            .format
            .max_key_bytes
            .checked_add(self.format.max_value_bytes)
            .and_then(|bytes| bytes.checked_add(17))
            .ok_or(StorageError::Capacity("segment record limit overflow"))?;
        let min_descriptor = self
            .format
            .max_key_bytes
            .checked_mul(2)
            .and_then(|bytes| bytes.checked_add(16 + 24))
            .ok_or(StorageError::Capacity("segment directory limit overflow"))?;
        let max_mutable_records = self.max_mutable_bytes.div_ceil(ENTRY_OVERHEAD + 1);
        if self.max_mutable_bytes < self.format.max_frame_bytes + ENTRY_OVERHEAD
            || self.max_disk_bytes < self.format.max_frame_bytes as u64
            || self.max_runs == 0
            || self.max_runs > 2_048
            || self.max_live_transactions == 0
            || self.max_scan_rows == 0
            || self.max_scan_bytes == 0
            || self.format.max_records_per_block == 0
            || self.format.max_segment_blocks < max_mutable_records
            || self.format.max_block_plain_bytes < largest_record
            || self.format.max_block_stored_bytes < largest_record + 20
            || self.format.max_directory_bytes < min_descriptor
        {
            return Err(StorageError::Capacity("invalid storage limits"));
        }
        Ok(())
    }
}

fn system_time_ms() -> u64 {
    let elapsed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    u64::try_from(elapsed.as_millis()).unwrap_or(u64::MAX)
}

pub(crate) struct Version {
    pub(crate) sequence: CommitSeq,
    pub(crate) value: Option<Vec<u8>>,
}

struct DedupEntry {
    sequence: CommitSeq,
    digest: [u8; 32],
    wal_offset: u64,
    frame_len: usize,
    _permit: Reservation,
    _item_permit: ItemReservation,
}

struct AdmittedRawRecord {
    record: RawRecord,
    _permit: Option<Reservation>,
}

struct PreparedCommit {
    frame_bytes: Vec<u8>,
    digest: [u8; 32],
    next_seq: CommitSeq,
    estimated_mutable: usize,
    directory_added: usize,
    _frame_permit: Reservation,
}

struct CommitPermits {
    mutable_item: ItemReservation,
    mutable: Reservation,
    dedup: Reservation,
    dedup_item: ItemReservation,
}

/// Exact or conservatively bounded work for one planned raw WAL group.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BatchFootprint {
    pub frame_bytes: usize,
    pub operations: usize,
    pub mutable_bytes: usize,
    pub directory_bytes: usize,
}

/// A frozen stage identity, or an upper bound for the final group.
pub struct PlannedBatch {
    transaction_id: TransactionId,
    footprint: BatchFootprint,
    digest: Option<[u8; 32]>,
}

/// Ordered D6-stage groups followed by one atomic final group.
pub struct StageWorkPlan {
    stages: Vec<PlannedBatch>,
    final_batch: PlannedBatch,
}

struct StagePermitBanks {
    frame: Reservation,
    mutable: Reservation,
    mutable_items: ItemReservation,
    dedup: Reservation,
    dedup_items: ItemReservation,
    compaction: Reservation,
    read: Reservation,
}

struct ActiveStageWork {
    id: u64,
    remaining_frame: u64,
    remaining_mutable: u64,
    remaining_directory: u64,
    remaining_runs: usize,
    remaining_dedup: usize,
    baseline_manifest_bytes: u64,
    max_manifest_bytes: u64,
}

/// A non-cloneable whole-operation claim that releases `Inner` between stages.
#[must_use = "dropping a stage reservation releases unused admission claims"]
pub struct StageWorkReservation<'s> {
    storage: &'s Storage,
    plan: StageWorkPlan,
    next_stage: usize,
    id: u64,
    banks: Option<StagePermitBanks>,
}

impl PlannedBatch {
    /// Freeze the transaction and exact mutations without retaining their values.
    ///
    /// # Errors
    /// Returns an error for a frame or footprint that exceeds format limits.
    pub fn exact(
        format: &FormatLimits,
        transaction_id: TransactionId,
        mutations: &[Mutation],
    ) -> Result<Self, StorageError> {
        if mutations.is_empty() {
            return Err(StorageError::InvalidBatch("operation count"));
        }
        let operations = frame_operations(mutations);
        let mut frame = Vec::new();
        encode_wal_frame_into(&mut frame, 1, transaction_id, &operations, format)?;
        let mut mutable_bytes = 0_usize;
        let mut directory_bytes = 0_usize;
        for mutation in mutations {
            let key_len = mutation.key().as_bytes().len();
            let value_len = match mutation {
                Mutation::Put { value, .. } => value.len(),
                Mutation::Delete { .. } => 0,
            };
            mutable_bytes = mutable_bytes
                .checked_add(ENTRY_OVERHEAD + key_len + value_len)
                .ok_or(StorageError::Capacity("stage mutable bytes"))?;
            directory_bytes = directory_bytes
                .checked_add(directory_record_bytes(key_len)?)
                .ok_or(StorageError::Capacity("stage directory bytes"))?;
        }
        Ok(Self {
            transaction_id,
            footprint: BatchFootprint {
                frame_bytes: frame.len(),
                operations: mutations.len(),
                mutable_bytes,
                directory_bytes,
            },
            digest: Some(canonical_digest(&frame)),
        })
    }

    /// Bound a final batch whose exact bytes will be frozen after staging.
    #[must_use]
    pub const fn final_upper_bound(
        transaction_id: TransactionId,
        footprint: BatchFootprint,
    ) -> Self {
        Self {
            transaction_id,
            footprint,
            digest: None,
        }
    }

    /// Checked disk and memory footprint for this planned group.
    #[must_use]
    pub const fn footprint(&self) -> BatchFootprint {
        self.footprint
    }
}

impl StageWorkPlan {
    /// Create an ordered list of exact stages and one final group.
    #[must_use]
    pub const fn new(stages: Vec<PlannedBatch>, final_batch: PlannedBatch) -> Self {
        Self {
            stages,
            final_batch,
        }
    }
}

impl StagePermitBanks {
    fn frame(&mut self, bytes: usize) -> Result<Reservation, StorageError> {
        self.frame
            .split(bytes)
            .map_err(|_| StorageError::Capacity("stage frame reservation"))
    }

    fn commit(
        &mut self,
        operations: usize,
        mutable_bytes: usize,
    ) -> Result<CommitPermits, StorageError> {
        Ok(CommitPermits {
            mutable_item: self
                .mutable_items
                .split(operations)
                .map_err(|_| StorageError::Capacity("stage mutable item reservation"))?,
            mutable: self
                .mutable
                .split(mutable_bytes)
                .map_err(|_| StorageError::Capacity("stage mutable reservation"))?,
            dedup: self
                .dedup
                .split(DEDUP_OVERHEAD)
                .map_err(|_| StorageError::Capacity("stage dedup reservation"))?,
            dedup_item: self
                .dedup_items
                .split(1)
                .map_err(|_| StorageError::Capacity("stage dedup item reservation"))?,
        })
    }
}

impl<'s> StageWorkReservation<'s> {
    fn retire(&mut self) {
        self.storage
            .cancelled_stage_work_id
            .store(self.id, Ordering::Release);
        self.banks = None;
    }

    /// Commit the next exact stage while retaining all later-stage claims.
    ///
    /// # Errors
    /// Returns an error for a changed plan, conflicting state, or failed durability.
    pub fn commit_stage(
        &mut self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
    ) -> Result<CommitReceipt, StorageError> {
        let planned = self
            .plan
            .stages
            .get(self.next_stage)
            .ok_or(StorageError::InvalidBatch("no remaining stage"))?;
        let inner = self.storage.lock();
        if inner
            .stage_work
            .as_ref()
            .is_none_or(|work| work.id != self.id)
        {
            return Err(StorageError::Capacity("stage work is no longer active"));
        }
        if transaction_id != planned.transaction_id
            || PlannedBatch::exact(&inner.config.format, transaction_id, mutations)?.digest
                != planned.digest
        {
            return Err(StorageError::InvalidBatch("stage plan mismatch"));
        }
        if inner.dedup.contains_key(&transaction_id) {
            return Err(StorageError::IdempotencyConflict);
        }
        let footprint = planned.footprint;
        let outcome = match Storage::prepare_batch_locked(
            inner,
            snapshot,
            transaction_id,
            mutations,
            self.banks.as_mut(),
        ) {
            Ok(outcome) => outcome,
            Err(error) => {
                self.retire();
                return Err(error);
            }
        };
        let receipt = match outcome {
            PreparedBatchOutcome::Ready(mut prepared) => {
                prepared.stage_footprint = Some(footprint);
                match prepared.commit() {
                    Ok(receipt) => receipt,
                    Err(error) => {
                        self.retire();
                        return Err(error);
                    }
                }
            }
            PreparedBatchOutcome::AlreadyCommitted(_) => {
                self.retire();
                return Err(StorageError::IdempotencyConflict);
            }
        };
        self.next_stage += 1;
        Ok(receipt)
    }

    /// Prepare the final atomic group and transfer its permits into `Ready`.
    /// The stage ledger is removed before the held writer guard is returned.
    ///
    /// # Errors
    /// Returns an error for incomplete stages, a changed final bound, or failed admission.
    pub fn prepare_final(
        mut self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
    ) -> Result<PreparedBatchOutcome<'s>, StorageError> {
        if self.next_stage != self.plan.stages.len() {
            return Err(StorageError::InvalidBatch("stages remain"));
        }
        let inner = self.storage.lock();
        if inner
            .stage_work
            .as_ref()
            .is_none_or(|work| work.id != self.id)
        {
            return Err(StorageError::Capacity("stage work is no longer active"));
        }
        let planned = &self.plan.final_batch;
        let exact = PlannedBatch::exact(&inner.config.format, transaction_id, mutations)?;
        if transaction_id != planned.transaction_id
            || exact.footprint.frame_bytes > planned.footprint.frame_bytes
            || exact.footprint.operations > planned.footprint.operations
            || exact.footprint.mutable_bytes > planned.footprint.mutable_bytes
            || exact.footprint.directory_bytes > planned.footprint.directory_bytes
            || planned
                .digest
                .is_some_and(|digest| exact.digest != Some(digest))
        {
            return Err(StorageError::InvalidBatch("final plan mismatch"));
        }
        let mut outcome = Storage::prepare_batch_locked(
            inner,
            snapshot,
            transaction_id,
            mutations,
            self.banks.as_mut(),
        )?;
        match &mut outcome {
            PreparedBatchOutcome::Ready(prepared) => {
                prepared.inner.stage_work = None;
            }
            PreparedBatchOutcome::AlreadyCommitted(_) => {
                self.storage.lock().stage_work = None;
            }
        }
        self.banks = None;
        Ok(outcome)
    }
}

impl Drop for StageWorkReservation<'_> {
    fn drop(&mut self) {
        if self.banks.is_some() {
            self.storage
                .cancelled_stage_work_id
                .store(self.id, Ordering::Release);
        }
    }
}

struct Inner {
    config: StorageConfig,
    budget: MemoryBudget,
    _manifest_permit: Reservation,
    _lock_file: File,
    wal: File,
    wal_len: u64,
    visible: CommitSeq,
    floor: FloorAuthority,
    manifest: Manifest,
    mutable: BTreeMap<Key, Vec<Version>>,
    mutable_bytes: usize,
    // Upper bound: one descriptor per record, including both copies of its key.
    mutable_directory_bytes: usize,
    mutable_permits: Vec<Reservation>,
    mutable_item_permits: Vec<ItemReservation>,
    scan_work: ScanCounters,
    dedup: HashMap<TransactionId, DedupEntry>,
    stage_work: Option<ActiveStageWork>,
    next_stage_work_id: u64,
    poisoned: bool,
    #[cfg(any(test, feature = "test-hooks"))]
    test_wal_fault: Option<TestWalFault>,
}

#[cfg(any(test, feature = "test-hooks"))]
enum TestWalFault {
    AfterPrefix(usize),
    BeforeSync,
}

/// Current local durability, memory, and disk admission counters.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StorageStats {
    pub visible: CommitSeq,
    pub durable: CommitSeq,
    pub checkpoint: CommitSeq,
    pub expired_floor_ms: u64,
    pub wal_bytes: u64,
    pub immutable_runs: usize,
    pub mutable_bytes: usize,
    pub live_transaction_ids: usize,
    pub disk_bytes: u64,
    /// Cumulative work done inside completed or active `Storage::scan` calls.
    pub scan_work: crate::ScanWork,
}

/// A single-writer, shard-local durable engine.
pub struct Storage {
    inner: Mutex<Inner>,
    cancelled_stage_work_id: AtomicU64,
}

/// The result of fully admitting a raw batch before its WAL append.
#[allow(
    clippy::large_enum_variant,
    reason = "Ready owns the bounded guard and permits without another allocation"
)]
pub enum PreparedBatchOutcome<'s> {
    /// The exact batch is reserved and holds the shard writer lock.
    Ready(PreparedBatch<'s>),
    /// This live retry token already committed the same canonical batch.
    AlreadyCommitted(CommitReceipt),
}

/// A frozen, admitted batch whose reservations and writer lock remain held.
/// Dropping it releases those resources without appending its WAL frame.
pub struct PreparedBatch<'s> {
    inner: std::sync::MutexGuard<'s, Inner>,
    prepared: PreparedCommit,
    permits: CommitPermits,
    transaction_id: TransactionId,
    wal_offset: u64,
    next_wal_len: u64,
    stage_footprint: Option<BatchFootprint>,
}

impl Storage {
    /// Admit every planned stage and its final WAL group before the first stage write.
    /// The returned token releases the shard lock between stage commits.
    ///
    /// # Errors
    /// Returns an error if a plan, disk envelope, run/dedup slot, or memory
    /// reservation cannot be admitted.
    #[allow(
        clippy::too_many_lines,
        reason = "one checked admission transaction owns all stage claims"
    )]
    pub fn reserve_stage_work(
        &self,
        plan: StageWorkPlan,
    ) -> Result<StageWorkReservation<'_>, StorageError> {
        let mut inner = self.lock();
        if inner.poisoned {
            return Err(StorageError::Poisoned);
        }
        if inner.stage_work.is_some() {
            return Err(StorageError::Capacity("stage work already reserved"));
        }
        let batch_count = plan
            .stages
            .len()
            .checked_add(1)
            .ok_or(StorageError::Capacity("stage batch count"))?;
        let run_slots = batch_count
            .checked_add(1)
            .ok_or(StorageError::Capacity("stage run slots"))?;
        if inner
            .manifest
            .runs
            .len()
            .checked_add(run_slots)
            .is_none_or(|n| n > inner.config.max_runs)
        {
            return Err(StorageError::Capacity("immutable run count"));
        }
        if inner
            .dedup
            .len()
            .checked_add(batch_count)
            .is_none_or(|n| n > inner.config.max_live_transactions)
        {
            return Err(StorageError::Capacity("live transaction resolution table"));
        }
        let mut frame_bytes = 0_u64;
        let mut mutable_bytes = 0_u64;
        let mut directory_bytes = 0_u64;
        let mut frame_work_bytes = 0_usize;
        let mut operations = 0_usize;
        for (index, batch) in plan
            .stages
            .iter()
            .chain(std::iter::once(&plan.final_batch))
            .enumerate()
        {
            let footprint = batch.footprint;
            if (index < plan.stages.len() && batch.digest.is_none())
                || footprint.operations == 0
                || footprint.operations > inner.config.format.max_operations
                || footprint.frame_bytes > inner.config.format.max_frame_bytes
                || footprint.frame_bytes < WAL_HEADER_LEN + 8
                || footprint.mutable_bytes > inner.config.max_mutable_bytes
                || DIRECTORY_FIXED_BYTES
                    .checked_add(footprint.directory_bytes)
                    .is_none_or(|n| n > inner.config.format.max_directory_bytes)
                || plan.stages[..index.min(plan.stages.len())]
                    .iter()
                    .any(|previous| previous.transaction_id == batch.transaction_id)
                || inner.dedup.contains_key(&batch.transaction_id)
            {
                return Err(StorageError::Capacity("invalid stage work plan"));
            }
            frame_bytes = frame_bytes
                .checked_add(footprint.frame_bytes as u64)
                .ok_or(StorageError::Capacity("stage disk bytes"))?;
            mutable_bytes = mutable_bytes
                .checked_add(footprint.mutable_bytes as u64)
                .ok_or(StorageError::Capacity("stage disk bytes"))?;
            directory_bytes = directory_bytes
                .checked_add(footprint.directory_bytes as u64)
                .ok_or(StorageError::Capacity("stage disk bytes"))?;
            frame_work_bytes = frame_work_bytes
                .checked_add(footprint.frame_bytes)
                .and_then(|n| {
                    n.checked_add(
                        footprint
                            .operations
                            .checked_mul(std::mem::size_of::<WalOperation<'_>>())?,
                    )
                })
                .ok_or(StorageError::Capacity("stage frame memory"))?;
            operations = operations
                .checked_add(footprint.operations)
                .ok_or(StorageError::Capacity("stage item count"))?;
        }
        let manifest_size = run::manifest_bytes_for_run_count(inner.config.max_runs)?;
        let manifest_bytes = manifest_size as u64;
        let disk_claim = frame_bytes
            .checked_add(inner.mutable_bytes as u64)
            .and_then(|n| n.checked_add(mutable_bytes))
            .and_then(|n| {
                n.checked_add(
                    inner
                        .mutable_directory_bytes
                        .saturating_sub(DIRECTORY_FIXED_BYTES) as u64,
                )
            })
            .and_then(|n| n.checked_add(directory_bytes))
            .and_then(|n| n.checked_add((run_slots as u64).checked_mul(76)?))
            .and_then(|n| n.checked_add(manifest_bytes.checked_mul(3)?))
            .and_then(|n| n.checked_add(floor::PENDING_BYTES as u64))
            .ok_or(StorageError::Capacity("stage disk bytes"))?;
        if inner
            .actual_disk_bytes()?
            .checked_add(disk_claim)
            .is_none_or(|n| n > inner.config.max_disk_bytes)
        {
            return Err(StorageError::Capacity("SSD bytes and stage headroom"));
        }
        let baseline_manifest_bytes = inner.manifest_slot_bytes()?;
        let budget = &inner.budget;
        let config = &inner.config;
        let compaction_bytes = run::run_build_reservation_bytes(&config.format)?.max(manifest_size);
        let mutable_reservation_bytes = usize::try_from(mutable_bytes)
            .map_err(|_| StorageError::Capacity("stage mutable memory"))?;
        let point_bytes = config
            .format
            .max_value_bytes
            .checked_add(config.format.max_key_bytes)
            .and_then(|bytes| bytes.checked_add(ENTRY_OVERHEAD))
            .and_then(|bytes| bytes.checked_mul(2))
            .ok_or(StorageError::Capacity("stage point memory"))?;
        let read_bytes = point_bytes
            .checked_add(config.format.max_directory_bytes)
            .and_then(|bytes| bytes.checked_add(config.format.max_block_stored_bytes))
            .and_then(|bytes| bytes.checked_add(64))
            .ok_or(StorageError::Capacity("stage read memory"))?;
        let banks = StagePermitBanks {
            frame: budget.try_reserve(
                config.attribution(MemoryCategory::WriteBuffer),
                frame_work_bytes,
            )?,
            mutable: budget.try_reserve(
                config.attribution(MemoryCategory::TransactionVersion),
                mutable_reservation_bytes,
            )?,
            mutable_items: budget.try_reserve_items(
                config.attribution(MemoryCategory::TransactionVersion),
                operations,
            )?,
            dedup: budget.try_reserve(
                config.attribution(MemoryCategory::DatabaseState),
                batch_count
                    .checked_mul(DEDUP_OVERHEAD)
                    .ok_or(StorageError::Capacity("stage dedup memory"))?,
            )?,
            dedup_items: budget.try_reserve_items(
                config.attribution(MemoryCategory::DatabaseState),
                batch_count,
            )?,
            compaction: budget.try_reserve(
                config.attribution(MemoryCategory::Compaction),
                compaction_bytes,
            )?,
            read: budget.try_reserve(config.attribution(MemoryCategory::ReadCache), read_bytes)?,
        };
        let id = inner.next_stage_work_id;
        inner.next_stage_work_id = id
            .checked_add(1)
            .ok_or(StorageError::Capacity("stage work identity exhausted"))?;
        inner.stage_work = Some(ActiveStageWork {
            id,
            remaining_frame: frame_bytes,
            remaining_mutable: mutable_bytes,
            remaining_directory: directory_bytes,
            remaining_runs: run_slots,
            remaining_dedup: batch_count,
            baseline_manifest_bytes,
            max_manifest_bytes: manifest_bytes,
        });
        Ok(StageWorkReservation {
            storage: self,
            plan,
            next_stage: 0,
            id,
            banks: Some(banks),
        })
    }

    /// Open or create a local shard, validating authority and streaming WAL recovery.
    ///
    /// # Errors
    /// Returns an error for invalid limits, admission failure, corrupt authority,
    /// or filesystem and recovery failures.
    pub fn open(config: StorageConfig, budget: MemoryBudget) -> Result<Self, StorageError> {
        config.validate()?;
        let gate = OpenGate::ordinary(&config.path)?;
        let storage = Self::open_under_gate(config, budget)?;
        gate.verify_opened_ordinary()?;
        Ok(storage)
    }

    /// Open the authority-bound `documents/` shard after publishing or checking
    /// its durable local mode marker under the physical parent gate.
    ///
    /// # Errors
    /// Returns an error for a mismatched mode, unsafe path or marker, storage
    /// admission, or recovery failure.
    pub fn open_distributed_document(
        config: StorageConfig,
        budget: MemoryBudget,
        mode: DistributedDocumentMode,
    ) -> Result<Self, StorageError> {
        config.validate()?;
        if config.tenant != mode.tenant() {
            return Err(StorageError::InvalidDirectory {
                path: config.path,
                reason: "document mode tenant differs from storage tenant",
            });
        }
        let gate = OpenGate::distributed(&config.path, mode)?;
        let storage = Self::open_under_gate(config, budget)?;
        gate.verify_opened_distributed(mode)?;
        Ok(storage)
    }

    fn open_under_gate(config: StorageConfig, budget: MemoryBudget) -> Result<Self, StorageError> {
        ensure_durable_directory(&config.path)?;
        let lock_file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
            .open(config.path.join(LOCK_NAME))?;
        let lock_metadata = lock_file.metadata()?;
        if !lock_metadata.is_file() || lock_metadata.nlink() != 1 {
            return Err(StorageError::Corrupt("shard lock type or links".into()));
        }
        FileExt::try_lock_exclusive(&lock_file).map_err(|_| StorageError::InvalidDirectory {
            path: config.path.clone(),
            reason: "another process owns the shard",
        })?;
        let floor = FloorAuthority::load_or_init(&config.path)?;
        let wal_path = config.path.join(WAL_NAME);
        let wal_existed = wal_path.exists();
        let wal = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(&wal_path)?;
        if !wal_existed {
            wal.sync_all()?;
            sync_directory(&config.path)?;
            crate::crash_at("after_genesis_wal_sync");
        }
        let manifest_capacity = config
            .max_runs
            .checked_mul(2 * (std::mem::size_of::<RunMeta>() + 32))
            .and_then(|bytes| bytes.checked_add(512))
            .ok_or(StorageError::Capacity("manifest metadata"))?;
        let manifest_permit = budget.try_reserve(
            config.attribution(MemoryCategory::DatabaseState),
            manifest_capacity,
        )?;
        let mut manifest = run::load_manifest(
            &config.path,
            &config.format,
            &budget,
            config.attribution(MemoryCategory::ReadCache),
        )?;
        if manifest.expired_floor_ms > floor.floor_ms() {
            return Err(StorageError::Corrupt(
                "manifest expiry floor exceeds durable authority".into(),
            ));
        }
        manifest.expired_floor_ms = floor.floor_ms();
        let wal_len = wal.metadata()?.len();
        let mut inner = Inner {
            config,
            budget,
            _manifest_permit: manifest_permit,
            _lock_file: lock_file,
            wal,
            wal_len,
            visible: manifest.checkpoint,
            floor,
            manifest,
            mutable: BTreeMap::new(),
            mutable_bytes: 0,
            mutable_directory_bytes: 0,
            mutable_permits: Vec::new(),
            mutable_item_permits: Vec::new(),
            scan_work: ScanCounters::default(),
            dedup: HashMap::new(),
            stage_work: None,
            next_stage_work_id: 1,
            poisoned: false,
            #[cfg(any(test, feature = "test-hooks"))]
            test_wal_fault: None,
        };
        run::cleanup_orphans(&inner.config.path)?;
        inner.recover()?;
        Ok(Self {
            inner: Mutex::new(inner),
            cancelled_stage_work_id: AtomicU64::new(0),
        })
    }

    /// Capture the current shard-local visibility cut.
    #[must_use]
    pub fn snapshot(&self) -> ReadSnapshot {
        let inner = self.lock();
        ReadSnapshot::new(inner.visible)
    }

    /// Get one version visible at a snapshot, retaining an output memory permit.
    ///
    /// # Errors
    /// Returns an error for an invalid snapshot, admission failure, or corrupt run.
    pub fn get(
        &self,
        snapshot: ReadSnapshot,
        key: &Key,
    ) -> Result<Option<VersionedValue>, StorageError> {
        let inner = self.lock();
        inner.validate_snapshot(snapshot)?;
        let permit = inner.budget.try_reserve(
            inner.config.attribution(MemoryCategory::Query),
            inner.config.format.max_value_bytes + ENTRY_OVERHEAD,
        )?;
        let selected = inner.point_raw(snapshot.seq, key)?;
        match selected.and_then(|admitted| {
            admitted
                .record
                .value
                .map(|value| (admitted.record.sequence, value))
        }) {
            None => Ok(None),
            Some((sequence, value)) => Ok(Some(VersionedValue {
                sequence,
                value,
                _permit: permit,
            })),
        }
    }

    /// Return a bounded, globally ordered page of visible rows.
    ///
    /// # Errors
    /// Returns an error for invalid bounds, admission failure, or corrupt run.
    pub fn scan(
        &self,
        snapshot: ReadSnapshot,
        range: &KeyRange,
        row_limit: usize,
    ) -> Result<ScanPage, StorageError> {
        let inner = self.lock();
        inner.validate_snapshot(snapshot)?;
        let _scan_guard = inner.scan_work.begin();
        inner.scan(snapshot.seq, range, row_limit)
    }

    /// Atomically commit an owned batch after checking its snapshot and retry token.
    ///
    /// # Errors
    /// Returns an error for invalid or conflicting input, exhausted capacity, or
    /// durability and apply failures requiring recovery.
    pub fn commit_batch(
        &self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
    ) -> Result<CommitReceipt, StorageError> {
        match self.prepare_batch(snapshot, transaction_id, mutations)? {
            PreparedBatchOutcome::Ready(prepared) => prepared.commit(),
            PreparedBatchOutcome::AlreadyCommitted(receipt) => Ok(receipt),
        }
    }

    /// Fully admit and freeze a batch while retaining the shard writer lock.
    ///
    /// Preparation may checkpoint older state, but it never appends this batch
    /// or advances its visible sequence. A caller may sync a separate journal
    /// while holding `Ready`, then either consume or drop it synchronously.
    ///
    /// # Errors
    /// Returns an error for invalid or conflicting input, exhausted capacity,
    /// or a shard that requires recovery.
    pub fn prepare_batch<'s>(
        &'s self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
    ) -> Result<PreparedBatchOutcome<'s>, StorageError> {
        Self::prepare_batch_locked(self.lock(), snapshot, transaction_id, mutations, None)
    }

    fn prepare_batch_locked<'s>(
        mut inner: std::sync::MutexGuard<'s, Inner>,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
        mut prepaid: Option<&mut StagePermitBanks>,
    ) -> Result<PreparedBatchOutcome<'s>, StorageError> {
        if inner.poisoned {
            return Err(StorageError::Poisoned);
        }
        let token_owned = prepaid.is_some();
        let prepared =
            inner.prepare_commit(snapshot, transaction_id, mutations, prepaid.as_deref_mut())?;
        if let Some(previous) = inner.dedup.get(&transaction_id) {
            return if previous.digest == prepared.digest {
                Ok(PreparedBatchOutcome::AlreadyCommitted(receipt(
                    transaction_id,
                    previous.sequence,
                )))
            } else {
                Err(StorageError::IdempotencyConflict)
            };
        }
        if !token_owned {
            inner.ensure_stage_headroom(
                prepared.frame_bytes.len(),
                prepared.estimated_mutable,
                prepared.directory_added,
                1,
            )?;
        }
        if inner.dedup.len() >= inner.config.max_live_transactions {
            inner.checkpoint_with_prepaid(
                prepaid.as_deref().map(|bank| &bank.compaction),
                token_owned,
            )?;
            if inner.dedup.len() >= inner.config.max_live_transactions {
                return Err(StorageError::Capacity("live transaction resolution table"));
            }
        }
        for mutation in mutations {
            if inner.latest_sequence(mutation.key(), prepaid.as_deref().map(|bank| &bank.read))?
                > snapshot.seq
            {
                return Err(StorageError::Conflict);
            }
        }
        if inner.mutable_bytes + prepared.estimated_mutable > inner.config.max_mutable_bytes
            || inner.projected_directory_bytes(prepared.directory_added)?
                > inner.config.format.max_directory_bytes
        {
            inner.checkpoint_with_prepaid(
                prepaid.as_deref().map(|bank| &bank.compaction),
                token_owned,
            )?;
        }
        let permits =
            inner.reserve_commit_permits(mutations.len(), prepared.estimated_mutable, prepaid)?;
        inner.ensure_commit_disk_headroom(
            prepared.frame_bytes.len(),
            prepared.estimated_mutable,
            prepared.directory_added,
            token_owned,
        )?;
        // Verify both the frame and WAL position before an external marker may
        // be synced. The held guard prevents in-process target-state drift.
        decode_wal_frame(&prepared.frame_bytes, &inner.config.format)?;
        let wal_offset = inner.wal.seek(SeekFrom::End(0))?;
        if wal_offset != inner.wal_len {
            return Err(StorageError::Corrupt(
                "WAL length changed before append".into(),
            ));
        }
        let next_wal_len = wal_offset
            .checked_add(prepared.frame_bytes.len() as u64)
            .ok_or(StorageError::Capacity("WAL length overflow"))?;
        // A preparatory checkpoint may have advanced the durable floor.
        inner.check_commit_deadline(transaction_id)?;
        Ok(PreparedBatchOutcome::Ready(PreparedBatch {
            inner,
            prepared,
            permits,
            transaction_id,
            wal_offset,
            next_wal_len,
            stage_footprint: None,
        }))
    }

    /// Build and durably publish an immutable run from the current mutable state.
    ///
    /// # Errors
    /// Returns an error for exhausted capacity or a filesystem or format failure.
    pub fn checkpoint(&self) -> Result<(), StorageError> {
        self.lock().checkpoint()
    }

    /// Expose resource and frontier counters for the real caller and metrics.
    #[must_use]
    pub fn stats(&self) -> StorageStats {
        self.lock().stats()
    }

    /// Read the shard's configured wall clock, including deterministic test clocks.
    #[must_use]
    pub fn now_unix_ms(&self) -> u64 {
        let inner = self.lock();
        (inner.config.clock)()
    }

    /// Create a bounded committed-batch cursor from the retained WAL.
    ///
    /// # Errors
    /// Returns an error if the retained WAL cannot be opened.
    pub fn committed_cursor(&self) -> Result<CommittedBatchCursor, StorageError> {
        let inner = self.lock();
        Ok(CommittedBatchCursor {
            wal: OpenOptions::new()
                .read(true)
                .open(inner.config.path.join(WAL_NAME))?,
            limits: inner.config.format,
            budget: inner.budget.clone(),
            attribution: inner.config.attribution(MemoryCategory::Replication),
            durable: inner.visible,
            offset: 0,
        })
    }

    /// Read the exact retained WAL frame for a live indexed transaction.
    ///
    /// This lookup establishes frame provenance. A map hit remains readable
    /// after its deadline; commit admission still checks the current clock.
    ///
    /// # Errors
    /// Returns an error if the ID is expired and absent, the indexed frame is
    /// corrupt, or memory or filesystem access fails.
    pub fn committed_batch_for_transaction(
        &self,
        transaction_id: TransactionId,
    ) -> Result<Option<CommittedBatch>, StorageError> {
        self.lock().committed_batch_for_transaction(transaction_id)
    }

    /// Arm a one-shot partial WAL write for guest fault-injection tests.
    #[cfg(feature = "test-hooks")]
    #[doc(hidden)]
    pub fn inject_partial_wal_write_for_test(&self, prefix_bytes: usize) {
        self.lock().test_wal_fault = Some(TestWalFault::AfterPrefix(prefix_bytes));
    }

    /// Arm a one-shot full WAL write that fails before sync for guest tests.
    #[cfg(feature = "test-hooks")]
    #[doc(hidden)]
    pub fn inject_wal_fault_before_sync_for_test(&self) {
        self.lock().test_wal_fault = Some(TestWalFault::BeforeSync);
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        let mut inner = self
            .inner
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let cancelled = self.cancelled_stage_work_id.load(Ordering::Acquire);
        if cancelled != 0
            && inner
                .stage_work
                .as_ref()
                .is_some_and(|work| work.id == cancelled)
        {
            inner.stage_work = None;
            self.cancelled_stage_work_id.store(0, Ordering::Release);
        }
        inner
    }
}

fn sync_directory(path: &Path) -> Result<(), StorageError> {
    let path = if path.as_os_str().is_empty() {
        Path::new(".")
    } else {
        path
    };
    File::open(path)?.sync_all()?;
    Ok(())
}

pub(crate) fn ensure_durable_directory(path: &Path) -> Result<(), StorageError> {
    if path.as_os_str().is_empty() {
        return Ok(());
    }
    if path.exists() {
        if !path.is_dir() {
            return Err(StorageError::InvalidDirectory {
                path: path.to_path_buf(),
                reason: "path exists but is not a directory",
            });
        }
        return Ok(());
    }
    let parent = path
        .parent()
        .ok_or_else(|| StorageError::InvalidDirectory {
            path: path.to_path_buf(),
            reason: "directory has no parent",
        })?;
    ensure_durable_directory(parent)?;
    std::fs::create_dir(path)?;
    sync_directory(parent)?;
    sync_directory(path)
}

fn receipt(transaction_id: TransactionId, sequence: CommitSeq) -> CommitReceipt {
    CommitReceipt {
        transaction_id,
        commit_seq: sequence,
        frontiers: CommitFrontiers {
            received: Some(sequence),
            durable: Some(sequence),
            applied: Some(sequence),
            object_archived: None,
        },
    }
}

fn canonical_digest(frame: &[u8]) -> [u8; 32] {
    let mut digest = Sha256::new();
    digest.update(&frame[..16]);
    digest.update(0_u64.to_le_bytes());
    digest.update(&frame[24..frame.len() - 4]);
    digest.finalize().into()
}

fn frame_operations(mutations: &[Mutation]) -> Vec<WalOperation<'_>> {
    mutations
        .iter()
        .map(|mutation| match mutation {
            Mutation::Put { key, value } => WalOperation::Put {
                key: key.as_bytes(),
                value,
            },
            Mutation::Delete { key } => WalOperation::Delete {
                key: key.as_bytes(),
            },
        })
        .collect()
}

fn in_range(key: &[u8], range: &KeyRange) -> bool {
    let after_start = match range.start() {
        KeyBound::Unbounded => true,
        KeyBound::Included(start) => key >= start.as_bytes(),
        KeyBound::Excluded(start) => key > start.as_bytes(),
    };
    let before_end = match range.end() {
        KeyBound::Unbounded => true,
        KeyBound::Included(end) => key <= end.as_bytes(),
        KeyBound::Excluded(end) => key < end.as_bytes(),
    };
    after_start && before_end
}

impl Inner {
    fn manifest_slot_bytes(&self) -> Result<u64, StorageError> {
        let mut bytes = 0_u64;
        for slot in 0..=1 {
            let path = self.config.path.join(format!("manifest.{slot}"));
            let length = match std::fs::metadata(path) {
                Ok(metadata) => metadata.len(),
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => 0,
                Err(error) => return Err(error.into()),
            };
            bytes = bytes
                .checked_add(length)
                .ok_or(StorageError::Capacity("manifest slot bytes"))?;
        }
        Ok(bytes)
    }

    fn ensure_stage_headroom(
        &self,
        extra_frame: usize,
        extra_mutable: usize,
        extra_directory: usize,
        extra_dedup: usize,
    ) -> Result<(), StorageError> {
        let Some(work) = &self.stage_work else {
            return Ok(());
        };
        if self
            .manifest
            .runs
            .len()
            .checked_add(work.remaining_runs)
            .is_none_or(|count| count > self.config.max_runs)
            || self
                .dedup
                .len()
                .checked_add(work.remaining_dedup)
                .and_then(|count| count.checked_add(extra_dedup))
                .is_none_or(|count| count > self.config.max_live_transactions)
        {
            return Err(StorageError::Capacity("stage run or dedup slots"));
        }
        let manifest_growth = self
            .manifest_slot_bytes()?
            .saturating_sub(work.baseline_manifest_bytes)
            .min(work.max_manifest_bytes.saturating_mul(2));
        let manifest_claim = work
            .max_manifest_bytes
            .checked_mul(3)
            .and_then(|claim| claim.checked_sub(manifest_growth))
            .ok_or(StorageError::Capacity("stage manifest bytes"))?;
        let claim = self
            .actual_disk_bytes()?
            .checked_add(work.remaining_frame)
            .and_then(|n| n.checked_add(self.mutable_bytes as u64))
            .and_then(|n| n.checked_add(work.remaining_mutable))
            .and_then(|n| {
                n.checked_add(
                    self.mutable_directory_bytes
                        .saturating_sub(DIRECTORY_FIXED_BYTES) as u64,
                )
            })
            .and_then(|n| n.checked_add(work.remaining_directory))
            .and_then(|n| n.checked_add((work.remaining_runs as u64).checked_mul(76)?))
            .and_then(|n| n.checked_add(manifest_claim))
            .and_then(|n| n.checked_add(floor::PENDING_BYTES as u64))
            .and_then(|n| n.checked_add(extra_frame as u64))
            .and_then(|n| n.checked_add(extra_mutable as u64))
            .and_then(|n| n.checked_add(extra_directory as u64))
            .ok_or(StorageError::Capacity("stage disk bytes"))?;
        if claim > self.config.max_disk_bytes {
            return Err(StorageError::Capacity("SSD bytes and stage headroom"));
        }
        Ok(())
    }

    fn finish_stage_batch(&mut self, footprint: BatchFootprint) -> Result<(), StorageError> {
        let work = self.stage_work.as_mut().ok_or(StorageError::Poisoned)?;
        work.remaining_frame = work
            .remaining_frame
            .checked_sub(footprint.frame_bytes as u64)
            .ok_or(StorageError::Capacity("stage frame claim"))?;
        work.remaining_mutable = work
            .remaining_mutable
            .checked_sub(footprint.mutable_bytes as u64)
            .ok_or(StorageError::Capacity("stage mutable claim"))?;
        work.remaining_directory = work
            .remaining_directory
            .checked_sub(footprint.directory_bytes as u64)
            .ok_or(StorageError::Capacity("stage directory claim"))?;
        work.remaining_dedup = work
            .remaining_dedup
            .checked_sub(1)
            .ok_or(StorageError::Capacity("stage dedup claim"))?;
        Ok(())
    }

    fn validate_snapshot(&self, snapshot: ReadSnapshot) -> Result<(), StorageError> {
        if snapshot.seq > self.visible {
            return Err(StorageError::InvalidSnapshot);
        }
        Ok(())
    }

    fn stats(&self) -> StorageStats {
        StorageStats {
            visible: self.visible,
            durable: self.visible,
            checkpoint: self.manifest.checkpoint,
            expired_floor_ms: self.manifest.expired_floor_ms,
            wal_bytes: self.wal_len,
            immutable_runs: self.manifest.runs.len(),
            mutable_bytes: self.mutable_bytes,
            live_transaction_ids: self.dedup.len(),
            disk_bytes: self.actual_disk_bytes().unwrap_or(u64::MAX),
            scan_work: self.scan_work.snapshot(),
        }
    }

    fn actual_disk_bytes(&self) -> Result<u64, StorageError> {
        let mut total = 0_u64;
        for entry in std::fs::read_dir(&self.config.path)? {
            let entry = entry?;
            let metadata = entry.metadata()?;
            if metadata.is_file() {
                total = total
                    .checked_add(metadata.len())
                    .ok_or(StorageError::Capacity("SSD byte accounting overflow"))?;
            }
        }
        Ok(total)
    }

    fn recover(&mut self) -> Result<(), StorageError> {
        // Persist the time-based expiry decision before leaving any old ID out
        // of the recovered index. A later clock rollback cannot revive it.
        let now_ms = (self.config.clock)();
        self.floor.advance(&self.config.path, now_ms)?;
        self.manifest.expired_floor_ms = self.floor.floor_ms();
        let mut reader = OpenOptions::new()
            .read(true)
            .open(self.config.path.join(WAL_NAME))?;
        let mut previous = CommitSeq::ZERO;
        let mut offset = 0_u64;
        loop {
            let mut header = [0_u8; WAL_HEADER_LEN];
            let header_read = read_up_to(&mut reader, &mut header)?;
            if header_read == 0 {
                break;
            }
            if header_read < WAL_HEADER_LEN {
                self.truncate_incomplete_tail(offset)?;
                break;
            }
            let frame_len = wal_frame_len(&header, &self.config.format)?;
            let _frame_permit = self.budget.try_reserve(
                self.config.attribution(MemoryCategory::WriteBuffer),
                frame_len,
            )?;
            let mut bytes = vec![0_u8; frame_len];
            bytes[..WAL_HEADER_LEN].copy_from_slice(&header);
            let body_read = read_up_to(&mut reader, &mut bytes[WAL_HEADER_LEN..])?;
            if body_read < frame_len - WAL_HEADER_LEN {
                self.truncate_incomplete_tail(offset)?;
                break;
            }
            let frame = decode_wal_frame(&bytes, &self.config.format)?;
            let sequence = CommitSeq::new(frame.sequence());
            if sequence.get()
                != previous
                    .get()
                    .checked_add(1)
                    .ok_or_else(|| StorageError::Corrupt("WAL sequence overflow".into()))?
            {
                return Err(StorageError::Corrupt("WAL sequence gap or repeat".into()));
            }
            previous = sequence;
            let transaction_id = frame.transaction_id();
            if transaction_id.expires_at_unix_ms() > self.manifest.expired_floor_ms {
                if self.dedup.len() >= self.config.max_live_transactions {
                    return Err(StorageError::Capacity("live transaction resolution table"));
                }
                let permit = self.budget.try_reserve(
                    self.config.attribution(MemoryCategory::DatabaseState),
                    DEDUP_OVERHEAD,
                )?;
                let item_permit = self
                    .budget
                    .try_reserve_items(self.config.attribution(MemoryCategory::DatabaseState), 1)?;
                if self
                    .dedup
                    .insert(
                        transaction_id,
                        DedupEntry {
                            sequence,
                            digest: canonical_digest(&bytes),
                            wal_offset: offset,
                            frame_len,
                            _permit: permit,
                            _item_permit: item_permit,
                        },
                    )
                    .is_some()
                {
                    return Err(StorageError::Corrupt(
                        "same transaction ID committed twice".into(),
                    ));
                }
            }
            if sequence > self.manifest.checkpoint {
                let estimated = frame_memory_bytes(&frame)?;
                let directory_added = frame_directory_bytes(&frame)?;
                if self.mutable_bytes + estimated > self.config.max_mutable_bytes
                    || self.projected_directory_bytes(directory_added)?
                        > self.config.format.max_directory_bytes
                {
                    self.checkpoint()?;
                }
                self.apply_frame(&frame, sequence, estimated, directory_added)?;
            }
            self.visible = sequence;
            offset = offset
                .checked_add(frame_len as u64)
                .ok_or_else(|| StorageError::Corrupt("WAL offset overflow".into()))?;
        }
        if previous < self.manifest.checkpoint {
            return Err(StorageError::Corrupt(
                "manifest checkpoint is ahead of retained WAL".into(),
            ));
        }
        self.wal_len = self.wal.metadata()?.len();
        self.wal.seek(SeekFrom::End(0))?;
        Ok(())
    }

    fn truncate_incomplete_tail(&mut self, offset: u64) -> Result<(), StorageError> {
        self.wal.set_len(offset)?;
        self.wal.sync_data()?;
        self.wal_len = offset;
        Ok(())
    }

    fn apply_frame(
        &mut self,
        frame: &tb_format::WalFrame<'_>,
        sequence: CommitSeq,
        estimated: usize,
        directory_added: usize,
    ) -> Result<(), StorageError> {
        if self.mutable_bytes + estimated > self.config.max_mutable_bytes {
            return Err(StorageError::Capacity("mutable version bytes"));
        }
        if self.projected_directory_bytes(directory_added)? > self.config.format.max_directory_bytes
        {
            return Err(StorageError::Capacity("run directory bytes"));
        }
        let item_permit = match self.budget.try_reserve_items(
            self.config.attribution(MemoryCategory::TransactionVersion),
            frame.operation_count(),
        ) {
            Ok(permit) => permit,
            Err(_) if !self.mutable.is_empty() => {
                self.checkpoint()?;
                self.budget.try_reserve_items(
                    self.config.attribution(MemoryCategory::TransactionVersion),
                    frame.operation_count(),
                )?
            }
            Err(error) => return Err(error.into()),
        };
        let permit = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::TransactionVersion),
            estimated,
        )?;
        for operation in frame.operations() {
            let operation = operation?;
            let (key, value) = match operation {
                WalOperation::Put { key, value } => (Key::new(key.to_vec())?, Some(value.to_vec())),
                WalOperation::Delete { key } => (Key::new(key.to_vec())?, None),
            };
            self.mutable
                .entry(key)
                .or_default()
                .insert(0, Version { sequence, value });
        }
        self.mutable_bytes += estimated;
        self.mutable_directory_bytes = self.projected_directory_bytes(directory_added)?;
        self.mutable_permits.push(permit);
        self.mutable_item_permits.push(item_permit);
        Ok(())
    }

    fn read_context(&self) -> run::ReadContext<'_> {
        run::ReadContext {
            limits: &self.config.format,
            budget: &self.budget,
            attribution: self.config.attribution(MemoryCategory::ReadCache),
            work: &self.scan_work,
            prepaid: None,
        }
    }

    fn point_raw(
        &self,
        snapshot: CommitSeq,
        key: &Key,
    ) -> Result<Option<AdmittedRawRecord>, StorageError> {
        self.point_raw_with_prepaid(snapshot, key, None)
    }

    fn point_raw_with_prepaid(
        &self,
        snapshot: CommitSeq,
        key: &Key,
        prepaid: Option<&Reservation>,
    ) -> Result<Option<AdmittedRawRecord>, StorageError> {
        self.scan_work.point_lookup();
        let candidate_bytes = self
            .config
            .format
            .max_value_bytes
            .checked_add(self.config.format.max_key_bytes)
            .and_then(|bytes| bytes.checked_add(ENTRY_OVERHEAD))
            .and_then(|bytes| bytes.checked_mul(2))
            .ok_or(StorageError::Capacity("point candidate memory"))?;
        let attribution = self.config.attribution(MemoryCategory::ReadCache);
        let permit = if let Some(prepaid) = prepaid {
            if prepaid.attribution() != attribution || prepaid.bytes() < candidate_bytes {
                return Err(StorageError::Capacity("prepaid point memory"));
            }
            None
        } else {
            Some(self.budget.try_reserve(attribution, candidate_bytes)?)
        };
        let mut best = self.mutable.get(key).and_then(|versions| {
            versions
                .iter()
                .find(|version| version.sequence <= snapshot)
                .map(|version| RawRecord {
                    sequence: version.sequence,
                    value: version.value.clone(),
                })
        });
        for run in &self.manifest.runs {
            let mut read = self.read_context();
            read.prepaid = prepaid;
            if let Some(candidate) = run::point(&self.config.path, run, key, snapshot, &read)? {
                if best.as_ref().is_some_and(|existing| {
                    candidate.sequence == existing.sequence && candidate.value != existing.value
                }) {
                    return Err(StorageError::Corrupt(
                        "different values at one key and commit sequence".into(),
                    ));
                }
                if best
                    .as_ref()
                    .is_none_or(|existing| candidate.sequence > existing.sequence)
                {
                    best = Some(candidate);
                }
            }
        }
        Ok(best.map(|record| AdmittedRawRecord {
            record,
            _permit: permit,
        }))
    }

    fn latest_sequence(
        &self,
        key: &Key,
        prepaid: Option<&Reservation>,
    ) -> Result<CommitSeq, StorageError> {
        Ok(self
            .point_raw_with_prepaid(self.visible, key, prepaid)?
            .map_or(CommitSeq::ZERO, |admitted| admitted.record.sequence))
    }

    fn committed_batch_for_transaction(
        &self,
        transaction_id: TransactionId,
    ) -> Result<Option<CommittedBatch>, StorageError> {
        let Some(entry) = self.dedup.get(&transaction_id) else {
            let deadline = transaction_id.expires_at_unix_ms();
            if deadline <= self.manifest.expired_floor_ms || deadline <= (self.config.clock)() {
                return Err(StorageError::IdempotencyExpired);
            }
            return Ok(None);
        };
        let frame_end = u64::try_from(entry.frame_len)
            .ok()
            .and_then(|bytes| entry.wal_offset.checked_add(bytes))
            .ok_or_else(|| StorageError::Corrupt("indexed WAL frame offset overflow".into()))?;
        if entry.frame_len < WAL_HEADER_LEN
            || entry.frame_len > self.config.format.max_frame_bytes
            || frame_end > self.wal_len
        {
            return Err(StorageError::Corrupt("indexed WAL frame bounds".into()));
        }
        let mut wal = File::open(self.config.path.join(WAL_NAME))?;
        wal.seek(SeekFrom::Start(entry.wal_offset))?;
        let mut header = [0_u8; WAL_HEADER_LEN];
        if read_up_to(&mut wal, &mut header)? != WAL_HEADER_LEN {
            return Err(StorageError::Corrupt(
                "indexed WAL frame header missing".into(),
            ));
        }
        let frame_len = wal_frame_len(&header, &self.config.format)
            .map_err(|_| StorageError::Corrupt("indexed WAL frame header invalid".into()))?;
        if frame_len != entry.frame_len {
            return Err(StorageError::Corrupt(
                "indexed WAL frame length mismatch".into(),
            ));
        }
        let permit = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::Replication),
            frame_len,
        )?;
        let mut encoded_frame = vec![0_u8; frame_len];
        encoded_frame[..WAL_HEADER_LEN].copy_from_slice(&header);
        if read_up_to(&mut wal, &mut encoded_frame[WAL_HEADER_LEN..])? != frame_len - WAL_HEADER_LEN
        {
            return Err(StorageError::Corrupt("indexed WAL frame truncated".into()));
        }
        let frame = decode_wal_frame(&encoded_frame, &self.config.format)
            .map_err(|_| StorageError::Corrupt("indexed WAL frame invalid".into()))?;
        let digest = canonical_digest(&encoded_frame);
        if CommitSeq::new(frame.sequence()) != entry.sequence
            || frame.transaction_id() != transaction_id
            || digest != entry.digest
        {
            return Err(StorageError::Corrupt(
                "indexed WAL frame identity mismatch".into(),
            ));
        }
        Ok(Some(CommittedBatch {
            sequence: entry.sequence,
            transaction_id,
            encoded_frame,
            digest,
            _permit: permit,
        }))
    }

    #[allow(clippy::too_many_lines)] // The merge keeps snapshot resolution, admission, and output bounds in one path.
    fn scan(
        &self,
        snapshot: CommitSeq,
        range: &KeyRange,
        row_limit: usize,
    ) -> Result<ScanPage, StorageError> {
        if row_limit == 0 || row_limit > self.config.max_scan_rows {
            return Err(StorageError::Capacity("scan row limit"));
        }
        let permit = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::Query),
            self.config.max_scan_bytes,
        )?;
        let mut rows = Vec::new();
        let row_charge = ENTRY_OVERHEAD
            .checked_add(1)
            .ok_or(StorageError::Capacity("scan row memory"))?
            .max(std::mem::size_of::<ScanRow>());
        let row_capacity = row_limit.min(self.config.max_scan_bytes / row_charge);
        rows.try_reserve_exact(row_capacity)
            .map_err(|_| StorageError::Capacity("scan output allocation"))?;
        let cursor_entry_bytes = self
            .config
            .format
            .max_key_bytes
            .checked_mul(3)
            .and_then(|bytes| bytes.checked_add(512))
            .and_then(|bytes| bytes.checked_add(self.config.path.as_os_str().len()))
            .and_then(|bytes| bytes.checked_add(32))
            .ok_or(StorageError::Capacity("scan cursor memory"))?;
        let cursor_bytes = self
            .manifest
            .runs
            .len()
            .checked_mul(cursor_entry_bytes)
            .ok_or(StorageError::Capacity("scan cursor memory"))?;
        let _cursor_permit = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::ReadCache),
            cursor_bytes,
        )?;
        let scratch_bytes = self
            .config
            .format
            .max_key_bytes
            .checked_mul(2)
            .and_then(|bytes| bytes.checked_add(24))
            .ok_or(StorageError::Capacity("scan descriptor memory"))?;
        let _scratch_permit = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::ReadCache),
            scratch_bytes,
        )?;
        let winner_bytes = self
            .config
            .format
            .max_value_bytes
            .checked_mul(2)
            .ok_or(StorageError::Capacity("scan winner memory"))?;
        let mut winner_permit = None;
        let mut scratch = Vec::new();
        scratch
            .try_reserve_exact(scratch_bytes)
            .map_err(|_| StorageError::Capacity("scan descriptor allocation"))?;
        let mut cursors = Vec::new();
        cursors
            .try_reserve_exact(self.manifest.runs.len())
            .map_err(|_| StorageError::Capacity("scan cursor allocation"))?;
        for run in &self.manifest.runs {
            limit_scan_files(&mut cursors, None);
            cursors.push(run::ScanCursor::open(
                &self.config.path,
                run,
                &self.read_context(),
            )?);
            let index = cursors.len() - 1;
            advance_run_cursor(
                &mut cursors,
                index,
                range,
                &self.read_context(),
                &mut scratch,
            )?;
        }
        let mut mutable = self
            .mutable
            .iter()
            .filter(|(key, _)| {
                self.scan_work.mutable_key();
                in_range(key.as_bytes(), range)
            })
            .peekable();
        let mut used_bytes = 0_usize;
        while rows.len() < row_limit {
            let key = mutable
                .peek()
                .map(|(key, _)| (*key).clone())
                .into_iter()
                .chain(cursors.iter().filter_map(|cursor| {
                    cursor
                        .current
                        .as_ref()
                        .map(|candidate| candidate.key.clone())
                }))
                .min();
            let Some(key) = key else { break };
            let mut best: Option<(CommitSeq, Option<Vec<u8>>)> = None;
            if mutable
                .peek()
                .is_some_and(|(candidate, _)| **candidate == key)
            {
                let (_, versions) = mutable
                    .next()
                    .ok_or_else(|| StorageError::Corrupt("mutable scan cursor lost key".into()))?;
                if let Some(version) = versions.iter().find(|version| version.sequence <= snapshot)
                {
                    if version.value.is_some() {
                        reserve_scan_winner(
                            &mut winner_permit,
                            &self.budget,
                            self.config.attribution(MemoryCategory::ReadCache),
                            winner_bytes,
                        )?;
                    }
                    best = Some((version.sequence, version.value.clone()));
                }
            }
            for index in 0..cursors.len() {
                while cursors[index]
                    .current
                    .as_ref()
                    .is_some_and(|candidate| candidate.key == key)
                {
                    let (candidate_sequence, candidate_value_range) = cursors[index]
                        .current
                        .as_ref()
                        .map(|candidate| (candidate.sequence, candidate.value_range))
                        .ok_or_else(|| StorageError::Corrupt("run scan cursor lost key".into()))?;
                    if candidate_sequence <= snapshot {
                        if let Some((sequence, value)) = &best {
                            if candidate_sequence >= *sequence {
                                if candidate_value_range.is_some() {
                                    reserve_scan_winner(
                                        &mut winner_permit,
                                        &self.budget,
                                        self.config.attribution(MemoryCategory::ReadCache),
                                        winner_bytes,
                                    )?;
                                }
                                let candidate_value = candidate_value_range
                                    .map(|range| {
                                        value_from_run_cursor(
                                            &mut cursors,
                                            index,
                                            range,
                                            &self.read_context(),
                                        )
                                    })
                                    .transpose()?;
                                if candidate_sequence == *sequence && candidate_value != *value {
                                    return Err(StorageError::Corrupt(
                                        "different values at one key and commit sequence".into(),
                                    ));
                                }
                                if candidate_sequence > *sequence {
                                    best = Some((candidate_sequence, candidate_value));
                                }
                            }
                        } else {
                            if candidate_value_range.is_some() {
                                reserve_scan_winner(
                                    &mut winner_permit,
                                    &self.budget,
                                    self.config.attribution(MemoryCategory::ReadCache),
                                    winner_bytes,
                                )?;
                            }
                            let candidate_value = candidate_value_range
                                .map(|range| {
                                    value_from_run_cursor(
                                        &mut cursors,
                                        index,
                                        range,
                                        &self.read_context(),
                                    )
                                })
                                .transpose()?;
                            best = Some((candidate_sequence, candidate_value));
                        }
                    }
                    advance_run_cursor(
                        &mut cursors,
                        index,
                        range,
                        &self.read_context(),
                        &mut scratch,
                    )?;
                }
            }
            if let Some((sequence, Some(value))) = best {
                used_bytes = used_bytes
                    .checked_add(key.as_bytes().len() + value.len() + ENTRY_OVERHEAD)
                    .ok_or(StorageError::Capacity("scan output bytes"))?;
                if used_bytes > self.config.max_scan_bytes {
                    return Err(StorageError::Capacity("scan output bytes"));
                }
                rows.push(ScanRow {
                    key,
                    sequence,
                    value,
                });
            }
        }
        Ok(ScanPage {
            rows,
            _permit: permit,
        })
    }
}

fn reserve_scan_winner(
    permit: &mut Option<Reservation>,
    budget: &MemoryBudget,
    attribution: Attribution,
    bytes: usize,
) -> Result<(), StorageError> {
    if permit.is_none() {
        *permit = Some(budget.try_reserve(attribution, bytes)?);
    }
    Ok(())
}

fn advance_run_cursor(
    cursors: &mut [run::ScanCursor],
    index: usize,
    range: &KeyRange,
    read: &run::ReadContext<'_>,
    scratch: &mut Vec<u8>,
) -> Result<(), StorageError> {
    const CACHE_BYTES: usize = 8 * 1024 * 1024;
    if !cursors[index].has_file() {
        limit_scan_files(cursors, Some(index));
    }
    loop {
        match cursors[index].advance(read, scratch, range) {
            Ok(()) => break,
            Err(StorageError::Admission(error)) => {
                let victim = (0..cursors.len())
                    .find(|&other| other != index && cursors[other].cached_bytes() > 0);
                if let Some(victim) = victim {
                    cursors[victim].evict_block(read.work);
                } else {
                    return Err(StorageError::Admission(error));
                }
            }
            Err(error) => return Err(error),
        }
    }
    while cursors
        .iter()
        .map(run::ScanCursor::cached_bytes)
        .sum::<usize>()
        > CACHE_BYTES
    {
        let victim =
            (0..cursors.len()).find(|&other| other != index && cursors[other].cached_bytes() > 0);
        if let Some(victim) = victim {
            cursors[victim].evict_block(read.work);
        } else {
            break;
        }
    }
    Ok(())
}

fn value_from_run_cursor(
    cursors: &mut [run::ScanCursor],
    index: usize,
    range: tb_format::SegmentValueRange,
    read: &run::ReadContext<'_>,
) -> Result<Vec<u8>, StorageError> {
    if !cursors[index].has_file() {
        limit_scan_files(cursors, Some(index));
    }
    loop {
        match cursors[index].value(range, read) {
            Ok(value) => return Ok(value),
            Err(StorageError::Admission(error)) => {
                let victim = (0..cursors.len())
                    .find(|&other| other != index && cursors[other].cached_bytes() > 0);
                if let Some(victim) = victim {
                    cursors[victim].evict_block(read.work);
                } else {
                    return Err(StorageError::Admission(error));
                }
            }
            Err(error) => return Err(error),
        }
    }
}

fn limit_scan_files(cursors: &mut [run::ScanCursor], except: Option<usize>) {
    const MAX_OPEN_SCAN_FILES: usize = 32;
    let mut open = cursors.iter().filter(|cursor| cursor.has_file()).count();
    if open < MAX_OPEN_SCAN_FILES {
        return;
    }
    for (index, cursor) in cursors.iter_mut().enumerate() {
        if Some(index) != except && cursor.has_file() {
            cursor.evict_file();
            open -= 1;
            if open < MAX_OPEN_SCAN_FILES {
                break;
            }
        }
    }
}

fn read_up_to(reader: &mut impl Read, buffer: &mut [u8]) -> Result<usize, std::io::Error> {
    let mut read = 0;
    while read < buffer.len() {
        match reader.read(&mut buffer[read..])? {
            0 => break,
            count => read += count,
        }
    }
    Ok(read)
}

fn frame_memory_bytes(frame: &tb_format::WalFrame<'_>) -> Result<usize, StorageError> {
    let mut total = 0_usize;
    for operation in frame.operations() {
        let operation = operation?;
        let bytes = match operation {
            WalOperation::Put { key, value } => key.len() + value.len() + ENTRY_OVERHEAD,
            WalOperation::Delete { key } => key.len() + ENTRY_OVERHEAD,
        };
        total = total
            .checked_add(bytes)
            .ok_or(StorageError::Capacity("mutable version bytes"))?;
    }
    Ok(total)
}

fn frame_directory_bytes(frame: &tb_format::WalFrame<'_>) -> Result<usize, StorageError> {
    let mut total = 0_usize;
    for operation in frame.operations() {
        let operation = operation?;
        let key_len = match operation {
            WalOperation::Put { key, .. } | WalOperation::Delete { key } => key.len(),
        };
        total = total
            .checked_add(directory_record_bytes(key_len)?)
            .ok_or(StorageError::Capacity("run directory bytes"))?;
    }
    Ok(total)
}

impl PreparedBatch<'_> {
    /// Length of the exact WAL frame reserved by this guard.
    #[must_use]
    pub fn encoded_len(&self) -> usize {
        self.prepared.frame_bytes.len()
    }

    /// Append, sync, and publish the frozen batch while retaining its writer lock.
    ///
    /// # Errors
    /// Returns an expiry error before append if the token lapsed while held.
    /// An uncertain write or any failure after sync poisons the shard until reopen.
    pub fn commit(self) -> Result<CommitReceipt, StorageError> {
        let Self {
            mut inner,
            prepared,
            permits,
            transaction_id,
            wal_offset,
            next_wal_len,
            stage_footprint,
        } = self;
        let PreparedCommit {
            frame_bytes,
            digest,
            next_seq,
            estimated_mutable,
            directory_added,
            _frame_permit,
        } = prepared;
        let current_wal_offset = inner.wal.seek(SeekFrom::End(0))?;
        if current_wal_offset != wal_offset || inner.wal_len != wal_offset {
            return Err(StorageError::Corrupt(
                "WAL length changed before append".into(),
            ));
        }
        inner.check_commit_deadline(transaction_id)?;
        if let Err(error) = inner.write_and_sync_frame(&frame_bytes) {
            inner.poisoned = true;
            return Err(StorageError::Io(error));
        }
        inner.wal_len = next_wal_len;
        crate::crash_at("after_wal_sync_before_apply");
        let Ok(frame) = decode_wal_frame(&frame_bytes, &inner.config.format) else {
            inner.poisoned = true;
            return Err(StorageError::Poisoned);
        };
        if inner
            .apply_frame_with_permit(
                &frame,
                next_seq,
                estimated_mutable,
                directory_added,
                permits.mutable,
                permits.mutable_item,
            )
            .is_err()
        {
            inner.poisoned = true;
            return Err(StorageError::Poisoned);
        }
        inner.dedup.insert(
            transaction_id,
            DedupEntry {
                sequence: next_seq,
                digest,
                wal_offset,
                frame_len: frame_bytes.len(),
                _permit: permits.dedup,
                _item_permit: permits.dedup_item,
            },
        );
        inner.visible = next_seq;
        if let Some(footprint) = stage_footprint
            && inner.finish_stage_batch(footprint).is_err()
        {
            inner.poisoned = true;
            return Err(StorageError::Poisoned);
        }
        Ok(receipt(transaction_id, next_seq))
    }
}

impl Inner {
    fn projected_directory_bytes(&self, added: usize) -> Result<usize, StorageError> {
        self.mutable_directory_bytes
            .max(DIRECTORY_FIXED_BYTES)
            .checked_add(added)
            .ok_or(StorageError::Capacity("run directory bytes"))
    }

    fn check_commit_deadline(&self, transaction_id: TransactionId) -> Result<(), StorageError> {
        let now_ms = (self.config.clock)();
        if transaction_id.expires_at_unix_ms() <= now_ms
            || transaction_id.expires_at_unix_ms() <= self.manifest.expired_floor_ms
        {
            return Err(StorageError::IdempotencyExpired);
        }
        Ok(())
    }

    fn write_and_sync_frame(&mut self, frame: &[u8]) -> std::io::Result<()> {
        #[cfg(any(test, feature = "test-hooks"))]
        if let Some(fault) = self.test_wal_fault.take() {
            return match fault {
                TestWalFault::AfterPrefix(bytes) => {
                    self.wal
                        .write_all(&frame[..bytes.min(frame.len().saturating_sub(1))])?;
                    Err(std::io::Error::other("injected partial WAL write"))
                }
                TestWalFault::BeforeSync => {
                    self.wal.write_all(frame)?;
                    Err(std::io::Error::other("injected WAL sync uncertainty"))
                }
            };
        }
        self.wal
            .write_all(frame)
            .and_then(|()| self.wal.sync_data())
    }

    fn prepare_commit(
        &self,
        snapshot: ReadSnapshot,
        transaction_id: TransactionId,
        mutations: &[Mutation],
        prepaid: Option<&mut StagePermitBanks>,
    ) -> Result<PreparedCommit, StorageError> {
        self.validate_snapshot(snapshot)?;
        let now_ms = (self.config.clock)();
        let deadline = transaction_id.expires_at_unix_ms();
        if deadline <= now_ms || deadline <= self.manifest.expired_floor_ms {
            return Err(StorageError::IdempotencyExpired);
        }
        if deadline > now_ms.saturating_add(self.config.max_retry_window_ms) {
            return Err(StorageError::InvalidBatch(
                "transaction retry deadline too far ahead",
            ));
        }
        if mutations.is_empty() || mutations.len() > self.config.format.max_operations {
            return Err(StorageError::InvalidBatch("operation count"));
        }
        let mut estimated_frame = WAL_HEADER_LEN + 8;
        let mut estimated_mutable = 0_usize;
        let mut directory_added = 0_usize;
        for (index, mutation) in mutations.iter().enumerate() {
            let key = mutation.key();
            if mutations[..index]
                .iter()
                .any(|previous| previous.key() == key)
            {
                return Err(StorageError::InvalidBatch("duplicate key"));
            }
            let value_bytes = match mutation {
                Mutation::Put { value, .. } => value.len(),
                Mutation::Delete { .. } => 0,
            };
            if value_bytes > self.config.format.max_value_bytes {
                return Err(StorageError::InvalidBatch("value too large"));
            }
            estimated_frame = estimated_frame
                .checked_add(9 + key.as_bytes().len() + value_bytes)
                .ok_or(StorageError::Capacity("WAL frame bytes"))?;
            estimated_mutable = estimated_mutable
                .checked_add(ENTRY_OVERHEAD + key.as_bytes().len() + value_bytes)
                .ok_or(StorageError::Capacity("mutable version bytes"))?;
            directory_added = directory_added
                .checked_add(directory_record_bytes(key.as_bytes().len())?)
                .ok_or(StorageError::Capacity("run directory bytes"))?;
        }
        if estimated_frame > self.config.format.max_frame_bytes {
            return Err(StorageError::Capacity("WAL frame bytes"));
        }
        if estimated_mutable > self.config.max_mutable_bytes {
            return Err(StorageError::Capacity("mutable version bytes"));
        }
        if DIRECTORY_FIXED_BYTES + directory_added > self.config.format.max_directory_bytes {
            return Err(StorageError::Capacity("run directory bytes"));
        }
        let frame_work = estimated_frame
            .checked_add(
                mutations
                    .len()
                    .checked_mul(std::mem::size_of::<WalOperation<'_>>())
                    .ok_or(StorageError::Capacity("WAL frame memory"))?,
            )
            .ok_or(StorageError::Capacity("WAL frame memory"))?;
        let frame_permit = if let Some(bank) = prepaid {
            bank.frame(frame_work)?
        } else {
            self.budget.try_reserve(
                self.config.attribution(MemoryCategory::WriteBuffer),
                frame_work,
            )?
        };
        let operations = frame_operations(mutations);
        let next_seq = CommitSeq::new(
            self.visible
                .get()
                .checked_add(1)
                .ok_or(StorageError::Capacity("commit sequence exhausted"))?,
        );
        let mut frame_bytes = Vec::new();
        encode_wal_frame_into(
            &mut frame_bytes,
            next_seq.get(),
            transaction_id,
            &operations,
            &self.config.format,
        )?;
        debug_assert_eq!(frame_bytes.len(), estimated_frame);
        let digest = canonical_digest(&frame_bytes);
        Ok(PreparedCommit {
            frame_bytes,
            digest,
            next_seq,
            estimated_mutable,
            directory_added,
            _frame_permit: frame_permit,
        })
    }

    fn reserve_commit_permits(
        &mut self,
        mutation_count: usize,
        estimated_mutable: usize,
        prepaid: Option<&mut StagePermitBanks>,
    ) -> Result<CommitPermits, StorageError> {
        if let Some(bank) = prepaid {
            self.reserve_commit_tables()?;
            return bank.commit(mutation_count, estimated_mutable);
        }
        let mutable_item = match self.budget.try_reserve_items(
            self.config.attribution(MemoryCategory::TransactionVersion),
            mutation_count,
        ) {
            Ok(permit) => permit,
            Err(_) if !self.mutable.is_empty() => {
                self.checkpoint()?;
                self.budget.try_reserve_items(
                    self.config.attribution(MemoryCategory::TransactionVersion),
                    mutation_count,
                )?
            }
            Err(error) => return Err(error.into()),
        };
        let mutable = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::TransactionVersion),
            estimated_mutable,
        )?;
        let dedup = self.budget.try_reserve(
            self.config.attribution(MemoryCategory::DatabaseState),
            DEDUP_OVERHEAD,
        )?;
        let dedup_item = self
            .budget
            .try_reserve_items(self.config.attribution(MemoryCategory::DatabaseState), 1)?;
        self.reserve_commit_tables()?;
        Ok(CommitPermits {
            mutable_item,
            mutable,
            dedup,
            dedup_item,
        })
    }

    fn reserve_commit_tables(&mut self) -> Result<(), StorageError> {
        self.dedup
            .try_reserve(1)
            .map_err(|_| StorageError::Capacity("dedup table allocation"))?;
        self.mutable_permits
            .try_reserve(1)
            .map_err(|_| StorageError::Capacity("mutable permit table allocation"))?;
        self.mutable_item_permits
            .try_reserve(1)
            .map_err(|_| StorageError::Capacity("mutable item table allocation"))?;
        Ok(())
    }

    fn ensure_commit_disk_headroom(
        &self,
        frame_len: usize,
        estimated_mutable: usize,
        directory_added: usize,
        token_owned: bool,
    ) -> Result<(), StorageError> {
        if self.stage_work.is_some() {
            return if token_owned {
                self.ensure_stage_headroom(0, 0, 0, 0)
            } else {
                self.ensure_stage_headroom(frame_len, estimated_mutable, directory_added, 1)
            };
        }
        let projected_directory = self.projected_directory_bytes(directory_added)?;
        let manifest_scratch = run::manifest_bytes_for_run_count(self.config.max_runs)?;
        let required = [
            frame_len as u64,
            self.mutable_bytes as u64,
            estimated_mutable as u64,
            projected_directory as u64,
            RUN_FIXED_BYTES as u64,
            manifest_scratch as u64,
            floor::PENDING_BYTES as u64,
        ]
        .into_iter()
        .try_fold(self.actual_disk_bytes()?, |total, bytes| {
            total
                .checked_add(bytes)
                .ok_or(StorageError::Capacity("SSD byte accounting overflow"))
        })?;
        if required > self.config.max_disk_bytes {
            return Err(StorageError::Capacity("SSD bytes and checkpoint headroom"));
        }
        Ok(())
    }

    fn apply_frame_with_permit(
        &mut self,
        frame: &tb_format::WalFrame<'_>,
        sequence: CommitSeq,
        estimated: usize,
        directory_added: usize,
        permit: Reservation,
        item_permit: ItemReservation,
    ) -> Result<(), StorageError> {
        for operation in frame.operations() {
            let operation = operation?;
            let (key, value) = match operation {
                WalOperation::Put { key, value } => (Key::new(key.to_vec())?, Some(value.to_vec())),
                WalOperation::Delete { key } => (Key::new(key.to_vec())?, None),
            };
            self.mutable
                .entry(key)
                .or_default()
                .insert(0, Version { sequence, value });
        }
        self.mutable_bytes += estimated;
        self.mutable_directory_bytes = self.projected_directory_bytes(directory_added)?;
        self.mutable_permits.push(permit);
        self.mutable_item_permits.push(item_permit);
        Ok(())
    }

    fn checkpoint(&mut self) -> Result<(), StorageError> {
        self.checkpoint_with_prepaid(None, false)
    }

    #[allow(
        clippy::too_many_lines,
        reason = "checkpoint publishes run and manifest under one writer guard"
    )]
    fn checkpoint_with_prepaid(
        &mut self,
        prepaid: Option<&Reservation>,
        token_owned: bool,
    ) -> Result<(), StorageError> {
        self.ensure_stage_headroom(
            0,
            0,
            if token_owned || self.mutable.is_empty() {
                0
            } else {
                76
            },
            0,
        )?;
        if !token_owned
            && !self.mutable.is_empty()
            && let Some(work) = &self.stage_work
            && self
                .manifest
                .runs
                .len()
                .checked_add(work.remaining_runs)
                .and_then(|count| count.checked_add(1))
                .is_none_or(|count| count > self.config.max_runs)
        {
            return Err(StorageError::Capacity(
                "immutable run count and stage slots",
            ));
        }
        let now_ms = (self.config.clock)();
        let mut next = self.manifest.clone();
        next.generation = next
            .generation
            .checked_add(1)
            .ok_or(StorageError::Capacity("manifest generation exhausted"))?;
        next.expired_floor_ms = next.expired_floor_ms.max(now_ms);
        let has_run = !self.mutable.is_empty();
        let next_run_count = next
            .runs
            .len()
            .checked_add(usize::from(has_run))
            .ok_or(StorageError::Capacity("immutable run count"))?;
        if next_run_count > self.config.max_runs {
            return Err(StorageError::Capacity("immutable run count"));
        }
        let manifest_scratch = run::manifest_bytes_for_run_count(next_run_count)? as u64;
        let bounded_run = if has_run {
            (self.mutable_bytes as u64)
                .checked_add(
                    self.mutable_directory_bytes
                        .saturating_sub(DIRECTORY_FIXED_BYTES) as u64,
                )
                .and_then(|bytes| bytes.checked_add(76))
                .ok_or(StorageError::Capacity("checkpoint run bytes"))?
        } else {
            0
        };
        if self.stage_work.is_none() {
            let required = self
                .actual_disk_bytes()?
                .checked_add(bounded_run)
                .and_then(|bytes| bytes.checked_add(manifest_scratch))
                .and_then(|bytes| bytes.checked_add(floor::PENDING_BYTES as u64))
                .ok_or(StorageError::Capacity("SSD checkpoint bytes"))?;
            if required > self.config.max_disk_bytes {
                return Err(StorageError::Capacity("SSD checkpoint bytes"));
            }
        }
        if !self.mutable.is_empty() {
            let run_id = next.generation;
            let run = run::write_run(
                &self.config.path,
                run_id,
                &self.mutable,
                &self.config.format,
                &self.budget,
                self.config.attribution(MemoryCategory::Compaction),
                prepaid,
            )?;
            if run.bytes > bounded_run {
                run::remove_unpublished_run(&self.config.path, &run)?;
                return Err(StorageError::Capacity("checkpoint run bytes"));
            }
            next.runs.push(run);
            next.checkpoint = self.visible;
        }
        if let Err(error) = self.floor.advance(&self.config.path, next.expired_floor_ms) {
            self.poisoned = true;
            return Err(error);
        }
        if let Err(error) = run::publish_manifest(
            &self.config.path,
            &next,
            &self.budget,
            self.config.attribution(MemoryCategory::Compaction),
            prepaid,
        ) {
            self.poisoned = true;
            return Err(error);
        }
        crate::crash_at("after_manifest_sync_before_apply");
        if token_owned && next.runs.len() > self.manifest.runs.len() {
            let work = self.stage_work.as_mut().ok_or(StorageError::Poisoned)?;
            work.remaining_runs = work
                .remaining_runs
                .checked_sub(1)
                .ok_or(StorageError::Capacity("stage run slots"))?;
        }
        self.manifest = next;
        self.mutable.clear();
        self.mutable_bytes = 0;
        self.mutable_directory_bytes = 0;
        self.mutable_permits.clear();
        self.mutable_item_permits.clear();
        self.dedup.retain(|transaction_id, _| {
            transaction_id.expires_at_unix_ms() > self.manifest.expired_floor_ms
        });
        Ok(())
    }
}

/// A durable local batch retained by the WAL. Its encoded bytes own a memory
/// permit for the full lifetime of the returned value.
pub struct CommittedBatch {
    pub sequence: CommitSeq,
    pub transaction_id: TransactionId,
    encoded_frame: Vec<u8>,
    pub digest: [u8; 32],
    _permit: Reservation,
}

impl CommittedBatch {
    /// Borrow the complete WAL frame while its memory permit remains held.
    #[must_use]
    pub fn encoded_bytes(&self) -> &[u8] {
        &self.encoded_frame
    }

    /// Decode the already validated, complete frame's canonical operations.
    ///
    /// # Errors
    /// Returns an error if the frame fails the supplied format limits.
    pub fn frame(&self, limits: &FormatLimits) -> Result<tb_format::WalFrame<'_>, StorageError> {
        Ok(decode_wal_frame(&self.encoded_frame, limits)?)
    }
}

/// Reads complete synced batches up to the durable frontier captured at open.
pub struct CommittedBatchCursor {
    wal: File,
    limits: FormatLimits,
    budget: MemoryBudget,
    attribution: Attribution,
    durable: CommitSeq,
    offset: u64,
}

impl CommittedBatchCursor {
    /// Read the next committed frame after `after`, bounded by `max_bytes`.
    ///
    /// # Errors
    /// Returns an error for an undersized bound, admission failure, or corrupt WAL.
    pub fn next(
        &mut self,
        after: CommitSeq,
        max_bytes: usize,
    ) -> Result<Option<CommittedBatch>, StorageError> {
        if after >= self.durable {
            return Ok(None);
        }
        self.wal.seek(SeekFrom::Start(self.offset))?;
        loop {
            let mut header = [0_u8; WAL_HEADER_LEN];
            let count = read_up_to(&mut self.wal, &mut header)?;
            if count == 0 {
                return Ok(None);
            }
            if count != WAL_HEADER_LEN {
                return Err(StorageError::Corrupt(
                    "truncated retained WAL header".into(),
                ));
            }
            let frame_len = wal_frame_len(&header, &self.limits)?;
            if frame_len > max_bytes {
                return Err(StorageError::Capacity("replication cursor frame bytes"));
            }
            let permit = self.budget.try_reserve(self.attribution, frame_len)?;
            let mut encoded_frame = vec![0_u8; frame_len];
            encoded_frame[..WAL_HEADER_LEN].copy_from_slice(&header);
            if read_up_to(&mut self.wal, &mut encoded_frame[WAL_HEADER_LEN..])?
                != frame_len - WAL_HEADER_LEN
            {
                return Err(StorageError::Corrupt("truncated retained WAL frame".into()));
            }
            self.offset += frame_len as u64;
            let frame = decode_wal_frame(&encoded_frame, &self.limits)?;
            let sequence = CommitSeq::new(frame.sequence());
            if sequence > self.durable {
                return Ok(None);
            }
            if sequence > after {
                let transaction_id = frame.transaction_id();
                let digest = canonical_digest(&encoded_frame);
                return Ok(Some(CommittedBatch {
                    sequence,
                    transaction_id,
                    encoded_frame,
                    digest,
                    _permit: permit,
                }));
            }
        }
    }
}

#[cfg(test)]
mod prepared_batch_tests {
    use std::fs;
    use std::sync::Arc;
    use std::time::{SystemTime, UNIX_EPOCH};

    use tb_core::{BudgetConfig, TenantId, WorkerId};

    use super::{
        Mutation, PreparedBatchOutcome, Storage, StorageConfig, StorageError, TestWalFault,
    };

    fn fixture(label: &str) -> (std::path::PathBuf, StorageConfig, tb_core::MemoryBudget) {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "terrabase-prepared-{label}-{}-{nanos}",
            std::process::id()
        ));
        let config = StorageConfig::local(&path, TenantId::from_bytes([1; 16]), WorkerId(1))
            .with_clock(Arc::new(|| 100));
        let budget =
            tb_core::MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("budget"));
        (path, config, budget)
    }

    #[test]
    fn post_sync_apply_failure_poisons_until_recovery() {
        let (path, config, budget) = fixture("poison");
        let storage = Storage::open(config.clone(), budget.clone()).expect("open");
        let id = tb_core::TransactionId::new(500, [1; 16]);
        let mutation = Mutation::Put {
            key: tb_core::Key::new(b"recovered".to_vec()).expect("key"),
            value: b"value".to_vec(),
        };
        let mut ready = match storage
            .prepare_batch(storage.snapshot(), id, &[mutation])
            .expect("prepare")
        {
            PreparedBatchOutcome::Ready(ready) => ready,
            PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
        };
        // Inject a post-sync apply error without changing the already frozen
        // WAL bytes. Recovery must replay that synced frame exactly once.
        ready.prepared.directory_added = usize::MAX;
        assert!(matches!(ready.commit(), Err(StorageError::Poisoned)));
        assert!(matches!(
            storage.commit_batch(
                storage.snapshot(),
                tb_core::TransactionId::new(500, [2; 16]),
                &[Mutation::Delete {
                    key: tb_core::Key::new(b"recovered".to_vec()).expect("key"),
                }],
            ),
            Err(StorageError::Poisoned)
        ));
        drop(storage);

        let storage = Storage::open(config, budget).expect("replay synced frame");
        assert_eq!(storage.stats().visible, tb_core::CommitSeq::new(1));
        assert_eq!(
            storage
                .get(
                    storage.snapshot(),
                    &tb_core::Key::new(b"recovered".to_vec()).expect("key")
                )
                .expect("read")
                .expect("replayed value")
                .bytes(),
            b"value"
        );
        drop(storage);
        fs::remove_dir_all(path).expect("remove shard");
    }

    #[test]
    fn partial_wal_write_poisons_and_recovery_discards_incomplete_tail() {
        let (path, config, budget) = fixture("partial");
        let storage = Storage::open(config.clone(), budget.clone()).expect("open");
        let id = tb_core::TransactionId::new(500, [3; 16]);
        let mutation = Mutation::Put {
            key: tb_core::Key::new(b"partial".to_vec()).expect("key"),
            value: b"value".to_vec(),
        };
        let mut ready = match storage
            .prepare_batch(storage.snapshot(), id, std::slice::from_ref(&mutation))
            .expect("prepare")
        {
            PreparedBatchOutcome::Ready(ready) => ready,
            PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
        };
        let prefix_len = ready.encoded_len() / 2;
        ready.inner.test_wal_fault = Some(TestWalFault::AfterPrefix(prefix_len));
        assert!(matches!(ready.commit(), Err(StorageError::Io(_))));
        assert!(matches!(
            storage.commit_batch(storage.snapshot(), id, std::slice::from_ref(&mutation)),
            Err(StorageError::Poisoned)
        ));
        assert_eq!(
            fs::metadata(path.join("wal.log"))
                .expect("WAL metadata")
                .len(),
            prefix_len as u64
        );
        drop(storage);

        let storage = Storage::open(config, budget).expect("discard incomplete tail");
        assert_eq!(storage.stats().visible, tb_core::CommitSeq::ZERO);
        assert_eq!(storage.stats().wal_bytes, 0);
        storage
            .commit_batch(storage.snapshot(), id, &[mutation])
            .expect("retry after incomplete tail");
        assert_eq!(storage.stats().visible, tb_core::CommitSeq::new(1));
        drop(storage);
        fs::remove_dir_all(path).expect("remove shard");
    }

    #[test]
    fn failed_sync_is_uncertain_until_reopen_reconciles_full_frame() {
        let (path, config, budget) = fixture("sync");
        let storage = Storage::open(config.clone(), budget.clone()).expect("open");
        let id = tb_core::TransactionId::new(500, [4; 16]);
        let mutation = Mutation::Put {
            key: tb_core::Key::new(b"sync".to_vec()).expect("key"),
            value: b"value".to_vec(),
        };
        let mut ready = match storage
            .prepare_batch(storage.snapshot(), id, std::slice::from_ref(&mutation))
            .expect("prepare")
        {
            PreparedBatchOutcome::Ready(ready) => ready,
            PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
        };
        ready.inner.test_wal_fault = Some(TestWalFault::BeforeSync);
        assert!(matches!(ready.commit(), Err(StorageError::Io(_))));
        assert!(matches!(
            storage.commit_batch(storage.snapshot(), id, std::slice::from_ref(&mutation)),
            Err(StorageError::Poisoned)
        ));
        drop(storage);

        // This same-host reopen sees the complete write. A real failed sync
        // does not guarantee those bytes would survive a power loss.
        let storage = Storage::open(config, budget).expect("reconcile uncertain full frame");
        assert_eq!(storage.stats().visible, tb_core::CommitSeq::new(1));
        assert_eq!(
            storage
                .get(
                    storage.snapshot(),
                    &tb_core::Key::new(b"sync".to_vec()).expect("key")
                )
                .expect("read")
                .expect("replayed value")
                .bytes(),
            b"value"
        );
        assert_eq!(
            storage
                .commit_batch(storage.snapshot(), id, &[mutation])
                .expect("retry returns recovered commit")
                .commit_seq,
            tb_core::CommitSeq::new(1)
        );
        drop(storage);
        fs::remove_dir_all(path).expect("remove shard");
    }
}
