# Guard lookup contention diagnostic

The server library's noisy-neighbour test measures 400 victim requests alone
and 400 while another thread floods an attacker database. Each probe checks
the read guard and runs a fixed 512-iteration arithmetic loop. These are debug
build measurements on a shared development host, not production qualification.

| Implementation | Run scope | Solo p99 | Victim p99 with neighbour | Ratio | 2x gate |
| --- | --- | ---: | ---: | ---: | --- |
| Exclusive map locks and allocated state lookup key | Full server library suite | 10.205 µs | 36.000 µs | 3.53 | Fail |
| Shared map reads and borrowed existing-state lookup | Focused diagnostic during server suite | 12.599 µs | 13.064 µs | 1.04 | Pass |

The change uses read locks for plan, limit, and existing state lookups. State
creation takes a write lock and rechecks the entry. Each database still has
its own counter mutex. The samples have different surrounding workloads and
do not establish a controlled speedup or a production latency guarantee.

Reproduce the diagnostic with:

```sh
nice -n 19 cargo test --locked -p terrabase-server --lib guards::tests::adversarial_neighbour_keeps_victim_p99_within_2x_solo -- --exact --nocapture
```
