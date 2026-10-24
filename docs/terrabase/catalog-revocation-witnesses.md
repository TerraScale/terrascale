# Catalog restore and revocation witnesses

A matching per-machine restore anchor proves only that machine's catalog did not roll back. Two catalogs that never received an acknowledged revocation can still match their own anchors. They cannot form a serving majority on that evidence alone.

Before acknowledging a destructive management request, the catalog commits an opaque marker after that request's transaction. The encrypted external `catalog-anchor/<machine>` records its exact replica and committed stamp, alongside the existing incarnation and CDC head. No subject, key secret, email, or document content enters that witness. A failed anchor write leaves the response unacknowledged.

At startup, each gate checks the external witnesses of listed and recorded machines and the bucket's other anchors. Both anchor listing and reads request Tigris consistent reads. A remote witness requires received-prefix coverage through its stamp. Seeing its marker through ordinary bucket anti-entropy, or matching another stale peer's roots, proves nothing about the preceding deletion.

A missing prefix can be recovered by importing every state and the vector from a trusted peer's pinned snapshot in one durable local transaction. An interrupted import certifies nothing; source absence without deletion evidence cannot erase a local survivor. If no source holds the anchored cut, the gate stays closed. HTTP authorization, management recovery, backup shipping, and peer snapshot exports remain refused on an untrusted restored catalog.

The original per-machine incarnation/head check remains in force, including on a locally restored revocation origin. It is not bypassed by a majority or an imported vector. Every serving gate must hold the external revocation cuts before opening; an offline machine can be omitted from the pull quorum only when its external witness is already covered.

The outer encrypted anchor format remains version 1. The additive `revocations: 1` evidence is understood by this build. Anchors written by older builds require pulls from every known holder before a new gate may open; the first upgraded anchor publishes a causal witness. Unknown evidence versions, malformed/oversized objects, missing existing anchors, and unverifiable origins fail closed. During rollout, old software does not provide the new cross-machine guarantee; complete the server rollout before accepting restore proof.

The Fast regressions include the one-restored-origin/two-uninformed-peer case that opened on the previous gate, mixed and all-restored catalogs, a current-origin control that converges, revoked-key denial, and stale catalog access after database deletion. Tenant-state tombstones and collected-state floors remain required independently; this gate does not authorize replay of an old tenant file or discard deletion evidence. The snapshot regression also refuses a source's collected absence when the destination still holds an unproven live row.

Bucket rollback or replay of the external authority itself is outside this proof. A per-machine anchor must remain outside the restored volume. There is no operator bypass.

The existing `DatabasePurgeTests.A_stale_live_catalog_row_cannot_serve_restore_adopt_or_place_a_purged_database` exercises permanent database authority against replayed catalog rows. `AntiEntropyGcTests.Collected_documents_stay_absent_when_two_healthy_replicas_pull_a_stale_peer` covers deleted records before and after restart; the collecting-writer race test covers replay queued across collection. These protections remain separate from startup anchor admission.
