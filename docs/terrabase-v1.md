# Dashboard on TerraBase v1 (design, TS-809)

Status: design. Blocks TS-677's TerraBase cutover for the dashboard.

## Why

- Production runs f190cbad8. Its SDK addresses documents as `documents/{id}`
  with legacy IDs, which only TerraBase 263598b4 (pre-TS-754) serves. After
  TS-677 moves production to TerraBase main, that build stops working.
- `main` cannot replace it: its vendored ts-go (1f598c62) validates ULIDs but
  tests against a pre-ULID TerraBase. ts-go main changed the API the
  dashboard is built on:
  - `DocumentIDFor` and `ValidateDocumentKey` are gone;
  - document keys must be ULIDs;
  - `RegisterProject` became `PrepareProjectRegistration` + `Commit`.

Production data is wiped once by the TS-677 cutover. Nothing migrates: the
dashboard starts on empty collections in the new shape.

## The v1 contract the dashboard builds on

Sources: TerraBase main 7736093, ts-go main 4ded8f0.

- **Document IDs** are canonical ULIDs (`v1.NewID()`). Natural keys are
  payload fields. ts-go rejects any other document key.
- **Uniqueness** comes from native unique constraints. They are declared per
  collection with `PUT /v1/management/…/databases/{d}/schema/{collection}`:
  - up to 32 keys, each covering 1–4 paths;
  - normalization `none`, `lowercase` or `trim_lowercase`;
  - only on an empty collection.

  A duplicate fails the whole transaction with 409 `unique_violation`. Across
  regions, duplicates converge to the smallest key.
- **Transactions** support `create`, `put`/`patch`/`delete` with
  preconditions, `assert` and `assert_count`. Limits: 16 `assert_count` per
  transaction and 10,000 documents examined. A transaction never returns
  query rows, so read-modify-write uses document versions (`IfMatch`).

## Collection model

Every document gets a fresh ULID. Each lookup by natural key is a `Query`
with `Eq` on a uniquely constrained field, `Limit: 1`.

| Collection | Natural key field(s) | Unique constraint | Replaces |
|---|---|---|---|
| sessions | `token_hash` | `token_hash` | `DocumentIDFor(sessions, hash)` |
| throttle | `key_hash` | `key_hash` | `DocumentIDFor(throttle, hash)` |
| grants | `device_hash`, `user_code_hash` | each separately | grant ID + `claim:user_code_hash:` claim document |
| users | `email`, `auth_subject` | `email` (trim_lowercase), `auth_subject` | `claim:` documents |
| access_tokens | `token_hash` | `token_hash` | `claim:` documents |
| invitations | `organization_id` + `email` while pending | `[organization_id, pending_email]` (cleared when accepted) | `claim:` documents |
| plans | `key` | `key` | fixed IDs + catalog lock |
| customers | `stripe_customer_id`, `organization_id` | each separately | org↔customer mirror documents |

- **Seeding:** plans are seeded with `create` and unique keys. A
  `unique_violation` on reseeding means "already present", so seeding stays
  idempotent with no catalog lock.
- **Data API:** user collections accept only ULID document IDs. Anything else
  is a 400 with the SDK's message.
- **Sessions page:** `SessionInfo.ID` is the session document's ULID.

The unique constraints are declared together with the collections, by whatever
provisions the dashboard's database (TS-529 bootstrap, and the test fixture).
That happens on empty collections, so it is always allowed.

## ts-go additions (slice 1)

- `Database.Schema(ctx, collection)` and
  `Database.DeclareUnique(ctx, collection, keys ...UniqueKey)`, where
  `UniqueKey` is `{Paths []string; Normalization string}`. They wrap the
  management schema route.
- `v1.ErrUniqueViolation`, matched from 409 `unique_violation`.

## Project registration across regions (upstream decision needed)

- **Today:** for each replica region, the dashboard registers a user's project
  under the same IDs. Under v1, a `create_project` that carries an ID requires
  the management service credential.
- **Why that fails:** TerraBase accepts the credential only on each region's
  loopback admin listener, and the dashboard runs on Fly. TerraBase does not
  copy organizations or projects between regions itself.
- **Why it doesn't scale:** a client registering per region also breaks
  "adding a region needs no code and no setup". At 20 regions, every client
  would have to track the region list.
- **Proposal for TS-803 / TerraBase:** TerraBase replicates organization and
  project registration to every region in the live registry. The dashboard
  creates the project once, in the home region.
  - Until that exists, the dashboard keeps single-region projects.
  - Replica placement returns a clear "not yet available" error, instead of
    a silent 403.

## Slices

Each slice stays at or under ~1,500 lines. `main`'s tests stay red until
slice 5, which vendors ts-go main and its ULID TerraBase pin. Until then,
every slice keeps `main` compiling and adds no new failures.

1. **ts-go:** add the schema/unique API and `ErrUniqueViolation`, tested
   against the pinned TerraBase.
2. **ts-dashboard:** move sessions, throttle and device grants to ULID IDs
   with natural-key fields and query lookups. This uses the SDK already
   vendored: until slice 5, uniqueness is an `assert_count` in the create
   transaction.
3. **ts-dashboard:** move users, access tokens and invitations the same way,
   and delete the `claim:` documents and records keys.
4. **ts-dashboard:** move plans, organizations and customers, and drop the
   catalog lock and mirror documents.
5. **ts-dashboard:** vendor ts-go main, declare the unique constraints in
   place of the `assert_count` guards, and make data API document IDs
   ULID-only. Adopt project registration per the upstream decision.

## Deploy order

- `main` deploys blue-green (#239): a build that fails `/health/ready`
  against live TerraBase never takes traffic.
- After slice 5, `main` still cannot serve production TerraBase 263598b4.
  Production keeps f190cbad8 until TS-677's cutover.
- The first deploy after the cutover ships the v1 dashboard on the wiped data.
