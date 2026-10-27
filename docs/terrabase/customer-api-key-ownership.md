# Customer API key ownership

ts-auth is the sole customer API key issuer. Create and manage keys through its
`/v1/terrascale/organizations/{organization}/projects/{project}/api-keys` API,
then exchange the client credentials for a short-lived access token.
The shared scope vocabulary is exactly `documents:read`, `documents:write`,
`databases:read`, and `databases:write`. ts-auth rejects unknown scopes when
creating a key and rejects a token request whose scopes exceed the key's grants.
TerraBase checks the granted scopes and project/database boundaries on requests.

Breaking change: TerraBase no longer serves POST, GET or DELETE at
`/v1/management/organizations/{organization}/projects/{project}/api-keys`
or its `/{key}` child. There is no compatibility route. No known clients require
these routes: the dashboard, SDKs and customer documentation use ts-auth's API.

The internal [operator key API](operator-api-keys.md), `tb_` authentication and
shape access remain. Operator keys can hold only the two document scopes;
database management scopes remain exclusive to ts-auth access tokens.
Project closure still revokes operator keys and project deletion still refuses
projects containing them. Their internal project index stores only key hashes;
the customer list metadata, ordering sequence and response types are removed.
This catalog change needs the deploy lane's batched format bump; no migration or format-version edit is included.
