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

Validation: this exact helper source ran the authenticated three-region control published in
`2026-10-27-perf-api-live.md` on branch `fix/api-overhead-live`: database deletion accepted, account receipt 204,
inbox deletion settled and private credential directory removed. That measurement was contended and establishes
no speedup. The accompanying keyed-auth attribution report is historical diagnostic evidence, with its original
one-second warmup and source identity; it does not describe the current ten-second-warmup harness.
