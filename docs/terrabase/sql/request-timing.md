# SQL request timing

`POST /v1/projects/{project}/databases/{database}/sql/execute` returns
`Server-Timing` durations in milliseconds. `app;desc="<region>"` measures origin
processing from entry to response headers, including queueing, authentication,
catalog resolution and execution. It excludes the client connection, TLS, edge
and response-body transfer.

The stage durations partition that origin interval, except the acknowledgement
substeps (`local_capture` through `home_wait`), which are nested inside `ack`:

| Stage | Work since the previous checkpoint |
| --- | --- |
| `admission` | Request setup, listener checks and foreground memory admission |
| `auth` | Authentication, project checks and authorization |
| `home_lookup` | Lookup for automatic forwarding before the endpoint |
| `qos` | Request QoS admission |
| `project` | Project catalog resolution |
| `placement` | Database placement check |
| `tenant` | Tenant, body and batch resolution |
| `replica_open` | Replica placement, statement classification and opening its reader |
| `route` | Parsing and schema-based write routing |
| `local_begin` | Releasing the routing reader, ordering fences and acquiring the replica writer |
| `local_execute` | Write allowance checks and SQL execution on the replica |
| `ack` | Capture, clock stamp, durable publish, local commit and requested acknowledgement |
| `local_capture` | Capture and encode row changes inside `ack` |
| `local_hlc` | Issue a timestamp, including any required durable HLC reservation |
| `local_seal` | Encode and seal the push payload |
| `push_publish` | Publish to JetStream and receive its durable acknowledgement |
| `push_hold` | Hold the required copy on the discovered regional quorum |
| `local_commit` | Commit and release the local writer |
| `home_wait` | Remaining wait for the home's applied notice, when requested or required |
| `execute` | Remaining endpoint work and response serialization |

A forwarded response retains the home's timings and adds the entry region's
timings. `home;desc="<region>"` is the forwarding call through the home's response
headers; it includes the mesh round trip and the home's processing. It overlaps
the entry region's `execute` and `app` durations. Use the `app` entry whose `desc`
names the entry region for total server time; do not add nested durations.
Only reached checkpoints are emitted. Early refusals or home execution can fold
several stages into `execute`. Stage names can repeat on forwarded responses, with the entry region's stages
last. Timing values are measurements, not a protocol deadline or an SLA.

The measurements contain fixed stage and region names, never SQL, tenant names,
credentials or query arguments. Read behavior and acknowledgement guarantees
are unchanged by timing instrumentation.
