# Sharding glossary migration note

The 2026-08-26 single-owner Shard proposal is superseded by ADR 0019.
Current terms are already incorporated into `../../CONTEXT.md`:

- Bucket;
- Shard Key;
- Logical Partition;
- Partition Split;
- Request Coordinator; and
- replica-set and causal identities.

Do not restore the former definitions of Shard as an owned range, Member as a
range owner, or migration as a leader ownership transfer.
