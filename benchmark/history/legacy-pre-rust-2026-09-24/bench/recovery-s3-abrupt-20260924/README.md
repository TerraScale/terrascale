# Native RustFS abrupt source-loss drill

This C# harness uses the held TerraBase source snapshot and local RustFS over
loopback. It verifies the held source manifest before each run. A child process
creates a full plus two incremental backup chain and leaves the source engine
open after one later durable write. It writes a flushed readiness receipt and
waits. The parent sends SIGKILL with Process.Kill, waits for child exit,
deletes the source directory, then lists, plans, and restores the chain from
RustFS to a fresh local directory. The restored document set, schema
fingerprint, and imported automatic index decision authority are checked
against the child receipt. This is an abrupt process kill, not a clean engine
shutdown or a host power-loss test.

The run creates a distinct isolated RustFS bucket for each sample and deletes
it after that sample.
Credentials are obtained from the local container by podman inspect at
runtime and are absent from receipts. All measurement files are under this
benchmark directory; transient source and restore directories and build
artifacts stay outside the held source and are removed.

From /home/mariogk/Projects/TerraScale/ts-benchmark:

    nice -n 19 dotnet build bench/recovery-s3-abrupt-20260924/RecoveryS3AbruptDrill.csproj -c Release --artifacts-path /tmp/terrabase-recovery-s3-abrupt-artifacts -p:SubjectRoot=/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase
    nice -n 19 dotnet /tmp/terrabase-recovery-s3-abrupt-artifacts/bin/RecoveryS3AbruptDrill/release/RecoveryS3AbruptDrill.dll --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/recovery-s3-abrupt-20260924/full-5 --subject-root /tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase --source-manifest /tmp/terrabase-heldout-snapshot-20260924T0340Z/source-manifests/terrabase.files.sha256 --samples 5 --archival-delay-ms 250 --s3-endpoint http://127.0.0.1:19000 --container-name terrabase-rustfs-20260922

The RPO observation is archived tip source timestamp to kill invocation. The
RTO observation starts at kill invocation and includes kill wait, source
directory removal, object listing, restore planning and execution, fresh
reopen, full document scan, schema registration, and decision authority import.
Stage times and source, tip, and restored frontiers are recorded per sample.
These local observations do not establish a production RPO or RTO.

Completed smoke evidence: [summary](smoke-1/summary.md),
[raw sample](smoke-1/samples.jsonl), [kill evidence](smoke-1/sample-1-kill.json),
and [run identity](smoke-1/run-identity.json). The one sample passed; the
child exited 137 after SIGKILL, the source frontier was 37, and the backed and
restored frontier was 36. The exact scan found 32 documents and excluded the
one unarchived write.

The five-sample follow-up runner was built with one bucket per sample. Its
timed run was deferred because other build and test processes remained active
at the final clean-window check.
