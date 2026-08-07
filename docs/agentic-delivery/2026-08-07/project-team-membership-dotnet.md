# Project team membership .NET SDK delivery

| Contract surface | Producer | Consumer | Landing order | Compatibility validation |
| --- | --- | --- | --- | --- |
| `/api/v1/projects/{projectId}/access/team-members` | TerraScale.Platform (committed TypeScript OpenAPI descriptor) | TerraScale.DotNet | Existing backward-compatible API, then SDK | Focused route, serialization, 204, failure, retry, and project-header-conflict tests |

- Added `client.Access` typed list/add/update/remove methods, with page `skip`/`take`, URI escaping, source-generated JSON metadata, descriptor-required response fields, and string roles matching the descriptor. The original positional constructors and `Deconstruct` APIs for page and member models are preserved for source and binary compatibility.
- Membership mutations intentionally ignore configured idempotency/retry settings; delete accepts only successful `204` and preserves typed public errors.
- Management endpoints require a suitable owner bearer identity; the recording transport test only verifies configured API-key/project headers are attached and does not assert endpoint acceptance of that credential.
- The opt-in lifecycle/auth E2E gate requires `TERRASCALE_E2E_BASE_URL`, `TERRASCALE_E2E_PROJECT_ACCESS_PROJECT_ID`, `TERRASCALE_E2E_PROJECT_ACCESS_OWNER_BEARER_TOKEN`, `TERRASCALE_E2E_PROJECT_ACCESS_LOW_PRIVILEGE_BEARER_TOKEN`, `TERRASCALE_E2E_PROJECT_ACCESS_MEMBER_USER_ID`, `TERRASCALE_E2E_PROJECT_ACCESS_ADD_ROLE`, `TERRASCALE_E2E_PROJECT_ACCESS_UPDATE_ROLE`, `TERRASCALE_E2E_PROJECT_ACCESS_CONFIRMED_DISPOSABLE=true`, and `TERRASCALE_E2E_ENABLE_PROJECT_ACCESS_LIFECYCLE=true`. It exhaustively pages before mutation, skips if target absence cannot be proven, and verifies cleanup by exhaustive paging. It remains skipped without a dedicated disposable membership fixture and separate lower-privilege bearer.
