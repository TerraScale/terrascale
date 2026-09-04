//! Bounded `SQLite` execution for SQL query plans.

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt::Write,
    sync::{
        Arc,
        atomic::{AtomicU8, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
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
    types::{Cancellation, Document, DocumentValue, ReadSnapshot, TenantScope},
};

/// Hard input and output bounds for one SQL execution. These bounds apply to
/// the in-memory `SQLite` copy as well as the result retained by the API.
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
    // FTS5 `MATCH` rides the typed seam (D-78/D-39): the in-memory SQLite
    // copy holds plain tables with no FTS5 index, so `MATCH` has no
    // executable form there (`unable to use function MATCH`). The SQL
    // lowering already resolved the query string — bound or literal — into
    // typed `Search` conditions, and the typed executor serves them exactly
    // (analyzer term space, FTS narrowing, exact re-check). Live plans only:
    // `EXPLAIN` keeps its SQLite path so it still reports the VM program
    // instead of executing the statement.
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
    let scope = TenantScope::from_parts(snapshot.tenant().clone(), engine.identity());
    let documents =
        engine.snapshot_documents(&scope, snapshot).map_err(|_| QueryError::StorageFailure)?;
    control.check()?;
    if documents.len() > MAX_SQL_INPUT_DOCUMENTS {
        return Err(QueryError::CandidateBoundExceeded);
    }
    let mut input_bytes = 0usize;
    for document in &documents {
        control.check()?;
        input_bytes = input_bytes
            .checked_add(document_size(document)?)
            .ok_or(QueryError::CandidateBoundExceeded)?;
        if input_bytes > MAX_SQL_INPUT_BYTES {
            return Err(QueryError::CandidateBoundExceeded);
        }
    }

    let started = Instant::now();
    let connection = Connection::open_in_memory().map_err(|_| QueryError::StorageFailure)?;
    let progress_control = control.clone();
    connection
        .progress_handler(SQL_PROGRESS_INTERVAL, Some(move || progress_control.progress()))
        .map_err(|_| QueryError::StorageFailure)?;
    load_documents(&connection, &documents, &runtime.tables, snapshot.sequence().get(), &control)?;
    control.check()?;
    let mut statement = connection
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
            id = resolve_source_id(plan, &documents, &projected, &used_ids).unwrap_or(id);
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
    plan: &QueryPlan,
    documents: &[Document],
    projected: &[(String, DocumentValue)],
    used_ids: &BTreeSet<String>,
) -> Option<String> {
    let collection = plan.model().source().collection()?.as_str();
    documents
        .iter()
        .filter(|document| document.collection().as_str() == collection)
        .filter(|document| {
            projected.iter().all(|(name, expected)| {
                let actual = super::model::field_of(document, name).unwrap_or(&DocumentValue::Null);
                actual == expected
            })
        })
        .map(|document| document.id().as_str().to_owned())
        .find(|id| !used_ids.contains(id))
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

fn load_documents(
    connection: &Connection,
    documents: &[Document],
    table_names: &[String],
    snapshot_sequence: u64,
    control: &SqlControl,
) -> Result<(), QueryError> {
    let mut fields = BTreeMap::<String, BTreeSet<String>>::new();
    for table in table_names {
        fields.entry(table.clone()).or_default();
    }
    for document in documents {
        control.check()?;
        if let DocumentValue::Object(values) = document.value() {
            fields
                .entry(document.collection().as_str().to_owned())
                .or_default()
                .extend(values.keys().cloned());
        }
    }
    for (collection, names) in &fields {
        control.check()?;
        let mut schema =
            format!("CREATE TABLE \"{}\" (\"_id\" TEXT, \"body\" TEXT", quote(collection));
        for name in names {
            if name != "_id" && name != "body" && !SQL_METADATA_COLUMNS.contains(&name.as_str()) {
                write!(&mut schema, ", \"{}\"", quote(name))
                    .map_err(|_| QueryError::StorageFailure)?;
            }
        }
        for column in SQL_METADATA_COLUMNS {
            write!(&mut schema, ", \"{column}\" INTEGER")
                .map_err(|_| QueryError::StorageFailure)?;
        }
        schema.push(')');
        connection
            .execute(&schema, [])
            .map_err(|_| control.interrupted(QueryError::StorageFailure))?;
    }
    for document in documents {
        control.check()?;
        let DocumentValue::Object(values) = document.value() else { continue };
        let Some(names) = fields.get(document.collection().as_str()) else { continue };
        let mut columns = vec!["_id".to_owned(), "body".to_owned()];
        columns.extend(SQL_METADATA_COLUMNS.iter().map(|column| (*column).to_owned()));
        columns.extend(
            names
                .iter()
                .filter(|name| {
                    *name != "_id"
                        && *name != "body"
                        && !SQL_METADATA_COLUMNS.contains(&name.as_str())
                })
                .cloned(),
        );
        let placeholders =
            (1..=columns.len()).map(|index| format!("?{index}")).collect::<Vec<_>>().join(",");
        let sql = format!(
            "INSERT INTO \"{}\" ({}) VALUES ({placeholders})",
            quote(document.collection().as_str()),
            columns.iter().map(|name| format!("\"{}\"", quote(name))).collect::<Vec<_>>().join(",")
        );
        let mut params = vec![
            Value::Text(document.id().as_str().to_owned()),
            Value::Text(json_value(document.value())?),
            Value::Integer(i64::try_from(document.metadata().version()).unwrap_or(i64::MAX)),
            Value::Integer(i64::try_from(snapshot_sequence).unwrap_or(i64::MAX)),
            Value::Integer(i64::try_from(snapshot_sequence).unwrap_or(i64::MAX)),
            Value::Integer(i64::try_from(snapshot_sequence).unwrap_or(i64::MAX)),
        ];
        for name in names.iter().filter(|name| {
            *name != "_id" && *name != "body" && !SQL_METADATA_COLUMNS.contains(&name.as_str())
        }) {
            params.push(values.get(name).map_or(Ok(Value::Null), sqlite_value)?);
        }
        connection
            .execute(&sql, rusqlite::params_from_iter(params.iter()))
            .map_err(|_| control.interrupted(QueryError::StorageFailure))?;
    }
    Ok(())
}

fn quote(value: &str) -> String {
    value.replace('"', "\"\"")
}

fn document_size(document: &Document) -> Result<usize, QueryError> {
    value_size(document.value())?
        .checked_add(document.id().as_str().len())
        .and_then(|size| size.checked_add(document.collection().as_str().len()))
        .ok_or(QueryError::CandidateBoundExceeded)
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
