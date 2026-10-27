`Database::stream_export::<Box<RawValue>>(after).await?` yields complete NDJSON
export documents as they arrive. Pin the stream and call `StreamExt::next`;
dropping it cancels I/O. Total export size is unbounded; the existing response
limit bounds each line and refusal body. An interrupted HTTP body, incomplete
line or malformed document yields one terminal error after any complete items.
Keep the last consumed `(collection, id)` and explicitly pass it to a new stream
to resume; that starts a new snapshot. No transport replay, redirect following
or regional failover occurs. API-key authentication retains its single 401 refresh.
