//! Typed, closed query specification values.
//!
//! Every value in this file is constructed through one validated path
//! (`try_new` or a named builder) and observed only through read-only
//! accessors. None of them accepts a route, arbitrary SQL, a generic map, or
//! generic bytes: the raw-query escape hatch is the separately validated
//! [`V1QueryText`](crate::operations::V1QueryText), which normalizes through
//! the same compile seam as these typed values.

use crate::operations::RequestValidationError;
use crate::types::{Cancellation, CollectionName, Deadline, DocumentValue, TenantId};

/// The reserved family collection. Family state is read only through the
/// family seam; the family module owns the canonical constant and it is not
/// re-exported, so the query seam keeps its own spelled copy of the same name.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// The maximum byte length of one query field name.
pub const MAX_FIELD_BYTES: usize = 255;

/// The maximum number of conditions in one predicate conjunction.
pub const MAX_CONDITIONS: usize = 32;

/// The maximum number of ordering keys.
pub const MAX_ORDERING_KEYS: usize = 8;

/// The maximum number of projected fields.
pub const MAX_PROJECTION_FIELDS: usize = 32;

/// The maximum number of index namespaces one staleness policy may require.
pub const MAX_STALENESS_INDEXES: usize = 8;

/// The maximum page limit. `None` still means "no limit".
pub const MAX_PAGE_LIMIT: u32 = 1024;

/// Validates one query field name: non-empty, at most [`MAX_FIELD_BYTES`]
/// bytes, no control characters, and no surrounding whitespace.
pub(crate) fn validate_field_name(field: &str) -> Result<(), RequestValidationError> {
    if field.is_empty() {
        return Err(RequestValidationError::EmptyField);
    }
    if field.len() > MAX_FIELD_BYTES || field.chars().any(char::is_control) || field != field.trim()
    {
        return Err(RequestValidationError::InvalidQuery);
    }
    Ok(())
}

/// A typed query source: one user collection.
///
/// The reserved family collection `terrabase.families` is not a query source:
/// family state is read only through the family seam, so a query naming it is
/// rejected at construction instead of leaking family records.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QuerySource {
    collection: CollectionName,
}

impl QuerySource {
    /// Creates a typed source from one validated collection name.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the collection is
    /// the reserved family collection.
    pub fn try_new(collection: CollectionName) -> Result<Self, RequestValidationError> {
        if collection.as_str() == RESERVED_FAMILY_COLLECTION {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { collection })
    }

    /// Returns the source collection.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }
}

/// One closed field condition of a typed predicate.
///
/// The variants are data: field names are validated when the containing
/// [`QueryPredicate`] is built, and evaluation never inspects storage,
/// indexes, or another tenant's documents.
#[derive(Clone, Debug, PartialEq)]
pub enum QueryCondition {
    /// The field is present and structurally equal to the value.
    Equals {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is absent, or present and not equal to the value.
    NotEquals {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is numerically or textually greater than the value.
    GreaterThan {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is numerically or textually greater than or equal to the value.
    GreaterOrEqual {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is numerically or textually less than the value.
    LessThan {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is numerically or textually less than or equal to the value.
    LessOrEqual {
        /// The compared field name.
        field: String,
        /// The compared literal.
        value: DocumentValue,
    },
    /// The field is present, including when its value is null.
    Exists {
        /// The inspected field name.
        field: String,
    },
    /// The field is absent from the document.
    Missing {
        /// The inspected field name.
        field: String,
    },
}

impl QueryCondition {
    /// Returns the inspected field name.
    #[must_use]
    pub fn field(&self) -> &str {
        match self {
            Self::Equals { field, .. }
            | Self::NotEquals { field, .. }
            | Self::GreaterThan { field, .. }
            | Self::GreaterOrEqual { field, .. }
            | Self::LessThan { field, .. }
            | Self::LessOrEqual { field, .. }
            | Self::Exists { field }
            | Self::Missing { field } => field,
        }
    }
}

/// A typed predicate: the conjunction of a closed condition list.
///
/// An empty conjunction matches every document of the source. Field names are
/// validated once here, so evaluation never sees an invalid field.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryPredicate {
    conditions: Vec<QueryCondition>,
}

impl QueryPredicate {
    /// Creates a conjunction from closed conditions.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty field name
    /// and [`RequestValidationError::InvalidQuery`] for an invalid field name
    /// or a conjunction longer than [`MAX_CONDITIONS`].
    pub fn all(conditions: Vec<QueryCondition>) -> Result<Self, RequestValidationError> {
        if conditions.len() > MAX_CONDITIONS {
            return Err(RequestValidationError::InvalidQuery);
        }
        for condition in &conditions {
            validate_field_name(condition.field())?;
        }
        Ok(Self { conditions })
    }

    /// Returns the closed condition list in declaration order.
    #[must_use]
    pub fn conditions(&self) -> &[QueryCondition] {
        &self.conditions
    }
}

/// The direction of one ordering key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OrderDirection {
    /// Smallest value first.
    Ascending,
    /// Largest value first.
    Descending,
}

/// One typed ordering key: a field and a direction.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OrderingKey {
    field: String,
    direction: OrderDirection,
}

impl OrderingKey {
    /// Creates one ordering key after validating the field name.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty field name
    /// and [`RequestValidationError::InvalidQuery`] for an invalid one.
    pub fn try_new(field: &str, direction: OrderDirection) -> Result<Self, RequestValidationError> {
        validate_field_name(field)?;
        Ok(Self { field: field.to_owned(), direction })
    }

    /// Returns the ordering field name.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the ordering direction.
    #[must_use]
    pub fn direction(&self) -> OrderDirection {
        self.direction
    }
}

/// A typed ordering: an ordered list of ordering keys.
///
/// An empty ordering keeps the source order, which is the storage canonical
/// key order and is therefore already deterministic; the sort applied on top
/// of it is stable.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct QueryOrdering {
    keys: Vec<OrderingKey>,
}

impl QueryOrdering {
    /// Creates an ordering from validated keys.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the key list is
    /// longer than [`MAX_ORDERING_KEYS`] or names one field twice.
    pub fn try_new(keys: Vec<OrderingKey>) -> Result<Self, RequestValidationError> {
        if keys.len() > MAX_ORDERING_KEYS {
            return Err(RequestValidationError::InvalidQuery);
        }
        let mut fields: Vec<&str> = keys.iter().map(OrderingKey::field).collect();
        fields.sort_unstable();
        if fields.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { keys })
    }

    /// Returns the ordering keys in declaration order.
    #[must_use]
    pub fn keys(&self) -> &[OrderingKey] {
        &self.keys
    }
}

/// A typed projection: the whole document or a named field subset.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QueryProjection {
    fields: Option<Vec<String>>,
}

impl QueryProjection {
    /// Projects every field of the document.
    #[must_use]
    pub fn whole_document() -> Self {
        Self { fields: None }
    }

    /// Projects the named field subset.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty name,
    /// [`RequestValidationError::InvalidQuery`] for an invalid or duplicated
    /// name or a subset larger than [`MAX_PROJECTION_FIELDS`].
    pub fn fields(names: &[&str]) -> Result<Self, RequestValidationError> {
        if names.len() > MAX_PROJECTION_FIELDS {
            return Err(RequestValidationError::InvalidQuery);
        }
        let mut validated = Vec::with_capacity(names.len());
        for name in names {
            validate_field_name(name)?;
            if validated.iter().any(|seen: &String| seen == name) {
                return Err(RequestValidationError::InvalidQuery);
            }
            validated.push((*name).to_owned());
        }
        Ok(Self { fields: Some(validated) })
    }

    /// Returns the projected names, or `None` for the whole document.
    #[must_use]
    pub fn selected(&self) -> Option<&[String]> {
        self.fields.as_deref()
    }
}

/// A typed page request: an offset and an optional limit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct QueryPaging {
    offset: u64,
    limit: Option<u32>,
}

impl QueryPaging {
    /// Creates a page request after validating its boundaries.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] for a zero limit or a
    /// limit above [`MAX_PAGE_LIMIT`]. `None` requests no limit.
    pub fn try_new(offset: u64, limit: Option<u32>) -> Result<Self, RequestValidationError> {
        if limit.is_some_and(|value| value == 0 || value > MAX_PAGE_LIMIT) {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { offset, limit })
    }

    /// Returns the number of matched rows skipped before the page.
    #[must_use]
    pub const fn offset(self) -> u64 {
        self.offset
    }

    /// Returns the page limit, or `None` when no limit was requested.
    #[must_use]
    pub const fn limit(self) -> Option<u32> {
        self.limit
    }
}

/// The identity of one logical index: a tenant and a logical definition name.
///
/// A namespace plus one persisted epoch is the unit that owns a monotonic,
/// non-wrapping generation sequence. Generations of two namespaces, or of two
/// epochs of one namespace, are never ordered or compared as one sequence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexNamespace {
    tenant: TenantId,
    name: String,
}

impl IndexNamespace {
    /// Creates a namespace from a validated tenant and definition name.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty name and
    /// [`RequestValidationError::InvalidQuery`] for an invalid one.
    pub fn try_new(tenant: TenantId, name: &str) -> Result<Self, RequestValidationError> {
        validate_field_name(name)?;
        Ok(Self { tenant, name: name.to_owned() })
    }

    /// Returns the owning tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the logical definition name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the catalog key of this namespace. Crate-only: the key shape is
    /// an implementation detail of the private index catalog.
    ///
    /// The key joins the tenant and the definition name with the unit
    /// separator `'\u{1f}'`. Both parts reject control characters — the
    /// tenant through the shared identifier rules and the name through the
    /// field-name rules — so the separator cannot occur inside either part
    /// and the pair is injective: the namespace `("a", "b/c")` and the
    /// namespace `("a/b", "c")`, which a plain `'/'` join would collapse into
    /// one key, stay distinct catalog entries. A namespace identifies the
    /// tenant and the logical definition; the key must not blur that.
    pub(crate) fn catalog_key(&self) -> String {
        format!("{}\u{1f}{}", self.tenant.as_str(), self.name)
    }
}

/// How strictly a query requires the selected indexes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StalenessMode {
    /// Every required index must meet the generation the snapshot observed
    /// for its namespace; otherwise the query fails with
    /// [`QueryError::IndexNotReady`](crate::query::QueryError::IndexNotReady).
    Strict,
    /// An older, absent, or not-comparable index generation may be used, and
    /// the generation actually used is reported per namespace.
    AllowStale,
}

/// A typed index staleness policy: a mode and the required index namespaces.
///
/// An empty namespace list selects no index: the query executes index-less
/// against the snapshot, which is the W3-2 tracer base path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StalenessPolicy {
    mode: StalenessMode,
    indexes: Vec<IndexNamespace>,
}

impl StalenessPolicy {
    /// Creates a policy after validating the namespace list.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the list is
    /// longer than [`MAX_STALENESS_INDEXES`], names one namespace twice, or
    /// is empty under [`StalenessMode::AllowStale`]: an allow-stale policy
    /// that requires no index would report nothing and only strict may
    /// select the index-less base path.
    pub fn try_new(
        mode: StalenessMode,
        indexes: Vec<IndexNamespace>,
    ) -> Result<Self, RequestValidationError> {
        if indexes.len() > MAX_STALENESS_INDEXES {
            return Err(RequestValidationError::InvalidQuery);
        }
        if mode == StalenessMode::AllowStale && indexes.is_empty() {
            return Err(RequestValidationError::InvalidQuery);
        }
        let mut keys: Vec<String> = indexes.iter().map(IndexNamespace::catalog_key).collect();
        keys.sort();
        if keys.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { mode, indexes })
    }

    /// Returns the strictness mode.
    #[must_use]
    pub fn mode(&self) -> StalenessMode {
        self.mode
    }

    /// Returns the required index namespaces.
    #[must_use]
    pub fn indexes(&self) -> &[IndexNamespace] {
        &self.indexes
    }
}

/// A validated typed query specification.
///
/// This is the typed input form of the one compile seam; the V1-compatible
/// text form normalizes to the same internal model.
#[derive(Clone, Debug, PartialEq)]
pub struct QuerySpec {
    tenant_claim: TenantId,
    source: QuerySource,
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
    staleness: StalenessPolicy,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl QuerySpec {
    /// Creates a typed query specification after request validation.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an already
    /// expired deadline and [`RequestValidationError::InvalidCancellation`]
    /// for an already cancelled signal. The clause values are pre-validated by
    /// their own constructors.
    #[allow(clippy::too_many_arguments)] // the frozen contract passes every clause
    pub fn try_new(
        tenant_claim: TenantId,
        source: QuerySource,
        predicate: QueryPredicate,
        ordering: QueryOrdering,
        projection: QueryProjection,
        paging: QueryPaging,
        staleness: StalenessPolicy,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self {
            tenant_claim,
            source,
            predicate,
            ordering,
            projection,
            paging,
            staleness,
            deadline,
            cancellation,
        })
    }

    /// Returns the untrusted tenant claim.
    #[must_use]
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the typed source.
    #[must_use]
    pub fn source(&self) -> &QuerySource {
        &self.source
    }

    /// Returns the typed predicate.
    #[must_use]
    pub fn predicate(&self) -> &QueryPredicate {
        &self.predicate
    }

    /// Returns the typed ordering.
    #[must_use]
    pub fn ordering(&self) -> &QueryOrdering {
        &self.ordering
    }

    /// Returns the typed projection.
    #[must_use]
    pub fn projection(&self) -> &QueryProjection {
        &self.projection
    }

    /// Returns the typed page request.
    #[must_use]
    pub fn paging(&self) -> QueryPaging {
        self.paging
    }

    /// Returns the typed index staleness policy.
    #[must_use]
    pub fn staleness(&self) -> &StalenessPolicy {
        &self.staleness
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}
