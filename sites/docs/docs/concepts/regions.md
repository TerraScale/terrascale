---
title: Regions
---

# Regions

A database lives in a set of regions you choose, at least one. Every region of the set is an equal peer: each one serves reads and commits `INSERT`, `UPDATE` and `DELETE` where you called, and replicates them to the others. There is no home region and no writer region to choose, move or fail over. See [replication and consistency](/concepts/replication).

| Region | ID |
| --- | --- |
| São Paulo | `gru` |
| San Francisco Bay Area | `sjc` |
| Frankfurt | `fra` |

## Place a database

Create a database with `regions`, a list of region ids:

```http
POST /v1/projects/{project}/databases
Content-Type: application/json

{"name":"app","regions":["gru","fra"]}
```

The list must contain at least one serving region, with no duplicates. The response lists `regions` sorted by region id; the order carries no meaning, because every region is equal. Omit `regions` and the called region holds the database.

Send `"defaultConsistency":"strong"` to run every request that names no mode in strong mode; the default is `fast`. The same field is accepted when you update the database.

To place the database in every serving region, including regions added later, send `{"allRegions":true}` and do not send `regions`. The CLI flag is `-all-regions`. Send a list or `allRegions: true`, not both.

Change the set by sending the new set:

```http
PATCH /v1/projects/{project}/databases/{database}
Content-Type: application/json

{"regions":["sjc","fra"]}
```

A region you add copies the database from its peers and then serves reads and writes like the others. The call answers `503` `replica_not_ready` until the added regions have caught up. Repeat the same request: it finishes the change once they are ready. Regions that stay in the set keep serving throughout. A different change of the same database while one is running answers `409` `move_in_progress`.

SDKs take the same field:

```js
const db = await client.createDatabase(project, "orders", { regions: ["gru", "sjc"] });
await client.setDatabasePlacement(project, db.id, { regions: ["fra", "gru", "sjc"] });
```

```python
db = ts.create_database("orders", regions=["gru", "sjc"])
ts.set_database_placement(db.id, regions=["fra", "gru", "sjc"])
```

```csharp
var db = await client.CreateDatabaseAsync(project, "orders", placement: TerraBaseDatabasePlacement.In("gru", "sjc"));
await client.SetDatabasePlacementAsync(project, db.Id, placement: TerraBaseDatabasePlacement.In("fra", "gru", "sjc"));
```

```go
db, err := c.CreateDatabase(ctx, projectID, "orders", tb.CreateDatabaseOptions{Regions: []string{"gru", "sjc"}})
db, err = c.SetDatabasePlacement(ctx, projectID, db.ID, tb.DatabasePlacement{Regions: []string{"sjc", "fra"}})
```

```rust
let database = client.create_database("orders", &CreateDatabaseOptions {
    regions: Some(vec!["gru".into(), "sjc".into()]),
    ..Default::default()
}).await?;
client.set_database_placement(&database.id, &DatabasePlacement {
    regions: Some(vec!["fra".into(), "gru".into(), "sjc".into()]),
    ..Default::default()
}).await?;
```

`replica_not_ready` means the regions you added are stored; send the same request again. It usually completes within about half a minute. `move_unresolved` means an earlier move is unresolved; send the same request again later. The SDKs do not retry `replica_not_ready` for you, so your code repeats the same request, bounded:

```js
const signal = AbortSignal.timeout(90_000);
for (;;) { try { await client.setDatabasePlacement(project, database, { regions: ["fra"] }, { signal }); break; }
  catch (e) { if (e.code !== "replica_not_ready") throw e; await new Promise(resolve => setTimeout(resolve, 1000)); } }
```

The CLI takes the same list: `ts db create example -regions gru,fra` and `ts db placement example -regions sjc,fra`. `-all-regions` places the database in every serving region. When a requested region is not ready, `ts db placement <name> -regions …` exits with a message asking you to run the same command again.

## Choosing a region by ping

Wherever the dashboard shows the region map — when you create a database, change its regions, set up a project, and on the Home and Analytics pages — each region shows its ping: the round trip from your browser to that region, in milliseconds.

The dashboard measures it in your browser: it calls the region's public health endpoint, `https://<region>.api.terrascale.tech/healthz`, once to open the connection and then three more times, and shows the median. A green dot means under 100 ms, amber under 250 ms and red 250 ms or more. "Unreachable" means the region didn't answer within 3 seconds from your network. When you pick regions, the one closest to you is marked "Lowest ping".

The ping is measured from where you are now, not from your application's servers. If your application runs elsewhere, measure from there:

```sh
curl -o /dev/null -s -w '%{time_starttransfer}\n' https://gru.api.terrascale.tech/healthz
```

Home also shows a "Status probe" latency for each region: that one comes from TerraScale's status monitoring, not from your browser.

## Connect from any region

REST requests, WebSocket sessions and Postgres sessions can reach the database in any region of its set, and every region commits writes locally. Send the newest `TerraScale-Bookmark` you hold to read your own writes in whichever region answers; the SDKs do this for you. Requests without a bookmark read what their region already holds, which can briefly lag writes made in another region. See [replication and consistency](/concepts/replication).

If a region becomes unreachable, the other regions keep serving reads and fast writes with no failover step. Strong transactions need two of the three regions of a three-region set. `ts db regions <db>` and `GET …/databases/{database}/regions` show each region's health and replication lag.

## Storage and billing

Database files in every region of the set are encrypted at rest. Adding a region places a copy of your data in that region. Removing a region erases the database's files in that region, usually within half a minute; a region that cannot be reached then erases them when it is back. Backups and point-in-time history do not depend on any one region and stay available.

The catalog's regionsPerDatabase counts the regions in which a database's writes and stored bytes are metered: one per database on every plan. Copies in the database's other regions are not billed separately. See [pricing](/pricing).
