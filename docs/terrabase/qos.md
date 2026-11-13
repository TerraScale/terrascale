# Organization quality of service

Resource admission belongs to the organization and is shared across its projects, databases and credentials. The runtime reads the catalog plan rather than trusting a client-selected plan. Replication and system maintenance retain process safety limits even when commercial quotas do not apply. Platform database requests use the process concurrency, engine and memory bounds without consuming the commercial request-rate allowance. At most 64 platform requests execute and 64 wait, in arrival order, for up to the five-second request bound.

## SQL work and memory

SQL statements share the request and transaction deadlines in `Features/Sql/SqlLimits.cs`. The native engine enforces query cancellation and value-length bounds. Statement, body, row, byte and transaction-growth limits are in the [SQL contract](sql/native-sql.md). Admission, authorization and the SQL sandbox run before customer work executes.

Request/result buffers reserve process and organization memory before allocation. A reservation lasts until the owned buffer or response is released, including cancellation and error paths. Native allocations and physical I/O are not fully measured by managed reservation estimates. See [memory admission](memory.md) and [shared-process tenancy](shared-process-tenancy.md).

Storage work retains tenant ownership through queues and callbacks. Scheduling is bounded and fair across organizations, but cannot promise hard latency isolation for an already-running native call or a stalled device. Cancellation drains the owned reader and operation gate before releasing resources.

## SQL sessions

Postgres and SQL WebSocket sessions share one organization allowance per process, across credentials, projects and databases. The allowance is twice the plan's concurrent-request limit: 8 for Free, 16 for Starter, 32 for Pro and Team, and 128 for Enterprise. Each process also admits at most 4,096 connections, including connections relayed to another node. At most 2,048 slots may be relays, reserving half the ceiling for locally served sessions. Relay shutdown allows 2 seconds for the close handshake before aborting both sockets and awaiting both forwarding pumps. After authentication, relays and local sessions consume the same organization allowance on the entry process. Forwarded SQL sessions also take the allowance on the home process until their session is disposed. Before relay authentication, at most eight concurrent attempts per client address are admitted; WebSocket authentication has five seconds and Postgres authentication has ten. Postgres relay establishment has one ten-second deadline through the ready response; cancellation drains forwarding before releasing admission. A Postgres connection above the allowance receives fatal SQLSTATE `53300`; a WebSocket receives `tenant_busy` and closes with policy violation. Browser authentication succeeds only after session admission.

A session with no message in flight and no open transaction disconnects after 30 minutes without a complete protocol message. Client pools should retire or validate idle connections in under 30 minutes. Once a message starts arriving, its deadline is its start plus 30 seconds plus the bytes received divided by 16 KiB/s. Both protocols re-evaluate that deadline as bytes arrive, allowing a sustained upload while cutting a byte-at-a-time trickle. WebSocket ping/pong traffic does not count as activity. Executing a statement does not consume either waiting period. Inside an open transaction, only the 5-second transaction deadline applies, starting at BEGIN even if no statement follows. An input wait that reaches it reports query_canceled (SQLSTATE 57014) and disconnects. Disconnects and failed session startup release the allowance after session cleanup. Statement execution, snapshots, quota admission, transaction acquisition and replica promotion use the remaining transaction time; promotion preserves the original deadline. A forwarded call sends a remaining duration, which the receiving node enforces with its own clock. Previous nodes ignore the additional JSON field; a receiving node without that field applies its own full bound.

Session metrics expose `terrabase_qos_active_sessions`, `terrabase_qos_relay_sessions` and the maximum organization utilization in `terrabase_qos_organization_session_utilization`. `terrabase_qos_session_refusals_total` counts `organization_cap`, `process_ceiling`, `relay_ceiling` and `preauthentication_allowance` by the bounded `kind` label, without organization labels.

Enterprise uses finite runtime ceilings: 2,000 requests/second, a 4,000-request burst, 64 concurrent requests, a 5-second query timeout, 20 million query work units, 400,000 scan rows, 128 MiB scanned bytes, 1 TB storage, 1,000 databases, 100 million monthly writes and 1 billion monthly SQL reads. Engine time replenishes at 8 seconds/second with a 60-second burst. These runtime safety ceilings do not change billing allowances. General SQL VM work is not yet reported by the native driver; the work ceiling currently covers instrumented catalog scans and FTS, alongside the existing execution timeout.

## Storage

SQL writes take the organization quota lease before the database writer. The transaction checks net used-page growth and persists SQL storage accounting atomically. Failed statements and rolled-back work cannot publish a larger unchecked database. Branch and restore publication retain the same quota and identity checks through registration or rollback.

Storage accounting, physical file bytes, replica copies and backup retention are distinct quantities. A node-local check is not proof of an atomic global physical-disk quota. Disk work also passes [bulk disk admission](disk-admission.md).

## Responses and monthly caps

Preserve the existing rate, concurrency, memory and storage refusal contract, including `Retry-After` where supplied. Never bypass validation to make a request fit. Enterprise plans still observe statement, transaction and process safety bounds.

SQL reads are free with plan fair use; shape reads are free and unmetered. Committed writes count toward the monthly write allowance and cap. Replication does not add billable units. The published pricing catalog and [operation allowance contract](operation-allowance.md) define the current values; do not duplicate a stale plan table here.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
