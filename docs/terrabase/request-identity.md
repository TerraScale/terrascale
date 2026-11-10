# Customer request identity

Every HTTP response carries a canonical ULID in `X-Request-ID`, including authorization and validation errors. Browser CORS exposes the header. Public IDs are replaced at ingress unless authenticated by first-party proof; identity never grants access. Edge replay retains it. Deploy the matching edge and service changes together for continuity.

First-party services use the existing Vault peer credential to authenticate correlation only: uppercase hexadecimal HMAC-SHA256 over UTF-8 `terrascale.request-id.v1\n` followed by the ID, keyed by the literal UTF-8 credential (not hex-decoded). The internal proof header is consumed before endpoint processing and stripped from responses. Only first-party transports propagate it; third-party callback clients do not. Structured request log scopes include `request_id`.

Authorized database log rows expose nullable `requestId` separately from the event `id`. Existing rows may have no identity. Retention remains best effort: newest 100 observations within 24 hours; `truncated` and `recordingLimited` describe loss. No query strings, record values, credentials or diagnostic lookup privilege are added. The dashboard's authorized Logs and support form carry the customer ID.

TerraBase signs outbound auth requests only for its configured HTTPS issuer origin (scheme, host and port); discovery-selected external JWKS hosts receive no correlation proof. Redirects remain disabled. Auth storage and authenticated internal peer transports carry the same request identity when invoked within a request.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
