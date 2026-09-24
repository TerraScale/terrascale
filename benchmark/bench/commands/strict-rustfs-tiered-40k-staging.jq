# Remote WAL recovery staging is a sparse FileDurableLog allocation. Its
# allocation target is (logicalLength / quantum + 1) * quantum, so derive the
# qualification bound from the listed remote journal bytes and the same 64 MiB
# quantum. The final sample must show that temporary staging was reclaimed.

.RemoteJournalObjects.TotalBytes as $remote_bytes
| (($remote_bytes / $allocation_quantum | floor) + 1) as $allocated_chunks
| ($allocated_chunks * $allocation_quantum) as $derived_bound
| select(
    ($remote_bytes | type) == "number"
    and $remote_bytes > 0
    and (.PeakStagingBytes | type) == "number"
    and .PeakStagingBytes > 0
    and .PeakStagingBytes <= $derived_bound
    and (.LatestResources.StagingBytes | type) == "number"
    and .LatestResources.StagingBytes == 0
  )
| {
    verification: "passed",
    staging: {
      remoteJournalBytes: $remote_bytes,
      allocationQuantumBytes: $allocation_quantum,
      allocatedChunks: $allocated_chunks,
      derivedBoundBytes: $derived_bound,
      peakBytes: .PeakStagingBytes,
      finalBytes: .LatestResources.StagingBytes
    },
    note: "the derived bound follows FileDurableLog chunk preallocation; final temporary recovery staging must be zero"
  }
