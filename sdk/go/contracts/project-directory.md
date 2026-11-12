# Project end-user directory

Producer pin: `4b6dfd03b2d814cea0b1f7d599478f6b06532b4a` (monorepo origin/main).
Authority: `src/TerraScale.Authentication/Features/ProjectTenants/` endpoints,
`ProjectTenantEndpoint.cs` DTOs/serializer, `TenantDirectory.cs`, and platform
`PlatformEndpoints.cs` authentication, paging, metadata and errors.

| Method/path | Operation | Role | Success |
| --- | --- | --- | --- |
| GET /v1/{project}/users | list_project_users | viewer | 200 ProjectUserPageBody |
| GET /v1/{project}/users/{user} | get_project_user | viewer | 200 ProjectUserBody |
| GET /v1/{project}/sessions | list_project_sessions | viewer | 200 ProjectSessionPageBody |
| DELETE /v1/{project}/sessions/{session} | revoke_project_session | admin | 204 empty |

Bearer is a terrascale sign-in user token or an unrestricted secret API key of
this project. GET requires auth:read and mutations auth:write; step-up routes
refuse keys with 403 person_required. Exchanged API-key and end-user JWTs fail.
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

Sessions are live only: unrevoked, of enabled users, within both their issued
expiry and the current project lifetime, with the earlier effective expiry
answered. `user` filters to one user id; limit 1..100 defaults to 50; `after`
is opaque, list-bound. Items are id/user_id/email/created_at/expires_at,
snake_case, every member required, next alone nullable. Revoke signs the
session out and answers 204 again for an already-revoked session; it is sent
once and never replayed automatically.
