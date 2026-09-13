// OpenRaft's concrete storage error is intentionally preserved throughout
// this storage boundary; boxing it would alter the pinned trait-facing API.
#![allow(clippy::result_large_err)]

use std::collections::BTreeMap;
use std::fmt::Debug;
use std::io::Cursor;
use std::ops::{Deref, DerefMut, RangeBounds};
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard};

use openraft::storage::{LogFlushed, RaftLogStorage, RaftStateMachine};
use openraft::{
    BasicNode, Entry, EntryPayload, LogId, LogState, RaftLogReader, RaftSnapshotBuilder, Snapshot,
    SnapshotMeta, StorageError, StorageIOError, StoredMembership, Vote,
};
use rusqlite::{Connection, OptionalExtension, Transaction, params};
use serde::{Deserialize, Serialize, de::DeserializeOwned};

use crate::diagnostics::Timer;
use crate::{Action, Command, Outcome, Predicate, Record, TypeConfig};

type Result<T> = std::result::Result<T, StorageError<u64>>;

/// SQLite-backed Raft log and state machine. No in-memory durable fallback.
#[derive(Clone)]
pub struct SqliteStore {
    connection: Arc<Mutex<Option<Connection>>>,
    keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
}

struct ConnectionGuard<'a>(MutexGuard<'a, Option<Connection>>);

impl Deref for ConnectionGuard<'_> {
    type Target = Connection;

    fn deref(&self) -> &Self::Target {
        self.0.as_ref().expect("connection guard is constructed only for an open store")
    }
}

impl DerefMut for ConnectionGuard<'_> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        self.0.as_mut().expect("connection guard is constructed only for an open store")
    }
}

fn storage(error: impl std::fmt::Display) -> StorageError<u64> {
    StorageIOError::write_state_machine(&std::io::Error::other(error.to_string())).into()
}

fn encode<T: Serialize>(value: &T) -> Result<Vec<u8>> {
    serde_json::to_vec(value).map_err(storage)
}

fn decode<T: DeserializeOwned>(bytes: &[u8]) -> Result<T> {
    serde_json::from_slice(bytes).map_err(storage)
}

fn meta<T: DeserializeOwned>(connection: &Connection, key: &str) -> Result<Option<T>> {
    let bytes: Option<Vec<u8>> = connection
        .query_row("SELECT value FROM metadata WHERE key=?1", [key], |row| row.get(0))
        .optional()
        .map_err(storage)?;
    bytes.map(|bytes| decode(&bytes)).transpose()
}

const LOG_RANGE_QUERY: &str =
    "SELECT log_index,entry FROM raft_log WHERE log_index>=?1 AND log_index<=?2 ORDER BY log_index";

fn set_meta<T: Serialize>(connection: &Connection, key: &str, value: &T) -> Result<()> {
    connection.execute("INSERT INTO metadata(key,value) VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value", params![key, encode(value)?]).map_err(storage)?;
    Ok(())
}

// Decimal text preserves the complete unsigned Raft index, ordered by its fixed width.
fn index_key(index: u64) -> String {
    format!("{index:020}")
}

impl SqliteStore {
    pub(crate) fn bind_admission(
        &self,
        group: &str,
        local: u64,
        voters: &std::collections::BTreeMap<u64, BasicNode>,
    ) -> Result<()> {
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        if meta::<String>(&transaction, "group_identity")?.as_deref() != Some(group) {
            return Err(storage("auth admission group mismatch"));
        }
        let encoded = encode(&(local, voters))?;
        if let Some(prior) = meta::<Vec<u8>>(&transaction, "voter_admission")? {
            if prior != encoded {
                return Err(storage("auth voter identity mapping changed"));
            }
        } else {
            if meta::<LogId<u64>>(&transaction, "applied")?.is_some() {
                return Err(storage("existing auth state lacks voter admission"));
            }
            set_meta(&transaction, "voter_admission", &encoded)?;
        }
        transaction.commit().map_err(storage)?;
        Ok(())
    }

    pub(crate) fn verify_bootstrap(
        &self,
        voters: &std::collections::BTreeMap<u64, BasicNode>,
    ) -> Result<()> {
        let connection = self.lock()?;
        let encoded: Vec<u8> = meta(&connection, "voter_admission")?
            .ok_or_else(|| storage("auth voter admission missing"))?;
        let (_, admitted): (u64, std::collections::BTreeMap<u64, BasicNode>) = decode(&encoded)?;
        if &admitted != voters {
            return Err(storage("auth bootstrap voters differ from admitted mapping"));
        }
        Ok(())
    }

    /// Open encrypted persistent auth state using an explicitly authorized database key.
    /// No plaintext production constructor is exposed. Existing empty/missing storage
    /// may be created only with the one-time permission reserved by bootstrap control.
    pub fn open_encrypted(
        path: &Path,
        group_identity: &str,
        keys: Arc<dyn terrabase::encryption::DatabaseKeySource>,
        allow_create: bool,
    ) -> Result<Self> {
        if !allow_create && !path.is_file() {
            return Err(storage("auth state missing after bootstrap reservation"));
        }
        let connection =
            terrabase::storage::open_encrypted_sqlite(path, Arc::clone(&keys)).map_err(storage)?;
        let mut store = Self::from_connection(connection, group_identity, allow_create)?;
        store.keys = Some(keys);
        Ok(store)
    }

    #[cfg(test)]
    pub(crate) fn open(path: &Path, group_identity: &str) -> Result<Self> {
        Self::from_connection(Connection::open(path).map_err(storage)?, group_identity, true)
    }

    #[cfg(test)]
    pub(crate) fn insert_raw_log_for_test(&self, index: u64, bytes: &[u8]) -> Result<()> {
        self.lock()?
            .execute(
                "INSERT INTO raft_log(log_index,entry) VALUES(?1,?2)",
                params![index_key(index), bytes],
            )
            .map_err(storage)?;
        Ok(())
    }

    #[cfg(test)]
    pub(crate) fn explain_log_range_for_test(&self) -> Result<Vec<String>> {
        let connection = self.lock()?;
        let mut statement = connection
            .prepare(&format!("EXPLAIN QUERY PLAN {LOG_RANGE_QUERY}"))
            .map_err(storage)?;
        statement
            .query_map(params![index_key(1), index_key(2)], |row| row.get(3))
            .map_err(storage)?
            .map(|row| row.map_err(storage))
            .collect()
    }

    fn from_connection(
        connection: Connection,
        group_identity: &str,
        allow_create: bool,
    ) -> Result<Self> {
        if group_identity.is_empty() || group_identity.len() > 512 {
            return Err(storage("invalid auth group identity"));
        }
        if !allow_create {
            let stored: Option<String> = meta(&connection, "group_identity")?;
            if stored.as_deref() != Some(group_identity) {
                return Err(storage("auth state identity missing or changed"));
            }
        }
        connection.busy_timeout(std::time::Duration::from_secs(5)).map_err(storage)?;
        connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA locking_mode=EXCLUSIVE;
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS raft_log(log_index TEXT PRIMARY KEY,entry BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS auth_records(model TEXT NOT NULL,id TEXT NOT NULL,body BLOB NOT NULL,PRIMARY KEY(model,id));
            CREATE TABLE IF NOT EXISTS auth_indexes(model TEXT NOT NULL,fields BLOB NOT NULL,PRIMARY KEY(model,fields));
            CREATE TABLE IF NOT EXISTS auth_dedupe(operation_key TEXT PRIMARY KEY,digest BLOB NOT NULL,outcome BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS auth_transactions(id TEXT PRIMARY KEY,workspace BLOB NOT NULL);").map_err(storage)?;
        let prior: Option<String> = meta(&connection, "group_identity")?;
        match prior {
            Some(prior) if prior != group_identity => {
                return Err(storage("auth group identity mismatch"));
            }
            None => set_meta(&connection, "group_identity", &group_identity)?,
            _ => {}
        }
        Ok(Self { connection: Arc::new(Mutex::new(Some(connection))), keys: None })
    }

    fn lock(&self) -> Result<ConnectionGuard<'_>> {
        let _timing = Timer::new("store.lock");
        if let Some(keys) = &self.keys {
            let lease = keys.current_lease().map_err(storage)?;
            if lease.is_expired() {
                return Err(storage("auth storage key lease expired"));
            }
        }
        let connection =
            self.connection.lock().map_err(|_| storage("auth SQLite lock poisoned"))?;
        if connection.is_none() {
            return Err(storage("auth SQLite store is closed"));
        }
        Ok(ConnectionGuard(connection))
    }

    /// Closes the shared connection after Raft has stopped. Retained store
    /// clones fail closed instead of retaining a SQLite/VFS owner across reopen.
    pub(crate) fn close(&self) -> Result<()> {
        let mut connection =
            self.connection.lock().map_err(|_| storage("auth SQLite lock poisoned"))?;
        drop(connection.take());
        Ok(())
    }

    /// Read local applied state. Callers must first confirm Raft's linearizable barrier.
    /// This method does not itself grant authority to return the record to a client.
    #[cfg(test)]
    pub(crate) fn read_record(&self, model: &str, id: &str) -> Result<Option<Record>> {
        read_record(&*self.lock()?, model, id)
    }

    pub(crate) fn query(&self, action: &Action) -> Result<Outcome> {
        let _timing = Timer::new("store.query");
        if !matches!(action, Action::Find { .. } | Action::Count { .. }) {
            return Ok(invalid("auth query action required"));
        }
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        execute_action(&transaction, action)
    }
}

fn read_record(connection: &Connection, model: &str, id: &str) -> Result<Option<Record>> {
    let bytes: Option<Vec<u8>> = connection
        .query_row(
            "SELECT body FROM auth_records WHERE model=?1 AND id=?2",
            params![model, id],
            |row| row.get(0),
        )
        .optional()
        .map_err(storage)?;
    bytes.map(|bytes| decode(&bytes)).transpose()
}

fn records(connection: &Connection, model: &str) -> Result<Vec<(String, Record)>> {
    let mut statement = connection
        .prepare("SELECT id,body FROM auth_records WHERE model=?1 ORDER BY id")
        .map_err(storage)?;
    let rows = statement
        .query_map([model], |row| Ok((row.get::<_, String>(0)?, row.get::<_, Vec<u8>>(1)?)))
        .map_err(storage)?;
    rows.map(|row| {
        let (id, body) = row.map_err(storage)?;
        Ok((id, decode(&body)?))
    })
    .collect()
}

fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 128
        && name.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

fn matches(record: &Record, expected: &Record) -> bool {
    expected.iter().all(|(field, value)| record.get(field) == Some(value))
}

fn index_value(record: &Record, fields: &[String]) -> Option<Vec<serde_json::Value>> {
    fn normalize(value: &mut serde_json::Value) {
        use serde_json::Value;
        match value {
            Value::Number(number) if number.is_f64() && number.as_f64() == Some(0.0) => {
                *number = serde_json::Number::from_f64(0.0).expect("finite zero");
            }
            Value::Array(values) => values.iter_mut().for_each(normalize),
            Value::Object(values) => values.values_mut().for_each(normalize),
            _ => {}
        }
    }
    fields
        .iter()
        .map(|field| {
            let mut value = record.get(field).filter(|value| !value.is_null())?.clone();
            // Value equality treats signed float zero equally. Serialized index
            // keys must preserve that equality, including inside arrays/objects.
            normalize(&mut value);
            Some(value)
        })
        .collect()
}

fn conflicts(connection: &Connection, model: &str, id: &str, record: &Record) -> Result<bool> {
    let mut statement =
        connection.prepare("SELECT fields FROM auth_indexes WHERE model=?1").map_err(storage)?;
    let fields: Vec<Vec<String>> = statement
        .query_map([model], |row| row.get::<_, Vec<u8>>(0))
        .map_err(storage)?
        .map(|row| decode(&row.map_err(storage)?))
        .collect::<Result<_>>()?;
    if fields.is_empty() {
        return Ok(false);
    }
    for (other_id, other) in records(connection, model)? {
        if other_id == id {
            continue;
        }
        for index in &fields {
            if let Some(value) = index_value(record, index)
                && index_value(&other, index).as_ref() == Some(&value)
            {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

const MAX_MUTATION_ROWS: usize = 1000;
const MAX_MUTATION_BYTES: usize = 1024 * 1024;
const MAX_QUERY_JOINS: usize = 8;
const MAX_JOIN_ROWS: usize = 1000;
const MAX_JOIN_SCAN_ROWS: usize = 1_000_000;
const MAX_JOIN_SCAN_BYTES: usize = 64 * 1024 * 1024;

#[derive(Default)]
struct QueryScanBudget {
    rows: usize,
    bytes: usize,
}

impl QueryScanBudget {
    fn admit(&mut self, bytes: usize) -> bool {
        self.rows = self.rows.saturating_add(1);
        self.bytes = self.bytes.saturating_add(bytes);
        self.rows <= MAX_JOIN_SCAN_ROWS && self.bytes <= MAX_JOIN_SCAN_BYTES
    }
}

fn apply_joins(
    connection: &Connection,
    base: &Record,
    mut projected: Record,
    joins: &[crate::QueryJoin],
    scan: &mut QueryScanBudget,
    available_bytes: usize,
) -> Result<Option<Record>> {
    if encode(&projected)?.len() > available_bytes {
        return Ok(None);
    }
    for join in joins {
        if base.contains_key(&join.output_field) {
            return Ok(None);
        }
        // Every relation reads the original base row, never a prior join result.
        let key = base.get(&join.from_field).filter(|value| !value.is_null());
        let cap = match join.relation {
            crate::JoinRelation::OneToOne => join.limit.min(1),
            crate::JoinRelation::OneToMany => join.limit,
        } as usize;
        let mut related = Vec::new();
        let mut related_bytes = 0usize;
        if let Some(key) = key.filter(|_| cap > 0) {
            if join.to_field == "id" {
                if let Some(id) = key.as_str()
                    && let Some(found) = read_record(connection, &join.model, id)?
                {
                    let size = encode(&found)?.len();
                    if !scan.admit(size) || size > available_bytes {
                        return Ok(None);
                    }
                    related.push(found);
                }
            } else {
                let mut statement = connection
                    .prepare("SELECT body FROM auth_records WHERE model=?1 ORDER BY id")
                    .map_err(storage)?;
                let rows = statement
                    .query_map([&join.model], |row| row.get::<_, Vec<u8>>(0))
                    .map_err(storage)?;
                for row in rows {
                    let body = row.map_err(storage)?;
                    if !scan.admit(body.len()) {
                        return Ok(None);
                    }
                    let candidate: Record = decode(&body)?;
                    if candidate.get(&join.to_field) == Some(key) {
                        related_bytes = related_bytes.saturating_add(body.len());
                        if related_bytes > available_bytes {
                            return Ok(None);
                        }
                        related.push(candidate);
                        if related.len() >= cap {
                            break;
                        }
                    }
                }
            }
        }
        let object = |record: Record| serde_json::Value::Object(record.into_iter().collect());
        let value = match join.relation {
            crate::JoinRelation::OneToOne => {
                related.into_iter().next().map_or(serde_json::Value::Null, object)
            }
            crate::JoinRelation::OneToMany => {
                serde_json::Value::Array(related.into_iter().map(object).collect())
            }
        };
        projected.insert(join.output_field.clone(), value);
        // Account for field names, arrays and punctuation as well as row bodies.
        if encode(&projected)?.len() > available_bytes {
            return Ok(None);
        }
    }
    Ok(Some(projected))
}

fn matching_records(
    connection: &Connection,
    model: &str,
    predicates: &[Predicate],
    limit: usize,
) -> Result<Option<Vec<(String, Record)>>> {
    if predicates.len() > 64 {
        return Ok(None);
    }
    let mut statement = connection
        .prepare("SELECT id,body FROM auth_records WHERE model=?1 ORDER BY id")
        .map_err(storage)?;
    let rows = statement
        .query_map([model], |row| Ok((row.get::<_, String>(0)?, row.get::<_, Vec<u8>>(1)?)))
        .map_err(storage)?;
    let mut matches = Vec::new();
    let mut matched_bytes = 0usize;
    for row in rows {
        let (id, body) = row.map_err(storage)?;
        let record: Record = decode(&body)?;
        if crate::query::matches(&record, predicates) {
            if body.len() > MAX_MUTATION_BYTES - matched_bytes || matches.len() >= limit {
                return Ok(None);
            }
            matched_bytes += body.len();
            matches.push((id, record));
            if limit == 1 {
                return Ok(Some(matches));
            }
        }
    }
    Ok(Some(matches))
}

fn validate_final_records(
    connection: &Connection,
    model: &str,
    replacements: &BTreeMap<String, Record>,
) -> Result<bool> {
    let mut statement =
        connection.prepare("SELECT fields FROM auth_indexes WHERE model=?1").map_err(storage)?;
    let indexes = statement.query_map([model], |row| row.get::<_, Vec<u8>>(0)).map_err(storage)?;
    for fields in indexes {
        let fields: Vec<String> = decode(&fields.map_err(storage)?)?;
        let mut seen = std::collections::BTreeSet::new();
        for record in replacements.values() {
            if let Some(value) = index_value(record, &fields)
                && !seen.insert(encode(&value)?)
            {
                return Ok(true);
            }
        }
        // Unchanged rows already satisfy this index. Compare only against the
        // bounded replacement keys, without loading the entire model into RAM.
        let mut rows = connection
            .prepare("SELECT id,body FROM auth_records WHERE model=?1 ORDER BY id")
            .map_err(storage)?;
        let rows = rows
            .query_map([model], |row| Ok((row.get::<_, String>(0)?, row.get::<_, Vec<u8>>(1)?)))
            .map_err(storage)?;
        for row in rows {
            let (id, body) = row.map_err(storage)?;
            if replacements.contains_key(&id) {
                continue;
            }
            let record: Record = decode(&body)?;
            if let Some(value) = index_value(&record, &fields)
                && seen.contains(&encode(&value)?)
            {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

fn invalid(reason: &str) -> Outcome {
    Outcome::Invalid { reason: reason.into() }
}

fn execute(transaction: &Transaction<'_>, command: &Command) -> Result<Outcome> {
    let body = encode(&command.action)?;
    if command.idempotency_key.is_empty()
        || command.idempotency_key.len() > 256
        || body.len() > 1024 * 1024
    {
        transactions::poison_operation(transaction, &command.action)?;
        return Ok(invalid("invalid idempotency key or operation exceeds 1 MiB"));
    }
    let digest = blake3::hash(&body);
    let prior: Option<(Vec<u8>, Vec<u8>)> = transaction
        .query_row(
            "SELECT digest,outcome FROM auth_dedupe WHERE operation_key=?1",
            [&command.idempotency_key],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .optional()
        .map_err(storage)?;
    if let Some((prior_digest, outcome)) = prior {
        return if prior_digest == digest.as_bytes() {
            decode(&outcome)
        } else {
            transactions::poison_operation(transaction, &command.action)?;
            Ok(Outcome::IdempotencyConflict)
        };
    }
    let logical_time =
        meta::<u64>(transaction, "logical_time")?.unwrap_or(0).max(command.observed_at_ms);
    set_meta(transaction, "logical_time", &logical_time)?;
    let outcome = execute_dispatched(transaction, &command.action, logical_time)?;
    transaction
        .execute(
            "INSERT INTO auth_dedupe(operation_key,digest,outcome) VALUES(?1,?2,?3)",
            params![command.idempotency_key, digest.as_bytes().as_slice(), encode(&outcome)?],
        )
        .map_err(storage)?;
    Ok(outcome)
}

#[path = "transactions.rs"]
mod transactions;
use transactions::execute_dispatched;

fn execute_action(transaction: &Transaction<'_>, action: &Action) -> Result<Outcome> {
    let model = match action {
        Action::CreateUniqueIndex { model, .. }
        | Action::InsertUnique { model, .. }
        | Action::CompareAndSwap { model, .. }
        | Action::ConsumeOne { model, .. }
        | Action::UpdateOne { model, .. }
        | Action::UpdateMany { model, .. }
        | Action::DeleteOne { model, .. }
        | Action::DeleteMany { model, .. }
        | Action::IncrementOne { model, .. }
        | Action::ConsumeMatching { model, .. }
        | Action::Count { model, .. }
        | Action::Find { model, .. } => model,
        _ => return Ok(invalid("nested transaction command is forbidden")),
    };
    if !valid_name(model) {
        return Ok(invalid("invalid model"));
    }
    match action {
        Action::Find { predicates, offset, limit, select, sort, joins, .. } => {
            if *limit > 1000 || *offset > 1000 {
                return Ok(invalid("query page exceeds bound"));
            }
            if predicates.len() > 64 {
                return Ok(invalid("query predicate bound exceeded"));
            }
            if select.as_ref().is_some_and(|fields| {
                fields.len() > 64 || fields.iter().any(|field| !valid_name(field))
            }) {
                return Ok(invalid("query selection exceeds bound"));
            }
            if sort.as_ref().is_some_and(|sort| !valid_name(&sort.field)) {
                return Ok(invalid("invalid query sort field"));
            }
            let outputs = joins
                .iter()
                .map(|join| &join.output_field)
                .collect::<std::collections::BTreeSet<_>>();
            if joins.len() > MAX_QUERY_JOINS || outputs.len() != joins.len() {
                return Ok(invalid("query join bound or duplicate output"));
            }
            if joins.iter().any(|join| {
                !valid_name(&join.model)
                    || !valid_name(&join.output_field)
                    || !valid_name(&join.from_field)
                    || !valid_name(&join.to_field)
                    || join.limit > MAX_JOIN_ROWS as u32
                    || join.output_field == "id"
            }) {
                return Ok(invalid("invalid query join"));
            }
            if *limit == 0 {
                return Ok(Outcome::Records { records: Vec::new() });
            }
            let retained = (*offset as usize) + (*limit as usize);
            let mut scan = QueryScanBudget::default();
            let mut statement = transaction
                .prepare("SELECT body FROM auth_records WHERE model=?1 ORDER BY id")
                .map_err(storage)?;
            let rows =
                statement.query_map([model], |row| row.get::<_, Vec<u8>>(0)).map_err(storage)?;
            let mut retained_rows: Vec<(Record, usize)> = Vec::new();
            let mut retained_bytes = 0usize;
            for row in rows {
                let body = row.map_err(storage)?;
                if !scan.admit(body.len()) {
                    return Ok(invalid("query scan exceeds bounded work limit"));
                }
                let record: Record = decode(&body)?;
                if !crate::query::matches(&record, predicates) {
                    continue;
                }
                let position = if let Some(sort) = sort {
                    retained_rows
                        .binary_search_by(|(left, _)| {
                            crate::query::compare_records(left, &record, sort)
                        })
                        .unwrap_or_else(|position| position)
                } else {
                    retained_rows.len()
                };
                if position >= retained {
                    continue;
                }
                if retained_rows.len() == retained {
                    let (_, size) = retained_rows.pop().expect("retained page is nonempty");
                    retained_bytes -= size;
                }
                retained_bytes = retained_bytes.saturating_add(body.len());
                if retained_bytes > 4 * 1024 * 1024 {
                    return Ok(invalid("query retained page exceeds 4 MiB"));
                }
                retained_rows.insert(position, (record, body.len()));
                if sort.is_none() && retained_rows.len() == retained {
                    break;
                }
            }
            let mut result = Vec::new();
            // Include the array brackets and separators in the response budget.
            let mut output_bytes = 2usize;
            for (base, _) in retained_rows.into_iter().skip(*offset as usize) {
                let projected = match select {
                    Some(fields) => fields
                        .iter()
                        .filter_map(|field| {
                            base.get(field).map(|value| (field.clone(), value.clone()))
                        })
                        .collect(),
                    None => base.clone(),
                };
                let available = MAX_MUTATION_BYTES.saturating_sub(output_bytes);
                let Some(record) =
                    apply_joins(transaction, &base, projected, joins, &mut scan, available)?
                else {
                    return Ok(invalid(
                        "query exceeds join work, collision or response byte limit",
                    ));
                };
                let size = encode(&record)?.len();
                output_bytes = output_bytes.saturating_add(size + usize::from(!result.is_empty()));
                if output_bytes > MAX_MUTATION_BYTES {
                    return Ok(invalid("query response exceeds 1 MiB"));
                }
                result.push(record);
            }
            Ok(Outcome::Records { records: result })
        }
        Action::Count { predicates, .. } => {
            if predicates.len() > 64 {
                return Ok(invalid("query predicate bound exceeded"));
            }
            let mut count = 0u64;
            let mut statement = transaction
                .prepare("SELECT body FROM auth_records WHERE model=?1 ORDER BY id")
                .map_err(storage)?;
            let rows =
                statement.query_map([model], |row| row.get::<_, Vec<u8>>(0)).map_err(storage)?;
            for row in rows {
                let record: Record = decode(&row.map_err(storage)?)?;
                if crate::query::matches(&record, predicates) {
                    count = count.checked_add(1).ok_or_else(|| storage("count exhausted"))?;
                }
            }
            Ok(Outcome::Count { count })
        }
        Action::CreateUniqueIndex { fields, .. } => {
            if fields.is_empty()
                || fields.len() > 64
                || fields.iter().any(|field| !valid_name(field))
            {
                return Ok(invalid("invalid unique index fields"));
            }
            let mut seen = std::collections::BTreeSet::new();
            for (_, record) in records(transaction, model)? {
                if let Some(value) = index_value(&record, fields)
                    && !seen.insert(encode(&value)?)
                {
                    return Ok(Outcome::UniqueConflict);
                }
            }
            transaction
                .execute(
                    "INSERT OR IGNORE INTO auth_indexes(model,fields) VALUES(?1,?2)",
                    params![model, encode(fields)?],
                )
                .map_err(storage)?;
            Ok(Outcome::Applied)
        }
        Action::InsertUnique { record, .. } => {
            let Some(id) = record
                .get("id")
                .and_then(serde_json::Value::as_str)
                .filter(|id| !id.is_empty() && id.len() <= 256)
            else {
                return Ok(invalid("record requires a bounded string id"));
            };
            if read_record(transaction, model, id)?.is_some()
                || conflicts(transaction, model, id, record)?
            {
                return Ok(Outcome::UniqueConflict);
            }
            transaction
                .execute(
                    "INSERT INTO auth_records(model,id,body) VALUES(?1,?2,?3)",
                    params![model, id, encode(record)?],
                )
                .map_err(storage)?;
            Ok(Outcome::Record { record: record.clone() })
        }
        Action::CompareAndSwap { id, expected, update, .. } => {
            if expected.is_empty() || update.contains_key("id") {
                return Ok(invalid("CAS requires predicates and cannot update id"));
            }
            let Some(mut record) =
                read_record(transaction, model, id)?.filter(|record| matches(record, expected))
            else {
                return Ok(Outcome::NotMatched);
            };
            record.extend(update.clone());
            if encode(&record)?.len() > 1024 * 1024 {
                return Ok(invalid("record exceeds 1 MiB"));
            }
            if conflicts(transaction, model, id, &record)? {
                return Ok(Outcome::UniqueConflict);
            }
            transaction
                .execute(
                    "UPDATE auth_records SET body=?3 WHERE model=?1 AND id=?2",
                    params![model, id, encode(&record)?],
                )
                .map_err(storage)?;
            Ok(Outcome::Record { record })
        }
        Action::ConsumeOne { id, expected, .. } => {
            if expected.is_empty() {
                return Ok(invalid("consume requires predicates"));
            }
            let Some(record) =
                read_record(transaction, model, id)?.filter(|record| matches(record, expected))
            else {
                return Ok(Outcome::NotMatched);
            };
            transaction
                .execute("DELETE FROM auth_records WHERE model=?1 AND id=?2", params![model, id])
                .map_err(storage)?;
            Ok(Outcome::Record { record })
        }
        Action::UpdateOne { predicates, update, .. } => {
            if update.contains_key("id") || predicates.len() > 64 {
                return Ok(invalid("invalid update predicates or id"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, 1)? else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            let Some((id, mut record)) = rows.into_iter().next() else {
                return Ok(Outcome::NotMatched);
            };
            record.extend(update.clone());
            if encode(&record)?.len() > MAX_MUTATION_BYTES
                || validate_final_records(
                    transaction,
                    model,
                    &BTreeMap::from([(id.clone(), record.clone())]),
                )?
            {
                return Ok(if encode(&record)?.len() > MAX_MUTATION_BYTES {
                    invalid("record exceeds 1 MiB")
                } else {
                    Outcome::UniqueConflict
                });
            }
            transaction
                .execute(
                    "UPDATE auth_records SET body=?3 WHERE model=?1 AND id=?2",
                    params![model, id, encode(&record)?],
                )
                .map_err(storage)?;
            Ok(Outcome::Record { record })
        }
        Action::UpdateMany { predicates, update, .. } => {
            if update.contains_key("id") || predicates.len() > 64 {
                return Ok(invalid("invalid update predicates or id"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, MAX_MUTATION_ROWS)?
            else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            let mut replacements = BTreeMap::new();
            let mut bytes = 0usize;
            for (id, mut record) in rows {
                record.extend(update.clone());
                let size = encode(&record)?.len();
                if size > MAX_MUTATION_BYTES - bytes {
                    return Ok(invalid("mutation byte bound exceeded"));
                }
                bytes += size;
                replacements.insert(id, record);
            }
            if validate_final_records(transaction, model, &replacements)? {
                return Ok(Outcome::UniqueConflict);
            }
            let count = replacements.len() as u64;
            for (id, record) in replacements {
                transaction
                    .execute(
                        "UPDATE auth_records SET body=?3 WHERE model=?1 AND id=?2",
                        params![model, id, encode(&record)?],
                    )
                    .map_err(storage)?;
            }
            Ok(Outcome::Count { count })
        }
        Action::DeleteOne { predicates, .. } => {
            if predicates.len() > 64 {
                return Ok(invalid("query predicate bound exceeded"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, 1)? else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            let Some((id, _)) = rows.into_iter().next() else {
                return Ok(Outcome::Count { count: 0 });
            };
            transaction
                .execute("DELETE FROM auth_records WHERE model=?1 AND id=?2", params![model, id])
                .map_err(storage)?;
            Ok(Outcome::Count { count: 1 })
        }
        Action::DeleteMany { predicates, .. } => {
            if predicates.len() > 64 {
                return Ok(invalid("query predicate bound exceeded"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, MAX_MUTATION_ROWS)?
            else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            for (id, _) in &rows {
                transaction
                    .execute(
                        "DELETE FROM auth_records WHERE model=?1 AND id=?2",
                        params![model, id],
                    )
                    .map_err(storage)?;
            }
            Ok(Outcome::Count { count: rows.len() as u64 })
        }
        Action::ConsumeMatching { predicates, .. } => {
            if predicates.len() > 64 {
                return Ok(invalid("query predicate bound exceeded"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, 1)? else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            let Some((id, record)) = rows.into_iter().next() else {
                return Ok(Outcome::NotMatched);
            };
            transaction
                .execute("DELETE FROM auth_records WHERE model=?1 AND id=?2", params![model, id])
                .map_err(storage)?;
            Ok(Outcome::Record { record })
        }
        Action::IncrementOne { predicates, increment, set, .. } => {
            if predicates.len() > 64
                || set.contains_key("id")
                || increment.keys().any(|field| field == "id")
                || increment.keys().any(|field| set.contains_key(field))
            {
                return Ok(invalid("invalid increment fields"));
            }
            let Some(rows) = matching_records(transaction, model, predicates, 1)? else {
                return Ok(invalid("mutation row bound exceeded"));
            };
            let Some((id, mut record)) = rows.into_iter().next() else {
                return Ok(Outcome::NotMatched);
            };
            record.extend(set.clone());
            for (field, amount) in increment {
                let Some(serde_json::Value::Number(current)) = record.get(field) else {
                    return Ok(invalid("increment field is not numeric"));
                };
                let value = if let (Some(a), Some(b)) = (
                    current.as_i64().map(i128::from).or_else(|| current.as_u64().map(i128::from)),
                    amount.as_i64().map(i128::from).or_else(|| amount.as_u64().map(i128::from)),
                ) {
                    let sum = a + b;
                    if sum < i128::from(i64::MIN) || sum > i128::from(u64::MAX) {
                        return Ok(invalid("increment overflow"));
                    }
                    if sum < 0 {
                        serde_json::Number::from(sum as i64)
                    } else {
                        serde_json::Number::from(sum as u64)
                    }
                } else {
                    // Mixed floating-point arithmetic may not silently discard
                    // the low bits of an exact integer counter.
                    for number in [current, amount] {
                        let integer = number
                            .as_i64()
                            .map(i128::from)
                            .or_else(|| number.as_u64().map(i128::from));
                        if integer.is_some_and(|value| value.abs() > 9_007_199_254_740_992) {
                            return Ok(invalid(
                                "integer cannot be represented exactly for floating-point increment",
                            ));
                        }
                    }
                    let Some(current) = current.as_f64() else {
                        return Ok(invalid("increment field is not numeric"));
                    };
                    let Some(amount) = amount.as_f64() else {
                        return Ok(invalid("increment is not numeric"));
                    };
                    let Some(value) = serde_json::Number::from_f64(current + amount) else {
                        return Ok(invalid("increment overflow"));
                    };
                    value
                };
                record.insert(field.clone(), serde_json::Value::Number(value));
            }
            if encode(&record)?.len() > MAX_MUTATION_BYTES {
                return Ok(invalid("record exceeds 1 MiB"));
            }
            if validate_final_records(
                transaction,
                model,
                &BTreeMap::from([(id.clone(), record.clone())]),
            )? {
                return Ok(Outcome::UniqueConflict);
            }
            transaction
                .execute(
                    "UPDATE auth_records SET body=?3 WHERE model=?1 AND id=?2",
                    params![model, id, encode(&record)?],
                )
                .map_err(storage)?;
            Ok(Outcome::Record { record })
        }
        _ => Ok(invalid("nested transaction command is forbidden")),
    }
}

impl RaftLogReader<TypeConfig> for SqliteStore {
    async fn try_get_log_entries<R: RangeBounds<u64> + Clone + Debug + Send>(
        &mut self,
        range: R,
    ) -> Result<Vec<Entry<TypeConfig>>> {
        let _timing = Timer::new("store.log_read");
        let lower = match range.start_bound() {
            std::ops::Bound::Included(index) => Some(*index),
            std::ops::Bound::Excluded(index) => index.checked_add(1),
            std::ops::Bound::Unbounded => Some(0),
        };
        let upper = match range.end_bound() {
            std::ops::Bound::Included(index) => Some(*index),
            std::ops::Bound::Excluded(index) => index.checked_sub(1),
            std::ops::Bound::Unbounded => Some(u64::MAX),
        };
        let (Some(lower), Some(upper)) = (lower, upper) else {
            return Ok(Vec::new());
        };
        if lower > upper {
            return Ok(Vec::new());
        }
        let connection = self.lock()?;
        let mut statement = connection.prepare(LOG_RANGE_QUERY).map_err(storage)?;
        let rows = statement
            .query_map(params![index_key(lower), index_key(upper)], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, Vec<u8>>(1)?))
            })
            .map_err(storage)?;
        let mut entries = Vec::new();
        for row in rows {
            let (key, bytes) = row.map_err(storage)?;
            let entry: Entry<TypeConfig> = decode(&bytes)?;
            if key != index_key(entry.log_id.index) {
                return Err(storage("raft log index does not match entry"));
            }
            entries.push(entry);
        }
        Ok(entries)
    }
}

impl RaftLogStorage<TypeConfig> for SqliteStore {
    type LogReader = Self;
    async fn get_log_state(&mut self) -> Result<LogState<TypeConfig>> {
        let connection = self.lock()?;
        let purged: Option<LogId<u64>> = meta(&connection, "purged")?;
        let bytes: Option<Vec<u8>> = connection
            .query_row("SELECT entry FROM raft_log ORDER BY log_index DESC LIMIT 1", [], |row| {
                row.get(0)
            })
            .optional()
            .map_err(storage)?;
        let last = bytes
            .map(|bytes| decode::<Entry<TypeConfig>>(&bytes).map(|entry| entry.log_id))
            .transpose()?
            .or(purged);
        Ok(LogState { last_purged_log_id: purged, last_log_id: last })
    }
    async fn save_vote(&mut self, vote: &Vote<u64>) -> Result<()> {
        let _timing = Timer::new("store.save_vote");
        set_meta(&*self.lock()?, "vote", vote)
    }
    async fn read_vote(&mut self) -> Result<Option<Vote<u64>>> {
        meta(&*self.lock()?, "vote")
    }
    async fn save_committed(&mut self, committed: Option<LogId<u64>>) -> Result<()> {
        let _timing = Timer::new("store.save_committed");
        set_meta(&*self.lock()?, "committed", &committed)
    }
    async fn read_committed(&mut self) -> Result<Option<LogId<u64>>> {
        Ok(meta::<Option<LogId<u64>>>(&*self.lock()?, "committed")?.flatten())
    }
    async fn append<I>(&mut self, entries: I, callback: LogFlushed<TypeConfig>) -> Result<()>
    where
        I: IntoIterator<Item = Entry<TypeConfig>> + Send,
        I::IntoIter: Send,
    {
        let _timing = Timer::new("store.append");
        {
            let mut connection = self.lock()?;
            let transaction = connection.transaction().map_err(storage)?;
            for entry in entries {
                transaction.execute("INSERT INTO raft_log(log_index,entry) VALUES(?1,?2) ON CONFLICT(log_index) DO UPDATE SET entry=excluded.entry",params![index_key(entry.log_id.index),encode(&entry)?]).map_err(storage)?;
            }
            transaction.commit().map_err(storage)?;
        }
        callback.log_io_completed(Ok(()));
        Ok(())
    }
    async fn truncate(&mut self, log_id: LogId<u64>) -> Result<()> {
        self.lock()?
            .execute("DELETE FROM raft_log WHERE log_index>=?1", [index_key(log_id.index)])
            .map_err(storage)?;
        Ok(())
    }
    async fn purge(&mut self, log_id: LogId<u64>) -> Result<()> {
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        set_meta(&transaction, "purged", &log_id)?;
        transaction
            .execute("DELETE FROM raft_log WHERE log_index<=?1", [index_key(log_id.index)])
            .map_err(storage)?;
        transaction.commit().map_err(storage)?;
        Ok(())
    }
    async fn get_log_reader(&mut self) -> Self {
        self.clone()
    }
}

#[derive(Serialize, Deserialize)]
struct SnapshotRows {
    group_identity: String,
    records: Vec<(String, String, Vec<u8>)>,
    indexes: Vec<(String, Vec<u8>)>,
    dedupe: Vec<(String, Vec<u8>, Vec<u8>)>,
    transactions: Vec<(String, Vec<u8>)>,
    revision: u64,
    logical_time: u64,
    transaction_generation: u64,
}

impl RaftSnapshotBuilder<TypeConfig> for SqliteStore {
    async fn build_snapshot(&mut self) -> Result<Snapshot<TypeConfig>> {
        let _timing = Timer::new("store.build_snapshot");
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        let rows = SnapshotRows {
            revision: meta(&transaction, "revision")?.unwrap_or(0),
            logical_time: meta(&transaction, "logical_time")?.unwrap_or(0),
            transaction_generation: meta(&transaction, "transaction_generation")?.unwrap_or(0),
            transactions: transaction
                .prepare("SELECT id,workspace FROM auth_transactions ORDER BY id")
                .map_err(storage)?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
                .map_err(storage)?
                .collect::<std::result::Result<_, _>>()
                .map_err(storage)?,
            group_identity: meta(&transaction, "group_identity")?
                .ok_or_else(|| storage("auth group identity missing"))?,
            records: transaction
                .prepare("SELECT model,id,body FROM auth_records ORDER BY model,id")
                .map_err(storage)?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))
                .map_err(storage)?
                .collect::<std::result::Result<_, _>>()
                .map_err(storage)?,
            indexes: transaction
                .prepare("SELECT model,fields FROM auth_indexes ORDER BY model,fields")
                .map_err(storage)?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
                .map_err(storage)?
                .collect::<std::result::Result<_, _>>()
                .map_err(storage)?,
            dedupe: transaction
                .prepare(
                    "SELECT operation_key,digest,outcome FROM auth_dedupe ORDER BY operation_key",
                )
                .map_err(storage)?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))
                .map_err(storage)?
                .collect::<std::result::Result<_, _>>()
                .map_err(storage)?,
        };
        let data = encode(&rows)?;
        let last_log_id = meta::<LogId<u64>>(&transaction, "applied")?;
        let last_membership = meta(&transaction, "membership")?.unwrap_or_default();
        let snapshot_id = blake3::hash(&data).to_hex().to_string();
        let snapshot_meta = SnapshotMeta { last_log_id, last_membership, snapshot_id };
        set_meta(&transaction, "snapshot_meta", &snapshot_meta)?;
        set_meta(&transaction, "snapshot_data", &data)?;
        transaction.commit().map_err(storage)?;
        Ok(Snapshot { meta: snapshot_meta, snapshot: Box::new(Cursor::new(data)) })
    }
}

impl RaftStateMachine<TypeConfig> for SqliteStore {
    type SnapshotBuilder = Self;
    async fn applied_state(
        &mut self,
    ) -> Result<(Option<LogId<u64>>, StoredMembership<u64, BasicNode>)> {
        let connection = self.lock()?;
        Ok((meta(&connection, "applied")?, meta(&connection, "membership")?.unwrap_or_default()))
    }
    async fn apply<I>(&mut self, entries: I) -> Result<Vec<Outcome>>
    where
        I: IntoIterator<Item = Entry<TypeConfig>> + Send,
        I::IntoIter: Send,
    {
        let _timing = Timer::new("store.apply");
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        let mut outcomes = Vec::new();
        for entry in entries {
            let outcome = match &entry.payload {
                EntryPayload::Blank => Outcome::Applied,
                EntryPayload::Normal(command) => execute(&transaction, command)?,
                EntryPayload::Membership(membership) => {
                    set_meta(
                        &transaction,
                        "membership",
                        &StoredMembership::new(Some(entry.log_id), membership.clone()),
                    )?;
                    Outcome::Applied
                }
            };
            set_meta(&transaction, "applied", &entry.log_id)?;
            outcomes.push(outcome);
        }
        transaction.commit().map_err(storage)?;
        Ok(outcomes)
    }
    async fn begin_receiving_snapshot(&mut self) -> Result<Box<Cursor<Vec<u8>>>> {
        Ok(Box::new(Cursor::new(Vec::new())))
    }
    async fn install_snapshot(
        &mut self,
        snapshot_meta: &SnapshotMeta<u64, BasicNode>,
        snapshot: Box<Cursor<Vec<u8>>>,
    ) -> Result<()> {
        let data = snapshot.into_inner();
        let rows: SnapshotRows = decode(&data)?;
        if meta::<String>(&*self.lock()?, "group_identity")?.as_deref()
            != Some(&rows.group_identity)
        {
            return Err(storage("snapshot auth group identity mismatch"));
        }
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        transaction
            .execute_batch(
                "DELETE FROM auth_records; DELETE FROM auth_indexes; DELETE FROM auth_dedupe; DELETE FROM auth_transactions;",
            )
            .map_err(storage)?;
        for (model, id, body) in rows.records {
            transaction
                .execute("INSERT INTO auth_records VALUES(?1,?2,?3)", params![model, id, body])
                .map_err(storage)?;
        }
        for (model, fields) in rows.indexes {
            transaction
                .execute("INSERT INTO auth_indexes VALUES(?1,?2)", params![model, fields])
                .map_err(storage)?;
        }
        for (key, digest, outcome) in rows.dedupe {
            transaction
                .execute("INSERT INTO auth_dedupe VALUES(?1,?2,?3)", params![key, digest, outcome])
                .map_err(storage)?;
        }
        for (id, workspace) in rows.transactions {
            transaction
                .execute("INSERT INTO auth_transactions VALUES(?1,?2)", params![id, workspace])
                .map_err(storage)?;
        }
        set_meta(&transaction, "revision", &rows.revision)?;
        set_meta(&transaction, "logical_time", &rows.logical_time)?;
        set_meta(&transaction, "transaction_generation", &rows.transaction_generation)?;
        if let Some(applied) = snapshot_meta.last_log_id {
            set_meta(&transaction, "applied", &applied)?;
        } else {
            transaction.execute("DELETE FROM metadata WHERE key='applied'", []).map_err(storage)?;
        }
        set_meta(&transaction, "membership", &snapshot_meta.last_membership)?;
        set_meta(&transaction, "snapshot_meta", snapshot_meta)?;
        set_meta(&transaction, "snapshot_data", &data)?;
        transaction.commit().map_err(storage)?;
        Ok(())
    }
    async fn get_current_snapshot(&mut self) -> Result<Option<Snapshot<TypeConfig>>> {
        let connection = self.lock()?;
        let Some(snapshot_meta) = meta(&connection, "snapshot_meta")? else {
            return Ok(None);
        };
        let data: Vec<u8> =
            meta(&connection, "snapshot_data")?.ok_or_else(|| storage("snapshot data missing"))?;
        Ok(Some(Snapshot { meta: snapshot_meta, snapshot: Box::new(Cursor::new(data)) }))
    }
    async fn get_snapshot_builder(&mut self) -> Self {
        self.clone()
    }
}

impl std::fmt::Debug for SqliteStore {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("AuthSqliteStore([redacted])")
    }
}
