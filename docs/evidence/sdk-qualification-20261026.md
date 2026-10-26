# SDK qualification reconciliation — 2026-10-26

Snapshot: 2026-10-26 05:04 UTC. TS-1154 remains incomplete. This is a reconciliation of saved evidence, not a new live run or a product qualification receipt. TS-1154/P04 owns qualification; TS-1173 owns Q02/orchestrator closure only. Missing authorized fixtures are PENDING, never Done. No private ledgers, credentials, cookies or live services were accessed for this document.

## Versioned evidence anchors

| Anchor | Package / time (UTC) | Evidence and limits |
|---|---|---|
| Q11 | `reconciled-results.json` / `.md`, inventory 0.0.13, `ce9a644b`, generated 2026-10-26 04:26:47 | Historical 441 operations: 211 PASS / 3 FAIL / 227 PENDING; 30 independent gates. These are historical inventory counts, not current 0.0.16 counts. Baseline installed evidence spans 0.0.9–0.0.11 and local/browser checks. |
| P04 | `FINAL-REPORT.md`, `final-live-evidence.json`, `final-identity-coverage-results.json`; registry 0.0.12, `b8352ac`, finished 04:23:11.783 | 31 distinct identity operations exercised; 17 successful mutation checks across identity and management. Expected absence/denial does not prove positive admission. Installation integrity is recorded in the artifact. |
| Import | Q11 `import-installed-live-results.json`; installed 0.0.13, 04:37:44.500–04:38:03.283 | 22 scenario checks PASS; existing owned job lifecycle, no create/delete calls in this run. Independent of P04 destination-create 409. |
| NDJSON | Q11 `ndjson-installed-live-results.json`; installed 0.0.13, 04:39:33.220–04:39:58.867 | 13 checks PASS: owned database deletion/404 and recreation with automatic default placement; NDJSON import create 201/upload/preview/start/completion, two-row checksum and generated ULIDs. Separate allocation from P04 quota refusal. |
| Userinfo | Q11 `userinfo-installed-live-results.json`; installed 0.0.11, 04:29:38.008 | Named `oidc.userinfo` 200 with own-email/subject checks; no originating-client introspection/revocation or tenant-admin admission implied. |
| Current | Repository package 0.0.16 | 0.0.14 removed Better Auth public entrypoint; 0.0.15 cancellation proof is local; 0.0.16 source removes legacy entrypoints. Source removal merged as `15fc1af`; tag pushed, registry removal qualification remains pending. No automatic promotion of old evidence to this version. |

Artifacts Q11 reside in the sibling `qa-q11-sdk-js/.qa-q11/`; P04 artifacts reside in `/home/mariogk/scratch/p04-js/`. Filenames and operation anchors below identify sanitized records without exposing allocation identifiers.

## Independent positive and denial evidence

P04 proves the listed scenarios for the credentialed management factory and current identity factory; current identity methods are not equivalent evidence for tenant `TsAuth*` APIs. Management positive calls: `databases.get/list`, `database().collection().put/get/list`, project/database usage reads, `backups.list/getPolicy/setPolicy` (preserve existing values), `schema.list/infer`, `indexes.list/getAutoPolicy/recommendations`, and `previewSchema`. Default placement was observed on a retained allocation, not newly proven database creation.

Ordinary-customer `admin.databases` returned 403 `forbidden_scope`: denial-only PASS, privileged success PENDING. Restricted project-key document write also returned 403 and full-key read returned 404; rotated/revoked project secrets returned 401 `invalid_client`. These prove the specified denial/issuance boundaries, not expiry of previously issued tokens. P04 import-destination create returned 409 `database_limit` and exact read 404: no allocation and no import success in that fixture.

Import independently proves `V1Imports.get/uploadSource/update/cancel/resume/start` on the Q11 owned existing job: range replay, 409 `offset_mismatch` without advancement, preview exclusions, cancellation, resumed preview confirmation, completion and count/checksum match. `V1Imports.create` is not executed in the 22-check PostgreSQL artifact; its separate NDJSON artifact supplies an actual create 201, upload/preview/start/completion and generated-ULID proof.

## Remaining method matrix

Names below are exact Q11 export/member groups; constructor/factory rows mean admission of their returned methods, not just construction. All rows are PENDING live unless explicitly stated. Q11 native/local evidence remains independent. Fixtures are required allocations/roles, not permission to use another customer’s data. TS-1154/P04 owns fixture qualification; specialist contract gates retain their named ticket.

| Exact class / callable and members | Named owned fixture / role required | Status and artifact anchor |
|---|---|---|
| `.` `V1BackupDestinationClient.{set, delete}` | owned object-store backup destination and project configuration authority | PENDING; Q11 OP018, OP019 |
| `.` `V1Backups.{restore, restorePointInTime}` | owned backup/restore destination, restore capacity and retention authority | PENDING; Q11 OP022, OP027; policy-preserving setPolicy has P04 positive evidence |
| `.` `V1ChangeWebhooks.{create, delete, deliveries}` | owned registered webhook callback and delivery fixture | PENDING; Q11 OP028, OP030, OP031 |
| `.` `V1Collection.{decryptShapeObject}` | owned shape object/key and current producer contract | PENDING; Q11 OP051 |
| `.` `V1Databases.{update, branch}` | isolated project/database owner; exact ledger and capacity | PENDING; Q11 OP069, OP071 |
| `.` `V1FileBucket.{delete, createUpload, upload}` | owned storage bucket/object and admitted object-store service (TS-962) | Historical FAIL (0.0.9); current PENDING; Q11 OP080, OP081, OP086 |
| `.` `V1FileBucket.{completeUpload, downloadUrl, deleteFile, download}` | owned storage bucket/object and admitted object-store service (TS-962) | PENDING; Q11 OP082, OP084, OP085, OP087 |
| `.` `V1Indexes.{setAutoPolicy, applyRecommendation}` | isolated project/database owner; exact ledger and capacity | PENDING; Q11 OP123, OP125 |
| `.` `V1Platform.{databases, collections, documents, document}` | separately authorized platform administrator; ordinary account denial insufficient | PENDING; Q11 OP126, OP127, OP128, OP129 |
| `./ts-auth` `TsAuthApiKeysClient.{create, list, delete}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP195, OP196, OP197 |
| `./ts-auth` `TsAuthAuditEventsClient.{list}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP198 |
| `./ts-auth` `TsAuthBillingClient.{get, checkout, portal, enforceCheck, reconcileQuota}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP199, OP200, OP201, OP202, OP203 |
| `./ts-auth` `TsAuthConnectionsClient.{create, list, update, delete}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP204, OP205, OP206, OP207 |
| `./ts-auth` `TsAuthDirectoriesClient.{create, list, rotate, delete}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP208, OP209, OP210, OP211 |
| `./ts-auth` `TsAuthInstanceClient.{get, update}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP212, OP213 |
| `./ts-auth` `TsAuthJwtTemplatesClient.{create, list, update, delete, issueToken}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP214, OP215, OP216, OP217, OP218 |
| `./ts-auth` `TsAuthOauthClientsClient.{create, list, update, delete}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP219, OP220, OP221, OP222 |
| `./ts-auth` `TsAuthOauthResourcesClient.{create, list, update, revoke}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP223, OP224, OP225, OP226 |
| `./ts-auth` `TsAuthOidcClient.{deviceAuthorization, introspect, introspectResource, revoke}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP230, OP232, OP233, OP234 |
| `./ts-auth` `TsAuthOrganizationsClient.{create, list, get, update, delete, createMembership, listMemberships, updateMembership, deleteMembership, createInvitation, listInvitations, revokeInvitation, createDomain, listDomains, deleteDomain, enrollDomain}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP236, OP237, OP238, OP239, OP240, OP241, OP242, OP243, OP244, OP245, OP246, OP247, OP248, OP249, OP250, OP251 |
| `./ts-auth` `TsAuthSessionsClient.{revoke}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP252 |
| `./ts-auth` `TsAuthUsersClient.{list, listAll, get, create, update, delete, impersonate, sessions, revokeAllSessions, mfaFactors, deleteMfaFactor, regenerateRecoveryCodes, passkeys, deletePasskey, organizations}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP268, OP269, OP270, OP271, OP272, OP273, OP274, OP275, OP276, OP277, OP278, OP279, OP280, OP281, OP282 |
| `./ts-auth` `TsAuthWebhooksClient.{create, list, delete, test, deadLetter, replayDeadLetter}` | registered tenant/app/resource; tenant administrator and disposable owned users/resources | PENDING; Q11 OP283, OP284, OP285, OP286, OP287, OP288 |
| `./react` `disposeMutation` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 OP297 |
| `./react` `ReactiveMutationObserver.{mutate}` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 OP302 |
| `./react` `ReactiveQueryObserver.{getServerSnapshot}` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 OP307 |
| `./react` `useMutation` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 OP313 |
| `./react` `useQuery().refetch` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET008 |
| `./react` `useMutation().mutate` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET009 |
| `./react` `useMutation().mutateAsync` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET010 |
| `./react` `useMutation().cancel` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET011 |
| `./react` `useMutation().reset` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET012 |
| `./svelte` `createMutation().mutate` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET016 |
| `./svelte` `createMutation().cancel` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET018 |
| `./svelte` `createMutation().reset` | current C# reactive corpus and authorized mounted framework fixture (TS-1153) | PENDING; Q11 RET019 |

P04 factory aliases for existing root management operations require per-alias evidence; historical root success does not qualify every wrapper. `requireTerraScaleAdministration` / `createTerraScaleAdministrationClient` still require positive admin-role admission; `requireTerraScaleManagement` / `createTerraScaleManagementClient().database` retain only the concrete P04 scenarios above. `previewSchema` has P04 positive evidence, not a durable migration plan/apply/status contract.

## Current identity factory coverage and fixture gates

All following names belong to `createTerraScaleIdentityManagementClient()` unless otherwise specified. Anchor: P04 `final-identity-coverage-results.json` route operation names and `final-live-evidence.json` steps. EXERCISED includes expected denial/absence and is not blanket positive PASS.

| Exact methods | Evidence type / remaining fixture |
|---|---|
| `mintEndUserToken` | PENDING: registered customer app/project assertion, separate customer-app authority. |
| `acceptInvitation, createInvitation, declineInvitation, lookupInvitation, revokeInvitation` | PENDING: exact owned invitation capability, authorized mail flow and invited second identity. |
| `cancelOwnershipTransfer, startOwnershipTransfer, confirmOwnershipTransfer` | PENDING: second owned member, owner authority and transfer confirmation capability. |
| `leaveOrganization, removeMember, removeProjectMember, setProjectMemberRole` | PENDING: second disposable member and organization/project role assignments; preserve last-owner guard. |
| `accountUnlinkProvider` | PENDING: owned linked provider and authorized unlink email code. |
| `deleteSsoConnection, putSsoConnection, verifySsoDomain` | PENDING: owned registered SSO application/domain and organization administrator. |
| `accountDeletionHistory, deleteOrganization` | PENDING: project closure prerequisite; no logical/physical erasure claim. |
| `accountDeletionStatus, deleteProject` | 202 acceptance exercised; logical completion PENDING. |
| `getAccountMfa, getOwnershipTransfer, changeMemberRole, listInvitations, createOrganizationApiKey, listOrganizationApiKeys, createOrganization, listOrganizations, createProjectApiKey, listProjectApiKeys, createProject, listProjects, getOrganization, renameOrganization, getProject, renameProject, getOrganizationApiKey, revokeOrganizationApiKey, getProjectApiKey, revokeProjectApiKey, listAuditEvents, listMembers, listProjectMembers, rotateOrganizationApiKey, rotateProjectApiKey, setOrganizationMfaPolicy, setOrganizationApiKeyAllowedCidrs, setProjectAllowedCidrs, setProjectApiKeyAllowedCidrs, accountLinkedProviders, getSsoConnection` | 0.0.12 exercised scenarios; getOwnershipTransfer/getSsoConnection expected 404; changeMemberRole preserves owner and tests 409 last_owner. |
| `deleteAccount` | 202 acceptance exercised once; receipt completion 204 PENDING. |

`mintEndUserToken` belongs to `createTerraScaleProjectIdentityClient()`; `accountDeletionStatus/History` belong to `createTerraScaleAccountDeletionClient()`. Final receipt-only check at 04:32:11.666 returned 202 (`cleanup-final-receipt-check.json`); TS-969 symptom retained without asserting root cause. No active reusable P04 fixture remains available to qualify new methods.

## Retired surfaces and separate gates

`better-auth` / `requireTerraScaleBetterAuth` is ABSENT from 0.0.14, not adapter PASS. TS-1026/TS-1085 are parked Backlog for replication design; remaining producer/adapter gates include exact-version adapter, unique/CAS/consume, migration, concurrency, retry, revocation and denial proof are not supplied by removal.

Registry-installed 0.0.15 passed local cancellation fixtures at 04:50:19.774 (`published-cancellation-evidence.json`), sufficient for TS-1151. These do not qualify a producer or privileged role. TS-1153 current producer/reactive and mounted-framework gates remain independent of helper tests.

0.0.16 source removes `local`, `postgres`, `schema`, `sql`, `tanstack`; registry-installed removal proof is PENDING (TS-1152). Every historical Q11 row under these entrypoints is RETIRED, never PASS: SQLite runtime/factory/client/adapter methods, PostgresClient query/close/connect and postgresQuery, schema/sql builders, TanStack publisher/sync/options and returned callbacks. `createDatabaseQuery`, returned `subscribe`, `DatabaseSubscriptionView.{getSnapshot,getRequest,restart,apply}`, and native result branches were also removed in 0.0.16: RETIRED, not fixture gates. Retired rows remain historical inventory, not current producer conformance.

## Q01 exact-fragment gate (limited TS-1154 scope)

Only canonical sources `docs/guides/realtime.md` and `docs/guides/sdks.md` are included from Q01 `q01-remaining-fragments.json` (generated 04:13:10.891190, source `8a2dfdd6`). Their 120 locale rows collapse to 12 canonical body hashes. The full 2,770-row TS-1172 inventory is not inherited here. Each selected exact execution remains PENDING: no runnable authorized environment was supplied. Hashes classify fragments, not execution evidence.

| Canonical source / SHA-256 | Classification | Exact execution |
|---|---|---|
| `docs/guides/realtime.md` `0fbece26b70b00a9ccf00cb1e478a15b9f6604ce15ed2993c2d5f8427f21f374` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/realtime.md` `00df95b0192f00bbed1f76a41682b57ce343cafb11eeb3780615e5ba4364e6b4` | request response or literal data | PENDING |
| `docs/guides/realtime.md` `0f634d6fb0a8c9509e712ffa020c934417e4fd28e649421dc781608481babc8d` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/realtime.md` `53217e528ed84ecb2d7e60cc84f68601d08138ef5a6e5e9d42d1c8a640e62982` | language fragment requires program bootstrap | PENDING |
| `docs/guides/realtime.md` `3271e8c0364239f6f422ffc5f0442ddff5f67d69e29917bbf3909c88f0039648` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/realtime.md` `f3065c303eb7fafcb08cc70dd713cb578ea4aac78a294b4c212ec8c6a126cdb1` | request response or literal data | PENDING |
| `docs/guides/realtime.md` `dcb4cea9560fbc4d6d306c3cfca064b3bf94f103fd8e19e1b6b3513308b99f98` | language fragment requires program bootstrap | PENDING |
| `docs/guides/sdks.md` `bb9476811eaaea5189f39b4ebe6f0addbbb56c725fa34538bb5f9b6284c0174c` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/sdks.md` `8cba83746037808dad3b05729888dfff938c48ccc373c75e6bf67a9879396dae` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/sdks.md` `55d1cb34ba70386c06bffd149efc85be881233e4cb2499d277a666958010b7ad` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/sdks.md` `0dd05c6adefb1030df984680811fcc1c1268b31b6345ff42f4202928d0ba6b20` | shell workflow requires environment and preceding steps | PENDING |
| `docs/guides/sdks.md` `2004a581a3012e0ffc676868c8a8298f357a65086d97991e916bd128302cb990` | request response or literal data | PENDING |

Completion requires a current registry-installed per-method run with owned active fixture, explicit registered tenant/app/resource roles, separately authorized administrator, backup destination/webhook, and sufficient allocation limits; attach exact version/time/status/type, bounded cleanup and published-contract parity. Current evidence does not satisfy those gates.
