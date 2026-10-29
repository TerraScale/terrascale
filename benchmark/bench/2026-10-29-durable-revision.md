# Durable sync revision prototype (W0-2), 2026-10-29

**Verdict: PASS.** 3/3 repeats passed every gate with the official `@tursodatabase/sync` 0.8.2 client. The unchanged upstream sync server reproduced the stale checkpoint pull in 3/3 control repeats.

Source: [`benchmark/turso-sync-host/`](../turso-sync-host/README.md). Regenerate this page with `qualify/report.mjs` from the JSON written by `qualify/run.sh`.

## Pins and environment

| Item | Value |
| --- | --- |
| TerraScale commit | `7b8a055692a2aaae6383edeadad71c0e542822d9` |
| Turso source | [`5c168be5ef6a32ac966daf5983502a4f694b37ad`](https://github.com/tursodatabase/turso/tree/5c168be5ef6a32ac966daf5983502a4f694b37ad) (0.8.2, tarball SHA-256 `7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb`) |
| Rust toolchain | 1.88.0 |
| `ts-sync-revision-host` SHA-256 | `4456dc9adc16e51d99a158e48b21d601cc9cc1490d59c575bdddb465a2fe1ccc` |
| `tursodb` (control) SHA-256 | `b43dd89da77cc522fb5745e0d8d6eeccf03de13ba002c01c1ef5fbad32f055f9` |
| Official client | `@tursodatabase/sync` 0.8.2, Node v24.22.2 |
| Machine | mariogk-dev (Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz, 46 CPUs), Linux 7.3.0 |
| Network | loopback; client, host and control on one machine |

## Gates

Every counter must be 0 in every repeat. The oracle compares full rows (`id`, `origin`, `v`), not just keys, through inserts, updates and deletes on both sides. "Acked" means a server write whose pipeline request returned success, or a client change whose `push()` returned. "Local" means a change the client committed offline and has not pushed yet. Keys are ULIDs; clients change only their own rows, so the oracle has no conflicts to resolve.

| Gate | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Lost acked writes (server missing an acked row) | 0 | 0 | 0 |
| Lost local writes (client lost a pending offline row) | 0 | 0 | 0 |
| Skipped commits (client missing an acked row after `pull()`) | 0 | 0 | 0 |
| Phantom rows (client keeps a row the server does not have) | 0 | 0 | 0 |
| `pull()` returned false while rows were missing | 0 | 0 | 0 |
| Unexpected server rows | 0 | 0 | 0 |
| Plan mismatches (wrong incremental/full/expired decision) | 0 | 0 | 0 |
| SQL sandbox failures (PRAGMA, VACUUM INTO, ATTACH, open transaction) | 0 | 0 | 0 |
| History bounded | yes | yes | yes |
| All clients equal to the server, `integrity_check` ok | yes | yes | yes |
| Repeat passed | yes | yes | yes |

## Lifecycle events with pending offline writes

Each row is the first `/pull-updates` response the official client received after the event, while it held offline writes. The host labels each pull response; the harness checks the label and then the rows.

| Event / client | Expected | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | --- | --- | --- | --- |
| coordinated-checkpoint / a | `incremental` | ok: `incremental` 20/41 pages | ok: `incremental` 22/41 pages | ok: `incremental` 21/41 pages |
| sql-sandbox / a | `incremental` | ok: `incremental` 7/42 pages | ok: `incremental` 8/42 pages | ok: `incremental` 12/43 pages |
| retained-history-window / a | `incremental` | ok: `incremental` 20/47 pages (x10) | ok: `incremental` 27/49 pages (x10) | ok: `incremental` 21/48 pages (x10) |
| retained-history-window / c | `incremental` | ok: `incremental` 30/51 pages (x2) | ok: `incremental` 37/53 pages (x2) | ok: `incremental` 36/53 pages (x2) |
| retained-history-window / b | `incremental` | ok: `incremental` 63/63 pages | ok: `incremental` 67/67 pages | ok: `incremental` 66/66 pages |
| history-horizon-expires / a | `incremental` | ok: `incremental` 24/67 pages (x11) | ok: `incremental` 23/71 pages (x11) | ok: `incremental` 27/70 pages (x11) |
| history-horizon-expires / c | `incremental` | ok: `incremental` 24/67 pages (x10) | ok: `incremental` 23/71 pages (x10) | ok: `incremental` 27/70 pages (x10) |
| history-horizon-expires / e | `full;expired` | ok: `full;expired` 102/102 pages | ok: `full;expired` 102/102 pages | ok: `full;expired` 102/102 pages |
| automatic-checkpoint-policy / a | `incremental` | ok: `incremental` 2769/2850 pages | ok: `incremental` 2765/2850 pages | ok: `incremental` 2772/2850 pages |
| graceful-restart / a | `incremental` | ok: `incremental` 31/2855 pages | ok: `incremental` 31/2857 pages | ok: `incremental` 30/2856 pages |
| graceful-restart / c | `incremental` | ok: `incremental` 2788/2855 pages | ok: `incremental` 2785/2857 pages | ok: `incremental` 2790/2856 pages |
| crash-restart / a | `incremental` | ok: `incremental` 25/2859 pages | ok: `incremental` 25/2861 pages | ok: `incremental` 20/2860 pages |
| external-checkpoint-while-down / a | `full;rewritten` | ok: `full;rewritten` 2862/2862 pages | ok: `full;rewritten` 2865/2865 pages | ok: `full;rewritten` 2863/2863 pages |
| external-change-after-clean-stop / a | `full;rewritten` | ok: `full;rewritten` 2862/2862 pages | ok: `full;rewritten` 2865/2865 pages | ok: `full;rewritten` 2863/2863 pages |
| rewrite-vacuum-swap / a | `full;rewritten` | ok: `full;rewritten` 104/104 pages | ok: `full;rewritten` 102/102 pages | ok: `full;rewritten` 104/104 pages |
| rewrite-vacuum-swap / c | `full;rewritten` | ok: `full;rewritten` 104/104 pages | ok: `full;rewritten` 102/102 pages | ok: `full;rewritten` 105/105 pages |
| restore-backup-new-incarnation / a | `full;other-incarnation` | ok: `full;other-incarnation` 105/105 pages | ok: `full;other-incarnation` 104/104 pages | ok: `full;other-incarnation` 105/105 pages |
| restore-backup-new-incarnation / c | `full;other-incarnation` | ok: `full;other-incarnation` 107/107 pages | ok: `full;other-incarnation` 105/105 pages | ok: `full;other-incarnation` 105/105 pages |
| restore-rolls-back-unbacked-writes / a | `incremental` | ok: `incremental` 22/109 pages | ok: `incremental` 24/110 pages | ok: `incremental` 22/110 pages |
| restore-rolls-back-unbacked-writes / a | `full;other-incarnation` | ok: `full;other-incarnation` 105/105 pages | ok: `full;other-incarnation` 104/104 pages | ok: `full;other-incarnation` 104/104 pages |

Explicit expiry: a chunked bootstrap (`pullBytesThreshold`) pins its first revision. The harness checkpoints between chunks; the next pinned chunk must fail loudly, and a retry on the same path must bootstrap.

| Check | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Pinned chunk rejected with HTTP 409 `revision_expired` | yes | yes | yes |
| Retry on the same path converged | yes | yes | yes |
| Chunked bootstrap requests (first + pinned) | 11 | 11 | 11 |

## Bounded history

| Measure | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Peak WAL bytes | 4482592 | 4478472 | 4486712 |
| Largest single pipeline item, frames | 80 | 80 | 80 |
| WAL bound: 32 + (1024 + largest item) frames x 4120 B | 4548512 | 4548512 | 4548512 |
| Peak exact generations retained (limit 8) | 8 | 8 | 8 |
| Peak compacted buckets (limit 8 x 8 generations) | 8 | 8 | 8 |
| Peak manifest bytes | 44200 | 44120 | 44141 |
| Automatic checkpoints during the 8,000-row burst | 24 | 24 | 24 |
| Coordinated checkpoints counted before each stop (shutdown checkpoints not included) | 128 | 128 | 128 |
| Checkpoints detected outside the coordinator | 3 | 3 | 3 |
| Rewrite: database pages before / after | 2863 / 102 | 2866 / 100 | 2864 / 101 |
| Acked row changes rolled back by restoring an older backup | 27 | 27 | 27 |

The host checks the WAL policy after every pipeline item (one execute, or one batch), so the WAL can exceed 1,024 frames by at most the largest item. The rolled-back changes (inserts, updates and deletes) were acked after backup `b2` was taken. Restoring `b2` reverts them by definition (recovery point); the gate is that every client converges to the restored rows (0 skipped, 0 phantom). They are not counted as lost acked writes.

## Pull latency (same host, includes client apply)

| Pull kind | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Incremental, p50 / max ms | 16.8 / 193.7 (n=42) | 17.8 / 161.8 (n=42) | 16.6 / 166.3 (n=42) |
| Full resend, p50 / max ms | 27.4 / 207.9 (n=12) | 22.5 / 199.1 (n=12) | 23.2 / 186.6 (n=12) |
| Empty, p50 / max ms | 4.5 / 22.2 (n=9) | 4.0 / 14.3 (n=9) | 3.5 / 4.0 (n=9) |

## Control: unchanged upstream sync server

`tursodb --sync-server` from the same pin, same official client. 100 rows, `PRAGMA wal_checkpoint(TRUNCATE)`, one more insert, then `pull()`.

| Measure | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Client rows before | 100 | 100 | 100 |
| `pull()` returned | false | false | false |
| Client rows after | 100 | 100 | 100 |
| Server rows | 101 | 101 | 101 |
| Stale pull reproduced | yes | yes | yes |

## Limits

- One machine over loopback. No network latency, packet loss or cross-region links.
- Sequential workload: one request at a time, so there are no concurrent writer races. Crash tests use SIGKILL of the host process, not power loss.
- Only the official JS client (page protocol, V1 revisions) was qualified. Go, Python and Rust clients, partial sync and MVCC were not tested.
- Client pushes use upstream's Hrana pipeline only to deliver the pending offline writes. Production never uses Turso push (D14); R6/R7 replace it.
- The prototype buffers every response in memory, keeps every database open, and binds 127.0.0.1 with fixture tokens. R1/R2 must add streaming, LRU, TLS and production auth.
- The prototype sandbox denies PRAGMA, VACUUM, ATTACH and DETACH, and rolls back a transaction left open at the end of a request. It is not the full D15 sandbox (file functions, schema hiding, resource limits).
- The host must own the database files exclusively while it runs. A change made by another process while the host is down is detected through the WAL salt and the database file identity (size, inode, mtime), then served as a full resend.
- Durability was tested with SIGKILL only. Power loss was not simulated, although the manifest and directories are fsynced.
