# Held source local recovery drill

This C# harness uses the production `StorageEngineBackupSource`,
`BackupManager`, `FileSystemBackupObjectStore`, and `StorageEngineRestoreTarget`
from the immutable held source at
`/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase`. It verifies every
one of the 1,641 source file hashes before the first sample. The build uses an
external artifacts directory so neither the held source nor TerraBase contains
measurement output.

The existing `NativeRestoreProcessCampaign` stores evidence under TerraBase's
`.codex/evidence` path and records manifest age without a later unarchived
write. The existing `RestoreLagMeasurement` uses an in-memory object store and
does not inject source loss. This bounded harness supplies those missing local
RPO/RTO boundaries and keeps all measurement results here.

From `/home/mariogk/Projects/TerraScale/ts-benchmark`:

```text
nice -n 19 dotnet build bench/recovery-rpo-rto-20260924/RecoveryRpoRtoDrill.csproj -c Release --artifacts-path /tmp/terrabase-recovery-rpo-rto-artifacts -p:SubjectRoot=/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase
nice -n 19 dotnet /tmp/terrabase-recovery-rpo-rto-artifacts/bin/RecoveryRpoRtoDrill/release/RecoveryRpoRtoDrill.dll --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/recovery-rpo-rto-20260924/full-5 --subject-root /tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase --source-manifest /tmp/terrabase-heldout-snapshot-20260924T0340Z/source-manifests/terrabase.files.sha256 --samples 5 --archival-delay-ms 250
```

The [summary table](full-5/summary.md), [raw samples](full-5/samples.jsonl),
and [run identity](full-5/run-identity.json) are the completed measurement.
`build.log` and `full-5.log` are command output. The summary defines the fault,
RPO, RTO, data, schema, authority, hardware, and interpretation limits.
The fixture sets 5 minutes RPO and 30 minutes RTO in `BackupOptions`; the
observed local samples meet both configured values. This is a local drill,
not a production objective qualification.

The snapshot JSON's `sourceFilesManifestSha256` value differs from SHA-256 of
the raw `terrabase.files.sha256` bytes. No source for that field's encoding was
found. The receipt preserves both values and the per-file verification result;
it does not infer a snapshot envelope defect from the name alone.
