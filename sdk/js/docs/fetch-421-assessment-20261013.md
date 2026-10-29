# HTTP 421: native fetch, SDK retries and mutation effects

The SDK issues one fetch call. Chrome154.0.8037.57 and Edge154.0.4258.37 each retransmit buffered PATCH and bodyless DELETE once on421. Those are two physical requests. The rejection fixture records zero applied effects. No current immutable producer was run, so there is no claim about observed real producer effects.

## Primary evidence

1. [WHATWG Fetch, HTTP-network-or-cache fetch](https://fetch.spec.whatwg.org/#http-network-or-cache-fetch) retries when status421, isNewConnectionFetch=false, and body is null or its source is non-null. It sets response to another HTTP-network-or-cache fetch on a new connection. `redirect: "manual"` does not disable this separate rule. RequestInit has no retry-control field.
2. [RFC9110 §15.5.20](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.20): “A client that receives a 421 (Misdirected Request) response MAY retry the request, whether or not the request method is idempotent, over a different connection”. A proxy “MUST NOT generate a 421 response”. TerraBase's placement/edge interpretation therefore requires its owning producer to review authoritative origin/gateway behavior.
3. [Chromium network source](https://chromium.googlesource.com/chromium/src/+/main/net/http/http_network_transaction.cc): the421 branch checks `!has_body_with_null_source`, disables IP pooling/alternative services and calls `ResetConnectionAndRequestForResend(RetryReason::kHttpMisdirectedRequest)`. This independently corroborates the specification and observed real browser requests; the moving source URL is not claimed as the exact installed binary revision.
4. [Chrome request-stream documentation](https://developer.chrome.com/docs/capabilities/web-apis/fetch-streaming-requests) requires `duplex: "half"` and says “The fetch will be rejected if the connection is HTTP/1.x.” Streams also need CORS preflight and cannot follow ordinary redirects. [MDN Request.duplex](https://developer.mozilla.org/en-US/docs/Web/API/Request/duplex) marks the feature limited/experimental across browsers. It is not a portable replacement for this SDK's supported Node/browser fetch contract.

These documents were retrieved during this audit. The real-browser fixture uses native globalThis.fetch; its counting wrapper delegates directly to that function and cannot suppress network retries.

## Standards-supported prevention assessed

| Candidate | Assessment / real fixture evidence |
| --- | --- |
| `redirect: "manual"` | Already used; native PATCH and DELETE still send twice on421. It controls redirect following, not421. |
| Buffered string, Blob or other replayable body | Fetch extracts a non-null body source, so the421 retry remains eligible; changing media/container does not provide a standard no-retry guarantee. No replacement introduced. |
| One-shot ReadableStream body | Source is null, excluding this421 retry rule. Proven for native Node22/24 mutation requests, including empty DELETE. That is the scoped implementation. |
| Same stream in Chrome/Edge on existing HTTP/1 fixture | Actual native fetch rejects with TypeError and sends zero application requests. Both diagnostic tests pass. HTTP/2/3 stream behavior was not qualified, and changing the SDK to require it would break supported transports; no browser workaround shipped. |
| Cache, keepalive, AbortSignal or an SDK counter | No documented421 prevention switch. AbortSignal is cancellation, not a reliable rejection-before-retry hook; the first421 is hidden from application code. No speculative timing workaround added. |

The browser assertions demanding exactly one wire PATCH/DELETE remain failing, with one SDK fetch call asserted separately. The HTTP/1 stream diagnostic is an assessment of limitations, not a replacement SDK compatibility test. There is no claim of full browser compatibility.

## Source-backed integration boundary

At server `e30ffdc287530551b161b0130220be53d3c0475e`:

- `DatabaseRegions.cs` placement middleware returns WrongRegion before `next(context)` for databases not served in this region. `Program.cs` registers it after authentication/authorization and before endpoint mapping.
- `CreateManagedDatabaseEndpoint.cs`, `UpdateManagedDatabaseEndpoint.cs`, `BranchManagedDatabaseEndpoint.cs` and `DeleteManagedDatabaseEndpoint.cs` reject wrong home/placement before management mutation calls.
- `DocumentRoute.CheckIdempotencyKey` rejects the header; no stable general mutation identity/result replay contract is exposed.

The source-backed requirement is **all421 misdirected writes must be rejected before any mutation**, including forwarding/edge paths. Under that invariant transport retransmission produces repeated rejection, not duplicate effects. The current source supports that ordering in the inspected paths; actual deployment/runtime proof remains open.

Affected browser mutations are document PUT/POST/PATCH/DELETE, bounded transaction POST, collection POST/DELETE, declared-schema PUT/DELETE and database create/update/delete/branch. The fixture directly demonstrates PATCH and bodyless DELETE; Fetch's rule establishes buffered/absent-body eligibility for the rest.

Ambiguous errors after commit are a separate problem. Automatic recovery would need a producer-published stable operation identifier, scoped atomic deduplication, request-fingerprint conflict behavior, original-result replay, retention boundaries and a reconciliation operation. The SDK cannot invent these fields or silently retry. Existing client-generated PUT keys identify an address; CAS protects a condition. Neither provides general exactly-once result semantics. Status changes or new idempotency APIs remain with the producer owner.
