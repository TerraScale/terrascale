---
name: firebase-to-terrascale
description: Migrate a Firebase application to TerraScale by inventorying services, planning data and identity changes, replacing storage and functions, and coordinating client cutover and rollback. Use when asked to move off Firebase, replace Firebase with TerraScale, or plan a Firebase migration spanning multiple services.
---

# Migrate a Firebase application

Use this entry point when the app uses more than Firestore, or when its Firebase
dependencies are not yet known. Deliver an inventory, explicit representation
and access decisions, a rehearsed migration, and a reversible cutover.

See also `firestore-to-terrascale` for document modeling and data movement, and
`firebase-auth-to-terrascale` for identity migration. These names are optional
discovery hints; this skill works independently. Read the local
[service migration reference](references/service-migration.md) for storage,
Functions and client replacement details.

## Prerequisites

- [ ] Access to the app source, Firebase console and selected Google Cloud project.
- [ ] Firebase CLI and Google Cloud CLI with read/export permissions; `rg`, `jq`.
- [ ] A private migration directory outside the checkout; start with `umask 077`.
- [ ] A TerraScale project and isolated rehearsal database, with server-held
  credentials for the operations in the plan.
- [ ] Product owners for sign-in, notification delivery and hosting changes.
- [ ] An agreed write-freeze window or an application-owned mutation journal.

Follow the [Firebase CLI setup](https://firebase.google.com/docs/cli),
[Google Cloud CLI setup](https://cloud.google.com/sdk/docs/install), and
[TerraScale getting started](https://docs.terrascale.tech/getting-started/).
Never commit exports, print tokens, run shell tracing, or copy project credentials
into browser/mobile bundles. References below use environment variable names;
supply their values through the existing secret manager.

## 1. Inventory the app and every Firebase service

Inspect `firebase.json` and `.firebaserc` locally. Record aliases, project IDs,
Firestore database names, rules/index file paths, RTDB instances, Storage rules,
Functions source/codebase/runtime settings and Hosting targets/rewrites. Neither
file is a complete inventory: services can be configured in the console or used
from a separate repository. Do not paste whole config files into reports.

Run these read-only commands, saving outputs privately:

```sh
firebase --version
firebase projects:list
firebase use
firebase apps:list --project "$FIREBASE_PROJECT_ID"
firebase functions:list --project "$FIREBASE_PROJECT_ID"
firebase hosting:sites:list --project "$FIREBASE_PROJECT_ID"
firebase remoteconfig:get --project "$FIREBASE_PROJECT_ID" > remote-config.json
gcloud firestore databases list --project "$FIREBASE_PROJECT_ID"
gcloud storage buckets list --project "$FIREBASE_PROJECT_ID"
```

`firebase use` without an alias reports the active selection; still pass
`--project` explicitly to subsequent commands. Check installed `--help` if command
availability differs; no empty output from a failed command counts as evidence.
Remote Config templates can contain sensitive application configuration.
Sources: [Firebase CLI command reference](https://firebase.google.com/docs/cli),
[Firestore databases list](https://cloud.google.com/sdk/gcloud/reference/firestore/databases/list),
[Storage buckets list](https://cloud.google.com/sdk/gcloud/reference/storage/buckets/list).

Search names first with `rg -l` to avoid printing inline credentials or user data:

```sh
rg -l --hidden -g '!.git/**' -g '!node_modules/**' -g '!vendor/**' \
  'firebase(/|[-.]|\b)|@react-native-firebase|FirebaseAdmin|FirebaseAuth|FirebaseFirestore|com\.google\.firebase|cloud_firestore|firebase_core' .
rg -l --hidden -g '!.git/**' -g '!node_modules/**' \
  'getFirestore|collectionGroup|onSnapshot|enableIndexedDbPersistence|persistentLocalCache|initializeFirestore|FieldValue|Timestamp|GeoPoint' .
rg -l --hidden -g '!.git/**' -g '!node_modules/**' \
  'getDatabase|onValue|onDisconnect|\.database\(|getAuth|onAuthStateChanged|verifyIdToken|setCustomUserClaims|\.auth\(' .
rg -l --hidden -g '!.git/**' -g '!node_modules/**' \
  'getStorage|uploadBytes|getDownloadURL|\.storage\(|httpsCallable|onCall|onRequest|onDocument|onObject|onSchedule|\.functions\(' .
rg -l --hidden -g '!.git/**' -g '!node_modules/**' \
  'getMessaging|getToken|onMessage|sendEach|\.messaging\(|getRemoteConfig|fetchAndActivate|\.remoteConfig\(' .
```

Inspect matching call sites, lockfiles, service workers, build-time config and
deployment jobs. Include modular JS, namespaced/compat SDKs, Admin SDKs, native
Android/Apple, Flutter and React Native integrations. Search source-specific
imports once the framework is known; text matches alone do not prove usage.
Sources: [Firebase web setup](https://firebase.google.com/docs/web/setup),
[Admin setup](https://firebase.google.com/docs/admin/setup),
[Firestore listeners](https://firebase.google.com/docs/firestore/query-data/listen).

Complete this inventory with console findings and owners:

| Service | Record before planning | Destination or explicit retained dependency |
| --- | --- | --- |
| Firestore | Every database, collection group, subcollection, count, query, rule, index, listener | Relational schema and SQL; detailed data guide |
| Realtime Database | Every instance, JSON paths, priorities, rules, presence, transactions | SQL tables and app-managed presence; retain RTDB if required |
| Auth | Providers, UIDs, claims, anonymous users, MFA, sessions, redirect domains | Identity map and rehearsed sign-in transition |
| Cloud Storage | Buckets, generations, bytes, metadata, ACL/rules, links, upload flows | File-ID map and tested TerraScale file buckets, or retained storage |
| Cloud Functions | Runtime, dependencies, region, trigger, retries, event IDs, secrets by name | HTTP/cron Functions or an external event worker |
| Hosting | Domains, TLS, rewrites, headers, SPA routes, previews, CDN caching | A separately selected web host |
| FCM | Tokens, topics, senders, service worker, platform credentials | Keep FCM or select a separate push provider |
| Remote Config | Template versions, conditions, experiments, fetch intervals, defaults | Keep Remote Config or build an explicit configuration service |

Add other discovered services, such as Analytics, App Check, Extensions or
Crashlytics, to the plan rather than silently removing them. Verify scheduled
jobs, mobile versions and background integrations outside this checkout.

## 2. Fill the migration plan and close capability gaps

TerraScale databases use the SQLite dialect. Documents, Firestore rules and
Firebase client calls need explicit conversion; a database connection is not a
Firebase API adapter. Source: [SQL](https://docs.terrascale.tech/concepts/sql).

Create a plan using this template:

```text
Source Firebase project / databases / RTDB instances:
Target TerraScale project / database / rehearsal database:
Services retained and their owners:
Collection-to-table map and ID convention:
Subcollection / missing-parent / reference handling:
Timestamp / numeric / missing-versus-null conversions:
Firebase UID -> application identity -> scoped-token subject map:
Read rules / write validation / role derivation:
File path + generation -> bucket ID + file ID map:
Function -> HTTP / cron / retained worker map:
Listener / offline / presence replacement per screen:
Export route and consistent-read strategy:
Batch sizing, checkpoints, verification counts and checksums:
Write-freeze entry / exit criteria and maximum duration:
Client rollout, minimum versions and stale-client rejection:
Cutover owner / rollback owner / go-no-go thresholds:
Reverse export / mutation replay procedure after target writes:
Source retention deadline and deletion authorization:
```

Use the following order; dependencies can be rehearsed in parallel but must be
ready before production traffic moves:

1. **Data model:** stable IDs, tables, constraints, queries and authorization.
2. **Data:** export, deterministic conversion, bounded import and reconciliation.
3. **Auth:** establish identity mapping and session handling before owner access.
4. **Storage:** copy bytes, rewrite file references, verify access and downloads.
5. **Functions:** replace synchronous operations and background consumers.
6. **Client code:** switch reads, mutations, listeners, sign-in and file flows.

Resolve these product boundaries in writing:

- [ ] Data import accepts SQL statements; there is no Firestore backup upload or
  hosted continuous migration service. Use the
  [import guide](https://docs.terrascale.tech/guides/import).
- [ ] The documented Functions triggers are HTTPS and cron. Firestore, Storage,
  Auth and Pub/Sub triggers require an application workflow or separate worker:
  [Functions](https://docs.terrascale.tech/guides/functions).
- [ ] File storage is rolling out; the docs mark live upload/download and
  authorization verification as pending. Prove availability for this project
  before scheduling cutover: [file storage](https://docs.terrascale.tech/guides/file-storage).
- [ ] Live table shapes are documented; React/Svelte examples require the next
  JavaScript SDK release. The published npm package is a previous API. Verify
  the installed version instead of assuming current-source examples work:
  [SQL](https://docs.terrascale.tech/concepts/sql),
  [JavaScript](https://docs.terrascale.tech/getting-started/javascript).
- [ ] The cited TerraScale guides do not provide Firebase-compatible offline
  persistence, RTDB presence, FCM, Remote Config or Firebase Hosting replacement
  contracts. Retain those dependencies or implement and verify their replacements.
  Use the specific alternatives in the local reference; do not claim equivalence.

## 3. Migrate the data model and data

For Firestore, use the standalone `firestore-to-terrascale` skill if available,
or apply this minimum sequence:

- [ ] Inventory all collection groups; exporting a parent collection does not
  export its differently named subcollections.
- [ ] Preserve original document paths as unique source identifiers; model
  references and parent paths explicitly, including absent parent documents.
- [ ] Separate arrays/maps that need relational filtering; retain unqueried JSON
  as text with a documented codec for special types and absent fields.
- [ ] Make a managed Firestore backup, then a typed logical export for conversion.
- [ ] Freeze source writers for the final export, or reconcile a complete
  application-owned journal of creates, updates and deletes.
- [ ] Create tables, load parents before children and check each committed batch.
- [ ] Reconcile per-collection counts, canonical values, references and query results.

Firestore managed exports are not an exact snapshot at export start, and their
backup format is not a JSONL/SQL interchange format. See
[Firestore export/import](https://firebase.google.com/docs/firestore/manage-data/export-import).
For a small RTDB source, export its JSON tree to a private file:

```sh
firebase database:get / --instance "$FIREBASE_RTDB_INSTANCE" \
  --project "$FIREBASE_PROJECT_ID" > rtdb.json
```

The command is documented in the
[Firebase CLI database commands](https://firebase.google.com/docs/cli#database-commands).
Split large trees by audited paths; do not treat separate reads as one snapshot.
Map push keys to text IDs, nested ownership to foreign keys, and preserve array
positions. Replace presence, priorities and `onDisconnect` behavior explicitly;
ordinary SQL rows alone do not reproduce those RTDB client semantics.

TerraScale imports run in independent bounded batches, with no whole-import
atomicity. Keep a load ledger and rebuild the rehearsal database after conversion
changes: [import](https://docs.terrascale.tech/guides/import),
[limits](https://docs.terrascale.tech/limits).

## 4. Migrate identity and access

- [ ] Preserve Firebase UID references in an app-owned identity map. A scoped
  TerraScale data token asserts a canonical ULID `user_id`; do not send an
  arbitrary Firebase UID as that field.
- [ ] The backend verifies the source session during a staged transition, maps
  its UID to the application's subject and derives roles from trusted records.
- [ ] Register the new sign-in flow and redirects; rehearse existing account,
  new account, linked provider, disabled user, MFA and sign-out journeys.
- [ ] Handle UIDs, providers, credentials and refresh sessions as separate items.
  The public guides do not establish automatic Firebase password-hash import
  or session portability. Choose verified reauthentication/re-enrollment where
  no documented transfer exists; never promise retained sessions.
- [ ] Replace Firestore/Storage rules with target controls before exposing data.

The backend can authenticate users with its own sign-in and mint scoped data
tokens using the documented server-side minting flow. Keep its
`auth:tokens:mint` key on the server; table names in token mint requests are
ignored by TerraBase and do not restrict rows. Source:
[end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).

The newer [authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities)
page takes precedence over older claims: project OIDC tokens do not authorize
data APIs. Use validated app sessions plus backend-minted scoped tokens for
rule-gated reads, or route operations through an authorizing backend. Do not
infer a Firestore rule translator or a direct client write contract.

## 5. Replace storage, Functions and client code

Follow the detailed procedures in
[service migration](references/service-migration.md). Complete these gates:

- [ ] Copy objects and maintain a source-generation-to-target-file map.
- [ ] Translate ownership and distinguish authenticated-public access from
  anonymous/public Firebase download links.
- [ ] Replace mutable object paths with immutable file IDs and app-owned aliases.
- [ ] Convert every function trigger and prevent duplicate external side effects.
- [ ] Port Firebase queries to parameterized SQL and index the real access paths.
- [ ] Choose live shapes or backend refresh for each `onSnapshot` listener.
- [ ] Design offline reads, queued writes and conflicts explicitly, or change the
  product to require an online connection for mutations.
- [ ] Replace callable-function error/auth handling, unsubscribe cleanup,
  optimistic updates and sign-out cache clearing.
- [ ] Preserve Hosting/FCM/Remote Config until their replacements pass acceptance.

## 6. Verify and rehearse cutover

- [ ] Run the full plan against the rehearsal project with representative sizes.
- [ ] Compare source counts by actual collection path, not just collection-group
  totals; also compare nested arrays, blobs, timestamps and deleted records.
- [ ] Compare query pagination and sort order, including absent/null values,
  duplicate sort keys, case sensitivity and ties.
- [ ] Exercise two unrelated users, a privileged user, a disabled user and an
  unauthenticated caller against reads, writes, file lists and Functions.
- [ ] Verify network loss, token expiry, reload, listener teardown and sign-out.
- [ ] Confirm file counts, exact byte checksums, metadata and new URL behavior.
- [ ] Check every scheduled/event job's last processed event and external effects.
- [ ] Record measured freeze duration, import rate, failures and rollback time.

Do not call access verified because a project admin can query a table. Browser
filters are not authorization. Function invocation needs separate attention:
the documented invocation requires read access and SQL runs with the function's
own authority, so keep privileged operations behind an authorizing server.
Sources: [SQL rules](https://docs.terrascale.tech/concepts/sql#live-shapes-and-rules),
[Functions](https://docs.terrascale.tech/guides/functions).

## 7. Cut over

1. Freeze every source writer: old clients, admin tools, Functions, scheduled jobs
   and importers. Enforce the freeze server-side; UI maintenance mode is insufficient.
2. Drain in-flight jobs. Capture final export/journal checkpoints and source counts.
3. Load the final data delta, identity map and file map. Re-run all reconciliation
   gates while writes remain stopped. Preserve a private migration manifest.
4. Deploy backend authorization and Functions, then the selected client cohort.
   Switch config through the app's existing rollout mechanism.
5. Exercise one complete create/read/update/delete flow, file upload/download,
   sign-in/sign-out and background side effect. Record target mutation IDs.
6. Open target writes after the go/no-go owner accepts the gates. Reject or route
   stale Firebase clients so they cannot silently create a second source of truth.
7. Watch error rates, denied access, listener freshness and queued work for the
   agreed window. Keep the source retained and writes disabled.

## 8. Roll back and retain evidence

- [ ] **Before target writes:** return routing/config to Firebase, verify source
  access rules and jobs, then unfreeze source writers.
- [ ] **After target writes:** freeze the target first. Export and reverse-map all
  accepted mutations, tombstones, new identities and files since the checkpoint.
  Reconcile those into Firebase before redirecting traffic.
- [ ] If reverse conversion was not rehearsed, remain in maintenance mode for
  repair rather than reopening a stale Firebase copy.
- [ ] Restart exactly one set of background consumers. Suppress replayed external
  effects with their original idempotency identifiers.
- [ ] Keep exports, the identity/file maps, counts and checkpoints for the agreed
  retention window. Delete source services only under the approved cleanup plan.

TerraScale database export is a consistent SQL read snapshot, useful for the
reverse conversion; it is not a Firebase restore format. Source:
[SQL export](https://docs.terrascale.tech/guides/sql-http#export-a-database).
