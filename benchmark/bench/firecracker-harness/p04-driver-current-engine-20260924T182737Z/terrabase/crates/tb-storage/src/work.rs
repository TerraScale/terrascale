//! Cumulative work performed only while `Storage::scan` is active.

use std::cell::Cell;

/// Successful scan I/O and examined candidates since this shard was opened.
///
/// Each field is a saturating counter. A caller can snapshot `StorageStats`
/// immediately before and after one scan to attribute that scan's work.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ScanWork {
    /// Successful immutable-run file opens.
    pub run_file_opens: u64,
    /// Successful metadata calls on those run file handles.
    pub run_metadata_calls: u64,
    /// Complete run-header bytes read.
    pub run_header_bytes_read: u64,
    /// Complete run-footer bytes read.
    pub run_footer_bytes_read: u64,
    /// Complete run-directory bytes read.
    pub run_directory_bytes_read: u64,
    /// Complete run-block bytes read.
    pub run_block_bytes_read: u64,
    /// Checked run-block buffers discarded under scan cache pressure.
    pub run_block_cache_evictions: u64,
    /// Successful loads of a block already loaded by this cursor in this scan.
    pub run_block_rereads: u64,
    /// Mutable keys tested while finding the next scan key.
    pub mutable_keys_examined: u64,
    /// Run descriptors tested by `next_key`.
    pub next_key_descriptors_examined: u64,
    /// Run records tested by `next_key`.
    pub next_key_records_examined: u64,
    /// Calls to the point resolver for a selected scan key.
    pub point_lookup_calls: u64,
    /// Run descriptors tested by those point lookups.
    pub point_descriptors_examined: u64,
    /// Run records tested by those point lookups.
    pub point_records_examined: u64,
}

#[derive(Default)]
pub(crate) struct ScanCounters {
    active: Cell<bool>,
    work: Cell<ScanWork>,
}

pub(crate) struct ScanGuard<'a>(&'a ScanCounters);

impl Drop for ScanGuard<'_> {
    fn drop(&mut self) {
        self.0.active.set(false);
    }
}

impl ScanCounters {
    pub(crate) fn begin(&self) -> ScanGuard<'_> {
        debug_assert!(!self.active.get());
        self.active.set(true);
        ScanGuard(self)
    }

    pub(crate) fn snapshot(&self) -> ScanWork {
        self.work.get()
    }

    fn update(&self, update: impl FnOnce(&mut ScanWork)) {
        if self.active.get() {
            let mut work = self.work.get();
            update(&mut work);
            self.work.set(work);
        }
    }

    pub(crate) fn run_open(&self) {
        self.update(|work| work.run_file_opens = work.run_file_opens.saturating_add(1));
    }

    pub(crate) fn run_metadata(&self) {
        self.update(|work| work.run_metadata_calls = work.run_metadata_calls.saturating_add(1));
    }

    pub(crate) fn header_bytes(&self, bytes: usize) {
        self.update(|work| {
            work.run_header_bytes_read = work.run_header_bytes_read.saturating_add(bytes as u64);
        });
    }

    pub(crate) fn footer_bytes(&self, bytes: usize) {
        self.update(|work| {
            work.run_footer_bytes_read = work.run_footer_bytes_read.saturating_add(bytes as u64);
        });
    }

    pub(crate) fn directory_bytes(&self, bytes: usize) {
        self.update(|work| {
            work.run_directory_bytes_read =
                work.run_directory_bytes_read.saturating_add(bytes as u64);
        });
    }

    pub(crate) fn block_bytes(&self, bytes: usize) {
        self.update(|work| {
            work.run_block_bytes_read = work.run_block_bytes_read.saturating_add(bytes as u64);
        });
    }

    pub(crate) fn block_cache_eviction(&self) {
        self.update(|work| {
            work.run_block_cache_evictions = work.run_block_cache_evictions.saturating_add(1);
        });
    }

    pub(crate) fn block_reread(&self) {
        self.update(|work| work.run_block_rereads = work.run_block_rereads.saturating_add(1));
    }

    pub(crate) fn mutable_key(&self) {
        self.update(|work| {
            work.mutable_keys_examined = work.mutable_keys_examined.saturating_add(1);
        });
    }

    pub(crate) fn next_descriptor(&self) {
        self.update(|work| {
            work.next_key_descriptors_examined =
                work.next_key_descriptors_examined.saturating_add(1);
        });
    }

    pub(crate) fn next_record(&self) {
        self.update(|work| {
            work.next_key_records_examined = work.next_key_records_examined.saturating_add(1);
        });
    }

    pub(crate) fn point_lookup(&self) {
        self.update(|work| work.point_lookup_calls = work.point_lookup_calls.saturating_add(1));
    }

    pub(crate) fn point_descriptor(&self) {
        self.update(|work| {
            work.point_descriptors_examined = work.point_descriptors_examined.saturating_add(1);
        });
    }

    pub(crate) fn point_record(&self) {
        self.update(|work| {
            work.point_records_examined = work.point_records_examined.saturating_add(1);
        });
    }
}
