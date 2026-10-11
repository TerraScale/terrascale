# TerraBase on Fly.io

TerraBase runs as the Fly app `ts-terrabase`. It takes its identity and finds its cell from Fly; it has no identity or
membership settings.

## Identity

Fly sets these variables on every machine; TerraBase reads them at startup (`Configuration/TerraBaseOptions.cs`):

| Variable | Meaning |
| --- | --- |
| `FLY_REGION` | The machine's region; `X-TerraBase-Region`, `/healthz` and placement use it |
| `FLY_MACHINE_ID` | The machine's ID, which names it in the cell (ring, metrics, usage batches) |
| `FLY_APP_NAME` | The app whose internal DNS lists the cell |
| `PRIMARY_REGION` | The app's primary region (`primary_region` in `fly.toml`), home of the catalog primary; the machine's own region when unset |

`FLY_REGION`, `FLY_MACHINE_ID` and `FLY_APP_NAME` are set together or not at all; a partial or malformed identity stops
the process with exit code 2. A process with none of them (a developer machine) is region `local`, machine `local`, a
cell of its own. Tests give each server its identity through the harness (`tests/TerraBase.TestHarness/TestTopology.cs`).

## Membership

Every 5 seconds a machine reads the TXT record `vms.<app>.internal` of Fly's internal DNS: one `<machine id> <region>`
entry per started machine of every region (`Engine/Cluster/FlyMachines.cs`). Fly has no `regions.<app>.internal`
record (checked 2026-10-11), so the regions are the ones these entries name. A machine's peer listener is
`<machine id>.vm.<app>.internal`.

- The **cell** is the started machines of the machine's own region: the ring that picks each database's backup
  shipper, the replication sender's peers and anti-entropy's members.
- A machine holds its **lease** while its last successful read, which listed it, began less than 10 seconds ago. A
  machine Fly stops leaves the DNS; one cut off from the DNS stops acting on its lease after 10 seconds.
- The replication sender takes its peers from the cell when it starts; anti-entropy follows the cell as it changes.

## Catalog primary

Machines of the primary region are catalog primaries; every other machine follows the first machine (lowest ID) of the
primary region and finds it again whenever the DNS changes. The primary region holds one TerraBase machine until
anna-full (TS-647) removes the catalog primary.
