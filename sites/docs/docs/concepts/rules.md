---
title: Rules
---

# Rules

Rules let signed-in users read and write a TerraBase database directly from a browser. You deploy one JSON rules set per database. It decides which rows and columns each user can read, which values they can write, and which changes are valid.

A database without rules denies every end-user request. A missing operation denies that operation. Project credentials have broader access and are not restricted by these rules: keep them out of your browser app.

Start with [Build a todo app with no backend](/getting-started/todo-app) for a complete browser example.

## Allow an operation

This rules set gives each user their own todos:

```json
{
  "tables": {
    "todos": {
      "read": "row.owner_id = auth.uid",
      "create": "new.owner_id = auth.uid",
      "update": "row.owner_id = auth.uid",
      "delete": "row.owner_id = auth.uid",
      "columns": ["id", "owner_id", "title", "done"],
      "writable": ["title", "done"],
      "validate": ["length(new.title) BETWEEN 1 AND 200", "new.done IN (0, 1)"],
      "set": {
        "create": {"id": "request.ulid", "owner_id": "auth.uid"}
      }
    }
  }
}
```

| Condition | What it checks |
| --- | --- |
| `read` | Which stored rows the user can read |
| `create` | Whether the new row may be created |
| `update` | Whether a stored row may be changed |
| `delete` | Whether a stored row may be removed |

`row` is the stored row before a write. `new` is the complete row after a create or update, including defaults and server-set values. An update body contains only the fields to change; `new` also contains the unchanged fields. Updates and deletes require the existing row to be readable.

Conditions use SQL expressions with qualified names such as `row.owner_id` and `new.title`. Comparisons, `AND`, `OR`, `IN`, `CASE`, `CAST` and deterministic functions are supported. Subqueries and author-supplied parameters such as `:user` are refused. Only true allows an operation; false and NULL deny it.

## Choose columns and validate writes

`columns` lists the columns end users receive. If omitted, every stored column is readable. A client may request fewer columns, but it cannot select or filter on a hidden column.

`writable` lists the columns a client may submit on create or update. Without it, none are writable. `set.create` and `set.update` fill values on the server. A column cannot be both writable and server-set; a client that submits a server-set column gets `column_not_writable`.

In the example, the server generates a ULID `id` and fills `owner_id` from the signed-in user. A client sends only `title` and `done`, so it cannot choose another owner or transfer ownership.

Every `validate` condition must hold after a create or update. A failed condition cancels the write. For example, an empty title fails validation even when the user owns the row. Validation checks the stored values, including defaults and trigger changes.

You can also set timestamps:

```json
{
  "create": {"created_at": "request.time"},
  "update": {"updated_at": "request.time"}
}
```

Use this object as `set` on a table with those timestamp columns. `request.time` is server time in Unix milliseconds. `request.ulid` is available only in `set.create`. Read conditions cannot use `request.time`; update or delete expired rows to change their visibility.

## Identity and trusted claims

Rules use the user's project sign-in access token. Guests are signed-in users too. A request without a token gets `401`; an ID token does not authorize data access. End users cannot run SQL or manage rules.

| Reference | Value |
| --- | --- |
| `auth.uid` | The user's subject ID |
| `auth.is_anonymous` | `1` for a guest, otherwise `0` |
| `auth.org_id` | The selected project organization, or NULL |
| `auth.org_role` | `admin`, `member`, or NULL |
| `auth.claims.<name>` | A trusted custom claim, or NULL when absent |

An administrator sets a user's `custom_claims` through `PATCH /v1/{project}/users/{user}` with `auth:write` or project admin access. The object replaces the previous claims; `null` clears it. Claims reach rules at the user's next token refresh. Profile metadata and values supplied by the browser do not become trusted claims.

```json
{"custom_claims": {"plan": "pro", "roles": ["editor"]}}
```

For example, use `auth.claims.plan = 'pro'` in a condition, or `'editor' IN auth.claims.roles` for a list. Claim names use lowercase letters, digits and underscores, starting with a letter. Claims are limited to 32 members and 1000 serialized bytes; values may be scalars or lists of strings and numbers. Never store secrets in claims: the token holder can read them.

Missing claims are NULL. Both `auth.claims.plan = 'pro'` and its negation deny when `plan` is missing. Use an explicit condition such as `coalesce(auth.claims.plan, 'free') = 'free'` when absence should allow access. A missing list also makes both `IN` and `NOT IN` deny.

### Custom tokens

If your backend already authenticates users, it can mint a short-lived data access token:

```http
POST https://auth.terrascale.tech/v1/{project}/custom-tokens
Authorization: Bearer <project credential with auth:tokens:mint>
Content-Type: application/json

{"user_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","database":"01ARZ3NDEKTSV4RRFFQ69G5FAW","claims":{"plan":"pro"}}
```

The response contains `access_token`, `token_type` and `expires_in`. Tokens last at most five minutes. `user_id` must be a canonical uppercase ULID. Omit `database` to let the token reach any database in the project whose rules allow it; otherwise it reaches only that database. Custom tokens have no selected organization and are not guests.

Keep the minting credential on your backend. It can impersonate any user of the project. Rules apply to the returned token exactly as they do to project sign-in tokens.

## Test a rule

Open the database's **Rules** page in the dashboard. In **Playground**, choose an operation and table, supply **Auth**, **Row** and **New**, then select **Run test**. The playground evaluates the editor's draft without deploying it.

The API simulator is `POST /v1/projects/{project}/databases/{database}/rules:test`, using a project credential with `databases:read`:

```json
{
  "operation": "update",
  "table": "todos",
  "auth": {"uid": "alice", "is_anonymous": false},
  "row": {"owner_id": "bob", "title": "Keep this", "done": 0},
  "new": {"title": "Changed"}
}
```

With the ownership rules above, this returns `allowed: false`. The result includes `decidedBy`, `clause`, each condition's `results`, readable `columns` and any `problems`. Send a `rules` object to evaluate a draft instead of the deployed rules. For an update, `new` is a patch merged over `row`.

The simulator reads or writes no application rows. It evaluates the supplied values with server-set fields, but does not reproduce stored defaults, column conversions or triggers. Check actual browser writes as well. The [todo quickstart](/getting-started/todo-app#test-the-rule) shows the equivalent CLI commands.

## Deploy and keep versions

Deploy in the dashboard with **Deploy rules**, or use `ts rules deploy <database>` with `terrascale.rules.json`. Deployment validates every condition against the current table schema. Invalid rules return `invalid_rules` with a problem list. A later schema change that breaks a rule denies affected requests with `rules_invalid`; correct and redeploy the rules.

For direct API calls, read `GET /v1/projects/{project}/databases/{database}/rules`. It returns the rules and an `ETag` containing their version. Send that exact ETag with the next deployment:

```http
PUT /v1/projects/{project}/databases/{database}/rules
If-Match: "<current version>"
Content-Type: application/json
Authorization: Bearer <project credential with databases:write>

{"tables":{}}
```

This example denies all end-user operations. On the first deployment, use `If-None-Match: *` instead of `If-Match`. Missing preconditions return `428 precondition_required`; a stale version returns `412 version_mismatch`. Read the current rules, review the changes and submit against that version.

Each successful deployment creates a version. The last 100 are retained. List them with `GET …/rules/versions`, read one with `GET …/rules/versions/{version}`, or restore one with `POST …/rules/versions/{version}/restore`. Restore needs the same precondition and validates against today's schema; it creates a new version. The dashboard provides history, diffs and restore.

Writes use new rules on their next request. Reads use them when replication reaches the serving region. Live shapes refetch when their rules change. Disabling a user or changing claims does not invalidate an already issued access token immediately: allow up to five minutes plus 30 seconds of clock skew. Data already delivered to a client remains with that client.

## Handle errors

| Status and code | Action |
| --- | --- |
| `401` | Sign in again or obtain a current access token with the correct project audience |
| `403 rule_denied` | Check the operation's condition and the user's identity |
| `403 column_not_readable` | Remove the hidden column from selections and filters |
| `403 column_not_writable` | Submit only writable fields; omit server-set fields |
| `403 rules_invalid` | Fix rules that no longer match the schema |
| `403 forbidden` | Use project credentials for SQL and management |
| `400 validation_failed` | Correct the value; `details.clause` identifies the validation index |
| `400 invalid_rules` | Fix the entries in `details.problems` |
| `400 unknown_column`, `constraint_failed`, `invalid_request` | Correct the body or violated table constraint |
| `404 row_not_found` | The row is absent or hidden; updates and deletes also require read access |
| `404 unknown_database`, `unknown_table`, `no_rules`, `unknown_version` | Check the resource or deploy rules first |
| `409 row_exists` | A unique value is already present |
| `409 must_refetch` | Refetch the shape with current authorization |
| `412 version_mismatch` | Read the latest rules before deploying |
| `428 precondition_required` | Supply `If-Match` or `If-None-Match` |
| `421 wrong_region` | Nothing was applied; send the request to a region of the database's set |
| `503 write_outcome_unknown` | Inspect the result before another write; it may have committed |

A denied request does not disclose its condition text. Use the owner-only simulator to diagnose it. After a timeout or unknown write outcome, do not automatically replay a mutation.
