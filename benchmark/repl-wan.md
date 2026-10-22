# Isolated peer WAN measurement

Build outside the namespace, under the existing heavy-job slot:

```sh
TS_HEAVY_HOST=beta nice -n 19 make -f benchmark/repl.mk bench-repl-build
exec 3</proc/self/ns/net
unshare --user --map-root-user --net bash -c '
  ip link set lo up
  TS_HEAVY_HOST=beta nice -n 19 make -f benchmark/repl.mk bench-repl-run \
    PROFILE=wan SCENARIO=writes N=100000 REPEATS=3 OUT=benchmark/bench/wan
'
```

FD 3 retains the original network namespace. The harness refuses shaping unless
that inherited descriptor names a network namespace different from its current
one. Missing, non-network and equal descriptors fail before any qdisc mutation.
The caller must open it **before** `unshare`; no host qdisc is changed.

After each fresh cluster is ready, `tc` classifies IPv4 TCP source and destination
ports matching its three peer proxy listeners. Each proxy has a separate netem
queue: fixed 100 ms delay per direction, 100 Mbit/s aggregate serialized traffic
through that proxy's queue, no injected loss, maximum 1,024 queued packets.
Unmatched traffic uses the unshaped priority band, including client HTTP,
administrative metrics and proxy-to-backend sockets. Ports are rebuilt per cluster;
the root qdisc is removed before that cluster closes and before the next repeat.
Setup refuses an existing root qdisc rather than replacing it. Namespace teardown
also discards its qdiscs if the harness is killed.

The three regions share the host's CPU, memory and storage. Opposite directions
share each proxy's rate limit; this is not a full-duplex per-link topology. Kernel
queue overflow and transport loss remain possible even though netem injects none.
Loopback offloads remain at kernel defaults. These limits must accompany results.

The bounded TCP self-check starts its own isolated namespace and uses the real
wire proxy: three echo RTTs must exceed 180 ms, a separate unclassified TCP control
must stay below 100 ms, and removing the qdisc must restore low proxy RTT:

```sh
cd benchmark
nice -n 19 go test ./cmd/repl-local -run '^TestWANPeerPortIsolation$' -count=1 -v
```

Run this check inside a heavy-job slot. It builds before entering the child
namespace; it does not launch a TerraBase cluster or a throughput benchmark.
