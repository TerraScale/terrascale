# Deleting orphan database files

The deletion layer extends the observation-only audit: after 24 hours of node-local observed absence, it checks registration, cleanup intents, purge ownership and first-seen identity inside a system write transaction held through unlink, under the engine lifecycle lock. The engine closes idle stores, defers while native handles remain, and uses its existing sidecar deletion path. Ledger cleanup follows release of the system writer. Unknown paths, links, system files, lifecycle operations and rebuild/salvage work remain protected.

PITR staging receives a durable cleanup intent before import and an engine lifecycle lease until restore finishes (a peer can clear the replicated intent during startup): a long restore can exceed the grace period while its staging file has no registration. A remaining cleanup intent enables startup recovery after a crash; otherwise the orphan grace period applies. No public API or storage format changes.
