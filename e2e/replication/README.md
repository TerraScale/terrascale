# Peer replication frontier proof

This request run needs a fresh two-region journey namespace (without the stack booted), the TerraBase build in
`$E2E_BUILD/terrabase`, `DOTNET_ROOT` and Node 24. CI runs it as the `replication frontier` job of the journey
workflow when any TerraBase build input, the journey environment, the CI or journey workflow or the proof changes.
It starts three TerraBase processes, `journey-1` (gru, 127.0.0.1), `journey-2` (fra, 127.0.0.2) and `journey-3`
(gru, 127.0.0.3), joined into one QUIC mesh by a peers file (`<node> <region> <host> <quicPort> <internalPort>`), and
stops them on completion. Nothing else runs: there is no broker and no object store.

    export DOTNET_ROOT=$HOME/.local/share/mise/dotnet-root
    export DOTNET_PROCESSOR_COUNT=4
    export E2E_REGIONS=2
    bash e2e/env/local.sh prepare
    bash e2e/env/local.sh start
    bash e2e/env/local.sh exec node e2e/replication/frontier.mjs
    bash e2e/env/local.sh stop

The run takes its ports from `env/ports.py` and drives every node over its internal HTTP/2 listener with the peer
credential. It creates one organization, project and database placed in `gru` and `fra`, waits until the database's
`/regions` status reports both regions healthy, and proves:

- (a) Peer CRDT convergence: 20 distinct-key inserts from gru and fra and three concurrent updates of one row from
  gru, fra and the second gru node converge to identical rows on all three nodes, the shared row holding one of the
  concurrent values.
- (b) Causal bookmarks: a write on fra read on the second gru node with its `TerraScale-Bookmark` returns the row on
  the first read, without polling. Every answer carries `TerraScale-Bookmark` and `TerraScale-Consistency` equal to
  its body.
- (c) Strong OCC: pairs of concurrent `TerraScale-Consistency: strong` increments of one row from gru and fra each
  commit at least once per pair; a refused one answers 409 `serialization_conflict` with SQLSTATE 40001, and at least
  one conflict is observed. Every node then holds a counter equal to the number of commits.
- (d) Anti-entropy: ten writes on gru while fra is stopped reach fra after it restarts.
- (e) A malformed `TerraScale-Bookmark` answers 400 `invalid_bookmark`.

Polling happens only where the outcome is eventual: node readiness (`/readyz`), peer health and cross-node
convergence, each with a deadline. Logs land in `$E2E_STATE/replication-*/logs`, which CI uploads on failure.
