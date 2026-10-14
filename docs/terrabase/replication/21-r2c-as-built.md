# 21. Slice plan: R2c as built

**R2c as built** (2026-10-12), in two PRs:

1. R2c-1 deletes shape reads and SSE subscriptions: the `GET …/collections/{c}/shape` and `…/subscribe` routes,
   `IShapeService`, `IShapeSubscriptionService`, the `shapes:read` scope on the TerraBase side, the shape headers,
   offsets and models, `UriTooLong` (414) and the `must_refetch` handle, the engine's snapshot and change-stream reads
   (`SnapshotAsync`, `ChangesAsync`, `CompileSnapshot`, `CompileChanges`), and `shape-reads.md` and
   `subscriptions.md`. It leaves the on-disk format unchanged. ts-auth stopped issuing `shapes:read` afterwards; TerraBase
   ignores scopes it does not know.
2. R2c-2 deletes the per-collection polling feed: `GET …/collections/{c}/changes`, `IChangeFeedService`,
   `CollectionChangePage` and `ReadCollectionChangesAsync`, and the storage that only served the feed and shapes:
   `change_log` (with every write's history append and the drop's history rewrite), `shape_history`,
   the collections' `generation` column (T1 had already removed `change_log_births`, `change_log_high`, their trigger
   and `change_log_by_document`) and `change-feeds.md`. This is an on-disk format change. `collections` and `commit_offset`
   stay: collection metadata and the commit offset of write responses still read them, so both stay reserved
   collection names.

Kept: `StampingStorageEngine`, which still drives the document read cache's invalidation (`IWriteStamps`), and the
preconditions (`If-Match`, `If-None-Match: *`, transaction `assert`), which ts-auth's stores depend on through
ts-dotnet until R12 makes ts-auth lattice-native.
