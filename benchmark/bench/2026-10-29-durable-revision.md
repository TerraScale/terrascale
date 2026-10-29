# Durable sync revision prototype (W0-2), 2026-10-29

**Verdict: PASS.** 3/3 repeats passed every gate with the official `@tursodatabase/sync` 0.8.2 client. The unchanged upstream sync server reproduced the stale checkpoint pull in 3/3 control repeats.

Source: [`benchmark/turso-sync-host/`](../turso-sync-host/README.md). Regenerate this page with `qualify/report.mjs` from the JSON written by `qualify/run.sh`.

## Pins and environment

| Item | Value |
| --- | --- |
| TerraScale commit | `6ee50149ca6f644973f16e5df84e1227a79eadfb` |
| Turso source | [`5c168be5ef6a32ac966daf5983502a4f694b37ad`](https://github.com/tursodatabase/turso/tree/5c168be5ef6a32ac966daf5983502a4f694b37ad) (0.8.2, tarball SHA-256 `7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb`) |
| Rust toolchain | 1.88.0 |
| `ts-sync-revision-host` SHA-256 | `31cd57aef42b99ce05b349907b9b2dec453acfb190bbaca910016fe64f521fa9` |
| `tursodb` (control) SHA-256 | `fd7f6d11f0d2a01d2ff0311664b8ded10e8a45e780e567a0d9d0c1934f9fdf1e` |
| Official client | `@tursodatabase/sync` 0.8.2, Node v24.22.2 |
| Machine | mariogk-dev (Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz, 46 CPUs), Linux 7.3.0 |
| Network | loopback; client, host and control on one machine |

## Gates

Every counter must be 0 in every repeat. "Acked" means a server write whose pipeline request returned success, or a client row whose `push()` returned. "Local" means a row the client committed offline and has not pushed yet.

| Gate | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Lost acked writes (server missing an acked row) | 0 | 0 | 0 |
| Lost local writes (client lost a pending offline row) | 0 | 0 | 0 |
| Skipped commits (client missing an acked row after `pull()`) | 0 | 0 | 0 |
| Phantom rows (client keeps a row the server does not have) | 0 | 0 | 0 |
| `pull()` returned false while rows were missing | 0 | 0 | 0 |
| Unexpected server rows | 0 | 0 | 0 |
| Plan mismatches (wrong incremental/full/expired decision) | 0 | 0 | 0 |
| History bounded | yes | yes | yes |
| All clients equal to the server, `integrity_check` ok | yes | yes | yes |
| Repeat passed | yes | yes | yes |

## Lifecycle events with pending offline writes

Each row is the first `/pull-updates` response the official client received after the event, while it held offline writes. The host labels each pull response; the harness checks the label and then the rows.

| Event / client | Expected | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | --- | --- | --- | --- |
| coordinated-checkpoint / a | `incremental` | ok: `incremental` 10/38 pages | ok: `incremental` 10/38 pages | ok: `incremental` 10/38 pages |
| raw-pragma-checkpoint / a | `full;rewritten` | ok: `full;rewritten` 41/41 pages | ok: `full;rewritten` 41/41 pages | ok: `full;rewritten` 41/41 pages |
| raw-pragma-checkpoint-inside-one-request / a | `full;rewritten` | ok: `full;rewritten` 42/42 pages | ok: `full;rewritten` 42/42 pages | ok: `full;rewritten` 42/42 pages |
| retained-history-window / a | `incremental` | ok: `incremental` 9/46 pages (x10) | ok: `incremental` 9/46 pages (x10) | ok: `incremental` 9/46 pages (x10) |
| retained-history-window / c | `incremental` | ok: `incremental` 11/49 pages (x2) | ok: `incremental` 11/49 pages (x2) | ok: `incremental` 11/49 pages (x2) |
| retained-history-window / b | `full;` | ok: `full;expired` 60/60 pages | ok: `full;expired` 60/60 pages | ok: `full;expired` 60/60 pages |
| history-horizon-expires / a | `incremental` | ok: `incremental` 11/63 pages (x11) | ok: `incremental` 11/63 pages (x11) | ok: `incremental` 11/63 pages (x11) |
| history-horizon-expires / c | `incremental` | ok: `incremental` 6/65 pages (x9) | ok: `incremental` 6/65 pages (x9) | ok: `incremental` 6/65 pages (x9) |
| history-horizon-expires / e | `full;expired` | ok: `full;expired` 85/85 pages | ok: `full;expired` 85/85 pages | ok: `full;expired` 85/85 pages |
| automatic-checkpoint-policy / a | `incremental` | ok: `incremental` 2721/2795 pages | ok: `incremental` 2721/2795 pages | ok: `incremental` 2721/2795 pages |
| graceful-restart / a | `incremental` | ok: `incremental` 13/2799 pages | ok: `incremental` 13/2799 pages | ok: `incremental` 13/2799 pages |
| graceful-restart / c | `incremental` | ok: `incremental` 2726/2800 pages | ok: `incremental` 2726/2800 pages | ok: `incremental` 2726/2800 pages |
| crash-restart / a | `incremental` | ok: `incremental` 10/2802 pages | ok: `incremental` 10/2802 pages | ok: `incremental` 10/2802 pages |
| external-checkpoint-while-down / a | `full;rewritten` | ok: `full;rewritten` 2805/2805 pages | ok: `full;rewritten` 2805/2805 pages | ok: `full;rewritten` 2805/2805 pages |
| rewrite-vacuum-swap / a | `full;rewritten` | ok: `full;rewritten` 95/95 pages | ok: `full;rewritten` 95/95 pages | ok: `full;rewritten` 95/95 pages |
| rewrite-vacuum-swap / c | `full;rewritten` | ok: `full;rewritten` 96/96 pages | ok: `full;rewritten` 96/96 pages | ok: `full;rewritten` 96/96 pages |
| restore-backup-new-incarnation / a | `full;other-incarnation` | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages |
| restore-backup-new-incarnation / c | `full;other-incarnation` | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages |
| restore-rolls-back-unbacked-writes / a | `incremental` | ok: `incremental` 8/100 pages | ok: `incremental` 8/100 pages | ok: `incremental` 8/100 pages |
| restore-rolls-back-unbacked-writes / a | `full;other-incarnation` | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages | ok: `full;other-incarnation` 97/97 pages |

Explicit expiry: a chunked bootstrap (`pullBytesThreshold`) pins its first revision. The harness checkpoints between chunks; the next pinned chunk must fail loudly, and a retry on the same path must bootstrap.

| Check | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Pinned chunk rejected with HTTP 409 `revision_expired` | yes | yes | yes |
| Retry on the same path converged | yes | yes | yes |
| Chunked bootstrap requests (first + pinned) | 11 | 11 | 11 |

## Bounded history

| Measure | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Peak WAL bytes | 4915192 | 4915192 | 4915192 |
| WAL bound (32 + (1024 + 256) frames x 4120 B) | 5273632 | 5273632 | 5273632 |
| Peak exact generations retained (limit 8) | 8 | 8 | 8 |
| Peak compacted buckets (limit 8 x 8 generations) | 8 | 8 | 8 |
| Peak manifest bytes | 44458 | 44458 | 44458 |
| Automatic checkpoints during the 8,000-row burst | 22 | 22 | 22 |
| Coordinated checkpoints, all host lifetimes | 124 | 124 | 124 |
| Checkpoints detected outside the coordinator | 3 | 3 | 3 |
| Rewrite: database pages before / after | 2805 / 95 | 2805 / 95 | 2805 / 95 |
| Acked rows rolled back by restoring an older backup | 20 | 20 | 20 |

The rolled-back rows were acked after backup `b2` was taken. Restoring `b2` removes them by definition (recovery point), and the gate is that every client drops them too (0 phantom rows). They are not counted as lost acked writes.

## Pull latency (same host, includes client apply)

| Pull kind | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | ---: | ---: | ---: |
| Incremental, p50 / max ms | 18.2 / 195.2 (n=40) | 21.0 / 155.4 (n=40) | 16.2 / 192.0 (n=40) |
| Full resend, p50 / max ms | 29.3 / 174.4 (n=14) | 24.8 / 189.2 (n=14) | 24.1 / 169.3 (n=14) |
| Empty, p50 / max ms | 4.8 / 6.5 (n=9) | 3.5 / 6.3 (n=9) | 3.6 / 5.3 (n=9) |

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
- Upstream SQL can run `PRAGMA wal_checkpoint` through the pipeline. Here that is detected and served as a full resend. Production denies it in the D15 sandbox.
