# Document query Explain contract

Producer authority: TerraScale/terrascale
`a3cbf8b1c73d304189a489fa8033d41f3b508de3`, read with `git show`:

- `src/TerraBase/Features/Documents/DocumentRoute.cs`: versioned route and JSON media type.
- `src/TerraBase/Features/Documents/QueryDocumentsEndpoint.cs`: `queryDocuments`,
  `POST /v1/projects/{project}/databases/{database}/collections/{collection}/query`,
  `documents:read` authorization, tenant/collection validation, diagnostics construction.
- `src/TerraBase/Features/Documents/DocumentModels.cs:39-66,126-140`:
  `DocumentPageBody`, `ExplainBody`, `QueryRequest`.
- `src/TerraBase/Features/Documents/QueryParser.cs:59-64,121-124`: optional
  boolean `explain`; null and other types fail `invalid_query`. The SDK omits false.

The existing authenticated JSON query operation accepts `explain: true` to
return diagnostics alongside the normal page and bypass the result cache.
`plan` and `indexes` are nonnullable string arrays; `full_scan` is boolean;
`rows_scanned` and `work_units` are numeric signed 64-bit integers;
`elapsed_ms` is a double. `suggested_index` is an optional string array,
omitted when null, containing btree index fields on a qualifying full scan.
The optional response `explain` object is omitted when unavailable.

Pagination is unchanged: `next_cursor` continues the same filter/sort/projection;
count applies only without a cursor. Explain remains requested on each SDK page.
This read-only operation needs no mutation idempotency key; existing safe-read
retry, API error and context cancellation handling remains unchanged. Errors
include invalid-query/cursor 400, authorization failures, tenant/collection 404,
body/media 413/415 and unavailable 503; diagnostics do not turn errors into success.

No full generated TerraBase OpenAPI/route manifest is committed at this producer
revision. Endpoint/DTO parity was inspected directly; generated/live parity and
before/after-index production qualification remain pending, not inferred from
the SDK's manifest or these fake HTTP tests. No TerraBase binary is required.

When `explain` is present, all six required members must be present and non-null with the documented types. String arrays cannot contain null elements (including `suggested_index`). False, zero counters/timing and empty arrays remain valid. Outer absence/null, optional suggested-index absence/null and unknown fields remain accepted. Invalid diagnostics return a bounded generic error without response values; exact int64 decoding is preserved.
