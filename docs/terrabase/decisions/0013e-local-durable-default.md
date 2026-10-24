# 0013e: A write answers after its local durable commit; waiting for another region is a per-request opt-in

- **Status: accepted** (user decision Q-WRITE-ACK, 2026-10-23).
- Scope: `Features/Documents` (`DocumentRoute`, `DocumentService.WriteConfirmedAsync`, `TransactionEndpoint`).
- Changes the default of [0013a](0013a-remote-durable-acknowledgement.md): its barrier still holds, but only for a request
  that asks for it. Prompted by TS-927: live writes waited p50 430–700 ms for another region's acknowledgement.

## Decision

1. **Default: `local_durable`.** A document mutation (`PUT`, `PATCH`, `DELETE` by key, `POST …/documents`) or a
   transaction with a put, patch or delete answers once its commit in this region is synced to disk (`synchronous = FULL`,
   decision 0010). Replication to the other placed regions continues in the background through the same peer streams.
   It is not refused because another region is unreachable or because many writes wait for confirmation. A database
   left in its home region alone still answers `409 placement_needs_two_regions` on both paths (decision 0013c,
   single-region writes): that is placement, not the acknowledgement wait.
2. **Opt-in: `TB-Durability: remote_durable`.** The request waits as 0013a describes: it is admitted before it executes,
   waits up to 10 s for an eligible acknowledgement from another placed region, and answers `remote_durable`. Only this
   path answers `503 remote_region_unavailable` or `confirmation_busy` (with Retry-After) before execution, and
   `503 write_outcome_unknown` when no acknowledgement arrived in time. A cell of one answers `local_durable` either way.
3. **`TB-Durability: local_durable`** names the default explicitly. Any other value answers `400 invalid_request`; the
   write does not execute.
4. **An ambiguous local commit** (the commit, or reading its own commit marker, failed at or after the commit) still
   answers `503 write_outcome_unknown` on both paths: the write may have landed, so a client must not replay it blindly.
5. Assertions-only transactions answer `unchanged` without a wait, as before.

## Consequences

- A default write's latency is this region's commit (one FULL sync) plus request handling. It no longer includes the
  round trip to another region, which is at least 140–190 ms between gru, sjc and fra.
- A `local_durable` answer means one durable copy, on the accepting machine. If that machine's disk is lost before its
  change reaches another region, the write is lost. Clients that need the 0013a guarantee send the header.
- Readers in another region see a default write once it replicates (the replication lag), not before the writer's answer.
- Protocol: no peer protocol, storage format or replication change. Old and new nodes in a rolling deploy differ only in
  what a client write waits for, so a mixed cell is safe; an old node ignores the header and waits as before.
- The SDKs (ts-go, ts-dotnet, ts-js, ts-cli) live in their own repositories and need a "wait for another region" option
  that sends the header.
