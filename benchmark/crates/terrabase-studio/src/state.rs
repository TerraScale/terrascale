//! Studio process state: the one engine location, and the small amount of
//! operator state the engine's public API does not expose in-process.
//!
//! ## Why any state exists beside the engine
//!
//! The engine's public surface has no tenant or collection enumeration:
//! `Engine::committed_tenants` and the storage port's `documents_at` window
//! are crate-private, and every query names one collection through
//! [`terrabase::QuerySource::try_new`]. The studio therefore keeps its own
//! registry of the tenants and collections it has observed, persisted as one
//! JSON sidecar under `<TERRABASE_STUDIO_DIR>/studio/state.json`. It is a
//! registry of names to *probe*, never a copy of data, and the Documents page
//! lets the operator type any tenant or collection the studio has not seen.

use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};

use crate::html::Tone;
use crate::s3_backup::S3BackupSettings;

/// How many recent action receipts the journal retains.
const ACTION_JOURNAL_CAPACITY: usize = 40;
/// Bounds the operator-name registry, which is a convenience index rather
/// than authoritative engine state.
const REGISTRY_TENANT_CAP: usize = 1_024;
const REGISTRY_COLLECTION_CAP: usize = 4_096;
/// Keep registry keys within the same byte bound as engine logical
/// identifiers. The registry is a convenience index, but it is persisted and
/// must not become an unbounded sink for arbitrary form input.
const REGISTRY_IDENTIFIER_MAX_BYTES: usize = 255;
/// Maximum synchronous engine jobs admitted at once. Excess work fails fast
/// instead of growing Tokio's blocking queue without bound.
pub const MAX_BLOCKING_OPERATIONS: usize = 8;
/// Maximum long-lived metrics streams admitted at once.
pub const MAX_METRICS_STREAMS: usize = 4;

/// Environment variable selecting the durable engine location.
pub const ENV_DIR: &str = "TERRABASE_STUDIO_DIR";
/// Environment variable selecting the listen address.
pub const ENV_ADDR: &str = "TERRABASE_STUDIO_ADDR";

/// Default listen address: loopback only, never a public boundary.
pub const DEFAULT_ADDR: &str = "127.0.0.1:8090";
/// Default engine location, relative to the working directory.
pub const DEFAULT_DIR: &str = "terrabase-studio-data";

/// The resolved runtime configuration.
#[derive(Clone, Debug)]
pub struct Config {
    /// The durable engine location the studio opened.
    pub data_dir: PathBuf,
    /// The address the HTTP listener binds.
    pub addr: String,
}

impl Config {
    /// Reads the configuration from the environment, applying the defaults.
    pub fn from_env() -> Self {
        let data_dir = std::env::var(ENV_DIR).unwrap_or_else(|_| DEFAULT_DIR.to_owned());
        let addr = std::env::var(ENV_ADDR).unwrap_or_else(|_| DEFAULT_ADDR.to_owned());
        Self { data_dir: PathBuf::from(data_dir), addr }
    }

    /// The directory holding the studio's own sidecar files.
    pub fn sidecar_dir(&self) -> PathBuf {
        self.data_dir.join("studio")
    }

    /// Disposable transfer space for S3 upload, verification, and restore.
    pub fn backup_scratch_dir(&self) -> PathBuf {
        self.data_dir.join("studio").join("backup-transfer")
    }
}

/// One operator action and its typed outcome, as the journal recorded it.
#[derive(Clone, Debug)]
pub struct ActionRecord {
    /// Wall-clock time of the action, rendered as an RFC 3339 stamp.
    pub at: String,
    /// Short action name, for example `documents/put`.
    pub action: String,
    /// The typed outcome rendered for the operator.
    pub outcome: String,
    /// Whether the outcome was a success.
    pub ok: bool,
}

/// The persisted shape of the studio's sidecar file.
#[derive(Default, Deserialize, Serialize)]
struct Sidecar {
    /// Tenant names the studio has observed, sorted.
    tenants: BTreeSet<String>,
    /// Collection names per tenant the studio has observed.
    collections: BTreeMap<String, BTreeSet<String>>,
    /// The operator's topology specification, if edited.
    #[serde(default)]
    topology: TopologySpec,
    /// Non-secret S3 backup settings. Credentials come from workload identity
    /// or the standard AWS environment and never enter this file.
    #[serde(default)]
    backup: S3BackupSettings,
}

/// The operator's editable topology specification.
///
/// The engine's `TopologyView` is an immutable builder, so the studio stores
/// the spec it came from and rebuilds the view on demand. Each region names
/// exactly two members because that is the shape the cluster crate's
/// durability model requires.
#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct TopologySpec {
    /// The regions, in declaration order.
    pub regions: Vec<RegionSpec>,
}

impl TopologySpec {
    /// A two-region starting point, so the page has something to show.
    pub fn seeded() -> Self {
        Self {
            regions: vec![
                RegionSpec {
                    name: "gru".to_owned(),
                    members: ["gru-a".to_owned(), "gru-b".to_owned()],
                },
                RegionSpec {
                    name: "sfo".to_owned(),
                    members: ["sfo-a".to_owned(), "sfo-b".to_owned()],
                },
            ],
        }
    }
}

/// One region of the operator's topology specification.
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RegionSpec {
    /// The region identifier.
    pub name: String,
    /// The region's two voting members.
    pub members: [String; 2],
}

/// The whole studio process state shared by every handler.
pub struct StudioState {
    /// The one engine location this studio drives.
    pub engine: Arc<terrabase::Engine>,
    /// The resolved configuration.
    pub config: Config,
    sidecar_path: PathBuf,
    registry: Mutex<Sidecar>,
    journal: Mutex<Vec<ActionRecord>>,
    operation_counter: AtomicU64,
    /// Admission control for synchronous engine and filesystem work.
    pub blocking_slots: Arc<tokio::sync::Semaphore>,
    /// Admission control for long-lived browser metrics streams.
    pub metrics_slots: Arc<tokio::sync::Semaphore>,
    shutdown: tokio::sync::watch::Sender<bool>,
    /// The studio-local usage segment ledger.
    ///
    /// The engine's usage domain is a pure value domain: `UsageSegmentLedger`
    /// has no durable store bound to an engine location. This instance is
    /// studio-local and in-memory, and the Usage page says so.
    pub usage: Mutex<terrabase::UsageSegmentLedger>,
    /// The deletion workflows the studio is driving.
    pub deletions: Mutex<terrabase::deletion::DeletionLedger>,
    /// The operator's topology specification, edited on the Replication page.
    pub topology: Mutex<TopologySpec>,
    /// The region catalog the operator is editing.
    pub regions: Mutex<terrabase::RegionCatalog>,
    /// The voter the durability model currently treats as inactive.
    ///
    /// The cluster crate's coordinator is rebuilt per request, so this is the
    /// only place the page's fault-injection toggle survives between requests.
    pub durability_inactive: Mutex<Option<String>>,
    /// Serializes manual and scheduled backup or restore transfers.
    pub backup_job: tokio::sync::Mutex<()>,
    backup_successes: AtomicU64,
    backup_failures: AtomicU64,
    backup_last_success_millis: AtomicU64,
}

impl StudioState {
    /// Opens the engine and loads the sidecar registry.
    ///
    /// # Errors
    ///
    /// Returns a human-readable message when the engine location cannot be
    /// opened or the sidecar directory cannot be created.
    pub fn open(config: Config) -> Result<Self, String> {
        let engine_config =
            terrabase::EngineConfig::durable(&config.data_dir).map_err(|error| {
                format!("engine location {:?} is unusable: {error:?}", config.data_dir)
            })?;
        let engine = terrabase::Engine::open(engine_config).map_err(|error| {
            format!("cannot open engine location {:?}: {error:?}", config.data_dir)
        })?;
        let sidecar_dir = config.sidecar_dir();
        std::fs::create_dir_all(&sidecar_dir)
            .map_err(|error| format!("cannot create {}: {error}", sidecar_dir.display()))?;
        let sidecar_path = sidecar_dir.join("state.json");
        let mut registry = read_sidecar(&sidecar_path);
        if let Some(environment) = S3BackupSettings::from_environment() {
            registry.backup = environment;
        }
        let topology = registry.topology.clone();
        let (shutdown, _) = tokio::sync::watch::channel(false);
        Ok(Self {
            engine: Arc::new(engine),
            config,
            sidecar_path,
            registry: Mutex::new(registry),
            journal: Mutex::new(Vec::new()),
            operation_counter: AtomicU64::new(1),
            blocking_slots: Arc::new(tokio::sync::Semaphore::new(MAX_BLOCKING_OPERATIONS)),
            metrics_slots: Arc::new(tokio::sync::Semaphore::new(MAX_METRICS_STREAMS)),
            shutdown,
            usage: Mutex::new(terrabase::UsageSegmentLedger::default()),
            deletions: Mutex::new(terrabase::deletion::DeletionLedger::default()),
            topology: Mutex::new(topology),
            regions: Mutex::new(terrabase::RegionCatalog::production()),
            durability_inactive: Mutex::new(None),
            backup_job: tokio::sync::Mutex::new(()),
            backup_successes: AtomicU64::new(0),
            backup_failures: AtomicU64::new(0),
            backup_last_success_millis: AtomicU64::new(0),
        })
    }

    /// Records one action outcome and returns the tone the receipt should use.
    pub fn record(&self, action: &str, outcome: String, ok: bool) -> Tone {
        let at = chrono_free_rfc3339();
        let record = ActionRecord { at, action: action.to_owned(), outcome, ok };
        let mut journal = self.journal.lock().unwrap_or_else(|poison| poison.into_inner());
        journal.push(record);
        let overflow = journal.len().saturating_sub(ACTION_JOURNAL_CAPACITY);
        journal.drain(..overflow);
        if ok { Tone::Good } else { Tone::Bad }
    }

    /// Mints a fresh operation identity for one engine transaction.
    pub fn next_operation(&self) -> Result<terrabase::OperationId, String> {
        let value = self.operation_counter.fetch_add(1, Ordering::Relaxed);
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|since| since.as_millis())
            .unwrap_or_default();
        // The engine's durable operation ledger binds an identity to one write
        // set for the tenant's whole history, so the mint spans restarts: the
        // process id separates two studios over the same location and the
        // counter separates two writes inside one process.
        terrabase::OperationId::try_new(&format!("studio-{}-{now}-{value}", std::process::id()))
            .map_err(|error| format!("cannot mint an operation identity: {error:?}"))
    }

    /// Subscribes a long-lived response to process shutdown.
    pub fn shutdown_receiver(&self) -> tokio::sync::watch::Receiver<bool> {
        self.shutdown.subscribe()
    }

    /// Tells long-lived responses to finish before Axum drains connections.
    pub fn begin_shutdown(&self) {
        let _ = self.shutdown.send(true);
    }

    /// The tenant names the studio has observed, sorted.
    pub fn tenants(&self) -> Vec<String> {
        self.registry
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
            .tenants
            .iter()
            .cloned()
            .collect()
    }

    /// The collection names the studio has observed for one tenant, sorted.
    pub fn collections_of(&self, tenant: &str) -> Vec<String> {
        self.registry
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
            .collections
            .get(tenant)
            .map(|names| names.iter().cloned().collect())
            .unwrap_or_default()
    }

    /// The operator's current topology specification.
    pub fn topology(&self) -> TopologySpec {
        self.topology.lock().unwrap_or_else(|poison| poison.into_inner()).clone()
    }

    /// The voter the durability model treats as inactive, if any.
    pub fn inactive_voter(&self) -> Option<String> {
        self.durability_inactive.lock().unwrap_or_else(|poison| poison.into_inner()).clone()
    }

    /// Sets the voter the durability model treats as inactive.
    pub fn set_inactive_voter(&self, member: Option<String>) {
        *self.durability_inactive.lock().unwrap_or_else(|poison| poison.into_inner()) = member;
    }

    /// Replaces the topology specification and persists it.
    pub fn set_topology(&self, spec: TopologySpec) {
        *self.topology.lock().unwrap_or_else(|poison| poison.into_inner()) = spec.clone();
        let mut registry = self.registry.lock().unwrap_or_else(|poison| poison.into_inner());
        registry.topology = spec;
        drop(registry);
        let _ = self.persist_registry_locked();
    }

    /// Rebuilds the engine's topology view from the stored specification.
    ///
    /// Returns the first typed refusal the builder reports, so the page can
    /// show the exact rule a spec violates instead of a partial view.
    pub fn topology_view(&self) -> Result<terrabase::TopologyView, String> {
        let spec = self.topology();
        let mut view = terrabase::TopologyView::new();
        for region in &spec.regions {
            view = view
                .with_region(&region.name)
                .map_err(|error| format!("region `{}` refused: {error:?}", region.name))?;
            for member in &region.members {
                view = view
                    .with_member(member, &region.name, &region.name)
                    .map_err(|error| format!("member `{member}` refused: {error:?}"))?;
            }
        }
        view.validated().map_err(|error| format!("the topology is not servable: {error:?}"))
    }

    /// Remembers a tenant and one of its collections, persisting the registry.
    pub fn observe(&self, tenant: &str, collection: Option<&str>) {
        let mut registry = self.registry.lock().unwrap_or_else(|poison| poison.into_inner());
        record_observation(&mut registry, tenant, collection);
        drop(registry);
        let _ = self.persist_registry_locked();
    }

    /// The recent action journal, newest first.
    pub fn journal(&self) -> Vec<ActionRecord> {
        let journal = self.journal.lock().unwrap_or_else(|poison| poison.into_inner());
        journal.iter().rev().cloned().collect()
    }

    /// Returns the current non-secret S3 backup settings.
    pub fn backup_settings(&self) -> S3BackupSettings {
        self.registry.lock().unwrap_or_else(|poison| poison.into_inner()).backup.clone()
    }

    /// Persists validated non-secret S3 backup settings.
    pub fn set_backup_settings(&self, settings: S3BackupSettings) -> Result<(), String> {
        settings.validate()?;
        self.registry.lock().unwrap_or_else(|poison| poison.into_inner()).backup = settings;
        self.persist_registry_locked()
    }

    /// Records one S3 backup attempt for bounded Prometheus metrics.
    pub fn record_backup_run(&self, succeeded: bool) {
        if succeeded {
            self.backup_successes.fetch_add(1, Ordering::Relaxed);
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis();
            self.backup_last_success_millis
                .store(u64::try_from(now).unwrap_or(u64::MAX), Ordering::Relaxed);
        } else {
            self.backup_failures.fetch_add(1, Ordering::Relaxed);
        }
    }

    /// Returns successful S3 backup attempts in this Studio process.
    pub fn backup_successes(&self) -> u64 {
        self.backup_successes.load(Ordering::Relaxed)
    }

    /// Returns failed S3 backup attempts in this Studio process.
    pub fn backup_failures(&self) -> u64 {
        self.backup_failures.load(Ordering::Relaxed)
    }

    /// Returns the latest successful S3 backup time as Unix milliseconds.
    pub fn backup_last_success_millis(&self) -> u64 {
        self.backup_last_success_millis.load(Ordering::Relaxed)
    }

    fn persist_registry_locked(&self) -> Result<(), String> {
        let registry = self.registry.lock().unwrap_or_else(|poison| poison.into_inner());
        let body = serde_json::to_string_pretty(&*registry)
            .map_err(|error| format!("cannot encode the registry: {error}"))?;
        std::fs::write(&self.sidecar_path, body)
            .map_err(|error| format!("cannot write {}: {error}", self.sidecar_path.display()))
    }
}

/// Adds one observation to a registry while enforcing both identifier and
/// cardinality bounds. In particular, a tenant rejected by the tenant cap
/// must not still acquire a collection entry in the sidecar.
fn record_observation(registry: &mut Sidecar, tenant: &str, collection: Option<&str>) {
    if !valid_registry_identifier(tenant) {
        return;
    }
    let known_tenant = registry.tenants.contains(tenant);
    if !known_tenant && registry.tenants.len() >= REGISTRY_TENANT_CAP {
        return;
    }
    registry.tenants.insert(tenant.to_owned());

    let Some(collection) = collection.filter(|value| valid_registry_identifier(value)) else {
        return;
    };
    let names = registry.collections.entry(tenant.to_owned()).or_default();
    if names.len() < REGISTRY_COLLECTION_CAP || names.contains(collection) {
        names.insert(collection.to_owned());
    }
}

fn valid_registry_identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= REGISTRY_IDENTIFIER_MAX_BYTES
        && value == value.trim()
        && !value.chars().any(char::is_control)
}

fn sanitize_sidecar(mut sidecar: Sidecar) -> Sidecar {
    sidecar.tenants = sidecar
        .tenants
        .into_iter()
        .filter(|tenant| valid_registry_identifier(tenant))
        .take(REGISTRY_TENANT_CAP)
        .collect();
    sidecar.collections = sidecar
        .collections
        .into_iter()
        .filter(|(tenant, _)| sidecar.tenants.contains(tenant))
        .map(|(tenant, names)| {
            let names = names
                .into_iter()
                .filter(|name| valid_registry_identifier(name))
                .take(REGISTRY_COLLECTION_CAP)
                .collect();
            (tenant, names)
        })
        .collect();
    sidecar
}

fn read_sidecar(path: &std::path::Path) -> Sidecar {
    let mut sidecar = match std::fs::read_to_string(path) {
        Ok(body) => serde_json::from_str(&body).unwrap_or_default(),
        Err(_) => Sidecar::default(),
    };
    sidecar = sanitize_sidecar(sidecar);
    if sidecar.topology.regions.is_empty() {
        sidecar.topology = TopologySpec::seeded();
    }
    sidecar
}

/// Formats the current time as an RFC 3339 stamp without a date dependency.
fn chrono_free_rfc3339() -> String {
    let since =
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let total = since.as_secs();
    let millis = since.subsec_millis();
    let (year, month, day, hour, minute, second) = civil_from_unix(total as i64);
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

/// Splits a Unix timestamp into civil date and time parts.
///
/// Howard Hinnant's `civil_from_days` algorithm; the studio needs no date
/// crate for a journal stamp.
fn civil_from_unix(seconds: i64) -> (i64, u32, u32, u32, u32, u32) {
    let days = seconds.div_euclid(86_400);
    let remainder = seconds.rem_euclid(86_400);
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let mp = (5 * day_of_year + 2) / 153;
    let day = (day_of_year - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = if month <= 2 { year + 1 } else { year };
    (
        year,
        month,
        day,
        (remainder / 3_600) as u32,
        ((remainder % 3_600) / 60) as u32,
        (remainder % 60) as u32,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn civil_conversion_matches_known_stamps() {
        // Each pair is a UTC timestamp verified against an independent calendar.
        for (seconds, expected) in [
            (0_i64, (1970_i64, 1_u32, 1_u32, 0_u32, 0, 0)),
            (1_767_225_600, (2026, 1, 1, 0, 0, 0)),
            (1_789_056_000, (2026, 9, 10, 16, 0, 0)),
            (4_102_444_800, (2100, 1, 1, 0, 0, 0)),
        ] {
            assert_eq!(civil_from_unix(seconds), expected, "for {seconds}");
        }
    }

    #[test]
    fn the_journal_is_bounded_and_newest_first() {
        let state = open_scratch_state("journal-bound");
        for index in 0..(ACTION_JOURNAL_CAPACITY + 5) {
            state.record("test", format!("run {index}"), true);
        }
        let journal = state.journal();
        assert_eq!(journal.len(), ACTION_JOURNAL_CAPACITY);
        assert_eq!(journal[0].outcome, format!("run {}", ACTION_JOURNAL_CAPACITY + 4));
    }

    #[test]
    fn observed_tenants_and_collections_round_trip_through_the_sidecar() {
        let directory = unique_scratch("registry");
        let config = config_for(&directory);
        let state = StudioState::open(config.clone()).unwrap();
        state.observe("tenant-a", Some("users"));
        state.observe("tenant-a", Some("orders"));
        state.observe("tenant-b", None);
        drop(state);

        let reopened = StudioState::open(config).unwrap();
        assert_eq!(reopened.tenants(), vec!["tenant-a", "tenant-b"]);
        assert_eq!(reopened.collections_of("tenant-a"), vec!["orders", "users"]);
        assert!(reopened.collections_of("tenant-b").is_empty());
        assert!(reopened.collections_of("unknown").is_empty());
    }

    #[test]
    fn operation_identities_are_fresh_each_call() {
        let state = open_scratch_state("operation-ids");
        let first = state.next_operation().unwrap();
        let second = state.next_operation().unwrap();
        assert_ne!(first.as_str(), second.as_str());
    }

    #[test]
    fn registry_bounds_identifiers_and_does_not_orphan_collections() {
        let mut sidecar = Sidecar::default();
        for index in 0..REGISTRY_TENANT_CAP {
            record_observation(&mut sidecar, &format!("tenant-{index}"), None);
        }
        assert_eq!(sidecar.tenants.len(), REGISTRY_TENANT_CAP);

        record_observation(&mut sidecar, "rejected-tenant", Some("orphaned"));
        assert!(!sidecar.tenants.contains("rejected-tenant"));
        assert!(!sidecar.collections.contains_key("rejected-tenant"));

        let long = "x".repeat(REGISTRY_IDENTIFIER_MAX_BYTES + 1);
        record_observation(&mut sidecar, &long, Some(&long));
        assert!(!sidecar.tenants.contains(&long));
        assert!(!sidecar.collections.contains_key(&long));

        record_observation(&mut sidecar, "tenant-0", Some(&long));
        assert!(sidecar.collections.get("tenant-0").is_none());
    }

    #[test]
    fn registry_collection_cap_is_enforced() {
        let mut sidecar = Sidecar::default();
        for index in 0..REGISTRY_COLLECTION_CAP {
            record_observation(&mut sidecar, "tenant", Some(&format!("collection-{index}")));
        }
        assert_eq!(
            sidecar.collections.get("tenant").expect("tenant collection set").len(),
            REGISTRY_COLLECTION_CAP
        );
        record_observation(&mut sidecar, "tenant", Some("collection-over-cap"));
        assert_eq!(
            sidecar.collections.get("tenant").expect("tenant collection set").len(),
            REGISTRY_COLLECTION_CAP
        );
    }

    #[test]
    fn overlong_observations_are_not_persisted() {
        let directory = unique_scratch("registry-overlong");
        let config = config_for(&directory);
        let state = StudioState::open(config.clone()).unwrap();
        let long = "x".repeat(REGISTRY_IDENTIFIER_MAX_BYTES + 1);
        state.observe(&long, Some(&long));
        drop(state);

        let reopened = StudioState::open(config).unwrap();
        assert!(reopened.tenants().is_empty());
        assert!(reopened.collections_of(&long).is_empty());
    }

    fn config_for(directory: &str) -> Config {
        Config { data_dir: std::env::temp_dir().join(directory), addr: DEFAULT_ADDR.to_owned() }
    }

    fn unique_scratch(name: &str) -> String {
        use std::sync::atomic::AtomicUsize;
        static COUNTER: AtomicUsize = AtomicUsize::new(0);
        let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
        let process = std::process::id();
        format!("terrabase-studio-{name}-{process}-{sequence}")
    }

    fn open_scratch_state(name: &str) -> StudioState {
        StudioState::open(config_for(&unique_scratch(name))).expect("scratch engine opens")
    }
}
