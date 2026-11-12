# Database placement

The dashboard shows a database's regions as one set of equal regions and never names a home or a replica. The create
dialog and the **Regions** card on the database's **Replication** tab render the shared region map
(`Common/UI/Slices/RegionMap.cshtml`): each region is a checkbox named `regions`, pressing its dot toggles it, and the
last selected region cannot be unselected. Only organization members with management access can change the regions;
everyone else sees the map read-only. Platform databases keep their placement.

Region discovery determines the engine contract. When it returns a catalog, the create dialog and placement editor
use the shared map. Creates send the selected `regions` set, and placement changes send the complete set in one
management call. An all-regions database retains that mode while every offered region remains selected.

When discovery is unavailable, the create dialog shows the region verified through TerraBase health. Creates retain
`homeRegion`, optional `replicaRegions` and `allRegions`, and never send `regions`. A successful response with a home
and replicas is canonical placement even when it has no `regions` array. The placement editor stays read-only until
a catalog is available. Existing recorded regions keep their identities.

At least one region and at most nine explicit regions must be selected. Only catalog entries can be newly selected.
If a region is still copying, the editor keeps the requested selection, records the engine's current placement and
shows a status message. Saving again completes the change after copying finishes. A stale regional route refreshes
the database placement before resubmitting; a concurrent change to the region set requires reviewing it first.

The dashboard calls TerraBase through the current user's management RPC authority. Rename and delete also refresh a
stale route, preserving regional database management after a placement change.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

Catalog reconciliation also refreshes the home and replica set after a move made through a CLI or SDK. When regional
catalogs disagree, it prefers the record returned by the database's home region. Each update
checks the dashboard placement revision again inside its storage compare-and-set, so a listing started before a
newer dashboard edit cannot overwrite that edit. Status and health remain separate from placement.

The region catalog comes from TerraBase's regions RPC. Existing recorded regions keep their regional identity when
absent from discovery. The nearest answering region never chooses a different home for an existing database.

Server API, fencing and move timing: [database placement](../terrabase/placement.md). The earlier home-only controls
described in the identity descriptor and metric reference are superseded by this page.
