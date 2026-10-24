# Erasure recovery after a replica epoch change

This extends [shared-history erasure](history-erasure.md) for TS-875. An epoch change is still `epoch_stalled` until there is evidence for the old acknowledged tail. A newer epoch and equal Merkle roots provide no such evidence.

An ACK from a newer epoch retires the old outgoing cursor while retaining the old epoch's received `applied` frontier. These retained witnesses do not claim that the restarted machine retained its own old writes.

After observing a new epoch, every holder commits a new auth marker and publishes its actual auth and SYSTEM applied vectors for the same current replica view. A receipt written before the crash cannot stand in for this barrier: the only surviving ACKed tail may be on a third holder. Another crash changes the view and repeats the barrier.

A holder below an old cut reads every state and the applied vector from one pinned native transaction on a trusted peer. The authenticated internal `ReadSnapshot` RPC sends bounded ordered pages and a terminal vector. The receiver joins the baseline, handles collected tombstones, and records only the requested obsolete origins in one durable transaction after clean stream EOF. Interrupted streams, failed trailers, replaced destination files, and malformed pages certify nothing. Source and receiver transactions have a ten-minute transport deadline; a large import can hold the receiver's writer for that duration and can be refused by memory admission.

A retained source survivor can be older than the receiver's collected-state floor. Baseline import preserves it. Source absence cannot erase a live local row: if a collected gap lacks deletion evidence, the import rolls back and the erasure stays pending. No GC floor override or roots-only override is available.

After actual coverage is imported, the holder fences again. Every holder must cover the refreshed vectors before the requester gets a new settlement token. Auth repeats its scoped deletion verification, then presents that token for a fresh final cut. Existing rewrite, residue, backup inventory, bucket-snapshot, and retirement barriers still apply.

While a listed witness is offline, a legacy receipt lacks its original cuts, deletion evidence is missing, or a peer cannot supply a certified snapshot, the erasure stays pending without a token or completion claim. Witness selection rotates durably so one unavailable peer cannot starve another. The existing `epoch_stalled` alert fires within 15 minutes; persistent stalls require escalation to engineering. There is no operator re-cut endpoint or command.

The per-file snapshot and origin vectors are bounded, including a maximum of 1024 origins. Exceeding that bound refuses certification. Old builds that lack the snapshot RPC cannot provide recovery proofs; a rolling update keeps affected erasures pending until capable witnesses are available. This adds an internal RPC and optional receipt fields, without a storage schema change.
