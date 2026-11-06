# Project end-user directory

Producer pin: `4b6dfd03b2d814cea0b1f7d599478f6b06532b4a` (monorepo origin/main).
Authority: `src/TerraScale.Authentication/Features/ProjectTenants/` endpoints,
`ProjectTenantEndpoint.cs` DTOs/serializer, `TenantDirectory.cs`, and platform
`PlatformEndpoints.cs` authentication, paging, metadata and errors. The live
sessions section below pins `a811367bd82e0cbe510cd2340fc93e25f81c2dd0`.

| Method/path | Operation | Role | Success |
| --- | --- | --- | --- |
| GET /v1/{project}/users | list_project_users | viewer | 200 ProjectUserPageBody |
| GET /v1/{project}/users/{user} | get_project_user | viewer | 200 ProjectUserBody |

Bearer is a terrascale sign-in user token; project/API-key/end-user tokens fail.
JSON uses **snake_case** at runtime: id/email/created_at/disabled; page items/next.
Every member is required; next alone is nullable. IDs are canonical uppercase ULIDs.
q is an email substring; limit 1..100 defaults to 50; after is opaque, list-bound.
Lists return 400 invalid_request; all operations return 401,403,404,503.
Errors remain typed APIError with status/code/RetryAfter and sanitized diagnostics.
Requests are sent once, redirects refused, cancellation honored, deadline 30s,
responses bounded to 8 MiB. Caller HTTP clients remain owned by the caller.
Captured generated OpenAPI has createdAt instead of runtime created_at (known
producer schema drift TS1274); no schema parity or live qualification is claimed.

| Method/path | Operation | Role | Success |
| --- | --- | --- | --- |
| POST /v1/{project}/users/{user}/disable | disable_project_user | admin | 200 ProjectUserBody |
| POST /v1/{project}/users/{user}/enable | enable_project_user | admin | 200 ProjectUserBody |
| DELETE /v1/{project}/users/{user} | delete_project_user | admin | 204 empty |

These operations have no request body and return 401,403,404,503 JSON errors.
Disable prevents sign-in and invalidates existing sessions/tokens immediately.
Enable permits new sign-ins but does not restore ended sessions. Delete releases
email and invalidates tokens/sessions. Never automatically replay any mutation;
reconcile an uncertain response through the directory before a manual retry.

## Live sessions

Producer pin: `a811367bd82e0cbe510cd2340fc93e25f81c2dd0` (monorepo origin/main),
`ListProjectSessionsEndpoint.cs`, `RevokeProjectSessionEndpoint.cs` and the
`ProjectSessionBody`/`ProjectSessionPageBody` DTOs of `ProjectTenantEndpoint.cs`.

| Method/path | Operation | Role | Success |
| --- | --- | --- | --- |
| GET /v1/{project}/sessions | list_project_sessions | viewer | 200 ProjectSessionPageBody |
| DELETE /v1/{project}/sessions/{session} | revoke_project_session | admin | 204 empty |

Go operations: `ProjectSessions(ctx, project, ProjectSessionsOptions{User,
Limit, After})` and `RevokeProjectSession(ctx, project, session)` on
`client.WithToken(signInToken)`. Sessions are live ones only: unrevoked, within
their issued expiry and the project's lifetime policy (the earlier effective
`expires_at` is returned), of enabled users at their current epoch. Items carry
`id`, `user_id`, `email`, `created_at`, `expires_at`; every member is required
and `next` alone is nullable. `user` narrows to one canonical user ULID; limit
1..100 defaults to 50; `after` is the previous page's opaque cursor. Revoke
signs the session out; an already revoked session answers 204 again, an unknown
one 404. Revocation is never automatically replayed; reconcile an uncertain
outcome through the listing before a manual retry.

