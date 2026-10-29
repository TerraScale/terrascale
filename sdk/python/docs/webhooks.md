# Collection webhooks

```python
from terrascale.webhooks import CollectionWebhooks

hooks = CollectionWebhooks(client.database("main").collection("people"))
# Use only an approved public HTTPS receiver you control.
created = hooks.create_webhook(receiver_url)
# Store created.secret directly in your private signing-key store, without logs.
registrations = hooks.list_webhooks(limit=50)
deliveries = hooks.list_webhook_deliveries(created.id, limit=50)
# Stop retries and remove history when the registration is no longer needed:
hooks.delete_webhook(created.id)
```

`AsyncCollectionWebhooks` has identical awaitable methods. Reads require
`documents:read`; create/delete require both `documents:read` and
`documents:write`. Creation starts at the current local feed head; at most 32
registrations per collection. The base64url 32-byte signing secret is returned
once, excluded from repr, and unavailable from lists. Receiver URLs are also
excluded from repr because they may contain private query parameters.

Lists are direct arrays in ascending ULID order, not page envelopes. Continue
using the last item's ID as `after` until empty; limit 1–100 defaults to 50.
`feed_gap` requires reconciling retained-history loss and recreating the webhook.
Delivery history retains only the latest 100 entries, with exact attempt counts,
nullable attempt times/status/error and no bodies or signing secrets.

DECISION: redact credential-bearing API errors. Create
and delete never replay. After an uncertain create, list to reconcile; a lost
creation-only key cannot be recovered. Deletion stops future retries but an
in-flight delivery can finish. Do not serialize dataclass secrets with `asdict`.
The server enforces public DNS/receiver safety; these clients make no receiver
requests. Register a receiver you control and account for its delivery costs.
