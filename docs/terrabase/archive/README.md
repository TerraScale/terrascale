# Archived TerraBase designs

These pages describe designs that are no longer built or no longer served. They are kept for history only; none of
them describes TerraBase on `main`. Current storage and placement: [single-region decision](../decisions/2026-10-29-single-region-until-sync.md)
and [cross-region.md](../cross-region.md).

| Page | Why it is archived |
| --- | --- |
| [anna-core.md](anna-core.md) | Coordination-free cell design (TS-634); the lattice engine and in-region replication are removed. |
| [actor-transport.md](actor-transport.md) | Preparatory actor transport (TS-648); `Program` never registers it and nothing routes through it. |
| [operator-tenant-recovery.md](operator-tenant-recovery.md) | Peer-certified rebuild retry endpoint; the endpoint and peer refill are removed. |

Removed with the replication engine (`440e54abc`), readable at their last revision:
[multi-region-replication.md](https://github.com/TerraScale/terrascale/blob/0d5d2b64c89553bf66ed30388b0624adc43d2741/docs/terrabase/multi-region-replication.md),
[replication.md](https://github.com/TerraScale/terrascale/blob/0d5d2b64c89553bf66ed30388b0624adc43d2741/docs/terrabase/replication.md).
