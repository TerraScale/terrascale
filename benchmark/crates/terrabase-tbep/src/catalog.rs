//! The database catalog (S-01): lazy open of one engine host per
//! database, an RSS-sized LRU, and idle-close.
//!
//! [`HostCatalog`] maps a `tenant/project/database` scope to one durable
//! [`MemberHost`] at `root/tenant/project/database`. Hosts open lazily on
//! first use, stay resident in a least-recently-used set bounded by the
//! catalog capacity (the open-file bound that keeps RSS flat), and close
//! when the catalog needs room or when they have gone unused past the
//! idle threshold. Create, open, and close are crash-safe by delegation:
//! the engine creates its own directory, `SQLite` WAL recovery runs on
//! every reopen, and closing drains through the supervision shutdown
//! path before the catalog's last reference drops the engine.

use std::collections::HashMap;
use std::path::{Path, PathBuf, is_separator};
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use crate::host::TbepHost;
use crate::member_host::{MemberHost, MemberHostOptions};
use crate::registry::{ShutdownReason, SupervisionShutdownRequest};

/// The catalog key: one tenant, Project, and Database identity.
type ScopeKey = (String, String, String);

/// One resident host with the stamp its LRU and idle-close decisions read.
struct Entry {
    /// The lazily opened engine host.
    host: Arc<MemberHost>,
    /// When the catalog last handed this host out (or opened it).
    last_used: Instant,
}

/// A catalog failure. Every row fails closed.
#[derive(Clone, Debug, PartialEq)]
pub enum CatalogError {
    /// The tenant, Project, or Database name is not one safe path
    /// component. The detail names the offending field.
    InvalidName(String),
    /// The database's engine host could not be opened. The detail carries
    /// the hosting failure.
    OpenFailed(String),
}

impl std::fmt::Display for CatalogError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidName(field) => {
                write!(f, "the {field} name is not one safe path component")
            }
            Self::OpenFailed(detail) => {
                write!(f, "the database engine host could not open: {detail}")
            }
        }
    }
}

impl std::error::Error for CatalogError {}

/// The catalog of databases (S-01): lazy open, an RSS-sized LRU, and
/// idle-close over one engine host per database directory.
///
/// The catalog owns the resident set: at most `capacity` engine hosts
/// stay open at once. Opening past the bound closes the
/// least-recently-used host first, so the open-file (and therefore
/// memory) footprint never grows past the bound while traffic walks cold
/// databases. Hosts unused past the idle threshold close the same way on
/// the next catalog access and on an explicit [`HostCatalog::close_idle`]
/// sweep. Dropping the catalog drains every resident host.
pub struct HostCatalog {
    /// The filesystem root every database directory nests under.
    root: PathBuf,
    /// The maximum number of simultaneously resident hosts (never below 1).
    capacity: usize,
    /// How long a host may sit unused before a sweep closes it.
    idle_after: Duration,
    /// The next host instance identity, handed out monotonically from 1.
    next_instance: AtomicU64,
    /// The resident hosts and their LRU and idle bookkeeping, behind one
    /// lock.
    // ponytail: a single Mutex serializes lookups and opens; shard it if a
    // hot multi-tenant server ever shows contention here.
    entries: Mutex<HashMap<ScopeKey, Entry>>,
}

impl HostCatalog {
    /// Opens the catalog rooted at `root` with the given resident-set
    /// bound and idle threshold. A capacity below 1 is raised to 1, and
    /// the root directory is created when absent.
    ///
    /// # Errors
    ///
    /// Returns [`CatalogError::OpenFailed`] when the root directory
    /// cannot be created.
    pub fn open(root: &Path, capacity: usize, idle_after: Duration) -> Result<Self, CatalogError> {
        std::fs::create_dir_all(root).map_err(|error| {
            CatalogError::OpenFailed(format!("the catalog root cannot be created: {error}"))
        })?;
        reject_symlink(root)?;
        let root = root.canonicalize().map_err(|error| {
            CatalogError::OpenFailed(format!("the catalog root cannot be resolved: {error}"))
        })?;
        Ok(Self {
            root,
            capacity: capacity.max(1),
            idle_after,
            next_instance: AtomicU64::new(1),
            entries: Mutex::new(HashMap::new()),
        })
    }

    /// The location directory `host_for` opens for a database — without
    /// opening it. Restore targets this path (S-06: the restored file
    /// lands exactly where the catalog will find it).
    #[must_use]
    pub fn location_for(&self, tenant: &str, project: &str, database: &str) -> PathBuf {
        self.root.join(tenant).join(project).join(database)
    }

    /// Returns the resident member host for the database, lazily opening
    /// `root/tenant/project/database` on first use. The host is the TB-1a
    /// composition: one engine plus journal-derived delivery, so the
    /// public API's documents and the reactive plane share one storage
    /// truth.
    ///
    /// A cache hit refreshes the entry's recency and returns the same
    /// host. A miss first closes hosts idle past the threshold, then —
    /// while the catalog is at capacity — closes the least-recently-used
    /// host *before* the new engine opens, so the resident set never
    /// exceeds the bound.
    ///
    /// # Errors
    ///
    /// Returns [`CatalogError::InvalidName`] when any name is not one
    /// safe path component, and [`CatalogError::OpenFailed`] when the
    /// engine host cannot open the database directory.
    ///
    /// # Panics
    ///
    /// Panics when the catalog lock is poisoned by a panicked open.
    pub fn host_for(
        &self,
        tenant: &str,
        project: &str,
        database: &str,
    ) -> Result<Arc<MemberHost>, CatalogError> {
        safe_component(tenant, "tenant")?;
        safe_component(project, "project")?;
        safe_component(database, "database")?;
        let key: ScopeKey = (tenant.to_owned(), project.to_owned(), database.to_owned());
        let mut entries = self.entries.lock().expect("the catalog lock is not poisoned");
        let now = Instant::now();
        if let Some(entry) = entries.get_mut(&key) {
            entry.last_used = now;
            return Ok(Arc::clone(&entry.host));
        }
        // The access-driven idle sweep rides the same lock hold as the
        // open. A dedicated sweep thread is deliberately absent.
        // ponytail: idle-close must pause a backup child
        // before its source database closes — S-06 hooks that here.
        Self::sweep_idle(&mut entries, self.idle_after, now);
        while entries.len() >= self.capacity {
            let evicted = Self::remove_oldest(&mut entries).expect("the map is at capacity");
            Self::close(evicted);
        }
        let location = self.root.join(tenant).join(project).join(database);
        reject_symlink_components(&self.root, &[tenant, project, database])?;
        let instance = self.next_instance.fetch_add(1, Ordering::Relaxed);
        let host = Arc::new(
            MemberHost::open(
                &location,
                instance,
                tenant,
                project,
                database,
                MemberHostOptions::tb1a(),
            )
            .map_err(|error| CatalogError::OpenFailed(error.detail()))?,
        );
        entries.insert(key, Entry { host: Arc::clone(&host), last_used: now });
        Ok(host)
    }

    /// Closes every host idle for at least the threshold and returns how
    /// many closed.
    ///
    /// # Panics
    ///
    /// Panics when the catalog lock is poisoned by a panicked open.
    pub fn close_idle(&self) -> usize {
        let mut entries = self.entries.lock().expect("the catalog lock is not poisoned");
        let before = entries.len();
        Self::sweep_idle(&mut entries, self.idle_after, Instant::now());
        before - entries.len()
    }

    /// Returns how many engine hosts are currently resident.
    ///
    /// # Panics
    ///
    /// Panics when the catalog lock is poisoned by a panicked open.
    #[must_use]
    pub fn open_len(&self) -> usize {
        self.entries.lock().expect("the catalog lock is not poisoned").len()
    }

    /// Closes every entry idle past `idle_after` as of `now`.
    fn sweep_idle(entries: &mut HashMap<ScopeKey, Entry>, idle_after: Duration, now: Instant) {
        let idle: Vec<ScopeKey> = entries
            .iter()
            .filter(|(_, entry)| now.duration_since(entry.last_used) >= idle_after)
            .map(|(key, _)| key.clone())
            .collect();
        for key in idle {
            let entry = entries.remove(&key).expect("the idle key is resident");
            Self::close(entry);
        }
    }

    /// Removes the least-recently-used entry, if any.
    // ponytail: LRU by a full scan for the oldest stamp — O(n) per miss at
    // RSS-sized widths (tens of hosts); an ordered deque if n ever reaches
    // tens of thousands.
    fn remove_oldest(entries: &mut HashMap<ScopeKey, Entry>) -> Option<Entry> {
        let oldest =
            entries.iter().min_by_key(|(_, entry)| entry.last_used).map(|(key, _)| key.clone())?;
        entries.remove(&oldest)
    }

    /// Drains one resident entry through the supervision shutdown path.
    fn close(entry: Entry) {
        let _ = entry
            .host
            .begin_shutdown(&SupervisionShutdownRequest { reason: ShutdownReason::Supervisor });
        // The explicit drop is the consumption the by-value signature
        // promises: the catalog's reference releases here, and a caller
        // still holding the Arc keeps that engine open until it drops —
        // the catalog bounds its own resident set, not the references it
        // already handed out.
        drop(entry);
    }

    /// Closes one database's resident host by name (the LRU sweep's
    /// eviction, on demand). Restore uses this to swap the database file
    /// under a closed engine (S-06): the next `host_for` reopens the new
    /// bytes. `None` when no host is resident.
    ///
    /// # Panics
    ///
    /// Panics when the catalog lock is poisoned by a panicked open.
    pub fn close_database(&self, tenant: &str, project: &str, database: &str) -> bool {
        let key: ScopeKey = (tenant.to_owned(), project.to_owned(), database.to_owned());
        let mut entries = self.entries.lock().expect("the catalog lock is not poisoned");
        match entries.remove(&key) {
            Some(entry) => {
                Self::close(entry);
                true
            }
            None => false,
        }
    }
}

impl Drop for HostCatalog {
    fn drop(&mut self) {
        // A poisoned lock can only follow a panic mid-open; draining the
        // contents is still the correct shutdown, and panicking inside
        // drop would abort the process for no gain.
        let Ok(mut entries) = self.entries.lock() else {
            return;
        };
        for (_, entry) in entries.drain() {
            Self::close(entry);
        }
    }
}

/// Checks that a name is exactly one safe path component: non-empty, not
/// `.` or `..`, and free of separators and control characters.
///
/// [`BoundScope`] validation alone is not enough here: it rejects control
/// characters but still admits `.`-relative and separator-carrying names,
/// which would escape the catalog root. This check is the trust boundary
/// between authenticated scope names and the filesystem.
///
/// # Errors
///
/// Returns [`CatalogError::InvalidName`] naming `field` for any violation.
fn safe_component(name: &str, field: &'static str) -> Result<(), CatalogError> {
    let safe = !name.is_empty()
        && name != "."
        && name != ".."
        && !name.chars().any(|character| {
            character.is_control()
                || character == '/'
                || character == '\\'
                || is_separator(character)
        });
    if safe { Ok(()) } else { Err(CatalogError::InvalidName(field.into())) }
}

/// Refuses a symlink at a catalog root.  Placement is immutable once the
/// catalog opens: following a mutable root symlink could silently move a
/// database to another filesystem or residency domain between accesses.
fn reject_symlink(path: &Path) -> Result<(), CatalogError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() => {
            Err(CatalogError::OpenFailed("the catalog root must not be a symlink".into()))
        }
        Ok(_) => Ok(()),
        Err(error) => {
            Err(CatalogError::OpenFailed(format!("the catalog root cannot be inspected: {error}")))
        }
    }
}

/// Refuses symlink components below the immutable catalog root. Missing
/// components are allowed and are created by the engine's open path.
fn reject_symlink_components(root: &Path, components: &[&str]) -> Result<(), CatalogError> {
    let mut current = root.to_path_buf();
    for component in components {
        current.push(component);
        if let Ok(metadata) = std::fs::symlink_metadata(&current)
            && metadata.file_type().is_symlink()
        {
            return Err(CatalogError::OpenFailed(
                "the database placement contains a symlink".into(),
            ));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;
    use std::path::{Path, PathBuf};
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::Duration;

    use super::{CatalogError, HostCatalog};
    use crate::member_host::MemberHost;
    use terrabase::engine::CommitOutcome;
    use terrabase::types::{
        CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId, TenantId,
    };

    static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

    /// One disposable catalog root, removed on drop.
    struct TestLocation(PathBuf);

    impl TestLocation {
        fn new(label: &str) -> Self {
            let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
            Self(
                std::env::temp_dir()
                    .join(format!("terrabase-catalog-{label}-{}-{number}", std::process::id())),
            )
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestLocation {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    /// Writes one small document through the host's engine transaction
    /// path and asserts the commit.
    fn put(host: &MemberHost, database: &str, id: &str, value: i64) {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let engine_scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
        let operation =
            OperationId::try_new(&format!("put-{database}-{id}")).expect("the identity parses");
        let mut transaction =
            engine.begin(&engine_scope, snapshot, operation).expect("the write begins");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(id).expect("the document id parses"),
                    CollectionName::try_new("users").expect("the collection parses"),
                    DocumentValue::Object(BTreeMap::from([(
                        "value".to_owned(),
                        DocumentValue::Integer(value),
                    )])),
                ),
                terrabase::types::ExpectedVersion::Any,
            )
            .expect("the put stages");
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {}
            outcome => panic!("the write committed: {outcome:?}"),
        }
    }

    /// Reads one document back through the host's engine snapshot path,
    /// returning its `value` field when present.
    fn get(host: &MemberHost, _database: &str, id: &str) -> Option<i64> {
        let engine = host.engine();
        let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
        let engine_scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
        let operation = OperationId::try_new(&format!("get-{id}")).expect("the identity parses");
        let transaction =
            engine.begin(&engine_scope, snapshot, operation).expect("the read begins");
        let found = transaction
            .read(&DocumentId::try_new(id).expect("the document id parses"))
            .expect("the engine reads the document");
        found.map(|found| match found.value() {
            DocumentValue::Object(fields) => match fields.get("value") {
                Some(DocumentValue::Integer(value)) => *value,
                other => panic!("the read body is not the written body: {other:?}"),
            },
            other => panic!("the read body is not the written body: {other:?}"),
        })
    }

    #[test]
    fn lazy_open_reuses_one_host_per_scope() {
        let location = TestLocation::new("lazy");
        let catalog = HostCatalog::open(location.path(), 4, Duration::from_mins(10))
            .expect("the catalog opens");
        let first =
            catalog.host_for("tenant-a", "project-a", "database-a").expect("the first host opens");
        let again = catalog
            .host_for("tenant-a", "project-a", "database-a")
            .expect("the cached host returns");
        assert!(Arc::ptr_eq(&first, &again), "the same scope must return the same host");
        let other =
            catalog.host_for("tenant-a", "project-a", "database-b").expect("the second host opens");
        assert!(!Arc::ptr_eq(&first, &other), "another database opens another host");
        assert_eq!(catalog.open_len(), 2);
    }

    #[test]
    fn the_lru_bound_evicts_the_oldest_and_data_survives_reopen() {
        let location = TestLocation::new("lru");
        let catalog = HostCatalog::open(location.path(), 2, Duration::from_mins(10))
            .expect("the catalog opens");
        let first =
            catalog.host_for("tenant-a", "project-a", "database-a").expect("the first host opens");
        put(&first, "database-a", "doc-1", 7);
        catalog.host_for("tenant-a", "project-a", "database-b").expect("the second host opens");
        catalog.host_for("tenant-a", "project-a", "database-c").expect("the third host opens");
        assert_eq!(catalog.open_len(), 2, "the resident set stays at the capacity bound");
        let reopened = catalog
            .host_for("tenant-a", "project-a", "database-a")
            .expect("the evicted database reopens");
        assert!(!Arc::ptr_eq(&first, &reopened), "the evicted scope reopens as a new host");
        assert_eq!(
            get(&reopened, "database-a", "doc-1"),
            Some(7),
            "the written document survives eviction and reopen"
        );
    }

    #[test]
    fn unsafe_names_are_rejected_before_any_host_opens() {
        let location = TestLocation::new("names");
        let catalog = HostCatalog::open(location.path(), 4, Duration::from_mins(10))
            .expect("the catalog opens");
        for name in ["", "..", ".", "a/b", "a\\b"] {
            for (tenant, project, database) in
                [(name, "p", "db"), ("t", name, "db"), ("t", "p", name)]
            {
                let error = catalog
                    .host_for(tenant, project, database)
                    .err()
                    .expect("an unsafe name must be rejected");
                assert!(
                    matches!(error, CatalogError::InvalidName(_)),
                    "{name:?} in every position: {error:?}"
                );
            }
        }
        assert_eq!(catalog.open_len(), 0, "no unsafe name may open a host");
    }

    #[test]
    fn idle_close_drains_and_the_database_reopens() {
        let location = TestLocation::new("idle");
        let catalog =
            HostCatalog::open(location.path(), 4, Duration::ZERO).expect("the catalog opens");
        let host = catalog.host_for("tenant-a", "project-a", "database-a").expect("the host opens");
        put(&host, "database-a", "doc-1", 3);
        assert_eq!(catalog.close_idle(), 1, "the zero threshold closes the idle host");
        assert_eq!(catalog.open_len(), 0);
        let reopened = catalog
            .host_for("tenant-a", "project-a", "database-a")
            .expect("the closed database reopens");
        assert!(!Arc::ptr_eq(&host, &reopened), "the idle-closed scope reopens as a new host");
        assert_eq!(
            get(&reopened, "database-a", "doc-1"),
            Some(3),
            "the committed document survives the close"
        );
    }

    /// The S-01 gate: 1,000 databases open under an RSS-sized bound,
    /// idle-close sweeps stay inside it, and committed documents survive
    /// a full catalog drop and restart on the same root.
    ///
    /// RSS flatness is asserted indirectly: the resident-set bound is the
    /// open-file bound, so `open_len() <= capacity` throughout is the
    /// measurable stand-in for a flat memory profile — every closed host
    /// drops its `SQLite` pool with it.
    ///
    /// Runtime: one engine open dominates each database (~64 ms measured
    /// in the debug profile; the committed put adds only a few
    /// milliseconds), so the 1,000 mandatory opens put the debug-build
    /// floor near 65 s — the per-database work is already the minimum
    /// this gate permits.
    #[test]
    fn thousand_databases_open_idle_close_and_restart() {
        let location = TestLocation::new("gate");
        let root = location.path().to_path_buf();
        {
            // One engine open plus one committed document per database;
            // the small idle threshold lets the periodic close_idle sweep
            // retire the backlog the LRU would otherwise evict.
            let catalog =
                HostCatalog::open(&root, 32, Duration::from_millis(25)).expect("the catalog opens");
            for index in 0..1000_u32 {
                let database = format!("db-{index:04}");
                let host = catalog
                    .host_for("tenant-a", "project-a", &database)
                    .expect("the database opens");
                put(&host, &database, "doc-1", i64::from(index));
                if index % 128 == 127 {
                    let _ = catalog.close_idle();
                }
                assert!(
                    catalog.open_len() <= 32,
                    "the resident set stays bounded at iteration {index}"
                );
            }
        }
        // Restart: a fresh catalog over the same root reopens spot-checked
        // databases across the whole range and reads committed data back —
        // SQLite WAL recovery runs inside each reopen.
        let catalog = HostCatalog::open(&root, 32, Duration::from_mins(10))
            .expect("the restarted catalog opens");
        for index in (0..1000_u32).step_by(50) {
            let database = format!("db-{index:04}");
            let host =
                catalog.host_for("tenant-a", "project-a", &database).expect("the database reopens");
            assert_eq!(
                get(&host, &database, "doc-1"),
                Some(i64::from(index)),
                "database {database} kept its document across the restart"
            );
        }
    }
}
