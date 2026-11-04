//! SQL over `POST /v1/projects/{project}/databases/{database}/sql/execute`:
//! one request, one transaction.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use base64::engine::general_purpose::STANDARD;
use base64::Engine as _;
use reqwest::{Method, StatusCode};
use serde::de::{DeserializeOwned, Error as _};
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use super::client::{backoff, Req, RetryPolicy};
use super::documents::Database;
use super::errors::{Error, Result};
use super::ids::check_database_selector;

/// Maximum statements in one request.
pub const MAX_SQL_STATEMENTS: usize = 256;
/// Maximum UTF-8 bytes of one statement's SQL text.
pub const MAX_SQL_BYTES: usize = 16 << 10;

const CONFLICT_RETRIES: u32 = 2;

/// TerraScale SQL error codes, the same on REST, the WebSocket and the
/// PostgreSQL wire. The SQLSTATE and HTTP status of each code are listed in
/// `sdk/dotnet/contracts/terrabase/sql/error-codes.json`.
pub mod sql_error_codes {
    pub const SQL_ERROR: &str = "sql_error";
    pub const SYNTAX_ERROR: &str = "syntax_error";
    pub const UNDEFINED_TABLE: &str = "undefined_table";
    pub const UNDEFINED_COLUMN: &str = "undefined_column";
    pub const DUPLICATE_OBJECT: &str = "duplicate_object";
    pub const PRIMARY_KEY_VIOLATION: &str = "primary_key_violation";
    pub const UNIQUE_VIOLATION: &str = "unique_violation";
    pub const NOT_NULL_VIOLATION: &str = "not_null_violation";
    pub const CHECK_VIOLATION: &str = "check_violation";
    pub const FOREIGN_KEY_VIOLATION: &str = "foreign_key_violation";
    pub const CONSTRAINT_VIOLATION: &str = "constraint_violation";
    pub const FORBIDDEN_SCOPE: &str = "forbidden_scope";
    pub const SERIALIZATION_CONFLICT: &str = "serialization_conflict";
    pub const TOO_LARGE: &str = "too_large";
    pub const QUERY_CANCELED: &str = "query_canceled";
    pub const TRANSACTION_ROLLED_BACK: &str = "transaction_rolled_back";
    pub const TRANSACTION_ABORTED: &str = "transaction_aborted";
    pub const READ_ONLY_TRANSACTION: &str = "read_only_transaction";
    pub const QUOTA_EXCEEDED: &str = "quota_exceeded";
    pub const INVALID_REQUEST: &str = "invalid_request";
    pub const TRANSACTION_STATE: &str = "transaction_state";
    pub const TENANT_BUSY: &str = "tenant_busy";
    pub const UNAVAILABLE: &str = "unavailable";
    pub const UNAUTHORIZED: &str = "unauthorized";
    pub const INTERNAL_ERROR: &str = "internal_error";
}

/// A SQL value. Booleans bind as `1`/`0`; integers stay exact `i64`.
#[derive(Debug, Clone, PartialEq)]
pub enum SqlValue {
    Null,
    Integer(i64),
    Real(f64),
    Text(String),
    Blob(Vec<u8>),
}

impl Serialize for SqlValue {
    fn serialize<S: Serializer>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Self::Null => serializer.serialize_unit(),
            Self::Integer(value) => serializer.serialize_i64(*value),
            Self::Real(value) => serializer.serialize_f64(*value),
            Self::Text(value) => serializer.serialize_str(value),
            Self::Blob(value) => {
                let mut map = serializer.serialize_map(Some(1))?;
                map.serialize_entry("base64", &STANDARD.encode(value))?;
                map.end()
            }
        }
    }
}

impl<'de> Deserialize<'de> for SqlValue {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> std::result::Result<Self, D::Error> {
        Ok(match serde_json::Value::deserialize(deserializer)? {
            serde_json::Value::Null => Self::Null,
            serde_json::Value::Bool(value) => Self::Integer(value.into()),
            serde_json::Value::String(value) => Self::Text(value),
            serde_json::Value::Number(number) => match number.as_i64() {
                Some(value) => Self::Integer(value),
                None if number.is_f64() => Self::Real(number.as_f64().unwrap_or(f64::NAN)),
                None => return Err(D::Error::custom("SQL integer exceeds i64")),
            },
            serde_json::Value::Object(object) => match object.get("base64") {
                Some(serde_json::Value::String(encoded)) if object.len() == 1 => Self::Blob(
                    STANDARD
                        .decode(encoded)
                        .map_err(|_| D::Error::custom("SQL blob is not base64"))?,
                ),
                _ => return Err(D::Error::custom("SQL blob is {\"base64\":…}")),
            },
            serde_json::Value::Array(_) => {
                return Err(D::Error::custom("SQL values are scalars or blobs"))
            }
        })
    }
}

macro_rules! sql_value_from {
    ($variant:ident: $($ty:ty),+) => {$(
        impl From<$ty> for SqlValue {
            fn from(value: $ty) -> Self {
                Self::$variant(value.into())
            }
        }
    )+};
}
sql_value_from!(Integer: i64, i32, i16, i8, u32, u16, u8);
sql_value_from!(Real: f64, f32);
sql_value_from!(Text: String, &str);
sql_value_from!(Blob: Vec<u8>, &[u8]);

impl From<bool> for SqlValue {
    fn from(value: bool) -> Self {
        Self::Integer(value.into())
    }
}

impl<T: Into<SqlValue>> From<Option<T>> for SqlValue {
    fn from(value: Option<T>) -> Self {
        value.map_or(Self::Null, Into::into)
    }
}

/// One statement with positional `?` arguments and/or named arguments.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct SqlStatement {
    sql: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    args: Vec<SqlValue>,
    #[serde(skip_serializing_if = "BTreeMap::is_empty")]
    named: BTreeMap<String, SqlValue>,
}

impl SqlStatement {
    #[must_use]
    pub fn new(sql: impl Into<String>) -> Self {
        Self {
            sql: sql.into(),
            args: Vec::new(),
            named: BTreeMap::new(),
        }
    }

    /// Appends positional arguments.
    #[must_use]
    pub fn args(mut self, args: impl IntoIterator<Item = impl Into<SqlValue>>) -> Self {
        self.args.extend(args.into_iter().map(Into::into));
        self
    }

    /// Binds `:name`, `@name` or `$name`; pass `name` without its prefix.
    #[must_use]
    pub fn named(mut self, name: impl Into<String>, value: impl Into<SqlValue>) -> Self {
        self.named.insert(name.into(), value.into());
        self
    }

    fn validate(&self, index: usize) -> Result<()> {
        let invalid =
            |message: String| Err(Error::InvalidInput(format!("statement {index}: {message}")));
        if self.sql.trim().is_empty() || self.sql.len() > MAX_SQL_BYTES {
            return invalid(format!("SQL must be 1..={MAX_SQL_BYTES} bytes"));
        }
        if let Some(name) = self
            .named
            .keys()
            .find(|name| name.is_empty() || name.starts_with([':', '@', '$']))
        {
            return invalid(format!("named argument {name:?} must omit its prefix"));
        }
        if self
            .args
            .iter()
            .chain(self.named.values())
            .any(|value| matches!(value, SqlValue::Real(real) if !real.is_finite()))
        {
            return invalid("REAL arguments must be finite".into());
        }
        Ok(())
    }
}

impl From<&str> for SqlStatement {
    fn from(sql: &str) -> Self {
        Self::new(sql)
    }
}

impl From<String> for SqlStatement {
    fn from(sql: String) -> Self {
        Self::new(sql)
    }
}

/// A result column; `decltype` is the declared type, absent for expressions.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SqlColumn {
    pub name: String,
    pub decltype: Option<String>,
}

/// The outcome of one statement.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SqlStatementResult {
    pub columns: Vec<SqlColumn>,
    pub rows: Vec<Vec<SqlValue>>,
    pub rows_affected: u64,
    pub last_insert_rowid: Option<i64>,
}

impl SqlStatementResult {
    /// The rows, readable by ordinal or column name.
    #[must_use]
    pub fn records(&self) -> Vec<SqlRecord> {
        let columns = Arc::new(Columns::new(&self.columns));
        self.rows
            .iter()
            .map(|row| SqlRecord {
                columns: columns.clone(),
                values: row.clone(),
            })
            .collect()
    }

    /// Maps each row, as an object keyed by column name with blobs as base64
    /// strings, to `T`. A repeated column name keeps its first value.
    pub fn decode_rows<T: DeserializeOwned>(&self) -> Result<Vec<T>> {
        self.rows
            .iter()
            .map(|row| {
                let mut object = serde_json::Map::with_capacity(row.len());
                for (column, value) in self.columns.iter().zip(row) {
                    if !object.contains_key(&column.name) {
                        let value = match value {
                            SqlValue::Null => serde_json::Value::Null,
                            SqlValue::Integer(value) => (*value).into(),
                            SqlValue::Real(value) => (*value).into(),
                            SqlValue::Text(value) => value.clone().into(),
                            SqlValue::Blob(value) => STANDARD.encode(value).into(),
                        };
                        object.insert(column.name.clone(), value);
                    }
                }
                serde_json::from_value(object.into())
                    .map_err(|error| Error::Decode(format!("SQL row: {error}")))
            })
            .collect()
    }
}

#[derive(Debug)]
struct Columns {
    columns: Vec<SqlColumn>,
    ordinals: HashMap<String, usize>,
}

impl Columns {
    fn new(columns: &[SqlColumn]) -> Self {
        let mut ordinals = HashMap::with_capacity(columns.len());
        for (index, column) in columns.iter().enumerate() {
            ordinals.entry(column.name.clone()).or_insert(index);
        }
        Self {
            columns: columns.to_vec(),
            ordinals,
        }
    }
}

/// A column selector: a zero-based ordinal or a column name. A repeated
/// column name resolves to its first ordinal.
pub trait SqlColumnIndex {
    fn ordinal(&self, columns: &[SqlColumn], ordinals: &HashMap<String, usize>) -> Option<usize>;
}

impl SqlColumnIndex for usize {
    fn ordinal(&self, columns: &[SqlColumn], _: &HashMap<String, usize>) -> Option<usize> {
        (*self < columns.len()).then_some(*self)
    }
}

impl SqlColumnIndex for &str {
    fn ordinal(&self, _: &[SqlColumn], ordinals: &HashMap<String, usize>) -> Option<usize> {
        ordinals.get(*self).copied()
    }
}

/// One row of a statement result.
#[derive(Debug, Clone)]
pub struct SqlRecord {
    columns: Arc<Columns>,
    values: Vec<SqlValue>,
}

impl SqlRecord {
    #[must_use]
    pub fn columns(&self) -> &[SqlColumn] {
        &self.columns.columns
    }

    #[must_use]
    pub fn values(&self) -> &[SqlValue] {
        &self.values
    }

    /// The value of a column.
    pub fn get(&self, column: impl SqlColumnIndex) -> Result<&SqlValue> {
        column
            .ordinal(&self.columns.columns, &self.columns.ordinals)
            .and_then(|ordinal| self.values.get(ordinal))
            .ok_or_else(|| Error::InvalidInput("no such SQL column".into()))
    }

    pub fn is_null(&self, column: impl SqlColumnIndex) -> Result<bool> {
        Ok(matches!(self.get(column)?, SqlValue::Null))
    }

    pub fn get_i64(&self, column: impl SqlColumnIndex) -> Result<i64> {
        match self.get(column)? {
            SqlValue::Integer(value) => Ok(*value),
            other => Err(mismatch("an integer", other)),
        }
    }

    pub fn get_f64(&self, column: impl SqlColumnIndex) -> Result<f64> {
        match self.get(column)? {
            SqlValue::Real(value) => Ok(*value),
            SqlValue::Integer(value) => Ok(*value as f64),
            other => Err(mismatch("a number", other)),
        }
    }

    pub fn get_bool(&self, column: impl SqlColumnIndex) -> Result<bool> {
        Ok(self.get_i64(column)? != 0)
    }

    pub fn get_str(&self, column: impl SqlColumnIndex) -> Result<&str> {
        match self.get(column)? {
            SqlValue::Text(value) => Ok(value),
            other => Err(mismatch("text", other)),
        }
    }

    pub fn get_bytes(&self, column: impl SqlColumnIndex) -> Result<&[u8]> {
        match self.get(column)? {
            SqlValue::Blob(value) => Ok(value),
            other => Err(mismatch("a blob", other)),
        }
    }
}

fn mismatch(expected: &str, actual: &SqlValue) -> Error {
    Error::Decode(format!("SQL value is not {expected}: {actual:?}"))
}

/// SQL on one database. Every call is one request and one transaction: all
/// statements commit or none do. `BEGIN`/`COMMIT`/`ROLLBACK` are refused.
/// A `409 serialization_conflict` rolled back and is resent at most twice.
///
/// ```no_run
/// # async fn demo(client: terrascale::v1::Client) -> terrascale::v1::Result<()> {
/// use terrascale::v1::SqlStatement;
/// #[derive(serde::Deserialize)]
/// struct User { id: i64, name: String }
///
/// let db = client.database("main").sql();
/// db.execute("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY, name TEXT)", &[]).await?;
/// let inserted = db.execute("INSERT INTO users(name) VALUES (?)", &["Ada".into()]).await?;
/// let rows = db.query("SELECT id, name FROM users", &[]).await?;
/// let name = rows[0].get_str("name")?;
/// let users: Vec<User> = db
///     .query_as("SELECT id, name FROM users WHERE id = ?", &[inserted.last_insert_rowid.into()])
///     .await?;
/// db.batch(vec![
///     SqlStatement::new("UPDATE users SET name = :name WHERE id = :id").named("name", "Grace").named("id", 1),
///     SqlStatement::new("DELETE FROM users WHERE id > ?").args([10]),
/// ]).await?;
/// # let _ = (name, users); Ok(()) }
/// ```
#[derive(Debug, Clone)]
pub struct Sql {
    db: Database,
}

impl Database {
    /// SQL on this database. Performs no I/O.
    #[must_use]
    pub fn sql(&self) -> Sql {
        Sql { db: self.clone() }
    }
}

impl Sql {
    /// Runs one statement with positional arguments.
    pub async fn execute(&self, sql: &str, args: &[SqlValue]) -> Result<SqlStatementResult> {
        self.execute_statement(SqlStatement::new(sql).args(args.iter().cloned()))
            .await
    }

    /// Runs one statement with named arguments.
    pub async fn execute_named<K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<SqlStatementResult> {
        self.execute_statement(with_named(sql, named)).await
    }

    /// Runs one statement.
    pub async fn execute_statement(
        &self,
        statement: impl Into<SqlStatement>,
    ) -> Result<SqlStatementResult> {
        let mut results = self.batch(vec![statement.into()]).await?;
        Ok(results.remove(0))
    }

    /// Runs one statement and returns its rows.
    pub async fn query(&self, sql: &str, args: &[SqlValue]) -> Result<Vec<SqlRecord>> {
        Ok(self.execute(sql, args).await?.records())
    }

    /// Runs one statement with named arguments and returns its rows.
    pub async fn query_named<K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<Vec<SqlRecord>> {
        Ok(self.execute_named(sql, named).await?.records())
    }

    /// Runs one statement and maps its rows to `T`; see
    /// [`SqlStatementResult::decode_rows`].
    pub async fn query_as<T: DeserializeOwned>(
        &self,
        sql: &str,
        args: &[SqlValue],
    ) -> Result<Vec<T>> {
        self.execute(sql, args).await?.decode_rows()
    }

    /// Runs one statement with named arguments and maps its rows to `T`.
    pub async fn query_as_named<T: DeserializeOwned, K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<Vec<T>> {
        self.execute_named(sql, named).await?.decode_rows()
    }

    /// Runs 1..=256 statements as one transaction, all or nothing; one
    /// result per statement in order. A refused statement rolls everything
    /// back and returns [`Error::Sql`] naming it.
    pub async fn batch(&self, statements: Vec<SqlStatement>) -> Result<Vec<SqlStatementResult>> {
        check_database_selector(self.db.name())?;
        if statements.is_empty() || statements.len() > MAX_SQL_STATEMENTS {
            return Err(Error::InvalidInput(format!(
                "a SQL batch holds 1..={MAX_SQL_STATEMENTS} statements"
            )));
        }
        for (index, statement) in statements.iter().enumerate() {
            statement.validate(index)?;
        }
        let body = serde_json::json!({ "statements": statements });
        let mut attempt = 0;
        loop {
            match self.send(&body, statements.len()).await {
                Err(error)
                    if attempt < CONFLICT_RETRIES
                        && error.status() == Some(409)
                        && error.sql().is_some_and(|sql| {
                            sql.code == sql_error_codes::SERIALIZATION_CONFLICT
                        }) =>
                {
                    attempt += 1;
                    tokio::time::sleep(backoff(&RetryPolicy::default(), attempt)).await;
                }
                result => return result,
            }
        }
    }

    async fn send(
        &self,
        body: &serde_json::Value,
        expected: usize,
    ) -> Result<Vec<SqlStatementResult>> {
        let request = Req::new(
            Method::POST,
            vec!["v1", "databases", self.db.name(), "sql", "execute"],
        )
        .json(body)?;
        let response = self.db.client().send_project(request).await?;

        #[derive(Deserialize)]
        struct Envelope {
            results: Vec<SqlStatementResult>,
        }
        let Envelope { results } = response.decode_status(&[StatusCode::OK])?;
        if results.len() != expected
            || results.iter().any(|result| {
                result
                    .rows
                    .iter()
                    .any(|row| row.len() != result.columns.len())
            })
        {
            return Err(response.invalid("SQL results do not match the statements".into()));
        }
        Ok(results)
    }
}

fn with_named<K: Into<String>, V: Into<SqlValue>>(
    sql: &str,
    named: impl IntoIterator<Item = (K, V)>,
) -> SqlStatement {
    named
        .into_iter()
        .fold(SqlStatement::new(sql), |statement, (name, value)| {
            statement.named(name, value)
        })
}
