# Node runner controls, 2026-10-28

Synthetic qualification on beta via `ts-heavy-any nice -n 19` at `f1197ab58`:
Node 24.22.1, npm 11.19.1, SDK `b1bfe76254d8cc3e8e0aaa1a87dd599e7af4efe0`, unchanged lock.
All six trusted-TLS transport cases passed again; source diff and error logs empty.

| Check | Observed | Result |
| --- | --- | --- |
| Slow starts / measured / failures / stuck | 3 / 2 / 0 / false | passed |
| Retained latency ms / denominator seconds | 2513.174078, 2513.143024 / 5.539833729 | final tail retained |
| Symlinked CLI / peers | usage exit 2 / deduplicated, malformed rejected | passed |
| Cleanup 404, uncertain / confirmed create | reconcile required / absence accepted | passed; physical erasure unverified |

Artifacts: beta managed `fix-api-node-parity-df22682154d29bffe27a1ec49e6517294c450a61736d16d927caac4b5592f964`, `benchmark/bench/node-lifecycle-qualification-a/`.
Runner SHA-256 `970d2fe05e2bfeeea66aa558ecf0d1c08017f0bbad1ac8d5240670a86002e6ca`;
window control `c176b1db4cbb68333bafefe847633ae5aaa07cf8afc056ee098a9be41d380fa1`.
Earlier delta window qualification is retained in commit `ed8d277c7` and its recorded artifacts.
Pool release on cleanup-output failure is source-reviewed, not exercised end to end.
No production tenant, credential, setup/erasure qualification, latency or speedup claim.
