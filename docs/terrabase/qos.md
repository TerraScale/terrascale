# Organization quality of service

Resource admission belongs to the organization and is shared across its projects, databases and credentials. The runtime reads the catalog plan rather than trusting a client-selected plan. Replication and system maintenance retain process safety limits even when commercial quotas do not apply.

## SQL work and memory

SQL statements share the request and transaction deadlines in `Features/Sql/SqlLimits.cs`. Beyond those deadlines, each statement runs under the organization's authenticated plan budget (`Features/Qos/QosPolicy.cs`): engine execution time (Hobby 1 s, Starter 2 s, Pro and Team 5 s) and engine work units (1, 2 and 5 million by the same plans), plus the plan's candidate scan-row and scan-byte ceilings. These are engine-work bounds: they are charged only while the engine runs the statement, not while the request waits in queues or admission. Enterprise sets no plan time or work ceiling, so an Enterprise query does not fail for age or volume alone; it still stops on the request deadline (5 s), the 5 s transaction deadline, statement, body and result limits, value-length bounds, memory admission, organization rate, concurrency and engine-second admission, and caller cancellation. A query past its time or work budget answers `query_canceled` (`57014`); admission refusals answer `tenant_busy` (`53300`) with `Retry-After` where supplied. Statement, body, row, byte and transaction-growth limits are in the [SQL contract](sql/native-sql.md). Admission, authorization and the SQL sandbox run before customer work executes.

Request/result buffers reserve process and organization memory before allocation. A reservation lasts until the owned buffer or response is released, including cancellation and error paths. Native allocations and physical I/O are not fully measured by managed reservation estimates. See [memory admission](memory.md) and [shared-process tenancy](shared-process-tenancy.md).

Storage work retains tenant ownership through queues and callbacks. Scheduling is bounded and fair across organizations, but cannot promise hard latency isolation for an already-running native call or a stalled device. Cancellation drains the owned reader and operation gate before releasing resources.

## Storage

SQL writes take the organization quota lease before the database writer. The transaction checks net used-page growth and persists SQL storage accounting atomically. Failed statements and rolled-back work cannot publish a larger unchecked database. Branch and restore publication retain the same quota and identity checks through registration or rollback.

Storage accounting, physical file bytes, replica copies and backup retention are distinct quantities. A node-local check is not proof of an atomic global physical-disk quota. Disk work also passes [bulk disk admission](disk-admission.md).

## Responses and monthly caps

Preserve the existing rate, concurrency, memory and storage refusal contract, including `Retry-After` where supplied. Never bypass validation to make a request fit. Enterprise plans still observe statement, transaction and process safety bounds.

SQL reads are free with plan fair use; shape reads are free and unmetered. Committed writes count toward the monthly write allowance and cap. Replication does not add billable units. The published pricing catalog and [operation allowance contract](operation-allowance.md) define the current values; do not duplicate a stale plan table here.
