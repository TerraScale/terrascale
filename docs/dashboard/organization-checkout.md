# Organization checkout

Business organization creation waits for a paid Stripe Checkout Session and an active or trialing subscription with the selected published plan and a validated payment method. The wizard, direct dashboard creation endpoint and MCP create tool all start this draft flow. No organization, membership, audit entry or project is created while choosing a name or plan or while checkout is open.

Stripe creates the session before the dashboard inserts its pending row, with the draft ULID as its idempotency key. The row requires a unique session ID, a published paid plan and a billing interval. A concurrent insert adopts the winning row and expires its unused session. A Stripe idempotency conflict reads the existing session; an unresolved race returns a retryable 409.

Completion runs on the authenticated Stripe return naming the pending draft's session, once per request on full signed-in dashboard HTML GET navigations, and in the MCP create and organization-list tools. Other checkout returns use the existing organization's billing return flow. Navigation and MCP completion share one five-second linked cancellation deadline across the whole attempt; the Stripe return retains the normal client timeout. Account context reads, POSTs, fragments, assets and API requests never trigger it. Optional-panel error handling logs a completion failure and leaves the page usable with the pending row intact. The organization page adds one status notice and a retry link without replacing its content.

An unpaid complete session waits while its subscription is incomplete. An expired session or an unpaid subscription that becomes incomplete_expired or canceled removes the pending row and reports once. The wizard offers cancellation of the caller's pending draft instead of starting another checkout, and restores the draft's name, plan and interval after cancellation.

| Refusal | Stripe cleanup before removing the draft | One-time notice |
| --- | --- | --- |
| Mismatched session or plan, missing payment method, subscription outside active/trialing, or organization_deleted after payment | Cancel the subscription immediately without proration; refund every paid invoice in full | Refusal reason and “Your payment was refunded.” |
| Expired or canceled unpaid draft | Expire an open session and cancel any remaining subscription | Checkout expired/canceled or payment did not go through |
| Missing checkout/subscription or permanent invalid-request 4xx | Remove stale state; cancel/refund any retrievable paid draft | Checkout unavailable, plus refund confirmation when applicable |
| Stripe 5xx, 429, network failure, timeout, cancellation/refund failure | Keep the pending row; the next permitted request rechecks Stripe | Retry notice |

Cancellation rereads Stripe status, treating an absent subscription as already canceled. Refunds use the invoice-payment ID, successful refunded total and prior failed-refund ID as a stable idempotency key, and inspect existing refunds before submitting the remaining amount. Pending or unsuccessful refunds retain the row and never report a completed refund.

A keyed ts-auth create refuses deleted or closing organization IDs with 409. Finished organization deletion records remain refusal markers for at least 24 hours after history completion, and are then eligible for normal cleanup after the existing credential fence and dashboard verification. This requires no auth schema change.

Account deletion checks a continuation's receipt before touching Stripe. A missing checkout or subscription counts as already expired; transient Stripe failures lead to the existing retryable deletion dialog with the durable operation retained. Paid drafts are canceled and refunded before their pending row is removed.

Successful completion uses the session ID as the caller-scoped ts-auth creation key, inserts or adopts dashboard state, binds Stripe metadata and indexes, reconciles billing, and removes the pending row last. Repeating it yields one organization, one owner membership and one creation audit.
