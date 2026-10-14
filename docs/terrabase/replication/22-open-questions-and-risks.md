# 22. Open questions and risks

1. **CDC cost on Fly.** The only valid measurement is −37 % in WAL. If R3 measures a lane below about 500 operations/s
   on `shared-cpu-2x`, the fallbacks are:
   - capture only `_tb_state`, by writing collection rows with capture off, if per-statement toggling works;
   - batch more operations per lane transaction.
2. **`BEGIN CONCURRENT`** (MVCC + CDC on Turso main). Concurrent commits break "commit order = stamp order" per
   origin, which the feed cursor and GC rely on. Before adopting it, either mint stamps in a pre-commit hook, or move
   cursors and horizons to per-origin commit sequences. Revisit when Turso calls MVCC production-ready.
3. **`after` mode and pragma toggling** are untested on 0.8.2. R3 verifies both and falls back as described in
   section 7.
4. **3a's WAN-over-gRPC** (`refactor/fly-wan-grpc`) is interim: R4b deletes it. If it is more than about a day from
   merging, landing only #11 plus the 8081 internal listener is enough for R4a.
5. **Nested fields** are LWW per top-level key in v1 (section 4).
6. **Spike report fixes** (PR #12: the invalid CDC and encryption figures, the BackupDatabase GO that contradicts the
   recorded FAIL) should land before R3 relies on its numbers.
