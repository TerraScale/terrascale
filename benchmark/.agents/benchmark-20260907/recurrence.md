# Confirmed recurring errors

| Pattern | Evidence / cause | Correction | Prevention |
| --- | --- | --- | --- |
| Final-family invocation loses required environment names | `run-terrabase-final.sh:40-41,64-76` sets member-prefixed values; `auto_index_local.rs:29-33` and `ingest.rs:35,353-372` require different names. Existing family runs were unavailable/failed in `bench/results/run-20260907/verification.md`. | Add explicit, scoped exports in the final orchestrator; retain fail-closed driver validation. | Add a preflight that checks every required variable for the selected family before creating/starting its lane; record the names in the execution brief. |

This is the only recurring pattern confirmed from current source and retained
evidence. Performance hypotheses (SQLite fsync cost, API latency, or index
maintenance) remain unconfirmed until the fresh run manifest identifies a
measured failure or regression.
