# SE-11 S3 slow-consumer backpressure — tracer receipt

Scenario `s3-slow-consumer` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `backend-operator` / X-TB-Tenant `tenant-a`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `cde691866e04882998a29e6ecd2c661802d48459`, TerraScale.DotNet @ `21d21aa9a5f3d9231f005a4b4eb5fb6bd986abe7`
- Reproduce: `python3 scripts/se11/run.py --scenario s3-slow-consumer` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).

Outcome: **PASS** (9/9 checks).


| Check | Outcome | Detail |
| --- | --- | --- |
| `the stalled consumer's bounded queue shed frames (counted, never blocked the engine)` | pass | droppedFrames 1, openStreams 3 |
| `the stalled consumer's missed range is surfaced in-band or re-delivered from the boundary` | pass | 0 in-band drop markers (resumeFrom 51); the boundary reconnect re-delivered 1 batch events covering the missed range |
| `ts observed the oversize single commit delivered alone (the .NET core also carries its commits == 1 flag)` | pass | 1 over-budget batches; bytes 20138; commits n/a (ts surface) |
| `ts every other batch fits the delivery byte budget` | pass | 96 batches checked against the 8192-byte budget |
| `net observed the oversize single commit delivered alone (the .NET core also carries its commits == 1 flag)` | pass | 1 over-budget batches; bytes 20138; commits 1 |
| `net every other batch fits the delivery byte budget` | pass | 151 batches checked against the 8192-byte budget |
| `the fast consumers lost nothing while the slow consumer shed frames` | pass | ts 401, net 401 (total 401 commits) |
| `no duplicate application at the fast consumers` | pass | 0 duplicate batches |
| `the content-free trigger hint channel stayed live (advisory hints; the checkpoint-bounded pull is authoritative)` | pass | 72 triggers observed by the .NET core for 401 commits; a parallel raw consumer recorded {'opened': 1, 'batch': 52, 'trigger': 51} (the pull is authoritative: both fast consumers reached every commit with zero duplicates) |
