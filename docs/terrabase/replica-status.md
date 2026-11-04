# Database region observations

`GET /v1/projects/{project}/databases/{database}/regions` requires `databases:read` and the same project/database authority as the database read. It runs at the database's home; another region returns `421 wrong_region` naming that home. Responses are not cached.

The MagicOnion `IManagementService.GetDatabaseRegionsAsync` method returns the same contract. The dashboard calls the home through this RPC. Its Replication tab refreshes observations every ten seconds and also offers an explicit refresh.

The response contains `databaseId`, `homeRegion`, `observedAt`, and `replicas`. Each replica has `region`, nullable `lagMs`, `lastPulledAt`, `lastVerified`, and `revision`. Placement includes every configured replica, including replicas with no observations. For an `allRegions` database, replicas are every serving region other than its home, discovered from the current region catalog.

The home attributes a page pull to the authenticated internal peer's mesh source address from `TS_PEERS_FILE`, and records its returned revision only after all pages and the final flush succeed. `lastPulledAt` means a complete pull was served; it does not claim that the replica applied the pages. A cancelled or failed pull does not advance it, and an older concurrent pull cannot overwrite a newer revision.

`lagMs` is zero when the last served revision includes the home's newest commit. Otherwise it is the age, on the home's clock, of the oldest commit that revision lacks. Revision indexing records commit times. Checkpoints do not count as commits. Commit boundaries and observations are bounded operational telemetry owned by the open database, with up to eight replicas and 256 recent commit boundaries for pulls still in flight. Once observed, the oldest missing commit for a replica is retained until its next advancing pull, even as later commits expire. Reopening the database resets these observations. If the relevant commit time is unknown or expired, lag is null, never zero or an invented starting state.

`lastVerified` remains null until checksum verification supplies it. The Replication tab renders this as **not yet verified**. Missing pulls render as **—**; unknown lag renders as **Not reported**. Verification and successful page delivery are independent observations.
