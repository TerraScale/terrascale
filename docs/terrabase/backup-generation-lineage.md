# Backup generations created before erasure receipts

A clean generation can start immediately after a healthy rewrite and before its receipt is committed. Its ULID and initial snapshot timestamps then predate the receipt. Time ordering alone safely leaves that erasure at `backup`, potentially forever.

Each new generation now stores an encrypted `generations/<generation>` certificate before uploading its initial snapshot. The certificate binds the generation, initial snapshot key, physical file incarnation, and commit offset read from the snapshot's pinned native transaction. It contains infrastructure identifiers only. Backup taps terminate on a rewrite, so that generation's later snapshots and WAL cannot span into another physical file.

Erasure inventory retains the existing timestamp check. A generation that predates the receipt instead needs an authenticated certificate whose target incarnation matches a completed clean rewrite receipt and whose pinned offset covers that rewrite's offset. Auth uses the residue rewrite; SYSTEM uses its completed rewrite. A certificate for another file, insufficient offset, changed object key, missing proof, invalid format, or failed authentication cannot qualify the generation. Missing causal evidence stays fail-closed; timestamp comparisons are not rounded or relaxed.

This changes only generation admission. Noncurrent versions, delete markers, incomplete uploads, every other old snapshot/WAL generation, the clean-inventory cut, bucket snapshots covering that cut, all-holder receipts, and retirement remain barriers to completion. The existing day-20 alert, with its 15-minute hold, covers any persistent safe stall.

New native snapshots return their pinned incarnation and offset to continuous backup. Engines or older generations without that evidence retain the timestamp path. A later file replacement without a matching receipt does not inherit a certificate from another incarnation. Generation metadata is protected with the same project/system backup key and object-key binding as the snapshot.

Retention removes an expired certificate only after complete snapshot and WAL inventories show no remaining object of its generation. Retention locks or malformed inventories preserve it. Cancellation before the initial snapshot upload can leave only the certificate; it contains no snapshot or WAL payload and expires through the same retention check.
