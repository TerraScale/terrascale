---
title: Errors and safe retries
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Errors and safe retries

Decide recovery from the typed status and code, and from whether an operation may have committed. A timeout does not prove a write failed.

`query_canceled` (400, SQLSTATE 57014) has two messages: `query too expensive: it exceeds the work budget` when a query passes the plan's scan or work budget, and `the request exceeded its 5 s time limit; split the batch or narrow the query` when the request's shared 5 s of statement and commit execution runs out. Time spent admitted, queued or waiting on the organization's limits does not count. Narrow the query, add indexes or split the batch before retrying.

## Typed status codes

| Response | Recovery |
| --- | --- |
| `400` / `413` / `415` | Correct the request, payload, size or media type. |
| `401` / `403` | Obtain valid authorized access; do not auto-retry. `403` means the resource is yours but the key lacks a scope (`forbidden_scope`) or the client is outside its IP allowlist (`ip_not_allowed`). |
| `404` | The resource does not exist **or your credential cannot reach it**; both answer the same, so another project's resources stay invisible. For `unknown_database`, check the project ID and database name and the key's project and database restriction. |
| `409` / `412` | Resolve the conflict or the stale version before writing again. |
| `421` | Wrong region: the database is served in another region. See [Wrong region](#wrong-region). |
| `429` / `503` | Respect `Retry-After` where supplied; repeat only safe operations within a deadline. Plan quotas (`quota_exceeded`, `storage_quota`, `operation_allowance`, `read_fair_use`) carry no `Retry-After`: the message names the used and allowed amounts and the next step, so free the quota or upgrade instead of retrying. |

Every error body carries a `requestId` (`{"error":{"code":...,"requestId":"..."}}`) and every response, success or error, carries the same id in its `X-Request-ID` header. Quote it to support: server logs record it as `request_id` on every log line of that request. An `unknown_route` error (404) names the method and path and points to the API reference at `/scalar/`. Error frames on the SQL WebSocket carry the same two fields: the `requestId` of the socket's upgrade request (one per socket) and the `docsUrl` of the error's `code`.

## Error codes

Every error body carries a `docsUrl` pointing at the entry below for its `code`. Retry means resending the same request: `Yes` after the stated fix or wait, `No` when the request must change first.

### `access_denied`

403 — the bucket access rules deny this operation. Change the rules or use an allowed operation. Retry: no.

### `auth_unavailable`

503 — the key-authorization service could not be reached to check the credential. Wait and resend. Retry: yes.

### `backup_history_incomplete`

409 — continuous backup cannot prove this database's history complete up to pointInTime. Choose an earlier point. Retry: no.

### `backup_inventory_limit`

503 — the backup inventory scan exceeded its execution budget. Retry later or narrow the request. Retry: yes.

### `backup_region_not_placed`

409 — the snapshot's region is outside the database's current placement. Add the region to the placement or restore from a placed snapshot. Retry: no.

### `backup_region_unknown`

503 — the snapshot's region is not one this deployment serves. Restore from a snapshot in a served region. Retry: no.

### `backup_unreadable`

409 — a backup object failed authentication or its integrity check. Contact support with the `requestId`. Retry: no.

### `binding_conflict`

409 — this project id is bound to another organization. Resolve the binding first. Retry: no.

### `capacity`

503 — an internal budget for this kind of work is exhausted right now. Retry later. Retry: yes.

### `catalog_anchor_unavailable`

503 — the change committed locally, but its restore anchor could not be written in time. Retry the request. Retry: yes.

### `catalog_stale`

503 — this machine cannot confirm current credentials. Retry later. Retry: yes.

### `catalog_unverified`

503 — this machine's catalog has not passed the restore gate. Wait for the node to become ready. Retry: yes.

### `check_violation`

400, SQLSTATE 23514 — a row broke a `CHECK` constraint. Fix the values or the constraint. Retry: no.

### `closure_conflict`

409 — the project is already closing under another closure id or organization. Use the running closure. Retry: no.

### `column_not_granted`

403 — the read grant does not include a column the query names. Extend the grant or drop the column. Retry: no.

### `conflict`

409 — the request conflicts with the current state, such as a name that is already taken. Resolve the conflict before sending again. Retry: no.

### `constraint_violation`

400, SQLSTATE 23000 — a statement broke a constraint that has no more specific code. Fix the statement. Retry: no.

### `copy_faulted`

503 — the database's storage copy faulted and requires recovery. Contact support with the `requestId`. Retry: no.

### `database_deleted`

409 — a database deleted here is not brought back. Create a new database. Retry: no.

### `database_identity_changed`

409 — the database changed between your read and your conditional write. Re-read and resend. Retry: yes.

### `database_identity_mismatch`

409 — the database no longer has the identity your conditional delete expects. Re-read the database. Retry: yes.

### `database_shredded`

409 — this database was erased by retention and cannot be restored. There is nothing to restore. Retry: no.

### `disk_capacity`

503 — the node is out of disk space. Wait for capacity or contact support. Retry: yes, after `Retry-After`.

### `duplicate_object`

400, SQLSTATE 42P07 — a schema object of that name already exists. Choose another name or drop the existing object. Retry: no.

### `forbidden`

403 — the credential may not do this. Use a credential with the required access. Retry: no.

### `forbidden_scope`

403, SQLSTATE 42501 — the key lacks the scope the operation needs. Use a key with the scope. Retry: no.

### `foreign_key_violation`

400, SQLSTATE 23503 — a statement broke a foreign key constraint. Insert the parent row or fix the reference. Retry: no.

### `function_error`

500 — the invoked function's JavaScript handler threw, exceeded its runtime limits or a SQL call it made failed. The message carries the handler's error and the run, with its logged lines, is kept in the function's logs; SQL the handler committed outside a transaction stays committed. Fix the function source and invoke again. Retry: no.

### `function_limit`

409 — the plan's function count limit is reached. Delete a function or upgrade the plan. Retry: no.

### `gone`

410 — what the request resumes from is gone for good, such as a cursor below retention. Start over from the beginning. Retry: no.

### `grant_invalid`

403 — the read grant does not match the table's current schema. Update the grant. Retry: no.

### `id_taken`

409 — the id is already taken. Choose another id. Retry: no.

### `internal_error`

500, SQLSTATE XX000 — an unexpected server fault. Quote the `requestId` to support. Retry: yes, once.

### `invalid_cursor`

400 — `after` is not one canonical uppercase ULID. Send a cursor from a previous page. Retry: no.

### `invalid_key`

401 — the credential is missing, unknown or expired. Use a valid API key or token. Retry: no.

### `invalid_query`

400 — the analytics query is too expensive or its parameters are out of range. Narrow the query or fix the parameters. Retry: no.

### `invalid_region_pin`

421 — `X-TerraBase-Region-Pin` does not hold exactly one region ID. Send one of the serving regions. Retry: yes.

### `invalid_request`

400, SQLSTATE 22023 — the request is malformed or breaks an input rule. Correct the request. Retry: no.

### `invalid_rules`

400 — the bucket access rules are not a valid owner pointer with bounded roles. Fix the rules. Retry: no.

### `invalid_status`

400 — `status` must be one of `2xx`, `3xx`, `4xx` or `5xx`. Fix the filter. Retry: no.

### `invalid_token`

401 — the end-user token was refused by the authorization service. Use a fresh, valid token. Retry: no.

### `ip_not_allowed`

403 — the client's address is outside the key's IP allowlist. Call from an allowed address. Retry: no.

### `key_expired`

401 — the API key has expired. Create a new key. Retry: no.

### `key_revoked`

401 — the API key was revoked or rotated, or its project was deleted. Use a current key. Retry: no.

### `method_not_allowed`

404 — the route exists but not for this method. Use the documented method. Retry: no.

### `misdirected_request`

421 — the request reached a listener that does not serve it. Send it to the documented endpoint. Retry: yes.

### `move_aborted`

409 — the database's home move this operation depended on was abandoned. Re-check the database's placement. Retry: yes.

### `move_in_progress`

409 — another move of this database's home is running. Wait for it to finish. Retry: yes.

### `move_unresolved`

503 — the database's home region is moving and its placement is not yet known. Retry after `Retry-After`. Retry: yes.

### `must_refetch`

409 — the cached copy is stale beyond its retention. Refetch from the source. Retry: no.

### `name_taken`

409 — the name is already taken. Choose another name. Retry: no.

### `no_read_grant`

404 — the table has no read grant. Create one first. Retry: no.

### `not_empty`

409 — the resource still contains something. Delete what it contains first. Retry: no.

### `not_found`

404 — the addressed resource does not exist or is hidden from the caller. Check the identifier and the credential's reach. Retry: no.

### `not_granted`

403 — this token has no read grant for the table. Create a read grant or use another token. Retry: no.

### `not_null_violation`

400, SQLSTATE 23502 — a `NOT NULL` column received no value. Supply the value. Retry: no.

### `operation_allowance`

429 — the plan's operation allowance is used up. Free the allowance or upgrade. Retry: no.

### `payload_too_large`

413 — the request body is larger than the route accepts. Send a smaller body. Retry: no.

### `pitr_plan_limit`

503 — the point-in-time metadata scan exceeds its object limit. Narrow the restore window. Retry: no.

### `pitr_plan_timeout`

503 — the point-in-time plan metadata scan timed out. Retry later. Retry: yes.

### `pitr_plan_unavailable`

503 — the point-in-time plan metadata could not be read. Contact support with the `requestId`. Retry: no.

### `plan_unknown`

503 — the organization's plan has not been published in this region yet. Retry shortly. Retry: yes.

### `plan_unpublished`

503 — the plan is not published in every region yet. Retry shortly. Retry: yes.

### `platform_database_taken`

409 — a platform database with that name already exists. Choose another name. Retry: no.

### `platform_organization_taken`

409 — a platform organization with that name already exists. Choose another name. Retry: no.

### `platform_placement_locked`

409 — platform databases are placed in every region and their placement cannot change. The request cannot be served. Retry: no.

### `platform_project_taken`

409 — a platform project with that name already exists. Choose another name. Retry: no.

### `precondition_failed`

412 — a precondition such as a version check no longer holds. Re-read the resource and resend. Retry: yes.

### `primary_key_violation`

400, SQLSTATE 23505 — a row with the same primary key already exists. Use a new key or update the existing row. Retry: no.

### `query_canceled`

400, SQLSTATE 57014 — the query exceeded its work budget or the request's 5 s time limit. Narrow the query, add indexes or split the batch. Retry: yes.

### `quota_exceeded`

429, SQLSTATE 53100 — a plan limit is exhausted. Free the quota or upgrade the plan. Retry: no.

### `read_fair_use`

429 — the fair-use read limit is exhausted for the period. Wait for the period to roll over. Retry: yes, later.

### `read_only_transaction`

400, SQLSTATE 25006 — the transaction is read-only and received a write. Open a writable transaction. Retry: no.

### `recovery_erasure_fenced`

409 — platform database historical recovery is fenced by erasure requirements. Recovery is not available for this database. Retry: no.

### `region_pin_mismatch`

421 — the pinned region does not match the request's host or does not serve the database. Pin a serving region or drop the pin. Retry: yes.

### `region_unavailable`

503 — the region required for this request has no machine to answer. Retry later or use another region. Retry: yes.

### `replica_not_ready`

503 — the target region's replica could not catch up; nothing changed. Retry the operation. Retry: yes.

### `request_timeout`

408 — the request body was not received before its deadline. Resend the complete request. Retry: yes.

### `restore_point_unproven`

409 — pointInTime falls between two transactions and cannot be proven. Choose a proven point. Retry: no.

### `row_conflict`

409, SQLSTATE P0001 — a `terrascale_expect_changes(changes(), n)` guard found that the previous statement did not change `n` rows: the row was changed or deleted since you read it. The request rolled back. Reload the row and decide again. Retry: no.

### `serialization_conflict`

409, SQLSTATE 40001 — the transaction's write set conflicted with a concurrent one. Rerun the transaction; SDKs resend it at most twice. Retry: yes.

### `shape_busy`

429 — shape reads are at capacity. Retry later. Retry: yes.

### `shape_too_large`

503 — the shape's result exceeds its size limit. Narrow the shape's filter. Retry: no.

### `sql_error`

400, SQLSTATE 0A000 — a statement failed without a more specific code. Fix the statement. Retry: no.

### `sql_sandbox`

403, SQLSTATE 42501 — the statement is outside the SQL the sandbox allows. Use an allowed statement. Retry: no.

### `storage_accounting_busy`

503 — storage accounting is at capacity. Retry later. Retry: yes.

### `storage_busy`

503 — storage is busy right now. Retry after `Retry-After`. Retry: yes.

### `storage_quota`

429 — the plan's storage quota is used. Free storage or upgrade the plan. Retry: no.

### `syntax_error`

400, SQLSTATE 42601 — the statement is not valid SQL. Fix the syntax. Retry: no.

### `tenant_busy`

429, SQLSTATE 53300 — the database already holds its limit of concurrent work. Retry after `Retry-After`. Retry: yes.

### `tenant_memory`

429 — the organization's memory budget is exceeded. Retry after `Retry-After`. Retry: yes.

### `tls_required`

403 — plaintext client requests are refused at non-edge listeners. Use HTTPS on the public host. Retry: yes.

### `too_large`

413, SQLSTATE 54000 — a statement's data exceeds the size limit. Send smaller data. Retry: no.

### `transaction_aborted`

400, SQLSTATE 25P02 — the transaction was aborted by an earlier failed statement. Rerun the transaction from its start. Retry: yes.

### `transaction_rolled_back`

400, SQLSTATE 40000 — the transaction was rolled back. Rerun it. Retry: yes.

### `transaction_state`

400, SQLSTATE 25000 — the batch's durability or transaction-state setting is invalid for this transaction. Fix the setting. Retry: no.

### `unavailable`

503, SQLSTATE 57P03 — the service cannot serve the request now. Retry after `Retry-After`. Retry: yes.

### `unauthorized`

401, SQLSTATE 28000 — the caller is not authenticated. Authenticate and resend. Retry: no.

### `undefined_column`

400, SQLSTATE 42703 — the statement names a column that does not exist. Fix the column name. Retry: no.

### `undefined_table`

400, SQLSTATE 42P01 — the statement names a table that does not exist. Fix the table name. Retry: no.

### `unique_violation`

400, SQLSTATE 23505 — a unique constraint would be duplicated. Use a new value or update the existing row. Retry: no.

### `unknown_column`

400 — `columns` names a column the table does not have or names one twice. Fix the column list. Retry: no.

### `unknown_database`

404 — the database does not exist or the credential cannot reach it. Check the project and database name and the key's restrictions. Retry: no.

### `unknown_route`

404 — no route matches the method and path; the API reference is at `/scalar/`. Use a documented route. Retry: no.

### `unknown_table`

404 — the table does not exist in this database. Check the table name. Retry: no.

### `unsafe_where`

400 — the shape's where clause is not one shapes accept. Simplify the where clause. Retry: no.

### `unsupported_media_type`

415 — the body's media type or encoding is not one the route reads. Send the documented media type. Retry: no.

### `unsupported_pull`

400 — the pull request is not one this endpoint accepts. Fix the pull parameters. Retry: no.

### `unsupported_table`

400 — shapes read ordinary rowid tables and this table is not one. Query it with SQL. Retry: no.

### `websocket_required`

400 — the endpoint requires a WebSocket upgrade. Connect with WebSocket. Retry: no.

### `write_outcome_unknown`

503 — a write committed or may have committed: its durable copy in another region was not confirmed in time, or the region you called forwarded it to the database's writer and got no answer. Read what the write would have produced before repeating it. Retry: not blindly.

### `wrong_region`

421, SQLSTATE 08004 — the region you called does not serve the database, or could not get the request to its writer; nothing was written. Resend to the region named in the response. Retry: yes.

## Wrong region

A `421` with code `wrong_region` means the region you called does not serve the database: only its home region does. The response names the home in the `X-TerraBase-Database-Home` header, its `Location` header carries the same request URL in the home region, and the message reads `the database is served in <region>: send it to https://<host>`. Recover by sending the request to the home region — `ts db info <name>` prints a database's metadata, including its home region. The SDKs do not retry a `421` automatically: the .NET SDK raises `TerraBaseWrongRegionException` and the Go SDK returns an `*tb.APIError`, both carrying `HomeRegion` (and the regions that serve the database), so your code can resend there.

## Retry behavior

A `serialization_conflict` is resent at most twice; nothing else is.

For a repeatable read, wait as directed by the server, bound attempts and honor cancellation. Validation, authorization and conflict failures need a fix before another attempt. Do not hide those failures in an infinite retry loop.

## Uncertain writes

A write whose response was lost may have committed. Read the data that the write would have produced before you repeat it. A conditional write, such as one that fails when a key already exists or when a row has changed, can prevent a duplicate; do not turn a rejected conditional write into an unconditional one.

</div>
