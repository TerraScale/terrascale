---
title: Collection change webhooks
description: Register signed HTTPS notifications, verify deliveries and recover from retries or feed gaps.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Collection change webhooks

Register a public HTTPS receiver to receive batches of document change identities for one existing collection. Registration starts at the current change-feed head; it does not send existing data.

## Register a receiver and save its secret

1. Serve an HTTPS endpoint on port 443 with a valid public certificate. Its URL must have at most 2048 characters, no embedded credentials and no fragment. Every DNS answer must be a public IPv4 or IPv6 unicast address. Localhost, private, reserved, link-local, metadata and transition addresses are rejected. Redirects are not followed, and delivery does not use proxies.
2. Obtain an existing [project access token](/fr/guides/authentication) for the intended database. Listing hooks and delivery logs requires `documents:read`. Creation and deletion require **both** `documents:read` and `documents:write`; a write-only token cannot create a hook. There are no additional webhook scopes to request.
3. Send this request, replacing the project with its canonical uppercase ULID and the database and collection with their lowercase names:

```http
POST /v1/projects/{project}/databases/{database}/collections/{collection}/webhooks
Authorization: Bearer <project access token>
Content-Type: application/json

{"url":"https://receiver.example/events"}
```

Use your authorized TerraBase API endpoint. The illustrative receiver hostname must be replaced with your publicly reachable receiver.

A successful response is `201 Created`, with `Location` and `Cache-Control: no-store` headers and this body:

```json
{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAX","url":"https://receiver.example/events","createdAt":"2026-10-23T12:00:00Z","status":"active","secret":"<creation-only base64url key>"}
```

Store `secret` immediately in your receiver's secret manager, indexed by this webhook ID. It is a base64url-encoded 32-byte HMAC key, unique to the hook. Never log the response or expose the key in a browser, URL or source repository. List and log calls never return it. If you lose it or need to rotate it, delete this registration and create another; reconcile data across the replacement interval.

Do not blindly repeat creation after a lost response: the registration may exist and its one-time secret may be lost. List registrations to reconcile the result, delete an inaccessible registration and create a replacement.

## Verify the exact request bytes

TerraBase sends HTTPS `POST` requests with `Content-Type: application/json` and these headers:

| Header | Value |
| --- | --- |
| `X-TerraScale-Delivery` | Delivery ULID, stable across retries |
| `X-TerraScale-Timestamp` | Unix seconds, refreshed for each attempt |
| `X-TerraScale-Signature` | `v1=` followed by the lowercase hexadecimal HMAC-SHA256 |

Decode the stored secret as base64url bytes. Compute HMAC-SHA256 over the timestamp text, a literal `.` and the **raw request body bytes**. Compare signatures in constant time and reject timestamps more than 5 minutes in the past or future. Synchronize the receiver's clock. Parsing and serializing JSON before verification changes the signed bytes.

This Node.js verifier expects a `Buffer` captured before any JSON middleware and Node's lowercase request-header names. Load `secret` and `webhookId` for the specific receiver from your secret manager. Do not select an arbitrary key based on unverified body fields.

```js
import { createHmac, timingSafeEqual } from 'node:crypto'

export function verifyWebhook(rawBody, headers, secret, webhookId, now = Date.now()) {
  const timestamp = headers['x-terrascale-timestamp']
  const signature = headers['x-terrascale-signature']
  if (!Buffer.isBuffer(rawBody) || rawBody.length > 512 * 1024 ||
      typeof timestamp !== 'string' || !/^\d{1,12}$/.test(timestamp) ||
      Math.abs(now / 1000 - Number(timestamp)) > 300 ||
      typeof signature !== 'string' || !/^v1=[0-9a-f]{64}$/.test(signature)) {
    throw new Error('Invalid webhook authentication')
  }
  const key = Buffer.from(secret, 'base64url')
  if (key.length !== 32) throw new Error('Invalid stored webhook key')
  const expected = createHmac('sha256', key)
    .update(timestamp).update('.').update(rawBody).digest()
  if (!timingSafeEqual(expected, Buffer.from(signature.slice(3), 'hex'))) {
    throw new Error('Invalid webhook signature')
  }
  const payload = JSON.parse(rawBody.toString('utf8'))
  if (!payload || typeof payload.id !== 'string' ||
      !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(payload.id) ||
      payload.id !== headers['x-terrascale-delivery'] || payload.webhookId !== webhookId) {
    throw new Error('Invalid webhook identity')
  }
  return payload
}
```

Enforce the 512 KiB body limit while reading the HTTP request, before buffering it. Reject failed verification without applying effects. Signed bodies contain change identities rather than document values:

```json
{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAY","webhookId":"01ARZ3NDEKTSV4RRFFQ69G5FAX","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","database":"orders","collection":"items","events":[{"origin":"<opaque origin>","version":"<opaque version>","id":"<document key>","deleted":false}]}
```

Fetch current document state through the document API if needed; it may already differ from the changed value. `deleted: true` identifies a deletion. Collection/index changes and collection drops do not emit document events. Batches contain whole source transactions, at most 1024 events and 512 KiB encoded. There is no global ordering across origins.

## Acknowledge and deduplicate

1. Verify the signature and identities before processing.
2. Use a durable unique delivery-ID record to deduplicate. Commit that record atomically with your effects, or use an idempotent durable inbox before acknowledging. A separate “seen” write followed by an effect can lose work after a crash. Return success for an already committed delivery.
3. Return any `2xx` only after durable acceptance. Complete within 10 seconds. All other statuses, including redirects and authentication failures, and DNS/TLS/connect errors or timeouts trigger retries.

Delivery is at least once. A crash after your receiver succeeds can cause another attempt with the **same body and delivery ID**, a fresh timestamp and a fresh signature. Backoff is 1, 2, 4, … seconds, capped at 1 hour, with no retry-count limit. Each hook has at most one pending batch and one in-flight attempt; another hook progresses independently. Feed replay can repeat event identities in a different delivery, so also deduplicate `(origin, version, collection, id)` where your effects require it. Keep identity records for your application's full replay/reconciliation horizon; the five-minute signature window is not a deduplication retention period.

## List registrations and delivery logs

All paths below extend the registration URL. Use `Authorization: Bearer <project access token>` with `documents:read`:

| Request | Response |
| --- | --- |
| `GET …/webhooks?limit=50` | Direct JSON array of registrations, with no secret |
| `GET …/webhooks/{webhook}/deliveries?limit=50` | Direct JSON array of retained delivery records |

Both list routes accept `limit=1..100` (default 50) and `after=<canonical uppercase ULID>`. IDs are ascending. Continue using the final item's ID as `after`; an empty array ends pagination. There is no `items` or `next` envelope. Each collection permits at most 32 registrations. Logs retain only the latest 100 records per hook, including a pending record; they are not a permanent audit archive.

```json
[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAY","createdAt":"2026-10-23T12:00:01Z","status":"pending","attempts":1,"lastAttemptAt":"2026-10-23T12:00:02Z","nextAttemptAt":"2026-10-23T12:00:03Z","httpStatus":503,"errorCode":"http_error"}]
```

Delivery status is `pending` or `delivered`. `lastAttemptAt`, `nextAttemptAt`, `httpStatus` and `errorCode` can be `null`. Failure codes are `http_error`, `transport_error` and `target_rejected`. Logs omit receiver bodies, exception text, signing headers and secrets. A rejected or unsafe DNS answer is retried, with address safety rechecked on every connection; mixed public/private DNS answers are rejected.

| HTTP status | Action |
| --- | --- |
| `400`, `409` | Correct the URL/pagination, or remove an unused hook if the registration limit was reached. |
| `401`, `403` | Refresh an expired token or use the required existing document permissions. Write-only credentials cannot create/delete hooks. |
| `404` | Check the project/database token scope and that the database and hook exist. Creation also requires an existing collection; listing registrations does not. |
| `503` | Retry reads with backoff; reconcile uncertain creation/deletion before repeating mutations. |

## Recover a feed gap or remove a hook

After a collection is dropped, retained webhook registrations, their status and delivery logs remain accessible under the same collection name. You can still list and reconcile these hooks, inspect their logs or delete them. Listing returns `200 OK` with `[]` when no registrations exist, including for a missing collection; creating a hook for a missing collection returns `404`.

Registration status is `active` or `feed_gap`. Change history is retained for 7 days. Excessive downtime/backlog, missing retained history, a source transaction exceeding the 512 KiB batch limit or recreation of the database/collection stops scanning with `feed_gap`; already captured pending batches keep retrying. Delivery resumes after a restart of its owning service, but does not automatically transfer to a different owner. Do not assume failover or delivery beyond retained history.

For `feed_gap`, reconcile your application's current data, delete the registration and create a new one to resume at the current head. Coordinate reconciliation with writes, or perform a final reconciliation after registering, because a replacement does not backfill changes during the replacement interval.

Send `DELETE …/webhooks/{webhook}` with both `documents:read` and `documents:write`. Success is `204 No Content`; an unknown hook is `404`. Deletion stops future attempts, but an in-flight request can still finish. Retain deduplication records long enough to handle that request safely.

</div>
