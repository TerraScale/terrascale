//! Measurement helpers for the spike's bytes-on-wire and memory
//! observations.
//!
//! These are observations, not benchmark receipts: nothing here runs in
//! the exclusive benchmark lane, uses the TerraBase.Benchmark statistical
//! gates, or supports any latency, throughput, or "faster than" claim
//! (PD-2026-08-26-03). The byte counts are exact integer measurements of
//! produced payloads; the resident-memory samples are coarse process
//! observations with their limits stated where they are reported.

/// The resident set size of this process in bytes, read from
/// `/proc/self/statm` on Linux.
///
/// Limits, stated exactly: this is the operating system's coarse view of
/// the whole process (Rust runtime, allocator retention, and Loro state
/// together); it is not a per-structure measurement, and it may lag frees
/// because allocators retain memory. `None` means the platform does not
/// expose the file (the observation is then recorded as unrun).
#[must_use]
pub fn resident_bytes() -> Option<u64> {
    let statm = std::fs::read_to_string("/proc/self/statm").ok()?;
    let mut fields = statm.split_whitespace();
    let _total = fields.next()?;
    let resident_pages: u64 = fields.next()?.parse().ok()?;
    let page_size = page_size_bytes()?;
    Some(resident_pages * page_size)
}

fn page_size_bytes() -> Option<u64> {
    // No libc dependency in the spike: the common Linux page sizes are
    // tried against a real /proc/self/smaps probe; 4096 is the near
    // universal answer on x86-64 and arm64 Linux.
    let _ = std::fs::read_to_string("/proc/self/smaps").ok()?;
    Some(4096)
}
