# Database placement

Database creation accepts a home region and either replica regions or **All regions**. All-regions placement follows
new serving regions automatically. The database's **Replication** tab owns the replica set and **Move home** action.
Only organization members with management access can change placement; platform databases keep their placement.

The dashboard calls TerraBase's placement API through the management RPC contract using the current user's
`databases:write` authority. Moving to a replica waits for TerraBase to complete the move, then saves the returned
home, replicas and endpoint. The old home becomes a replica. A failed or unresolved move displays TerraBase's error;
the dashboard does not assume that a failed response changed the home.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

Catalog reconciliation also refreshes the home and replica set after a move made through a CLI or SDK. When regional
catalogs disagree, it prefers the record returned by the database's home region. Each update
checks the dashboard placement revision again inside its storage compare-and-set, so a listing started before a
newer dashboard edit cannot overwrite that edit. Status and health remain separate from placement.

The region catalog comes from TerraBase's regions RPC. Existing recorded regions keep their regional identity when
absent from discovery. The nearest answering region never chooses a different home for an existing database.

Server API, fencing and move timing: [database placement](../terrabase/placement.md). The earlier home-only controls
described in the identity descriptor and metric reference are superseded by this page.
