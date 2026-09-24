# Remote durability evidence for the full tiered 40K qualification.
# The full workload validates the journal frontier during each local-root-loss
# rebuild, but does not serialize the final frontier scalar into its receipt.
# The acknowledged batch count and listed journal object count must therefore
# agree, and both recovery phases must be present.

.RemoteAck as $ack
| .RemoteJournalObjects as $journal
| select(
    ($ack.AcknowledgedBatches | type) == "number"
    and ($ack.AcknowledgedRecords | type) == "number"
    and ($journal.ObjectCount | type) == "number"
    and ($journal.TotalBytes | type) == "number"
    and $ack.AcknowledgedBatches > 0
    and $ack.AcknowledgedRecords >= $minimum_ack
    and $journal.ObjectCount == $ack.AcknowledgedBatches
    and $journal.ObjectCount > 0
    and $journal.TotalBytes > 0
    and (.SeedRootLossRecoverySeconds | type) == "number"
    and .SeedRootLossRecoverySeconds > 0
    and (.MutationRootLossRecoverySeconds | type) == "number"
    and .MutationRootLossRecoverySeconds > 0
  )
| {
    verification: "passed",
    remoteJournal: {
      acknowledgedBatches: $ack.AcknowledgedBatches,
      listedObjects: $journal.ObjectCount,
      acknowledgedRecords: $ack.AcknowledgedRecords,
      totalBytes: $journal.TotalBytes,
      batchesMatchListedObjects: ($ack.AcknowledgedBatches == $journal.ObjectCount)
    },
    frontierEvidence: {
      seedRootLossRecoverySeconds: .SeedRootLossRecoverySeconds,
      mutationRootLossRecoverySeconds: .MutationRootLossRecoverySeconds,
      validatedByRootLossRebuilds: true,
      note: "both local-root-loss phases rebuilt and validated the complete remote journal before exact scans; this workload does not serialize a final frontier scalar"
    }
  }
