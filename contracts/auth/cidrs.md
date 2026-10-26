# Project and API-key IP allowlists

Source: [cidr-provenance.json](cidr-provenance.json), producer `a3cbf8b1c73d304189a489fa8033d41f3b508de3`. The supplemental pin owns the CIDR fields and setter operations in `dtos.json`, `routes.json` and the platform fixtures; older contract entries keep their existing provenance.

`Platform.SetProjectAllowedCidrsAsync` requires project admin. `SetProjectApiKeyAllowedCidrsAsync` requires project member, and `SetOrganizationApiKeyAllowedCidrsAsync` requires organization admin. All take the user's sign-in token first, a `TerraScaleAuthPlatformAllowedCidrs` record and optional cancellation token. Requests use JSON, return 200 with project/key metadata, and are sent once; 400/401/403/404/503 remain typed exceptions. No setter is paginated.

```csharp
await auth.Platform.SetProjectAllowedCidrsAsync(signInToken, organizationId, projectId,
    new TerraScaleAuthPlatformAllowedCidrs(["192.0.2.0/24", "2001:db8::/32"]), cancellationToken);
```

The producer validates up to 32 distinct normalized IPv4/IPv6 CIDRs or bare addresses, clears host bits and removes duplicates. An empty list clears restrictions; null/missing setter lists and invalid values return 400. Create-key requests accept optional `AllowedCidrs` (default null); absent, null or empty means unrestricted. SDK requests retain null as JSON null. Project and API-key results require non-null `AllowedCidrs` lists, including empty lists; missing/null result fields are invalid responses. These required constructor fields change direct DTO construction; supply the actual returned CIDRs rather than inferring unrestricted access.

Project and key restrictions both apply to subsequently issued tokens, within 60 seconds. TerraBase rejects disallowed addresses with 403 `ip_not_allowed`; the SDK neither bypasses authorization nor infers the caller's IP. Existing tokens are not immediately revoked. Live own-resource allow/deny qualification remains separate from fake-handler contract tests.
