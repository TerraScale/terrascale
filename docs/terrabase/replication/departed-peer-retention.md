# Departed peer identities stop holding the change log

## Incident (gru, 2026-10-26/27)

From about 19:20Z on 2026-10-26 one `_tb_peers` row of gru's platform database stopped acknowledging. Its identity was neither
sjc's nor fra's current replica. Both were healthy, with sub-second ack lag. A peer counted as gone only after this process
had been up 24 h (`LocalReplica.GoneBefore`). TerraBase restarts on every deploy, so the row kept holding every later CDC row:

| gru, `terrabase_replication_*` | 2026-10-26 19:30Z | 2026-10-27 01:00Z | 2026-10-27 14:10Z |
|---|---|---|---|
| `tail_lag_seconds` | 629 | 20,429 | 68,237 |
| `cdc_backlog` (rows) | 21,309 | 296,990 | 714,598 |
| mean writer hold, `StorageChangeGroupSink.cs:124` | 0.55 s (20:40Z) | 1.09 s (23:00Z) | 1.66 s |

At the same point sjc held 9,987 rows and fra 10,108, with mean holds of 0.22 s and 0.16 s. Writes queued behind the slow
holds for 5 s and more. The writes behind gru's canaries and ts-auth's `remote_durable` readiness probe and token writes
waited past their deadlines, so `/readyz` flapped and sign-ins failed.

## Rule

- `PeerPresence.Current()` lists every listed machine's current identity, but only once this process has heard each of them.
- When a live peer acks with that list (`TursoStorageTransaction.RecordAckedAsync`), every other row with `seen > 0` is
  retired the way an older epoch already is: `acked`, `stable` and `seen` drop to 0 and its received `applied` prefix stays.
  The cut then follows the live peers. A live identity whose row was retired by mistake revives it with its next ack and
  refills what it missed (`10-anti-entropy-and-refill.md`).
- Once every listed machine has been heard, `GoneBefore` no longer waits for 24 h of uptime. A process that hears all its
  peers is not "back after a long absence", so a restart no longer restarts the 24 h for peers that went silent.
- Before every listed machine has been heard, or while nothing is listed, both rules keep their old behavior.
