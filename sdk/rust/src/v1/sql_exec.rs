//! Relational SQL over `POST .../sql/execute`: parameters, typed rows and one
//! transaction per request.

use std::collections::BTreeMap;

use reqwest::{Method, StatusCode};
use serde::de::{DeserializeOwned, Error as _};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use super::client::Req;
use super::documents::Database;
use super::errors::{Error, Result};
use super::ids::check_database_selector;

/// Maximum statements in one request.
pub const MAX_SQL_STATEMENTS: usize = 256;
/// Maximum UTF-8 bytes of one statement's SQL text.
pub const MAX_SQL_BYTES: usize = 16 << 10;

/// A SQLite value. Booleans bind as `1`/`0`. Integers travel as JSON numbers,
/// so JavaScript peers lose precision beyond 2^53; this client keeps `i64`.
#[derive(Debug, Clone, PartialEq)]
pub enum SqlValue {
    Null,
    Integer(i64),
    Real(f64),
    Text(String),
}

impl Serialize for SqlValue {
    fn serialize<S: Serializer>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Self::Null => serializer.serialize_unit(),
            Self::Integer(value) => serializer.serialize_i64(*value),
            Self::Real(value) => serializer.serialize_f64(*value),
            Self::Text(value) => serializer.serialize_str(value),
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
            _ => return Err(D::Error::custom("SQL values are scalars")),
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
    /// Decodes each row as an object keyed by column name (the last duplicate wins).
    pub fn decode_rows<T: DeserializeOwned>(&self) -> Result<Vec<T>> {
        self.rows
            .iter()
            .map(|row| {
                let object = self
                    .columns
                    .iter()
                    .zip(row)
                    .map(|(column, value)| {
                        let value = serde_json::to_value(value)
                            .map_err(|error| Error::Decode(error.to_string()))?;
                        Ok((column.name.clone(), value))
                    })
                    .collect::<Result<serde_json::Map<_, _>>>()?;
                serde_json::from_value(object.into())
                    .map_err(|error| Error::Decode(format!("SQL row: {error}")))
            })
            .collect()
    }
}

/// Relational SQL on one database. Every request is one transaction:
/// all statements commit or none do. `BEGIN`/`COMMIT`/`ROLLBACK` are refused.
///
/// ```no_run
/// # async fn demo(client: terrascale::v1::Client) -> terrascale::v1::Result<()> {
/// use terrascale::v1::{SqlStatement, SqlValue};
/// #[derive(serde::Deserialize)]
/// struct User { id: i64, name: String }
///
/// let sql = client.database("main").sql();
/// let inserted = sql.execute("INSERT INTO users(name) VALUES (?)", &["Ada".into()]).await?;
/// let users: Vec<User> = sql
///     .query("SELECT id, name FROM users WHERE id = ?", &[inserted.last_insert_rowid.into()])
///     .await?;
/// let results = sql.batch(vec![
///     SqlStatement::new("UPDATE users SET name = :name WHERE id = :id").named("name", "Grace").named("id", 1),
///     SqlStatement::new("SELECT count(*) FROM users"),
/// ]).await?;
/// assert!(matches!(results[1].rows[0][0], SqlValue::Integer(_)));
/// # let _ = users; Ok(()) }
/// ```
#[derive(Debug, Clone)]
pub struct Sql {
    db: Database,
}

impl Database {
    /// The relational SQL API for this database. Performs no I/O.
    #[must_use]
    pub fn sql(&self) -> Sql {
        Sql { db: self.clone() }
    }
}

impl Sql {
    /// Runs one statement with positional arguments.
    pub async fn execute(&self, sql: &str, args: &[SqlValue]) -> Result<SqlStatementResult> {
        self.run(SqlStatement::new(sql).args(args.iter().cloned()))
            .await
    }

    /// Runs one statement and decodes each row by column name, for example
    /// into a `#[derive(Deserialize)]` struct or a `HashMap<String, SqlValue>`.
    pub async fn query<T: DeserializeOwned>(&self, sql: &str, args: &[SqlValue]) -> Result<Vec<T>> {
        self.execute(sql, args).await?.decode_rows()
    }

    /// Runs one statement, including named arguments.
    pub async fn run(&self, statement: impl Into<SqlStatement>) -> Result<SqlStatementResult> {
        let mut results = self.batch(vec![statement.into()]).await?;
        Ok(results.remove(0))
    }

    /// Runs 1..=256 statements in one transaction; one result per statement.
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
        let request = Req::new(
            Method::POST,
            vec!["v1", "databases", self.db.name(), "sql", "execute"],
        )
        .json(&serde_json::json!({"statements": statements}))?;
        let response = self.db.client().send_project(request).await?;

        #[derive(Deserialize)]
        struct Envelope {
            results: Vec<SqlStatementResult>,
        }
        let Envelope { results } = response.decode_status(&[StatusCode::OK])?;
        if results.len() != statements.len()
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
