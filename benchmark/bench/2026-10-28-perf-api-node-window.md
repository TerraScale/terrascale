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
