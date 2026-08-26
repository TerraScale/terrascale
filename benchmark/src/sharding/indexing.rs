//! Sharded index definition and readers (SH-04, PB-4060, ADR 0011).
//!
//! An index definition is declared once for a collection and served by every
//! shard that owns the collection's buckets: each shard's catalog registers
//! the definition under the same namespace and publishes its own segments
//! from its own publications, so the per-shard generations differ whenever
//! the shards' publication histories differ.
//!
//! This module owns the fan-out of that shape:
//!
//! - [`ShardedIndexDefinition`] declares one logical namespace plus one typed
//!   explicit definition, and refuses everything the Query Optimizer owns
//!   (the reserved `Auto/` namespace and automatic kinds).
//! - [`ShardedIndexDefinition::register`] registers the definition on every
//!   shard the seated table names, reporting each shard's epoch and each
//!   refusal instead of hiding a partial registration.
//! - [`ShardedIndexDefinition::read`] reads every shard's published state for
//!   the namespace and composes the reader's verdict: the least advanced
//!   shard bounds the whole namespace, exactly as the composed staleness of
//!   a fanned-out query does.
//!
//! Non-claims: no automatic index lifecycle (the IX wave owns it), no index
//! data served to a query (the query seam selects indexes per shard), and no
//! performance fact.

use crate::index::{IndexCatalog, IndexError};
use crate::query::optimizer::{AUTO_NAMESPACE_PREFIX, IndexDefinition, IndexKind};
use crate::query::{IndexNamespace, ObservedGeneration};
use crate::replication::MemberId;

use super::query::{MemberServing, ShardServingError, ShardedExecutionError};
use super::{ShardId, ShardTopology, ShardingError};

/// One shard endpoint's index refusal during a sharded index operation.
///
/// A narrow public mirror of the index catalog's own refusal categories: the
/// catalog's error type is engine-private, and the sharded surface reports
/// the categories a caller can act on.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShardIndexError {
    /// The namespace is reserved for the Query Optimizer's automatic indexes
    /// and refuses explicit registration.
    AutoNamespaceReserved,
    /// The definition's kind does not match the endpoint's registration path.
    KindMismatch,
    /// The endpoint's catalog has no such namespace.
    NamespaceUnknown,
    /// The endpoint refused the operation for another catalog reason.
    Other,
}

impl From<IndexError> for ShardIndexError {
    fn from(error: IndexError) -> Self {
        match error {
            IndexError::AutoNamespaceReserved => Self::AutoNamespaceReserved,
            IndexError::KindMismatch => Self::KindMismatch,
            IndexError::NamespaceUnknown => Self::NamespaceUnknown,
            IndexError::EpochNotNewer
            | IndexError::NoPublicationObserved
            | IndexError::GenerationExhausted
            | IndexError::StorageFailure
            | IndexError::AutoIdentityMismatch
            | IndexError::AutoRegistryFull => Self::Other,
        }
    }
}

/// One shard's serving endpoint as a sharded index operation reaches it: the
/// explicit-index registration and published-state reads of that shard's
/// engine-owned index catalog.
pub trait ShardIndexServing {
    /// Registers one explicit index definition on this shard's catalog and
    /// returns the namespace's epoch there.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's own refusal, which always names the Member when
    /// no endpoint is seated for it.
    fn register_explicit(
        &self,
        owner: &MemberId,
        namespace: &IndexNamespace,
        definition: &IndexDefinition,
    ) -> Result<u64, ShardServingError>;

    /// Returns this shard's published state for one namespace, or `None` when
    /// the namespace carries no definition at this endpoint.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's own refusal, which always names the Member when
    /// no endpoint is seated for it.
    fn published_index(
        &self,
        owner: &MemberId,
        namespace: &IndexNamespace,
    ) -> Result<Option<ShardPublishedIndex>, ShardServingError>;
}

impl ShardIndexServing for MemberServing<'_> {
    fn register_explicit(
        &self,
        owner: &MemberId,
        namespace: &IndexNamespace,
        definition: &IndexDefinition,
    ) -> Result<u64, ShardServingError> {
        self.engine_of(owner)?
            .index_catalog()
            .register_explicit(namespace, definition)
            .map_err(|error| ShardServingError::Index(ShardIndexError::from(error)))
    }

    fn published_index(
        &self,
        owner: &MemberId,
        namespace: &IndexNamespace,
    ) -> Result<Option<ShardPublishedIndex>, ShardServingError> {
        let catalog: &IndexCatalog = &self.engine_of(owner)?.index_catalog();
        let entry = catalog
            .definitions(namespace.tenant())
            .into_iter()
            .find(|entry| entry.namespace() == namespace);
        Ok(entry.map(|entry| ShardPublishedIndex::new(entry.published(), entry.covered_sequence())))
    }
}

/// One shard's published state for one index namespace.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ShardPublishedIndex {
    published: Option<ObservedGeneration>,
    covered_sequence: Option<u64>,
}

impl ShardPublishedIndex {
    /// Creates one published-state view. Crate-only: the serving endpoint
    /// produces these from its catalog.
    pub(crate) const fn new(
        published: Option<ObservedGeneration>,
        covered_sequence: Option<u64>,
    ) -> Self {
        Self { published, covered_sequence }
    }

    /// Returns the newest published generation of this namespace at this
    /// shard, when one exists.
    #[must_use]
    pub const fn published(&self) -> Option<ObservedGeneration> {
        self.published
    }

    /// Returns the storage sequence that publication covers, when one was
    /// published.
    #[must_use]
    pub const fn covered_sequence(&self) -> Option<u64> {
        self.covered_sequence
    }
}

// ---------------------------------------------------------------------------
// Definition
// ---------------------------------------------------------------------------

/// One sharded explicit index definition: a logical namespace plus a typed
/// definition, registered and served per shard.
///
/// The definition is the same typed value one shard's catalog registers;
/// sharding adds the fan-out (every seated shard) and the composed reads, not
/// a new index model.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardedIndexDefinition {
    namespace: IndexNamespace,
    definition: IndexDefinition,
}

/// What a sharded index definition refuses at declaration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardedIndexRefusal {
    /// The namespace sits under the reserved `Auto/` prefix the Query
    /// Optimizer owns; explicit definitions never take it.
    AutoNamespaceReserved {
        /// The refused namespace name.
        name: String,
    },
    /// Only explicit definitions may be declared sharded: an automatic index
    /// belongs to the Query Optimizer's own lifecycle, never to a typed
    /// declaration.
    AutoKind,
}

impl ShardedIndexDefinition {
    /// Declares one sharded index definition.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedIndexRefusal::AutoNamespaceReserved`] for a namespace
    /// under the reserved automatic prefix and
    /// [`ShardedIndexRefusal::AutoKind`] for a non-explicit definition; the
    /// endpoint refuses both again, so a refusal can never be routed away.
    pub fn try_new(
        namespace: IndexNamespace,
        definition: IndexDefinition,
    ) -> Result<Self, ShardedIndexRefusal> {
        if namespace.name().starts_with(AUTO_NAMESPACE_PREFIX) {
            return Err(ShardedIndexRefusal::AutoNamespaceReserved {
                name: namespace.name().to_owned(),
            });
        }
        if definition.kind() != IndexKind::Explicit {
            return Err(ShardedIndexRefusal::AutoKind);
        }
        Ok(Self { namespace, definition })
    }

    /// Returns the logical namespace the shards register under.
    #[must_use]
    pub fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns the typed definition each shard registers.
    #[must_use]
    pub fn definition(&self) -> &IndexDefinition {
        &self.definition
    }

    /// Registers this definition on every shard the seated table names,
    /// reporting each shard's epoch and each refusal.
    ///
    /// A partial registration is reported, never hidden: the outcome carries
    /// every shard's result and [`ShardedIndexRegistration::is_uniform`]
    /// answers whether the whole table accepted the definition.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::Routing`] when the seated pair stops
    /// covering a shard the table maps buckets to; endpoint refusals are
    /// per-shard outcomes, not operation failures.
    pub fn register(
        &self,
        topology: &ShardTopology,
        serving: &dyn ShardIndexServing,
    ) -> Result<ShardedIndexRegistration, ShardedExecutionError> {
        let mut per_shard = Vec::new();
        for shard in topology.table().shards() {
            let owner = topology.owner_of(shard).ok_or(ShardedExecutionError::Routing(
                ShardingError::ShardWithoutOwner { shard },
            ))?;
            per_shard.push(
                match serving.register_explicit(&owner, &self.namespace, &self.definition) {
                    Ok(epoch) => ShardRegistration::Registered { shard, epoch },
                    Err(error) => ShardRegistration::Refused { shard, error },
                },
            );
        }
        Ok(ShardedIndexRegistration { per_shard })
    }

    /// Reads every shard's published state for this definition's namespace.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::Routing`] when the seated pair stops
    /// covering a shard the table maps buckets to, and
    /// [`ShardedExecutionError::IndexShard`] when one shard's endpoint
    /// refuses the read.
    pub fn read(
        &self,
        topology: &ShardTopology,
        serving: &dyn ShardIndexServing,
    ) -> Result<ShardedIndexView, ShardedExecutionError> {
        let mut per_shard = Vec::new();
        for shard in topology.table().shards() {
            let owner = topology.owner_of(shard).ok_or(ShardedExecutionError::Routing(
                ShardingError::ShardWithoutOwner { shard },
            ))?;
            let state = match serving.published_index(&owner, &self.namespace) {
                Ok(Some(published)) => ShardIndexState::Registered {
                    shard,
                    published: published.published(),
                    covered_sequence: published.covered_sequence(),
                },
                Ok(None) => ShardIndexState::Unregistered { shard },
                Err(error) => {
                    return Err(ShardedExecutionError::IndexShard { shard, error });
                }
            };
            per_shard.push(state);
        }
        Ok(ShardedIndexView { namespace: self.namespace.clone(), per_shard })
    }
}

/// The per-shard outcome of one sharded registration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardRegistration {
    /// The shard's catalog registered the definition at this epoch.
    Registered {
        /// The shard that registered.
        shard: ShardId,
        /// The namespace's epoch at that shard.
        epoch: u64,
    },
    /// The shard's endpoint refused the registration.
    Refused {
        /// The shard that refused.
        shard: ShardId,
        /// The endpoint's refusal.
        error: ShardServingError,
    },
}

impl ShardRegistration {
    /// Returns the shard this outcome names.
    #[must_use]
    pub fn shard(&self) -> ShardId {
        match self {
            Self::Registered { shard, .. } | Self::Refused { shard, .. } => *shard,
        }
    }
}

/// The outcome of registering one sharded index definition across a seated
/// table: every shard's result, in the table's canonical shard order.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardedIndexRegistration {
    per_shard: Vec<ShardRegistration>,
}

impl ShardedIndexRegistration {
    /// Returns every shard's outcome, in canonical shard order.
    #[must_use]
    pub fn per_shard(&self) -> &[ShardRegistration] {
        &self.per_shard
    }

    /// Reports whether every seated shard accepted the definition.
    #[must_use]
    pub fn is_uniform(&self) -> bool {
        self.per_shard.iter().all(|outcome| matches!(outcome, ShardRegistration::Registered { .. }))
    }

    /// Returns the shards that accepted the definition with their epochs, in
    /// canonical shard order.
    #[must_use]
    pub fn registered_epochs(&self) -> Vec<(ShardId, u64)> {
        self.per_shard
            .iter()
            .filter_map(|outcome| match outcome {
                ShardRegistration::Registered { shard, epoch } => Some((*shard, *epoch)),
                ShardRegistration::Refused { .. } => None,
            })
            .collect()
    }
}

// ---------------------------------------------------------------------------
// Readers
// ---------------------------------------------------------------------------

/// The sharded reader's view of one index namespace: every seated shard's
/// state and the composed verdict a reader acts on.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardedIndexView {
    namespace: IndexNamespace,
    per_shard: Vec<ShardIndexState>,
}

impl ShardedIndexView {
    /// Returns the namespace this view reports.
    #[must_use]
    pub fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns every seated shard's state, in canonical shard order.
    #[must_use]
    pub fn per_shard(&self) -> &[ShardIndexState] {
        &self.per_shard
    }

    /// Returns the least advanced published generation of the namespace
    /// across the whole table, or `None` when any shard holds no definition,
    /// holds one but has published nothing, or the table seats no shard.
    ///
    /// This is the reader's composed basis, the same quantity a fanned-out
    /// query's composed staleness reports as its accepted generation: the
    /// least advanced shard bounds the namespace for the whole table, and a
    /// shard that published nothing leaves the table without a uniform basis
    /// rather than reporting the published shards as if they were the table.
    #[must_use]
    pub fn least_advanced(&self) -> Option<ObservedGeneration> {
        if !self.is_uniformly_published() {
            return None;
        }
        self.per_shard
            .iter()
            .filter_map(|state| match state {
                ShardIndexState::Registered { published: Some(published), .. } => Some(*published),
                ShardIndexState::Registered { published: None, .. }
                | ShardIndexState::Unregistered { .. } => None,
            })
            .min_by_key(|generation| (generation.epoch(), generation.generation()))
    }

    /// Reports whether every seated shard holds the definition.
    #[must_use]
    pub fn is_uniformly_registered(&self) -> bool {
        self.per_shard.iter().all(|state| matches!(state, ShardIndexState::Registered { .. }))
    }

    /// Reports whether every seated shard holds the definition and has
    /// published a segment for it.
    #[must_use]
    pub fn is_uniformly_published(&self) -> bool {
        self.per_shard
            .iter()
            .all(|state| matches!(state, ShardIndexState::Registered { published: Some(_), .. }))
    }

    /// Returns the shards that do not hold the definition, in canonical
    /// order.
    #[must_use]
    pub fn unregistered_shards(&self) -> Vec<ShardId> {
        self.per_shard
            .iter()
            .filter(|state| matches!(state, ShardIndexState::Unregistered { .. }))
            .map(ShardIndexState::shard)
            .collect()
    }
}

/// One seated shard's state for one index namespace.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardIndexState {
    /// The definition is registered at this shard, with its published state.
    Registered {
        /// The shard holding the definition.
        shard: ShardId,
        /// The newest published generation, when one exists.
        published: Option<ObservedGeneration>,
        /// The storage sequence that publication covers, when one was
        /// published.
        covered_sequence: Option<u64>,
    },
    /// The namespace carries no definition at this shard.
    Unregistered {
        /// The shard without the definition.
        shard: ShardId,
    },
}

impl ShardIndexState {
    /// Returns the shard this state names.
    #[must_use]
    pub fn shard(&self) -> ShardId {
        match self {
            Self::Registered { shard, .. } | Self::Unregistered { shard } => *shard,
        }
    }
}
