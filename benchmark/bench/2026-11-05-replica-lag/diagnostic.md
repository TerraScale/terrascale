# Live replica lag diagnostic — 2026-11-05

This is a failed diagnostic, not the six-cell acceptance proof. SJC served every measured read locally on deployed `cf2550738309a1bcddd1e4edfd61d87419f6cd25`. The strict benchmark was built from `6af360996` (rebased as `95561688e`). One sequential 500-write run, gru home → SJC replica, received all 500 pushed page notices. Raw [samples](cf255-sjc.jsonl.gz) and [poll/notice trace](cf255-sjc-trace.jsonl.gz) accompany this report.

| Stage (ms) | p50 | p99 | maximum | writes ≥200 ms |
| --- | ---: | ---: | ---: | ---: |
| Commit → local visibility | 110.8 | 306.0 | 429.3 | 9 |
| Commit → notice received | 93.4 | 128.4 | 282.3 | 4 |
| Notice received → local visibility | 17.0 | 69.3 | 306.6 | 5 |
| Home acknowledgment → local visibility | 22.2 | 206.0 | 312.2 | 8 |

The earlier persistent home-forwarding transition near write 301 did not recur in this run. This does not establish its cause or validate the checkpoint gate hypothesis. The checkpoint change excludes readers during WAL generation changes, but still needs repeated live proof.

The poll trace shows stale local reads for 266/312/312 ms at writes 130/262/395; starts are about 30.3 seconds apart. Those are followed by late notices on writes 131/263/396. Writes 189 and 385 instead have a single local read taking 276 and 258 ms. The follower awaits checksum verification inline, and a moved verification can perform additional serialized home pulls. Catalog recovery pulls also serialize behind the same follower. These paths can hold up pushed answers despite their arrival; a pending maintenance change separates read-only verification requests from notice consumption and avoids recovery pulls while applied revisions are arriving.

The x64, 85 ms one-way simulations did not reproduce persistent forwarding. The before run (beta) had ack→visible p99 257.5 ms; after checkpoint gating (mariogk-dev) had 353.2 ms. Different hosts and loads prevent a controlled performance comparison. Both completed bootstrap, DML, restart/catch-up and 500-write lag scenarios; neither passes the live target.

The synthetic account `lag-checkpoint-1105` was deleted and remote credential files removed after this diagnostic. No DONE event has been sent. Acceptance still requires gru→sjc and gru→fra, 500 writes ×3 each, with all reads local and each commit→visible p99 <200 ms.
