# 14. Changes feed

`GET /v1/projects/{p}/databases/{d}/changes?cursor=<c>[&collections=a,b]`, served as SSE with the `documents:read`
scope. The per-collection `…/changes`, `/shape` and `/subscribe` routes and their stores are deleted (plan
decision).

- **Event:** `id: <cursor after this event>`, `event: change`,
  `data: {"collection", "id", "op": "upsert"|"delete", "version", "origin", "document"}`. `document` is the current
  materialized value on the serving machine. `event: cursor` is a heartbeat every 15 s with the current cursor.
  `Last-Event-ID` resumes.
- **Cursor** = an opaque base64url vector `{origin → highest stamp delivered}` over the origins in the serving
  machine's `_tb_feed`, plus the instant it was issued. Origins whose events all aged out of retention drop out of the
  vector (R8 as built: after twice the retention).
- **Valid on any machine.** Each origin's events reach every replica in stamp order, on one ordered stream (sections 3
  and 9), and the tailer indexes them in apply order. So `cursor[o] = t` means "every event of `o` up to `t` was
  delivered". Any machine can resume by emitting, for each origin, `_tb_feed` rows with `stamp > cursor[o]`, merged
  across origins by `(stamp, origin)`.
  - An origin missing from the cursor is emitted from its first retained event. This is at-least-once delivery, with
    the dedupe key `(origin, stamp, collection, id)`.
  - When `cursor[o] < _tb_feed_floor[o]` (retention, or a refill on this machine), the answer is 410 `cursor_expired`.
    So is an origin missing from the cursor whose floor is at or above the cursor's issue instant minus the retention:
    retention deleted an event of it that the cursor's reader never got and that was still retained when the cursor was
    issued (a replaced machine or a new epoch that wrote after it). A request without a cursor promises nothing about
    deleted events and starts at what retention still holds.
- **Start.** `cursor=now` returns the serving machine's current vector. A client takes it, lists or queries, then
  tails. Events that overlap the list are harmless.
- **Repairs** (origin 0) write `_tb_feed` rows in their apply transaction, one per distinct `(node, stamp)` among the
  entries they changed, under that original identity. A machine's stream from an origin has no holes: it is applied
  in order, and an ack follows a `FULL` commit. So a repair only fills events above this machine's `applied[origin]`.
  Example: an origin crashed after shipping to one peer but not another, and the second peer gets those events by
  repair. Consumers never passed those events. The only case where the stream does have a hole is a refill, and a
  refill raises the floors (section 10).
