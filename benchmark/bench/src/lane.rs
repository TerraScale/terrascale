//! The lane executor: thread fan-out, warm-up discipline, and sample
//! windows, shared by every reference backend.
//!
//! Fairness rules this executor enforces on itself, matching the SE-15
//! pin and `src/benchmarks/lane.rs`:
//!
//! - one connection per concurrency slot, opened once before warm-up and
//!   reused for every sample (no per-sample connection churn);
//! - warm-up operations are real work excluded from every measured
//!   sample;
//! - every measured sample starts and ends on barriers of size
//!   `concurrency + 1` (all slots plus the coordinator), so all slots
//!   begin together and each sample's wall clock runs from the start
//!   barrier's release to the last slot's arrival at the end barrier;
//! - per-operation latencies are captured per operation, so percentiles
//!   describe operations, not windows.

use std::error::Error;
use std::sync::{Arc, Barrier};
use std::time::{Duration, Instant};

use crate::config::{LaneConfig, Workload};
use crate::metrics::{self, RunResults, Sample};

/// One concurrency slot's connection to the reference.
pub trait Slot {
    /// One point read of a seeded document.
    ///
    /// # Errors
    ///
    /// Returns the backend's error; the lane fails the run on any error
    /// rather than skipping an operation.
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>>;

    /// One acknowledged create/replace of document `doc_index`.
    ///
    /// # Errors
    ///
    /// Returns the backend's error.
    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>>;

    /// One equality query on the indexed `name` field of a seeded
    /// document, first row only.
    ///
    /// # Errors
    ///
    /// Returns the backend's error.
    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>>;
}

/// One reference backend: setup, seeding, and per-slot connections.
pub trait Backend {
    /// Opens the backend's administration handle.
    ///
    /// # Errors
    ///
    /// Returns the backend's connection error.
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>>
    where
        Self: Sized;

    /// The target name recorded in results JSON.
    fn target_name(&self) -> &'static str;

    /// The identity the reference reports about itself, verbatim.
    ///
    /// # Errors
    ///
    /// Returns the backend's error observing its own version.
    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>>;

    /// The endpoint string recorded in results JSON.
    fn endpoint(&self) -> String;

    /// The client transport description recorded in results JSON.
    fn client_transport(&self) -> &'static str;

    /// Prepares a fresh, empty store: schema and indexes created before
    /// warm-up, any prior run's data discarded.
    ///
    /// # Errors
    ///
    /// Returns the backend's error.
    fn setup(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>>;

    /// Seeds `config.seed_documents` documents shaped like the lane's
    /// benchmark document: a name, a group, and a 256-byte payload.
    ///
    /// # Errors
    ///
    /// Returns the backend's error.
    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>>;

    /// Opens one concurrency slot's connection with its prepared
    /// statements.
    ///
    /// # Errors
    ///
    /// Returns the backend's connection error.
    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>>;

    /// Honest limits this backend knows about its own side.
    fn limits(&self) -> Vec<String>;
}

/// The lane's benchmark document fields, shared by every backend so the
/// seeded and written shapes cannot drift between references.
pub mod document {
    /// The single tenant every benchmark document belongs to, matching the
    /// engine side's `benchmark-tenant`.
    pub const TENANT: &str = "benchmark-tenant";

    /// The written document's name field.
    pub const MEASURED_NAME: &str = "measured";

    /// The written document's group field.
    pub const MEASURED_GROUP: i32 = 42;

    /// The seeded document's name field: `user-{index}`.
    pub fn seeded_name(index: usize) -> String {
        format!("user-{index}")
    }

    /// The seeded document's identifier: `seed-{index}`.
    pub fn seeded_id(index: usize) -> String {
        format!("seed-{index}")
    }

    /// The written document's identifier: `measured-{index}`.
    pub fn measured_id(index: u64) -> String {
        format!("measured-{index}")
    }

    /// The seeded document's group field: `index % 100`.
    pub const fn seeded_group(index: usize) -> i32 {
        (index % 100) as i32
    }

    /// The 256-byte payload every benchmark document carries.
    pub fn payload() -> String {
        "x".repeat(256)
    }
}

/// Runs one workload against one backend and returns the results document.
///
/// # Errors
///
/// Returns the first backend error; a run that cannot complete fails
/// rather than emitting a partial sample set.
pub fn run<B: Backend>(
    backend: &mut B,
    workload: Workload,
    config: LaneConfig,
) -> Result<RunResults, Box<dyn Error + Send + Sync>> {
    config.validate()?;
    backend.setup(&config)?;
    backend.seed(&config)?;

    let environment = metrics::host_environment(
        backend.observed_identity()?,
        backend.endpoint(),
        backend.client_transport(),
    );

    let slots: Vec<_> =
        (0..config.concurrency).map(|_| backend.open_slot()).collect::<Result<Vec<_>, _>>()?;

    let per_slot_operations = config.operations / config.concurrency;
    let per_slot_warmup = config.warmup_operations / config.concurrency;
    let warmup_remainder = config.warmup_operations % config.concurrency;
    let start = Arc::new(Barrier::new(config.concurrency + 1));
    let end = Arc::new(Barrier::new(config.concurrency + 1));
    let warmup_end = Arc::new(Barrier::new(config.concurrency + 1));
    let failed = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let failure = Arc::new(std::sync::Mutex::new(None::<String>));

    let mut handles = Vec::with_capacity(slots.len());
    for (slot_index, mut slot) in slots.into_iter().enumerate() {
        let start = Arc::clone(&start);
        let end = Arc::clone(&end);
        let warmup_end = Arc::clone(&warmup_end);
        let failed = Arc::clone(&failed);
        let failure = Arc::clone(&failure);
        handles.push(std::thread::spawn(move || -> Result<Vec<Vec<f64>>, String> {
            // Warm-up: real work, excluded from every measured sample.
            let mut warmup_error = None;
            let slot_warmup = per_slot_warmup + usize::from(slot_index < warmup_remainder);
            for index in 0..slot_warmup {
                if let Err(error) =
                    one_op(&mut *slot, workload.select_mixed(index), index, config.seed_documents)
                {
                    warmup_error = Some(format!("warm-up operation {index} failed: {error}"));
                    break;
                }
            }
            if let Some(error) = &warmup_error {
                failed.store(true, std::sync::atomic::Ordering::Release);
                if let Ok(mut message) = failure.lock() {
                    message.get_or_insert_with(|| error.clone());
                }
            }
            // Every slot reaches this barrier even after an error, so a bad
            // warm-up cannot strand healthy slots before the coordinator can
            // classify the run as invalid.
            warmup_end.wait();
            if let Some(error) = warmup_error {
                return Err(error);
            }
            let mut samples = Vec::with_capacity(config.samples);
            for sample in 0..config.samples {
                start.wait();
                let mut latencies = Vec::with_capacity(per_slot_operations);
                let mut sample_error = None;
                for index in 0..per_slot_operations {
                    let window_index = sample * per_slot_operations + index;
                    let started = Instant::now();
                    if let Err(error) = one_op(
                        &mut *slot,
                        workload.select_mixed(window_index),
                        window_index,
                        config.seed_documents,
                    ) {
                        sample_error =
                            Some(format!("measured operation {window_index} failed: {error}"));
                        failed.store(true, std::sync::atomic::Ordering::Release);
                        if let Ok(mut message) = failure.lock() {
                            message.get_or_insert_with(|| sample_error.clone().unwrap_or_default());
                        }
                        break;
                    }
                    latencies.push(started.elapsed().as_secs_f64() * 1_000_000.0);
                }
                end.wait();
                if failed.load(std::sync::atomic::Ordering::Acquire) {
                    return Err(sample_error.unwrap_or_else(|| {
                        "another lane worker failed during the measured window".to_owned()
                    }));
                }
                samples.push(latencies);
            }
            Ok(samples)
        }));
    }

    warmup_end.wait();
    if failed.load(std::sync::atomic::Ordering::Acquire) {
        let message = failure
            .lock()
            .ok()
            .and_then(|message| message.clone())
            .unwrap_or_else(|| "a lane warm-up operation failed".to_owned());
        for handle in handles {
            let _ = handle.join();
        }
        return Err(message.into());
    }

    // The coordinator times each sample from the start barrier's release
    // to the last slot's arrival at the end barrier, then joins the
    // threads and merges each sample's latency vectors in the same order,
    // so a sample's wall clock and its latency set stay paired.
    let mut walls: Vec<Duration> = Vec::with_capacity(config.samples);
    for _ in 0..config.samples {
        start.wait();
        let started = Instant::now();
        end.wait();
        walls.push(started.elapsed());
        if failed.load(std::sync::atomic::Ordering::Acquire) {
            break;
        }
    }
    let mut thread_samples = Vec::with_capacity(handles.len());
    for handle in handles {
        thread_samples.push(
            handle
                .join()
                .map_err(|_| "a slot thread panicked".to_owned())?
                .map_err(|error| error.to_owned())?,
        );
    }
    if failed.load(std::sync::atomic::Ordering::Acquire) {
        let message = failure
            .lock()
            .ok()
            .and_then(|message| message.clone())
            .unwrap_or_else(|| "a measured lane operation failed".to_owned());
        return Err(message.into());
    }

    let samples: Vec<Sample> = walls
        .into_iter()
        .enumerate()
        .map(|(sample, wall)| {
            let mut latencies = Vec::with_capacity(config.operations);
            for slot_samples in &thread_samples {
                latencies.extend_from_slice(&slot_samples[sample]);
            }
            metrics::sample_of((per_slot_operations * config.concurrency) as u64, wall, latencies)
        })
        .collect();

    Ok(RunResults {
        status: "complete",
        target: backend.target_name(),
        workload: workload.as_str().to_owned(),
        config,
        comparability: metrics::comparability(&config, backend.target_name(), &environment, None),
        environment,
        samples,
        limits: backend.limits(),
    })
}

/// Executes one operation of a workload on one slot.
fn one_op<S: Slot + ?Sized>(
    slot: &mut S,
    workload: Workload,
    index: usize,
    seeded: usize,
) -> Result<(), Box<dyn Error + Send + Sync>> {
    match workload {
        Workload::Read => slot.read(index % seeded.max(1)),
        Workload::Write => slot.write(index as u64),
        Workload::Query => slot.query(index % seeded.max(1)),
        // `select_mixed` resolves the Mixed workload to its component
        // operations before this point, so a Mixed value here means the
        // executor's own invariant broke; failing loudly beats measuring
        // an operation the mapping does not name.
        Workload::Mixed => Err("mixed workload was not resolved by select_mixed".into()),
    }
}
