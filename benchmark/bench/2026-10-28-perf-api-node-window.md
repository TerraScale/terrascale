# Node measurement-window control, 2026-10-28

Synthetic scheduler validation, not TerraBase or SDK latency. On delta through
`ts-heavy-any nice -n 19`, Node 24.21.0, the committed `window-control.mjs` ran
three operations delayed 2.5 s each: one starts in warmup, two in the 5 s window.
The last completes after the window; both measured latencies were retained.

| Check | Observed | Result |
| --- | --- | --- |
| Started / measured / failed / stuck | 3 / 2 / 0 / false | passed |
| Retained latency ms | 2513.053373, 2512.457301 | final tail retained |
| Throughput denominator seconds | 5.538819597 | drain included |
| Symlinked CLI without credentials | usage, exit 2 | passed |
| Peer regions / malformed row | deduplicated / rejected | passed |

Artifacts: delta managed `fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e/benchmark/bench/node-parity-control-f/`.
Runner SHA-256 `a9abd65dda57d2990c00bf727ded2f30a22101407524f98bf9f718fc0e2df00f`;
control SHA-256 `bc4458cba32b67981713018a6f78f365061c7182238219d24d3f6be4eb650537`.
No production tenant or credential was used. This does not qualify setup or erasure.

Follow-up on beta via the heavy wrapper at `f1197ab58`: Node 24.22.1, npm
11.19.1, same pinned SDK and unchanged lock. All six trusted-TLS transport cases
passed again. The window retained both measured 2.5 s operations with a
5.539833729 s denominator. The new cleanup control rejected a 404 after an
uncertain create, accepted absence after a confirmed create, and marked physical
erasure unverified in both cases. Pool release on cleanup-output failure is a
source fix; that failure path was not exercised end to end.
Artifacts: beta managed `fix-api-node-parity-df22682154d29bffe27a1ec49e6517294c450a61736d16d927caac4b5592f964`,
`benchmark/bench/node-lifecycle-qualification-a/`; source diff and error logs empty.
Runner SHA-256 `970d2fe05e2bfeeea66aa558ecf0d1c08017f0bbad1ac8d5240670a86002e6ca`;
window control `c176b1db4cbb68333bafefe847633ae5aaa07cf8afc056ee098a9be41d380fa1`.
