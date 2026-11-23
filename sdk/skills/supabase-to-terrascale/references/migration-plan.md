# Migration plan worksheet

Fill this in before rehearsal. Store identifiers and results, never secrets or
personal records. Refer to credentials by environment variable name.

## Template

| Field | Fill in |
| --- | --- |
| Scope and exclusions | Project, applications, data and integrations |
| Source | Supabase project ref, app schemas, deployment revision |
| Destination | TerraScale project/database, region selection, capacity evidence |
| Owners | Cutover owner, verifier, rollback owner |
| Objectives | Maintenance budget, RPO, RTO, rollback deadline |
| Inventory | Resource, caller, count/bytes, authorization, replacement, owner |
| Gaps | Unsupported behavior, replacement, proving request, release prerequisite |
| Identity | Source ID, destination subject, mapping storage, orphan handling |
| Data | Table mapping, conversion rules, dependencies, export cutoff |
| Artifacts | Private paths, versions, artifact hashes, access/retention |
| Verification | Counts, canonical hashes, queries, isolation and side-effect checks |
| Cutover | Maintenance or dual-run; writer fence and routing sequence |
| Rollback | Trigger, reverse mapping, accepted-write journal, stop conditions |
| Closure | Monitoring interval, retention expiry, credential revocation |

For every resource, track these states: inventoried, replacement decided,
rehearsed, reconciled, cut over, accepted. A resource cannot skip reconciliation
because its SQL imported successfully.

## Filled example: a team task app

This is a plan example, not a claim that a production project was migrated.
Destination behavior is based on the [import guide](https://docs.terrascale.tech/guides/import),
[SQL guide](https://docs.terrascale.tech/concepts/sql) and
[end-user authentication guide](https://docs.terrascale.tech/guides/end-user-authentication).

| Field | Example decision |
| --- | --- |
| Scope | Web frontend, API, nightly task report; attachments excluded until storage rehearsal passes |
| Source | Supabase production ref recorded privately; `public.todos`, `public.profiles` |
| Destination | Project selected explicitly; database `app-rehearsal` for rehearsal, `app` for cutover |
| Owners | API maintainer fences writers; operator switches deployments; teammate verifies |
| Objectives | Planned 30-minute maintenance; RPO zero at the fence; 30-minute rollback target |
| Database | UUID IDs retained as lowercase text; booleans become 0/1; UTC timestamps normalized |
| Auth | Keep existing sign-in during database cutover; server validates tokens and maps subject to old owner ID |
| RLS | API adds owner predicates to every read/update/delete; browser loses direct privileged access |
| Functions | Report ported to a documented JavaScript handler; schedule enabled only after cutover |
| Realtime | Use application refresh until actual installed shape support passes the client rehearsal |
| Gaps | No Postgres extension restore or continuous source sync; convert data under maintenance |
| Artifacts | Private migration directory, schema map and canonical sample hashes; no exported users in Git |
| Verification | Counts for both tables; hashes for 20 fixed task IDs; two-user CRUD isolation |
| Cutover | Stop API writes/report; drain requests; export/import; verify; route; reopen API writes |
| Rollback | Before reopening, route back directly; afterwards fence and reverse-map all accepted changes |
| Closure | Observe for 24 hours; retain source and artifacts for seven days under project policy |

Example inventory entries:

| Resource/caller | Source behavior | Replacement and proof | Gate |
| --- | --- | --- | --- |
| `todos` / API | `auth.uid() = owner_id` on CRUD | Validated session plus SQL owner predicate; test two distinct users | Cross-user attempts denied |
| `profiles` / server | Source UUID keys | Retain legacy ID until identity mapping is complete | No missing owners |
| Task report | Scheduled RPC | Port logic to app job or documented function; synthetic execution | One report, no duplicate send |
| Browser subscription | Supabase Postgres changes | Explicit refresh or tested shape integration | Reconnect and permission tests |

## Failure record

For a failed rehearsal record the resource, stage, error code, first failed
statement/batch, last successful artifact/checkpoint, cause, fix and rerun scope.
Never paste the full request when it contains row values or credentials.
A changed conversion requires re-verification of every affected table and query.
A changed identity mapping requires all isolation tests again.
