# Dashboard import evidence

Source main: `181c8dc17daa8c12ddb49f73ff45a8a78170ed72`.

The import merge preserves the original dashboard history as its second parent.
Its `imports/dashboard` tree equals the source root tree:
`e93447e62178e155b246372f3a39c5ee49c93df9`.

The following relocation moves projects to `src/TerraScale.Dashboard` and
`tests/TerraScale.Dashboard.E2E`, helpers to `scripts/dashboard`, documentation
to `docs/dashboard`, and benchmark evidence to `benchmark/dashboard`. Subsequent
changes apply the monorepo .NET 11 analyzer policies and source SDK references.
The historical merge retains the exact original tree before integration edits.

Original repository backup: delta:/home/mariogk/Projects/TerraScale/.cos/monorepo-20261029/ts-dashboard.bundle.

The delta bundle was verified by restoring all 405 refs and counting all 2177
commits. SHA-256: `ec922eb3005862931bfc48d03cb5af07357ed41847a1a34d9c930ad664129085`.
There were zero source tags and zero releases. All 14 open issues were transferred
to terrascale issues 944, 945, 946, 948, 950, 953, 955, 958, 960, 961, 962, 963, 964, 965.
Original refs are retained under `refs/archive/ts-dashboard/` on the monorepo origin.
