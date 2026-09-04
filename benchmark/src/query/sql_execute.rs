//! Bounded `SQLite` execution for SQL query plans.

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt::Write,
    sync::{
        Arc,
        atomic::{AtomicU8, AtomicU64, Ordering},
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use rusqlite::{
    Connection,
    types::{Value, ValueRef},
};

use super::{
    DiagnosticDuration, QueryError, QueryPlan, QueryResult, QueryRow, ResultEtag, StalenessReport,
};
use crate::{
    engine::Engine,
    query::sql::SqlRuntime,
    types::{Cancellation, Document, DocumentValue, ReadSnapshot},
};

/// Hard input and output bounds for one SQL execution. The input bound is
/// checked against durable SQLite rows before the statement runs; the output
/// bound applies to the result retained by the API.
pub(crate) const MAX_SQL_INPUT_DOCUMENTS: usize = 65_536;
pub(crate) const MAX_SQL_INPUT_BYTES: usize = 64 * 1024 * 1024;
pub(crate) const MAX_SQL_OUTPUT_ROWS: usize = 65_536;
pub(crate) const MAX_SQL_OUTPUT_BYTES: usize = 16 * 1024 * 1024;
/// `SQLite` invokes the callback at this approximate VM instruction interval.
pub(crate) const SQL_PROGRESS_INTERVAL: i32 = 1_000;
pub(crate) const MAX_SQL_STEPS: u64 = 5_000_000;
pub(crate) const MAX_SQL_TIME: Duration = Duration::from_secs(30);
const SQL_METADATA_COLUMNS: [&str; 4] = ["_rev", "_hlc", "_created", "_updated"];

const ABORT_NONE: u8 = 0;
const ABORT_DEADLINE: u8 = 1;
const ABORT_CANCELLED: u8 = 2;
const ABORT_BOUND: u8 = 3;

#[derive(Clone)]
struct SqlControl {
    deadline: Option<crate::types::Deadline>,
    cancellation: Option<Cancellation>,
    started: Instant,
    steps: Arc<AtomicU64>,
    abort: Arc<AtomicU8>,
}

impl SqlControl {
    fn new(runtime: &SqlRuntime) -> Self {
        Self {
            deadline: runtime.deadline,
            cancellation: runtime.cancellation.clone(),
            started: Instant::now(),
            steps: Arc::new(AtomicU64::new(0)),
            abort: Arc::new(AtomicU8::new(ABORT_NONE)),
        }
    }

    fn check(&self) -> Result<(), QueryError> {
        if self.deadline.is_some_and(|deadline| deadline.is_expired()) {
            return Err(QueryError::Deadline);
        }
        if self.cancellation.as_ref().is_some_and(Cancellation::is_cancelled) {
            return Err(QueryError::Cancelled);
        }
        if self.started.elapsed() >= MAX_SQL_TIME {
            return Err(QueryError::CandidateBoundExceeded);
        }
        Ok(())
    }

    fn progress(&self) -> bool {
        let reason = if self.deadline.is_some_and(|deadline| deadline.is_expired()) {
            ABORT_DEADLINE
        } else if self.cancellation.as_ref().is_some_and(Cancellation::is_cancelled) {
            ABORT_CANCELLED
        } else if self.started.elapsed() >= MAX_SQL_TIME
            || self.steps.fetch_add(SQL_PROGRESS_INTERVAL as u64, Ordering::Relaxed)
                >= MAX_SQL_STEPS
        {
            ABORT_BOUND
        } else {
            ABORT_NONE
        };
        if reason == ABORT_NONE {
            false
        } else {
            self.abort.store(reason, Ordering::Release);
            true
        }
    }

    fn interrupted(&self, fallback: QueryError) -> QueryError {
        match self.abort.load(Ordering::Acquire) {
            ABORT_DEADLINE => QueryError::Deadline,
            ABORT_CANCELLED => QueryError::Cancelled,
            ABORT_BOUND => QueryError::CandidateBoundExceeded,
            _ => {
                if self.deadline.is_some_and(|deadline| deadline.is_expired()) {
                    QueryError::Deadline
                } else if self.cancellation.as_ref().is_some_and(Cancellation::is_cancelled) {
                    QueryError::Cancelled
                } else {
                    fallback
                }
            }
        }
    }
}

#[allow(
    clippy::too_many_lines,
    reason = "one bounded pass loads, binds, and pages the in-memory result"
)]
pub(crate) fn execute(
    engine: &Engine,
    plan: &QueryPlan,
    runtime: &SqlRuntime,
    snapshot: &ReadSnapshot,
) -> Result<QueryResult, QueryError> {
    if snapshot.engine_identity() != engine.identity() {
        return Err(QueryError::SnapshotEngineMismatch);
    }
    // FTS5 `MATCH` rides the typed seam (D-78/D-39): the durable storage
    // backend owns the FTS5 table and its analyzer term space, while a
    // collection view used for ordinary SQL is intentionally not a virtual
    // table. The typed executor serves these conditions exactly (FTS
    // narrowing and exact re-check). Live plans only: `EXPLAIN` keeps its
    // SQLite path so it still reports the VM program instead of executing the
    // statement.
    if plan.sql_execute_only_operator().is_none()
        && plan
            .model()
            .predicate()
            .conditions()
            .iter()
            .any(|condition| matches!(condition, super::QueryCondition::Search { .. }))
    {
        return super::execute::execute(engine, &engine.index_catalog(), plan, snapshot);
    }
    let control = SqlControl::new(runtime);
    control.check()?;
    let started = Instant::now();
    let connection = open_storage_connection(engine)?;
    let progress_control = control.clone();
    connection
        .progress_handler(SQL_PROGRESS_INTERVAL, Some(move || progress_control.progress()))
        .map_err(|_| QueryError::StorageFailure)?;
    let transaction = connection.unchecked_transaction().map_err(|_| QueryError::StorageFailure)?;
    check_input_bounds(
        &transaction,
        snapshot.tenant().as_str(),
        snapshot.sequence().get(),
        &control,
    )?;
    create_collection_views(
        &transaction,
        snapshot.tenant().as_str(),
        snapshot.sequence().get(),
        &runtime.tables,
        &control,
    )?;
    control.check()?;
    let mut statement = transaction
        .prepare(&runtime.text)
        .map_err(|_| control.interrupted(QueryError::InvalidSpec))?;
    let bound = bound_values(&statement, runtime)?;
    let column_count = statement.column_count();
    let mut names = Vec::with_capacity(column_count);
    let mut seen = BTreeSet::new();
    for index in 0..column_count {
        let name = statement.column_name(index).unwrap_or("column").to_owned();
        if !seen.insert(name.clone()) {
            return Err(QueryError::InvalidSpec);
        }
        names.push(name);
    }
    let mut rows = statement
        .query(rusqlite::params_from_iter(bound.iter()))
        .map_err(|_| control.interrupted(QueryError::InvalidSpec))?;
    let mut result_rows = Vec::new();
    let mut output_bytes = 0usize;
    let mut used_ids = BTreeSet::new();
    while let Some(row) = rows.next().map_err(|_| control.interrupted(QueryError::InvalidSpec))? {
        control.check()?;
        if result_rows.len() == MAX_SQL_OUTPUT_ROWS {
            return Err(QueryError::CandidateBoundExceeded);
        }
        let mut value = BTreeMap::new();
        let mut projected = Vec::with_capacity(column_count);
        let mut id = format!("sql-row-{}", result_rows.len());
        for (index, name) in names.iter().enumerate() {
            let item = value_ref(row.get_ref(index).map_err(|_| QueryError::InvalidSpec)?);
            if name == "_id"
                && let DocumentValue::String(candidate) = &item
            {
                id.clone_from(candidate);
            }
            output_bytes = output_bytes
                .checked_add(name.len())
                .and_then(|bytes| bytes.checked_add(value_size(&item).ok()?))
                .ok_or(QueryError::CandidateBoundExceeded)?;
            if output_bytes > MAX_SQL_OUTPUT_BYTES {
                return Err(QueryError::CandidateBoundExceeded);
            }
            projected.push((name.clone(), item.clone()));
            value.insert(name.clone(), item);
        }
        if id.starts_with("sql-row-") {
            id = resolve_source_id(&projected, &used_ids).unwrap_or(id);
        }
        used_ids.insert(id.clone());
        let document = Document::new(
            crate::types::DocumentId::try_new(&id).map_err(|_| QueryError::InvalidSpec)?,
            crate::types::CollectionName::try_new("__sql_result__").expect("static collection"),
            DocumentValue::Object(value),
        );
        result_rows.push(QueryRow::new(document, projected));
    }
    control.check()?;
    drop(rows);
    drop(statement);
    transaction.rollback().map_err(|_| QueryError::StorageFailure)?;
    let matched = u64::try_from(result_rows.len()).unwrap_or(u64::MAX);
    let staleness = StalenessReport::default();
    let stats = super::statistics::QueryStatistics::new(
        matched,
        plan.model().paging(),
        matched,
        0,
        ResultEtag::of(snapshot.sequence(), &staleness),
        DiagnosticDuration::new(started.elapsed()),
    );
    Ok(QueryResult::new(
        result_rows,
        matched,
        plan.ordering().clone(),
        staleness,
        None,
        super::includes::IncludeSet::default(),
        stats,
    ))
}

fn resolve_source_id(
    projected: &[(String, DocumentValue)],
    used_ids: &BTreeSet<String>,
) -> Option<String> {
    // SQL aliases do not survive into the typed result metadata. Preserve the
    // durable identity for the common `SELECT _id AS id` shape by recognizing
    // the alias and validating it as a DocumentId.
    projected
        .iter()
        .find(|(name, value)| {
            matches!(name.as_str(), "_id" | "id")
                && matches!(value, DocumentValue::String(value) if crate::types::DocumentId::try_new(value).is_ok())
        })
        .and_then(|(_, value)| match value {
            DocumentValue::String(value) if !used_ids.contains(value) => Some(value.clone()),
            _ => None,
        })
}

fn bound_values(
    statement: &rusqlite::Statement<'_>,
    runtime: &SqlRuntime,
) -> Result<Vec<Value>, QueryError> {
    // SQLite parameter indexes are 1-based; parameter_count is the largest
    // index, so explicit ?3 leaves two legitimate NULL slots.
    let mut values = vec![Value::Null; statement.parameter_count()];
    for index in 1..=statement.parameter_count() {
        let name = statement
            .parameter_name(index)
            .map(|name| name.trim_start_matches(['$', ':', '@', '?']).to_owned())
            .unwrap_or_else(|| index.to_string());
        if let Some(value) = runtime.parameters.get(&name) {
            values[index - 1] = sqlite_value(value)?;
        }
    }
    Ok(values)
}

/// Opens the durable SQLite file for one bounded SQL pass. The transaction in
/// `execute` is rolled back after result materialization, so this adapter
/// cannot mutate storage and does not create an in-memory copy.
///
/// The current `Engine` contract intentionally keeps the physical location
/// private and exposes no reader callback. Until the engine adds the narrow
/// `Engine::storage_location() -> &Path` (or an equivalent bounded SQLite
/// reader callback), this adapter refuses closed rather than reopening an
/// unverified path. The parent integration should replace only this body with
/// that hook; all view construction, metadata mapping, and bounds remain
/// storage-backed below.
fn open_storage_connection(_engine: &Engine) -> Result<Connection, QueryError> {
    Err(QueryError::StorageFailure)
}

fn check_input_bounds(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &str,
    sequence: u64,
    control: &SqlControl,
) -> Result<(), QueryError> {
    control.check()?;
    let sequence = i64::try_from(sequence).map_err(|_| QueryError::CandidateBoundExceeded)?;
    let (documents, bytes) = transaction
        .query_row(
            "SELECT COUNT(*), COALESCE(SUM(LENGTH(body) + LENGTH(id) + LENGTH(collection)), 0)
             FROM (
                 SELECT collection, id, deleted, expires_at, body, MAX(commit_seq)
                 FROM documents WHERE tenant = ?1 AND commit_seq <= ?2
                 GROUP BY collection, id
             )
             WHERE deleted = 0 AND (expires_at IS NULL OR expires_at > ?3)",
            rusqlite::params![tenant, sequence, unix_millis()],
            |row| Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?)),
        )
        .map_err(|_| control.interrupted(QueryError::StorageFailure))?;
    if documents < 0 || bytes < 0 {
        return Err(QueryError::StorageFailure);
    }
    if usize::try_from(documents).unwrap_or(usize::MAX) > MAX_SQL_INPUT_DOCUMENTS
        || usize::try_from(bytes).unwrap_or(usize::MAX) > MAX_SQL_INPUT_BYTES
    {
        return Err(QueryError::CandidateBoundExceeded);
    }
    control.check()
}

fn create_collection_views(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &str,
    sequence: u64,
    tables: &[String],
    control: &SqlControl,
) -> Result<(), QueryError> {
    let columns = storage_columns(transaction)?;
    for collection in tables {
        control.check()?;
        let fields = collection_fields(transaction, tenant, sequence, collection)?;
        let mut select = String::from(
            "SELECT d.id AS \"_id\",\n                   json_extract(CAST(d.body AS TEXT), '$.\"$value\"') AS \"body\",\n                   d.revision AS \"_rev\", d.hlc AS \"_hlc\",\n                   (SELECT MIN(created.committed_at) FROM documents created\n                    WHERE created.tenant = d.tenant AND created.collection = d.collection\n                      AND created.id = d.id AND created.commit_seq <= d.snapshot_seq) AS \"_created\",\n                   d.committed_at AS \"_updated\"",
        );
        for field in fields {
            if field == "_id" || field == "body" || SQL_METADATA_COLUMNS.contains(&field.as_str()) {
                continue;
            }
            let path = sql_string(&format!("$.\"{}\"", json_path_component(&field)));
            let text_column = format!("f:{field}#t");
            let number_column = format!("f:{field}#n");
            let fallback = format!("json_extract(CAST(d.body AS TEXT), {path})");
            let expression = match (
                columns.contains(&text_column),
                columns.contains(&number_column),
            ) {
                (true, true) => format!(
                    "CASE WHEN d.\"{}\" IS NOT NULL THEN d.\"{}\" WHEN d.\"{}\" IS NOT NULL THEN d.\"{}\" ELSE {fallback} END",
                    quote(&text_column),
                    quote(&text_column),
                    quote(&number_column),
                    quote(&number_column)
                ),
                (true, false) => format!("COALESCE(d.\"{}\", {fallback})", quote(&text_column)),
                (false, true) => format!("COALESCE(d.\"{}\", {fallback})", quote(&number_column)),
                (false, false) => fallback,
            };
            write!(&mut select, ",\n                   {expression} AS \"{}\"", quote(&field))
                .map_err(|_| QueryError::StorageFailure)?;
        }
        let sequence = i64::try_from(sequence).map_err(|_| QueryError::CandidateBoundExceeded)?;
        let source = format!(
            "CREATE TEMP VIEW \"{}\" AS\n             WITH ranked AS (\n                 SELECT documents.*, {sequence} AS snapshot_seq,\n                        ROW_NUMBER() OVER (PARTITION BY id ORDER BY commit_seq DESC) AS row_number\n                 FROM documents\n                 WHERE tenant = {tenant} AND collection = {collection} AND commit_seq <= {sequence}\n             )\n             {select}\n             FROM ranked d\n             WHERE d.row_number = 1 AND d.deleted = 0\n               AND (d.expires_at IS NULL OR d.expires_at > {now})",
            quote(collection),
            tenant = sql_string(tenant),
            collection = sql_string(collection),
            now = unix_millis(),
        );
        transaction
            .execute(&source, [])
            .map_err(|_| control.interrupted(QueryError::StorageFailure))?;
    }
    Ok(())
}

fn storage_columns(
    transaction: &rusqlite::Transaction<'_>,
) -> Result<BTreeSet<String>, QueryError> {
    let mut statement = transaction
        .prepare("SELECT name FROM pragma_table_xinfo('documents')")
        .map_err(|_| QueryError::StorageFailure)?;
    statement
        .query_map([], |row| row.get::<_, String>(0))
        .map_err(|_| QueryError::StorageFailure)?
        .collect::<Result<BTreeSet<_>, _>>()
        .map_err(|_| QueryError::StorageFailure)
}

fn collection_fields(
    transaction: &rusqlite::Transaction<'_>,
    tenant: &str,
    sequence: u64,
    collection: &str,
) -> Result<BTreeSet<String>, QueryError> {
    let sequence = i64::try_from(sequence).map_err(|_| QueryError::CandidateBoundExceeded)?;
    let mut statement = transaction
        .prepare(
            "SELECT DISTINCT json_each.key
             FROM documents, json_each(
                 CASE WHEN json_type(CAST(documents.body AS TEXT), '$.\"$value\"') = 'object'
                      THEN json_extract(CAST(documents.body AS TEXT), '$.\"$value\"')
                      ELSE '{}' END
             ) AS json_each
             WHERE documents.tenant = ?1 AND documents.collection = ?2
               AND documents.commit_seq <= ?3",
        )
        .map_err(|_| QueryError::StorageFailure)?;
    statement
        .query_map(rusqlite::params![tenant, collection, sequence], |row| row.get::<_, String>(0))
        .map_err(|_| QueryError::StorageFailure)?
        .collect::<Result<BTreeSet<_>, _>>()
        .map_err(|_| QueryError::StorageFailure)
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| i64::try_from(duration.as_millis()).ok())
        .unwrap_or(i64::MAX)
}

fn sql_string(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn json_path_component(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

fn quote(value: &str) -> String {
    value.replace('"', "\"\"")
}

fn value_size(value: &DocumentValue) -> Result<usize, QueryError> {
    Ok(json_value(value)?.len())
}

fn sqlite_value(value: &DocumentValue) -> Result<Value, QueryError> {
    match value {
        DocumentValue::Null => Ok(Value::Null),
        DocumentValue::Boolean(value) => Ok(Value::Integer(i64::from(*value))),
        DocumentValue::Integer(value) => Ok(Value::Integer(*value)),
        DocumentValue::Number(value) if value.is_finite() => Ok(Value::Real(*value)),
        DocumentValue::Number(_) => Err(QueryError::InvalidSpec),
        DocumentValue::String(value) => Ok(Value::Text(value.clone())),
        DocumentValue::Array(_) | DocumentValue::Object(_) => Ok(Value::Text(json_value(value)?)),
    }
}

fn value_ref(value: ValueRef<'_>) -> DocumentValue {
    match value {
        ValueRef::Null => DocumentValue::Null,
        ValueRef::Integer(value) => DocumentValue::Integer(value),
        ValueRef::Real(value) => DocumentValue::Number(value),
        ValueRef::Text(value) | ValueRef::Blob(value) => {
            DocumentValue::String(String::from_utf8_lossy(value).into_owned())
        }
    }
}

/// Serializes values with the JSON grammar. Every string is escaped by code
/// point, including all controls, rather than by replacing only quotes.
fn json_value(value: &DocumentValue) -> Result<String, QueryError> {
    fn string(value: &str) -> String {
        let mut output = String::with_capacity(value.len() + 2);
        output.push('"');
        for character in value.chars() {
            match character {
                '"' => output.push_str("\\\""),
                '\\' => output.push_str("\\\\"),
                '\u{08}' => output.push_str("\\b"),
                '\u{0c}' => output.push_str("\\f"),
                '\n' => output.push_str("\\n"),
                '\r' => output.push_str("\\r"),
                '\t' => output.push_str("\\t"),
                character if character <= '\u{1f}' => {
                    write!(&mut output, "\\u{:04x}", character as u32).expect("String write");
                }
                character => output.push(character),
            }
        }
        output.push('"');
        output
    }
    match value {
        DocumentValue::Null => Ok("null".into()),
        DocumentValue::Boolean(value) => Ok(value.to_string()),
        DocumentValue::Integer(value) => Ok(value.to_string()),
        DocumentValue::Number(value) if value.is_finite() => Ok(value.to_string()),
        DocumentValue::Number(_) => Err(QueryError::InvalidSpec),
        DocumentValue::String(value) => Ok(string(value)),
        DocumentValue::Array(values) => values
            .iter()
            .map(json_value)
            .collect::<Result<Vec<_>, _>>()
            .map(|values| format!("[{}]", values.join(","))),
        DocumentValue::Object(values) => values
            .iter()
            .map(|(key, value)| Ok(format!("{}:{}", string(key), json_value(value)?)))
            .collect::<Result<Vec<_>, QueryError>>()
            .map(|values| format!("{{{}}}", values.join(","))),
    }
}
