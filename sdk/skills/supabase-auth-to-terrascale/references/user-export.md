# Export Supabase Auth users and import them into TerraScale

The SQL runs against the Supabase database as `postgres` ([connecting to Postgres][sb-connect]); the `auth`
schema is described in [Supabase users][sb-users] and [managing user data][sb-user-data]. The import uses
`POST /v1/{project}/users/import` ([user profiles][profiles], [served OpenAPI][openapi]).

## Handling rules: PII and password hashes

- The export holds emails, names, avatars, metadata and bcrypt hashes. It is personal data and credential
  material. Work in a private directory outside every git repository, on an encrypted disk, with `umask 077`.
- A bcrypt hash is not a password, but it can be cracked offline. Treat `users.ndjson` like a password
  database: never attach it to a ticket, chat, CI artifact or backup; delete it with the steps in section 8.
- Never print a hash. Inventory queries print counts and prefixes only. The export writes to a file with
  `psql -o`; nothing goes to the terminal.
- Never select `auth.mfa_factors.secret`, anything from `auth.refresh_tokens` or `auth.sessions` except counts,
  `confirmation_token`, `recovery_token`, `email_change_token_*` or `reauthentication_token`.
- Hashes leave the machine only over HTTPS to `auth.terrascale.tech`. The import never returns them
  ([user profiles][profiles]).
- Plaintext passwords are never exported, asked for or moved. Supabase does not have them.

Set up the session once:

```sh
umask 077
mkdir -p "$HOME/supabase-auth-export" && cd "$HOME/supabase-auth-export"
export HISTFILE=/dev/null
test -n "$SUPABASE_DB_URL" && test -n "$TERRASCALE_PROJECT" && test -n "$TERRASCALE_ACCESS_TOKEN" && echo ready
```

`psql -X` skips `~/.psqlrc`, so no echo or logging setting there copies results to the terminal.

## 1. Inventory

Counts only. Save as `inventory.sql` and run `psql "$SUPABASE_DB_URL" -X -v ON_ERROR_STOP=1 -f inventory.sql`.
The queries use the current `auth` schema; on an old project without `auth.users.is_anonymous`, replace
`is_anonymous` with `false`.

```sql
SELECT count(*) AS users,
       count(*) FILTER (WHERE deleted_at IS NOT NULL) AS soft_deleted,
       count(*) FILTER (WHERE is_anonymous) AS anonymous,
       count(*) FILTER (WHERE is_sso_user) AS sso,
       count(*) FILTER (WHERE banned_until > now()) AS banned,
       count(*) FILTER (WHERE coalesce(email, '') = '') AS without_email,
       count(*) FILTER (WHERE coalesce(email, '') = '' AND coalesce(phone, '') <> '') AS phone_only,
       count(*) FILTER (WHERE email_confirmed_at IS NULL AND coalesce(email, '') <> '') AS email_unconfirmed,
       count(*) FILTER (WHERE coalesce(encrypted_password, '') <> '') AS with_password
FROM auth.users;

SELECT left(encrypted_password, 7) AS hash_prefix, length(encrypted_password) AS hash_length, count(*)
FROM auth.users
WHERE coalesce(encrypted_password, '') <> ''
GROUP BY 1, 2
ORDER BY 3 DESC;

SELECT provider, count(*) AS identities, count(DISTINCT user_id) AS users
FROM auth.identities
GROUP BY 1
ORDER BY 2 DESC;

SELECT factor_type, status, count(*) FROM auth.mfa_factors GROUP BY 1, 2 ORDER BY 1, 2;

SELECT (SELECT count(*) FROM auth.sso_providers) AS sso_providers,
       (SELECT count(*) FROM auth.saml_providers) AS saml_providers;

SELECT 'app' AS source, key, count(*) FROM auth.users, jsonb_object_keys(coalesce(raw_app_meta_data, '{}')) AS key GROUP BY 1, 2
UNION ALL
SELECT 'user', key, count(*) FROM auth.users, jsonb_object_keys(coalesce(raw_user_meta_data, '{}')) AS key GROUP BY 1, 2
ORDER BY 1, 3 DESC;

SELECT count(*) AS duplicate_email_groups
FROM (SELECT lower(btrim(email)) FROM auth.users
      WHERE coalesce(email, '') <> '' AND deleted_at IS NULL
      GROUP BY 1 HAVING count(*) > 1) AS duplicates;

SELECT count(DISTINCT user_id) AS users_with_sessions FROM auth.sessions WHERE not_after IS NULL OR not_after > now();

SELECT conrelid::regclass AS referencing_table, conname, pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE contype = 'f' AND confrelid = 'auth.users'::regclass AND connamespace <> 'auth'::regnamespace;

SELECT schemaname, tablename, policyname, cmd
FROM pg_policies
WHERE schemaname NOT IN ('auth', 'storage', 'realtime')
  AND (coalesce(qual, '') ~ 'auth\.' OR coalesce(with_check, '') ~ 'auth\.');

SELECT n.nspname AS schema, p.proname AS function
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE p.prokind = 'f'
  AND n.nspname NOT IN ('auth', 'storage', 'realtime', 'extensions', 'graphql', 'graphql_public', 'vault',
                        'pgsodium', 'net', 'supabase_functions', 'pg_catalog', 'information_schema')
  AND p.prosrc ~ 'auth\.';

SELECT tgname, pg_get_triggerdef(oid) AS definition
FROM pg_trigger
WHERE tgrelid = 'auth.users'::regclass AND NOT tgisinternal;
```

Read the results:

- `hash_prefix` should be `$2a$10$` (or another `$2a$`, `$2b$`, `$2y$` with cost 04 to 12) and `hash_length` 60.
  Anything else cannot be imported ([user profiles][profiles]); those users reset their password.
- A nonzero `duplicate_email_groups` must be resolved before the import: TerraScale emails are unique per
  project. List the groups' ids (not emails) with
  `SELECT lower(btrim(email)) AS e, array_agg(id ORDER BY last_sign_in_at DESC NULLS LAST) FROM auth.users WHERE coalesce(email, '') <> '' AND deleted_at IS NULL GROUP BY 1 HAVING count(*) > 1;`
  run with `-o duplicates.txt`, and decide per group which account keeps the address. SSO users can share an
  address with a password user ([Supabase SAML][sb-saml]).
- The policy, function and trigger lists are the work list for step 6 of the skill.

## 2. Export

Save as `export.sql`. It writes one JSON object per line, one per eligible user. Hashes are kept only for
confirmed, non-banned users without a verified MFA factor and with a canonical bcrypt hash; everyone else gets
`null`. A user whose TOTP used to stop a stolen password must not be opened by that password alone, so MFA users
sign in with an email code and enroll again. `fingerprint` is an MD5 over the fields that matter for sign-in
(hash, email, confirmation, ban, `app_metadata`, verified factors); the delta pass compares it, never the hash.

```sql
SELECT json_build_object(
  'supabase_id', u.id,
  'email', lower(btrim(u.email)),
  'email_confirmed', u.email_confirmed_at IS NOT NULL,
  'banned', coalesce(u.banned_until > now(), false),
  'password_hash', CASE
    WHEN u.email_confirmed_at IS NOT NULL
     AND coalesce(u.banned_until <= now(), true)
     AND NOT EXISTS (SELECT 1 FROM auth.mfa_factors f WHERE f.user_id = u.id AND f.status = 'verified')
     AND u.encrypted_password ~ '^\$2[aby]\$(0[4-9]|1[0-2])\$[./A-Za-z0-9]{21}[.Oeu][./A-Za-z0-9]{30}[.CGKOSWaeimquy26]$'
    THEN u.encrypted_password
  END,
  'fingerprint', md5(concat_ws('|',
    coalesce(u.encrypted_password, ''),
    coalesce(lower(btrim(u.email)), ''),
    coalesce(extract(epoch FROM u.email_confirmed_at)::text, ''),
    coalesce(extract(epoch FROM u.banned_until)::text, ''),
    coalesce(u.raw_app_meta_data::text, ''),
    (SELECT count(*) FROM auth.mfa_factors f WHERE f.user_id = u.id AND f.status = 'verified')::text)),
  'user_metadata', coalesce(u.raw_user_meta_data, '{}'::jsonb),
  'app_metadata', coalesce(u.raw_app_meta_data, '{}'::jsonb),
  'providers', (SELECT coalesce(json_agg(DISTINCT i.provider), '[]'::json) FROM auth.identities i WHERE i.user_id = u.id),
  'mfa_verified', EXISTS (SELECT 1 FROM auth.mfa_factors f WHERE f.user_id = u.id AND f.status = 'verified'),
  'created_at', u.created_at,
  'last_sign_in_at', u.last_sign_in_at
)
FROM auth.users u
WHERE u.deleted_at IS NULL
  AND NOT coalesce(u.is_anonymous, false)
  AND NOT u.is_sso_user
  AND coalesce(u.email, '') <> ''
ORDER BY u.created_at, u.id;
```

```sh
psql "$SUPABASE_DB_URL" -X -q -A -t -v ON_ERROR_STOP=1 -f export.sql -o users.ndjson
jq -r '[.supabase_id, .fingerprint] | @csv' users.ndjson > fingerprints.csv
wc -l users.ndjson
jq -s '{rows: length, with_hash: map(select(.password_hash != null)) | length, unconfirmed: map(select(.email_confirmed | not)) | length, banned: map(select(.banned)) | length, mfa: map(select(.mfa_verified)) | length}' users.ndjson
```

`-A -t` writes unaligned rows without headers, one JSON document per line; `-o` sends them to the file only.
The `jq` summary prints counts, never rows. The bcrypt pattern is the
same canonical form ts-auth checks ([source][src-hashes]), so the import should refuse no exported hash.
`fingerprints.csv` is written before any row is dropped, so a deliberately skipped user shows up in the delta
pass only when they change.

Excluded on purpose, each with its plan in the skill: soft-deleted users, anonymous users, SSO users (they
sign in through generic OIDC and get a new account, or you import them as ordinary rows after resolving their
duplicate addresses), and users without an email.

Decide about unconfirmed users now. If they have no rows in your tables, drop them:
`jq -c 'select(.email_confirmed)' users.ndjson > confirmed.ndjson && mv confirmed.ndjson users.ndjson`.
Otherwise they import without a hash and with `email_verified: false`.

## 3. Transform and import

Save as `import-users.mjs` in the same private directory and run `node import-users.mjs users.ndjson`. It is
resumable: rows already in `mapping.csv` are skipped. It disables each banned user as soon as its batch
returns, so the account is never usable, not even by email code. It sleeps 6.5 seconds between calls to stay under ten
calls a minute, waits on `429`, and stops on any other failure so you reconcile before sending again.

```js
import { appendFileSync, existsSync, readFileSync } from "node:fs";

const input = process.argv[2] ?? "users.ndjson";
const project = process.env.TERRASCALE_PROJECT;
const token = process.env.TERRASCALE_ACCESS_TOKEN;
if (!project || !token) {
  throw new Error("Set TERRASCALE_PROJECT and TERRASCALE_ACCESS_TOKEN.");
}

const base = `https://auth.terrascale.tech/v1/${encodeURIComponent(project)}`;
const endpoint = `${base}/users/import`;
const lines = (file) => (existsSync(file) ? readFileSync(file, "utf8").split("\n").filter(Boolean) : []);
const done = new Set(lines("mapping.csv").map((line) => line.split(",")[0]));
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const clean = (value) =>
  typeof value === "string"
    ? value.replace(/[\p{Cc}\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069]/gu, "").trim().slice(0, 255) || null
    : null;
const https = (value) =>
  typeof value === "string" && value.startsWith("https://") && value.length <= 2048 ? value : null;

const toRow = (user) => {
  const { provider, providers, ...appMetadata } = user.app_metadata;
  const { iss, sub, provider_id, email, email_verified, phone_verified, ...userMetadata } = user.user_metadata;
  return {
    email: user.email,
    email_verified: user.email_confirmed === true,
    ...(user.password_hash && user.email_confirmed && !user.banned && !user.mfa_verified
      ? { password_hash: user.password_hash }
      : {}),
    first_name: clean(user.user_metadata.given_name ?? user.user_metadata.full_name ?? user.user_metadata.name),
    last_name: clean(user.user_metadata.family_name),
    image_url: https(user.user_metadata.avatar_url ?? user.user_metadata.picture),
    external_id: user.supabase_id,
    private_metadata: { ...appMetadata, supabase_providers: user.providers },
    unsafe_metadata: userMetadata,
  };
};

const disable = async (id) => {
  const response = await fetch(`${base}/users/${encodeURIComponent(id)}/disable`, {
    method: "POST",
    headers: { authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new Error(`Disabling a banned user failed: HTTP ${response.status}. Run the section 5 disable loop now.`);
  }
};

const users = lines(input).map((line) => JSON.parse(line)).filter((user) => !done.has(user.supabase_id));
console.log(`${users.length} users to import`);

for (let start = 0; start < users.length; start += 500) {
  const batch = users.slice(start, start + 500);
  const response = await fetch(endpoint, {
    method: "POST",
    headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
    body: JSON.stringify({ users: batch.map(toRow) }),
  });
  if (response.status === 429) {
    await pause(1000 * Number(response.headers.get("retry-after") ?? 60));
    start -= 500;
    continue;
  }
  if (!response.ok) {
    throw new Error(`Stopped at row ${start}: HTTP ${response.status}. Reconcile (section 4) before running again.`);
  }
  const body = await response.json();
  for (const item of body.items) {
    appendFileSync("mapping.csv", `${batch[item.index].supabase_id},${item.user.id}\n`);
    if (batch[item.index].banned) {
      await disable(item.user.id);
    }
  }
  for (const error of body.errors) {
    const failed = { supabase_id: batch[error.index].supabase_id, code: error.code, message: error.message };
    appendFileSync("import-errors.ndjson", `${JSON.stringify(failed)}\n`);
  }
  console.log(`rows ${start + 1}-${start + batch.length}: ${body.items.length} imported, ${body.errors.length} refused`);
  await pause(6500);
}
```

What the transform does, and why:

- `email_verified` is `true` only for confirmed addresses. A hash is sent only for confirmed, non-banned users,
  checked twice (SQL and script).
- `external_id` keeps the Supabase UUID, so a second submission of the same user is refused as `user_conflict`
  instead of creating a duplicate ([user profiles][profiles]).
- Supabase-managed keys (`provider`, `providers` in `app_metadata`; `iss`, `sub`, `provider_id`, `email`,
  `email_verified`, `phone_verified` in `user_metadata`) are dropped; the provider list is kept in
  `private_metadata.supabase_providers` so support can tell a social-only user what to do.
- `first_name` takes `given_name`, else the full name; split names yourself if your data allows it. Control and
  bidirectional formatting characters are removed because the import refuses them ([user profiles][profiles]).
- `mapping.csv` holds ids only, no emails. `import-errors.ndjson` holds the Supabase id, the code and the
  server's message, which never contains a hash.

Errors to expect:

| Code | Cause | Fix |
| --- | --- | --- |
| `invalid_request` mentioning `password_hash` | Hash outside the accepted formats | Re-import the row without the hash; the user resets the password |
| `invalid_request` on a profile field or metadata | Too long, control characters, metadata over 8192 bytes or depth 10 | Trim the field and re-import the row |
| `user_conflict` | Email or `external_id` already in the project | Section 4 |

To re-import selected rows, filter them into a new file and run the script on it, for example
`jq -c 'del(.password_hash)' retry.ndjson > retry-no-hash.ndjson`.

## 4. Reconcile

Every `user_conflict`, and every run that stopped on an error, is reconciled before anything is resent. The
directory export lists ids and emails ([end-user auth][eua]); a user's `external_id` comes from
`GET /v1/{project}/users/{user}` ([user profiles][profiles]). Save as `reconcile.mjs` and run
`node reconcile.mjs users.ndjson`:

```js
import { appendFileSync, existsSync, readFileSync } from "node:fs";

const input = process.argv[2] ?? "users.ndjson";
const project = process.env.TERRASCALE_PROJECT;
const token = process.env.TERRASCALE_ACCESS_TOKEN;
const base = `https://auth.terrascale.tech/v1/${encodeURIComponent(project)}`;
const lines = (file) => (existsSync(file) ? readFileSync(file, "utf8").split("\n").filter(Boolean) : []);
const get = async (path) => {
  const response = await fetch(base + path, { headers: { authorization: `Bearer ${token}` } });
  if (!response.ok) {
    throw new Error(`GET ${path.split("?")[0]}: HTTP ${response.status}`);
  }
  return response.json();
};

const mapped = new Set(lines("mapping.csv").map((line) => line.split(",")[0]));
const byEmail = new Map();
let after = null;
do {
  const page = await get(`/users/export?limit=100${after ? `&after=${encodeURIComponent(after)}` : ""}`);
  for (const user of page.items) {
    byEmail.set(user.email, user.id);
  }
  after = page.next;
} while (after);

let added = 0;
let review = 0;
let missing = 0;
for (const user of lines(input).map((line) => JSON.parse(line))) {
  if (mapped.has(user.supabase_id)) {
    continue;
  }
  const id = byEmail.get(user.email);
  if (!id) {
    missing += 1;
    appendFileSync("not-imported.txt", `${user.supabase_id}\n`);
    continue;
  }
  const account = await get(`/users/${encodeURIComponent(id)}`);
  if (account.external_id === user.supabase_id) {
    appendFileSync("mapping.csv", `${user.supabase_id},${id}\n`);
    added += 1;
  } else {
    appendFileSync("review.csv", `${user.supabase_id},${id}\n`);
    review += 1;
  }
}
console.log(`mapped ${added}, needs review ${review}, not imported ${missing}`);
```

- **mapped**: imported earlier, response lost. Nothing else to do.
- **review** (`review.csv`): the address belongs to an account that is not this Supabase user, for example
  someone signed up while sign-up was on. Never merge them automatically: that hands one person's data to
  another. Contact the address owner, or delete the stray account if it has no activity and import again.
- **not imported** (`not-imported.txt`): fix the error in `import-errors.ndjson` and import those rows again.

Done when `wc -l < mapping.csv` plus the rows you chose to skip equals `wc -l < users.ndjson`.

## 5. Claims and banned users

The docs' `api` helper keeps the token out of process arguments ([end-user auth][eua]):

```bash
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
ts_id() { awk -F, -v s="$1" '$1 == s { print $2 }' mapping.csv; }

USERS="${USERS:-users.ndjson}"

jq -r 'select(.banned) | .supabase_id' "$USERS" | while read -r sid; do
  id="$(ts_id "$sid")"
  [ -n "$id" ] && api -X POST "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT/users/$id/disable" > /dev/null && echo "disabled $id"
done

jq -c 'select(.app_metadata.role != null) | {sid: .supabase_id, claims: {role: .app_metadata.role}}' "$USERS" | while read -r row; do
  id="$(ts_id "$(jq -r .sid <<< "$row")")"
  [ -n "$id" ] && jq -c '{custom_claims: .claims}' <<< "$row" |
    api -X PATCH -H 'Content-Type: application/json' --data-binary @- \
      "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT/users/$id" > /dev/null && echo "claims $id"
done
```

Replace `role` with the keys your policies and JWT checks read. `custom_claims` replaces the whole object on
each call; names match `[a-z][a-z0-9_]{0,62}`; at most 32 members and 1000 bytes; values are visible to the
user, so never put a secret in them ([user profiles][profiles]).

The import script already disabled banned users batch by batch; the loop above catches the ones mapped later
by reconciliation (lost responses). Run it right after reconciliation. Run both loops with
`USERS=delta.ndjson` after each delta pass.

## 6. Map user ids and foreign keys

Load the map into the Supabase database in a schema the Data API does not expose, then add a TerraScale id
column next to each user column. The columns are additive, so Supabase keeps working for a rollback, and the
database move (`supabase-database-to-terrascale`) carries them like any other column.

```sql
CREATE SCHEMA IF NOT EXISTS migration;
REVOKE ALL ON SCHEMA migration FROM anon, authenticated;
CREATE TABLE migration.auth_user_map (supabase_id uuid PRIMARY KEY, terrascale_id text NOT NULL UNIQUE);
```

Load it, and reload it after every delta pass:

```sh
psql "$SUPABASE_DB_URL" -X -v ON_ERROR_STOP=1 -c "TRUNCATE migration.auth_user_map" -c "\copy migration.auth_user_map (supabase_id, terrascale_id) FROM 'mapping.csv' WITH (FORMAT csv)"
```

For each foreign key from the inventory (here `public.todos.user_id`):

```sql
ALTER TABLE public.todos ADD COLUMN IF NOT EXISTS user_ts_id text;

UPDATE public.todos t
SET user_ts_id = m.terrascale_id
FROM migration.auth_user_map m
WHERE m.supabase_id = t.user_id
  AND t.user_ts_id IS DISTINCT FROM m.terrascale_id;

UPDATE public.todos t
SET user_ts_id = NULL
WHERE t.user_ts_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM migration.auth_user_map m WHERE m.terrascale_id = t.user_ts_id);

SELECT count(*) AS orphaned_rows, count(DISTINCT user_id) AS orphaned_owners
FROM public.todos
WHERE user_id IS NOT NULL AND user_ts_id IS NULL;
```

Orphaned rows belong to users who were not imported (phone-only, anonymous, deleted, skipped). The data owner
decides: delete them, keep them without an owner, or contact the user before the cutover. Run both `UPDATE`s
again during the cutover for rows written since the last run and for users the delta pass re-imported (the
second one clears ids of deleted TerraScale accounts).

In the TerraScale schema, the TerraScale id column becomes the owner column your rules and code use
(`auth.uid` is the token's `sub`, [rules][rules]); drop the `REFERENCES auth.users` constraint there.

## 7. Delta pass before the switch

Hiding password and email changes in your app does not stop them: Supabase's auth API keeps accepting password
resets and `updateUser` calls from old app versions and from anyone with the anon key. No TerraScale route can
replace a hash after import, so every user who changed after their export is deleted at TerraScale and imported
again, while no TerraScale data exists for them yet. Repeat until a pass finds nobody.

Save as `delta.sql`. It prints Supabase ids only: users whose fingerprint changed, users deleted or no longer
eligible, and users created since the export.

```sql
CREATE TEMP TABLE imported (id uuid PRIMARY KEY, fingerprint text NOT NULL);
\copy imported FROM 'fingerprints.csv' WITH (FORMAT csv)
WITH eligible AS (
  SELECT u.id, md5(concat_ws('|',
      coalesce(u.encrypted_password, ''),
      coalesce(lower(btrim(u.email)), ''),
      coalesce(extract(epoch FROM u.email_confirmed_at)::text, ''),
      coalesce(extract(epoch FROM u.banned_until)::text, ''),
      coalesce(u.raw_app_meta_data::text, ''),
      (SELECT count(*) FROM auth.mfa_factors f WHERE f.user_id = u.id AND f.status = 'verified')::text)) AS fingerprint
  FROM auth.users u
  WHERE u.deleted_at IS NULL
    AND NOT coalesce(u.is_anonymous, false)
    AND NOT u.is_sso_user
    AND coalesce(u.email, '') <> ''
)
SELECT coalesce(e.id, i.id)
FROM eligible e FULL JOIN imported i ON i.id = e.id
WHERE e.id IS NULL OR i.id IS NULL OR e.fingerprint <> i.fingerprint;
```

Save as `delta.sh` and run `bash delta.sh` from the private directory. It stops at the first failed delete, so a
user is never re-imported next to an account that still has the old hash.

```bash
set -euo pipefail
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
ts_id() { awk -F, -v s="$1" '$1 == s { print $2 }' mapping.csv; }

psql "$SUPABASE_DB_URL" -X -q -A -t -v ON_ERROR_STOP=1 -f delta.sql -o changed.txt
echo "changed: $(wc -l < changed.txt)"
if [ ! -s changed.txt ]; then exit 0; fi

while read -r sid; do
  id="$(ts_id "$sid")"
  if [ -n "$id" ]; then
    api -X DELETE "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT/users/$id" > /dev/null
    echo "deleted $id"
  fi
done < changed.txt

grep -v -F -f changed.txt mapping.csv > mapping.tmp || true
mv mapping.tmp mapping.csv
grep -v -F -f changed.txt fingerprints.csv > fingerprints.tmp || true
mv fingerprints.tmp fingerprints.csv

psql "$SUPABASE_DB_URL" -X -q -A -t -v ON_ERROR_STOP=1 -f export.sql -o fresh.ndjson
jq -c --rawfile ids changed.txt 'select(.supabase_id as $s | $ids | split("\n") | index($s))' fresh.ndjson > delta.ndjson
rm -f fresh.ndjson
jq -r '[.supabase_id, .fingerprint] | @csv' delta.ndjson >> fingerprints.csv
echo "to re-import: $(wc -l < delta.ndjson)"
```

Then, with the same decisions as the first run (drop unconfirmed users from `delta.ndjson` if you dropped
them before):

1. `node import-users.mjs delta.ndjson`, then `node reconcile.mjs delta.ndjson`.
2. Section 5 with `USERS=delta.ndjson`.
3. Section 6: reload `auth_user_map` and run both `UPDATE`s.
4. `bash delta.sh` again. **STOP**: do not switch the application until it prints `changed: 0`.

Run one more pass right after the switch. A user it finds changed in Supabase during the last minutes may
already have TerraScale data, so do not delete them blindly: disable the account at once
(`POST /v1/{project}/users/{user}/disable`, [end-user auth][eua]), let support confirm the person, then delete,
re-import and rewrite that user's TerraScale rows from the old id to the new one.

## 8. Cut over and roll back

Stop clients that still hold Supabase sessions from writing once the application has switched. In the
Supabase database:

```sql
REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public FROM anon, authenticated;
```

Roll back the revoke by granting the same privileges again to the roles that had them; record the existing
grants first with
`SELECT grantee, table_name, privilege_type FROM information_schema.role_table_grants WHERE table_schema = 'public' AND grantee IN ('anon', 'authenticated');`
saved with `-o grants-before.txt`.

Delete an imported user in a failed rehearsal or a restart with `DELETE /v1/{project}/users/{user}`
([end-user auth][eua]). Deleting changes nothing in Supabase.

Rolling back after TerraScale writes, once the data is back in Supabase, goes in this order. First list the
users who exist only at TerraScale: the directory export ([end-user auth][eua]) minus the ids in `mapping.csv`,
saved as `new-users.csv` (`terrascale_id,email`). Create them in Supabase with the admin API
([Supabase admin createUser][sb-admin]) and record the new UUIDs. Save as `rollback-users.mjs`, run
`npm install @supabase/supabase-js` in the private directory, and run it with `SUPABASE_URL` and
`SUPABASE_SERVICE_ROLE_KEY` supplied privately:

```js
import { appendFileSync, readFileSync } from "node:fs";
import { createClient } from "@supabase/supabase-js";

const supabase = createClient(process.env.SUPABASE_URL, process.env.SUPABASE_SERVICE_ROLE_KEY, {
  auth: { autoRefreshToken: false, persistSession: false },
});

for (const line of readFileSync("new-users.csv", "utf8").split("\n").filter(Boolean)) {
  const [terrascaleId, email] = line.split(",");
  const { data, error } = await supabase.auth.admin.createUser({ email, email_confirm: true });
  if (error) {
    throw new Error(`createUser failed for TerraScale user ${terrascaleId}: ${error.message}`);
  }
  appendFileSync("rollback-map.csv", `${data.user.id},${terrascaleId}\n`);
}
```

`email_confirm: true` is right for them: TerraScale sign-ups proved their address at account creation
([end-user auth][eua]). Then add them to the map and point every user column back to Supabase ids:

```sh
psql "$SUPABASE_DB_URL" -X -v ON_ERROR_STOP=1 -c "\copy migration.auth_user_map (supabase_id, terrascale_id) FROM 'rollback-map.csv' WITH (FORMAT csv)"
```

```sql
UPDATE public.todos t
SET user_id = m.supabase_id
FROM migration.auth_user_map m
WHERE m.terrascale_id = t.user_ts_id
  AND t.user_id IS DISTINCT FROM m.supabase_id;

SELECT count(*) AS unmapped_rows FROM public.todos WHERE user_ts_id IS NOT NULL AND user_id IS NULL;
```

`unmapped_rows` must be zero. Finally ask every rolled-back user to reset the password: TerraScale exports no
hashes, so a password set at TerraScale does not come back, and Supabase still accepts the old one.

## 9. Clean up

After the rollback window closes:

```sh
shred -u users.ndjson 2>/dev/null || rm -P users.ndjson 2>/dev/null || rm -f users.ndjson
shred -u fingerprints.csv delta.ndjson 2>/dev/null || rm -f fingerprints.csv delta.ndjson
rm -f import-errors.ndjson review.csv not-imported.txt duplicates.txt changed.txt new-users.csv rollback-map.csv
```

`shred` is GNU coreutils, `rm -P` is BSD and macOS; on SSDs and copy-on-write file systems neither guarantees
erasure, which is why the work directory belongs on an encrypted disk. Keep `mapping.csv` and
`migration.auth_user_map` (ids only) as long as you keep the old UUID columns, then drop them:
`DROP SCHEMA migration CASCADE;`.

[eua]: https://docs.terrascale.tech/guides/end-user-authentication
[openapi]: https://auth.terrascale.tech/openapi/v1.json
[profiles]: https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md
[rules]: https://github.com/TerraScale/terrascale/blob/main/docs/terrabase/rules.md
[sb-connect]: https://supabase.com/docs/guides/database/connecting-to-postgres
[sb-users]: https://supabase.com/docs/guides/auth/users
[sb-user-data]: https://supabase.com/docs/guides/auth/managing-user-data
[sb-saml]: https://supabase.com/docs/guides/auth/enterprise-sso/auth-sso-saml
[sb-admin]: https://supabase.com/docs/reference/javascript/auth-admin-createuser
[src-hashes]: https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication.Security/ImportedPasswords.cs
