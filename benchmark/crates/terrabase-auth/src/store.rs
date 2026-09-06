use std::fmt::Debug;
use std::io::Cursor;
use std::ops::RangeBounds;
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard};

use openraft::storage::{LogFlushed, RaftLogStorage, RaftStateMachine};
use openraft::{
    BasicNode, Entry, EntryPayload, LogId, LogState, RaftLogReader, RaftSnapshotBuilder, Snapshot,
    SnapshotMeta, StorageError, StorageIOError, StoredMembership, Vote,
};
use rusqlite::{Connection, OptionalExtension, Transaction, params};
use serde::{Deserialize, Serialize, de::DeserializeOwned};

use crate::{Action, Command, Outcome, Record, TypeConfig};

type Result<T> = std::result::Result<T, StorageError<u64>>;

/// SQLite-backed Raft log and state machine. No in-memory durable fallback.
#[derive(Clone)]
pub struct SqliteStore {
    connection: Arc<Mutex<Connection>>,
    keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
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
        Ok(Self { connection: Arc::new(Mutex::new(connection)), keys: None })
    }

    fn lock(&self) -> Result<MutexGuard<'_, Connection>> {
        if let Some(keys) = &self.keys {
            let lease = keys.current_lease().map_err(storage)?;
            if lease.is_expired() {
                return Err(storage("auth storage key lease expired"));
            }
        }
        self.connection.lock().map_err(|_| storage("auth SQLite lock poisoned"))
    }

    /// Read local applied state. Callers must first confirm Raft's linearizable barrier.
    /// This method does not itself grant authority to return the record to a client.
    pub(crate) fn read_record(&self, model: &str, id: &str) -> Result<Option<Record>> {
        read_record(&*self.lock()?, model, id)
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
    fields.iter().map(|field| record.get(field).filter(|value| !value.is_null()).cloned()).collect()
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
            if let Some(value) = index_value(record, index) {
                if index_value(&other, index).as_ref() == Some(&value) {
                    return Ok(true);
                }
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
        | Action::Find { model, .. } => model,
        _ => return Ok(invalid("nested transaction command is forbidden")),
    };
    if !valid_name(model) {
        return Ok(invalid("invalid model"));
    }
    match action {
        Action::Find { expected, offset, limit, .. } => {
            if *limit > 1000 || *offset > 1000 {
                return Ok(invalid("query page exceeds bound"));
            }
            let all = records(transaction, model)?;
            if all.len() > 1000 {
                return Ok(invalid("auth model query exceeds 1000 record bound"));
            }
            Ok(Outcome::Records {
                records: all
                    .into_iter()
                    .map(|(_, record)| record)
                    .filter(|record| matches(record, expected))
                    .skip(*offset as usize)
                    .take(*limit as usize)
                    .collect(),
            })
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
                if let Some(value) = index_value(&record, fields) {
                    if !seen.insert(encode(&value)?) {
                        return Ok(Outcome::UniqueConflict);
                    }
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
        _ => Ok(invalid("nested transaction command is forbidden")),
    }
}

impl RaftLogReader<TypeConfig> for SqliteStore {
    async fn try_get_log_entries<R: RangeBounds<u64> + Clone + Debug + Send>(
        &mut self,
        range: R,
    ) -> Result<Vec<Entry<TypeConfig>>> {
        let connection = self.lock()?;
        let mut statement =
            connection.prepare("SELECT entry FROM raft_log ORDER BY log_index").map_err(storage)?;
        let rows = statement.query_map([], |row| row.get::<_, Vec<u8>>(0)).map_err(storage)?;
        let mut entries = Vec::new();
        for row in rows {
            let entry: Entry<TypeConfig> = decode(&row.map_err(storage)?)?;
            if range.contains(&entry.log_id.index) {
                entries.push(entry);
            }
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
        set_meta(&*self.lock()?, "vote", vote)
    }
    async fn read_vote(&mut self) -> Result<Option<Vote<u64>>> {
        meta(&*self.lock()?, "vote")
    }
    async fn save_committed(&mut self, committed: Option<LogId<u64>>) -> Result<()> {
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
}

impl RaftSnapshotBuilder<TypeConfig> for SqliteStore {
    async fn build_snapshot(&mut self) -> Result<Snapshot<TypeConfig>> {
        let mut connection = self.lock()?;
        let transaction = connection.transaction().map_err(storage)?;
        let rows = SnapshotRows {
            revision: meta(&transaction, "revision")?.unwrap_or(0),
            logical_time: meta(&transaction, "logical_time")?.unwrap_or(0),
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
