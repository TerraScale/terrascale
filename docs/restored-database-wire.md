`RestoredDatabase.placement_version` remains an `i64`. Restore receipts accept
an exact JSON integer or canonical decimal string within the signed i64 range.
Floats, booleans, null, overflow, whitespace, plus signs and leading zeros fail;
errors do not echo input. The production string pattern permits `-0`.
Optional branch/home/replica fields retain absent or explicit null as `None`.
Populated replicas remain intact. Restore status and uncertainty handling are unchanged.
