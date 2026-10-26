# Import lifecycle

Use `DatabaseImports(client.database(name))` from `terrascale.imports`; for asyncio,
use `AsyncDatabaseImports(async_client.database(name))` and await the same methods.
Both handles reuse customer authentication. Root wrappers and live qualification
remain separate work. Export streaming is outside this chunk.

Create a new empty destination with the database API first. Construct
`ImportRequest("ndjson", source_size, source_sha256, ImportOptions(collection="people"))`
and pass it to `create_import`. Formats are `postgres_plain`, `sqlite` and `ndjson`;
the declared file size is 1 byte–1 GiB, and SHA-256 is lowercase hexadecimal.
Then upload the original bytes with `upload_import_source(offset, part)`.
Each part must contain 1 byte–4 MiB; offset is the job's `source.receivedBytes`.
After an uncertain upload, read the job before deciding which bytes to send next.

`get_import` returns a typed dictionary with the server's field names, preserving
nested preview, progress, validation and error details. Contract integers remain
`int | str`; use `int(job["source"]["receivedBytes"])` when calculating an offset.
Inspect the preview before calling `start_import(confirmed_preview_id)` explicitly.
`update_import(ImportOptions(exclude=["public.audit"]))` reanalyzes a ready/rejected
job and produces a new preview requiring confirmation. `cancel_import` keeps
already written documents. `resume_import` may require the same source uploaded
again, or a newly confirmed preview if copying never started. The API requires
the destination's home region (421 otherwise). Mutations are never replayed after
uncertainty; reconcile the job state before retrying. No automatic polling occurs.
