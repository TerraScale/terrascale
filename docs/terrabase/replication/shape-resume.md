# Shape resume across machines (FR-23)

Implements [shape-reads.md](../shape-reads.md) section 9. A resume is `GET …/shape?offset=-1` with no `handle` and the
`cursor_resume` envelope in `cursor` or `TB-Shape-Resume`. A proven resume answers `307` with `Cache-Control: no-store`
to `?handle=<local>&offset=<target>`.

| Rule | Check | Refusal |
| --- | --- | --- |
| 0 | Envelope scope matches tenant, collection generation, canonical filter and wire format | `409 must_refetch` |
| 1 | Issued within 7 days; R8 retention and origin-prefix verification pass | `409 must_refetch` |
| 2 | Every named origin's local head stamp reaches the cursor's stamp | `409 must_refetch` |
| 3 | Cover rows within verified origin prefixes, above their proof floors and at or below their cursor stamps | none |
| 4 | Target is the boundary before the first uncovered collection row's commit, capped at the remote durable frontier in a cell, else at the local head | `409` for an unproven boundary or target below the shape retention floor |

Proof, origin heads and target read in one transaction. Rows at or below a proof floor replay conservatively.
Covered rows after the target are delivered again with destination values. The client keeps state and applies
responses in order; delivery is at least once.

The merge prerequisite is #211's persisted feed proofs. Its issuing-machine routing applies to the SSE feed.
Shape resumes verify proofs on the destination and fail closed when those proofs cannot be verified there.
An origin the destination forgot counts as behind. Cursor issue and seal stamps use the host wall clock.

`ShapeResumeExtensions` registers `IShapeResume` after `ShapesExtensions`, replacing the old gate's registration.

Fast HTTP coverage lives in `ShapeResumeTests.cs`; the second stack layer adds `ShapeResumeRegionTests.cs`
for gru-to-sjc failover with a deterministic covered-row replay and a third region confirming writes during restarts.
