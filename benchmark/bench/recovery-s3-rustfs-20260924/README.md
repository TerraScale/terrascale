# Native S3-compatible recovery drill

This bounded C# harness uses the held TerraBase source snapshot and the local
RustFS container `terrabase-rustfs-20260922`. It reads only that container's
local access key and secret at runtime through `podman inspect`; credentials
are never included in the receipt. The harness creates an isolated temporary
bucket, deletes it after the run, and stores all measurement files here.

From `/home/mariogk/Projects/TerraScale/ts-benchmark`:

```text
nice -n 19 dotnet build bench/recovery-s3-rustfs-20260924/RecoveryS3Drill.csproj -c Release --artifacts-path /tmp/terrabase-recovery-s3-artifacts -p:SubjectRoot=/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase
nice -n 19 dotnet /tmp/terrabase-recovery-s3-artifacts/bin/RecoveryS3Drill/release/RecoveryS3Drill.dll --output /home/mariogk/Projects/TerraScale/ts-benchmark/bench/recovery-s3-rustfs-20260924/full-5 --subject-root /tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase --source-manifest /tmp/terrabase-heldout-snapshot-20260924T0340Z/source-manifests/terrabase.files.sha256 --samples 5 --archival-delay-ms 250 --s3-endpoint http://127.0.0.1:19000 --container-name terrabase-rustfs-20260922
```

The `--artifacts-path` keeps all build output outside the immutable source.
The runner verifies every listed source file before creating the bucket. Each
sample uses a distinct source ID in that bucket and verifies the full plus two
incremental chain, source loss, exact data, schema, and authority import.
Separate gates corrupt one full artifact, delete one incremental artifact, and
interrupt native S3 multipart upload after its first provider-confirmed part.
The upload is resumed by a new store instance from its persisted checkpoint.

The RPO clock ends at fault declaration and starts at the archived tip's
source timestamp. The RTO clock starts at fault declaration and ends after
restore, fresh reopen, exact data scan, schema registration, and authority
import. The fault includes a controlled source shutdown and directory removal;
this local loopback drill is not a WAN or power-loss qualification.

Completed evidence: [five-sample summary](full-5/summary.md),
[raw samples](full-5/samples.jsonl), [run identity](full-5/run-identity.json),
and [upload resume gate](full-5/upload-resume.json). `build.log` and
`full-5.log` retain command output. `smoke-2/` holds the preceding two-sample
functional smoke.
