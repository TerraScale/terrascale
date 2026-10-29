# Account, project and key JSON

`--json` supports auth login/logout, whoami, projects/create and keys list/create/rotate/revoke.
Login prints its approval URL and user code to stderr, then emits `{signedIn:true}`
after saving credentials. Logout emits `{signedIn:false}`. Tokens are never returned.
Whoami returns `{sub,email}` after an authenticated API request, not token contents.
Project/key lists are arrays, including `[]`; creation returns SDK metadata.
Only explicit `keys create` and `keys rotate` return the one-time secret, in `key` alongside
`api_key` metadata. Key lists never include secrets. Revocation returns `{id,revoked:true}`.
A failure produces the common error envelope and retains exit status 1 or 2.
