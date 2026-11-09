---
title: Errors and safe retries
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Errors and safe retries

Decide recovery from the typed status and code, and from whether an operation may have committed. A timeout does not prove a write failed.

`query_canceled` (400, SQLSTATE 57014) has two messages: `query too expensive: it exceeds the work budget` when a query passes the plan's scan or work budget, and `the request exceeded its 5 s time limit; split the batch or narrow the query` when the request's shared 5 s of statement execution runs out. Time spent admitted, queued or waiting on the organization's limits does not count. Narrow the query, add indexes or split the batch before retrying.

## Typed status codes

| Response | Recovery |
| --- | --- |
| `400` / `413` / `415` | Correct the request, payload, size or media type. |
| `401` / `403` | Obtain valid authorized access; do not auto-retry. `403` means the resource is yours but the key lacks a scope (`forbidden_scope`) or the client is outside its IP allowlist (`ip_not_allowed`). |
| `404` | The resource does not exist **or your credential cannot reach it**; both answer the same, so another project's resources stay invisible. For `unknown_database`, check the project ID and database name and the key's project and database restriction. |
| `409` / `412` | Resolve the conflict or the stale version before writing again. |
| `421` | Wrong region: the database is served in another region. See [Wrong region](#wrong-region). |
| `429` / `503` | Respect `Retry-After` where supplied; repeat only safe operations within a deadline. Plan quotas (`quota_exceeded`, `storage_quota`, `operation_allowance`, `read_fair_use`) carry no `Retry-After`: the message names the used and allowed amounts and the next step, so free the quota or upgrade instead of retrying. |

## Wrong region

A `421` with code `wrong_region` means the region you called does not serve the database: only its home region does. The response names the home in the `X-TerraBase-Database-Home` header, its `Location` header carries the same request URL in the home region, and the message reads `the database is served in <region>: send it to https://<host>`. Recover by sending the request to the home region — `ts db info <name>` prints a database's metadata, including its home region. The SDKs do not retry a `421` automatically: the .NET SDK raises `TerraBaseWrongRegionException` and the Go SDK returns an `*tb.APIError`, both carrying `HomeRegion` (and the regions that serve the database), so your code can resend there.

## Retry behavior

A `serialization_conflict` is resent at most twice; nothing else is.

For a repeatable read, wait as directed by the server, bound attempts and honor cancellation. Validation, authorization and conflict failures need a fix before another attempt. Do not hide those failures in an infinite retry loop.

## Uncertain writes

A write whose response was lost may have committed. Read the data that the write would have produced before you repeat it. A conditional write, such as one that fails when a key already exists or when a row has changed, can prevent a duplicate; do not turn a rejected conditional write into an unconditional one.

</div>
