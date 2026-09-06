# Local physical restore with imported provenance

Measured on the shared local x86_64 workspace, using the debug test build and
`nice -n 19`. The fixture seeds 100,000 documents, waits for the backup artifact,
then times `restore_to_location`. Setup time is excluded from the restore
duration below. These results do not qualify launch hardware or production
encrypted restore.

| Run | Restore duration | Unchanged limit | Result |
| --- | ---: | ---: | --- |
| Provenance reset, full server suite | 13.421756086 s | 10 s | Failed |
| Provenance reset, isolated test | 13.338558436 s | 10 s | Failed |
| Same reset with a temporary request-identity index | 14.942165607 s | 10 s | Failed; candidate removed |

The reset preserves imported data and causal progress while removing source
billing and lifecycle state. Removing that reset to improve timing would restore
the billing defect. The temporary-index candidate did not demonstrate a benefit;
the retained implementation has no such index.

The isolated command is:

```sh
nice -n 19 cargo test --locked -p terrabase-server --lib \
  backup::tests::branching_100k_documents_restores_under_ten_seconds -- --exact
```

The baseline includes workspace revision `546affae`. The candidate's
`src/storage/sqlite/branch_provenance.rs` SHA-256 was
`ef8e051cc693186985b63497a4238167142ee439851676a3294846fe820ba3d1`.
Execution handles were `86471`, `7470`, and `55229`, respectively. Stage profiling
is still needed before attributing the elapsed time to a particular operation.
