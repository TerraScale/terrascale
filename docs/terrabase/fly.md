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

## Catalog

Every machine writes its own catalog (`_system.db`) locally. Catalogs are not replicated until R10a, which makes `_system`
a lattice database replicated to every region ([multi-region-replication.md](multi-region-replication.md) section 15).
TerraBase does not read `PRIMARY_REGION`. `primary_region` in `fly.toml` stays for Fly's `min_machines_running`.

## Other regions

Cross-region replication pulls from every other region the DNS lists, asking each member directly with gRPC
`Wan.Ask` on its peer listener ([cross-region.md](cross-region.md)). A region joins as soon as one of its machines is
listed; there is no region list to maintain.

## Listeners

| Listener | Port | Binds | Protocol | Serves |
| --- | --- | --- | --- | --- |
| Public | 8080 | every interface, behind Fly Proxy (`[http_service]`); loopback in Development off Fly | HTTP/1.1, TLS ends at Fly's edge | the client API, `/healthz`, `/readyz`, OpenAPI |
| Internal | 8081 | `fly-local-6pn` (loopback off Fly) | HTTP/2 without TLS (h2c) | inter-node gRPC, the platform operator's Management API, administration, `/metrics` |
| Metrics | 9091 | every interface, no Fly service (`[[metrics]]`); loopback in Development off Fly | HTTP/1.1 | `/metrics` only |

- Every request on the internal listener verifies `X-TerraBase-Peer-Credential` before endpoint execution and records matched/unmatched outcomes. This preparation release does not refuse unmatched requests; refusal follows after all three regions run the senders. The shared `TERRABASE_PEER_SECRET` arrives from Fly secrets; a missing or malformed secret stops a Fly node before it listens. Transport remains h2c over Fly's private network. On the
  internal listener the platform operator acts as the user named in `X-TerraBase-User-Id`; every use is logged.
- Kestrel cannot serve HTTP/1.1 and HTTP/2 prior knowledge on one cleartext port (checked 2026-10-11: an HTTP/2
  request to an `Http1AndHttp2` port answers `HTTP_1_1_REQUIRED`). The internal listener serves gRPC, so it is
  HTTP/2 only: its clients send HTTP/2 with prior knowledge (`curl --http2-prior-knowledge`, .NET
  `HttpVersion.Version20` with `RequestVersionExact`).
- `port`, `internal-port` and `metrics-port` exist only so tests can bind ephemeral ports.

## fly-replay

A public request a machine cannot serve because it landed in the wrong region still answers its 421, and also sets
`fly-replay: region=<region>`, so Fly Proxy replays it there and the client gets that region's answer:

- a database outside this region's placement, or a Management change of a database managed elsewhere: its home region
  (`421 wrong_region`, [cross-region.md](cross-region.md));
- a mismatched `X-TerraScale-Region-Pin` naming a single well-formed region: that region.

A request carrying `Fly-Replay-Src` was replayed already and is answered where it landed, without another replay, so a
disagreement between regions never loops. Requests on the internal listener never get `fly-replay`; Fly Proxy is not in
front of it.

Fly replays only bodies below 1 MB.
