# Private response cache policy

OIDC userinfo responses carry `Cache-Control: no-store` and `Pragma: no-cache` for successful identity claims, denials and failures. An early response-header callback applies this policy to the userinfo endpoint at header commit, including protocol errors before handler execution and generic error envelopes that clear prepared headers.

TerraBase defaults responses from endpoints requiring authorization to `Cache-Control: no-store` when no explicit Cache-Control is present. The policy runs when headers are committed, covers conditional document reads (including 304), and preserves ETags for preconditions. Public endpoints marked anonymous, discovery documents and static assets are outside this default.

Explicit policies remain authoritative: shape origin responses keep `private, no-store`, database change streams keep their streaming headers, and public regions and replication health keep their short public cache lifetimes. Encrypted shape objects in object storage retain their existing immutable ciphertext cache policy; this change does not alter shape storage or keys.

Fast HTTP integration coverage checks userinfo success, missing/invalid tokens, invalid identity state, unavailable storage and invalid signing-key metadata failures; document success, missing records and 304; and explicit shape, SSE and public region policies. Existing error-header integration tests cover authorization denials and structured unexpected failures.

This is a response-header hardening change, with no public payload, SDK, storage-format or authentication-contract changes. Missing cache directives alone do not establish shared-cache disclosure. CI and live response verification are required before marking the rollout complete.
