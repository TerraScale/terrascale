# Shape resume across machines (FR-23)

Implements [shape-reads.md](../shape-reads.md) section 9. A resume is `GET …/shape?offset=-1` with no `handle` and the
`cursor_resume` envelope in `cursor` or `TB-Shape-Resume`. Code: `src/TerraBase/Features/Shapes/ShapeResume.cs` and
`src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.ShapeResume.cs`.

## Rules as built

| Rule | Check | Refusal |
| --- | --- | --- |
| 0 | Envelope scope equals `ShapeService.ResumeScope` of this shape | `409 must_refetch` |
| 1 | Issued within 7 days; the R8 feed read (`ReadFeedAsync`, the collection only, zero events) is not expired: no promised event was trimmed and, for a cursor from another machine, each named origin's local prefix hash equals the cursor's | `409 must_refetch` |
| 2 | Every named origin's local head stamp is at or above the cursor's | `409 must_refetch` (behind) |
| 3 | A local row of the collection is covered when the cursor names its origin and its stamp is at or below that stamp | none |
| 4 | Target = the commit boundary before the first uncovered row's commit, else the sealed head; it must be a proven boundary at or above the handle's retention floor | `409 must_refetch` |

Rules 1 to 4 read in one read transaction, so the head, proof and target agree. A proven resume answers
`307` with `Cache-Control: no-store` to `?handle=<local>&offset=<target>`.

## Delivery

At least once. Covered rows after the target are delivered again with this machine's values; the client applies them in
order (section 7 rule 4). The target never moves past an uncovered row on a stamp comparison alone.

## Limits

- Rule 1 verifies the prefix on the receiving machine. Replaying a resume to its issuing machine arrives with #211; until
  then a refill or repair gap on the receiving machine refuses the resume (`409`), it never skips.
- An origin the receiving machine forgot (all its events aged out) counts as behind.
- `ShapeService.ResolveAsync` keeps the old gate but is no longer registered; `ShapeResumeExtensions` replaces the
  `IShapeResume` registration. Delete the gate when the shape files are free to edit.

## Tests

`tests/TerraBase.Fast/Shapes/ShapeResumeTests.cs`: rules 0 to 4 and both transports on one machine, a target below the
retention floor, and a client synced on gru that resumes on sjc of a replicated pair and converges (repeats allowed).
