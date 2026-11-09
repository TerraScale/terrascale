# End-user lifecycle webhooks

A project registers webhook endpoints to observe its end users' lifecycle. This
page covers registration; delivery arrives with later slices.

## Routes

All routes are project-scoped under `/v1/{project}/webhook-endpoints` and need
the project's admin role.

| Method and path | Role | Response |
| --- | --- | --- |
| `GET /webhook-endpoints` | admin | `{items: [WebhookEndpoint], next: null}`, never secrets |
| `POST /webhook-endpoints` | admin | `201 WebhookEndpoint` with `secret` once; `create_webhook_endpoint` step-up proof required; a concurrent create or delete winning the race answers `409 version_conflict` |
| `DELETE /webhook-endpoints/{id}` | admin | `204` |
| `POST /webhook-endpoints/{id}/rotate-secret` | admin | `WebhookSecret` with `secret` once; `rotate_webhook_secret` step-up proof required; a concurrent change to the endpoint answers `409 version_conflict` |

```text
POST /v1/{project}/webhook-endpoints
{ "url": "https://hooks.example.test/lifecycle",
  "event_types": ["user.signed_up", "user.disabled"] }

201 { "id": "01JD…", "url": "https://hooks.example.test/lifecycle",
      "event_types": ["user.signed_up", "user.disabled"],
      "created_at": "2026-11-10T12:00:00Z", "secret": "whsec_…" }

POST /v1/{project}/webhook-endpoints/{id}/rotate-secret
200 { "id": "01JD…", "secret": "whsec_…", "previous_secret_expires_at": "2026-11-11T12:00:00Z" }
```

## Registration rules

- `url` is an absolute `https` URL of at most 2048 characters, without userinfo
  or fragment. It is stored and answered in its normalized form. Anything else
  fails with `400 invalid_request`.
- `event_types` is a non-empty duplicate-free subset of `user.signed_up`,
  `user.signed_in`, `user.disabled`, `user.enabled`, `user.deleted`,
  `user.provider_linked` and `user.provider_unlinked`.
- A project registers at most 3 endpoints; a further registration answers
  `409 webhook_endpoint_limit`, and a concurrent create or delete that wins the
  race answers `409 version_conflict`.

## Secrets

`secret` starts with `whsec_` and is answered exactly once, on registration and
on rotation; the list never includes it. It is stored sealed under the ts-auth
master key, bound to the project and endpoint, like the project social
providers' client secrets. Rotation keeps the previous secret verifying for 24
hours, then it is refused.

Registration and rotation consume a purpose-bound step-up proof
([step-up proof](step-up-proof.md)); deletion needs only the admin role.

## Events

Each end-user lifecycle change writes one event record in the same store
transaction as the change itself, so an event exists if and only if the change
committed. Events are written only when the project has at least one registered
endpoint subscribed to that event type; nothing is dispatched yet (delivery
arrives with a later slice).

| Event | Written when |
| --- | --- |
| `user.signed_up` | an end user's account is created in the project (sign-up code redemption, first social sign-in) |
| `user.signed_in` | a sign-in completes and a session opens (password, email code, passkey, MFA, social, SSO) |
| `user.disabled` | an admin disables the end user |
| `user.enabled` | an admin re-enables the end user |
| `user.deleted` | the end user's account is deleted |
| `user.provider_linked` | a social identity is linked to the end user |
| `user.provider_unlinked` | a social identity is unlinked from the end user |

A first social sign-in emits both `user.signed_up` and `user.signed_in`, and a
hosted sign-up that opens a session does too; each is delivered per its own
subscriptions. Platform `terrascale` accounts never emit events: these are
end-user lifecycle events of a project only.

Each event record carries the project id, the event type, a ULID event id and
occurred-at timestamp, and a minimal payload: the end user's id and email
address (the project already owns both through its admin APIs), plus the
provider name on `user.provider_linked` and `user.provider_unlinked`. No other
personal data is stored. Delivery state starts at `status: "pending"` with
`attempts: 0` and a `next_attempt_at` equal to the occurred-at timestamp.

## Verify deliveries

Deliveries follow the [Standard Webhooks](https://www.standardwebhooks.com)
spec: the `webhook-id`, `webhook-timestamp` (unix seconds) and
`webhook-signature` headers, with space-separated `v1,<base64>` signature
entries. The signature is HMAC-SHA256, keyed with the base64-decoded payload of
the `whsec_` secret, over `{id}.{timestamp}.{body}` with the body exactly as
delivered. Verify every `v1` entry in constant time (rotation keeps the
previous secret's signatures valid for 24 hours) and refuse timestamps more
than 5 minutes from now. Each SDK ships one `verifyWebhook` helper that does
this and returns the parsed event, or the SDK's typed error.

```csharp
using TerraScale.DotNet;

JsonElement evt = TerraScaleWebhooks.VerifyWebhook(secret, request.Headers.ToDictionary(h => h.Key, h => h.Value.First()), rawBody);
```

```js
import { verifyWebhook } from "@terrascale/sdk/ts-auth/webhooks";

const event = verifyWebhook(secret, req.headers, rawBody);
```

```go
import "terrascale.tech/sdk/tsauth"

event, err := tsauth.VerifyWebhook(secret, r.Header, rawBody)
```

```python
from terrascale import verify_webhook

event = verify_webhook(secret, headers, raw_body)
```

```rust
use terrascale::webhooks::{verify_webhook, WebhookHeaders};

let event = verify_webhook(secret, &WebhookHeaders { id, timestamp, signature }, &raw_body)?;
```
