//! Shared, bounded durable-journal replay for one member host and tenant.

use std::collections::{BTreeMap, VecDeque};
use std::sync::{Arc, Mutex, Weak};

use terrabase::Engine;
use terrabase::streaming::{ChangeCheckpoint, StreamError, SyncJournalRecord};
use terrabase::types::{Cancellation, TenantId, TenantScope};
use terrabase_tbep::member_host::MemberHost;
use tokio::sync::Notify;

use super::ApiError;

mod dispatch;

const MAX_RECORDS: usize = 4096;
const MAX_BYTES: usize = 8 * 1024 * 1024;
const PAGE_SIZE: u32 = 64;
const MAX_PAGES: usize = MAX_RECORDS / PAGE_SIZE as usize;

#[derive(Default)]
pub(super) struct Sources(Mutex<BTreeMap<(usize, TenantId), Weak<JournalSource>>>);

impl Sources {
    pub(super) fn get(
        &self,
        host: &Arc<MemberHost>,
        tenant: &TenantId,
    ) -> Result<Arc<JournalSource>, ApiError> {
        // A live source owns the host, so its address cannot be reused while
        // the key is live. Reopened database incarnations get another source.
        let key = (Arc::as_ptr(host) as usize, tenant.clone());
        let mut sources = self.0.lock().map_err(|_| ApiError::storage_failure())?;
        sources.retain(|_, source| source.strong_count() > 0);
        if let Some(source) = sources.get(&key).and_then(Weak::upgrade) {
            return Ok(source);
        }
        let changed = Arc::new(Notify::new());
        let signal = Arc::clone(&changed);
        let watch = host
            .engine()
            .watch_publications(move || signal.notify_waiters())
            .map_err(|_| ApiError::storage_failure())?;
        let source = Arc::new(JournalSource {
            host: Arc::clone(host),
            tenant: tenant.clone(),
            changed,
            replay: Mutex::new(None),
            dependencies: Mutex::new(super::dependencies::Dependencies::default()),
            dispatch: Mutex::new(dispatch::State::default()),
            _watch: watch,
        });
        sources.insert(key, Arc::downgrade(&source));
        Ok(source)
    }
}

pub(super) struct JournalSource {
    dispatch: Mutex<dispatch::State>,
    dependencies: Mutex<super::dependencies::Dependencies>,
    host: Arc<MemberHost>,
    tenant: TenantId,
    changed: Arc<Notify>,
    replay: Mutex<Option<ReplayBuffer>>,
    _watch: terrabase::engine::PublicationWatch,
}

impl JournalSource {
    pub(super) fn register(
        self: &Arc<Self>,
        plan: &terrabase::query::QueryPlan,
    ) -> Result<Arc<Registration>, ApiError> {
        let live = plan.live_plan().map_err(|_| ApiError::storage_failure())?;
        let id = self.dependencies.lock().map_err(|_| ApiError::storage_failure())?.register(&live);
        Ok(Arc::new(Registration { source: Arc::clone(self), id }))
    }

    pub(super) fn records_for(
        &self,
        id: u64,
        after: u64,
        through: u64,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        let Some(mut records) = self.records(after, through)? else { return Ok(None) };
        let mut dependencies = self.dependencies.lock().map_err(|_| ApiError::storage_failure())?;
        records.retain(|record| dependencies.affects(id, record));
        Ok(Some(records))
    }
    #[cfg(test)]
    pub(super) fn stats(&self) -> (usize, usize) {
        (
            std::ptr::from_ref(self) as usize,
            self.replay.lock().unwrap().as_ref().map_or(0, |replay| replay.reads),
        )
    }

    #[cfg(test)]
    pub(super) fn dispatch_stats(&self) -> (usize, usize, usize, bool) {
        self.dispatch.lock().unwrap().stats()
    }

    pub(super) fn start_at(&self, revision: u64) -> Result<(), ApiError> {
        self.replay
            .lock()
            .map_err(|_| ApiError::storage_failure())?
            .get_or_insert_with(|| ReplayBuffer::new(revision));
        Ok(())
    }

    pub(super) fn records(
        &self,
        after: u64,
        through: u64,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        self.records_with_cancellation(after, through, &Cancellation::new())
    }

    fn records_with_cancellation(
        &self,
        after: u64,
        through: u64,
        cancellation: &Cancellation,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        let mut replay = self.replay.lock().map_err(|_| ApiError::storage_failure())?;
        let engine = self.host.engine();
        replay.get_or_insert_with(|| ReplayBuffer::new(after)).records_with_cancellation(
            engine,
            &engine.scope_for(&self.tenant),
            after,
            through,
            cancellation,
        )
    }
}

pub(super) struct ReplayBuffer {
    floor: u64,
    through: u64,
    records: VecDeque<(Arc<SyncJournalRecord>, usize)>,
    bytes: usize,
    #[cfg(test)]
    pub(super) reads: usize,
}

impl ReplayBuffer {
    pub(super) fn new(revision: u64) -> Self {
        Self {
            floor: revision,
            through: revision,
            records: VecDeque::new(),
            bytes: 0,
            #[cfg(test)]
            reads: 0,
        }
    }

    // None means this view must rebuild at its supplied immutable snapshot.
    // The cache is never an authority for source history or result revisions.
    #[cfg(test)]
    pub(super) fn records(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        after: u64,
        through: u64,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        self.records_with_cancellation(engine, scope, after, through, &Cancellation::new())
    }

    fn records_with_cancellation(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        after: u64,
        through: u64,
        cancellation: &Cancellation,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        if cancellation.is_cancelled() {
            return Err(ApiError::storage_failure());
        }
        if through > self.through {
            self.extend(engine, scope, through, cancellation)?;
        }
        if after < self.floor {
            return Ok(None);
        }
        Ok(Some(
            self.records
                .iter()
                .filter(|(record, _)| {
                    record.storage_sequence() > after && record.storage_sequence() <= through
                })
                .map(|(record, _)| Arc::clone(record))
                .collect(),
        ))
    }

    fn extend(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        through: u64,
        cancellation: &Cancellation,
    ) -> Result<(), ApiError> {
        let mut cursor = Some(ChangeCheckpoint::after(self.through, 0));
        // Read into a separate bounded candidate, so a storage error cannot
        // leave partially appended records that a retry would apply twice.
        let mut pending = VecDeque::new();
        let mut bytes = 0_usize;
        let mut floor = self.floor;
        for _ in 0..MAX_PAGES {
            #[cfg(test)]
            {
                self.reads += 1;
            }
            let page = match engine.streams().replay_view_journal(
                scope,
                cursor,
                PAGE_SIZE,
                cancellation,
            ) {
                Ok(page) => page,
                Err(StreamError::HistoryExpired) => {
                    self.reset(through);
                    return Ok(());
                }
                Err(_) => return Err(ApiError::storage_failure()),
            };
            let mut reached_cut = page.drained();
            for record in page.records() {
                if record.storage_sequence() > through {
                    reached_cut = true;
                    break;
                }
                let size = record
                    .published_document()
                    .map_or_else(
                        || record.document().as_str().len() + record.collection().as_str().len(),
                        |document| {
                            terrabase::query::LiveQueryPlan::estimated_document_bytes(&document)
                        },
                    )
                    .saturating_add(std::mem::size_of::<SyncJournalRecord>())
                    .saturating_add(record.operation_id().as_str().len())
                    .saturating_add(record.provenance().origin().len())
                    .saturating_add(record.provenance().context().len());
                let size = size.saturating_add(match record.before_image() {
                    terrabase::streaming::JournalBeforeImage::Document(document) => {
                        terrabase::query::LiveQueryPlan::estimated_document_bytes(document)
                    }
                    _ => 0,
                });
                if size > MAX_BYTES {
                    pending.clear();
                    bytes = 0;
                    floor = record.storage_sequence();
                    continue;
                }
                bytes = bytes.saturating_add(size);
                pending.push_back((Arc::new(record.clone()), size));
                while bytes > MAX_BYTES || pending.len() > MAX_RECORDS {
                    if let Some((record, size)) = pending.pop_front() {
                        bytes -= size;
                        floor = floor.max(record.storage_sequence());
                    }
                }
            }
            if reached_cut {
                self.records.extend(pending);
                self.bytes = self.bytes.saturating_add(bytes);
                self.floor = floor;
                self.through = through;
                while self.bytes > MAX_BYTES
                    || self.records.len() > MAX_RECORDS
                    || self
                        .records
                        .front()
                        .is_some_and(|(record, _)| record.storage_sequence() <= self.floor)
                {
                    let Some((record, size)) = self.records.pop_front() else { break };
                    self.bytes -= size;
                    self.floor = self.floor.max(record.storage_sequence());
                }
                return Ok(());
            }
            cursor = page.next_checkpoint();
        }
        // Excessive backlog is a bounded snapshot rebuild, never a skipped
        // prefix presented as a complete delta batch.
        self.reset(through);
        Ok(())
    }

    fn reset(&mut self, through: u64) {
        self.records.clear();
        self.bytes = 0;
        self.floor = through;
        self.through = through;
    }
}

pub(super) struct Registration {
    source: Arc<JournalSource>,
    id: u64,
}
impl Registration {
    pub(super) fn id(&self) -> u64 {
        self.id
    }
}
impl Drop for Registration {
    fn drop(&mut self) {
        if let Ok(mut dependencies) = self.source.dependencies.lock() {
            dependencies.remove(self.id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId,
    };
    use terrabase_tbep::member_host::MemberHostOptions;

    fn host(name: &str) -> Arc<MemberHost> {
        let directory = std::env::temp_dir()
            .join(format!("terrabase-shared-source-{name}-{}", std::process::id()));
        Arc::new(
            MemberHost::open(
                &directory,
                1,
                "tenant",
                "project",
                "database",
                MemberHostOptions::tb1a(),
            )
            .unwrap(),
        )
    }

    fn put(
        host: &MemberHost,
        tenant: &TenantId,
        start: usize,
        count: usize,
        payload: usize,
    ) -> u64 {
        let engine = host.engine();
        let scope = engine.scope_for(tenant);
        let snapshot = engine.read_snapshot(&scope).unwrap();
        let operation = OperationId::try_new(&format!(
            "{}-{start}-{}",
            tenant.as_str(),
            snapshot.sequence().get()
        ))
        .unwrap();
        let mut transaction = engine.begin(&scope, snapshot, operation).unwrap();
        for index in start..start + count {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(&format!("row-{index}")).unwrap(),
                        CollectionName::try_new("orders").unwrap(),
                        DocumentValue::Object(
                            [("payload".into(), DocumentValue::String("x".repeat(payload)))].into(),
                        ),
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
        }
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
        engine.read_snapshot(&scope).unwrap().sequence().get()
    }

    fn replace(
        host: &MemberHost,
        tenant: &TenantId,
        id: &str,
        value: Option<DocumentValue>,
    ) -> u64 {
        let engine = host.engine();
        let scope = engine.scope_for(tenant);
        let snapshot = engine.read_snapshot(&scope).unwrap();
        let operation =
            OperationId::try_new(&format!("replace-{id}-{}", snapshot.sequence().get())).unwrap();
        let mut transaction = engine.begin(&scope, snapshot, operation).unwrap();
        let collection = CollectionName::try_new("orders").unwrap();
        let id = DocumentId::try_new(id).unwrap();
        if let Some(value) = value {
            transaction
                .put(
                    Document::new(
                        id,
                        collection,
                        DocumentValue::Object([("payload".into(), value)].into()),
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
        } else {
            transaction.delete_in_collection(&collection, id, ExpectedVersion::Any);
        }
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
        engine.read_snapshot(&scope).unwrap().sequence().get()
    }

    fn register(source: &Arc<JournalSource>, condition: &str) -> Arc<Registration> {
        use terrabase::operations::{QueryInput, SqlQueryText};
        let plan = source
            .host
            .engine()
            .queries()
            .compile(QueryInput::sql(
                SqlQueryText::try_new(&format!(
                    "SELECT payload FROM orders WHERE {condition} LIMIT 10"
                ))
                .unwrap(),
            ))
            .unwrap();
        source.register(&plan).unwrap()
    }

    #[tokio::test]
    async fn dispatcher_targets_waiters_stops_when_idle_and_restarts_from_the_listener_cut() {
        use std::time::Duration;
        let host = host("targeted-dispatch");
        let tenant = TenantId::try_new("tenant").unwrap();
        let source = Sources::default().get(&host, &tenant).unwrap();
        let initial = host
            .engine()
            .read_snapshot(&host.engine().scope_for(&tenant))
            .unwrap()
            .sequence()
            .get();
        source.start_at(initial).unwrap();
        let x = register(&source, "payload = 'x'");
        let y = register(&source, "payload = 'y'");
        let first = x.listen(initial).unwrap();
        let duplicate = x.listen(initial).unwrap();
        let unrelated = y.listen(initial).unwrap();
        assert_eq!(source.dispatch_stats(), (2, 3, 1, true));
        let signal = Arc::clone(first.changed());
        let other_signal = Arc::clone(unrelated.changed());
        let wake = signal.notified();
        let other = other_signal.notified();
        tokio::pin!(wake, other);
        wake.as_mut().enable();
        other.as_mut().enable();
        replace(&host, &tenant, "one", Some(DocumentValue::String("x".into())));
        tokio::time::timeout(Duration::from_secs(2), wake).await.unwrap();
        assert!(tokio::time::timeout(Duration::from_millis(200), other).await.is_err());
        assert_eq!(source.stats().1, 1, "both views share one source pass");
        let wake = signal.notified();
        tokio::pin!(wake);
        wake.as_mut().enable();
        let noise = replace(&host, &tenant, "noise", Some(DocumentValue::String("z".into())));
        assert!(
            tokio::time::timeout(Duration::from_millis(250), wake).await.is_err(),
            "an irrelevant commit must not wake the matching view again"
        );
        drop(first);
        drop(duplicate);
        drop(unrelated);
        assert_eq!(source.dispatch_stats(), (0, 0, 1, false));
        tokio::time::sleep(Duration::from_millis(50)).await;
        let reads = source.stats().1;
        replace(&host, &tenant, "idle", Some(DocumentValue::String("y".into())));
        tokio::time::sleep(Duration::from_millis(200)).await;
        assert_eq!(source.stats().1, reads, "no journal worker runs without listeners");
        let resumed = y.listen(noise).unwrap();
        let wake = resumed.changed().notified();
        tokio::pin!(wake);
        wake.as_mut().enable();
        tokio::time::timeout(Duration::from_secs(2), wake).await.unwrap();
        assert_eq!(source.dispatch_stats(), (1, 1, 2, true));
    }

    #[test]
    fn interval_dispatch_preserves_leaving_rows_deletes_and_large_integer_bounds() {
        use terrabase::streaming::JournalBeforeImage;
        let host = host("intervals");
        let tenant = TenantId::try_new("tenant").unwrap();
        let sources = Sources::default();
        let source = sources.get(&host, &tenant).unwrap();
        let initial = replace(&host, &tenant, "one", Some(DocumentValue::String("old".into())));
        source.start_at(initial).unwrap();
        let old = register(&source, "payload = 'old'");
        let new = register(&source, "payload = 'new'");
        let unrelated = register(&source, "payload = 'unrelated'");
        let updated = replace(&host, &tenant, "one", Some(DocumentValue::String("new".into())));
        let rows = source.records_for(old.id(), initial, updated).unwrap().unwrap();
        assert_eq!(rows.len(), 1, "leaving a predicate still reaches its old view");
        assert!(matches!(rows[0].before_image(), JournalBeforeImage::Document(_)));
        assert_eq!(source.records_for(new.id(), initial, updated).unwrap().unwrap().len(), 1);
        assert!(source.records_for(unrelated.id(), initial, updated).unwrap().unwrap().is_empty());
        let late = register(&source, "payload = 'old'");
        assert_eq!(
            source.records_for(late.id(), initial, updated).unwrap().unwrap().len(),
            1,
            "new registrations rematch already retained records"
        );
        let deleted = replace(&host, &tenant, "one", None);
        assert_eq!(source.records_for(new.id(), updated, deleted).unwrap().unwrap().len(), 1);
        assert!(source.records_for(old.id(), updated, deleted).unwrap().unwrap().is_empty());
        let numeric = register(&source, "payload > 9007199254740992");
        let cut =
            replace(&host, &tenant, "one", Some(DocumentValue::Integer(9_007_199_254_740_993)));
        assert_eq!(source.records_for(numeric.id(), deleted, cut).unwrap().unwrap().len(), 1);
        assert!(matches!(
            source.records(deleted, cut).unwrap().unwrap()[0].before_image(),
            JournalBeforeImage::Absent
        ));
        let scope = host.engine().scope_for(&tenant);
        let ordinary = host
            .engine()
            .streams()
            .replay_sync_journal(
                &scope,
                Some(ChangeCheckpoint::after(initial, 0)),
                16,
                &Cancellation::new(),
            )
            .unwrap();
        assert!(
            ordinary
                .records()
                .iter()
                .all(|record| matches!(record.before_image(), JournalBeforeImage::Unknown))
        );
    }

    #[test]
    fn before_images_do_not_confuse_expiry_or_pruning_with_absence() {
        use terrabase::streaming::JournalBeforeImage;
        let host = host("before-images");
        let tenant = TenantId::try_new("tenant").unwrap();
        let initial = replace(&host, &tenant, "expired", Some(DocumentValue::String("old".into())));
        let connection =
            rusqlite::Connection::open(host.location().join("terrabase.sqlite3")).unwrap();
        connection
            .execute(
                "UPDATE documents SET expires_at = 0 WHERE tenant = ?1 AND id = 'expired'",
                [tenant.as_str()],
            )
            .unwrap();
        replace(&host, &tenant, "expired", Some(DocumentValue::String("new".into())));
        let scope = host.engine().scope_for(&tenant);
        let page = host
            .engine()
            .streams()
            .replay_view_journal(
                &scope,
                Some(ChangeCheckpoint::after(initial, 0)),
                16,
                &Cancellation::new(),
            )
            .unwrap();
        assert_eq!(page.records()[0].published_document().unwrap().metadata().version(), 1);
        assert!(
            matches!(page.records()[0].before_image(), JournalBeforeImage::Document(_)),
            "TTL version reset must not hide the predecessor from dependency routing"
        );
        connection
            .execute(
                "INSERT INTO journal_pruning (tenant, pruned_through) VALUES (?1, ?2)",
                rusqlite::params![tenant.as_str(), i64::try_from(initial).unwrap()],
            )
            .unwrap();
        let source = Sources::default().get(&host, &tenant).unwrap();
        source.start_at(initial).unwrap();
        let unrelated = register(&source, "payload = 'unrelated'");
        let current = host.engine().read_snapshot(&scope).unwrap().sequence().get();
        let rows = source.records_for(unrelated.id(), initial, current).unwrap().unwrap();
        assert_eq!(rows.len(), 1, "unknown predecessors conservatively include the collection");
        assert!(matches!(rows[0].before_image(), JournalBeforeImage::Unknown));
    }

    #[test]
    fn concurrent_views_share_pages_and_preserve_independent_snapshot_cuts() {
        let host = host("shared");
        let tenant = TenantId::try_new("tenant").unwrap();
        let sources = Sources::default();
        let source = sources.get(&host, &tenant).unwrap();
        let initial = host
            .engine()
            .read_snapshot(&host.engine().scope_for(&tenant))
            .unwrap()
            .sequence()
            .get();
        source.start_at(initial).unwrap();
        let cut = put(&host, &tenant, 0, 130, 8);
        let batches = std::thread::scope(|threads| {
            let tasks = (0..16)
                .map(|_| {
                    let attached = sources.get(&host, &tenant).unwrap();
                    assert!(Arc::ptr_eq(&source, &attached));
                    threads.spawn(move || attached.records(initial, cut).unwrap().unwrap())
                })
                .collect::<Vec<_>>();
            tasks.into_iter().map(|task| task.join().unwrap()).collect::<Vec<_>>()
        });
        assert_eq!(batches[0].len(), 130, "a commit split across pages is complete");
        for batch in &batches {
            assert!(Arc::ptr_eq(&batches[0][0], &batch[0]), "views reuse the same frozen record");
        }
        let reads = source.replay.lock().unwrap().as_ref().unwrap().reads;
        assert_eq!(reads, 3, "one 130-row journal pass, independent of 16 readers");
        let latest = put(&host, &tenant, 130, 1, 8);
        assert_eq!(source.records(cut, latest).unwrap().unwrap().len(), 1);
        assert_eq!(
            source.records(initial, cut).unwrap().unwrap().len(),
            130,
            "an older snapshot cannot see a newer write"
        );
        assert_eq!(source.records(initial, latest).unwrap().unwrap().len(), 131);
        assert_eq!(source.replay.lock().unwrap().as_ref().unwrap().reads, reads + 1);

        let foreign = TenantId::try_new("foreign").unwrap();
        let isolated = sources.get(&host, &foreign).unwrap();
        assert!(!Arc::ptr_eq(&source, &isolated));
        isolated.start_at(initial).unwrap();
        assert!(isolated.records(initial, latest).unwrap().unwrap().is_empty());
    }

    #[test]
    fn byte_eviction_and_excessive_backlog_require_an_exact_snapshot_rebuild() {
        let host = host("bounded");
        let tenant = TenantId::try_new("tenant").unwrap();
        let sources = Sources::default();
        let source = sources.get(&host, &tenant).unwrap();
        let initial = host
            .engine()
            .read_snapshot(&host.engine().scope_for(&tenant))
            .unwrap()
            .sequence()
            .get();
        source.start_at(initial).unwrap();
        let mut cut = initial;
        for batch in 0..12 {
            cut = put(&host, &tenant, batch * 64, 64, 16 * 1024);
        }
        assert!(source.records(initial, cut).unwrap().is_none());
        {
            let replay = source.replay.lock().unwrap();
            let replay = replay.as_ref().unwrap();
            assert!(replay.bytes <= MAX_BYTES);
            assert!(replay.records.len() <= MAX_RECORDS);
        }
        let next = put(&host, &tenant, 768, 1, 8);
        assert_eq!(source.records(cut, next).unwrap().unwrap().len(), 1);
        let lagging = next;
        for batch in 0..33 {
            cut = put(&host, &tenant, 1000 + batch * 128, 128, 1);
        }
        assert!(
            source.records(lagging, cut).unwrap().is_none(),
            "a partial backlog is never a delta"
        );
        assert_eq!(source.replay.lock().unwrap().as_ref().unwrap().through, cut);
        let next = put(&host, &tenant, 9999, 1, 8);
        assert_eq!(source.records(cut, next).unwrap().unwrap().len(), 1);
    }
}
