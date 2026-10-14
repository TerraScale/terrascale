# 21. Slice plan: R8 as built

**R8 as built** (2026-10-12, on R3; no storage format change: `_tb_feed` and `_tb_feed_floor` were in the R2a schema):

- **Index in the write transaction, not a tailer pass.** Every `_tb_state` write (a local request's flush and a join that
  changed a document) appends `_tb_feed(origin, stamp, collection, id, deleted)` in the same transaction, and the
  origin's `_tb_feed_floor` row (floor 0) on its first event in the file. So the feed holds exactly the committed
  states, never a state a crash lost, and backups and copies carry it (its rows are captured; the CDC tailer skips
  them). Cost: one more captured row per document write (the floor row only on an origin's first event here).
- **Not in the feed:** metadata documents (`_collections`, `_indexes`, `_unique`); documents wholly covered by a collection
  drop (their states go without events; rewritten survivor states produce local repair events); a uniqueness loser becoming visible when its winner goes (no state
  write); a duplicate or dominated join (it changes nothing). `document` is read when the event is sent: null when the
  document no longer exists on the serving machine (deleted since, dropped, or a hidden uniqueness loser).
- **Reading.** Per origin listed in `_tb_feed_floor`, the rows above the cursor's stamp (index `_tb_feed_order`, at most
  512, completed to the end of the last stamp), merged by (stamp, origin) up to the lowest last stamp of an origin that
  has more, cut only between two stamps at 256 events or 4 MiB of documents. A resumed cursor is 410 `cursor_expired`
  when a named origin's stamp is below its floor, when an origin it lacks has a floor at or above the cursor's issue
  instant minus 7 days (an event retention deleted that the reader never got), or when it was issued more than 7 days
  ago (it could lack an origin forgotten since). Without a cursor, every origin starts at its floor and nothing is
  expired. An origin the cursor lacks starts at its first event. Events the `collections` filter leaves out still
  advance the cursor, which the next event or heartbeat carries, so a cursor is valid only with the same filter. Only
  the last event of an (origin, stamp) carries an `id:` (a stamp holds one document today, as stamps are minted per
  document, but a reader that leaves inside one never skips the rest of it). These rules assume a machine receives an
  origin's events within the retention (otherwise it refills, R6).
- **Opaque cursor.** base64url of a marker, the issue instant, issuing node and Fly machine ID, then per origin in
  ascending order its node, stamp, retention floor and SHA-256 prefix hash. Integers are 8-byte big-endian; the header
  is 18 bytes plus the issuing machine ID (length-prefixed ASCII, at most 63 bytes), and each origin 56 bytes. The hash chains event identities (stamp, deletion bit, length-prefixed UTF-8
  collection and document id), including filtered events. Each `_tb_feed` row persists the running hash through it,
  written in the same transaction as the state and event. The retention floor preserves the last deleted row's hash,
  so the chain continues without rescanning or rewriting retained history. A cross-machine resume compares its
  persisted hash before sending 200; a mismatch or a newer retention floor returns 410. Head cursors and cross-machine
  validation need one indexed prefix lookup per named origin, with bounded memory. Ordered CDC appends extend the
  chain once; an out-of-order join rehashes its successors in bounded pages within the same transaction.
  Every event and heartbeat re-issues the cursor at the instant the read behind it began. Decoding accepts canonical
  bytes only, at most 256 origins; encoding refuses more. Any cursor that does not decode to the current opaque
  layout returns 410 `cursor_expired`; there are no alternate layouts or compatibility branches. No public API or
  schema version changes (`PRAGMA user_version` remains 1), nor changes to replication, authentication or lattice
  conflict semantics. Persisted prefix columns change the internal storage format; greenfield storage must be recreated
  at the reviewed storage cutover, with no migration. The internal format-9 rollout uses
  `ops/fly/greenfield-no-customer-data` to approve exactly `9-f707baac47dc` while only dogfood data exists. CI refuses
  format drift before mutation and never resets volumes. After the entire format-changing main push run completes,
  the reset owner stops auth and all old TerraBase machines, stages every old TerraBase machine on the approved image,
  then runs the guarded manual reset with `--image` through gru → sjc → fra. Main merges and push-run retries stay held;
  before each region, verify no main push CI run is queued or running. After all regions are reset and healthy, retry
  the failed normal deployment to restart auth. Verify the exact format, open catalog gates, both platform databases
  (`ts-auth` and `ts-dashboard`), owner, organization, projects, dashboard client and fresh signing keys in every region.
  **Delete the marker after the complete reset and normal rollout pass, before serving customer data.**
  An issued cursor with no origin is not
  "no cursor": it is expired by the retention rules above.
- **Issuer routing.** A resume on another machine answers 421 `cursor_elsewhere`, with
  `fly-replay: instance=<issuer>;timeout=2s;fallback=force_self`, before any SSE bytes. Fly Proxy replays the original
  URL, headers and credential to the issuer, which resumes on its own history. If that machine is unavailable, the
  documented proxy fallback returns the request to the initial serving machine with `fly-replay-failed`; that machine
  verifies the prefix and answers 200 or 410. Requests already carrying `Fly-Replay-Src` or `fly-replay-failed` never
  issue another replay, preventing loops and respecting Fly's prohibition on replaying a fallback. Outside Fly Proxy,
  the client receives the 421 and header directly. A network timeout or a prior regional replay can therefore still
  invoke conservative prefix validation, even when the issuer has not permanently disappeared.
  Off Fly, the derived machine identity is `local`, so ordinary local resumes do not require a proxy.
  The replay/fallback contract is documented at
  <https://docs.fly.io/networking/dynamic-request-routing#replay-timeout-and-fallback>.
  Fast exercises the routing headers and SSE outcome over real listeners with a test proxy and unchanged database
  identity and credential. This is not a deployed Fly Proxy measurement; pairwise replay latency remains to be
  measured after the deployment hold is lifted.
- **410 frequency and recovery impact.** Matching retained prefixes resume with 200 in all six directed gru/sjc/fra
  pairs in Fast, including collection-filtered events sharing a stamp. This is controlled integration coverage, not a
  measured production error rate. Prefix verification is conservative: a destination behind the consumed prefix,
  different events omitted by dominated joins, or different within-stamp ordering can produce 410 without a repair.
  A newer destination retention floor also expires the proof even if only already-consumed events were trimmed;
  hourly feed retention can therefore invalidate cross-machine resumes of an ongoing consumer's older proof.
  Healthy-issuer replays keep clients on that machine's history; same-machine resumes bypass prefix comparison and
  keep the existing retention checks. The cases below apply when replay cannot reach the issuer or was already used.
  A repair whose join changes no state adds no feed event. A repair that changes a document adds a machine-specific
  event: once a cursor passes it (even via a collection filter or a later local write), another machine normally lacks
  that event and rejects the proof. A cursor issued before repair may also differ from a machine holding the original
  history, as the regression demonstrates. Consequently **every affected client switching away from a repaired
  machine after an outage can receive 410**; this is not limited to one pre-repair cursor. Catch-up through retained
  ordered CDC groups alone does not introduce repair events. Resync recovers state but does not make that machine's
  repair history portable: repeated switches can cause repeated 410 until the incompatible prefix is no longer named,
  or that origin is forgotten after all its events age out. The persisted chain includes deleted history: trimming
  repair rows alone does not reconcile divergent hashes. This trades fewer resume scans for conservative expiry even
  when only the origin's older, already-consumed history differs.
  There is no fixed 410 percentage or guaranteed low frequency: it depends on repair/refill incidence, join histories,
  retention advancement and client region switches. The production rate has not been measured.
- **Stream.** One page at a time, no queue per connection: after a page that left nothing waiting, the stream waits for
  the database's next commit (`IDatabaseStore.WaitForCommitAsync`, joins included) or the 15 s heartbeat (`event:
  cursor`). A client that takes no bytes for 10 s is dropped. Limits: 32 streams per database and 1024 per machine
  (429 `changes_busy`); a stream ends after 30 minutes or at its access token's expiry, and the client reconnects with
  `Last-Event-ID` (so a revoked API key streams at most 30 minutes). Once streaming, a stream gives back its slot in the
  organization's concurrent requests (`QosRequestLease`; Hobby allows 4), and each page's documents are charged to the
  tenant's memory until sent. Errors after the first byte are an `event: error` with the usual error body, then the end
  of the stream. TerraBase's error body is `{"error": {"code", "message"}}` on every route, not RFC 9457 problem
  details; the feed keeps it.
- **Retention.** `FeedRetention` deletes events stamped more than 7 days ago every hour, 1024 at a time with capture
  suspended (a restore keeps more history, never less), and raises each origin's floor to the highest stamp deleted.
  It then forgets origins with no event left whose floor is older than 14 days (`FeedCursor.Forget`), so the per-read
  origin list and the cursors stay bounded; a resumed cursor drops an origin this machine forgot when its stamp is older
  than its issue instant minus 14 days. A failing pass logs a warning. Refill floors are R6's.
- **Repairs.** An anti-entropy repair joins with origin 0 (R6), which no cursor can name. Its feed row is filed as this
  machine's event at a fresh stamp from its clock, so this machine's events stay in commit order; repairs never ship,
  so every machine files its own. Section 14's "one row per original (node, stamp)" is not built: a reader sees the
  repaired document (its current value) under the repairing machine. Prefix proofs close the cross-region gap:
  if a reader gets an origin's later event before a repaired earlier event, another machine holding the original
  earlier event rejects that cursor with 410 rather than silently skipping it. This also covers cursors issued before
  the repair happens; invalidating only completed repairs would not suffice.
- **Future stamps.** Receiving a stamp remains capped at `MaxSkew` (1 s), counted by `terrabase.clock.skew_capped`.
  A local write that must exceed a future document stamp retains that mint without a cap, so later local writes to
  other documents advance past it and a feed cursor cannot skip them because their stamps went backwards.
- Metrics: `terrabase.changes.streams` (open streams).
- Tests: Fast `DatabaseChangesTests` (events, `cursor=now`, `Last-Event-ID`, filter, joined remote writes under their
  origin, 410 at retention, limits, heartbeat, errors); e2e `ChangesFeedE2ETests` (gru and sjc write, a cursor from
  sjc's feed resumes on fra after sjc stops or is SIGKILLed, in-process and as separate OS processes) and E10.

R2a is built on the Microsoft.Data.Sqlite engine and writes only SQL that Turso accepts (rowid tables for every new
table, plain `INSERT`/`UPDATE`/`DELETE` on captured tables). The engine swap (1b, PR #21) lands separately: on Turso,
174 Fast tests fail, most of them in continuous backup, which reads SQLite's WAL directly.
