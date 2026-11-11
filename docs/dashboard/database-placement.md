# Database placement

The dashboard shows a database's regions as one set of equal regions and never names a home or a replica. The create
dialog and the **Regions** card on the database's **Replication** tab render the shared region map
(`Common/UI/Slices/RegionMap.cshtml`): each region is a checkbox named `regions`, pressing its dot toggles it, and the
last selected region cannot be unselected. Only organization members with management access can change the regions;
everyone else sees the map read-only. Platform databases keep their placement.

TerraBase's placement API still takes a home region and replica regions, so `DatabasePlacementEndpoints` derives them
from the selected set:

- **Create.** The home is the first region the user selected: the dialog posts the selection order as `regionOrder`,
  and without it the first checked region in the page's order is used. Every other selected region is a replica.
- **Save, home still selected.** One `updateDatabase` with the other selected regions as replicas. An all-regions
  database keeps that mode while every catalog region stays selected; unselecting one sends the explicit list, which
  turns the mode off. The dashboard has no control that turns all-regions mode on.
- **Save, home unselected.** Three calls in order: `updateDatabase` so that every selected region is a replica (skipped
  when the replicas already are exactly the selected regions); `moveDatabaseHome` to the first selected region, in the
  database's stored region order, that already was a replica, or to the first selected region when none was;
  `updateDatabase` with the selected regions other than the new home, which removes the old one. A call that fails
  leaves the earlier ones applied: the page shows the stored regions with TerraBase's error, and saving again continues.
- **No region selected** answers `422`.

The dashboard calls TerraBase's placement API through the management RPC contract using the current user's
`databases:write` authority. A move waits for TerraBase to complete it, then saves the returned placement and endpoint.
A failed or unresolved move displays an error; the dashboard does not assume that a failed response changed anything.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

Catalog reconciliation also refreshes the home and replica set after a move made through a CLI or SDK. When regional
catalogs disagree, it prefers the record returned by the database's home region. Each update
checks the dashboard placement revision again inside its storage compare-and-set, so a listing started before a
newer dashboard edit cannot overwrite that edit. Status and health remain separate from placement.

The region catalog comes from TerraBase's regions RPC. Existing recorded regions keep their regional identity when
absent from discovery. The nearest answering region never chooses a different home for an existing database.

Server API, fencing and move timing: [database placement](../terrabase/placement.md). The earlier home-only controls
described in the identity descriptor and metric reference are superseded by this page.
