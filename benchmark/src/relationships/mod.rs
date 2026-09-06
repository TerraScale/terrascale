//! Native, typed document relationships.
//!
//! A relationship is an engine-owned edge record, rather than an application
//! convention over document fields.  This module deliberately contains only
//! the bounded index seam: persistence, transaction integration, and query
//! planning can consume the immutable snapshots without changing edge
//! identity or index ordering.

#![allow(clippy::pedantic)]

use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
    fmt,
    sync::{Arc, RwLock},
};

use crate::types::{CollectionName, DocumentId, DocumentValue, TenantId};

#[cfg(test)]
mod tests;

/// The largest identifier accepted by the relationship seam.
pub const MAX_RELATIONSHIP_IDENTIFIER_BYTES: usize = 255;
/// The maximum number of properties on one edge.
pub const MAX_EDGE_PROPERTIES: usize = 64;
/// The maximum encoded property bytes on one edge.
pub const MAX_EDGE_PROPERTY_BYTES: usize = 64 * 1024;

/// A typed failure from relationship construction, indexing, or traversal.
#[derive(Clone, Debug, Eq, PartialEq, thiserror::Error)]
pub enum RelationshipError {
    /// A relationship identifier was empty, oversized, padded, or contained
    /// a control character.
    #[error("invalid relationship identifier")]
    InvalidIdentifier,
    /// An edge used a different tenant or database from the index.
    #[error("relationship scope mismatch")]
    ScopeMismatch,
    /// An edge named a different relationship from the index definition.
    #[error("relationship name mismatch")]
    RelationMismatch,
    /// An endpoint collection did not match its declared side of the
    /// relationship.
    #[error("relationship endpoint collection mismatch")]
    EndpointCollectionMismatch,
    /// The same edge identity was presented with different immutable fields.
    #[error("relationship edge identity conflict")]
    EdgeIdentityConflict,
    /// A duplicate add arrived after the identity had been removed.
    #[error("relationship edge identity was removed")]
    RemovedIdentity,
    /// The generation requested by a caller is no longer the snapshot
    /// generation being read.
    #[error("stale relationship generation")]
    StaleGeneration {
        /// The generation requested by the caller.
        expected: u64,
        /// The generation present in the snapshot.
        actual: u64,
    },
    /// The generation counter cannot advance further.
    #[error("relationship generation exhausted")]
    GenerationExhausted,
    /// A traversal bound was zero or otherwise invalid.
    #[error("invalid relationship traversal bounds")]
    InvalidTraversalBounds,
    /// A traversal exceeded its maximum hop count.
    #[error("relationship traversal hop limit exceeded")]
    HopLimitExceeded,
    /// A traversal exceeded its maximum result count.
    #[error("relationship traversal result limit exceeded")]
    ResultLimitExceeded,
    /// A traversal exceeded its maximum encoded result bytes.
    #[error("relationship traversal byte limit exceeded")]
    ByteLimitExceeded,
    /// An edge property set exceeded a fixed cardinality or byte bound.
    #[error("relationship property limit exceeded")]
    PropertyLimitExceeded,
    /// A concurrent reader or writer observed a poisoned lock.
    #[error("relationship index lock poisoned")]
    LockPoisoned,
}

fn valid_identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= MAX_RELATIONSHIP_IDENTIFIER_BYTES
        && value == value.trim()
        && !value.chars().any(char::is_control)
}

/// A validated database identity used to bind relationship records.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseId(String);

impl DatabaseId {
    /// Creates a validated database identity.
    pub fn try_new(value: &str) -> Result<Self, RelationshipError> {
        if !valid_identifier(value) {
            return Err(RelationshipError::InvalidIdentifier);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical database identity text.
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for DatabaseId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// The tenant and database namespace of a relationship index.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RelationshipScope {
    tenant: TenantId,
    database: DatabaseId,
}

impl RelationshipScope {
    /// Creates a scope from an already validated tenant and database.
    pub fn new(tenant: TenantId, database: DatabaseId) -> Self {
        Self { tenant, database }
    }

    /// Returns the tenant identity.
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the database identity.
    pub fn database(&self) -> &DatabaseId {
        &self.database
    }
}

/// A validated relationship name.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RelationshipName(String);

impl RelationshipName {
    /// Creates a validated relationship name.
    pub fn try_new(value: &str) -> Result<Self, RelationshipError> {
        if !valid_identifier(value) {
            return Err(RelationshipError::InvalidIdentifier);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical relationship name.
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for RelationshipName {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// The supported relationship cardinalities.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum RelationshipCardinality {
    /// One source may connect to one target.
    OneToOne,
    /// One source may connect to many targets.
    OneToMany,
    /// Many sources may connect to many targets.
    ManyToMany,
}

/// A validated document endpoint.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RelationshipEndpoint {
    collection: CollectionName,
    document: DocumentId,
}

impl RelationshipEndpoint {
    /// Creates an endpoint from validated collection and document identities.
    pub fn new(collection: CollectionName, document: DocumentId) -> Self {
        Self { collection, document }
    }

    /// Returns the endpoint collection.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the endpoint document identity.
    pub fn document(&self) -> &DocumentId {
        &self.document
    }
}

/// A short alias for [`RelationshipEndpoint`].
pub type Endpoint = RelationshipEndpoint;

/// A stable application identity for one edge.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct EdgeId(String);

impl EdgeId {
    /// Creates a validated stable edge identity.
    pub fn try_new(value: &str) -> Result<Self, RelationshipError> {
        if !valid_identifier(value) {
            return Err(RelationshipError::InvalidIdentifier);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical edge identity text.
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for EdgeId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A stable edge identity including its immutable namespace and relationship.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct EdgeIdentity {
    scope: RelationshipScope,
    relation: RelationshipName,
    edge: EdgeId,
}

impl EdgeIdentity {
    /// Returns the edge's namespace.
    pub fn scope(&self) -> &RelationshipScope {
        &self.scope
    }

    /// Returns the edge's relationship name.
    pub fn relation(&self) -> &RelationshipName {
        &self.relation
    }

    /// Returns the application edge identity.
    pub fn edge_id(&self) -> &EdgeId {
        &self.edge
    }
}

/// The typed relationship declaration used to validate both endpoint sides.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RelationshipDefinition {
    name: RelationshipName,
    source_collection: CollectionName,
    target_collection: CollectionName,
    cardinality: RelationshipCardinality,
}

impl RelationshipDefinition {
    /// Declares a relationship and its endpoint collections.
    pub fn new(
        name: RelationshipName,
        source_collection: CollectionName,
        target_collection: CollectionName,
        cardinality: RelationshipCardinality,
    ) -> Self {
        Self { name, source_collection, target_collection, cardinality }
    }

    /// Returns the typed relationship name.
    pub fn name(&self) -> &RelationshipName {
        &self.name
    }

    /// Returns the required source collection.
    pub fn source_collection(&self) -> &CollectionName {
        &self.source_collection
    }

    /// Returns the required target collection.
    pub fn target_collection(&self) -> &CollectionName {
        &self.target_collection
    }

    /// Returns the declared cardinality.
    pub const fn cardinality(&self) -> RelationshipCardinality {
        self.cardinality
    }

    fn validate_edge(&self, edge: &EdgeRecord) -> Result<(), RelationshipError> {
        if edge.relation != self.name {
            return Err(RelationshipError::RelationMismatch);
        }
        if edge.source.collection != self.source_collection
            || edge.target.collection != self.target_collection
        {
            return Err(RelationshipError::EndpointCollectionMismatch);
        }
        Ok(())
    }
}

/// A native typed edge record.  Its identity and namespace never change.
#[derive(Clone, Debug, PartialEq)]
pub struct EdgeRecord {
    scope: RelationshipScope,
    relation: RelationshipName,
    id: EdgeId,
    source: RelationshipEndpoint,
    target: RelationshipEndpoint,
    properties: BTreeMap<String, DocumentValue>,
}

impl EdgeRecord {
    /// Creates an edge without optional properties.
    pub fn try_new(
        scope: RelationshipScope,
        relation: RelationshipName,
        id: EdgeId,
        source: RelationshipEndpoint,
        target: RelationshipEndpoint,
    ) -> Result<Self, RelationshipError> {
        Self::try_new_with_properties(scope, relation, id, source, target, BTreeMap::new())
    }

    /// Creates an edge with bounded typed properties.
    pub fn try_new_with_properties(
        scope: RelationshipScope,
        relation: RelationshipName,
        id: EdgeId,
        source: RelationshipEndpoint,
        target: RelationshipEndpoint,
        properties: BTreeMap<String, DocumentValue>,
    ) -> Result<Self, RelationshipError> {
        if properties.len() > MAX_EDGE_PROPERTIES
            || properties.keys().any(|key| !valid_identifier(key))
        {
            return Err(RelationshipError::PropertyLimitExceeded);
        }
        let property_bytes = properties
            .iter()
            .map(|(key, value)| key.len() + document_value_bytes(value))
            .sum::<usize>();
        if property_bytes > MAX_EDGE_PROPERTY_BYTES {
            return Err(RelationshipError::PropertyLimitExceeded);
        }
        Ok(Self { scope, relation, id, source, target, properties })
    }

    /// Returns the namespace bound to the edge.
    pub fn scope(&self) -> &RelationshipScope {
        &self.scope
    }

    /// Returns the relationship name.
    pub fn relation(&self) -> &RelationshipName {
        &self.relation
    }

    /// Returns the stable application edge identity.
    pub fn id(&self) -> &EdgeId {
        &self.id
    }

    /// Returns the complete stable identity of the edge.
    pub fn identity(&self) -> EdgeIdentity {
        EdgeIdentity {
            scope: self.scope.clone(),
            relation: self.relation.clone(),
            edge: self.id.clone(),
        }
    }

    /// Returns the source endpoint.
    pub fn source(&self) -> &RelationshipEndpoint {
        &self.source
    }

    /// Returns the target endpoint.
    pub fn target(&self) -> &RelationshipEndpoint {
        &self.target
    }

    /// Returns optional typed edge properties.
    pub fn properties(&self) -> &BTreeMap<String, DocumentValue> {
        &self.properties
    }

    fn same_immutable_fields(&self, other: &Self) -> bool {
        self.scope == other.scope
            && self.relation == other.relation
            && self.id == other.id
            && self.source == other.source
            && self.target == other.target
            && self.properties == other.properties
    }
}

fn document_value_bytes(value: &DocumentValue) -> usize {
    match value {
        DocumentValue::Null => 1,
        DocumentValue::Boolean(_) => 1,
        DocumentValue::Integer(_) | DocumentValue::Number(_) => 8,
        DocumentValue::String(value) => value.len(),
        DocumentValue::Array(values) => values.iter().map(document_value_bytes).sum(),
        DocumentValue::Object(values) => {
            values.iter().map(|(key, value)| key.len() + document_value_bytes(value)).sum()
        }
    }
}

/// The direction of a relationship traversal.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum TraversalDirection {
    /// Follow source-to-target edges.
    Forward,
    /// Follow target-to-source edges.
    Reverse,
}

/// Explicit resource bounds required by every traversal.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TraversalBounds {
    /// The maximum number of edge hops.
    pub max_hops: usize,
    /// The maximum number of returned endpoint rows.
    pub max_results: usize,
    /// The maximum encoded bytes of returned endpoint rows.
    pub max_bytes: usize,
}

impl TraversalBounds {
    /// Creates finite traversal bounds.
    pub const fn new(max_hops: usize, max_results: usize, max_bytes: usize) -> Self {
        Self { max_hops, max_results, max_bytes }
    }

    fn validate(self) -> Result<(), RelationshipError> {
        if self.max_hops == 0 || self.max_results == 0 || self.max_bytes == 0 {
            return Err(RelationshipError::InvalidTraversalBounds);
        }
        Ok(())
    }
}

/// One endpoint returned by a bounded traversal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TraversalHit {
    edge_id: EdgeId,
    endpoint: RelationshipEndpoint,
    hops: usize,
}

impl TraversalHit {
    /// Returns the edge that produced this row.
    pub fn edge_id(&self) -> &EdgeId {
        &self.edge_id
    }

    /// Returns the endpoint reached by the row.
    pub fn endpoint(&self) -> &RelationshipEndpoint {
        &self.endpoint
    }

    /// Returns the hop distance from the traversal origin.
    pub const fn hops(&self) -> usize {
        self.hops
    }

    fn encoded_bytes(&self) -> usize {
        self.edge_id.as_str().len()
            + self.endpoint.collection.as_str().len()
            + self.endpoint.document.as_str().len()
            + std::mem::size_of::<usize>()
    }
}

/// The result of a bounded relationship traversal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TraversalResult {
    rows: Vec<TraversalHit>,
    bytes: usize,
    generation: u64,
}

impl TraversalResult {
    /// Returns the rows in deterministic edge/index order.
    pub fn rows(&self) -> &[TraversalHit] {
        &self.rows
    }

    /// Returns the encoded row-byte charge.
    pub const fn bytes(&self) -> usize {
        self.bytes
    }

    /// Returns the snapshot generation used for the result.
    pub const fn generation(&self) -> u64 {
        self.generation
    }
}

#[derive(Clone, Debug, Default)]
struct IndexState {
    generation: u64,
    edges: BTreeMap<EdgeId, EdgeRecord>,
    removed: BTreeSet<EdgeId>,
    forward: BTreeMap<RelationshipEndpoint, BTreeSet<EdgeId>>,
    reverse: BTreeMap<RelationshipEndpoint, BTreeSet<EdgeId>>,
}

/// A concurrent relationship index for one tenant, database, and relation.
#[derive(Clone)]
pub struct RelationshipStore {
    scope: RelationshipScope,
    definition: RelationshipDefinition,
    state: Arc<RwLock<IndexState>>,
}

impl fmt::Debug for RelationshipStore {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("RelationshipStore")
            .field("scope", &self.scope)
            .field("definition", &self.definition)
            .finish_non_exhaustive()
    }
}

impl RelationshipStore {
    /// Creates an empty relationship index.
    pub fn new(scope: RelationshipScope, definition: RelationshipDefinition) -> Self {
        Self { scope, definition, state: Arc::new(RwLock::new(IndexState::default())) }
    }

    /// Returns the index namespace.
    pub fn scope(&self) -> &RelationshipScope {
        &self.scope
    }

    /// Returns the typed relationship declaration.
    pub fn definition(&self) -> &RelationshipDefinition {
        &self.definition
    }

    /// Adds one edge idempotently and publishes a new generation only when
    /// the visible set changes.
    pub fn add(&self, edge: EdgeRecord) -> Result<AddOutcome, RelationshipError> {
        self.validate_edge(&edge)?;
        let mut state = self.state.write().map_err(|_| RelationshipError::LockPoisoned)?;
        if state.removed.contains(edge.id()) {
            return Ok(AddOutcome::Removed { generation: state.generation });
        }
        if let Some(existing) = state.edges.get(edge.id()) {
            if existing.same_immutable_fields(&edge) {
                return Ok(AddOutcome::AlreadyPresent { generation: state.generation });
            }
            return Err(RelationshipError::EdgeIdentityConflict);
        }
        let generation = next_generation(state.generation)?;
        state.forward.entry(edge.source.clone()).or_default().insert(edge.id.clone());
        state.reverse.entry(edge.target.clone()).or_default().insert(edge.id.clone());
        state.edges.insert(edge.id.clone(), edge);
        state.generation = generation;
        Ok(AddOutcome::Inserted { generation })
    }

    /// Alias for [`Self::add`].
    pub fn insert(&self, edge: EdgeRecord) -> Result<AddOutcome, RelationshipError> {
        self.add(edge)
    }

    /// Removes an edge idempotently.  A tombstone makes remove-before-add
    /// ordering deterministic and prevents a delayed duplicate add from
    /// resurrecting the identity.
    pub fn remove(&self, edge_id: &EdgeId) -> Result<RemoveOutcome, RelationshipError> {
        let mut state = self.state.write().map_err(|_| RelationshipError::LockPoisoned)?;
        if state.removed.contains(edge_id) {
            return Ok(RemoveOutcome::AlreadyRemoved { generation: state.generation });
        }
        let Some(edge) = state.edges.remove(edge_id) else {
            state.removed.insert(edge_id.clone());
            let generation = next_generation(state.generation)?;
            state.generation = generation;
            return Ok(RemoveOutcome::Removed { generation });
        };
        remove_posting(&mut state.forward, &edge.source, edge_id);
        remove_posting(&mut state.reverse, &edge.target, edge_id);
        state.removed.insert(edge_id.clone());
        let generation = next_generation(state.generation)?;
        state.generation = generation;
        Ok(RemoveOutcome::Removed { generation })
    }

    /// Returns an immutable snapshot of the currently published generation.
    pub fn snapshot(&self) -> Result<RelationshipSnapshot, RelationshipError> {
        let state = self.state.read().map_err(|_| RelationshipError::LockPoisoned)?;
        Ok(RelationshipSnapshot::from_state(self.scope.clone(), self.definition.clone(), &state))
    }

    /// Returns a snapshot only if its generation is the requested one.
    pub fn snapshot_at(&self, expected: u64) -> Result<RelationshipSnapshot, RelationshipError> {
        let snapshot = self.snapshot()?;
        snapshot.require_generation(expected)?;
        Ok(snapshot)
    }

    /// Performs a generation-checked traversal against one immutable cut.
    pub fn traverse_at(
        &self,
        expected_generation: u64,
        start: &RelationshipEndpoint,
        direction: TraversalDirection,
        bounds: TraversalBounds,
    ) -> Result<TraversalResult, RelationshipError> {
        self.snapshot_at(expected_generation)?.traverse(start, direction, bounds)
    }

    fn validate_edge(&self, edge: &EdgeRecord) -> Result<(), RelationshipError> {
        if edge.scope != self.scope {
            return Err(RelationshipError::ScopeMismatch);
        }
        self.definition.validate_edge(edge)
    }
}

fn next_generation(current: u64) -> Result<u64, RelationshipError> {
    current.checked_add(1).ok_or(RelationshipError::GenerationExhausted)
}

fn remove_posting(
    index: &mut BTreeMap<RelationshipEndpoint, BTreeSet<EdgeId>>,
    endpoint: &RelationshipEndpoint,
    edge_id: &EdgeId,
) {
    if let Some(posting) = index.get_mut(endpoint) {
        posting.remove(edge_id);
        if posting.is_empty() {
            index.remove(endpoint);
        }
    }
}

/// The result of adding an edge.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AddOutcome {
    /// The edge became visible at this generation.
    Inserted {
        /// The newly published generation.
        generation: u64,
    },
    /// The exact edge was already visible at this generation.
    AlreadyPresent {
        /// The unchanged visible generation.
        generation: u64,
    },
    /// The identity was removed and cannot be resurrected.
    Removed {
        /// The generation containing the remove tombstone.
        generation: u64,
    },
}

impl AddOutcome {
    /// Returns the generation observed by the operation.
    pub const fn generation(self) -> u64 {
        match self {
            Self::Inserted { generation }
            | Self::AlreadyPresent { generation }
            | Self::Removed { generation } => generation,
        }
    }
}

/// The result of removing an edge.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RemoveOutcome {
    /// The edge was removed (or a remove tombstone was first published).
    Removed {
        /// The generation containing the remove tombstone.
        generation: u64,
    },
    /// The remove had already been observed.
    AlreadyRemoved {
        /// The unchanged generation containing the remove tombstone.
        generation: u64,
    },
}

impl RemoveOutcome {
    /// Returns the generation observed by the operation.
    pub const fn generation(self) -> u64 {
        match self {
            Self::Removed { generation } | Self::AlreadyRemoved { generation } => generation,
        }
    }
}

/// An immutable, generation-tagged relationship index snapshot.
#[derive(Clone, Debug, PartialEq)]
pub struct RelationshipSnapshot {
    scope: RelationshipScope,
    definition: RelationshipDefinition,
    generation: u64,
    edges: BTreeMap<EdgeId, EdgeRecord>,
    forward: BTreeMap<RelationshipEndpoint, BTreeSet<EdgeId>>,
    reverse: BTreeMap<RelationshipEndpoint, BTreeSet<EdgeId>>,
}

impl RelationshipSnapshot {
    fn from_state(
        scope: RelationshipScope,
        definition: RelationshipDefinition,
        state: &IndexState,
    ) -> Self {
        Self {
            scope,
            definition,
            generation: state.generation,
            edges: state.edges.clone(),
            forward: state.forward.clone(),
            reverse: state.reverse.clone(),
        }
    }

    /// Returns the namespace of this snapshot.
    pub fn scope(&self) -> &RelationshipScope {
        &self.scope
    }

    /// Returns the relationship definition of this snapshot.
    pub fn definition(&self) -> &RelationshipDefinition {
        &self.definition
    }

    /// Returns the immutable publication generation.
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the number of visible edge records.
    pub fn len(&self) -> usize {
        self.edges.len()
    }

    /// Reports whether this snapshot contains no visible edges.
    pub fn is_empty(&self) -> bool {
        self.edges.is_empty()
    }

    /// Looks up one visible edge by stable identity.
    pub fn get(&self, edge_id: &EdgeId) -> Option<&EdgeRecord> {
        self.edges.get(edge_id)
    }

    /// Returns all visible edges in stable edge-ID order.
    pub fn edges(&self) -> impl Iterator<Item = &EdgeRecord> {
        self.edges.values()
    }

    /// Requires this immutable cut to have one exact generation.
    pub fn require_generation(&self, expected: u64) -> Result<(), RelationshipError> {
        if self.generation != expected {
            return Err(RelationshipError::StaleGeneration { expected, actual: self.generation });
        }
        Ok(())
    }

    /// Follows forward or reverse edges with explicit depth, row, and byte
    /// bounds.  Rows are emitted in deterministic endpoint/index order.
    pub fn traverse(
        &self,
        start: &RelationshipEndpoint,
        direction: TraversalDirection,
        bounds: TraversalBounds,
    ) -> Result<TraversalResult, RelationshipError> {
        bounds.validate()?;
        let index = match direction {
            TraversalDirection::Forward => &self.forward,
            TraversalDirection::Reverse => &self.reverse,
        };
        let mut queue = VecDeque::from([(start.clone(), 0_usize)]);
        let mut visited = BTreeSet::from([start.clone()]);
        let mut rows = Vec::new();
        let mut bytes = 0_usize;
        while let Some((current, hops)) = queue.pop_front() {
            if hops >= bounds.max_hops {
                continue;
            }
            let Some(edge_ids) = index.get(&current) else { continue };
            for edge_id in edge_ids {
                let Some(edge) = self.edges.get(edge_id) else { continue };
                let endpoint = match direction {
                    TraversalDirection::Forward => edge.target.clone(),
                    TraversalDirection::Reverse => edge.source.clone(),
                };
                let next_hops = hops + 1;
                let hit = TraversalHit {
                    edge_id: edge_id.clone(),
                    endpoint: endpoint.clone(),
                    hops: next_hops,
                };
                let hit_bytes = hit.encoded_bytes();
                if rows.len() == bounds.max_results {
                    return Err(RelationshipError::ResultLimitExceeded);
                }
                let next_bytes =
                    bytes.checked_add(hit_bytes).ok_or(RelationshipError::ByteLimitExceeded)?;
                if next_bytes > bounds.max_bytes {
                    return Err(RelationshipError::ByteLimitExceeded);
                }
                rows.push(hit);
                bytes = next_bytes;
                if next_hops < bounds.max_hops && visited.insert(endpoint.clone()) {
                    queue.push_back((endpoint, next_hops));
                }
            }
        }
        Ok(TraversalResult { rows, bytes, generation: self.generation })
    }

    /// Performs a bounded forward traversal.
    pub fn forward(
        &self,
        start: &RelationshipEndpoint,
        bounds: TraversalBounds,
    ) -> Result<TraversalResult, RelationshipError> {
        self.traverse(start, TraversalDirection::Forward, bounds)
    }

    /// Performs a bounded reverse traversal.
    pub fn reverse(
        &self,
        start: &RelationshipEndpoint,
        bounds: TraversalBounds,
    ) -> Result<TraversalResult, RelationshipError> {
        self.traverse(start, TraversalDirection::Reverse, bounds)
    }
}
