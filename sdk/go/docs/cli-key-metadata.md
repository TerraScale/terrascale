# API key list metadata

`ts keys list` includes CREATED BY, CREATED AT and LAST USED alongside the key's
ID, name, scopes, database, expiry and allowed networks. Timestamps are UTC;
LAST USED is `never` when no successful token exchange has been recorded.
The service's last-use timestamp may lag successful token exchange by five minutes;
it does not measure individual TerraBase requests.

`--json` returns the direct SDK `[]APIKey` array. Metadata uses `created_by`,
`created_at`, `last_used_at` (timestamp or null), and `previous_secret_expires_at`.
Neither a list nor subsequent lookup retrieves a secret. Only explicit creation
and rotation return `{api_key,key}`. All pages finish before any list is printed.
