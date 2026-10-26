# Plan database allowance and organization plans

TerraBase enforces each plan's database allowance itself, and accepts an organization's plan
before the organization has a database.

## Database allowance

Plans allow these live databases per organization, across all of its projects
([pricing.json](https://terrascale.tech/pricing.json); `QosPolicy.Databases`):

| Plan | Databases |
|---|---|
| hobby (free) | 1 |
| starter | 5 |
| pro | 25 |
| team | 100 |
| enterprise | unlimited |

A request that would make a new database once the organization's live databases reach the
allowance answers `409` with code `database_limit` and a message naming the plan and allowance:

```json
{"error":{"code":"database_limit","message":"the organization's hobby plan allows 1 database; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing"}}
```

- Covered routes: `POST /v1/projects/{project}/databases` (and its management and organization
  route aliases), the legacy `PUT /v1/projects/{project}/databases/{database}`, branches
  (`POST …/databases/{database}/branches`), copies, backup and point-in-time restores, and
  `create_database` in management commits. Nothing is created when it is refused.
- The check runs in the catalog transaction that registers the database and is strict across
  regions: each region spends units of the allowance it holds and asks other regions for one when
  it has none ([decision 0016](decisions/0016-strict-database-allowance-escrow.md), which lists
  the cases it does not cover). Concurrent creates in different regions do not pass it together.
  A region that holds no unit and cannot reach every region that may hold one answers
  `409 database_limit` with "the rest of the organization's database allowance is held in regions
  this region cannot reach now; retry shortly".
- A database counts once, however many regions hold it: a region that adopts a replica of a
  database created elsewhere does not count it again. Deleted databases do not count.
- Replaying a create for a name that exists still finds it (`200` on the legacy `PUT`,
  `409 name_taken` on `POST`), even at the allowance.
- Organizations already above their allowance (after a downgrade) keep their databases; only new
  ones are refused.
- The [platform databases](platform-databases.md) are exempt: their organization is on enterprise
  and is excluded explicitly.

## Organization plans before the first database

`PUT /admin/organizations/{organization}/plan`, on the internal administrative listener with the
same identity as `PUT /admin/projects/{project}/plan`, publishes a plan for the storage
organization of a ts-auth organization (whose id is the ts-auth organization id).

Body: `{"plan": "hobby" | "starter" | "pro" | "team" | "enterprise"}`.

| Organization record | Result |
|---|---|
| `{organization}` is not a canonical ULID, or the plan is unknown | `400 invalid_request` |
| Exists, no members (created by enrollment or by this route) | `200` organization; an unchanged plan writes nothing and records no audit event |
| Missing, plan `hobby` | `404 not_found`; Hobby is the default, so nothing is stored |
| Missing, any other plan | `200` organization; creates the memberless record and records `settings.plan_updated` |
| Has members (owner-managed) | `404 not_found`; its owners set the plan |
| Deleted | `404 not_found`; it is not recreated |

The first database a project token creates for the organization adopts the record and keeps its
plan. `PUT /admin/projects/{project}/plan` keeps working for enrolled projects.
