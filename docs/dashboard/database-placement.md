# Database placement

The dashboard shows a database's regions as one set of equal regions and never names a home or a replica. The create
dialog and the **Regions** card on the database's **Replication** tab render the shared region map
(`Common/UI/Slices/RegionMap.cshtml`): each region is a checkbox named `regions`, pressing its dot toggles it, and the
last selected region cannot be unselected. Only organization members with management access can change the regions;
everyone else sees the map read-only. Platform databases keep their placement.

## One call

TerraBase takes the set itself ([database placement](../terrabase/placement.md)), so every create and every save is
one call with `regions`:

- **Create.** `createDatabase` with the selected regions. The dialog posts the selection order as `regionOrder`, and the
  call goes to the first region selected; without a selection the project's region is used. The MCP tool
  `create_database` takes `regions` the same way.
- **Save.** `updateDatabase` with the selected regions. The form lists the catalog regions it offered when the page
  rendered (`offered`). An all-regions database keeps that mode (`allRegions: true`, no `regions`) while every offered
  region stays selected, so a region the catalog gains after the page rendered does not turn the mode off; unselecting
  one sends the explicit list, which turns the mode off. The dashboard has no control that turns all-regions mode on.
- Every selected code that is not already one of the database's regions is checked against the region catalog before
  the call: a code the catalog does not list answers `422` and nothing is called. No region selected answers `422`, and
  so do more than 9 regions outside all-regions mode.

The dashboard never chooses which region takes over when a region is dropped: TerraBase does, and the dashboard sends
its calls to the region TerraBase's answers name for the database. The calls use the current user's
`databases:write` authority; a successful answer is saved as the database's regions.

## What the user sees after a save

| TerraBase answers | Regions card | JSON caller |
|---|---|---|
| `200` | the page reloads with the saved regions | `200` database |
| `503 replica_not_ready` | the page stays, with the alert below; the checkboxes keep the selection that was asked for | `503` problem with `code: replica_not_ready` |
| `409 move_in_progress` | "Another change to this database's regions is still running. Try again in a moment." with the selection kept | `409` problem with `code: move_in_progress` |
| anything else | the error message of every other TerraBase failure, with the selection kept | the same problem |

`replica_not_ready` means TerraBase added the new regions and they are still copying the database (replacing a
database's only region takes about half a minute). The dashboard reads the database again and saves what TerraBase now
stores, without an audit event (the `database.placement` event belongs to the save that completes), then shows:
"<new regions> added. It is (They are) still copying this database, which usually takes about half a minute. Save again
to finish; until then <regions being dropped> keep(s) serving." When the save adds no region (dropping a region before
the one that stays has copied the database), it shows "<selected regions> is (are) still copying this database, ..."
with the same ending. If that re-read answers `421 wrong_region`, the change finished in between: the dashboard
refreshes its record from TerraBase's listings and the page reloads with the stored regions, as a success. The checkboxes show what the user
asked for, so **Save regions** sends the same request again; the alert says what is stored right now, and the
replication table below the card lists every stored region with its copy progress. A save that still gets
`replica_not_ready` shows the same alert. A JSON caller does the same: repeat the request with the same `regions` until
it answers `200`.

## A database whose regions changed elsewhere

The dashboard calls the region its stored record names. When the regions changed through the CLI, an SDK or another
session, TerraBase refuses that call with `421 wrong_region` before doing anything. The dashboard then refreshes the
project's records from TerraBase's listings (the same reconciliation the databases list runs) and, when the record now
names another region, repeats the call there once:

- rename, TTL, delete and fork are repeated as they are: each was refused before it ran, and none depends on the
  database's regions;
- a save of regions is repeated only when the database's regions are the ones the page showed; when they changed, it
  answers `409` "This database's regions changed in another session. Check them and save again." and changes nothing.

A second refusal shows its error as before. The API-key MCP tools (`get_database`, `fork_database`, `set_database_ttl`,
`delete_database`, `create_backup`, `get_restore_status`, `restore_backup`) look the database up once; a `421` there
repeats the lookup at the region TerraBase names, and the tool then calls that region.

## Catalog and listings

Catalog reconciliation refreshes a database's regions after a change made through a CLI or SDK. When regional catalogs
disagree, it prefers the record returned by the region the database's calls go to. Each update checks the dashboard
placement revision again inside its storage compare-and-set, so a listing started before a newer dashboard edit cannot
overwrite that edit. Status and health remain separate from placement.

The region catalog comes from TerraBase's regions RPC. Existing recorded regions keep their regional identity when
absent from discovery.
