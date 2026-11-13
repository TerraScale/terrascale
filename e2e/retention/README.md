# Retained database proof

Run from the repository root with the normal journey tools, nats-server, a C compiler
and the pinned .NET SDK:

    E2E_REVISION=$(git rev-parse HEAD) nice -n 19 bash e2e/retention/run.sh

The runner builds the current TerraBase, auth and dashboard, a receiver-only snapshot
of origin/cos/922cbdaa-C54-delete-undo-receivers, and a disposable certification host.
All services use the isolated journey namespace, real project tokens, generated peer
credentials and the journey object-store stand-in.

The certification host includes the unchanged production certification method. Its
entry point writes 10,000 distinct valid retained catalog records with a shared valid
position and deliberately absent files, then measures three complete certifications.
This measures detection of a cold candidate, including catalog paging and ordered
key/position hashing; it does not claim 10,000 physical copies were imported.

The mixed-version step checks cross-region forgetting and restore with the receiver
build. The same-region step creates 300 retained databases while the candidate is
stopped, checks immediate planned-handoff refusal, waits for background reconciliation,
measures three warm certificates and performs the planned handoff. The existing
30-second drain and 45-second proof assertions remain.

The remaining proof covers missing-copy preservation, restoration, writer-only expiry,
two-clock confirmation, durable verified purge and an unplanned ownership change
through the real NATS ownership store. In that last step the writer is stopped and
one candidate copy is displaced. Live traffic must continue while restore and purge
are unavailable; returning the partner must supply the copy and restore the row.

The clock preload changes realtime only. Each proof node and the object-store
stand-in has a separate clock file. Monotonic clocks and lease deadlines are unchanged.
Fourteen-day certificates retain TLS checks.

The required multi-region fault journey also pauses the home consumer, commits a
remote write, retains the home database, resumes delivery before restoration and
requires the original row and remote row after restore. It is annotated as a release
dependency and remains an ordinary required assertion. The acknowledging consumer on
main is expected to lose the remote row. Release requires this step to pass with the
frontier consumer change; an annotation cannot qualify a release.

Release order: handoff-import compatibility already on main, then receiver-only
preparation on every node, then this retention-producing change. No automatic merge
or deployment is enabled by this runner.
