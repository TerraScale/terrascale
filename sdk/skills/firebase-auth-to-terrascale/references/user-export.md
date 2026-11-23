# Export, transform and import Firebase users

This reference moves the Firebase user directory into a TerraScale project and
builds the mapping table. It never moves a password: TerraScale accepts only
bcrypt, Argon2id PHC and Django PBKDF2-SHA256 hashes on import, and Firebase uses
its own modified scrypt
([user profiles and imports](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md#import),
[Firebase CLI auth reference](https://firebase.google.com/docs/cli/auth)).

## PII and secret handling

The export contains emails, phone numbers, names, photo URLs, provider ids and,
for scrypt accounts, password hashes and salts. Treat it like a database dump.

- Work in an encrypted, access-controlled location outside every repository.
  Add the file names to the global ignore list before you export.
- Never paste rows into tickets, chats or AI prompts. Logs show counts and uids,
  never emails or hashes.
- The password hash parameters (base64 signer key, base64 salt separator, rounds,
  memory cost) are shown in the Firebase console under Authentication > Users >
  the menu above the user list, and Firebase marks them sensitive. TerraScale does
  not use them. Copy them only if you must keep a rollback path into another
  Firebase or Identity Platform project, and then only into a secret manager as
  `FIREBASE_SCRYPT_SIGNER_KEY`, `FIREBASE_SCRYPT_SALT_SEPARATOR`,
  `FIREBASE_SCRYPT_ROUNDS` and `FIREBASE_SCRYPT_MEM_COST`. Never echo them, pass
  them as visible command arguments in shared terminals, or commit them.
- Strip `passwordHash` and `salt` from every derived file. Only the original
  export keeps them, for rollback.
- Delete the export, the derived files and any copied hash parameters on the
  retention date in the migration plan, and record the deletion.
- TerraScale's directory export is itself audited as `project.users_exported` per
  page ([export the directory](https://docs.terrascale.tech/guides/end-user-authentication#export-the-directory)).

## 1. Export from Firebase

~~~sh
firebase login
firebase auth:export users.json --format=json --project "$FIREBASE_PROJECT_ID"
~~~

`auth:export ACCOUNT_FILE --format=json|csv` is documented in the
[auth reference](https://firebase.google.com/docs/cli/auth); `--project` is the
CLI's global project option ([Firebase CLI](https://firebase.google.com/docs/cli)).
The documented record fields are `localId`, `email`, `emailVerified`,
`passwordHash`, `salt`, `displayName`, `photoUrl`, `createdAt`, `lastSignedInAt`,
`phoneNumber` and `providerUserInfo` (`providerId`, `rawId`, `email`,
`displayName`, `photoUrl`). Only scrypt accounts have a hash.

The reference does not list custom claims, MFA enrollments, the disabled flag or
tenant users, so read those with the Admin SDK `listUsers`
([list all users](https://firebase.google.com/docs/auth/admin/manage-users#list_all_users)).
Run this on a trusted machine with `GOOGLE_APPLICATION_CREDENTIALS` pointing at a
service account key, and set `FIREBASE_TENANT_ID` for one Identity Platform tenant:

~~~js
import { writeFileSync } from "node:fs";
import { initializeApp, applicationDefault } from "firebase-admin/app";
import { getAuth } from "firebase-admin/auth";

initializeApp({ credential: applicationDefault() });
const tenant = process.env.FIREBASE_TENANT_ID;
const auth = tenant ? getAuth().tenantManager().authForTenant(tenant) : getAuth();

const extras = {};
let pageToken;
do {
  const page = await auth.listUsers(1000, pageToken);
  for (const user of page.users) {
    extras[user.uid] = {
      disabled: user.disabled,
      customClaims: user.customClaims ?? null,
      mfa: (user.multiFactor?.enrolledFactors ?? []).map((factor) => factor.factorId),
    };
  }
  pageToken = page.pageToken;
} while (pageToken);

writeFileSync("extras.json", JSON.stringify(extras));
console.log(`users: ${Object.keys(extras).length}`);
~~~

For multi-tenant projects, export each tenant with this script (setting
`FIREBASE_TENANT_ID`) and keep each tenant's files apart.

## 2. Freeze, re-export and repeat

The first export is a rehearsal. On cutover day, block new Firebase accounts with
a `beforeUserCreated` blocking function
([blocking functions](https://firebase.google.com/docs/auth/extend-with-blocking-functions)),
export again, and run the transform with the previous `mapping.json`. Every run
writes to a **new output folder** (`run-1`, `run-2`, ...), keeps every
`app_user_id` it assigned before, and emits rows only for uids that are not
imported yet. Run it again during the bridge whenever users verify their email
on Firebase.

From the first export until the bridge closes, turn off email changes in the
Firebase app. `PATCH /v1/{project}/users/{user}` cannot change an email
([user profiles](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md)),
so a Firebase email change after import would leave the TerraScale account on the
old mailbox, and whoever holds that mailbox could sign in with an email code.

## 3. Transform

The mapping file is the migration ledger: one row per Firebase uid with
`app_user_id` (a ULID that never changes), `status` and `reason`. The rules:

- **Each uid gets its own `app_user_id`.** Rewrite data ownership to it once.
- **Only accounts with `emailVerified: true` are imported.** An unverified account
  is skipped as `unverified` or, when another account shares its email,
  `unverified_duplicate`. It keeps its `app_user_id` and keeps working on Firebase
  during the bridge. If its holder verifies the address there, the next run
  imports it. Importing it earlier would let whoever holds the mailbox (who may
  not be the person who created the Firebase account) inherit its data.
- **Merges happen only between verified accounts.** Verified accounts that share
  an email become one TerraScale user. The most recent sign-in is imported, and
  the others get `status: "merged"` and `merged_into` (the survivor's
  `app_user_id`). Their data moves to `merged_into` in an explicit data step;
  identities are never merged silently.
- **Users without an email** are skipped as `phone_only` or `no_email`.
- **Changes since the last run** go to `changed.json`: `email` (the address
  changed), `email_verified` (verification changed) or `deleted` (gone from
  Firebase).

Save the script as `transform.mjs` outside the repository. The first run passes
`-` as the previous mapping:

~~~sh
node transform.mjs users.json extras.json - run-1
node transform.mjs users-2.json extras-2.json run-1/mapping.json run-2
~~~

~~~js
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { randomBytes } from "node:crypto";

const [exportFile, extrasFile, previousFile, outDir] = process.argv.slice(2);
if (!outDir || existsSync(outDir)) {
  throw new Error("usage: transform.mjs users.json extras.json <previous mapping.json | -> <new output folder>");
}
const users = JSON.parse(readFileSync(exportFile, "utf8")).users;
const extras = JSON.parse(readFileSync(extrasFile, "utf8"));
const previous = previousFile === "-" ? [] : JSON.parse(readFileSync(previousFile, "utf8"));
mkdirSync(outDir, { recursive: true });

const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
function ulid() {
  let time = Date.now();
  let head = "";
  for (let i = 0; i < 10; i++) {
    head = alphabet[time % 32] + head;
    time = Math.floor(time / 32);
  }
  const bytes = randomBytes(16);
  let tail = "";
  for (let i = 0; i < 16; i++) tail += alphabet[bytes[i] & 31];
  return head + tail;
}

const key = (email) => email.trim().toLowerCase();
const httpsUrl = (value) =>
  typeof value === "string" && value.startsWith("https://") && value.length <= 2048 ? value : undefined;
const done = (row) => row?.status === "imported" || row?.status === "merged";

const mapping = new Map(previous.map((row) => [row.firebase_uid, row]));
const byUid = new Map(users.map((user) => [user.localId, user]));

const changed = [];
for (const row of mapping.values()) {
  if (!done(row)) continue;
  const user = byUid.get(row.firebase_uid);
  if (!user) changed.push({ firebase_uid: row.firebase_uid, change: "deleted" });
  else if (!user.email || key(user.email) !== row.email_key) changed.push({ firebase_uid: row.firebase_uid, change: "email" });
  else if ((user.emailVerified === true) !== row.email_verified) changed.push({ firebase_uid: row.firebase_uid, change: "email_verified" });
}

const owners = new Map([...mapping.values()].filter((row) => row.status === "imported").map((row) => [row.email_key, row]));
const emailCount = new Map();
for (const user of users) if (user.email) emailCount.set(key(user.email), (emailCount.get(key(user.email)) ?? 0) + 1);

const pending = new Map();
for (const user of users) {
  const before = mapping.get(user.localId);
  if (done(before)) continue;
  const row = {
    firebase_uid: user.localId,
    app_user_id: before?.app_user_id ?? ulid(),
    email_key: user.email ? key(user.email) : null,
    email_verified: user.emailVerified === true,
    status: "skipped",
    reason: null,
    merged_into: null,
    terrascale_user_id: null,
  };
  mapping.set(user.localId, row);
  if (!user.email) row.reason = user.phoneNumber ? "phone_only" : "no_email";
  else if (!row.email_verified) row.reason = emailCount.get(row.email_key) > 1 ? "unverified_duplicate" : "unverified";
  else pending.set(row.email_key, [...(pending.get(row.email_key) ?? []), user]);
}

const rows = [];
for (const [email, group] of pending) {
  group.sort((a, b) => Number(b.lastSignedInAt ?? 0) - Number(a.lastSignedInAt ?? 0));
  const owner = owners.get(email);
  const survivor = owner ? null : group[0];
  for (const user of group) {
    const row = mapping.get(user.localId);
    if (user !== survivor) {
      row.status = "merged";
      row.merged_into = (owner ?? mapping.get(survivor.localId)).app_user_id;
      row.terrascale_user_id = owner?.terrascale_user_id ?? null;
      continue;
    }
    const extra = extras[user.localId] ?? {};
    row.status = "pending_import";
    rows.push({
      email: user.email.trim(),
      email_verified: true,
      external_id: user.localId,
      first_name: user.displayName ? user.displayName.slice(0, 255) : undefined,
      image_url: httpsUrl(user.photoUrl),
      private_metadata: {
        firebase_providers: (user.providerUserInfo ?? []).map((info) => info.providerId),
        firebase_mfa: (extra.mfa ?? []).length > 0,
        firebase_disabled: extra.disabled === true,
      },
    });
  }
}

for (let i = 0; i < rows.length; i += 500) {
  writeFileSync(`${outDir}/batch-${String(i / 500).padStart(4, "0")}.json`, JSON.stringify({ users: rows.slice(i, i + 500) }));
}
const ledger = [...mapping.values()];
writeFileSync(`${outDir}/mapping.json`, JSON.stringify(ledger));
writeFileSync(`${outDir}/changed.json`, JSON.stringify(changed));
const count = (field, value) => ledger.filter((row) => row[field] === value).length;
console.log(`rows: ${rows.length} merged: ${count("status", "merged")} skipped: ${count("status", "skipped")} unverified: ${count("reason", "unverified") + count("reason", "unverified_duplicate")} changed: ${changed.length}`);
~~~

Review before importing:

- **`changed.json` must be handled first.** `deleted`: disable the TerraScale
  user. `email`: before cutover, delete the TerraScale user in the dashboard, set
  that uid's row back to `"status": "pending_import"` with
  `"terrascale_user_id": null`, and run the transform again into a new folder; the
  row is imported with the new address and the same `external_id`. After cutover,
  the user changes the address themselves on TerraScale. `email_verified`: if it
  became false, disable the TerraScale user until you know why.
- **Merges.** Only verified accounts are merged. List each `merged` row and move
  its data to `merged_into` in the data migration.
- **Names.** `displayName` goes to `first_name` whole; split it only if your app
  already stores given and family names.
- **Custom claims** stay out of the batches; they are set after import.
- **No hashes.** Search the output folder for `passwordHash` and `salt`; both must
  be absent.

## 4. Import into TerraScale

`POST /v1/{project}/users/import` takes 1–500 rows, commits each valid row
independently, sends no email, and allows ten calls per project per minute. It
needs a project admin's sign-in token or a project secret API key with
`auth:write`
([user profiles and imports](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md#import)).
Keep the token in `TERRASCALE_ACCESS_TOKEN`, read from a secret manager. Set
`RUN` to this run's folder:

~~~sh
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
for batch in "$RUN"/batch-*.json; do
  result="${batch%.json}.result.json"
  [ -s "$result" ] && continue
  api -X POST "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT_ID/users/import" \
    -H "Content-Type: application/json" --data-binary @"$batch" > "$result"
  jq -r --arg b "$batch" '"\($b) imported=\(.items|length) errors=\(.errors|length)"' "$result"
  sleep 7
done
~~~

Because each run has its own folder, the skip only resumes an interrupted run;
it never skips a new batch. The response is
`{"items":[{"index":0,"user":{...}}],"errors":[{"index":1,"code":"...","message":"..."}]}`.
Record the results into the run's ledger with `record.mjs`:

~~~js
import { readFileSync, writeFileSync } from "node:fs";

const [mappingFile, ...files] = process.argv.slice(2);
const mapping = JSON.parse(readFileSync(mappingFile, "utf8"));
const byUid = new Map(mapping.map((row) => [row.firebase_uid, row]));
const byEmail = new Map(mapping.filter((row) => row.status === "pending_import").map((row) => [row.email_key, row]));

for (const file of files) {
  for (const item of JSON.parse(readFileSync(file, "utf8")).items ?? []) {
    const user = item.user ?? item;
    const row = (user.external_id && byUid.get(user.external_id)) || byEmail.get(user.email?.trim().toLowerCase());
    if (row?.status !== "pending_import") continue;
    row.status = "imported";
    row.terrascale_user_id = user.id;
  }
}

const imported = new Map(mapping.filter((row) => row.status === "imported").map((row) => [row.app_user_id, row.terrascale_user_id]));
for (const row of mapping) {
  if (row.status === "merged" && !row.terrascale_user_id) row.terrascale_user_id = imported.get(row.merged_into) ?? null;
}
writeFileSync(mappingFile, JSON.stringify(mapping));
const count = (status) => mapping.filter((row) => row.status === status).length;
console.log(`imported: ${count("imported")} merged: ${count("merged")} pending: ${count("pending_import")} skipped: ${count("skipped")}`);
~~~

~~~sh
node record.mjs "$RUN/mapping.json" "$RUN"/batch-*.result.json
~~~

Errors stay `pending_import`, and the next run emits them again:

- `invalid_request`: fix the cause (for example a control character in a name)
  and run the transform again.
- `user_conflict`: the email or `external_id` already exists. A lost response
  can leave committed rows, so export the TerraScale directory (step 6) and pass
  the export pages to `record.mjs`; it attaches by email. If the email belongs to
  someone who signed up on TerraScale during the bridge, attaching is safe because
  both sides proved the mailbox: `PATCH` the user's `external_id` to the Firebase
  uid and move the data of their sign-up `app_user_id` into the Firebase
  `app_user_id`.
- A `429` means the per-minute limit; wait and rerun the loop.

## 5. Claims and disabled users

Set custom claims per user with `PATCH /v1/{project}/users/{user}` and a
`custom_claims` object that fits the limits in
[feature mapping](feature-mapping.md#custom-claims):

~~~sh
api -X PATCH "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT_ID/users/$TERRASCALE_USER_ID" \
  -H "Content-Type: application/json" -d '{"custom_claims":{"is_admin":true,"plan":"pro"}}'
~~~

Disable every user whose `private_metadata.firebase_disabled` is true in the
dashboard or with the SDK project client's disable call
([manage users](https://docs.terrascale.tech/guides/end-user-authentication#manage-users)).
Import has no disabled field, so do this before you open sign-in.

## 6. Reconcile

Export the TerraScale directory page by page
([export the directory](https://docs.terrascale.tech/guides/end-user-authentication#export-the-directory)):

~~~sh
api -G "https://auth.terrascale.tech/v1/$TERRASCALE_PROJECT_ID/users/export" --data-urlencode limit=100 > ts-page-1.json
~~~

Repeat with `after` set to the previous `next` until the response has no
`X-Export-Next` header, saving each page and deduplicating by `id`. Then check:

- Every `imported` row's `terrascale_user_id` is in the export with the same email,
  and no `terrascale_user_id` belongs to two `imported` rows.
- Every `merged` row points at an `imported` row's `app_user_id`.
- `email_verified` is `true` for every imported user (only verified accounts are
  imported).
- `disabled` is true exactly for the users disabled in Firebase.
- Every Firebase uid is in the ledger as `imported`, `merged` or `skipped` with a
  reason, and `changed.json` is empty or handled.

The export is a creation-cutoff scan, not a snapshot: pause sign-up and imports
while you reconcile.
