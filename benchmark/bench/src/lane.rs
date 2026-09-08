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
    let operation_remainder = config.operations % config.concurrency;
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
            let warmup_offset = slot_index * per_slot_warmup + slot_index.min(warmup_remainder);
            let slot_warmup = per_slot_warmup + usize::from(slot_index < warmup_remainder);
            for index in 0..slot_warmup {
                if let Err(error) = one_op(
                    &mut *slot,
                    workload.select_mixed(warmup_offset + index),
                    warmup_offset + index,
                    config.seed_documents,
                ) {
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
            if failed.load(std::sync::atomic::Ordering::Acquire) {
                return Err(warmup_error
                    .unwrap_or_else(|| "another lane worker failed during warm-up".to_owned()));
            }
            let slot_operations =
                per_slot_operations + usize::from(slot_index < operation_remainder);
            let operation_offset =
                slot_index * per_slot_operations + slot_index.min(operation_remainder);
            let mut samples = Vec::with_capacity(config.samples);
            for sample in 0..config.samples {
                start.wait();
                let mut latencies = Vec::with_capacity(slot_operations);
                let mut sample_error = None;
                for index in 0..slot_operations {
                    let window_index = sample * config.operations + operation_offset + index;
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
    // Join every worker before returning, then prefer the shared originating
    // failure over a sibling's generic cancellation message.
    let outcomes: Vec<_> = handles.into_iter().map(std::thread::JoinHandle::join).collect();
    if failed.load(std::sync::atomic::Ordering::Acquire) {
        let message = failure
            .lock()
            .ok()
            .and_then(|message| message.clone())
            .unwrap_or_else(|| "a measured lane operation failed".to_owned());
        return Err(message.into());
    }
    let thread_samples = outcomes
        .into_iter()
        .map(|outcome| outcome.map_err(|_| "a slot thread panicked".to_owned())?)
        .collect::<Result<Vec<_>, String>>()?;

    let samples: Vec<Sample> = walls
        .into_iter()
        .enumerate()
        .map(|(sample, wall)| {
            let mut latencies = Vec::with_capacity(config.operations);
            for slot_samples in &thread_samples {
                latencies.extend_from_slice(&slot_samples[sample]);
            }
            metrics::sample_of(config.operations as u64, wall, latencies)
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
    // Backend failures, including unwinding panics, must flow through the
    // worker's shared failure path so every participant reaches its barrier.
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| match workload {
        Workload::Read => slot.read(index % seeded.max(1)),
        Workload::Write => slot.write(index as u64),
        Workload::Query => slot.query(index % seeded.max(1)),
        // `select_mixed` resolves the Mixed workload to its component
        // operations before this point, so a Mixed value here means the
        // executor's own invariant broke; failing loudly beats measuring
        // an operation the mapping does not name.
        Workload::Mixed => Err("mixed workload was not resolved by select_mixed".into()),
    }))
    .unwrap_or_else(|panic| {
        let message = panic
            .downcast_ref::<String>()
            .map(String::as_str)
            .or_else(|| panic.downcast_ref::<&str>().copied())
            .unwrap_or("non-string panic payload");
        Err(format!("backend operation panicked: {message}").into())
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[derive(Clone, Copy)]
    struct Fault {
        slot: usize,
        call: usize,
        panic: bool,
    }

    #[derive(Default)]
    struct Observed {
        opened: AtomicUsize,
        dropped: AtomicUsize,
        writes: Mutex<Vec<(usize, u64)>>,
    }

    struct TestBackend {
        observed: Arc<Observed>,
        fault: Option<Fault>,
    }

    struct TestSlot {
        observed: Arc<Observed>,
        fault: Option<Fault>,
        index: usize,
        calls: usize,
    }

    impl Drop for TestSlot {
        fn drop(&mut self) {
            self.observed.dropped.fetch_add(1, Ordering::SeqCst);
        }
    }

    impl Slot for TestSlot {
        fn read(&mut self, index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
            self.write(index as u64)
        }

        fn query(&mut self, index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
            self.write(index as u64)
        }

        fn write(&mut self, index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
            self.calls += 1;
            if let Some(fault) = self.fault
                && self.index == fault.slot
                && self.calls == fault.call
            {
                if fault.panic {
                    panic!("original backend panic");
                }
                return Err("original backend error".into());
            }
            self.observed.writes.lock().unwrap().push((self.index, index));
            Ok(())
        }
    }

    impl Backend for TestBackend {
        fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
            Ok(Self { observed: Arc::default(), fault: None })
        }
        fn target_name(&self) -> &'static str {
            "test"
        }
        fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
            Ok("test".to_owned())
        }
        fn endpoint(&self) -> String {
            "in-process test".to_owned()
        }
        fn client_transport(&self) -> &'static str {
            "test"
        }
        fn setup(&mut self, _: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
            Ok(())
        }
        fn seed(&mut self, _: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
            Ok(())
        }
        fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
            Ok(Box::new(TestSlot {
                observed: self.observed.clone(),
                fault: self.fault,
                index: self.observed.opened.fetch_add(1, Ordering::SeqCst),
                calls: 0,
            }))
        }
        fn limits(&self) -> Vec<String> {
            Vec::new()
        }
    }

    fn bounded_run(fault: Option<Fault>) -> (Result<RunResults, String>, Arc<Observed>) {
        let observed = Arc::new(Observed::default());
        let mut backend = TestBackend { observed: observed.clone(), fault };
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let driver = std::thread::spawn(move || {
            let result = run(
                &mut backend,
                Workload::Write,
                LaneConfig {
                    seed_documents: 1,
                    operations: 5,
                    samples: 2,
                    concurrency: 2,
                    warmup_operations: 3,
                    noise_percent: 5.0,
                },
            )
            .map_err(|error| error.to_string());
            sender.send(result).unwrap();
        });
        let result = receiver
            .recv_timeout(Duration::from_secs(5))
            .expect("lane must finish without stranding a barrier");
        driver.join().unwrap();
        assert_eq!(observed.dropped.load(Ordering::SeqCst), 2, "all slots joined before return");
        (result, observed)
    }

    #[test]
    fn warmup_error_releases_healthy_worker_and_preserves_error() {
        let (result, observed) = bounded_run(Some(Fault { slot: 0, call: 1, panic: false }));
        assert!(result.unwrap_err().contains("warm-up operation 0 failed: original backend error"));
        assert_eq!(*observed.writes.lock().unwrap(), vec![(1, 2)], "no measured work starts");
    }

    #[test]
    fn warmup_panic_releases_all_workers_and_preserves_payload() {
        let (result, observed) = bounded_run(Some(Fault { slot: 0, call: 1, panic: true }));
        assert!(result.unwrap_err().contains(
            "warm-up operation 0 failed: backend operation panicked: original backend panic"
        ));
        assert_eq!(*observed.writes.lock().unwrap(), vec![(1, 2)]);
    }

    #[test]
    fn measured_panic_releases_all_workers_and_rejects_partial_samples() {
        let (result, observed) = bounded_run(Some(Fault { slot: 1, call: 2, panic: true }));
        assert!(result.unwrap_err().contains(
            "measured operation 3 failed: backend operation panicked: original backend panic"
        ));
        assert!(
            observed.writes.lock().unwrap().iter().all(|(_, index)| *index < 5),
            "no second sample starts"
        );
    }

    #[test]
    fn healthy_run_counts_remainders_and_excludes_warmup_from_samples() {
        let (result, observed) = bounded_run(None);
        let result = result.unwrap();
        assert_eq!(result.status, "complete");
        assert_eq!(result.samples.len(), 2);
        assert!(result.samples.iter().all(|sample| sample.operations == 5));
        let writes = observed.writes.lock().unwrap();
        let slot_zero: Vec<_> =
            writes.iter().filter(|(slot, _)| *slot == 0).map(|(_, index)| *index).collect();
        let slot_one: Vec<_> =
            writes.iter().filter(|(slot, _)| *slot == 1).map(|(_, index)| *index).collect();
        assert_eq!(slot_zero, vec![0, 1, 0, 1, 2, 5, 6, 7]);
        assert_eq!(slot_one, vec![2, 3, 4, 8, 9]);
        assert_eq!(writes.len(), 3 + 2 * 5);
    }
}
