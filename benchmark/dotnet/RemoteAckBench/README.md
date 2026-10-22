# Remote ACK scheduling benchmark

Measures the linked production waiter and its real source admission and marker types, with Ulid as the only explicit package.
The refusal record is a benchmark-local copy of `Unavailable`; the HTTP error dependency graph is excluded. No transport, disk,
authentication or replication receiver runs here. The 100 ms profile delays ACK delivery once after registering
all waiters; it is simulated scheduling delay, not a WAN measurement.

1. Build separate baseline and candidate output trees on beta. Run sequentially, outside another build:

   ```sh
   nice -n 19 dotnet build benchmark/dotnet/RemoteAckBench -c Release --artifacts-path /home/mariogk/.cache/r4-ack-before -p:AckWaiterSource="$PWD/.worktrees/r4-before/src/TerraBase/Features/Replication/RemoteAckWaiter.cs"
   nice -n 19 dotnet build benchmark/dotnet/RemoteAckBench -c Release --artifacts-path /home/mariogk/.cache/r4-ack-after
   ```

2. Run both executable DLLs back-to-back on the same host. Each invocation emits five repeats for every profile.
   Locate their generated paths with `rg --files /home/mariogk/.cache/r4-ack-before /home/mariogk/.cache/r4-ack-after`.
   Prefix each execution with `nice -n 19 dotnet`, passing `baseline` or `candidate` as the sole argument.
   For five paired process repetitions, alternate the two invocations five times.

3. Save stdout in `benchmark/bench/`; retain all repetitions and summarize median and min–max separately for
   each workload/profile. Record host, app commit, linked source SHA-256, SDK, host load and exact commands.

CPU and allocation totals cover registration, ACK calls, continuations and lease disposal, divided by ACK calls.
They are whole-workload amortized costs, not isolated Acked-method costs. Latencies measure registration to
waiter continuation return; individual delivery intentionally makes later waiters wait behind earlier ACKs.
The maximum pending count is the known number registered before delivering any ACK. Noise adds unrelated-incarnation
ACKs and duplicates. Noisy workloads count those ACKs in the denominator: compare identical workloads only.

Before measurement the benchmark checks confirmation success, admission with 10,000 parallel attempts, the machine and database bounds,
and that cancellation plus lease disposal allows refilling the database. It does not inspect the private pending collection.
Use R0's required real-process harness separately for end-to-end results; this executable cannot establish network,
storage, durability or production tail-latency behavior. No build or measurement result is implied by these commands.
