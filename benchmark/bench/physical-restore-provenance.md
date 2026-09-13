# Local physical restore with imported provenance

Measured on the shared local x86_64 workspace, using the debug test build and
`nice -n 19`. The fixture seeds 100,000 documents, waits for the backup artifact,
then times `restore_to_location`. Setup time is excluded from the restore
duration below. These results do not qualify launch hardware or production
encrypted restore. The ordinary functional test reports this duration but does
not fail on it; the explicit timing gate is ignored and must be run in release
mode on the pinned benchmark host.

| Run | Restore duration | Recorded limit at run | Result |
| --- | ---: | ---: | --- |
| Provenance reset, full server suite | 13.421756086 s | 1 s | Failed |
| Provenance reset, isolated test | 13.338558436 s | 1 s | Failed |
| Same reset with a temporary request-identity index | 14.942165607 s | 1 s | Failed; candidate removed |
| Provenance reset with temporary stage timers | 13.3045 s | 1 s | Failed |
| One-pass rewrite through a temporary identity map | 11.698746991 s | 1 s | Failed; improved over baseline |
| One-pass rewrite with standard bounded SQLite connection settings | 11.779108996 s | 1 s | Failed |
| Same bounded settings, complete server regression suite (`26026`) | 11.562381852 s | 1 s | Failed |
| Remove redundant document identity `EXISTS` probe, complete server suite (`76543`) | 12.119408652 s | 1 s | Failed; no measured speedup |
| Remove duplicate operation-facts identity index, isolated test (`43182`) | 12.255299851 s | 1 s | Failed; no measured speedup |

The reset preserves imported data and causal progress while removing source
billing and lifecycle state. Removing that reset to improve timing would restore
the billing defect. The temporary-index candidate did not demonstrate a benefit;
the retained implementation has no such index.

The isolated command is:

```sh
nice -n 19 cargo test --locked --release -p terrabase-server --lib \
  backup::tests::branching_100k_documents_restore_timing_gate -- --exact --ignored
```

Forgejo runs this gate in `.forgejo/workflows/release-benchmark.yml` on every
`main` push and release tag (and it is available for an explicit dispatch).
That workflow verifies that the pinned runner has Litestream before starting;
missing benchmark infrastructure is a failed qualification rather than a
silent skipped test.

The baseline includes workspace revision `546affae`. The candidate's
`src/storage/sqlite/branch_provenance.rs` SHA-256 was
`ef8e051cc693186985b63497a4238167142ee439851676a3294846fe820ba3d1`.
Execution handles were `86471`, `7470`, `55229`, `60771`, `61219`, and `30040`,
respectively.
The one-pass rewrite's `branch_provenance.rs` SHA-256 was
`f13184507371aac9c8b8d8d3e8ccbcdd03ff03769ed9c4d8e0a1683e75eb27c2`.

The stage timers in `60771` reported cumulative elapsed times. All temporary
timers were removed after measurement.

| Point reached | Clock starts at | Cumulative elapsed |
| --- | --- | ---: |
| Backup copied | Backup import entry | 0.0116 s |
| Source validation complete | Backup import entry | 3.430 s |
| Prefix materialization complete | Provenance transform entry | 0.448 s |
| Identity rewrite complete | Provenance transform entry | 5.775 s |
| Provenance transform complete | Backup import entry | 9.925 s |
| Transaction committed | Backup import entry | 10.802 s |

Identity rewriting consumed approximately 5.327 seconds after prefix
materialization. Source validation consumed approximately 3.418 seconds after
the copy. The remaining total includes final checkpoint, verification reopen,
and publication; those stages were not individually timed.
