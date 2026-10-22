# TerraBase on ts-orch

ts-orch runs one TerraBase node per region (gru, sjc, fra) on its hosts, with host networking, and tells each node who
it is and who its peers are. TerraBase has no Fly discovery left.

| Variable | Set by | Meaning |
|---|---|---|
| `TS_REGION` | ts-orch | This node's region, for example `sjc`. |
| `TS_NODE` | ts-orch | This node's stable name (`[a-z0-9-]`, at most 63 characters); it names the member in the cell. |
| `TS_PEERS_FILE` | ts-orch | Absolute path of the peers file, mounted read-only into the task. |

All three are set together or not at all. Without them the node is a cell of its own, named `local` ([off-fly.md](off-fly.md)).
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
