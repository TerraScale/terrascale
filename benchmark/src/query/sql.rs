//! SQL text validation for the read-only query endpoint (D-78/D-54).
//!
//! SQL is the only query language. This module is the fail-closed front end
//! the SQL planner (track item **E-4**) replaces with the full allow-list
//! parser, collection-as-table model, live-subset classification, and
//! hypothetical-index planning.
//!
//! What this stub owns:
//!
//! - [`SqlQueryText`] validation through [`validate_sql_text`]: non-empty,
//!   bounded by [`MAX_SQL_TEXT_BYTES`](crate::operations::MAX_SQL_TEXT_BYTES),
//!   and shaped as read-only SQL (`SELECT`, `WITH`, or `EXPLAIN` first
//!   keyword; no DDL, DML, pragma, attachment, or multi-statement text).
//! - [`SqlParameters`]: named, typed, out-of-band bindings. Values are data,
//!   never parsed text, so a binding can never inject a clause.
//! - [`SqlError`] / [`SqlErrorClass`]: the typed refusals the endpoint and
//!   the planner share.
//!
//! What this stub does NOT do (E-4): parse SQL into a plan. Authorization
//! compilation and index derivation operate on the typed AST
//! ([`QuerySpec`](super::QuerySpec) / `NormalizedQuery`), which is the SQL
//! AST surface until E-4 lands its parser. Compiling [`SqlQueryText`]
//! through [`QueryStore::compile`](super::QueryStore::compile) fails closed
//! with [`QueryError::InvalidSpec`](super::QueryError::InvalidSpec).

use crate::types::DocumentValue;

/// The SQL dialect marker this front end accepts.
///
/// There is exactly one query language, so there is no version negotiation:
/// this constant exists so error messages and the endpoint can name what was
/// validated.
pub const SQL_DIALECT: &str = "sql/sqlite-readonly";

/// The fail-closed refusal classes of the SQL front end.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SqlErrorClass {
    /// The text does not parse as read-only SQL for the accepted dialect.
    ParseError,
    /// The statement is outside the read-only allow-list (`SELECT`, `WITH`,
    /// `EXPLAIN`).
    UnsupportedStatement,
    /// The text carries a forbidden keyword (DDL, DML, pragma, attachment,
    /// or any non-deterministic escape hatch).
    ForbiddenKeyword,
    /// A `$name` reference has no binding.
    UnboundParameter,
    /// A binding is never referenced.
    UnexpectedParameter,
    /// A paging bound violation.
    InvalidBound,
    /// An identifier fails the shared identifier discipline.
    InvalidIdentifier,
}

/// One typed SQL front-end refusal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SqlError {
    class: SqlErrorClass,
    position: Option<usize>,
    construct: Option<String>,
}

impl SqlError {
    /// Returns the refusal class.
    #[must_use]
    pub const fn class(&self) -> SqlErrorClass {
        self.class
    }

    /// Returns the byte offset the refusal points at, when one applies.
    #[must_use]
    pub const fn position(&self) -> Option<usize> {
        self.position
    }

    /// Returns the offending construct, when the class names one.
    #[must_use]
    pub fn construct(&self) -> Option<&str> {
        self.construct.as_deref()
    }

    /// Builds an unpositioned refusal over one construct name.
    pub(super) fn binding(class: SqlErrorClass, name: &str) -> Self {
        Self { class, position: None, construct: Some(name.to_owned()) }
    }

    /// Builds a refusal at one byte offset.
    pub(super) fn at(class: SqlErrorClass, position: usize, construct: &str) -> Self {
        Self { class, position: Some(position), construct: Some(construct.to_owned()) }
    }

    /// Builds a refusal without a position or construct.
    pub(super) fn plain(class: SqlErrorClass) -> Self {
        Self { class, position: None, construct: None }
    }
}

/// The named, typed, out-of-band parameter bindings of one SQL request.
///
/// Parameter values are data, never parsed text: a binding can never inject
/// a clause or change the statement shape.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SqlParameters {
    values: std::collections::BTreeMap<String, DocumentValue>,
}

impl SqlParameters {
    /// Creates an empty binding set.
    #[must_use]
    pub const fn new() -> Self {
        Self { values: std::collections::BTreeMap::new() }
    }

    /// Creates a binding set from name-value pairs.
    ///
    /// # Errors
    ///
    /// Returns [`SqlErrorClass::InvalidIdentifier`] for one invalid name.
    pub fn try_new(entries: &[(&str, DocumentValue)]) -> Result<Self, SqlError> {
        let mut parameters = Self::new();
        for (name, value) in entries {
            parameters.insert(name, value.clone())?;
        }
        Ok(parameters)
    }

    /// Adds or replaces one binding.
    ///
    /// # Errors
    ///
    /// Returns [`SqlErrorClass::InvalidIdentifier`] for an invalid name.
    pub fn insert(&mut self, name: &str, value: DocumentValue) -> Result<(), SqlError> {
        if !valid_identifier(name) {
            return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, name));
        }
        self.values.insert(name.to_owned(), value);
        Ok(())
    }

    /// Returns one binding, when present.
    #[must_use]
    pub fn get(&self, name: &str) -> Option<&DocumentValue> {
        self.values.get(name)
    }

    /// Reports whether one binding is present.
    #[must_use]
    pub fn contains(&self, name: &str) -> bool {
        self.values.contains_key(name)
    }

    /// Returns the bound names in sorted order.
    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.values.keys().map(String::as_str)
    }

    /// Returns the number of bindings.
    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// Reports whether the set is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

/// Reports whether one name passes the shared identifier discipline.
pub(super) fn valid_identifier(name: &str) -> bool {
    use crate::query::spec::MAX_FIELD_BYTES;
    !name.is_empty()
        && name.len() <= MAX_FIELD_BYTES
        && !name.chars().any(char::is_control)
        && name == name.trim()
}

/// Validates one SQL text through the read-only allow-list.
///
/// The text must be non-empty, fit the shared bound, open with `SELECT`,
/// `WITH`, or `EXPLAIN` (case-insensitive, leading comments and whitespace
/// skipped), contain no statement separator, and name no forbidden keyword
/// (DDL, DML, pragma, attachment, or vacuum). Anything else fails closed.
///
/// # Errors
///
/// Returns one typed [`SqlError`]; see [`SqlErrorClass`] for the classes.
pub fn validate_text(text: &str, max_bytes: usize) -> Result<(), SqlError> {
    if text.trim().is_empty() {
        return Err(SqlError::plain(SqlErrorClass::ParseError));
    }
    if text.len() > max_bytes {
        return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement));
    }
    let stripped = strip_leading_comments(text);
    let first = first_keyword(stripped);
    match first.to_ascii_lowercase().as_str() {
        "select" | "with" | "explain" => {}
        _ => return Err(SqlError::at(SqlErrorClass::UnsupportedStatement, 0, &first)),
    }
    if stripped.contains(';') {
        return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement));
    }
    for keyword in FORBIDDEN_KEYWORDS {
        if contains_keyword(stripped, keyword) {
            return Err(SqlError::at(
                SqlErrorClass::ForbiddenKeyword,
                0,
                keyword,
            ));
        }
    }
    Ok(())
}

/// Keywords the read-only endpoint never accepts (D-54 allow-list).
const FORBIDDEN_KEYWORDS: &[&str] = &[
    "insert", "update", "delete", "merge", "replace", "upsert", "create", "drop", "alter",
    "truncate", "attach", "detach", "pragma", "vacuum", "reindex", "analyze", "grant", "revoke",
    "begin", "commit", "rollback", "savepoint", "release",
];

/// Skips leading whitespace and `--` / `/* */` comments.
fn strip_leading_comments(text: &str) -> &str {
    let mut rest = text.trim_start();
    loop {
        if let Some(after) = rest.strip_prefix("--") {
            let end = after.find('\n').map_or(after.len(), |index| index + 1);
            rest = after[end..].trim_start();
        } else if let Some(after) = rest.strip_prefix("/*") {
            match after.find("*/") {
                Some(end) => rest = after[end + 2..].trim_start(),
                None => return "",
            }
        } else {
            return rest;
        }
    }
}

/// Returns the first ASCII-alphanumeric keyword of the text.
fn first_keyword(text: &str) -> String {
    text.chars()
        .skip_while(|character| !character.is_ascii_alphabetic())
        .take_while(|character| character.is_ascii_alphanumeric() || *character == '_')
        .collect()
}

/// Reports whether the text names a whole keyword outside string literals.
fn contains_keyword(text: &str, keyword: &str) -> bool {
    let bytes = text.as_bytes();
    let needle = keyword.as_bytes();
    let mut index = 0;
    let mut in_string = false;
    while index < bytes.len() {
        let byte = bytes[index];
        if in_string {
            if byte == b'\'' {
                if bytes.get(index + 1) == Some(&b'\'') {
                    index += 2;
                    continue;
                }
                in_string = false;
            }
            index += 1;
            continue;
        }
        if byte == b'\'' {
            in_string = true;
            index += 1;
            continue;
        }
        if byte == b'"' || byte == b'`' {
            let quote = byte;
            index += 1;
            while index < bytes.len() && bytes[index] != quote {
                index += 1;
            }
            index += 1;
            continue;
        }
        if byte.is_ascii_alphabetic() || byte == b'_' {
            let start = index;
            while index < bytes.len()
                && (bytes[index].is_ascii_alphanumeric() || bytes[index] == b'_')
            {
                index += 1;
            }
            if text[start..index].eq_ignore_ascii_case(keyword)
                && !needle.is_empty()
            {
                return true;
            }
            continue;
        }
        index += 1;
    }
    false
}
