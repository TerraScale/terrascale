# 21. Slice plan: Contract for R3

**Contract for R3** (`Features/Replication/ChangeGroups.cs`). R4a registers `NoChangeGroupSource`. R3 replaces that
registration with its tailer and implements `IChangeGroupSource`:

| Member | R3 implements |
| --- | --- |
| `ListDatabasesAsync` | every open client database that has, or may have, unshipped local groups |
| `ReadAsync(database, after, maxGroups, maxBytes)` | groups with `change_id > after` in order, rebuilt from `turso_cdc`: only `_tb_state` rows with `origin = self`, one `OriginGroup` per Turso transaction (`change_id` = its last CDC row), one `ChangeDelta(collection, id, S, DeltaAt(S) with plain values from the paired doc image)` per state row; at most `maxGroups`, and at most `maxBytes` of encoded deltas unless the first group alone is larger |
| `AckedAsync(database, peer)` | `_tb_peers.acked` of the peer's node, or 0 |
| `AckAsync(database, peer, changeId)` | raise `_tb_peers.acked` (it drives CDC truncation) |
| `ChangedAsync` | completes at the lane's next commit signal |

The peer is the receiver's `ReplicaId`. A peer whose epoch rose is a new node and starts from 0, which only re-sends
retained groups. Those joins are idempotent. The fake in `tests/TerraBase.TestHarness/FakeChangeGroupSource.cs` is the
reference behaviour, and `tests/TerraBase.Fast/Replication/PeerStreamTests.cs` passes against it.

R2a was built on the Microsoft.Data.Sqlite engine and writes only SQL that Turso accepts (rowid tables for every new
table, plain `INSERT`/`UPDATE`/`DELETE` on captured tables).
