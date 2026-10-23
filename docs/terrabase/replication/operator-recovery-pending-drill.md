# Isolated pending-rebuild drill

The host-local `scripts/terrabase/recovery-drill.py` prepares the production drill without adding a fault-injection endpoint or permission. It is deliberately limited to FR1's retained throwaway organization `01M5N3V3EEA0QS2S7BT4PF1RF7`, project `01M5N3V3V78DCEHHRX1M5KZKAM`, and owner `5H1K4FE8MBAF6ENA73DF6F9JK6`. It refuses to fault the original `fr1-proof4` database. No auth/dashboard platform database, status canary, other project, SYSTEM file, or existing customer database is mutated.

The rollout owner first deploys this PR's merged release. Use the existing operator identity and the throwaway owner's current hosted sign-in token; do not issue a credential. On the chosen OCI target, prepare a root-owned 0600 regular header file containing exactly these three existing headers: `X-TerraBase-Peer-Credential`, `X-TerraBase-User-Id`, and `Authorization` with that user's sign-in Bearer token. Deliver values through protected stdin/files, never arguments, output, chat, source control, or the reason. The helper sends bearer proof only to the local recovery route and sends only peer authentication to data/replication routes. It never follows redirects.

Run each stage separately as the existing root operator. Paths below are examples of dedicated files; never reuse a manifest for another drill:

```sh
python3 scripts/terrabase/recovery-drill.py prepare \
  --headers-file /run/recovery-drill.headers \
  --manifest /var/tmp/recovery-drill-fixture.json

python3 scripts/terrabase/recovery-drill.py arm \
  --headers-file /run/recovery-drill.headers \
  --manifest /var/tmp/recovery-drill-fixture.json \
  --confirm <nonce-from-the-prepared-manifest>

python3 scripts/terrabase/recovery-drill.py retry \
  --headers-file /run/recovery-drill.headers \
  --manifest /var/tmp/recovery-drill-fixture.json
```

`prepare` first requires an authenticated 409 `no_matching_rebuild` on the retained original database. That request runs the real trusted-catalog and <=30-second peer-proof checks. It then creates a **new**, uniquely named `recovery_drill_<ULID>` database placed in all regions, creates two synthetic documents, verifies both on all three nodes, deletes one, and verifies its absence on every node. Every document carries the exact fixture nonce. It records the new database ID and local incarnation in a private manifest. Provisioning accepts only 201; an existing database is never reused.

`arm` requires the exact manifest nonce, intact synthetic peer copies, another fresh authenticated preflight, and no existing rebuild. It verifies the local copy has only the exact fixture state keys in an immediate SQLite transaction. An empty directory at this copy's exact rebuild side path prevents the ordinary worker from completing before the operator retry. The only injected damage is an invalid bucket in the **new synthetic database's derived Merkle metadata**; documents, replicated states and their hashes remain untouched. An authenticated normal Digest RPC discovers the damage. The server, rather than this script, writes its real durable pending-rebuild record. SYSTEM is read-only to the script. The pending stage is accepted only for this exact old incarnation, phase Pending, and no new incarnation.

`retry` rechecks that pending record and the actual fresh-authorized preflight **before removing the blocker**. It removes only that exact empty directory, then calls the authenticated retry endpoint with the recorded old incarnation and bounded reason. A successful drill requires 204, a new incarnation, retired pending record, no remaining side path, a production `_tb_refill` certificate, and the exact live/deleted document observations on all three nodes. Repeating the old request must reach 409 `no_matching_rebuild` without another rebuild.

## Refusals and cleanup

**503 `catalog_stale` is the correct fail-closed answer.** The script neither writes nor fabricates freshness state, changes membership, invokes a trust reset, relaxes the window, or forces a catalog proof. If the preflight is stale, preparation creates nothing; stale arm leaves the copy undamaged; stale retry retains the pending fixture and blocker. Restore ordinary authenticated peer connectivity/anti-entropy, then rerun only the unchanged stage when it is safe. A node can have a healthy readiness response while a peer proof is too old for recovery authorization.

Every failure stops; there are no mutation retry loops. Preserve the private manifest. A failure after provisioning can leave a partially seeded new database; inspect it rather than blindly repeating prepare. A failure after arming can leave a blocked pending rebuild; do not remove a blocker until the target, incarnation, intact sources and freshness are checked. A background worker can win after the blocker is removed; a 409 in that race is **not** an authenticated 204 proof, even if recovery completed. Never erase the ledger or fault record to manufacture a success.

After recording successful evidence, the rollout owner deletes **only the newly created fixture database by its manifest name** through the normal authorized database API and deletes the dedicated credential header file. Retain the non-secret manifest with the evidence. Keep FR1's original tenant until its owner releases it. No shared directory/glob deletion, service restart, SIGKILL, or graceful-stop/rollback change is involved.

## Local evidence and limits

The helper's nine filesystem/SQLite guard checks use owned temporary files and no network or credentials. They cover stale preparation/arm/retry, extra state, original/foreign tenant refusal, private files/symlinks, and preserving the pending blocker. Their fake fault response is not certification proof.

`An_authenticated_retry_certifies_a_real_pending_fixture_with_fresh_peer_proofs` exercises two real servers, the production derived-digest fault recorder, the real pending ledger/worker, fresh anti-entropy proof, the internal owner retry, certified refill and replay denial. It uses neither a rebuild nor freshness test double. Enrolled ts-auth ownership and role denials are covered separately by the PR's Fast integration tests. Production 204 and all-region evidence remain FR1's post-deploy drill; CI or local checks do not substitute for that proof.

Recorded focused validation on this change: the real two-server test passed 1/1 in 6.564 seconds; `nice -n 19 python3 scripts/terrabase/recovery-drill-test.py` passed 9/9. The earlier focused Fast ownership/binding filter passed 51/51. Production is still untested on this commit.
