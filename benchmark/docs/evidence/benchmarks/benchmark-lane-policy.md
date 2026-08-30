# Benchmark lane policy

This policy is part of PRG-P1-05 evidence. A number is a host observation,
not a production claim, until the exact candidate passes the required
qualification gates.

## Comparable run requirements

- Both sides use the same seed document count, operation count per sample,
  sample count, concurrency, warm-up count, and noise threshold.
- Operation counts divide evenly across concurrency slots. Warm-up remainders
  are distributed one operation at a time across slots, so no warm-up work is
  silently omitted.
- Setup and seeding finish before timing. Warm-up executes real operations and
  is excluded from measured samples.
- Every slot opens one connection before warm-up and reuses it for all samples.
  Measured windows use start and end barriers so all slots share a window.
- Every measured operation must succeed. A failed warm-up or operation writes a
  `status: failed` result document and cannot be parsed as comparison data.
- A result document must contain exactly the configured number of samples.
  Each sample must report the configured operation count, positive wall time,
  finite ordered percentiles, and throughput consistent with operation count
  divided by wall time.
- A comparison is inconclusive when configurations differ or when one side is
  in-process and the other uses a network client boundary. This keeps client
  transport overhead from becoming an unlabelled product claim.

## Retention and reporting

The reference driver writes every attempt to a unique file under
`bench/results/`, using the deterministic name
`<target>-<workload>-<unix-nanoseconds>-<pid>-<attempt>.json`. It never replaces
an earlier successful or failed attempt. Both the engine-side reader and the
history renderer select the newest retained attempt by the timestamp and full
filename fields; filesystem modification time is only a fallback for a
manually named attempt that has no numeric timestamp. The legacy exact name
`<target>-<workload>.json` is considered only when no retained attempt exists.

The newest retained attempt is authoritative even when it reports `failed` or
is malformed. Such an attempt remains visible with `failed` or `invalid` status,
and the comparison is inconclusive; an older successful file must never mask
it. Historical files are never used to fill missing samples from a newer run.

Each renderer invocation creates a timestamped Markdown snapshot under
`docs/evidence/benchmarks/history/`. It uses exclusive creation and never
replaces an earlier report. The `--history-dir` option may move that retained
folder for an isolated test run, but the renderer accepts no single output file
that could overwrite benchmark history.

Each retained result names the observed server identity, endpoint, client
transport, kernel, logical CPU count, timestamp, configuration, limits, and
per-sample raw facts. Failed attempts retain the error and host facts but no
partial sample values.

PostgreSQL uses `synchronous_commit = on`, `fsync = on`, and
`full_page_writes = on`. RavenDB uses its shipped server acknowledgement
defaults. This durability difference remains a stated limitation, not an
unstated equivalence. The engine-side lane currently runs in-process, so its
wall-clock output is not comparable to the loopback reference output until a
common served boundary is measured.
