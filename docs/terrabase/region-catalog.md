# Region catalog (TS-901, TS-885)

`GET /v1/regions` (anonymous, client listener) answers `200 {"regions":["fra","gru","sjc"]}`: the id of every region
with a live TerraBase member, this node's included, ordered ordinally. It comes from membership (ts-orch's peers file in
a cluster), so a region appears once its first node joins and leaves once its last node is gone; nothing is configured.
Every region answers the same list. Responses may be cached for 5 seconds.

Database placement requests name catalog regions:

- create: `homeRegion` and each of `replicaRegions` must be in the catalog;
- update: each of `replicaRegions` must be in the catalog or already placed for the database, so a region whose
  nodes are briefly down is not dropped, and a removed region can be added back while it serves.

Anything else answers `400 invalid_request` whose message names the unknown region and the valid ones. Management
commits applied between regions keep accepting any well-formed region: they repeat a decision the home region took.

The dashboard, SDKs and sign-up read the catalog instead of a fixed list; `https://<id>.api.terrascale.tech` reaches
region `<id>` ([region-identity.md](region-identity.md#regional-hosts)).
