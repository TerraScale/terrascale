//! Shared, bounded durable-journal replay for one member host and tenant.

use std::collections::{BTreeMap, VecDeque};
use std::sync::{Arc, Mutex, Weak};

use terrabase::Engine;
use terrabase::streaming::{ChangeCheckpoint, StreamError, SyncJournalRecord};
use terrabase::types::{Cancellation, TenantId, TenantScope};
use terrabase_tbep::member_host::MemberHost;
use tokio::sync::Notify;

use super::ApiError;

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
            _watch: watch,
        });
        sources.insert(key, Arc::downgrade(&source));
        Ok(source)
    }
}

pub(super) struct JournalSource {
    host: Arc<MemberHost>,
    tenant: TenantId,
    pub(super) changed: Arc<Notify>,
    replay: Mutex<Option<ReplayBuffer>>,
    _watch: terrabase::engine::PublicationWatch,
}

impl JournalSource {
    #[cfg(test)]
    pub(super) fn stats(&self) -> (usize, usize) {
        (
            std::ptr::from_ref(self) as usize,
            self.replay.lock().unwrap().as_ref().map_or(0, |replay| replay.reads),
        )
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
        let mut replay = self.replay.lock().map_err(|_| ApiError::storage_failure())?;
        let engine = self.host.engine();
        replay.get_or_insert_with(|| ReplayBuffer::new(after)).records(
            engine,
            &engine.scope_for(&self.tenant),
            after,
            through,
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
    pub(super) fn records(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        after: u64,
        through: u64,
    ) -> Result<Option<Vec<Arc<SyncJournalRecord>>>, ApiError> {
        if through > self.through {
            self.extend(engine, scope, through)?;
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
            let page = match engine.streams().replay_sync_journal(
                scope,
                cursor,
                PAGE_SIZE,
                &Cancellation::new(),
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
