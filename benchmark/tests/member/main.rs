use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use terrabase::{
    CollectionName, Document, DocumentId, DocumentValue, Engine, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
    member::{
        AdmissionLimits, AdmissionScope, CatalogValidationError, DatabaseId, DatabaseSpec,
        LifecycleState, MAX_DATABASES, Member, MemberError, MemberOpenError,
    },
};

static NEXT_ROOT: AtomicU64 = AtomicU64::new(0);

const DB_ALPHA: &str = "00000000000000000000000000";
const DB_BETA: &str = "00000000000000000000000001";
const DB_BROKEN: &str = "00000000000000000000000002";
const DB_GOOD: &str = "00000000000000000000000003";
const DB_ONE: &str = "00000000000000000000000004";
const DB_TWO: &str = "00000000000000000000000005";
const DB_SAME: &str = "00000000000000000000000006";
const DB_GENERIC: &str = "00000000000000000000000007";

struct TestRoot(PathBuf);

impl TestRoot {
    fn new(label: &str) -> Self {
        let sequence = NEXT_ROOT.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir()
            .join(format!("terrabase-member-{label}-{}-{sequence}", std::process::id()));
        fs::create_dir_all(&path).expect("test root creates");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestRoot {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn spec(id: &str, location: &str) -> DatabaseSpec {
    let id = match id {
        "alpha" => DB_ALPHA,
        "beta" => DB_BETA,
        "broken" => DB_BROKEN,
        "good" => DB_GOOD,
        "one" => DB_ONE,
        "two" => DB_TWO,
        "same" => DB_SAME,
        "db" => DB_GENERIC,
        id => id,
    };
    DatabaseSpec::new(id, location)
}

fn limits(global: usize, per_database: usize) -> AdmissionLimits {
    AdmissionLimits::new(global, per_database)
}

fn database_id(id: &str) -> DatabaseId {
    let id = match id {
        "alpha" => DB_ALPHA,
        "beta" => DB_BETA,
        "broken" => DB_BROKEN,
        "good" => DB_GOOD,
        "one" => DB_ONE,
        "two" => DB_TWO,
        "same" => DB_SAME,
        "db" => DB_GENERIC,
        id => id,
    };
    DatabaseId::try_new(id).expect("valid database id")
}

fn write(engine: &Engine, tenant: &TenantId, operation: &str, text: &str) {
    let scope = engine.scope_for(tenant);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine
        .begin(&scope, snapshot, OperationId::try_new(operation).expect("operation"))
        .expect("begin");
    transaction
        .put(
            Document::new(
                DocumentId::try_new("same-document").expect("document"),
                CollectionName::try_new("same-collection").expect("collection"),
                DocumentValue::String(text.to_owned()),
            ),
            ExpectedVersion::New,
        )
        .expect("put");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));
}

#[test]
fn identical_logical_keys_are_isolated_by_database_location() {
    let root = TestRoot::new("isolation");
    let member =
        Member::open(root.path(), [spec("alpha", "alpha"), spec("beta", "beta")], limits(4, 2))
            .expect("member opens");
    let tenant = TenantId::try_new("same-tenant").expect("tenant");

    {
        let alpha = member.acquire(&database_id("alpha")).expect("alpha lease");
        write(&alpha, &tenant, "alpha-write", "alpha");
        let beta = member.acquire(&database_id("beta")).expect("beta lease");
        write(&beta, &tenant, "beta-write", "beta");
    }

    let alpha = member.acquire(&database_id("alpha")).expect("alpha read");
    let alpha_scope = alpha.scope_for(&tenant);
    let alpha_snapshot = alpha.read_snapshot(&alpha_scope).expect("alpha snapshot");
    let alpha_tx = alpha
        .begin(&alpha_scope, alpha_snapshot, OperationId::try_new("alpha-read").expect("operation"))
        .expect("alpha transaction");
    assert_eq!(
        alpha_tx
            .read_in_collection(
                &CollectionName::try_new("same-collection").expect("collection"),
                &DocumentId::try_new("same-document").expect("document"),
            )
            .expect("alpha document")
            .expect("alpha exists")
            .value(),
        &DocumentValue::String("alpha".to_owned())
    );
    drop(alpha_tx);
    drop(alpha);

    member.close(Duration::from_secs(1)).expect("member closes");
}

#[test]
fn catalog_rejects_path_attacks_and_duplicate_locations() {
    let root = TestRoot::new("paths");
    let absolute = root.path().join("absolute");
    assert!(matches!(
        Member::open(root.path(), [DatabaseSpec::new(DB_GENERIC, absolute)], limits(1, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::InvalidDatabaseLocation))
    ));
    assert!(matches!(
        Member::open(root.path(), [], limits(1, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::EmptyCatalog))
    ));
    assert!(matches!(
        Member::open(root.path(), [spec("db", "../outside")], limits(1, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::InvalidDatabaseLocation))
    ));
    assert!(matches!(
        Member::open(root.path(), [spec("one", "same"), spec("two", "same/")], limits(2, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::DuplicateDatabaseLocation))
    ));
    assert!(matches!(
        Member::open(root.path(), [spec("same", "first"), spec("same", "second")], limits(2, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::DuplicateDatabaseIdentity))
    ));
    let oversized = (0..=MAX_DATABASES)
        .map(|index| DatabaseSpec::new(format!("{index:026X}"), format!("location-{index}")))
        .collect::<Vec<_>>();
    assert!(matches!(
        Member::open(root.path(), oversized, limits(1, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::CatalogTooLarge))
    ));

    let root_file = root.path().join("not-a-root");
    fs::write(&root_file, b"file").expect("root file writes");
    assert!(matches!(
        Member::open(&root_file, [spec("db", "db")], limits(1, 1)),
        Err(MemberOpenError::Validation(CatalogValidationError::InvalidRoot))
    ));

    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(root.path(), root.path().join("alias"))
            .expect("symlink creates");
        assert!(matches!(
            Member::open(root.path(), [spec("db", "alias/child")], limits(1, 1)),
            Err(MemberOpenError::Validation(CatalogValidationError::SymlinkComponent))
        ));
    }
}

#[test]
fn validation_precedes_open_and_does_not_publish_partial_catalog() {
    let root = TestRoot::new("partial");
    fs::create_dir_all(root.path().join("broken")).expect("broken location creates");
    fs::write(root.path().join("broken/format"), b"foreign-format").expect("format writes");
    let result =
        Member::open(root.path(), [spec("good", "good"), spec("broken", "broken")], limits(2, 1));
    assert!(matches!(
        result,
        Err(MemberOpenError::EngineOpen { database, .. }) if database == database_id(DB_BROKEN)
    ));
    assert!(root.path().join("good").is_dir());
}

#[test]
fn admission_is_bounded_and_permits_are_released_on_drop() {
    let root = TestRoot::new("admission");
    let member = Member::open(root.path(), [spec("one", "one"), spec("two", "two")], limits(2, 1))
        .expect("member opens");
    let one = member.acquire(&database_id("one")).expect("one lease");
    assert!(matches!(
        member.acquire(&database_id("one")),
        Err(MemberError::Overloaded(AdmissionScope::Database))
    ));
    let two = member.acquire(&database_id("two")).expect("two lease");
    assert!(matches!(
        member.acquire(&database_id("one")),
        Err(MemberError::Overloaded(AdmissionScope::Global))
    ));
    drop(one);
    let one_again = member.acquire(&database_id("one")).expect("permit released");
    drop(one_again);
    drop(two);
    member.close(Duration::from_secs(1)).expect("member closes");
}

#[test]
fn draining_refuses_new_work_and_closes_after_existing_work_drops() {
    let root = TestRoot::new("draining");
    let member = Member::open(root.path(), [spec("db", "db")], limits(1, 1)).expect("opens");
    let lease = member.acquire(&database_id("db")).expect("lease");
    assert!(matches!(member.drain(Duration::from_millis(1)), Err(MemberError::DrainTimeout)));
    assert_eq!(member.status().lifecycle, LifecycleState::Draining);
    assert!(matches!(member.acquire(&database_id("db")), Err(MemberError::Draining)));
    drop(lease);
    member.close(Duration::from_secs(1)).expect("closes");
    assert_eq!(member.status().lifecycle, LifecycleState::Stopped);
}

#[test]
fn multi_database_close_flushes_every_engine_before_restart() {
    let root = TestRoot::new("multi-close");
    let member = Member::open(root.path(), [spec("one", "one"), spec("two", "two")], limits(2, 1))
        .expect("opens");
    member.close(Duration::from_secs(1)).expect("closes every engine");
    assert_eq!(member.status().lifecycle, LifecycleState::Stopped);

    let restarted =
        Member::open(root.path(), [spec("one", "one"), spec("two", "two")], limits(2, 1))
            .expect("reopens every engine");
    assert!(restarted.acquire(&database_id("one")).is_ok());
    assert!(restarted.acquire(&database_id("two")).is_ok());
    restarted.close(Duration::from_secs(1)).expect("closes after restart");
}

#[test]
fn durable_documents_survive_member_restart() {
    let root = TestRoot::new("restart");
    let tenant = TenantId::try_new("tenant").expect("tenant");
    {
        let member = Member::open(root.path(), [spec("db", "db")], limits(1, 1)).expect("opens");
        let lease = member.acquire(&database_id("db")).expect("lease");
        write(&lease, &tenant, "restart-write", "durable");
        drop(lease);
        member.close(Duration::from_secs(1)).expect("closes");
    }
    let member = Member::open(root.path(), [spec("db", "db")], limits(1, 1)).expect("reopens");
    let lease = member.acquire(&database_id("db")).expect("lease");
    let scope = lease.scope_for(&tenant);
    let snapshot = lease.read_snapshot(&scope).expect("snapshot");
    let tx = lease
        .begin(&scope, snapshot, OperationId::try_new("restart-read").expect("operation"))
        .expect("transaction");
    assert!(
        tx.read_in_collection(
            &CollectionName::try_new("same-collection").expect("collection"),
            &DocumentId::try_new("same-document").expect("document"),
        )
        .expect("read")
        .is_some()
    );
    drop(tx);
    drop(lease);
    member.close(Duration::from_secs(1)).expect("closes");
}
