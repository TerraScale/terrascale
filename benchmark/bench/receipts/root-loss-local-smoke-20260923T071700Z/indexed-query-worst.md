# Indexed query worst samples

10 deterministic indexed query samples; rows, bytes, work, and physical counters are the runtime receipt for the successful query attempt. RSS is `Process.WorkingSet64`; GC columns are collection deltas during that attempt.

| Query | Low | High | Requested width | Effective width | Elapsed ms | Rows examined | Bytes read | Work units | Entry reads | Full document reads | GC Δ (0/1/2) | RSS Δ B |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 30 | 89 | 191 | 60 | 188.535 | 120 | 17,678 | 121 | 60 | 60 | 0/0/0 | 0 |
| 1 | 6 | 89 | 911 | 84 | 5.531 | 168 | 9,739 | 169 | 84 | 84 | 0/0/0 | 0 |
| 3 | 2 | 89 | 248 | 88 | 2.269 | 176 | 4,880 | 177 | 88 | 88 | 0/0/0 | 0 |
| 4 | 48 | 89 | 649 | 42 | 0.788 | 84 | 1,848 | 85 | 42 | 42 | 0/0/0 | 0 |
| 6 | 39 | 89 | 689 | 51 | 0.758 | 102 | 2,244 | 103 | 51 | 51 | 0/0/0 | 0 |
| 7 | 42 | 89 | 594 | 48 | 0.739 | 96 | 2,112 | 97 | 48 | 48 | 0/0/0 | 0 |
| 2 | 55 | 89 | 214 | 35 | 0.707 | 70 | 1,540 | 71 | 35 | 35 | 0/0/0 | 0 |
| 5 | 54 | 89 | 77 | 36 | 0.668 | 72 | 1,584 | 73 | 36 | 36 | 0/0/0 | 0 |
| 9 | 76 | 89 | 954 | 14 | 0.513 | 28 | 616 | 29 | 14 | 14 | 0/0/0 | 0 |
| 8 | 88 | 89 | 92 | 2 | 0.416 | 4 | 88 | 5 | 2 | 2 | 0/0/0 | 0 |
