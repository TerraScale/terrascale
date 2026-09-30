//! Scoped native document reads, lossless sibling state, and scalar queries.

use serde::{de, Deserialize, Deserializer, Serialize, Serializer};

use crate::client::{native_deadline, SharedClient};
use crate::discovery::NativeRequirement;
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};
use crate::values::{
    invalid, CollectionId, DatabaseId, DecimalU64, Digest, DocumentId, Fields, Incarnation,
    ReplicaId, TenantId, Value,
};

pub const MAX_CONTEXT_ENTRIES: usize = 1024;
pub const MAX_QUERY_ROWS: usize = 128;
pub const MAX_CURSOR_CHARS: usize = 1024;

/// All native operations are scoped to one tenant, database and shard.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DatabaseScope {
    pub tenant: TenantId,
    pub database: DatabaseId,
    pub shard: DecimalU64,
}

impl DatabaseScope {
    #[must_use]
    pub const fn new(tenant: TenantId, database: DatabaseId, shard: u64) -> Self {
        Self {
            tenant,
            database,
            shard: DecimalU64(shard),
        }
    }

    pub(crate) fn path(&self) -> String {
        format!(
            "/v1/tenants/{}/databases/{}/shards/{}",
            self.tenant, self.database, self.shard
        )
    }

    pub(crate) fn collection_path(&self, collection: CollectionId) -> String {
        format!("{}/collections/{collection}/documents", self.path())
    }
}

/// The wire protocol discriminator. This is separate from a state token.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ProtocolVersion;

impl Serialize for ProtocolVersion {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_u8(1)
    }
}

impl<'de> Deserialize<'de> for ProtocolVersion {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        match u64::deserialize(d)? {
            1 => Ok(Self),
            _ => Err(de::Error::custom("Unsupported native protocol version.")),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Observed {
    pub replica: ReplicaId,
    pub incarnation: Incarnation,
    pub sequence: DecimalU64,
}

pub(crate) fn validate_context(context: &[Observed]) -> Result<()> {
    if context.len() > MAX_CONTEXT_ENTRIES {
        return Err(invalid("Too many context entries."));
    }
    let mut previous = None;
    for entry in context {
        let key = (entry.replica, entry.incarnation);
        if entry.sequence.0 == 0 || previous.is_some_and(|p| p >= key) {
            return Err(invalid(
                "Context must have positive sequences and strictly ordered unique origins.",
            ));
        }
        previous = Some(key);
    }
    Ok(())
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Event {
    pub database: DatabaseId,
    pub shard: DecimalU64,
    pub replica: ReplicaId,
    pub incarnation: Incarnation,
    pub sequence: DecimalU64,
}

impl Event {
    pub(crate) fn validate_in(&self, scope: &DatabaseScope) -> Result<()> {
        if self.database != scope.database
            || self.shard != scope.shard
            || self.sequence.0 == 0
            || self.incarnation.bytes().iter().all(|byte| *byte == 0)
        {
            return Err(invalid(
                "Event scope must match and committed incarnation/sequence must be nonzero.",
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PointState {
    Absent,
    Deleted,
    Value,
    Conflict,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SiblingValue {
    Live(Fields),
    Tombstone,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Sibling {
    pub event: Event,
    pub schema_generation: DecimalU64,
    pub schema_digest: Digest,
    pub value: SiblingValue,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct WireSibling {
    event: Event,
    schema_generation: DecimalU64,
    schema_digest: Digest,
    kind: SiblingKind,
    #[serde(
        default,
        deserialize_with = "present_fields",
        skip_serializing_if = "Option::is_none"
    )]
    fields: Option<Fields>,
}

fn present_fields<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<Fields>, D::Error> {
    Fields::deserialize(d).map(Some)
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum SiblingKind {
    Live,
    Tombstone,
}

impl Serialize for Sibling {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        WireSibling {
            event: self.event.clone(),
            schema_generation: self.schema_generation,
            schema_digest: self.schema_digest,
            kind: if matches!(self.value, SiblingValue::Live(_)) {
                SiblingKind::Live
            } else {
                SiblingKind::Tombstone
            },
            fields: match &self.value {
                SiblingValue::Live(fields) => Some(fields.clone()),
                SiblingValue::Tombstone => None,
            },
        }
        .serialize(s)
    }
}

impl<'de> Deserialize<'de> for Sibling {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let wire = WireSibling::deserialize(d)?;
        let value = match (wire.kind, wire.fields) {
            (SiblingKind::Live, Some(fields)) => SiblingValue::Live(fields),
            (SiblingKind::Tombstone, None) => SiblingValue::Tombstone,
            _ => {
                return Err(de::Error::custom(
                    "Only live siblings carry fields, and their fields are required.",
                ))
            }
        };
        if wire.schema_generation.0 == 0
            || wire.event.sequence.0 == 0
            || wire.event.incarnation.bytes().iter().all(|byte| *byte == 0)
        {
            return Err(de::Error::custom(
                "Schema generation and event sequence must be positive; incarnation must be nonzero.",
            ));
        }
        Ok(Self {
            event: wire.event,
            schema_generation: wire.schema_generation,
            schema_digest: wire.schema_digest,
            value,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Point {
    pub version: ProtocolVersion,
    pub state: PointState,
    pub context: Vec<Observed>,
    pub siblings: Vec<Sibling>,
    /// Nullable, but always present. Only a unique live/deleted state has a token.
    #[serde(deserialize_with = "required_option")]
    pub state_version: Option<Digest>,
}

fn required_option<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<Digest>, D::Error> {
    Option::<Digest>::deserialize(d)
}

impl Point {
    pub fn validate_in(&self, scope: &DatabaseScope) -> Result<()> {
        validate_context(&self.context)?;
        if self.siblings.len() > MAX_CONTEXT_ENTRIES {
            return Err(invalid("Too many siblings."));
        }
        let valid_shape = match self.state {
            PointState::Absent => {
                self.siblings.is_empty() && self.context.is_empty() && self.state_version.is_none()
            }
            PointState::Deleted => {
                self.siblings.len() == 1
                    && matches!(self.siblings[0].value, SiblingValue::Tombstone)
                    && self.state_version.is_some()
            }
            PointState::Value => {
                self.siblings.len() == 1
                    && matches!(self.siblings[0].value, SiblingValue::Live(_))
                    && self.state_version.is_some()
            }
            PointState::Conflict => self.siblings.len() >= 2 && self.state_version.is_none(),
        };
        if !valid_shape {
            return Err(invalid("Point state, siblings and state token disagree."));
        }
        let mut origins = std::collections::BTreeSet::new();
        for sibling in &self.siblings {
            sibling.event.validate_in(scope)?;
            if sibling.schema_generation.0 == 0 {
                return Err(invalid("Schema generation must be positive."));
            }
            let event = &sibling.event;
            if !origins.insert((event.replica, event.incarnation))
                || !self.context.iter().any(|o| {
                    o.replica == event.replica
                        && o.incarnation == event.incarnation
                        && o.sequence >= event.sequence
                })
            {
                return Err(invalid(
                    "Sibling origins must be unique and covered by the returned context.",
                ));
            }
            if let SiblingValue::Live(fields) = &sibling.value {
                fields.validate()?;
            }
        }
        Ok(())
    }

    /// Preserve conflicts explicitly instead of selecting an arbitrary sibling.
    pub fn live_fields(&self) -> Result<Option<&Fields>> {
        match self.state {
            PointState::Conflict => Err(Error::new(
                ErrorKind::Conflict,
                "document_conflict",
                "Document has concurrent siblings; resolve using its complete context.",
            )),
            PointState::Value => match self.siblings.first().map(|s| &s.value) {
                Some(SiblingValue::Live(fields)) => Ok(Some(fields)),
                _ => Err(Error::invalid_response("Value point has no live sibling.")),
            },
            _ => Ok(None),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case", deny_unknown_fields)]
pub enum Predicate {
    Equal { path: Vec<u32>, value: Value },
    LessThan { path: Vec<u32>, value: Value },
    LessThanOrEqual { path: Vec<u32>, value: Value },
    GreaterThan { path: Vec<u32>, value: Value },
    GreaterThanOrEqual { path: Vec<u32>, value: Value },
    IsNull { path: Vec<u32> },
    IsMissing { path: Vec<u32> },
}

impl Predicate {
    pub fn validate(&self) -> Result<()> {
        let (path, value) = match self {
            Self::Equal { path, value }
            | Self::LessThan { path, value }
            | Self::LessThanOrEqual { path, value }
            | Self::GreaterThan { path, value }
            | Self::GreaterThanOrEqual { path, value } => (path, Some(value)),
            Self::IsNull { path } | Self::IsMissing { path } => (path, None),
        };
        if path.is_empty() || path.len() > 16 || path.contains(&0) {
            return Err(invalid(
                "Query path needs 1..16 positive numeric field IDs.",
            ));
        }
        if let Some(value) = value {
            if matches!(value, Value::Null | Value::Map(_) | Value::Array(_)) {
                return Err(invalid(
                    "Comparisons require bool, i64, utf8 or bytes; use is_null for null.",
                ));
            }
            value.validate()?;
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Query {
    pub version: ProtocolVersion,
    pub predicate: Predicate,
    pub limit: u16,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cursor: Option<String>,
}

impl Query {
    pub fn new(predicate: Predicate, limit: u16) -> Result<Self> {
        let query = Self {
            version: ProtocolVersion,
            predicate,
            limit,
            cursor: None,
        };
        query.validate()?;
        Ok(query)
    }
    pub fn validate(&self) -> Result<()> {
        self.predicate.validate()?;
        validate_limit(self.limit)?;
        if let Some(cursor) = &self.cursor {
            validate_cursor(cursor)?;
        }
        Ok(())
    }
}

pub(crate) fn validate_limit(limit: u16) -> Result<()> {
    if !(1..=128).contains(&limit) {
        return Err(invalid("Query limit must be 1..128."));
    }
    Ok(())
}

pub(crate) fn validate_cursor(cursor: &str) -> Result<()> {
    if cursor.is_empty()
        || cursor.len() > MAX_CURSOR_CHARS
        || !cursor
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        return Err(invalid("Cursor must be bounded opaque base64url text."));
    }
    Ok(())
}

fn present_cursor<'de, D: Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<String>, D::Error> {
    String::deserialize(d).map(Some)
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QueryRow {
    pub document: DocumentId,
    pub point: Point,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QueryPage {
    pub version: ProtocolVersion,
    pub rows: Vec<QueryRow>,
    #[serde(
        default,
        deserialize_with = "present_cursor",
        skip_serializing_if = "Option::is_none"
    )]
    pub next_cursor: Option<String>,
}

pub(crate) fn validate_rows(rows: &[QueryRow], scope: &DatabaseScope, limit: usize) -> Result<()> {
    if rows.len() > limit {
        return Err(invalid("Query response exceeds requested limit."));
    }
    let mut previous = None;
    for row in rows {
        if previous.is_some_and(|p| p >= row.document) {
            return Err(invalid("Query rows must be ordered and unique."));
        }
        previous = Some(row.document);
        row.point.validate_in(scope)?;
        if !matches!(row.point.state, PointState::Value | PointState::Conflict) {
            return Err(invalid(
                "Query row must preserve a live or conflicted point.",
            ));
        }
    }
    Ok(())
}

#[derive(Debug, Clone)]
pub struct DocumentsClient {
    shared: SharedClient,
}

impl DocumentsClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    pub async fn get(
        &self,
        scope: &DatabaseScope,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<Point> {
        let deadline = native_deadline();
        let origin = self
            .shared
            .native_origin(scope, NativeRequirement::Point, deadline)
            .await?;
        let request = Request {
            route_name: "native.point".into(),
            method: Method::Get,
            url: format!("{origin}{}/{document}", scope.collection_path(collection)),
            headers: vec![],
            body: None,
        };
        let point: Point = self
            .shared
            .send_native_typed(scope, NativeRequirement::Point, request, deadline)
            .await?;
        point
            .validate_in(scope)
            .map_err(|e| Error::invalid_response(e.message))?;
        Ok(point)
    }

    pub async fn query(
        &self,
        scope: &DatabaseScope,
        collection: CollectionId,
        query: &Query,
    ) -> Result<QueryPage> {
        let deadline = native_deadline();
        query.validate()?;
        let origin = self
            .shared
            .native_origin(scope, NativeRequirement::Query, deadline)
            .await?;
        let request = Request::json(
            "native.query",
            Method::Post,
            format!("{origin}{}/query", scope.collection_path(collection)),
            query,
        )?;
        let page: QueryPage = self
            .shared
            .send_native_typed(scope, NativeRequirement::Query, request, deadline)
            .await?;
        validate_rows(&page.rows, scope, usize::from(query.limit))
            .map_err(|e| Error::invalid_response(e.message))?;
        if let Some(cursor) = &page.next_cursor {
            validate_cursor(cursor).map_err(|e| Error::invalid_response(e.message))?;
        }
        Ok(page)
    }
}
