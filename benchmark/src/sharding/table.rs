//! The bucket-range table (CONTEXT: Bucket Range).
//!
//! The table is an exact, ordered, non-overlapping partition of the fixed
//! 2^20 bucket space into ranges mapped to shards, with adjacent-same-shard
//! entries merged (V1's merged-continuous rule). The Platform constructs it;
//! the engine validates it, routes through it, and edits it only through the
//! explicit bucket-move operation. A bucket move is the table edit of a
//! bucket migration: the streaming and restart machinery is SH-03, and the
//! ownership transfer that ends a move is the epoch-raised view transition
//! on [`ShardTopology`](crate::sharding::ShardTopology).

use super::{
    BUCKET_SPACE, Bucket, MAX_SHARDS, MIN_INITIAL_BUCKET_ALLOCATION, ShardId, ShardingError,
};

/// One entry of the bucket-range table: a contiguous `[start, end)` interval
/// of buckets mapped to one shard (CONTEXT: Bucket Range).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BucketRange {
    start: u32,
    end: u32,
    shard: ShardId,
}

impl BucketRange {
    /// Creates one bucket range over `[start, end)` for one shard.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::EmptyBucketRange`] when `start >= end` and
    /// [`ShardingError::BucketAboveSpace`] when `end` leaves the fixed
    /// 2^20 space.
    pub const fn try_new(start: u32, end: u32, shard: ShardId) -> Result<Self, ShardingError> {
        if start >= end {
            return Err(ShardingError::EmptyBucketRange { start, end });
        }
        if end > BUCKET_SPACE {
            return Err(ShardingError::BucketAboveSpace { value: end });
        }
        Ok(Self { start, end, shard })
    }

    /// Returns the inclusive start bucket.
    #[must_use]
    pub const fn start(&self) -> u32 {
        self.start
    }

    /// Returns the exclusive end bucket.
    #[must_use]
    pub const fn end(&self) -> u32 {
        self.end
    }

    /// Returns the shard this range maps to.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Reports whether this range covers one bucket.
    #[must_use]
    pub const fn contains(&self, bucket: Bucket) -> bool {
        bucket.get() >= self.start && bucket.get() < self.end
    }
}

/// The engine-validated bucket-range table: the partition of the fixed
/// bucket space that maps every bucket to exactly one shard.
///
/// Valid means: ordered, non-overlapping, no gaps, first entry starts at
/// zero, last entry ends at [`BUCKET_SPACE`], adjacent entries never share a
/// shard (merged), and at most [`MAX_SHARDS`] distinct shards. The table is
/// an immutable value; every edit is a validated transformation, and the
/// only edit this seam offers is the explicit
/// [`BucketRangeTable::with_bucket_moved`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BucketRangeTable {
    entries: Vec<BucketRange>,
}

impl BucketRangeTable {
    /// Validates one Platform-constructed table.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::EmptyTable`] for no entries,
    /// [`ShardingError::TableDoesNotStartAtZero`] when the first entry does
    /// not start at bucket zero, [`ShardingError::TableDoesNotCoverBucketSpace`]
    /// when the last entry ends below the space end,
    /// [`ShardingError::TableOverlap`] or [`ShardingError::TableGap`] when
    /// entries are not exactly consecutive,
    /// [`ShardingError::AdjacentShardsNotMerged`] when adjacent entries name
    /// one shard, and [`ShardingError::TooManyShards`] beyond
    /// [`MAX_SHARDS`].
    pub fn try_new(entries: &[BucketRange]) -> Result<Self, ShardingError> {
        if entries.is_empty() {
            return Err(ShardingError::EmptyTable);
        }
        if entries[0].start != 0 {
            return Err(ShardingError::TableDoesNotStartAtZero { first_start: entries[0].start });
        }
        let last = entries[entries.len() - 1];
        if last.end != BUCKET_SPACE {
            return Err(ShardingError::TableDoesNotCoverBucketSpace { last_end: last.end });
        }
        for pair in entries.windows(2) {
            let (previous, next) = (pair[0], pair[1]);
            if next.start < previous.end {
                return Err(ShardingError::TableOverlap { at: next.start });
            }
            if next.start > previous.end {
                return Err(ShardingError::TableGap { at: previous.end });
            }
            if next.shard == previous.shard {
                return Err(ShardingError::AdjacentShardsNotMerged { at: next.start });
            }
        }
        let shards = Self::distinct_shards(entries);
        if shards.len() > MAX_SHARDS {
            return Err(ShardingError::TooManyShards { count: shards.len() });
        }
        Ok(Self { entries: entries.to_vec() })
    }

    /// Returns the degenerate unsharded table: one shard owning the whole
    /// space (`0..2^20 -> shard/<number>`).
    #[must_use]
    pub fn single_shard(shard: ShardId) -> Self {
        Self { entries: vec![BucketRange { start: 0, end: BUCKET_SPACE, shard }] }
    }

    /// Returns the table's entries in canonical order.
    #[must_use]
    pub fn entries(&self) -> &[BucketRange] {
        &self.entries
    }

    /// Returns the distinct shards this table maps to, in canonical order.
    #[must_use]
    pub fn shards(&self) -> Vec<ShardId> {
        Self::distinct_shards(&self.entries)
    }

    /// Returns the shard one bucket maps to. A valid table is total over the
    /// fixed space, so every bucket resolves.
    ///
    /// # Panics
    ///
    /// Cannot panic for a table that reached this type through
    /// [`BucketRangeTable::try_new`]: a validated table covers every bucket
    /// of the fixed space.
    #[must_use]
    pub fn shard_of(&self, bucket: Bucket) -> ShardId {
        self.entries.iter().find(|entry| entry.contains(bucket)).map_or_else(
            || {
                unreachable!(
                    "a validated table covers every bucket of the fixed space, including {bucket:?}"
                )
            },
            BucketRange::shard,
        )
    }

    /// Returns this table's ranges for one shard, in canonical order (a
    /// shard may own several disjoint ranges).
    #[must_use]
    pub fn ranges_of(&self, shard: ShardId) -> Vec<BucketRange> {
        self.entries.iter().filter(|entry| entry.shard() == shard).copied().collect()
    }

    /// Returns the number of buckets one shard owns across all its ranges.
    #[must_use]
    pub fn bucket_count_of(&self, shard: ShardId) -> u64 {
        self.ranges_of(shard).iter().map(|range| u64::from(range.end - range.start)).sum()
    }

    /// Applies the table edit of one bucket move: the bucket passes from
    /// `source` to `destination`, ranges split and re-merge so the result is
    /// a valid partition again.
    ///
    /// This is the explicit engine operation a bucket migration ends with;
    /// it moves the table entry only — streaming the bucket's committed
    /// records is the replication-plane migration SH-03 owns, and the
    /// ownership transfer seats only through the epoch-raised transition on
    /// [`ShardTopology`](crate::sharding::ShardTopology). Moving a bucket to
    /// its current owner is an idempotent no-op.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::BucketNotOwnedBySource`] when `source` does
    /// not own the bucket, and re-validates the moved table with
    /// [`BucketRangeTable::try_new`] (a destination shard that would exceed
    /// [`MAX_SHARDS`] refuses there).
    pub fn with_bucket_moved(
        &self,
        bucket: Bucket,
        source: ShardId,
        destination: ShardId,
    ) -> Result<Self, ShardingError> {
        let owner = self.shard_of(bucket);
        if owner != source {
            return Err(ShardingError::BucketNotOwnedBySource { bucket, source, owner });
        }
        let mut pieces: Vec<BucketRange> = Vec::with_capacity(self.entries.len() + 2);
        for entry in &self.entries {
            if entry.contains(bucket) {
                if bucket.get() > entry.start {
                    pieces.push(BucketRange {
                        start: entry.start,
                        end: bucket.get(),
                        shard: entry.shard,
                    });
                }
                pieces.push(BucketRange {
                    start: bucket.get(),
                    end: bucket.get() + 1,
                    shard: destination,
                });
                if bucket.get() + 1 < entry.end {
                    pieces.push(BucketRange {
                        start: bucket.get() + 1,
                        end: entry.end,
                        shard: entry.shard,
                    });
                }
            } else {
                pieces.push(*entry);
            }
        }
        let merged = merge_adjacent(pieces);
        Self::try_new(&merged)
    }

    /// Checks the initial allocation guard: every shard the Platform seated
    /// in an initial (or rebalanced) construction received at least
    /// [`MIN_INITIAL_BUCKET_ALLOCATION`] buckets.
    ///
    /// A guard, not a runtime invariant: migration may transiently leave
    /// shards below the floor, so [`BucketRangeTable::try_new`] does not
    /// enforce it.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::InitialAllocationBelowFloor`] naming the
    /// first under-allocated shard.
    pub fn initial_allocation_guard(&self) -> Result<(), ShardingError> {
        for shard in self.shards() {
            let buckets = self.bucket_count_of(shard);
            if buckets < MIN_INITIAL_BUCKET_ALLOCATION {
                return Err(ShardingError::InitialAllocationBelowFloor {
                    shard,
                    buckets,
                    floor: MIN_INITIAL_BUCKET_ALLOCATION,
                });
            }
        }
        Ok(())
    }

    /// Returns the distinct shards of `entries` in canonical order.
    fn distinct_shards(entries: &[BucketRange]) -> Vec<ShardId> {
        let mut shards: Vec<ShardId> = Vec::new();
        for entry in entries {
            if !shards.contains(&entry.shard()) {
                shards.push(entry.shard());
            }
        }
        shards
    }
}

/// Merges adjacent pieces that map to one shard (V1's merged-continuous
/// rule), preserving order.
fn merge_adjacent(pieces: Vec<BucketRange>) -> Vec<BucketRange> {
    let mut merged: Vec<BucketRange> = Vec::with_capacity(pieces.len());
    for piece in pieces {
        if let Some(last) = merged.last_mut()
            && last.shard == piece.shard
            && last.end == piece.start
        {
            last.end = piece.end;
            continue;
        }
        merged.push(piece);
    }
    merged
}
