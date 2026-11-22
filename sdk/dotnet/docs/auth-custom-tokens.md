# Custom tokens

Available from this source checkout; this operation is not in the published package.

A backend with a project key carrying only `auth:tokens:mint` can mint a five-minute token for a verified user.
The key must belong to the project and have no database restriction. The user ID is a canonical uppercase ULID;
map other identity-system IDs on your backend. No tenant user account is required.

```csharp
var token = await auth.Projects.MintCustomTokenAsync(
    projectApiKey, projectId, verifiedUserId, database: databaseId,
    claims: new Dictionary<string, JsonElement>
    {
        ["plan"] = JsonElement.Parse("\"pro\""),
        ["roles"] = JsonElement.Parse("""["editor"]""")
    },
    cancellationToken: cancellationToken);
return Results.Json(new { access_token = token.AccessToken, expires_in = token.ExpiresIn });
```

Return that response from your authenticated backend to the browser over HTTPS. Keep the project key on the backend.
The browser sends the access token as Bearer on Rules reads and writes:

```javascript
const { access_token } = await (await fetch("/api/data-token", { method: "POST" })).json();
const response = await fetch(
  `https://api.terrascale.tech/v1/projects/${project}/databases/${database}/tables/notes/rows/${key}`,
  { headers: { Authorization: `Bearer ${access_token}` } }
);
const { row } = await response.json();
```

`database` and `claims` are optional. A database-bound token gets `404 unknown_database` elsewhere.
Claims have at most 32 members and 1000 serialized bytes; names match `[a-z][a-z0-9_]{0,62}`.
Values are strings up to 256 characters, finite numbers, booleans, or arrays of up to 64 strings or numbers.
Roles belong in `claims.roles`. Authorize every claim on the backend; holders can read the claims.

The route is `POST /v1/{project}/custom-tokens`. The returned `AuthRefused` preserves `403 insufficient_scope`,
`404 not_found`, and `429 rate_limited` with `RetryAfter`. Every mint is sent once, including rate refusals.
Tokens have no refresh token: request another from your backend before `ExpiresIn` elapses.
The token's diagnostic representation is redacted.

See the [route contract](../contracts/auth/custom-tokens.json) and the producer's
[custom token contract](https://github.com/TerraScale/terrascale/blob/main/docs/auth/custom-tokens.md).
