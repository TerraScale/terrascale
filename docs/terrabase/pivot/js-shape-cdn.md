# JavaScript SQL shape CDN reads

The current live subscription, sharing and hint behavior is documented in
[live shape reads](../free-reads-live.md); it supersedes the origin fan-out behavior below.

The JS SDK's `db.shape(table, { where, columns }).subscribe()` uses the authenticated
bootstrap redirect to obtain the handle and first snapshot offset. It requests
`shape/manifest` for snapshot and catch-up offsets and fetches the listed sealed
objects without credentials or redirects. The filter accompanies every origin
request, including the manifest and epoch-key release.

Before applying a sealed page, the client verifies the manifest's handle, binding,
expiry, offset, size and SHA-256, the TBS1 header's epoch and key ID, every AES-GCM
frame and the decrypted page's handle and continuation. Keys are nonextractable
and scoped to one subscription; their cache and the manifest queue clear at each
ten-minute epoch boundary or refetch. The queue holds at most eight objects.

The authenticated origin serves the live tail and any offset whose mirror is
missing, unavailable or invalid. Authorization failures and `must_refetch` from
manifest/key release remain authoritative. An expired or changed handle starts a
fresh snapshot. A browser that hides a cross-origin manual bootstrap redirect
must follow it and receive its first snapshot page from the origin; subsequent
sealed snapshot and catch-up pages still use the CDN. That bootstrap limitation
requires producer/browser qualification before claiming every browser snapshot
payload is offloaded.

CDN payload offload does not imply constant total origin requests: each independent
reader still bootstraps, reads manifests, requests keys and holds a live-tail
request. The F10 benchmark must count those separately from immutable payload
requests. Shape usage meters and infrastructure request counts are separate.

The .NET, Go, Python and Rust SDKs have no SQL table-shape subscription client at
this revision. They need both the shape protocol and sealed-object reads.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
