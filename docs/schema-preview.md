`Collection::preview_schema(schema_bytes, cursor)` observes existing documents
without changing documents or installing the proposed validation schema. Send
identical JSON bytes (at most 64 KiB) for each opaque continuation, on the same
process. Both `documents:read` and `documents:write` are required.
Counts describe one bounded page; diagnostics carry hashes and keywords only.
Restart after `preview_stale` or `invalid_cursor`. A successful preview is local
and observational; subsequent writes can stale it. Applying remains prospective.
