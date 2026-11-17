# Dashboard MCP

The dashboard owns the platform MCP endpoint. It calls ts-auth for identity and access, TerraBase for SQL databases and recovery, and Stripe for billing facts. It stores its own state in [SQL tables](state-storage.md).

Use the [public MCP guide](https://docs.terrascale.tech/guides/mcp) for connection instructions. Authentication uses the supported OAuth flow or a personal access token. Keep credentials on the server and out of prompts, tool results and logs.

## Discovery and authorization

Discover the current tool set through MCP rather than retaining a handwritten tool inventory. Tool names, input schemas, descriptions and error contracts come from the serving implementation. Rediscover after an upgrade. Unknown tools return the protocol's unknown-tool error; no compatibility alias is implied.

Every action resolves the current identity, organization, project and database, then enforces the operation's permissions. A resource name or tool annotation never grants access. Read-only roles cannot mutate. Database restrictions apply to the resolved immutable database ID, including when the display name was reused.

Treat query results as untrusted data, use the least authority needed, and obtain user consent for consequential mutations. Secrets returned once must be stored securely and never repeated in ordinary tool output. Cancellation preserves ownership and upstream authorization.

## Functions

Call `get_functions_reference` before writing a function: it returns the handler and event contracts, SQL and transaction signatures, limits, cron rules and three complete examples. `deploy_function` creates or replaces source and an optional five-field UTC cron, returning the HTTPS invoke URL or the runner's structured validation error. `invoke_function` runs a JSON body once and returns its HTTP status, content type and parsed body. It needs only SQL read access, even if the function writes SQL. With SQL write access it also returns the newest invocation log; concurrent calls or cron may produce a newer record. Without write access, `log` is null and `log_note` explains how to read logs. If a log read fails after execution, the call retains its HTTP response, with a null log and a failure note; inspect existing logs instead of invoking again. Both MCP and the dashboard test run send an empty `event.query`; direct HTTPS calls populate it from URL query parameters.

`get_function_stats` is read-only and returns aggregate metrics independently of retained logs. Pass `databaseId`, `name` and an optional `window`: `1h`, `24h` (default) or `7d`. Invalid windows return a structured validation error. The result contains `window`, `invocations`, `errors`, `p50Ms`, `p95Ms`, `p99Ms`, `maxMs`, `sqlCalls`, `rowsRead`, `rowsWritten` and oldest-first, zero-filled `buckets`. Each bucket contains `at`, `invocations`, `errors`, `p50Ms` and `p95Ms`.

Project API keys can use the same function tools. Deploy, list, get, logs and stats require SQL read and write scopes; invoke requires SQL read. The reference tool requires no data scope. Database restrictions and project ownership remain enforced. `list_functions`, `get_function`, `list_function_logs` and `get_function_stats` support inspection; `delete_function` retains its dashboard confirmation handoff.

## SQL and database lifecycle

SQL operates on real tables. TerraBase owns SQL validation, read/write scopes, transaction and result bounds, SQLSTATEs, storage quotas and usage accounting. Use parameters for values. A failure is not an empty result; preserve the upstream error.

Database lifecycle tools cover the current create/list/fork/TTL/delete contract. Forks are independent databases and count toward the database allowance. Guard destructive actions with the observed immutable ID. See [agent databases](agent-databases.md) and [SQL editor correctness](sql-editor-correctness.md).

Lists and queries use their returned continuation and bounds. A page can be incomplete without proving absence. Never infer successful mutation from a lost transport response; inspect current state before retrying an uncertain write.

`restore_backup` and `restore_database_point_in_time` start the restore and return at once with the operation (`operationId`, its `status` — `running`, or already `completed` when the copy finished before the answer, never a wait — and the requested target `name`), so one call never blocks for the copy. Poll `get_restore_status` with the source `databaseId` and `operationId`: it reports `running`, the restored database identity and name once `completed`, or the error code and message once `failed`. `restore_database_point_in_time` keeps its confirmName consent check and answers only after the dashboard's live management, provisioning, name and quota validation. Both restore tools keep the restore path's account, role and provisioning authorization; `get_restore_status` requires read access to the source database.

Billing tools follow the same financial and authorization safeguards as the dashboard. Stripe remains the authority for subscription and payment facts; see [Stripe billing](stripe.md). MCP annotations are discovery metadata rather than promises of idempotency or broader permissions.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
