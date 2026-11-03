# Provision the regional canaries

Run from a ts-status checkout with `CLOUDFLARE_API_TOKEN` exported (or
`ts-dev-secret get dev-cloudflare-api-token` available; the token only
updates ts-status Worker secrets). This command provisions the canary
databases and keys and updates only the two ts-status Worker secrets. It does
not deploy or restart a service, change orchestration, or touch status history
in D1.

```sh
nice -n 19 python3 scripts/recover-canaries.py
```

The first run of an account asks, once, for a password reset code mailed to
the owner's address; the password is generated and kept only in the private
state file. Every later step signs in with that password: session-approved
device grants and password-approved step-up proofs consume no email codes.
Keys are one per regional database, narrowed by the database's ULID (ts-auth
refuses a key narrowed by name, since a rename can move a name) and to
`documents:read documents:write`; probe requests still address the database by
name, which TerraBase resolves to the same ULID.

DECISION (2026-10-27, Q-CANARYPLAN): the canaries live in the internal
`terrascale` organization's `ts-status` project. The Free plan's 50,000 monthly
operation units cover less than a day of probing: the Data API read canary
alone takes about 2,900 units a day and the document probe about 35,000. Once
the allowance was spent, TerraBase refused every canary write with
`429 operation_allowance`, and the status page showed the Document API down in
every region. The internal organization's internal billing plan publishes a
paid tier to TerraBase, so no paid capacity is bought. A spent allowance now
shows as "Canary quota exhausted" (unmeasured), never as an outage
(`src/probe/canary.ts`). An automatic incident whose component the monitor
cannot measure for five minutes closes as unconfirmed
(`src/domain/auto-incident.ts`).

The command discovers serving regions from the public TerraBase `/v1/regions`
contract and uses the advertised global API with the supported regional pin.
Every region must name itself in `/readyz` and `X-TerraBase-Region`, including on
all document requests. Until the replacement sync engine is ready, TerraBase
refuses all-region and replica placement (`replicas_unavailable`), so the
canary is one home-region database per region, `status-canary-<region>`, each
proved by its own regional pin and its own database-narrowed key. Fresh
documents prove write/read/query/delete/read-after-delete; the retained
fixture supports Data API reads. Live status `/api/v1/regions` supplies
Document API endpoint IDs and the Data API's `canary` cluster ID. Negative
HTTP checks are excluded. Missing regional Document API catalog entries stop
installation and require the status catalog owner; this command does not
mutate D1 or invent endpoint IDs or origins.

Private state is kept at `~/.local/state/ts-status/canaries.json` (0600), outside
Git, under an exclusive lock; the sign-in cookie jar lives beside it with the
same mode. A valid grant and a key narrowed by the database's ULID are reused on
a rerun, so a rerun needs no approval while the grant lasts; a key narrowed by
name is replaced.

## After a production reset: re-provision the canary

A TerraBase production reset erases the internal organization's TerraBase
enrollment, databases and keys together with everything else; ts-auth's
PlatformBootstrap re-creates only the owner account and the platform
organization. Recovery is one command, but two of its dependencies are ordered:

1. **Internal billing must be reconfigured for the recreated organization.**
   The canary writes (about 1.1 million operation units a month across three
   regions) exceed every free allowance, and the free plan allows a single
   database. Recreate the six service projects (`ts-dashboard`, `ts-auth`,
   `ts-router`, `ts-status`, `ts-orchestrator`, `ku`) in the recreated
   `terrascale` organization — `recover-canaries.py` does this — then run the
   dashboard's `configure-internal-billing` command on the dashboard host
   (see the monorepo's `docs/dashboard/internal-billing.md`) before creating
   the second regional database. The dashboard publishes the paid plan to
   TerraBase within thirty seconds of configuration.
2. **The first database enrolls the organization.** TerraBase knows a
   recreated organization only after a project token creates its first
   database there; the script's first `status-canary-<region>` create performs
   the enrollment, so a `name_taken` or `unknown_database` in the first minute
   converges on retry.

Then rerun `recover-canaries.py`; the status page clears its
"Canary credential invalid" monitoring issues within one probe round of the
Worker secrets updating.

Secrets enter the Cloudflare API directly, one
`PUT /accounts/<account>/workers/scripts/ts-status/secrets` per secret, with
the token read from `CLOUDFLARE_API_TOKEN` or
`ts-dev-secret get dev-cloudflare-api-token`; key material never appears in
an argument or log. Subprocess responses, HTTP bodies, device codes, passwords
and keys are never logged. Output contains only stages, object IDs and
regional CRUD proof. If authorization fails, fix the intended CLI identity or
scope. D1 7403 does not justify widening the CI token or adding a TerraBase
status-storage path.

Focused offline tests: `nice -n 19 python3 -m unittest discover -s scripts -p 'test_*.py'`.
