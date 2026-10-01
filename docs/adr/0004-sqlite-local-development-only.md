# SQLite is local development only

Status: Accepted.

Keep SQLite behind explicit `terrascale/local` Node conditions and require
`sqlite: { enabled: true, databasePath: "/absolute/path/local.sqlite" }`. Reject
production, CI, qualification, staging and deployment indicators before opening
storage. Test helpers preserve these indicators and skip prohibited local suites.

The local runtime binds explicit native collection schemas, retains complete
points, atomic transactions and durable local status, and uses frozen snapshot
cursors and finite subscription poll groups. Its local state tokens and writer
authority do not prove producer compatibility or regional durability.

The listener defaults to loopback. Remote binding requires `allowRemote: true`,
required authentication and an explicit bearer token of at least 32 characters.
The CLI reads that token from `TERRASCALE_LOCAL_BEARER_TOKEN` without printing it.
Local Better Auth uses actual SQLite transactions and official maintained tests;
it cannot replace production security or deployed customer qualification.
