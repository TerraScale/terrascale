# Retained database handoff proof

Build the current TerraBase, auth and dashboard with the journey build scripts into
/tmp/922cbdaa-C7-retention/build. Build the receivers preparation branch separately
and set E2E_RETENTION_RECEIVER_BUILD to its TerraBase output directory. The runner
requires the normal journey tools, nats-server, a C compiler and the journey's browser.

Run from the repository root:

    nice -n 19 bash e2e/retention/run.sh

All state defaults to /tmp/922cbdaa-C7-retention/state. The namespace has loopback
networking only. Project requests use real project tokens. Internal requests use the
run's generated peer credential.

The mixed-version step runs the preparation receiver against the candidate home.
It checks that forgetting a retained deletion does not retire the id and restoration
re-adopts the original row.

The same-region run keeps both real nodes alive through deletion, handoff, restore
and purge. It transfers 300 retained databases in pages, checks restored rows after
the outgoing node's clock advances eight days, and verifies destruction on surviving
holders. SIGSTOP temporarily makes the outgoing node unreachable without removing
its process, files, keys or peer inventory; SIGCONT returns it to the same purge job.
The job must stay incomplete until files, keys and backup objects are verified absent.

The clock preload changes realtime only. Each proof node and the object-store
stand-in has a separate clock file. A fast writer with a correct partner must retain
the data; both clocks past the deadline permit scheduled expiry. Monotonic clocks,
leases and timed waits are unchanged. Fourteen-day certificates retain TLS checks.

The runner prints result lines and stops all proof processes and the namespace.
Release handoff-import compatibility everywhere, then preparation receivers
everywhere, then retention-producing deletion everywhere. The retained protocol has
a separate one-million-database bound; the existing 30-second drain budget remains.
The run measures 300 databases. Ten thousand databases have not been timed.
