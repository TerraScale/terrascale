# Prove recovery without a push run

Owner: CI/release lane. Run this controlled drill only as an assigned recovery
proof. Keep every required job and qualification check enabled.

1. Ship the gate implementation through `ts-ship` and inspect its CI result.
2. Commit a useful docs-only recovery change with `[skip ci]` to model a missed
   GitHub push event. Ship it through `ts-ship`, then immediately dispatch
   `ci.yml --ref main`. Also start Images on that same head to bind its run
   to the revision; it must refuse deployment until qualification is ready.
   Use `main`; do not dispatch a raw SHA, tag, or another branch.
3. Record the full dispatched head SHA. Confirm that `ci.yml` has no push run
   for that SHA and that its normal dispatch completes successfully, including
   `test`, `main` and `main-auth`.
   Native maintenance is not qualifying CI.
4. If the dashboard is part of the proof, fully qualify that exact docs head's
   dashboard on a dev host through `ts-heavy-any`; run all Fast, dependency,
   runtime and packaging checks. Keep its source-bound archive and trusted
   statuses. A docs-only shipping skip is not dashboard qualification.
5. Let CI completion heal the original Images dispatch, or rerun that run by
   ID once qualification completes. If no Images run was started and main
   advanced, qualify its current head instead; a new dispatch cannot sign an
   older SHA. Check the release logs for every service and region, not just
   the dispatch response.
6. Read `/readyz` publicly and directly from gru's dashboard ingress. The
   `X-TerraScale-Revision` must match the qualified dashboard revision and its
   ancestry must contain the intended application fix. Record both run URLs,
   the head and fix SHAs, the deployment revision and readiness result.

An Images run started from the real main head keeps its original signed SHA
after unrelated pushes. It can release only while it is the newest qualified,
green main revision newer than live. Never relabel an older archive. A failed,
skipped or cancelled required CI job never qualifies.

See [CI recovery](ci-recovery.md) for the normal operator commands and trust
boundaries. Suppressing push CI is a controlled proof step, not the normal
shipping procedure.

## Recorded recovery

On 2026-11-04, main revision
`3baf6b501902ea28d57c6731bc04a3201ab77ebd` was recovered through a normal
[CI dispatch](https://github.com/TerraScale/terrascale/actions/runs/41890569096)
and the [Images dispatch bound to that same head](https://github.com/TerraScale/terrascale/actions/runs/41890575553).
All required core jobs passed. Dashboard Fast, dependency and runtime audits,
packaging and the container smoke passed separately on a dev host and produced
the [exact-revision archive](https://github.com/TerraScale/terrascale/releases/tag/dashboard/qualified-3baf6b501902ea28d57c6731bc04a3201ab77ebd).
The original Images run was rerun after qualification, and the running image
labels in gru, sjc and fra and public readiness all reported that revision.

That recovery head had a successful push CI run too; it proves dispatch
deployment, not the absent-push part of the controlled drill above. Dispatching
CI alone did not create the dashboard archive. Keep these two proof phases
separate when recording a missed-event recovery.
