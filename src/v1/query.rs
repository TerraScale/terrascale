//! Queries (query-v1.md): the filter grammar, sort, projection, cursors and
//! counts.

use reqwest::Method;
use serde::de::DeserializeOwned;
use serde::ser::{SerializeMap, SerializeSeq};
use serde::{Deserialize, Serialize, Serializer};
use serde_json::value::RawValue;

use super::client::Req;
use super::documents::{Collection, Document};
use super::errors::{Error, Result};
use super::types::{u64_string, Number};

/// A filter or sort path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Path {
    /// An RFC 6901 JSON Pointer, 1–16 segments.
    Pointer(String),
    /// `{"lower": pointer}`: compare after Unicode default case folding.
    Lower(String),
    /// The pseudo-path `"$id"` (the document ID).
    Id,
}

impl Path {
    /// `{"lower": pointer}`.
    #[must_use]
    pub fn lower(pointer: impl Into<String>) -> Self {
        Self::Lower(pointer.into())
    }
}

impl From<&str> for Path {
    fn from(s: &str) -> Self {
        if s == "$id" {
            Self::Id
        } else {
            Self::Pointer(s.to_owned())
        }
    }
}

impl From<String> for Path {
    fn from(s: String) -> Self {
        Self::from(s.as_str())
    }
}

impl Serialize for Path {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Self::Pointer(p) => s.serialize_str(p),
            Self::Id => s.serialize_str("$id"),
            Self::Lower(p) => {
                let mut m = s.serialize_map(Some(1))?;
                m.serialize_entry("lower", p)?;
                m.end()
            }
        }
    }
}

/// A JSON scalar operand: string, exact number, boolean or null.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Scalar {
    Null,
    Bool(bool),
    Number(Number),
    String(String),
}

impl Serialize for Scalar {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Self::Null => s.serialize_unit(),
            Self::Bool(b) => s.serialize_bool(*b),
            Self::Number(n) => n.serialize(s),
            Self::String(v) => s.serialize_str(v),
        }
    }
}

impl From<&str> for Scalar {
    fn from(v: &str) -> Self {
        Self::String(v.to_owned())
    }
}
impl From<String> for Scalar {
    fn from(v: String) -> Self {
        Self::String(v)
    }
}
impl From<&String> for Scalar {
    fn from(v: &String) -> Self {
        Self::String(v.clone())
    }
}
impl From<bool> for Scalar {
    fn from(v: bool) -> Self {
        Self::Bool(v)
    }
}
impl From<Number> for Scalar {
    fn from(v: Number) -> Self {
        Self::Number(v)
    }
}
impl From<uuid::Uuid> for Scalar {
    fn from(v: uuid::Uuid) -> Self {
        Self::String(v.to_string())
    }
}
impl<T: Into<Scalar>> From<Option<T>> for Scalar {
    fn from(v: Option<T>) -> Self {
        v.map_or(Self::Null, Into::into)
    }
}
macro_rules! scalar_from_int {
    ($($t:ty),*) => {$(
        impl From<$t> for Scalar {
            fn from(v: $t) -> Self {
                Self::Number(Number::from(v))
            }
        }
    )*};
}
scalar_from_int!(i8, i16, i32, i64, u8, u16, u32, u64, usize, isize);

/// A query-v1 filter expression. Shared by queries, transaction asserts and
/// key row filters.
#[derive(Debug, Clone)]
pub enum Filter {
    And(Vec<Filter>),
    Or(Vec<Filter>),
    Not(Box<Filter>),
    Eq(Path, Scalar),
    Ne(Path, Scalar),
    Lt(Path, Scalar),
    Lte(Path, Scalar),
    Gt(Path, Scalar),
    Gte(Path, Scalar),
    In(Path, Vec<Scalar>),
    Nin(Path, Vec<Scalar>),
    Exists(Path),
    Prefix(Path, String),
    Contains(Path, Scalar),
    /// A filter given as raw JSON grammar, sent unchanged.
    Raw(Box<RawValue>),
}

// RawValue has no PartialEq; compare the encoded grammar.
impl PartialEq for Filter {
    fn eq(&self, other: &Self) -> bool {
        serde_json::to_string(self).ok() == serde_json::to_string(other).ok()
    }
}
impl Eq for Filter {}

impl Filter {
    /// All children match (1–64).
    #[must_use]
    pub fn and(children: impl IntoIterator<Item = Filter>) -> Self {
        Self::And(children.into_iter().collect())
    }
    /// Any child matches (1–64).
    #[must_use]
    pub fn or(children: impl IntoIterator<Item = Filter>) -> Self {
        Self::Or(children.into_iter().collect())
    }
    /// Negation.
    #[allow(clippy::should_implement_trait)]
    #[must_use]
    pub fn not(f: Filter) -> Self {
        Self::Not(Box::new(f))
    }
    /// Equal.
    #[must_use]
    pub fn eq(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Eq(path.into(), value.into())
    }
    /// Not equal (also matches missing).
    #[must_use]
    pub fn ne(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Ne(path.into(), value.into())
    }
    /// Ordered `<` within the value's type.
    #[must_use]
    pub fn lt(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Lt(path.into(), value.into())
    }
    /// Ordered `<=` within the value's type.
    #[must_use]
    pub fn lte(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Lte(path.into(), value.into())
    }
    /// Ordered `>` within the value's type.
    #[must_use]
    pub fn gt(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Gt(path.into(), value.into())
    }
    /// Ordered `>=` within the value's type.
    #[must_use]
    pub fn gte(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Gte(path.into(), value.into())
    }
    /// One of 1–256 values.
    #[must_use]
    pub fn in_<V: Into<Scalar>>(
        path: impl Into<Path>,
        values: impl IntoIterator<Item = V>,
    ) -> Self {
        Self::In(path.into(), values.into_iter().map(Into::into).collect())
    }
    /// None of 1–256 values.
    #[must_use]
    pub fn nin<V: Into<Scalar>>(
        path: impl Into<Path>,
        values: impl IntoIterator<Item = V>,
    ) -> Self {
        Self::Nin(path.into(), values.into_iter().map(Into::into).collect())
    }
    /// The member is present (including `null`).
    #[must_use]
    pub fn exists(path: impl Into<Path>) -> Self {
        Self::Exists(path.into())
    }
    /// The string starts with `prefix`.
    #[must_use]
    pub fn prefix(path: impl Into<Path>, prefix: impl Into<String>) -> Self {
        Self::Prefix(path.into(), prefix.into())
    }
    /// The array contains the scalar.
    #[must_use]
    pub fn contains(path: impl Into<Path>, value: impl Into<Scalar>) -> Self {
        Self::Contains(path.into(), value.into())
    }

    /// Parses a filter written in the query-v1 JSON grammar. Only JSON
    /// syntax and object shape are checked locally; the server validates the
    /// grammar.
    pub fn from_json(json: &str) -> Result<Self> {
        let raw: Box<RawValue> = serde_json::from_str(json)
            .map_err(|e| Error::InvalidInput(format!("filter JSON: {e}")))?;
        if !raw.get().trim_start().starts_with('{') {
            return Err(Error::InvalidInput("a filter must be a JSON object".into()));
        }
        Ok(Self::Raw(raw))
    }

    /// Checks the local limits of the grammar (child and value counts, depth,
    /// ordered comparisons with null).
    pub fn validate(&self) -> Result<()> {
        self.validate_at(0)
    }

    fn validate_at(&self, depth: usize) -> Result<()> {
        let bad = |m: &str| Err(Error::InvalidInput(format!("filter: {m}")));
        if depth > 8 {
            return bad("nesting depth exceeds 8");
        }
        match self {
            Self::And(c) | Self::Or(c) => {
                if c.is_empty() || c.len() > 64 {
                    return bad("and/or need 1-64 children");
                }
                c.iter().try_for_each(|f| f.validate_at(depth + 1))
            }
            Self::Not(f) => f.validate_at(depth + 1),
            Self::Lt(_, Scalar::Null)
            | Self::Lte(_, Scalar::Null)
            | Self::Gt(_, Scalar::Null)
            | Self::Gte(_, Scalar::Null) => bad("ordered comparisons reject null"),
            Self::In(_, v) | Self::Nin(_, v) if v.is_empty() || v.len() > 256 => {
                bad("in/nin need 1-256 values")
            }
            _ => Ok(()),
        }
    }
}

struct Pair<'a, A, B>(&'a A, &'a B);

impl<A: Serialize, B: Serialize> Serialize for Pair<'_, A, B> {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        let mut seq = s.serialize_seq(Some(2))?;
        seq.serialize_element(self.0)?;
        seq.serialize_element(self.1)?;
        seq.end()
    }
}

impl Serialize for Filter {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        if let Self::Raw(raw) = self {
            return raw.serialize(s);
        }
        let mut m = s.serialize_map(Some(1))?;
        match self {
            Self::And(c) => m.serialize_entry("and", c)?,
            Self::Or(c) => m.serialize_entry("or", c)?,
            Self::Not(f) => m.serialize_entry("not", f)?,
            Self::Eq(p, v) => m.serialize_entry("eq", &Pair(p, v))?,
            Self::Ne(p, v) => m.serialize_entry("ne", &Pair(p, v))?,
            Self::Lt(p, v) => m.serialize_entry("lt", &Pair(p, v))?,
            Self::Lte(p, v) => m.serialize_entry("lte", &Pair(p, v))?,
            Self::Gt(p, v) => m.serialize_entry("gt", &Pair(p, v))?,
            Self::Gte(p, v) => m.serialize_entry("gte", &Pair(p, v))?,
            Self::In(p, v) => m.serialize_entry("in", &Pair(p, v))?,
            Self::Nin(p, v) => m.serialize_entry("nin", &Pair(p, v))?,
            Self::Exists(p) => m.serialize_entry("exists", p)?,
            Self::Prefix(p, v) => m.serialize_entry("prefix", &Pair(p, v))?,
            Self::Contains(p, v) => m.serialize_entry("contains", &Pair(p, v))?,
            Self::Raw(_) => unreachable!(),
        }
        m.end()
    }
}

/// Sort direction.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Order {
    Asc,
    Desc,
}

/// One sort key.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SortKey {
    pub path: Path,
    pub order: Order,
}

impl SortKey {
    /// Ascending.
    #[must_use]
    pub fn asc(path: impl Into<Path>) -> Self {
        Self {
            path: path.into(),
            order: Order::Asc,
        }
    }
    /// Descending.
    #[must_use]
    pub fn desc(path: impl Into<Path>) -> Self {
        Self {
            path: path.into(),
            order: Order::Desc,
        }
    }
}

/// Query explanation mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Explain {
    /// The plan without executing the query.
    Plan,
    /// Execute and add measured counters.
    Analyze,
}

/// A query-v1 request. Every member is optional.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Query {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub filter: Option<Filter>,
    /// At most 4 keys; results are finally ordered by `$id` ascending.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub sort: Vec<SortKey>,
    /// At most 64 paths, kept with their parent objects.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub project: Vec<String>,
    /// 1–1000; `None` uses the server default of 50.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub limit: Option<u32>,
    /// Continues from a previous page's `next_cursor`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cursor: Option<String>,
    /// Requests the total match count (first page only).
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub count: bool,
    /// Requests the plan (first page only).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub explain: Option<Explain>,
}

impl Query {
    /// An empty query (matches everything).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
    /// Sets the filter.
    #[must_use]
    pub fn filter(mut self, filter: Filter) -> Self {
        self.filter = Some(filter);
        self
    }
    /// Appends a sort key.
    #[must_use]
    pub fn sort(mut self, key: SortKey) -> Self {
        self.sort.push(key);
        self
    }
    /// Sets the projection.
    #[must_use]
    pub fn project<I: IntoIterator<Item = S>, S: Into<String>>(mut self, paths: I) -> Self {
        self.project = paths.into_iter().map(Into::into).collect();
        self
    }
    /// Sets the page size.
    #[must_use]
    pub fn limit(mut self, limit: u32) -> Self {
        self.limit = Some(limit);
        self
    }
    /// Sets the cursor.
    #[must_use]
    pub fn cursor(mut self, cursor: impl Into<String>) -> Self {
        self.cursor = Some(cursor.into());
        self
    }
    /// Requests the match count.
    #[must_use]
    pub fn count(mut self, count: bool) -> Self {
        self.count = count;
        self
    }
    /// Requests explanation.
    #[must_use]
    pub fn explain(mut self, mode: Explain) -> Self {
        self.explain = Some(mode);
        self
    }

    fn validate(&self) -> Result<()> {
        if let Some(f) = &self.filter {
            f.validate()?;
        }
        if self.sort.len() > 4 {
            return Err(Error::InvalidInput("sort has at most 4 keys".into()));
        }
        if self.project.len() > 64 {
            return Err(Error::InvalidInput("project has at most 64 paths".into()));
        }
        if matches!(self.limit, Some(l) if !(1..=1000).contains(&l)) {
            return Err(Error::InvalidInput("limit must be 1-1000".into()));
        }
        Ok(())
    }
}

/// A match count; `value` is a lower bound when `exact` is false.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub struct Count {
    #[serde(with = "u64_string")]
    pub value: u64,
    pub exact: bool,
}

/// One page of query results.
#[derive(Debug, Clone, Deserialize)]
#[serde(bound(deserialize = "T: DeserializeOwned"))]
pub struct QueryPage<T = Box<RawValue>> {
    pub items: Vec<Document<T>>,
    /// `None` after the last page. A page may be empty and still carry a
    /// cursor (bounded collection scans).
    #[serde(default)]
    pub next_cursor: Option<String>,
    /// Set when the query requested a count.
    #[serde(default)]
    pub count: Option<Count>,
    /// The raw explain object, when requested.
    #[serde(default)]
    pub explain: Option<Box<RawValue>>,
}

impl Collection {
    /// Runs one page of `query` (`POST …/collections/{collection}/query`).
    pub async fn query<T: DeserializeOwned>(&self, query: &Query) -> Result<QueryPage<T>> {
        self.check()?;
        query.validate()?;
        let mut req = Req::new(Method::POST, self.query_segments()).json(query)?;
        req.replay_safe = true;
        self.client().send(req).await?.decode()
    }

    /// Iterates over every page of `query`, starting at `query.cursor`.
    #[must_use]
    pub fn pages<T: DeserializeOwned>(&self, query: Query) -> Pages<T> {
        Pages {
            collection: self.clone(),
            query,
            done: false,
            _marker: std::marker::PhantomData,
        }
    }

    /// Collects every matching document across pages, failing with
    /// [`Error::InvalidInput`] once more than `max_items` were returned.
    pub async fn query_all<T: DeserializeOwned>(
        &self,
        query: Query,
        max_items: usize,
    ) -> Result<Vec<Document<T>>> {
        let mut pages = self.pages::<T>(query);
        let mut out = Vec::new();
        while let Some(page) = pages.next_page().await? {
            out.extend(page.items);
            if out.len() > max_items {
                return Err(Error::InvalidInput(format!(
                    "query returned more than {max_items} documents"
                )));
            }
        }
        Ok(out)
    }
}

/// A page iterator returned by [`Collection::pages`].
#[derive(Debug)]
pub struct Pages<T> {
    collection: Collection,
    query: Query,
    done: bool,
    _marker: std::marker::PhantomData<fn() -> T>,
}

impl<T: DeserializeOwned> Pages<T> {
    /// The next page, or `Ok(None)` after the last one. Count and explain are
    /// requested with the first page only. After an error the iterator can be
    /// polled again to retry the same page.
    pub async fn next_page(&mut self) -> Result<Option<QueryPage<T>>> {
        if self.done {
            return Ok(None);
        }
        let page = self.collection.query::<T>(&self.query).await?;
        match &page.next_cursor {
            Some(c) => {
                self.query.cursor = Some(c.clone());
                self.query.count = false;
                self.query.explain = None;
            }
            None => self.done = true,
        }
        Ok(Some(page))
    }
}
