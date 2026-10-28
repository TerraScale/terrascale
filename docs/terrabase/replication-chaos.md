# Replication chaos checks

TS-682 runs in the existing **Slow integration** workflow on main, nightly and
on demand. It remains non-blocking; the existing failure reporter files a
GitHub issue with the Slow log using `gh`. Run the whole lane locally with
`nice -n 19 make test-slow`.

To replay only these scenarios:

```sh
nice -n 19 make build
nice -n 19 dotnet run --project tests/TerraBase.Slow --no-build -- \
  --treenode-filter '/TerraBase.Slow/TerraBase.Slow.Replication/ReplicationChaosTests/*' \
  --minimum-expected-tests 5
```

Each seed (682 and 683) owns three nodes, data directories, ports, certificates
and scoped keys. Two nodes run the real Program through the Kestrel test
factory; the third runs the same Program in a separate process. Six directed
gRPC relays carry unmodified LZ4 envelopes and real receiver acknowledgements.
Catalog identities are provisioned through the existing fixture before faults.

The fixed schedule is:

1. Write a baseline on all three nodes and check convergence.
2. Cut both directions of the third node's links. Prove the connected pair
   exchanges writes and cannot read the isolated node's acknowledged write.
   Kill the isolated process without graceful shutdown, restart its original
   directory, verify that write locally, heal and check all replicas.
3. Shift the two in-process UTC clocks by +12 and -12 hours, then jump the first
   backward to -24 hours, writing throughout. Monotonic timers remain real.
4. Hold the first node's SQLite write executor behind an entered/release barrier.
   Prove its HTTP write is queued while the other pair exchanges writes. Cut
   active links, release storage, heal, write again and check all replicas.

Workload IDs and Bogus payloads depend only on seed, phase, node and ordinal.
Barriers drive faults; there are no timing sleeps establishing test ordering.
Production retries retain their jitter, so exact network timing is not replayed.

The bounded add-only history records invocation/completion order, node, phase,
ID, submitted JSON, status, ETag and acknowledged/indeterminate outcome. After
writers finish and every sender reports initialization and zero durable lag,
the checker follows every HTTP page and compares IDs, bodies and versions.
Every acknowledged write must exist unchanged on every node. Indeterminate
writes may exist, but invented IDs and altered bodies fail. Failure messages
include the seed and history. Integration checks deliberately delete or alter
an acknowledged document beyond the first page to prove checker rejection.
A failed-launch check validates the real child's exit cause, captured startup
diagnostic and completed cleanup ownership.

This is a Jepsen-style convergence and acknowledged-write preservation check
for unique, never-overwritten documents, not a linearizability claim. The
storage fault models stalled SQLite execution rather than physical disk
latency. Process kill tests crash recovery, not loss of power or disks. UTC
skew covers injected TimeProvider consumers; it does not alter the host clock
or TLS certificate time. Document merge versions are logical offsets.
