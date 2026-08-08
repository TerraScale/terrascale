# Project team membership .NET SDK delivery

| Contract surface | Producer | Consumer | Landing order | Compatibility validation |
| --- | --- | --- | --- | --- |
| `/api/v1/projects/{projectId}/access/team-members` | TerraScale.Platform (committed TypeScript OpenAPI descriptor) | TerraScale.DotNet | Existing backward-compatible API, then SDK | Focused route, serialization, 204, failure, retry, and project-header-conflict tests |

- Added `client.Access` typed list/add/update/remove methods, with page `skip`/`take`, URI escaping, source-generated JSON metadata, descriptor-required response fields, and string roles matching the descriptor. This preserves positional constructor/`Deconstruct` compatibility from the initial client delivery and required-property object-initializer compatibility from the later strict-model hardening.
- Membership mutations intentionally ignore configured idempotency/retry settings; delete accepts only successful `204` and preserves typed public errors.
- Management endpoints require a suitable owner bearer identity; the recording transport test only verifies configured API-key/project headers are attached and does not assert endpoint acceptance of that credential.
- The opt-in bearer authorization E2E gate requires `TERRASCALE_E2E_BASE_URL`, `TERRASCALE_E2E_PROJECT_ACCESS_PROJECT_ID`, `TERRASCALE_E2E_PROJECT_ACCESS_OWNER_BEARER_TOKEN`, `TERRASCALE_E2E_PROJECT_ACCESS_LOW_PRIVILEGE_BEARER_TOKEN`, and `TERRASCALE_E2E_ENABLE_PROJECT_ACCESS_LIFECYCLE=true`. It proves owner read success and lower-principal read authorization denial only. All live mutation roundtrips are deliberately deferred until Platform provides a server-enforced atomic disposable fixture or create-only operation contract; this is a production safety decision.
