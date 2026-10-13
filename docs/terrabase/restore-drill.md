# Backup and restore drill

The required TerraBase Fast job runs `RestoreDrillTests` automatically. Its local drill writes 24 known documents,
ships an encrypted snapshot and a subsequent CDC segment, deletes the live database file through the real engine,
restores through the management PITR endpoint, and compares all 4096 anti-entropy bucket digests. It also reads every
one of the 25 recovered documents through the client listener. Each test owns its server, data directory and object store;
TUnit's parallel execution remains unchanged.

Run only the drill locally:

```sh
~/.local/bin/ts-heavy make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/RestoreDrillTests/*'
```

This is an integration test with an in-process S3 transport, not evidence of Tigris or Fly recovery. Timing output is
`RESTORE_DRILL documents=25 backup_ms=... restore_ms=... digest=...` in the test's output, retained by the HTML report.
Measured results belong in `benchmark/bench/`.

## Catalog restore gate

An older `_system.db` can contain revoked API keys, deleted memberships and removed databases. Its storage format being
supported says nothing about its security freshness. Every server starts with the catalog gate closed, before catalog
cleanup or platform seeding. While closed, requests that authorize from catalog state and `/readyz` answer 503;
probes and the peer receiver remain available for recovery. Management services also refuse work through the catalog guard.

A new empty cluster initializes automatically when no other machine is listed. A new region with listed peers pulls their
catalog before platform seeding, even when it records a destroyed historical machine. Existing catalogs require an authenticated external anchor and/or verified
pulls from trusted peers. Anchors are encrypted with the platform backup keys under `catalog-anchor/<machine-id>` in Tigris;
format 1 contains the file incarnation, CDC head and UTC time. Snapshot copies receive new file incarnations;
roll-forward renews the incarnation again after replay, which can otherwise overwrite it with the source row. A different incarnation, an older head, unreadable anchor,
or a missing anchor on a non-genesis catalog does not establish trust. Restored catalogs cannot vouch for each other:
they do not export system digests or bucket pages, and do not send system CDC batches. A gate counts a peer only after
checking its trust, completing a forced anti-entropy pull and checking its trust again.

The gate conservatively waits for every recorded/listed machine, or, when its own anchor proves it was not restored,
a trusted majority. Fresh replacements require every listed peer to be trusted and successfully pulled; historical destroyed members cannot block a new file. A catalog proven older than its anchor remains closed for reviewed recovery; there is no request,
configuration switch or local-file bypass. Rebuilding a lost region should start with a fresh catalog replicated from
healthy peers and restore the tenant databases from backup. Do not open an old catalog to make the drill pass.

Revocations and destructive management routes advance the anchor before returning success. If that write fails, the
request answers 503 `catalog_anchor_unavailable`: the local commit may already exist, so retry the request. Management
commit envelopes containing revoking operations follow the same rule on both first execution and replay. Tenant document
writes and non-revoking management commits do not wait on the anchor. The background pass advances it every 15 seconds.

Inspect a machine's gate over its internal listener (use `fly ssh`, never the public hostname):

```sh
curl --http2-prior-knowledge http://<machine-private-address>:8081/v1/catalog-gate
```

The response reports `open`, `trusted`, `pending` and the SHA-256 digest of its catalog bucket digests. A catalog whose
security freshness cannot be established stays unavailable even if tenant backup restore succeeds. Continuous shipping also waits for an open gate. Cleanup skipped while
the gate is closed is reconsidered at the next verified restart. Identical create requests reuse live catalog records
without changing their versions; a missing local database is initialized only after the commit succeeds. Different
names, ownership, placement and deleted IDs remain conflicts. Shutdown-caused cancelled RPCs are normal cancellation;
other RPC faults remain visible.

## Production acceptance

Production work waits for COS `GO DRILL`, after T2's Fly greenfield rollout and the live replication matrix. Merging also
waits for T0's green main and the PR's required checks on its latest commit. No local timing or test result satisfies these
gates.

After authorization, retain timestamped Tigris object listings, the exact image/commit and all three machine identities.
Write known data and record each region's digest; ship it before deleting the chosen region's machine and volume. Recreate
that region, restore tenant data, compare all three digests, and prove replication in both directions. Inspect Fly volume
snapshot scheduling and restore one daily snapshot into a separate volume before declaring snapshot recovery tested.
Record backup, download/replay, readiness and convergence timings in `benchmark/bench/`, distinguishing each measurement
from the total recovery time. Publish production proof after the local gate and drill PRs merge; the live drill remains blocked until COS authorizes it.
