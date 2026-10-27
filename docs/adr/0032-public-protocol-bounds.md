# Native public protocol bounds

Status: Accepted from the current producer contract; runtime qualification required.

Native documents are bounded to 1 MiB, nesting depth 16 and a shared 4,096-value
node budget. Field IDs are positive u32 integers, fields and writes are strictly
ordered, and integer quantities use exact bigint/decimal representations.
Transport request and response bodies are bounded to 4 MiB. Query and snapshot
pages admit at most 128 rows. Finite poll groups preserve complete transaction
cuts, including empty projected groups. Opaque cursors remain operation-bound.

The accepted native codec and actual producer vectors are authoritative for
operation-specific bounds. Schema helpers apply the same aggregate document
budget. Identity discovery has its separate 64 KiB document limit. Deadlines
cover credentials, discovery, dispatch and response consumption. Mutations are
never automatically replayed; replay-safe reads retry transient refusals within
their fixed deadline. Invalid media, malformed values, cancellation,
and uncertain outcomes must remain observable in installed consumers.
