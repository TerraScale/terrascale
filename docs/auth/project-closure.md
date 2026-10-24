# Project and account closure in ts-auth (TS-873)

ts-auth's half of the closure handoff; the TerraBase fence and its acceptance rule are in
[../terrabase/project-closure.md](../terrabase/project-closure.md). Routes and answers are in
[platform-api.openapi.yaml](platform-api.openapi.yaml).

## Intents

- A closure intent lives in `closure_jobs` at `project/<id>`, `organization/<id>` or `account/<user>` (an auth-only lookup
  address). Its `id` is an opaque random ULID, never derived from the subject, so TerraBase's permanent SYSTEM fence holds
  no subject hash. A project draws its id (the TerraBase `closureId`) once, stores it on the project record
  (`closureId`), and every later intent for that project reuses it: retries, restarts, repeated requests and a new
  request after a refusal all present the closure TerraBase may already hold. Account and organization intents draw a new
  id each. Every intent also has a `generation`, new per creation; every step compares both, so a pass that read an older
  intent never acts on a re-created one. No bearer is stored.
- While an intent exists, the store adds an `AssertAbsent` fence to writes that would add access to its scope: project
  creates and renames, memberships and project roles (creates and rewrites), API keys (create and rotate) and new
  invitations. Such a write fails (503 after its retries). Deletes and writes marked as revocations pass: lowering an
  organization role, lowering or removing a project role, revoking keys and invitations.
- A project intent fences that project's own roles, keys and invitations. It does not fence the organization: a new
  organization member, or a member whose organization role is raised, gets the implied role on a closing project as on
  any other. Such access cannot create anything in TerraBase (its fence refuses databases, keys and destinations) and
  ends with the project root.

## Project closure

- `DELETE …/projects/{project}` commits, in one transaction asserting the caller's admin role, the project intent.
- Then ts-auth calls TerraBase over its own peer transport (`TerraBaseManagementClient.CloseProjectAsync`):
  - `accepted` or `pending`: the intent first records `fenceSeen` (at the version read, read again after a lost race),
    so no later refusal can cancel it. `accepted` then removes, in one transaction, the project root, its logical
    deletion job and the intent (204), and afterwards its roles, keys and invitations. `pending` answers 202.
  - `not_empty`, `closure_conflict` or `binding_conflict` while `fenceSeen` is false: the intent is removed at the
    version read (read again after a lost race) and the request answers 409; the project keeps every right. If the
    intent showed the fence meanwhile it stays (202). If it is gone, the answer reflects the root: 204 when another pass
    removed it, 409 when another pass cancelled it.
  - Any other answer (an outage, a lost answer, a wrong listener's 404, a wrong peer credential's 401): the intent stays
    (202). No such answer is evidence of absence or acceptance.
- A pass whose intent is gone or re-created removes nothing and cleans nothing while the root survives (202).
- `ClosureWorker` resumes due intents at startup and every minute.

Limit: TerraBase writes its fence once and never removes it, and answers `accepted`/`pending` for it on every machine
that holds it. A refusal can still meet an intent whose `fenceSeen` was never recorded: the answer that showed the
fence was lost, or a concurrent request that TerraBase answered before its fence existed cancels the intent before the
one that saw the fence records it (both compare-and-write the same intent; the first wins). The project then keeps all
its rights and an empty, permanently fenced TerraBase project that can hold no database. This needs no endpoint change. A later request for the same project reuses the stored closure id, so TerraBase then
answers for the fence it holds and the closure proceeds.

## Account closure

- `DELETE /v1/terrascale/account` checks `last_owner`, then commits the account intent and a fence intent for every
  organization where the user is the only member with a role, asserting the owner role on each.
- Each step re-reads the user's organizations under those fences. An organization that became shared before its fence
  loses the fence; one that became sole later gets one. For every sole organization, each project gets a project intent
  owned by the account (`accountId`) and closes as above; a project removed meanwhile is skipped. A project intent's
  transaction requires the account intent, so a step running after a cancellation creates none.
- Until every project closure is accepted, a 409 (`last_owner`, `not_empty`, `closure_conflict`, `binding_conflict`)
  ends the account intent. The account intent goes first, at the version read: a concurrent pass that deferred it is read
  again and the delete retried; one that committed it pauses it instead (see below); a vanished one stops there. Only
  then are its children handled: each project intent it began is detached (`accountId` cleared at the version read) and
  finishes alone, removing that project once TerraBase accepts; its organization fences go. The account, its
  memberships, its organizations and untouched projects keep every right. TerraBase fences are permanent, so this is no
  rollback of the closures that started.
- When every project closure is accepted, the intent records `committedAt` and removes, in order: the shared
  memberships, the sole organizations, then the user. From `committedAt` on, a 409 (for example a late `last_owner`
  when another owner left a shared organization meanwhile) only pauses the closure; repeating the request, or the
  background pass, resumes it once the cause is resolved.
- `ClosureWorker` also recovers intents whose account intent is gone: an account-owned project intent is detached and
  finished, an organization fence is removed. An account-owned intent whose account intent exists is left to it.
