# A ts-auth restart signs nobody out

Reported 2026-10-29: "ts-auth keeps ASP.NET DataProtection keys in memory, so every restart invalidates every cookie
and antiforgery token". ts-auth does log ASP.NET's warning about storing keys in a non-persisted directory. But no
ts-auth credential, cookie or form depends on DataProtection:

| What | How it is protected | Survives a restart because |
| --- | --- | --- |
| Hosted session (`tsauth_session`) | opaque random token, its hash stored in TerraBase (`sessions`) | the record is in TerraBase |
| Authorization codes, refresh tokens | OpenIddict reference tokens, signed and encrypted with keys derived from the OCI Vault master key | every replica derives the same keys |
| Access and identity tokens | ES256, signed by the key ring stored encrypted in TerraBase | the ring is in TerraBase |
| Social and SSO state cookies | sealed with a key derived from the master key | every replica derives the same key |
| Hosted form posts | Origin check against the public origin; no antiforgery tokens | no per-process secret |

`OpenIddict.Server.DataProtection` is not referenced, and ts-auth never calls `IDataProtector` or antiforgery.
`RestartSurvivalTests` proves this end to end. It signs in, stops the server and starts a new process on the same
storage, which gets fresh in-memory DataProtection keys. The session cookie still authorizes, and the access token,
refresh token and a new code still work.

Adding a TerraBase-backed DataProtection key ring would protect nothing today, so none is added. If a future feature
uses `IDataProtector`, antiforgery or cookie authentication, it must first persist the ring: an `IXmlRepository` on
an auth-owned TerraBase collection, with keys encrypted by an `IXmlEncryptor` keyed from an HKDF subkey of the master
key. If users are signed out by ts-auth restarts, look at the client's own session store, for example ts-dashboard's
cookies.
