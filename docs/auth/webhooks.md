# Authentication webhooks and events

Projects receive signed HTTPS deliveries for end-user identity changes. Platform
`terrascale` accounts never emit these events. Every project event is recorded
atomically with its change, even without a webhook subscription, and is retained
for 30 days. The events API excludes expired records immediately; an hourly job
deletes them and their delivery history.

## Routes

Signed-in platform users need the project admin role, or the member role for
events. Project secret API keys need auth:read for events and auth:write for
endpoint and delivery management. Creating endpoints and rotating their secrets
still require a person's step-up proof.
Paths are relative to `/v1/{project}`.

| Method and path | Result |
| --- | --- |
| `GET /webhook-endpoints` | `{items, next: null}`; never secrets; `disabled_at` identifies disabled endpoints |
| `POST /webhook-endpoints` | `201` endpoint plus `secret` once; `create_webhook_endpoint` step-up proof required |
| `DELETE /webhook-endpoints/{id}` | `204`; removes endpoint and its delivery history |
| `POST /webhook-endpoints/{id}/rotate-secret` | New `secret` once; `rotate_webhook_secret` step-up proof required |
| `GET /webhook-endpoints/{id}/deliveries` | Latest 20 deliveries, each with latest 10 attempts |
| `POST /webhook-endpoints/{id}/deliveries/{delivery}/replay` | `204`; enqueue the original event for another 24-hour delivery cycle |
| `POST /webhook-endpoints/{id}/test` | Create and enqueue a `webhook.test` event directly to this endpoint |
| `GET /events?after={cursor}&limit=50` | `{items, next}` in ascending event ULID order; limit 1–100 |

The dashboard exposes endpoint deliveries, replay and test actions under
**Authentication → Webhooks**, and the retained event stream under **Logs**.
`next` is the last returned event id when more records exist; pass it as `after`.
The database clock applies both the retention cutoff and the five-second settle
window. Events become visible after that window to allow commits from
other nodes to arrive before advancing an ULID cursor. This assumes commits and
clock skew stay within that window. Endpoint events have their own `webhook.*`
type, an empty `user_id`, and an `endpoint_id`.
Replay keeps the original event body and `webhook-id`, and refreshes the signature
timestamp. A replay while the endpoint is delivering answers `409`.

Registration accepts at most three endpoints per project, an HTTPS URL of at most
2048 characters without userinfo or a fragment, and a non-empty, duplicate-free
subset of the events below. Subscription is evaluated when the worker expands
the event to the existing endpoints; newly registered endpoints receive only
events occurring after registration. Test events bypass subscription filtering.

## Events

| Event | Trigger |
| --- | --- |
| `user.signed_up` | A project account is created by email verification or first social sign-in |
| `user.signed_in` | A sign-in completes |
| `user.updated` | The end user completes a verified email change |
| `user.disabled` | An admin disables an end user |
| `user.enabled` | An admin re-enables an end user |
| `user.deleted` | An end user is deleted |
| `user.provider_linked` | A social identity is linked |
| `user.provider_unlinked` | A social identity is unlinked |
| `session.created` | A session is issued, including rotation successors |
| `session.revoked` | A session is explicitly revoked or sessions are revoked through sign-out everywhere |
| `webhook.endpoint_disabled` | An endpoint has failed continuously for five days |
| `webhook.test` | An admin sends a test event |

```json
{
  "id": "01K9EXAMPLE0000000000000000",
  "type": "session.created",
  "timestamp": "2026-11-12T12:00:00Z",
  "data": {
    "user_id": "01K9EXAMPLE0000000000000001",
    "email": "person@example.com",
    "provider": null,
    "session_id": "01K9EXAMPLE0000000000000002",
    "endpoint_id": null
  }
}
```

The endpoint-disabled event is persisted for the project owner to consume through
the authenticated events API and dashboard Logs; other subscribed, working
endpoints also receive it. It contains the endpoint id, without signing secrets.
Invalidation caused indirectly by credential or user changes is represented by
that user lifecycle event; it does not emit one event per affected session.

## Delivery and retries

Projects are served in order of their most recent endpoint claim, least recent
first; each project gets its oldest due endpoint next. Each endpoint pass sends
at most ten deliveries within 30 seconds and stops after its first failure.
The pass deadline also cancels any in-flight request.

A database version claim leases the endpoint and delivery for two minutes. Only
one worker can claim an endpoint at a time across nodes. Attempts are persisted
before sending, with completion status, latency, HTTP status and the first 1024
response bytes. A restart recovers expired leases; unfinished attempts are marked
`interrupted`. Delivery is at least once: receivers must deduplicate by event id,
including after a crash between receiving a response and committing completion.

Any non-2xx response, connection failure or 20-second deadline causes a retry.
Backoff starts at five seconds, doubles per failed attempt, and caps at six
hours before jitter. Each delay is multiplied by a random factor from 0.5 to 1.5.
Four independent delivery lanes claim the oldest due delivery per unleased
endpoint, choosing the least recently served project before that project's
oldest due endpoint. Each lane sends up to ten events per endpoint in event-id
order before selecting endpoints again. Busy lanes continue immediately; idle
lanes check due work every two seconds. Expansion and retention run separately;
the final retry scheduled at 24 hours is sent before exhaustion, measured from the
first actual attempt of the current delivery cycle, including a replay. Queued
deliveries with no attempts never expire because of backlog. The nominal sequence is 5s, 10s, 20s, 40s, 80s, 160s,
320s, 640s, 1280s, 2560s, 5120s, 10240s, 20480s, then 21600s.

A successful delivery resets the endpoint failure streak. After five continuous
days since its first failure without a success, the worker disables the endpoint
and emits `webhook.endpoint_disabled` when another failed attempt completes.
A gap of more than ten hours between attempts starts a fresh streak, exceeding
the maximum nine-hour retry interval with jitter. Inactivity alone never disables
an endpoint. The disable transaction also reserves one notification email per
project owner or admin in the existing email ledger; accepted or uncertain sends
are never sent again. Register a working replacement; disabled endpoints reject test/replay.
The email's sending claim has a recoverable two-minute lease and uses the
organization's email tenant. Its URL shows only the scheme, host and first path
segment; later segments are masked and the query is omitted.

Delivery never follows redirects or uses proxies. DNS resolution happens inside
the socket `ConnectCallback`, which requires every resolved address to be public
and connects directly to a validated address. Private, loopback, link-local,
metadata and IPv6 transition addresses are refused. HTTPS certificate validation
remains enabled, with a ten-second connection deadline.

## Secrets and verification

The `whsec_` secret is generated from 32 random bytes and stored encrypted under
the master key, bound to project and endpoint. It is returned only on creation or
rotation. Rotation signs with both current and previous secrets for 24 hours.
Delivery logs contain neither secrets nor request headers.

Use the raw body bytes before parsing JSON. Standard Webhooks signs
`{webhook-id}.{webhook-timestamp}.{body}` with HMAC-SHA256 using the base64-decoded
secret after `whsec_`. `webhook-signature` contains space-separated `v1,<base64>`
entries. Accept any valid v1 signature in constant time and reject timestamps more
than five minutes from the current time. Deduplicate only after verification.

```js
import { createHmac, timingSafeEqual } from "node:crypto";

function verify(secret, headers, rawBody) {
  const id = headers["webhook-id"];
  const timestamp = headers["webhook-timestamp"];
  if (!id || !/^\d+$/.test(timestamp ?? "") ||
      Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) throw Error("Invalid timestamp");
  const expected = createHmac("sha256", Buffer.from(secret.slice(6), "base64"))
    .update(`${id}.${timestamp}.`).update(rawBody).digest();
  const valid = String(headers["webhook-signature"]).split(" ").some(entry => {
    if (!entry.startsWith("v1,")) return false;
    const signature = Buffer.from(entry.slice(3), "base64");
    return signature.length === expected.length && timingSafeEqual(signature, expected);
  });
  if (!valid) throw Error("Invalid signature");
  return JSON.parse(rawBody.toString("utf8"));
}
```

The .NET SDK verifier implements timestamp validation and constant-time signature
comparison, including multiple v1 entries:

```csharp
using System.Text.Json;
using TerraScale.DotNet;

using var reader = new StreamReader(request.Body);
var rawBody = await reader.ReadToEndAsync();
JsonElement evt = TerraScaleWebhooks.VerifyWebhook(
    secret,
    request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString()),
    rawBody);
```
