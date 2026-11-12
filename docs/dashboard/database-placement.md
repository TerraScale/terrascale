# Database placement

The dashboard shows a database's regions as one set of equal regions and never names a home or a replica. The create
dialog and the **Regions** card on the database's **Replication** tab render the shared region map
(`Common/UI/Slices/RegionMap.cshtml`): each region is a checkbox named `regions`, pressing its dot toggles it, and the
last selected region cannot be unselected. Only organization members with management access can change the regions;
everyone else sees the map read-only. Platform databases keep their placement.

Region discovery supplies the available regions. With a catalog, create and Replication use the shared map with
region icons. Creates always send homeRegion, optional replicaRegions and allRegions, which every engine accepts.
The map still offers the catalog's regions; the first selected region becomes the home and the rest replicas.
Placement saves send the complete set in one management call when the engine's database response exposes a
`regions` set. A discovery catalog alone does not establish support for region-set placement: engines returning
home and replicas save through those operations even when discovery succeeds. An all-regions database keeps that
mode while every offered region stays selected. If a region is still copying,
the editor retains the requested selection, records the engine placement and shows a status message. Saving again
completes the change once copying finishes. A stale route refreshes placement before resubmitting; concurrent edits
require reviewing the region set first.

Without a catalog, create uses the shared map with the region verified through TerraBase health. It sends
`homeRegion`, optional `replicaRegions` and `allRegions`, never `regions`. A response without a `regions` array derives its placement from
home plus replicas. Replication remains editable for managers of ready customer databases, using the same shared
map and offered regions as before. It saves through the home/replica operations and never sends `regions`:

- **Create.** The home is the first region the user selected: the dialog posts the selection order as `regionOrder`,
  and without it the first checked region in the page's order is used. Every other selected region is a replica.
- **Save.** Every selected code is checked against the offered regions before the first call: a code the offered set does
  not list answers `422` and nothing is called. The selected regions are then handled in region id order, so a retry
  takes the same steps as the first attempt.
- **Save, home still selected.** One `updateDatabase` with the other selected regions as replicas. An all-regions
  database keeps that mode while every catalog region stays selected, however many regions the catalog has; unselecting
  one sends the explicit list, which turns the mode off. The dashboard has no control that turns all-regions mode on.
- **Save, home unselected.** Three calls in order: `updateDatabase` so that every selected region is a replica (skipped
  when the replicas already are exactly the selected regions); `moveDatabaseHome` to the first selected region, in
  region id order, that already was a replica, or to the first selected region in that order when none was;
  `updateDatabase` with the selected regions other than the new home, which removes the old one. A call that fails
  leaves the earlier ones applied: the page shows the stored regions with the error, and saving again continues.
  Replacing a database's only region takes two saves: the first adds the new region and answers that it is still
  syncing while the old one keeps serving, and the next one finishes once the new region has caught up.
- **No region selected** answers `422`, and so do more than 9 regions outside all-regions mode.

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
