# Confirmed recurring errors

| Pattern | Evidence / cause | Correction | Prevention |
| --- | --- | --- | --- |
| Final-family invocation loses required environment names | `run-terrabase-final.sh:40-41,64-76` sets member-prefixed values; `auto_index_local.rs:29-33` and `ingest.rs:35,353-372` require different names. Existing family runs were unavailable/failed in `bench/results/run-20260907/verification.md`. | Add explicit, scoped exports in the final orchestrator; retain fail-closed driver validation. | Add a preflight that checks every required variable for the selected family before creating/starting its lane; record the names in the execution brief. |

The environment-name mismatch is confirmed from current source and retained
evidence. Performance hypotheses (SQLite fsync cost, API latency, or index
maintenance) remain unconfirmed until the fresh run manifest identifies a
measured failure or regression.

| Frozen snapshot drift | During qualification, copying live implementation files into the frozen snapshot without the matching trait closure produced compiler E0407; the copied three-file set later aligned after correction (bench_owner report). | Keep one snapshot source owner and transfer the complete dependency/trait closure as a manifest; rebuild only from that coherent snapshot. | Capture the full source manifest before and after compilation and reject ad hoc live-file copies. |
