# Runbook: adopting the catalog restore anchor (first rollout)

- Status: **prepared, not executed.** Needs the user's explicit approval at the time of the rollout. Nothing in this
  document is to be run against live machines until then. No credentials are configured or handed out by it.
- Applies to: catalogs that are **not genesis and have no anchor**: a bucket swapped or emptied, anchors deleted by a
  lifecycle rule or an operator, or a build with anchors deployed onto machines that already serve a catalog. It does
  **not** apply to the first deploy of storage format 4 (R10a), which starts every catalog fresh (genesis): those machines
  open and anchor without an operator (R3, #108 comment 6131106787). Rules:
  [system-catalog-invariants.md](system-catalog-invariants.md), section 2a.
- Validation (isolated, no live system):
  `CatalogLatticeTests.Adopting_anchors_on_a_running_cluster_takes_one_recorded_operator_open_and_later_restarts_take_none`.

## Why the rollout needs one operator step

A machine compares its catalog with its sealed restore anchor (`catalog-anchor/<machine>` in the region's backup bucket)
at every start. Machines that ran a build without anchors have none, and their catalogs are not genesis (they hold
organizations and each other's states). A missing anchor on such a catalog is **unknown** (it could be a restore whose
anchor went missing), so after the deploy every machine is closed and untrusted, and none can verify another.

One operator open on **one** machine ends that: it opens, writes its anchor, reports itself trusted, and every other
machine opens by pulling from it (and anchors). Restarts after that need no operator.

## Adoption versus bypass

The same `catalog-open` file serves both. What separates them is the evidence, and this runbook only covers adoption.

| | Adoption (this runbook) | Bypass of restore protection (not this runbook) |
| --- | --- | --- |
| `pending` on the machine | `restore anchor missing: a trusted peer or an operator's decision` (plus other machines' ids) | `restored to an older copy: an operator's decision` or `restore anchor unread` |
| Cause | the build that introduced anchors | a volume or catalog put back to an older copy, a forged or unreadable anchor |
| Volume | the same volume that served before the deploy | a restored or replaced volume |
| Allowed by this runbook | yes, once, with the evidence below | **no**: stop, escalate to the user |

If any machine shows the right column, **stop** (see Stop conditions).

## Evidence that the chosen catalog is current

Collect all of it before writing the request; record it in the ticket the request names.

1. **Same volume, no restore.** For the chosen machine: the volume id attached after the deploy equals the one attached
   before it (`fly machine status <machine>`, `fly volumes list`), and the volume has no restore or fork since the last
   deploy (`fly volumes list` shows no newer volume created from a snapshot for that machine; `fly machine status`
   events show the deploy's update/restart only).
2. **It served until the deploy.** The machine was started and serving (`/readyz` 200 on the previous build, or its
   request logs) right up to the deploy's restart.
3. **It is the reason the gate waits, and nothing else.** `GET /catalog-gate` on its internal listener (port 8081,
   `fly-local-6pn`, HTTP/2), for example from `fly ssh console -s` on that machine:
   `curl -s --http2-prior-knowledge http://fly-local-6pn:8081/catalog-gate`
   - `open: false`, `trusted: false`;
   - `pending` holds `restore anchor missing: a trusted peer or an operator's decision` and, besides it, only ids of
     machines that are themselves in the same adoption state (or still on the previous build), never
     `restored to an older copy…` or `restore anchor unread`.
4. **Converged with the others (when they already run the new build).** Every machine that already runs the new build
   reports the **same `digest`** on two reads at least 60 s apart (two anti-entropy rounds). Machines still on the previous
   build expose no `/catalog-gate`; for them evidence 1 and 2 must hold on their side too, and they are deployed after.
5. **Bucket healthy.** No `restore anchor unread` anywhere; the backup bucket is attached on every machine (on Fly a
   machine without a bucket stays closed by design).

Choose the machine in the first region the deploy reaches (gru in today's order).

## Procedure

1. Deploy the new build to the first region only (the rollout stops there on its own: the live check fails while the gate
   is closed). Expected: the machine starts closed; `/healthz` 200, `/readyz` 503; tenant and management requests answer
   503 `catalog_unverified`; peers' replication streams keep flowing.
2. Collect the evidence above. Any stop condition: stop.
3. Write the request on the chosen machine, one line, `<operator>: <reason>` (at most 500 bytes), naming the ticket and
   the approval:
   `fly ssh console -s -C "sh -c 'printf %s \"<operator>: adopting restore anchors, evidence in <ticket>, approved by <user>\" > /data/data/catalog-open'"`
4. Within about a second: the file is gone (consumed), `GET /catalog-gate` shows `open: true, trusted: true`, `/readyz`
   200, and the log has the warning `Operator <operator> opened the catalog gate without verifying [...] for this start`.
5. Resume the rollout region by region. Each machine starts closed (missing anchor), pulls from the trusted, open machines
   and opens without an operator; each writes its anchor (`trusted: true` before it opens is not expected: it becomes
   trusted by opening).
6. After the rollout: restart one machine as a check (approved window): it opens without an operator.

## Expected transitions

| Step | Chosen machine | Other machines |
| --- | --- | --- |
| Deploy reaches it | closed, untrusted, `pending` = anchor missing (+ others) | previous build, serving |
| `catalog-open` accepted | open, anchors (`catalog-anchor/<machine>` written), trusted | unchanged |
| Rollout reaches another machine | open | closed, untrusted, pending = anchor missing; pulls from the open machine; opens in about a second; anchors |
| Any later restart | opens after pulling from its peers (anchor present, catalog not behind) | same |

## Audit record

- Catalog document `catalog_gate_opens/<ULID>`, replicated to every machine: `machine`, `operator`, `reason`, `at`,
  `accepted: true`, `unverified` (what the gate still waited for: the anchor-missing reason and the ids of machines not
  yet verified).
- Warning log on the machine: `Operator <operator> opened the catalog gate without verifying [...] for this start: <reason>`.
- A refused request (malformed, no reason) is recorded the same way with `accepted: false` and `refused`.
- Metric: `terrabase.catalog.verified` = 1 on the machine from the open on.

## State preserved

Everything. The open writes only its audit document and the anchor; the catalog, the client databases, the replication
cursors and the other machines' states are untouched. The other machines join the chosen machine's catalog by the
lattice (nothing they hold is removed by pulling).

## Rollback

- Rolling the build back removes the gate (the previous build has none): the machines serve as before. Anchors written
  by the new build stay in the bucket; the previous build ignores them.
- Re-deploying the new build later: machines whose catalog head moved past their anchor meanwhile open by pulling as usual
  (an anchor behind the catalog is fine); a machine without an anchor needs this runbook again.
- The open cannot be undone (its decision is recorded), and it does not need to be: it holds for that start only.

## Stop conditions (do not write the request; escalate to the user)

- Any machine shows `restored to an older copy: an operator's decision` or `restore anchor unread`.
- The chosen machine's volume changed, was restored or forked, or it did not serve until the deploy.
- Machines on the new build report different `digest` values on two reads 60 s apart.
- A recorded machine that should be running is stopped or unreachable (its id stays in `pending` after the others opened).
- The request is refused (its record says `accepted: false`): fix the request, do not retry blindly.
- After the open, another machine on the new build stays closed for more than a few minutes with the open machine
  reachable: inspect its `pending`; do not open it by request too without new evidence.
