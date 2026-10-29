# Project OAuth clients

Producer: `1d823d8534fc5035859a47e263691d0c7cb7b744`,
`src/TerraScale.Authentication/Features/ProjectTenants/`:
`ListProjectClientsEndpoint.cs`, `CreateProjectClientEndpoint.cs`,
`DeleteProjectClientEndpoint.cs`, `ProjectTenantEndpoint.cs`.
The public production OpenAPI retrieved 2026-10-26 at 04:55Z identifies these
same operations, routes, roles and statuses. Its generated DTO property naming
does not match the runtime source-generation context; the runtime's
`SnakeCaseLower` names below are authoritative. Generated parity remains an
owning producer gap; this file does not claim that export has been corrected.

| Go operation on `client.WithToken(signInToken)` | Operation identity | HTTP route | Success | Project role |
| --- | --- | --- | --- | --- |
| `ListProjectOAuthClients(ctx, project)` | `list_project_clients` | GET `/v1/{project}/clients` | 200 JSON | viewer |
| `CreateProjectOAuthClient(ctx, project, request)` | `create_project_client` | POST `/v1/{project}/clients` | 201 JSON | admin |
| `DeleteProjectOAuthClient(ctx, project, clientID)` | `delete_project_client` | DELETE `/v1/{project}/clients/{client_id}` | 204 | admin |

Authorization is the user's terrascale sign-in bearer, never an instance or
project API key. Project and client path IDs are canonical ULIDs. The project
replaces the management issuer's tenant segment; it is not an organization URL.
Invisible/unknown projects return 404; insufficient roles return 403.

Registration JSON: `name` (1–128 UTF-16 code units, trimmed, letter/digit,
no controls), `type` (`public` or `confidential`), `redirect_uris` (1–10 absolute
HTTPS URLs, or HTTP loopback; no fragment). The producer additionally enforces
exact project redirect allowlist membership. No edit or rotation operation exists.

Results contain `client_id`, `name`, `type`, `redirect_uris`, `created_at`;
`client_secret` appears only on confidential creation. Formatting redacts it;
explicit field access and JSON serialization intentionally retain the credential.
Lists return `items` and nullable `next`, currently every client in one page;
there are no client pagination parameters. No invented pagination loop is added.

Requests are sent once, with the shared fixed 30-second deadline and 8 MiB
response cap; registration JSON is capped at 16 KiB. Caller cancellation and
typed HTTP errors are retained. A lost creation response has an unknown outcome;
list before deciding whether to issue another creation. Deterministic denials
and mutations are never automatically replayed. No paid fixture or signup is
required by the HTTP tests; live qualification and cleanup remain separate gates.
