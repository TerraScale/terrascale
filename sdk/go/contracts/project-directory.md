# Project end-user directory

Producer pin: `4b6dfd03b2d814cea0b1f7d599478f6b06532b4a` (monorepo origin/main).
Authority: `src/TerraScale.Authentication/Features/ProjectTenants/` endpoints,
`ProjectTenantEndpoint.cs` DTOs/serializer, `TenantDirectory.cs`, and platform
`PlatformEndpoints.cs` authentication, paging, metadata and errors.

| Method/path | Operation | Role | Success |
| --- | --- | --- | --- |
| GET /v1/{project}/users | list_project_users | viewer | 200 ProjectUserPageBody |
| GET /v1/{project}/users/{user} | get_project_user | viewer | 200 ProjectUserBody |

Bearer is a terrascale sign-in user token; project/API-key/end-user tokens fail.
JSON uses **snake_case** at runtime: id/email/created_at/disabled; page items/next.
Every member is required; next alone is nullable. IDs are canonical uppercase ULIDs.
q is an email substring; limit 1..100 defaults to 50; after is opaque, list-bound.
Lists document 400 invalid_request; all operations document 401,403,404,503.
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

These operations have no request body and document 401,403,404,503 JSON errors.
Disable prevents sign-in and invalidates existing sessions/tokens immediately.
Enable permits new sign-ins but does not restore ended sessions. Delete releases
email and invalidates tokens/sessions. Never automatically replay any mutation;
reconcile an uncertain response through the directory before a manual retry.
