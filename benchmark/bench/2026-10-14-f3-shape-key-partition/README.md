# V4 F3: shape keys under a partition, ts-vtest-tb, 2026-10-14

Candidate `30d6f8331840add5f7df2a2b7f76fd38c29b2a81` (PR #316's first head; later heads change only the Fast test and
docs). Image `registry.fly.io/ts-vtest-tb:vtest-30d6f8331840`, digest `sha256:84e483a6…3d90d3`. Cluster: ts-vtest-tb
(terrascale-previews-515): gru `891e70eb4d0378`, sjc `6835466b44e258`, fra `48e4967a225338`, shared-cpu-2x 512 MB, 1 GB
volumes. Synthetic tenant only. Script: `f3-partition.py` (vtest.sh from `feat/repl-v5-harness` for partition and heal).
Raw rows: `raw/`. Ledger: `resources.md`.

## Method

1. Create database `f3-<ts>` (gru home; sjc and fra replicas), collection `probe`, and keys `K_rev` (documents:read)
   and `K_ctl` (read+write).
2. Wait until every machine passes both keys through the gate.
3. Cut sjc off: ip6tables drops all 6PN traffic between sjc and the other two, at both ends. DNS still lists sjc.
4. Revoke `K_rev` through gru (204).
5. Every ~6.6 s, ask each machine for a shape key with each credential. Stop after sjc has refused for 60 s.
6. Heal, then poll until sjc answers 401 for `K_rev` and every machine passes `K_ctl`.
7. Revoke `K_ctl` and drop the database.

Each request uses a well-formed handle and `key_id` that name nothing. Production seals no TBS1 objects yet, so no client
can learn the real key id. Without #307, shape bootstrap answers `429 shape_busy` on 512 MB. Response meanings:

| Response | Meaning |
|---|---|
| `409 must_refetch` | Passed authentication, the catalog gate and tenancy; released nothing |
| `503 catalog_stale` | The gate refused |
| `401 invalid_key` | Revocation applied |

The 200 path, with the correct epoch key, is covered by the Fast test.

## Result

Partition at 21:20:45Z. Revoke 204 at 21:20:45Z (cut + 0.3 s, round trip 292 ms). Heal at 21:30:58Z.

| Machine, credential | During the partition | First refusal after the cut | After heal |
|---|---|---|---|
| sjc, `K_rev` (revoked, cut off) | 409 through sweep at cut + 509.1 s | `catalog_stale` at cut + 515.7 s | 401 by heal + 20.6 s |
| sjc, `K_ctl` | 409, then 503 | `catalog_stale` at cut + 515.7 s | 409 by heal + 20.6 s |
| gru, `K_rev` | 401 from the first probe (cut + 0.3 s) | — | 401 |
| gru, `K_ctl` | 409 through cut + 569.0 s | `catalog_stale` by the first post-heal sweep | 409 by heal + 20.6 s |
| fra, `K_rev` | 401 from the first probe | — | 401 |
| fra, `K_ctl` | 409 through cut + 549.1 s | `catalog_stale` at cut + 561.7 s | 409 by heal + 31.2 s |

- **Bound held.** The revoked key last passed sjc's gate 508.8 s after the revoke's 204, within one epoch (600 s). sjc
  went stale at cut + 515.7 s. That places its last catalog proof about 84 s before the cut, consistent with 60 s
  anti-entropy rounds.
  - The revoke landed in epoch [21:20, 21:30). The last release was in that epoch too. So the revoked holder could open
    nothing sealed after 21:30:00Z, inside spec check 4 (no chunk sealed after the next epoch).
- **Refusals were safe.** All 23 refusals were `503 catalog_stale` with `Cache-Control: no-store` and `Retry-After: 1`,
  and no `key` field. The 265 passes carried `private, no-store` and released nothing (dummy `key_id`).
- **The cost is availability.** gru and fra also stopped releasing once they could not prove sjc for an epoch. Healing
  restored every machine within 31.2 s.

## Healthy revocation (V4 row 11 rerun on the candidate)

`revoke-timing.py 5` (from `feat/repl-v5-harness`) ran at 21:32:30Z, with no partition. For each region, the table shows
the first 401, measured from the revoke's 204.

| Trial | sjc first 401 sent | fra first 401 sent | Accepted after the 204 |
|---|---|---|---|
| 1 | −210.8 ms | −192.6 ms | none |
| 2 | −153.1 ms | −202.9 ms | none |
| 3 | −150.7 ms | −200.5 ms | none |
| 4 | −225.3 ms | −225.1 ms | none |
| 5 | −162.2 ms | −212.4 ms | none |

Negative values mean the revocation applied in that region before the operator's 204 reached the client.

## Limits

- One partition run, one cut region (sjc). Proof times are inferred from refusal times; they are not logged.
- The bound counts machines the directory lists. A machine that leaves the directory stops counting (docs/terrabase/shape-reads.md §13).
- Attempts 1–3 aborted before any partition: no handle (`429 shape_busy`), then the keys had not yet replicated. Each
  cleaned up (`raw/f3-aborted-attempts-1-3-30d6f833.jsonl`).

## Live proof with real epoch keys and real TBS1 chunks (run 2, 2026-10-14)

`live/f3-live.py` with `live/tbs1.py`. The decoder is written from shape-reads.md §12 and checked against
`shape-vectors.json` `tbs1_vectors`.

**Candidate.** `84d1e0991f4663f6eab4f77c16a271a66c120db2`, image `registry.fly.io/ts-vtest-tb:vtest-84d1e0991f46`,
digest `sha256:380977aea1c8f2ba6ccef1740f50a0fb3f94269c40acfa539b825785fea524ac`. It is built from the proof-only
branch `fix/f3-live-proof-candidate`: main `35b14217` + #307 + #309 + #314 + #283 (TBS1 sealing and the Tigris
manifest) + #316 at `41e715bc`. The #316 files are byte-identical. The only merge edit keeps #309's `MatchedAsync`
block next to #316's proof lines in `AntiEntropy.cs`.

| Step | Result |
|---|---|
| Before the cut, sjc, `K_rev` | Shape `snap.20.0` sealed and mirrored: 4273 B, size and sha256 checked. Its key came from sjc's `/shape/keys` (200, `private, no-store`). The TBS1 object decrypted, and the plaintext equals the origin body |
| Cut sjc (6PN only) | 22:54:18Z, epoch E = 2986697 |
| Revoke `K_rev` through gru | 204, cut + 0.2 s |
| gru and fra, `K_rev` | 401 from the first sweep |
| sjc, `K_rev`, partitioned | Last key 546.8 s after the revoke, for epoch E + 1 (2986698). Every key it got was for E or E + 1 |
| sjc refuses | From cut + 552 s: `503 catalog_stale`, `no-store`, `Retry-After: 1` |
| Heal | 23:04:50Z, rc 0. sjc answered 401 to `K_rev` and 200 to `K_ctl` within 11 s; every machine was released by then |
| Later chunk | Offset `20`, sealed on sjc in epoch E + 2 (2986699) after the heal. None of `K_rev`'s keys (E, E + 1) open it. `K_rev` gets 401 for its key and its manifest in all three regions. `K_ctl`'s key opens it, matching the origin body |

The revoked credential's last decryptable data is therefore what was sealed by the end of epoch E + 1, which is spec
check 4.

**Run 1 (`20f6238b`, also cut sjc's DNS, `raw/run1-20f6238b-dns-cut.jsonl`).**
- From 2 min after the revoke, sjc answered `503 unavailable`, then `catalog_stale` from cut + 554 s. With its DNS cut,
  sjc can no longer resolve Tigris, so its data-key reads failed: this fails closed, but it is not the gate.
- The keys `K_rev` got were all for epoch E.
- `vtest.sh heal` could not reach sjc, because `fly ssh` certificate checks need DNS. fra kept its rules until it was
  flushed by hand, and sjc's rules were cleared by `fly machine restart`. That is why run 2 cuts 6PN only.
- Its post-heal phase timed out, but every key and the database were still cleaned up.

## Fast coverage (CI)

`ShapeCryptoTests` covers:
- unproven peers;
- an exact 599 s / 601 s bound;
- tenant-only rounds never proving the catalog;
- an empty or self-only directory, with recorded regions;
- the catalog going stale during the lookup;
- an API key revoked after authentication;
- the 503 and 401 headers and bodies.
