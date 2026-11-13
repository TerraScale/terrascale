# Retained database regional handoff

Build TerraBase from the revision under test and prepare the local journey builds,
tools and CA with the normal e2e/env/local.sh prepare command. This proof also needs
nats-server and a C compiler installed.

With no journey namespace running, from the repository root:

    nice -n 19 bash e2e/retention/run.sh

The script starts the normal two-region stack and runs placement-setup. It stops
the initial TerraBase processes, preserves their verified local catalogs, then
starts both real nodes in gru with a shared JetStream writer lease. Project requests
use the client listeners and a real project-scoped token. Rollout and page-pull
requests use the internal listener and the journey's generated peer credential.

The runner regenerates the throwaway CA and edge certificates. Their fourteen-day
validity covers the clock jump while keeping certificate verification enabled.

The R2 stand-in and the proof nodes share the wall-clock preload built from
clock.c. It shifts only realtime reads and leaves monotonic clocks and timed waits
unchanged. The browser, ts-auth and dashboard keep their normal clock. Advancing the shared clock eight days
keeps object-store signatures valid without waiting for the seven-day deadline or
adding a clock hook to production code.

The proof checks retained SQL refusal, an authenticated peer from another region
being refused a retained pull, missing-file restore and explicit-purge 503 responses
with Retry-After and unchanged keys and backup objects, a real regional drain from a
source whose retained files are absent on the candidate, and restoration on the
incoming writer with the original rows.

After the client operations it retires the outgoing node: stops its process, removes
its local data and removes it from the peer inventory. This models a removed rollout
node, with no cached or disk keys left there. The incoming writer is the only holder.
The local auth service stays pinned to the outgoing listener, so client operations
finish before that retirement.

At the deadline it hides the peer's retained files and checks the missing-file
counter and warning name the database while preserving wrapped keys and backups.
It returns the peer's files and verifies maintenance removes its local files,
wrapped keys and backup objects. Key custody and backup data are queried in their separate buckets.

All proof nodes, JetStream and the journey namespace stop when the script exits.
The handoff-import compatibility change must be deployed before this feature.
