# Native package qualification

`check_native_package.py` consumes one immutable `.nupkg`; it never packs or
references the SDK project. Supply its exact normalized NuGet version (without
build metadata or leading zeros in numeric components), SHA-256 and target:

```bash
python3 scripts/qualification/check_native_package.py \
  --package /path/to/TerraScale.DotNet.VERSION.nupkg \
  --sha256 LOWERCASE_SHA256 --version VERSION --framework net10.0 \
  --receipt /owned/evidence/net10-consumer.json
```

Use a distinct receipt for `net11.0`. The runner checks package ID/version and
all six exact native contract/provenance entries. It copies only
`NativeConsumer.csproj`, `NativeConsumer.cs` and `NativeChecks.cs` into an
owned temporary directory. Restore maps `TerraScale.DotNet` exclusively to the
supplied local feed and uses NuGet.org for external dependencies, with fresh
owned caches. Each phase has a 120-second deadline, a 1 MiB output bound and
retained bounded output. The runner terminates a timed-out phase process group
and removes its scratch directory.

The installed public consumer checks canonical native values and atomic
vectors, the corrected producer create/update/delete example digests,
transaction-ID invariance, native reads/queries/commit/status handling,
original-identity recovery, authenticated Atlas routing boundaries, canonical
TerraScale-zone direct origins, independent provisioned/development origins
and installed public auth diagnostics with plain/labeled credential echoes.
Successful receipts identify package bytes and copied inputs; they report
`PASS_PACKAGE_PUBLIC_HANDLER_CONSUMER` and producer-native execution as
`NOT RUN`. This is package and local handler evidence, not a real-service
journey or publication claim.

Run this under the qualification coordinator's finite grant. The outer
coordinator must track child process groups and retain logs and receipts.
Do not rebuild a historical candidate or label its evidence as a changed
successor. [Package distribution](../../docs/native-package-distribution.md)
describes CI export and exact-byte verification.
