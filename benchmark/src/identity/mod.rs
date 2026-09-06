//! ULID identities backed by the `ulid` crate (ADR 0027).
//!
//! Values use 16 binary bytes internally and 26 uppercase Crockford Base32
//! characters at public boundaries. [`from_text`] enforces canonical input.
//! Typed entity IDs prevent mixing identities from different domains.
//! [`generate()`] shares the crate's monotonic generator across threads.
//! ULID timestamps are metadata; they do not establish causality or authority.

mod generate;
mod ulid;

#[cfg(test)]
mod tests;

pub use generate::generate;
pub use ulid::ENTROPY_BITS;
pub use ulid::MAX_ENTROPY;
pub use ulid::MAX_TIMESTAMP_MILLIS;
pub use ulid::TIMESTAMP_BITS;
pub use ulid::ULID_BINARY_BYTES;
pub use ulid::ULID_TEXT_LENGTH;
pub use ulid::UlidError;
pub use ulid::{Ulid, from_binary, from_text};

use std::cmp::Ordering;
use std::fmt;
use std::str::FromStr;
/// Declares one typed entity identity of the ADR 0027 entity list.
///
/// The generated type is a typed identity containing the upstream [`Ulid`]
/// value: same codec, same comparison, same generator, and no conversion to
/// any other entity type. Only the ADR's list may grow this macro's
/// invocations — a new entity type is an identity-surface decision, not a
/// local convenience.
macro_rules! entity_ulid {
    ($name:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name(Ulid);

        impl $name {
            /// Mints a fresh identity from the process-wide cryptographically
            /// secure monotonic generator (ADR 0027 'Generation').
            ///
            /// Every typed constructor in this module draws from the one
            /// process generator, so the process's identities are strictly
            /// increasing within a millisecond and independent across
            /// processes and Regions. Callers cannot supply their own random
            /// source or format.
            #[must_use]
            pub fn generate() -> Self {
                Self(generate())
            }

            /// Parses the one accepted text form: exactly
            /// [`ULID_TEXT_LENGTH`] canonical uppercase Crockford characters.
            ///
            /// # Errors
            ///
            /// Returns the refusal classes of the strict ULID parser: wrong
            /// length, ambiguous characters, non-canonical text (including
            /// lowercase), and 26-character values that overflow 128 bits. A
            /// malformed caller-supplied identity is a typed refusal, never a
            /// repaired or replaced value (ADR 0027 'Generation').
            pub fn from_text(text: &str) -> Result<Self, UlidError> {
                Ok(Self(from_text(text)?))
            }

            /// Parses the one accepted binary form: exactly
            /// [`ULID_BINARY_BYTES`] bytes, unsigned big-endian.
            ///
            /// # Errors
            ///
            /// Returns [`UlidError::WrongBinaryLength`] for any other byte
            /// length; the owning protocol boundary refuses trailing and
            /// truncated frames before this call.
            pub fn from_binary(bytes: &[u8]) -> Result<Self, UlidError> {
                Ok(Self(from_binary(bytes)?))
            }

            /// Constructs the identity from a decoded 16-byte wire frame.
            /// Every 16-byte pattern is a representable ULID, so the
            /// frame-validated wire path is infallible.
            #[must_use]
            pub const fn from_wire_bytes(bytes: [u8; ULID_BINARY_BYTES]) -> Self {
                Self(Ulid::from_bytes(bytes))
            }

            /// Constructs the identity from an already-validated [`Ulid`]
            /// value, for example one decoded by a shared codec.
            #[must_use]
            pub const fn from_value(value: Ulid) -> Self {
                Self(value)
            }

            /// Returns the 16-byte binary storage key. Storage keys embed
            /// these bytes directly and retain no second string copy
            /// (ADR 0027 'Storage and sharding').
            #[must_use]
            pub const fn as_bytes(self) -> [u8; ULID_BINARY_BYTES] {
                self.0.to_bytes()
            }

            /// Returns the one accepted 26-character uppercase text form.
            #[must_use]
            pub fn to_text(self) -> String {
                self.0.to_string()
            }

            /// Writes the one accepted text form into a caller-owned buffer;
            /// the allocation-free path for logs and protocol encoders.
            #[must_use]
            pub fn write_text(self, buffer: &mut [u8; ULID_TEXT_LENGTH]) -> &mut str {
                self.0.array_to_str(buffer)
            }

            /// Returns the 48-bit Unix-millisecond metadata component. Never
            /// trusted for causality, authorization, conflict resolution,
            /// expiry, durability, or proof of creation time (ADR 0027).
            #[must_use]
            pub const fn timestamp_millis(self) -> u64 {
                self.0.timestamp_ms()
            }

            /// Returns the upstream [`Ulid`] value.
            #[must_use]
            pub const fn value(self) -> Ulid {
                self.0
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                fmt::Display::fmt(&self.0, formatter)
            }
        }

        impl FromStr for $name {
            type Err = UlidError;

            fn from_str(text: &str) -> Result<Self, Self::Err> {
                Self::from_text(text)
            }
        }
    };
}

entity_ulid!(
    ClusterUlid,
    "The identity of one TerraBase cluster. A restore mints a new one rather than reusing the source cluster's (ADR 0027)."
);
entity_ulid!(
    TenantUlid,
    "The identity of one tenant. The engine's logical `TenantId` text remains a route name, never a storage key."
);
entity_ulid!(
    ProjectUlid,
    "The identity of one Project, the tenant-scoped grouping above databases."
);
entity_ulid!(DatabaseUlid, "The identity of one database inside its Project.");
entity_ulid!(PrincipalUlid, "The identity of one authenticated end-user or service principal.");
entity_ulid!(
    AuthorizationPolicyUlid,
    "The identity of one versioned authorization policy publication."
);
entity_ulid!(PrincipalSessionUlid, "The identity of one authenticated principal session binding.");
entity_ulid!(PrincipalGrantUlid, "The identity of one signed principal grant.");
entity_ulid!(
    CollectionInstanceUlid,
    "The identity of one collection instance. A collection *name* is a public route name, not an entity ID (ADR 0027)."
);
entity_ulid!(DocumentUlid, "The identity of one document.");
entity_ulid!(
    DocumentIncarnationUlid,
    "The identity of one document incarnation, distinct from the document identity it belongs to."
);
entity_ulid!(RelationshipEdgeUlid, "The identity of one native relationship edge (ADR 0021).");
entity_ulid!(MemberUlid, "The identity of one Member of a replica set.");
entity_ulid!(
    ReplicaUlid,
    "The identity of one replica. A causal dot remains `(ReplicaUlid, ReplicaEpoch, OriginCounter)` (ADR 0027)."
);
entity_ulid!(
    PartitionUlid,
    "The identity of one logical Partition. Bucket numbers and shard ranges are quantities, not identities."
);
entity_ulid!(
    SchemaCatalogEntryUlid,
    "The identity of one schema catalog entry. A schema digest is content identity, never an entity ID (ADR 0027)."
);
entity_ulid!(
    AutoIndexUlid,
    "The identity of one automatic-index instance (ADR 0010). Its canonical definition carries a separate `AutoIndexDefinitionDigest`."
);
entity_ulid!(ViewPlanUlid, "The identity of one view plan.");
entity_ulid!(SubscriptionUlid, "The identity of one reactive subscription (ADR 0044).");
entity_ulid!(
    CommitUlid,
    "The identity of one commit. A mutation tag remains `(CommitUlid, MutationOrdinal)` (ADR 0027)."
);
entity_ulid!(SnapshotUlid, "The identity of one snapshot.");
entity_ulid!(BackupUlid, "The identity of one backup.");
entity_ulid!(RestoreUlid, "The identity of one restore operation.");
entity_ulid!(
    LongRunningOperationUlid,
    "The identity of one durable long-running operation (ADR 0028)."
);
entity_ulid!(
    ClientRequestUlid,
    "The identity of one client request: the retry identity an API never silently replaces (ADR 0022 uses its timestamp only for the accepted age and future-skew window)."
);
entity_ulid!(
    CorrelationUlid,
    "The correlation identity that ties one request's diagnostics together."
);
entity_ulid!(
    CausationUlid,
    "The causation identity linking an effect to the request that caused it."
);

/// Compares two 16-byte identity storage keys in unsigned lexicographic byte
/// order, which is the ULID chronological order (ADR 0027 'Storage and
/// sharding'). This is exactly the order [`Ord`] on every identity type
/// already gives; the helper exists for call sites that hold raw key bytes —
/// cache keys, index cursors, WAL ordering — so they cannot accidentally
/// compare a prefix, a text form, or a signed reinterpretation.
#[must_use]
pub fn compare_storage_keys(
    left: &[u8; ULID_BINARY_BYTES],
    right: &[u8; ULID_BINARY_BYTES],
) -> Ordering {
    left.cmp(right)
}
