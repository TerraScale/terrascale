//! Range backfill interfaces.

/// One opaque canonical key and its canonical document bytes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CopyEntry {
    key: Vec<u8>,
    value: Vec<u8>,
}

impl CopyEntry {
    /// Creates one copy entry. Empty keys are refused by the source contract.
    #[must_use]
    pub fn new(key: Vec<u8>, value: Vec<u8>) -> Self {
        Self { key, value }
    }

    /// Returns the canonical key.
    #[must_use]
    pub fn key(&self) -> &[u8] {
        &self.key
    }

    /// Returns the opaque canonical document bytes.
    #[must_use]
    pub fn value(&self) -> &[u8] {
        &self.value
    }
}

/// One bounded backfill response, checkpointed at a journal sequence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CopyBatch {
    entries: Vec<CopyEntry>,
    through: u64,
    done: bool,
}

/// Durable cursor for restarting a range copy without rescanning applied
/// rows. The target apply operation remains idempotent if a crash happens
/// between its commit and persistence of this cursor.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CopyCheckpoint {
    lower: Option<Vec<u8>>,
    upper: Vec<u8>,
    after_key: Option<Vec<u8>>,
    through: u64,
    copied: u64,
    done: bool,
}

impl CopyCheckpoint {
    /// Returns the lower-inclusive bound, when the range is bounded below.
    #[must_use]
    pub fn lower(&self) -> Option<&[u8]> {
        self.lower.as_deref()
    }
    /// Returns the upper-exclusive bound.
    #[must_use]
    pub fn upper(&self) -> &[u8] {
        &self.upper
    }

    /// Returns the last copied key, if any.
    #[must_use]
    pub fn after_key(&self) -> Option<&[u8]> {
        self.after_key.as_deref()
    }

    /// Returns the source sequence represented by the copy.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.through
    }

    /// Returns the number of entries applied.
    #[must_use]
    pub const fn copied(&self) -> u64 {
        self.copied
    }

    /// Returns whether the source declared the range complete.
    #[must_use]
    pub const fn done(&self) -> bool {
        self.done
    }
}

impl CopyBatch {
    /// Creates a response for a range-copy page.
    #[must_use]
    pub fn new(entries: Vec<CopyEntry>, through: u64, done: bool) -> Self {
        Self { entries, through, done }
    }

    /// Returns entries in canonical key order.
    #[must_use]
    pub fn entries(&self) -> &[CopyEntry] {
        &self.entries
    }

    /// Returns the source sequence represented by this page.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.through
    }

    /// Returns whether this page completes the backfill.
    #[must_use]
    pub const fn done(&self) -> bool {
        self.done
    }
}

/// Failure detected while driving a range copy.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CopyError {
    /// The source failed before returning a page.
    SourceFailed,
    /// The target failed to durably apply a page.
    TargetFailed,
    /// A non-terminal empty page repeated the current checkpoint.
    NoProgress,
    /// A source returned a page that regressed its sequence checkpoint.
    CheckpointRegressed {
        /// Last accepted checkpoint.
        previous: u64,
        /// Invalid returned checkpoint.
        returned: u64,
    },
    /// A source returned keys out of canonical order.
    KeysOutOfOrder,
    /// A source returned an empty key.
    EmptyKey,
}

/// Source-side range scan seam implemented by the storage worker.
pub trait RangeCopySource {
    /// Source-specific I/O failure.
    type Error;

    /// Reads one bounded page at or above `lower`, strictly after `after_key`,
    /// and below `upper`.
    ///
    /// # Errors
    ///
    /// Returns [`Self::Error`] when the source scan fails.
    fn read_range(
        &mut self,
        lower: Option<&[u8]>,
        after_key: Option<&[u8]>,
        upper: &[u8],
        limit: usize,
    ) -> Result<CopyBatch, Self::Error>;
}

/// Target-side idempotent apply seam implemented by the storage worker.
pub trait RangeCopyTarget {
    /// Target-specific I/O failure.
    type Error;

    /// Applies one page transactionally and durably.
    ///
    /// # Errors
    ///
    /// Returns [`Self::Error`] when the page cannot be applied durably.
    fn apply_copy_batch(&mut self, batch: &CopyBatch) -> Result<(), Self::Error>;
}

/// Resumable, bounded range-copy driver.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RangeCopier {
    lower: Option<Vec<u8>>,
    upper: Vec<u8>,
    after_key: Option<Vec<u8>>,
    through: u64,
    copied: u64,
    done: bool,
}

impl RangeCopier {
    /// Starts a copy of keys below `upper`.
    ///
    /// # Errors
    ///
    /// Returns [`CopyError::EmptyKey`] when `upper` is empty.
    pub fn new(lower: Option<Vec<u8>>, upper: Vec<u8>) -> Result<Self, CopyError> {
        if upper.is_empty()
            || lower.as_deref().is_some_and(|lower| lower.is_empty() || lower >= upper.as_slice())
        {
            return Err(CopyError::EmptyKey);
        }
        Ok(Self { lower, upper, after_key: None, through: 0, copied: 0, done: false })
    }

    /// Resumes from a previously durable cursor.
    pub fn resume(checkpoint: CopyCheckpoint) -> Result<Self, CopyError> {
        if checkpoint.upper.is_empty()
            || checkpoint
                .lower
                .as_deref()
                .is_some_and(|lower| lower.is_empty() || lower >= checkpoint.upper.as_slice())
            || checkpoint
                .after_key
                .as_deref()
                .is_some_and(|key| key.is_empty() || key >= checkpoint.upper.as_slice())
        {
            return Err(CopyError::EmptyKey);
        }
        Ok(Self {
            lower: checkpoint.lower,
            upper: checkpoint.upper,
            after_key: checkpoint.after_key,
            through: checkpoint.through,
            copied: checkpoint.copied,
            done: checkpoint.done,
        })
    }

    /// Returns the upper-exclusive range bound.
    #[must_use]
    pub fn upper(&self) -> &[u8] {
        &self.upper
    }

    /// Returns the lower-inclusive bound, when the range is bounded below.
    #[must_use]
    pub fn lower(&self) -> Option<&[u8]> {
        self.lower.as_deref()
    }

    /// Returns the last durable source checkpoint.
    #[must_use]
    pub const fn through(&self) -> u64 {
        self.through
    }

    /// Returns the number of entries durably applied so far.
    #[must_use]
    pub const fn copied(&self) -> u64 {
        self.copied
    }

    /// Returns whether the source has declared the range complete.
    #[must_use]
    pub const fn is_done(&self) -> bool {
        self.done
    }

    /// Returns the cursor that should be persisted after a successful step.
    #[must_use]
    pub fn checkpoint(&self) -> CopyCheckpoint {
        CopyCheckpoint {
            lower: self.lower.clone(),
            upper: self.upper.clone(),
            after_key: self.after_key.clone(),
            through: self.through,
            copied: self.copied,
            done: self.done,
        }
    }

    /// Copies and applies one bounded page, preserving its checkpoint.
    ///
    /// # Errors
    ///
    /// Returns [`CopyError::SourceFailed`] when the source scan fails,
    /// [`CopyError::CheckpointRegressed`] when the page checkpoint moves
    /// backwards, [`CopyError::NoProgress`] when a non-terminal empty page
    /// repeats the current checkpoint, [`CopyError::EmptyKey`] for an empty
    /// key, [`CopyError::KeysOutOfOrder`] for keys outside the range or out
    /// of canonical order, and [`CopyError::TargetFailed`] when the target
    /// cannot apply the page durably.
    pub fn step<S, T>(
        &mut self,
        source: &mut S,
        target: &mut T,
        limit: usize,
    ) -> Result<bool, CopyError>
    where
        S: RangeCopySource,
        T: RangeCopyTarget,
    {
        if self.done {
            return Ok(true);
        }
        let batch = source
            .read_range(self.lower.as_deref(), self.after_key.as_deref(), &self.upper, limit.max(1))
            .map_err(|_| CopyError::SourceFailed)?;
        if batch.through() < self.through {
            return Err(CopyError::CheckpointRegressed {
                previous: self.through,
                returned: batch.through(),
            });
        }
        if batch.entries().is_empty() && !batch.done() && batch.through() == self.through {
            return Err(CopyError::NoProgress);
        }
        let mut previous = self.after_key.as_deref();
        for entry in batch.entries() {
            if entry.key().is_empty() {
                return Err(CopyError::EmptyKey);
            }
            if self.lower.as_deref().is_some_and(|lower| entry.key() < lower)
                || entry.key() >= self.upper.as_slice()
                || previous.is_some_and(|key| entry.key() <= key)
            {
                return Err(CopyError::KeysOutOfOrder);
            }
            previous = Some(entry.key());
        }
        target.apply_copy_batch(&batch).map_err(|_| CopyError::TargetFailed)?;
        self.through = batch.through();
        self.copied = self.copied.saturating_add(batch.entries().len() as u64);
        if let Some(entry) = batch.entries().last() {
            self.after_key = Some(entry.key().to_vec());
        }
        self.done = batch.done();
        Ok(self.done)
    }
}
