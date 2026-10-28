# Disposable API-overhead benchmark tenant

`benchmark/scripts/api-overhead-tenant.py` provisions a synthetic user through hosted signup/mail verification and
normal device authorization, then creates an organization, project and API key with document/database scopes.
It runs the supplied benchmark command with a private credentials-file path in `TERRABASE_API_OVERHEAD_CREDENTIALS`.
User grants refresh at 90% of their send-time lifetime; sensitive operations obtain password step-up when required.
The helper never prints credentials. Recovery checkpoints are private and atomically replaced.

Pass `--plan-command` as one quoted argv string for `benchmark/scripts/api-overhead-plan.py`, followed by `--` and
the benchmark argv. The plan helper requires an admitted ts-orch executable, Vault id, NetBird executable, known-hosts
file, identity file and node. It appends this run's organization ULID, discovers that node in its admitted peers file,
and assigns the synthetic Enterprise plan through authenticated administration. The peer credential stays on-host.
There is no shell evaluation or production auth/revocation bypass.

The account owner closes its account in `finally`, waits up to 180 seconds for its deletion receipt to return 204,
then deletes its inbox and private recovery directory. If cleanup fails, the directory is retained and printed;
resume with `--cleanup-directory <retained-directory>`. Receipt settlement is logical account closure, not proof of
physical erasure. Do not use a serving customer's account or credentials.
Deletion responses retain the exact continuation-authorization header in that private checkpoint before the response
is consumed. A resumed pending deletion submits it with the same receipt; server authorization remains required.

Validation: the prior helper revision ran the authenticated three-region control published in
`2026-10-27-perf-api-live.md` on branch `fix/api-overhead-live`: database deletion accepted, account receipt 204,
inbox deletion settled and private credential directory removed. That measurement was contended and establishes
no speedup. The accompanying keyed-auth attribution report is historical diagnostic evidence, with its original
one-second warmup and source identity; it does not describe the current ten-second-warmup harness.
The continuation-header retention change was syntax-checked; it has not yet been exercised against live auth.

Recovery recheck on 2026-10-28: the gru auth task's local readyz returned 200 at
06:52 UTC. Two subsequent receipt reads at 06:57:52 and 06:58:12 UTC still returned
503 `temporarily_unavailable`. Scrubbed correlated auth logs identify
`TerraBaseUnavailableException` in `TerraBaseConnection.OpenAsync`, called by
`AccountDeletion.ReadReceiptAsync`. This establishes a storage-read refusal rather
than account deletion completion. Both attempts stopped before a deletion mutation;
private recovery material remains on beta and no replacement tenant was created.
No more identical receipt retries are attempted until relevant service state changes.

The normal main retry, Actions run `40643893257`, failed release admission for
both ts-auth and TerraBase: all three regions reported `connect: nats: Authorization
Violation` (auth 06:31 UTC, TerraBase 06:46 UTC). This differs from the earlier bake
log-coverage failure. No workflow, deployment credential or serving data was changed.
These failures prevent a claim of a complete normal rollout or live after proof.
