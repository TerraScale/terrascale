# Preparatory actor transport (TS-648)

TS-648 waits for TS-647's persisted lattice state, delta protocol and owner operations. This slice supplies only the
transport behind `IActorRouter`. `Program` does not register it, and document requests still use today's engine.
It does not change storage, public APIs, replication formats, replica identities or the existing TS-648 markers.

`services.AddTerraBaseActors(actorCount, maxItemsPerActor, maxBytesPerActor)` opts a consumer into a fixed set of
logical actors. `GetActor(new ActorKey(database, collection, id))` hashes organization, project, database, collection
and document id with SHA-256. Each field is length-prefixed UTF-8; the first eight digest bytes interpreted as a
little-endian integer select the actor modulo the count. Plans do not affect placement. The complete encoded key
fits 4096 bytes; malformed Unicode and missing identity fields are refused. Actor-count changes require rebuilding
placement; there is no online resize in this slice.

`InvokeAsync(actor, retainedBytes, callback, cancellationToken)` admits work to that actor's bounded channel. One
consumer awaits callbacks serially; separate actors can execute independently. Workers do not inherit the resolving
request's execution context; callbacks carry their dependencies explicitly. Actors have independent atomic
admission counters, without a shared router lock. Queued and executing work count toward both limits. Admission is
fail-fast (`StorageBusyException`), so producers do not form an unbounded waiting queue. The caller accounts for
captured payload bytes and reserves payload memory before allocation; the item limit bounds fixed bookkeeping.

Cancellation is checked before execution and forwarded to the callback. A cancelled queued item stays admitted until
the consumer reaches it, then releases its capacity. Callback failures affect only that invocation. Disposal stops
admission and drains accepted work, including the executing callback, before returning. Callbacks must cooperate
with cancellation and must not await work on their own actor; neither preemption nor reentrant execution is provided.

The `TerraScale.TerraBase` meter records `terrabase.actors.completed` (successes, cancellations and failures) and
`terrabase.actors.rejected` (capacity refusals), with actor indices bounded to 0–255.

The Fast suite drives the real health endpoint through a test-only routing middleware, covering independent actors,
serialization, bounded admission, cancellation, failures and shutdown. The companion `ts-benchmark` actor harness
measures this transport with actor-count and uniform/Zipf workloads. These measurements do not prove database scaling.

After TS-647 lands, wire the router into key ownership and the shared lattice delta delivery path, add per-actor
state and flush completion, integrate cross-actor transactions, and rerun the database workload matrix. Thread
affinity, recovery, causal delivery and durable visibility remain unimplemented here.
