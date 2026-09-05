//! Native SQL candidate windows maintained from durable publication records.

use std::{cmp::Ordering, sync::Arc};

use terrabase::streaming::SyncJournalRecord;
use terrabase::{
    Engine,
    query::{LiveQueryPlan, QueryPlan, QueryRow},
    types::{Cancellation, Document, DocumentValue, ReadSnapshot},
};
#[cfg(test)]
use terrabase::{
    streaming::{ChangeCheckpoint, StreamError},
    types::TenantScope,
};
use terrabase_sync_core::{
    view::{
        MAX_TOP_K_ITEMS,
        plan::{MaterializeError, Materializer, MaterializerPlan, ZSetDelta, ZSetEntry},
    },
    wire::{ProjectedField, ResultItem},
};

use crate::api::ApiError;

#[derive(Clone, Debug)]
struct SqlRow {
    document: Arc<Document>,
    item: Option<ResultItem>,
}

#[derive(Clone, Debug)]
struct SqlKey {
    plan: Arc<LiveQueryPlan>,
    document: Arc<Document>,
}

impl PartialEq for SqlKey {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}
impl Eq for SqlKey {}
impl PartialOrd for SqlKey {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}
impl Ord for SqlKey {
    fn cmp(&self, other: &Self) -> Ordering {
        self.plan.compare(&self.document, &other.document)
    }
}

#[derive(Clone, Debug)]
struct SqlPlan {
    live: Arc<LiveQueryPlan>,
    limit: usize,
}

impl MaterializerPlan for SqlPlan {
    type Row = SqlRow;
    type Key = SqlKey;
    fn limit(&self) -> usize {
        self.limit
    }
    fn owns(&self, row: &SqlRow) -> bool {
        row.document.collection() == self.live.collection()
    }
    fn identity<'a>(&self, row: &'a SqlRow) -> &'a str {
        row.document.id().as_str()
    }
    fn key_identity<'a>(&self, key: &'a SqlKey) -> &'a str {
        key.document.id().as_str()
    }
    fn matches(&self, row: &SqlRow) -> bool {
        row.item.is_some()
    }
    fn key(&self, row: &SqlRow) -> SqlKey {
        SqlKey { plan: Arc::clone(&self.live), document: Arc::clone(&row.document) }
    }
    fn evaluate(&self, row: &SqlRow) -> Option<ResultItem> {
        row.item.clone()
    }
    fn row_bytes(&self, row: &SqlRow) -> usize {
        let projected = row.item.as_ref().map_or(0, |item| {
            item.fields.iter().fold(item.identity.len(), |sum, field| {
                sum.saturating_add(field.path.len()).saturating_add(field.value.len())
            })
        });
        LiveQueryPlan::estimated_document_bytes(&row.document).saturating_add(projected)
    }
}

#[derive(Clone, Debug)]
pub(super) struct SqlMaterializer {
    plan: SqlPlan,
    candidates: Materializer<SqlPlan>,
    offset: usize,
    limit: usize,
}

impl SqlMaterializer {
    pub(super) fn for_plan(plan: &QueryPlan) -> Result<Option<Self>, ApiError> {
        let live = plan
            .live_plan()
            .map_err(|_| super::sql_error("the plan cannot maintain individual rows"))?;
        let Some(limit) = live.paging().limit() else { return Ok(None) };
        let limit = usize::try_from(limit).map_err(|_| too_large())?;
        let offset = usize::try_from(live.paging().offset()).map_err(|_| too_large())?;
        let Some(total) = offset.checked_add(limit) else { return Ok(None) };
        if limit == 0 || total > MAX_TOP_K_ITEMS {
            return Ok(None);
        }
        let plan = SqlPlan { live: Arc::new(live), limit: total };
        Ok(Some(Self { candidates: Materializer::new(plan.clone()), plan, offset, limit }))
    }

    pub(super) fn seed(
        &mut self,
        engine: &Engine,
        snapshot: &ReadSnapshot,
    ) -> Result<(), ApiError> {
        let size = self.plan.limit.saturating_mul(2).min(MAX_TOP_K_ITEMS) + 1;
        let plan = self
            .plan
            .live
            .window_plan(u32::try_from(size).map_err(|_| too_large())?)
            .map_err(ApiError::query)?;
        let window = engine.queries().execute(&plan, snapshot).map_err(ApiError::query)?;
        self.candidates
            .seed(window.rows().iter().map(|row| SqlRow {
                document: Arc::new(row.document().clone()),
                item: Some(item(row)),
            }))
            .map_err(|_| too_large())
    }

    pub(super) fn items(&self) -> Vec<ResultItem> {
        self.candidates.result().iter().skip(self.offset).take(self.limit).cloned().collect()
    }

    #[cfg(test)]
    fn refresh(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        after: u64,
    ) -> Result<Vec<ResultItem>, ApiError> {
        let records = super::source::ReplayBuffer::new(after).records(
            engine,
            scope,
            after,
            snapshot.sequence().get(),
        )?;
        self.refresh_records(engine, snapshot, records.as_deref())
    }

    pub(super) fn refresh_records(
        &mut self,
        engine: &Engine,
        snapshot: &ReadSnapshot,
        records: Option<&[Arc<SyncJournalRecord>]>,
    ) -> Result<Vec<ResultItem>, ApiError> {
        self.plan.live = Arc::new(
            self.plan
                .live
                .as_ref()
                .clone()
                .with_execution_bounds(super::view_refresh_deadline()?, Cancellation::new()),
        );
        let Some(records) = records else {
            self.seed(engine, snapshot)?;
            return Ok(self.items());
        };
        // Keep evaluation and temporary projected rows bounded even when a
        // shared source retained multiple journal pages for this view.
        for records in records.chunks(256) {
            let mut entries = Vec::new();
            for record in records {
                if record.collection() != self.plan.live.collection() {
                    continue;
                }
                let (document, weight) = record.published_document().map_or_else(
                    || {
                        (
                            Document::new(
                                record.document().clone(),
                                record.collection().clone(),
                                DocumentValue::Null,
                            ),
                            -1,
                        )
                    },
                    |document| (document, 1),
                );
                let projected = if weight > 0 {
                    self.plan.live.evaluate(&document).map_err(ApiError::query)?.as_ref().map(item)
                } else {
                    None
                };
                entries.push(ZSetEntry {
                    row: SqlRow { document: Arc::new(document), item: projected },
                    weight,
                });
            }
            if !entries.is_empty() {
                match self.candidates.apply(&ZSetDelta::new(entries)) {
                    Ok(_) => {}
                    Err(MaterializeError::RefillRequired) => {
                        self.seed(engine, snapshot)?;
                        return Ok(self.items());
                    }
                    Err(MaterializeError::ByteBudgetExceeded) => return Err(too_large()),
                }
            }
        }
        Ok(self.items())
    }
}

pub(super) fn item(row: &QueryRow) -> ResultItem {
    ResultItem {
        identity: row.document().id().as_str().to_owned(),
        fields: row
            .projected()
            .iter()
            .map(|(path, value)| ProjectedField {
                path: path.clone(),
                value: terrabase_tbep::reactive::canonical_json_bytes(value),
            })
            .collect(),
    }
}

fn too_large() -> ApiError {
    ApiError {
        status: axum::http::StatusCode::UNPROCESSABLE_ENTITY,
        code: "view_too_large",
        message: "the live candidate window exceeds its memory bound".into(),
        fix: "reduce the projection or result window".into(),
        placement: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::{
        EngineConfig,
        engine::CommitOutcome,
        operations::{QueryInput, SqlQueryText},
        types::{
            CollectionName, DocumentId, ExpectedVersion, LocalDurability, OperationId, TenantId,
        },
    };

    fn put(engine: &Engine, scope: &TenantScope, id: &str, rank: i64) {
        let snapshot = engine.read_snapshot(scope).unwrap();
        let mut transaction = engine
            .begin(scope, snapshot, OperationId::try_new(&format!("put-{id}-{rank}")).unwrap())
            .unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(id).unwrap(),
                    CollectionName::try_new("orders").unwrap(),
                    DocumentValue::Object([("score".into(), DocumentValue::Integer(rank))].into()),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    }

    fn expire_journal(dir: &std::path::Path, tenant: &TenantId, cut: u64) {
        let connection = rusqlite::Connection::open(dir.join("terrabase.sqlite3")).unwrap();
        connection
            .execute(
                "INSERT INTO journal_pruning (tenant, pruned_through) VALUES (?1, ?2)",
                rusqlite::params![tenant.as_str(), i64::try_from(cut).unwrap()],
            )
            .unwrap();
    }

    #[test]
    fn native_journal_updates_avoid_sql_and_refill_at_the_requested_cut() {
        let dir =
            std::env::temp_dir().join(format!("terrabase-http-native-{}", std::process::id()));
        let engine = Engine::open(EngineConfig::durable(&dir).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = engine.scope_for(&tenant);
        for id in 0..20 {
            put(&engine, &scope, &format!("row-{id:02}"), id);
        }
        let plan = engine
            .queries()
            .compile(QueryInput::sql(
                SqlQueryText::try_new(
                    "SELECT score AS rank, _rev FROM orders ORDER BY score LIMIT 2",
                )
                .unwrap(),
            ))
            .unwrap();
        let attach_cancellation = Cancellation::new();
        let attach_plan = plan.clone().with_execution_bounds(
            super::super::view_refresh_deadline().unwrap(),
            attach_cancellation.clone(),
        );
        let mut view = SqlMaterializer::for_plan(&attach_plan).unwrap().unwrap();
        let initial = engine.read_snapshot(&scope).unwrap();
        view.seed(&engine, &initial).unwrap();
        attach_cancellation.cancel();
        let queries = engine.queries().recent_index_selections(&tenant).len();
        assert!(queries > 0);
        put(&engine, &scope, "row-19", -1);
        let cut = engine.read_snapshot(&scope).unwrap();
        put(&engine, &scope, "row-18", -2);
        let items = view.refresh(&engine, &scope, &cut, initial.sequence().get()).unwrap();
        assert_eq!(
            items.iter().map(|item| item.identity.as_str()).collect::<Vec<_>>(),
            ["row-19", "row-00"]
        );
        assert!(items[0].fields.iter().any(|field| field.path == "_rev" && field.value == b"2"));
        assert_eq!(
            engine.queries().recent_index_selections(&tenant).len(),
            queries,
            "journal updates do not execute snapshot SQL"
        );
        let current = engine.read_snapshot(&scope).unwrap();
        view.refresh(&engine, &scope, &current, cut.sequence().get()).unwrap();
        assert_eq!(
            view.items().iter().map(|item| item.identity.as_str()).collect::<Vec<_>>(),
            ["row-18", "row-19"]
        );
        let mut transaction = engine
            .begin(&scope, current.clone(), OperationId::try_new("delete-boundary").unwrap())
            .unwrap();
        for id in ["row-18", "row-19", "row-00"] {
            transaction.delete_in_collection(
                &CollectionName::try_new("orders").unwrap(),
                DocumentId::try_new(id).unwrap(),
                ExpectedVersion::Any,
            );
        }
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        let final_cut = engine.read_snapshot(&scope).unwrap();
        let result = view.refresh(&engine, &scope, &final_cut, current.sequence().get()).unwrap();
        assert_eq!(
            engine.queries().recent_index_selections(&tenant).len(),
            queries + 1,
            "depleted candidates issue one storage refill"
        );
        let expected = engine.queries().execute(&plan, &final_cut).unwrap();
        assert_eq!(result, expected.rows().iter().map(item).collect::<Vec<_>>());
        assert_eq!(view.candidates.boundary_len(), 4);
        // Model retention overtaking this idle view, while keeping the
        // materialized document state available for an exact snapshot refill.
        put(&engine, &scope, "row-17", -3);
        let recovery_cut = engine.read_snapshot(&scope).unwrap();
        expire_journal(&dir, &tenant, recovery_cut.sequence().get());
        assert_eq!(
            engine
                .streams()
                .replay_sync_journal(
                    &scope,
                    Some(ChangeCheckpoint::after(final_cut.sequence().get(), 0)),
                    256,
                    &Cancellation::new(),
                )
                .unwrap_err(),
            StreamError::HistoryExpired
        );
        put(&engine, &scope, "row-16", -4);
        let recovered =
            view.refresh(&engine, &scope, &recovery_cut, final_cut.sequence().get()).unwrap();
        let expected = engine.queries().execute(&plan, &recovery_cut).unwrap();
        assert_eq!(recovered, expected.rows().iter().map(item).collect::<Vec<_>>());
        assert_eq!(recovered[0].identity, "row-17");
        let latest = engine.read_snapshot(&scope).unwrap();
        view.refresh(&engine, &scope, &latest, recovery_cut.sequence().get()).unwrap();
        assert_eq!(view.items()[0].identity, "row-16", "replay resumes after recovery");
        drop(engine);
        std::fs::remove_dir_all(dir).unwrap();
    }
}
