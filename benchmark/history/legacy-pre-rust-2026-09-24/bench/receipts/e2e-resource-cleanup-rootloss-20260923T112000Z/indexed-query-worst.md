# Indexed query worst samples

2 deterministic indexed query samples; rows, bytes, work, and physical counters are the runtime receipt for the successful query attempt. RSS is `Process.WorkingSet64`; GC columns are collection deltas during that attempt.

| Query | Low | High | Requested width | Effective width | Elapsed ms | Rows examined | Bytes read | Work units | Entry reads | Full document reads | GC Δ (0/1/2) | RSS Δ B |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 13 | 17 | 191 | 5 | 80.245 | 10 | 1,510 | 11 | 5 | 5 | 0/0/0 | 0 |
| 1 | 11 | 17 | 911 | 7 | 2.601 | 14 | 817 | 15 | 7 | 7 | 0/0/0 | 0 |
