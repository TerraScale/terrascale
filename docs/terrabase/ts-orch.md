# TerraBase on ts-orch

ts-orch runs one TerraBase node per region (gru, sjc, fra) on its hosts, with host networking, and tells each node who
it is and who its peers are.

| Variable | Set by | Meaning |
|---|---|---|
| `TS_REGION` | ts-orch | This node's region, for example `sjc`. |
| `TS_NODE` | ts-orch | This node's stable name (`[a-z0-9-]`, at most 63 characters); it names the member in the cell. |
| `TS_PEERS_FILE` | ts-orch | Absolute path of the peers file, mounted read-only into the task. |

All three are set together or not at all. Without them the node is a cell of its own, named `local` ([local-host.md](local-host.md)).
With them, `TERRABASE_PEER_SECRET` is required.

## Peers file

One line per region the service runs in whose node ts-orch sees up, this node's included:

```
<node> <region> <netbird-ipv4>
```

ts-orch replaces the file atomically whenever its mesh view changes. TerraBase reads it every membership heartbeat (5 s):
a region that disappears stops receiving replication until it is listed again; malformed lines are ignored.

- The internal listener binds only this node's own NetBird address from the file, read at startup, never a public
  interface. A file that does not list `TS_NODE` in `TS_REGION` stops the start (exit 2).
- Peers are reached at `http://<netbird-ipv4>:<internal port>/`: every member of a cell uses the same `internal-port`.
- A second TerraBase service on the same hosts (for example the disposable `terrabase-e2e`) sets its own `TERRABASE_PORT`,
  `TERRABASE_INTERNAL_PORT`, `TERRABASE_METRICS_PORT` and data mount.

Listener port settings are `port`, `internal-port`, `metrics-port` and `postgres-port`;
tests use ephemeral ports.

## ts-replay

TerraBase answers a public request it cannot serve in its current region with 421 and
`ts-replay`, requesting replay by the edge. This is the service-side contract added
in [#353](https://github.com/TerraScale/terrascale/pull/353); it does not establish
that the deployed edge implements replay. Verify the edge revision and routing
before relying on it. The requested destinations are:

- a database outside this region's placement: every region that serves it, home first
  (`ts-replay: region=<home>,<replica>,...`, `421 wrong_region`, [cross-region.md](cross-region.md));
- a Management change of a database managed elsewhere (create, update, branch, delete, imports): its home region only
  (`ts-replay: region=<home>`);
- a changes-feed resume landing on another machine than its cursor's issuer:
  `ts-replay: region=<issuer region>;instance=<issuer machine>;fallback=self` (no replay when the issuer is not listed);
- a mismatched `X-TerraScale-Region-Pin` naming a single well-formed region: `ts-replay: region=<pin>`;
- a regional host landing in another region: `ts-replay: region=<host region>;fallback=self`.

The fields are `;`-separated. The required edge behavior with `fallback=self` is that a replay reaching no listed region comes back to the first
machine with `ts-replay-failed: 1`; without it the edge returns the 421. A request carrying `ts-replay-src` was
replayed already and is answered where it landed, without another replay, so a disagreement between regions never
loops. Requests on the internal listener never get `ts-replay`; the edge is not in front of it.

The edge contract limits replay to bodies up to 1 MiB and excludes websocket upgrades.
