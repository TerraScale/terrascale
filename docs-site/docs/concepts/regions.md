---
title: Regions
---

# Regions

A database lives in a set of regions you choose, at least one. Every region of the set accepts reads and writes. The region you call accepts the write; depending on the connection it commits it locally and confirms it, or forwards it to the region that orders writes. You pick the set; TerraBase decides which region of the set orders the database's writes, and moves that when the set changes.

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

The list must contain at least one serving region, with no duplicates. The response lists `regions` sorted by region id. That order carries no meaning and does not say which region orders writes. Omit `regions` and the called region holds the database.

To place the database in every serving region, including regions added later, send `{"allRegions":true}` and do not send `regions`. The CLI flag is `-all-regions`. Send a list or `allRegions: true`, not both.

Change the set by sending the new set:

```http
PATCH /v1/projects/{project}/databases/{database}
Content-Type: application/json

{"regions":["sjc","fra"]}
```

If the region that currently orders writes stays in the set, it keeps that role. If the new set drops it, regions you add stay added. The call answers `503` `replica_not_ready` until one of the requested regions has replicated the database. Repeat the same request: it finishes the change once a requested region is ready. A different change of the same database while one is running answers `409` `move_in_progress`.

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

## Earlier fields

`homeRegion`, `replicaRegions` and `POST …/move-home` are earlier fields and still work during the transition. New calls send `regions`. `allRegions: true`, sent without `regions`, is how you ask for every serving region, including regions added later. Responses list `regions`.

## Connect from any region

REST requests and Postgres sessions can reach the database from any region of its set. Reads are served in the region you call. A write sent to another region of the set is forwarded, or committed locally and confirmed, depending on the statement. See [replication](/concepts/replication).

At any moment one region of the set orders that database's writes. TerraBase picks it and moves it when the regions change. You do not choose it. `TerraScale-Durability: home` waits until that region has applied a local write. The server forwards a timed-out local publish with `TerraBase-Publish-Id` set to the same transaction id, and that region applies the write once. A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

A region of the set can lag behind the region that orders writes. Postgres and WebSocket sessions preserve read-your-writes by using the ordering region until the region you call catches up. REST reads are eventually consistent. A region that already holds the database can serve reads it already holds while the ordering region is unreachable. A database in a single region waits for that region.

## Storage and billing

Database files in every region of the set are encrypted at rest. Adding a region places a copy of your data in that region. Removing a region erases the database's files in that region, usually within half a minute; a region that cannot be reached then erases them when it is back. Snapshots that region took stay in it and cannot be restored while it is outside the set.

The catalog's regionsPerDatabase counts the regions in which a database's writes and stored bytes are metered: one per database on every plan. Copies in the database's other regions are not billed separately. See [pricing](/pricing).
