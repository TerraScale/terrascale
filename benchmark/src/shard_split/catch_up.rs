//! Publication-journal catch-up interfaces.

/// One journal operation in the moved key range.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalEntry {
    sequence: u64,
    key: Vec<u8>,
    value: Vec<u8>,
}

impl JournalEntry {
    /// Creates a journal entry containing opaque canonical bytes.
    #[must_use]
    pub fn new(sequence: u64, key: Vec<u8>, value: Vec<u8>) -> Self {
        Self { sequence, key, value }
    }

    /// Returns the global journal sequence.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Returns the canonical key used for range filtering.
    #[must_use]
    pub fn key(&self) -> &[u8] {
        &self.key
    }

    /// Returns the opaque operation/document bytes.
    #[must_use]
    pub fn value(&self) -> &[u8] {
        &self.value
    }
}

/// Reader seam for the durable publication journal.
pub trait JournalReader {
    /// Reader-specific I/O failure.
    type Error;

    /// Returns entries strictly after `after`, in sequence order.
    fn read_after(&mut self, after: u64, limit: usize) -> Result<Vec<JournalEntry>, Self::Error>;
}

/// Target seam for idempotent journal application.
pub trait JournalWriter {
    /// Writer-specific I/O failure.
    type Error;

    /// Applies a contiguous batch durably before returning.
    fn apply_journal(&mut self, entries: &[JournalEntry]) -> Result<(), Self::Error>;
}

/// Catch-up protocol failure detected before cutover.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CatchUpError {
    /// The journal reader failed before returning a batch.
    ReaderFailed,
    /// The target failed to durably apply a batch.
    WriterFailed,
    /// A journal sequence was not strictly greater than the applied mark.
    SequenceRegressed {
        /// Last applied sequence.
        previous: u64,
        /// Invalid returned sequence.
        returned: u64,
    },
    /// A journal batch was not strictly increasing.
    SequenceOutOfOrder,
    /// A journal entry had an empty key.
    EmptyKey,
}

/// Resumable journal tail state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalCatchUp {
    applied_through: u64,
    upper: Option<Vec<u8>>,
}

impl JournalCatchUp {
    /// Starts tailing after the supplied durable checkpoint.
    #[must_use]
    pub const fn new(applied_through: u64) -> Self {
        Self { applied_through, upper: None }
    }

    /// Starts tailing a moved range below the upper-exclusive `upper` bound.
    pub fn for_range(applied_through: u64, upper: Vec<u8>) -> Result<Self, CatchUpError> {
        if upper.is_empty() {
            return Err(CatchUpError::EmptyKey);
        }
        Ok(Self { applied_through, upper: Some(upper) })
    }

    /// Returns the last successfully applied sequence.
    #[must_use]
    pub const fn applied_through(&self) -> u64 {
        self.applied_through
    }

    /// Reads, validates, and applies one bounded journal batch.
    ///
    /// The target is called only after all sequence and key checks pass, so a
    /// malformed response cannot partially advance the catch-up checkpoint.
    pub fn step<R, W>(
        &mut self,
        reader: &mut R,
        writer: &mut W,
        limit: usize,
    ) -> Result<usize, CatchUpError>
    where
        R: JournalReader,
        W: JournalWriter,
    {
        let entries = reader
            .read_after(self.applied_through, limit.max(1))
            .map_err(|_| CatchUpError::ReaderFailed)?;
        let mut previous = self.applied_through;
        for entry in &entries {
            if entry.sequence() <= previous {
                return Err(CatchUpError::SequenceRegressed {
                    previous,
                    returned: entry.sequence(),
                });
            }
            if entry.key().is_empty() {
                return Err(CatchUpError::EmptyKey);
            }
            previous = entry.sequence();
        }
        if entries.windows(2).any(|pair| pair[0].sequence() >= pair[1].sequence()) {
            return Err(CatchUpError::SequenceOutOfOrder);
        }
        let selected: Vec<_> = entries
            .iter()
            .filter(|entry| self.upper.as_deref().is_none_or(|upper| entry.key() < upper))
            .cloned()
            .collect();
        if !selected.is_empty() {
            writer.apply_journal(&selected).map_err(|_| CatchUpError::WriterFailed)?;
            self.applied_through =
                entries.last().map_or(self.applied_through, JournalEntry::sequence);
        }
        Ok(entries.len())
    }
}
