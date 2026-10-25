# Customer request identity

Every HTTP response carries a canonical ULID in `X-Request-ID`, including authorization and validation errors. Browser CORS exposes the header. Public IDs are replaced at ingress unless authenticated by first-party proof; identity never grants access. Edge replay retains it. Deploy the matching edge and service changes together for continuity.

First-party services use the existing Vault peer credential to authenticate correlation only: uppercase hexadecimal HMAC-SHA256 over UTF-8 `terrascale.request-id.v1\n` followed by the ID, keyed by the literal UTF-8 credential (not hex-decoded). The internal proof header is consumed before endpoint processing and stripped from responses. Only first-party transports propagate it; vendor/webhook clients do not. Structured request log scopes include `request_id`.

Authorized database log rows expose nullable `requestId` separately from the event `id`. Existing rows may have no identity. Retention remains best effort: newest 100 observations within 24 hours; `truncated` and `recordingLimited` describe loss. No query strings, document values, credentials or diagnostic lookup privilege are added. The dashboard's authorized Logs and support form carry the customer ID.
