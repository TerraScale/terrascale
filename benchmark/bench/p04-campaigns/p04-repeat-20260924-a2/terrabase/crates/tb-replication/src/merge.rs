//! A causal multi-value register for one encoded document.
//!
//! A state keeps the active siblings and the causal prefixes that removed old
//! siblings. The join rule retains an entry only if it exists in both states or
//! is not covered by the other state's context. That is the state-based MV
//! register rule; retaining the context is essential to prevent resurrection.
//! Superseded dots no longer retain their digests here; the durable event log
//! must verify duplicate identities before replay reaches this state.

use std::collections::BTreeMap;

use tb_core::{CollectionId, DatabaseId, DocumentId};

use crate::event::{
    CausalContext, CollectionBinding, CommitEvent, DocumentValue, EventDigest, EventId,
    ReplicationError, ReplicationLimits, ShardId,
};

#[derive(Clone, Debug, Eq, PartialEq)]
struct StoredVersion {
    digest: EventDigest,
    binding: CollectionBinding,
    value: DocumentValue,
}

/// A borrowed, unresolved version. A tombstone may coexist with a concurrent
/// replacement; callers must expose or reject that conflict explicitly.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DocumentVersion<'a> {
    /// Event dot that wrote this version.
    pub id: EventId,
    /// Immutable schema binding of this version.
    pub binding: CollectionBinding,
    /// Encoded replacement or deletion marker.
    pub value: &'a DocumentValue,
}

/// One document's normalized causal multi-value register.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentState {
    database: DatabaseId,
    shard: ShardId,
    collection: CollectionId,
    document: DocumentId,
    context: CausalContext,
    versions: BTreeMap<EventId, StoredVersion>,
}

impl DocumentState {
    /// Starts an empty state for one document.
    #[must_use]
    pub fn new(
        database: DatabaseId,
        shard: ShardId,
        collection: CollectionId,
        document: DocumentId,
    ) -> Self {
        Self {
            database,
            shard,
            collection,
            document,
            context: CausalContext::new(),
            versions: BTreeMap::new(),
        }
    }

    /// Returns the observed causal prefixes, including removed predecessors.
    #[must_use]
    pub const fn context(&self) -> &CausalContext {
        &self.context
    }

    /// Immutable collection identity in this document's key.
    #[must_use]
    pub const fn collection(&self) -> CollectionId {
        self.collection
    }

    /// Returns all visible concurrent values, including any tombstone sibling.
    #[must_use]
    pub fn versions(&self) -> impl ExactSizeIterator<Item = DocumentVersion<'_>> {
        self.versions.iter().map(|(&id, version)| DocumentVersion {
            id,
            binding: version.binding,
            value: &version.value,
        })
    }

    /// Adds one change from a validated whole-transaction event. The caller is
    /// responsible for atomic publication of all document states in that event.
    ///
    /// # Errors
    ///
    /// Returns an error for scope mismatch, a conflicting dot, or state limits.
    pub fn apply(
        &self,
        event: &CommitEvent,
        limits: ReplicationLimits,
    ) -> Result<Self, ReplicationError> {
        if self.database != event.id().database() || self.shard != event.id().shard() {
            return Err(ReplicationError::ScopeMismatch);
        }
        let change = event
            .changes()
            .iter()
            .find(|change| {
                change.binding.collection == self.collection && change.document == self.document
            })
            .ok_or(ReplicationError::ScopeMismatch)?;
        let mut delta = Self::new(self.database, self.shard, self.collection, self.document);
        delta.context = change.observed.clone();
        delta
            .context
            .observe(event.id().origin(), event.id().sequence());
        delta.versions.insert(
            event.id(),
            StoredVersion {
                digest: event.digest(),
                binding: change.binding,
                value: change.value.clone(),
            },
        );
        self.join(&delta, limits)
    }

    /// Computes the associative, commutative, idempotent join for valid states.
    /// A conflicting value/digest for one dot is corruption, not a tie-breaker.
    ///
    /// # Errors
    ///
    /// Returns an error for scope mismatch, a conflicting dot, or state limits.
    pub fn join(&self, other: &Self, limits: ReplicationLimits) -> Result<Self, ReplicationError> {
        let limits = limits.validate()?;
        if self.database != other.database
            || self.shard != other.shard
            || self.collection != other.collection
            || self.document != other.document
        {
            return Err(ReplicationError::ScopeMismatch);
        }

        let context = self.context.join(&other.context, limits.max_origins)?;
        let mut versions = BTreeMap::new();
        for (&id, version) in &self.versions {
            match other.versions.get(&id) {
                Some(other_version) => {
                    if version != other_version {
                        return Err(ReplicationError::ConflictingVersion);
                    }
                    versions.insert(id, version.clone());
                }
                None if !other.context.covers(id) => {
                    versions.insert(id, version.clone());
                }
                None => {}
            }
        }
        for (&id, version) in &other.versions {
            if !self.versions.contains_key(&id) && !self.context.covers(id) {
                versions.insert(id, version.clone());
            }
        }
        if versions.len() > limits.max_siblings {
            return Err(ReplicationError::LimitExceeded("document siblings"));
        }
        let mut payload_bytes = 0_usize;
        for version in versions.values() {
            let value_bytes = version.value.byte_len();
            if value_bytes > limits.max_value_bytes {
                return Err(ReplicationError::LimitExceeded("document value bytes"));
            }
            payload_bytes = payload_bytes
                .checked_add(value_bytes)
                .ok_or(ReplicationError::LimitExceeded("document payload bytes"))?;
            if payload_bytes > limits.max_payload_bytes {
                return Err(ReplicationError::LimitExceeded("document payload bytes"));
            }
        }
        Ok(Self {
            database: self.database,
            shard: self.shard,
            collection: self.collection,
            document: self.document,
            context,
            versions,
        })
    }
}
