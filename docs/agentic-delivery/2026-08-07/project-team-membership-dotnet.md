# Project team membership .NET SDK delivery

| Contract surface | Producer | Consumer | Landing order | Compatibility validation |
| --- | --- | --- | --- | --- |
| `/api/v1/projects/{projectId}/access/team-members` | TerraScale.Platform (committed TypeScript OpenAPI descriptor) | TerraScale.DotNet | Existing backward-compatible API, then SDK | Focused route, serialization, 204, failure, retry, and project-header-conflict tests |

- Added `client.Access` typed list/add/update/remove methods, with page `skip`/`take`, URI escaping, source-generated JSON metadata, and string roles matching the descriptor.
- Membership mutations intentionally ignore configured idempotency/retry settings; delete accepts only successful `204` and preserves typed public errors.
- Live API E2E remains environment-gated because this checkout has no dedicated disposable team-member fixture or configured service credentials.
