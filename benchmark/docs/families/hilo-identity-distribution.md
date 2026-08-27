# HiLo identity-distribution decision record (GAP-F09, ticket #77)

Status: **proposed** — written before the module, per the test-first rule;
ratification belongs to the parent at integration. Direction was already
decided: GAP-D01 ownership class 12 names HiLo block distribution as this
ticket's resolution direction for the concurrent-session identity allocation
window (`docs/sessions/gapd01-ownership-decision.md:65`). This record owns the
distribution contract itself: the block hand-out discipline, the
member-loss recovery rule, the reclaim rejection, and the capacity math.
Every V1 citation below was resolved against the pinned sweep commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee` (read-only worktree; the same
pin `docs/parity/ps-01-documents-sweep.md:16-17` uses). No parity row
status changes here.

Owner: GAP-F09 (#77). The implementation lands in `src/families/hilo/`
with the member-loss proofs on the replication lane
(`src/replication/hilo_tests.rs`), the same split GAP-F07 used for its
merge encoding.

## Problem

GAP-D01 class 12 already gave V2 save-time identity allocation: a store
without an explicit id allocates from the engine's identity family inside
the save transaction (`identities_read`/`identities_stage`,
`src/families/store.rs:670-712`; the session path,
`src/session/mod.rs:1169-1198`), so a crashed save leaks no ids. That path
costs one engine transaction per save. V1 additionally ships a
**distribution** layer on top of the same idea: clients reserve whole
blocks (ranges) of ids and mint locally from the reserved range, so the
round trip happens once per block, not once per id. SDKs and hosts that
batch writes need exactly that layer, and GAP-D01 class 12 left it to this
ticket ("Cross-client HiLo block distribution stays with GAP-F9").

The hard part is not the happy path. Identity records are non-merging
family records: GAP-F07's carve-out merges counter records only
(`src/replication/member.rs:1334-1338` — `is_counter_record` is the only
merge branch), so an identity record competes under ADR-0004 D-3
whole-record last-writer-wins like any document. Two members allocating
from the same identity concurrently would converge by discarding one
side's allocation — and a discarded allocation whose ids were already
handed to a caller is a duplicate-id bug no later repair can see. That is
the concurrent-session identity allocation window the decision record
names. The contract below closes it with ownership fencing plus a
hand-out discipline, not with a merge.

## V1's HiLo, at the pinned commit

V1's implementation is client-driven against a server-side HiLo document;
the generator, the block size, and the max-id calculation are quoted here
because the V2 contract is documented against them.

**The generator (client).** One `AsyncHiLoIdGenerator` per collection tag,
held per store in a `ConcurrentDictionary`
(`src/TerraBase.Client/Documents/Identity/AsyncMultiTypeHiLoIdGenerator.cs:17`,
`GenerateNextIdForAsync` `:133-158`). The generator starts with an empty
range — `new RangeValue(1, 0, null)`, so `Min` exceeds `Max` and the first
mint must fetch (`AsyncHiLoIdGenerator.cs:34`). Mints increment locally:
`Interlocked.Increment(ref range.Current)` returned while `id <= range.Max`
(`:100-107`). When the local range is exhausted the generator fetches the
next one through `NextHiLoCommand(_tag, _lastBatchSize, _lastRangeDate,
_identityPartsSeparator, Range.Max)` and adopts `Low`/`High`/`LastSize`/
`ServerTag` (`GetNextRangeAsync`, `:143-158`). Exactly one next-range fetch
is in flight at a time — the `_nextRangeTask` lazy replaced by
`Interlocked.CompareExchange` (`:65`, `:96-129`). The id string is
formatted client-side as `Prefix + id + "-" + ServerTag`
(`GetDocumentIdFromId`, `:37-40`).

**The block size (server).** `CalculateCapacity(lastSize, lastRangeAt)`
(`src/TerraBase.Server/Documents/Handlers/Processors/HiLo/
HiLoHandlerProcessorForGetNextHiLo.cs:68-86`): with no prior range the
capacity is `max(32, lastSize)`; if the previous range was taken less than
30 s ago the capacity doubles, `min(max(32, max(lastSize, lastSize * 2)),
1024 * 1024)`; if more than 60 s ago it halves, `max(32, lastSize / 2)`;
otherwise it stays. The floor is 32 and the ceiling is 1 Mi — never less
than 32 regardless of input.

**The max-id calculation (server).** One HiLo document per tag, id
`"TerraBase/Hilo/" + tag` (`HiLoHandler.cs:9`,
`TerraBaseHiloIdPrefix`), carrying a single `Max` field
(`HiLoResult.cs:27-30`, `HiloDocument`). The merged next-range command
computes `OldMax` as the stored `Max`, or the client's reported
`LastRangeMax` when the document is missing, and as
`max(oldMax, LastRangeMax)` when it exists — then writes
`Max = OldMax + Capacity` and answers `Low = OldMax + 1`,
`High = OldMax + capacity` (`MergedNextHiLoCommand.ExecuteCmd`,
`HiLoHandlerProcessorForGetNextHiLo.cs:98-152`, the rebase at `:121` and
`:133-139`; the response at `:52-60`). The stored max therefore never
moves backwards, and a lost HiLo document recovers to at least the
client's last known max.

**The reclaim (server).** `hilo/return` hands the unused tail of a range
back: the client sends `HiLoReturnCommand(tag, Range.Current, Range.Max)`
(`AsyncHiLoIdGenerator.cs:160-170`,
`HiLoReturnCommand.cs:16-26`), and the server shrinks the document's
`Max` to `Last` — but only while that range is still the head of the
identity (`oldMax == End && Last <= oldMax`, otherwise the return is a
no-op) (`MergedHiLoReturnCommand.ExecuteCmd`,
`HiLoHandlerProcessorForReturnHiLo.cs:41-65`). This runs on V1's
single-server transaction merger, which serializes HiLo commands.

## The V2 contract

`src/families/hilo/` distributes blocks of one identity name. The module
owns the discipline; the engine identity family owns the accounting; the
replication topology owns the fencing.

**D1 — Blocks are owner-fenced allocations of the identity family.**
One block allocation is one `IdentityCommand::Allocate` on the identity
record (`src/families/store.rs:686-716`), committed through the owning
member's family-write path, so D-1 admission fencing applies exactly as
for any family write: a member that does not own the addressed range
refuses the allocation (`MemberError::NotOwner`,
`src/replication/member.rs:461-486`). Identity records never merge
(GAP-F07's carve-out is counters only), so the single-writer discipline is
not an optimization of the conflict policy — it is what keeps an
allocation from being converged away. The block handed out for an
observed `next` and count `c` is the half-open range `[next, next + c)`;
V1's inclusive `[Low, High]` against a stored `Max` is the same
partitioning anchored one row down (V1 stores the last id handed; V2
stores the next id to hand), so V1 `Low = OldMax + 1` maps onto V2 `low =
next` and V1's post-allocation `Max` onto V2's `next - 1`.

**D2 — One distributor, one live block, one outstanding allocation.**
A `HiLoDistributor` mints locally from its live block and reaches for the
engine only when the block is exhausted — V1's generator shape. A
successor block is plannable only in that exhausted-to-refetch window, and
never while another allocation is outstanding (planned or committed but
unacknowledged) — V1's single in-flight `_nextRangeTask` discipline
(`AsyncHiLoIdGenerator.cs:65`, `:96-129`). One distributor per (member,
tenant, identity name) is the host's discipline, exactly as one generator
per tag per store was V1's (`AsyncMultiTypeHiLoIdGenerator.cs:17`).

**D3 — Hand-out is acknowledgement-gated.** A committed block is
*pending* until its allocation is durably recorded on members that
survive the allocating member's loss; only then does the distributor make
its ids mintable. The module takes this as a reported fact
(`BlockDurability::SurvivesOwnerLoss`), because the families seam owns no
transport — the host maps the replication outcome (the acknowledgement
report of the allocation operation, or a verified durable resolution on a
surviving member) onto it. Minting from a pending block is refused with a
typed error.

**D4 — A fresh distributor burns one block before handing anything out.**
The first allocation of every fresh distributor is a *transition burn*:
`block_size` ids are allocated and discarded — recorded as burn
accounting, never handed out, never re-handed. This is the recovery rule
for a lost block, and it is uniform: the distributor cannot tell a fresh
identity from a stale view, and uniformity is the safety property.

**Non-reuse across member loss.** With D1-D4, the ids a replacement owner
hands out are disjoint from every id the lost owner allocated:

1. Every block the lost owner *handed out* was acknowledged (D3), so a
   surviving member durably holds the identity record at or past that
   block's high. The replacement catches up from a surviving member
   before its first allocation (the control-plane precondition on the
   ownership transition, aligning with SH-05's entering-shard fencing),
   so its observed `next` is at or past the last acknowledged block.
2. The lost owner's *unacknowledged* allocation spans exactly one block
   (D2's single outstanding allocation), starting at the replacement's
   observed `next` — and D4's burn discards precisely that range before
   any hand-out.

This is V1's `LastRangeMax` rebase
(`HiLoHandlerProcessorForGetNextHiLo.cs:121`, `:134`) moved from the
client's report to the topology's discipline: V1 recovers a lost HiLo
document by trusting the surviving client's last known max; V2 recovers a
lost owner by catching up to its survivors and burning the one in-flight
block the survivors cannot have. The lane proof
(`src/replication/hilo_tests.rs`) drives the worst window — the owner
dies holding an allocated-but-unacknowledged block that no other member
received — and observes the replacement hand out strictly past it.

**D5 — Lost and reclaimed ranges are burned, never re-handed.** V1's
`hilo/return` reclaims the unused tail of a range while it is still the
head (`HiLoHandlerProcessorForReturnHiLo.cs:50-57`). V2 rejects reclaim.
The reason is the convergence model, not the guard: V1 serializes HiLo
commands on one server's transaction merger, so "still the head" is a
fact; V2's identity record is a non-merging replicated document that
converges whole-record last-writer-wins, so a shrink-back write competes
with a successor's allocation and can lose to ordering — the lost write
resurrects a range the successor is already handing out. V2 therefore has
no return path: an abandoned block's unused tail
(`[cursor, high)`), a lost member's in-flight block, and a deposed
owner's live block are all simply skipped past. The cost is ids, and the
capacity math below prices it.

## Capacity math and exhaustion

The identity space of one name is the non-negative `i64` range: the
engine ceiling is `MAX_IDENTITY_VALUE = 2^63 - 1`
(`src/families/payloads.rs:37`), the record stores `next` as one
structured integer (`src/families/state.rs:1110-1128`), and the allocate
stage refuses `next + count` past the ceiling with the typed
`FamilyError::IdentityExhausted` instead of saturating — saturation would
pin `next` at the ceiling forever (`src/families/store.rs:695-707`). The
distributor applies the same inequality when planning
(`next + block_size > MAX_IDENTITY_VALUE` ⇒ typed refusal), so a block is
never planned that the stage would have to refuse. Because the block is
half-open `[next, next + count)`, an allocation of `count` from `next`
hands ids up to `next + count - 1 <= MAX_IDENTITY_VALUE - 1`: the ceiling
value itself is reserved by the stage's `<=` guard and is reachable only
by an operator `Seed`. Ids minted are `u64` values `0..=MAX - 1`.

Burn accounting: every distributor (re)start or ownership transition
costs one `block_size` burn plus the first block's `block_size` before
the first handed-out id. Total allocations of size `S` a name admits is
`(MAX_IDENTITY_VALUE + 1) / S`, so transitions-until-exhaustion at one
transition per second is `(MAX_IDENTITY_VALUE + 1) / S` seconds:

- `S = 32` (the default and V1's floor): 2.88e17 seconds ≈ 9.1e9 years.
- `S = 1 Mi` (V1's ceiling): 8.8e12 seconds ≈ 2.8e5 years.

Each identity name has its own space (the record is per name,
`record_id("identities", &[name])`, `src/families/store.rs:690`), so a
churning name burns only its own capacity. An exhausted name refuses with
the typed error and never wraps; the operator remediation is
`IdentityCommand::Seed`, which moves `next` forward (backwards only with
`force`, `src/families/store.rs:708-713`).

## Accepted differences (V1 → V2, none silent)

1. **Fixed block size, no wall-clock adaptivity.** V1's server doubles or
   halves the capacity by the elapsed time since the previous range
   (`HiLoHandlerProcessorForGetNextHiLo.cs:68-86`). The V2 engine is
   deterministic and reads no wall clock; the distributor takes a fixed
   configured size bounded by V1's floor and ceiling
   (`MIN_BLOCK_SIZE = 32`, `MAX_BLOCK_SIZE = 1024 * 1024`). A host may
   adapt by configuring the next distributor.
2. **No reclaim.** D5 above; V1's `hilo/return` has no V2 counterpart.
3. **Recovery by burn, not by client report.** V1's lost-document rebase
   trusts `LastRangeMax`; V2's replacement burns one block after catch-up
   (D4). The reported fact replaces the trusted client.
4. **First handed-out id of a fresh distributed identity is `block_size`,
   not 1.** The uniform burn (D4) consumes `[0, block_size)`; V1's first
   range is `Low = 1, High = capacity`. The session's save-time
   allocation (GAP-D01 class 12) is a different layer and still starts at
   0 — the burn applies to the distribution layer only.
5. **Blocks hand raw `u64`s, not formatted id strings.** V1 formats
   `Prefix + id + "-" + ServerTag` client-side
   (`AsyncHiLoIdGenerator.cs:37-40`); in V2 the id-to-document-id mapping
   is the session/SDK's concern (GAP-D01 class 11 already placed
   entity-id conventions SDK-side), and the server tag concept folds
   into ownership: the block's origin member is a topology fact, not a
   distributed id component.

## Rejected alternatives

- **Merge identity records across members** (per-member buckets, GAP-F07
  style): the union of two allocations is not an allocation — a merged
  `next` can skip a range a member is still handing out, and the max of
  per-member nexts still needs the single-writer discipline to be sound.
  Fencing the allocation to the owner is both necessary and sufficient.
- **Reclaim on graceful return** (V1 parity): rejected in D5 for the LWW
  convergence reason. The savings are ids, priced above as comfortable.
- **Trust the replacement to catch up with no burn:** covers every
  *acknowledged* allocation but re-hands the lost owner's in-flight
  block. The burn is exactly the residual.
- **Skip the burn when the observed record is absent:** unsound — an
  owner can die between its burn and its first block; a replacement
  reading an absent record would then hand out the burned range. The
  burn's uniformity is what makes the fresh-record case safe to pay.

## Recorded limits and follow-ups

- The host/TBEP exposure of block distribution (who holds the
  distributor process-side, the acknowledgement mapping from the
  replication wait) is not in this packet; the module is the contract and
  the engine-side discipline, like GAP-D01's session before SE-08.
- The ownership-transition precondition (replacement caught up before its
  first allocation) is the control plane's to enforce; this packet's
  proofs drive it on the lane and document it as P4 in the discipline.
- No Benchmark Receipt is claimed: nothing here asserts a performance
  fact (PD-2026-08-26-03). The distribution layer exists to reduce engine
  round trips, and quantifying that is benchmark-lane work if ever
  claimed.
