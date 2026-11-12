# Organization checkout

Business organization creation waits for a paid Stripe Checkout Session, an active or trialing subscription, the selected published plan and interval, the matching customer and owner, and a validated payment method. The wizard, direct dashboard creation endpoint and MCP create tool all start the same draft flow. Choosing a name or plan creates no organization, membership, audit entry or project.

Stripe creates the session before the pending row is inserted. A draft ULID identifies both the row and Stripe idempotency keys; a filtered unique user index permits one active draft alongside retained unsettled refunds. A concurrent insert adopts the winner and expires its unused session. Creates are insert-or-get; concurrency losses reread stored state.

## Point of no return

| Stored state | Completion and navigation | Wizard cancel, cancel return and refusal | Account deletion |
| --- | --- | --- | --- |
| Before mark: pending | Verify the session, owner, paid status, plan/interval, subscription and payment method; save the known-paid marker before further Stripe reads | Atomically claim canceling with the original refusal reason before touching money; expire, cancel and refund; remove only after cleanup succeeds | Claim the same cancellation state and finish cleanup |
| Marked: creating | Its own save stores verified plan ID, customer, subscription, active/trialing status and renewal. Resume keyed ts-auth creation using those facts, without reading Stripe | Refuse cancellation and resume completion. Never cancel, refund or remove this row as cleanup, including for permanent vendor answers | Finish completion first. While it fails, retain the durable deletion operation on the existing retry page and do not start ts-auth account deletion |
| Completed: pending row absent | One dashboard save inserts organization, billing binding, customer and checkout index and removes the marked row. Reread the winner on a concurrency loss | A stale wizard cancel reports the organization already created and cannot cancel checkout | Refresh the sole-organization scope, then use normal ts-auth account/organization deletion and subscription termination without a checkout refund |

The dashboard organization and billing rows share DashboardDbContext in ts-dashboard. OrganizationStore stages its aggregate alongside the billing rows in one SaveChangesAsync with no explicit transaction. No organization entitlement is committed before its binding and customer exist.

After the commit, metadata repair only adds our organization ID to the subscription and customer. Later navigations and MCP calls compare their metadata and repeat missing writes until they succeed. Vendor failures are logged through OptionalPanel and exported in terrascale_organization_checkout_failures_total, with creating and metadata phases. Metadata failure never blocks or undoes a completed organization.

Completion runs once per request on authenticated full dashboard HTML GET navigations, the matching Stripe return, and MCP create/get_context calls. Other checkout returns retain the existing organization's return flow. Navigation and MCP attempts have one five-second linked cancellation deadline; the Stripe return retains the normal client timeout. POSTs, fragments, assets and other APIs do not trigger navigation completion. Cancel explicitly resumes a marked draft.

## Refusal and refunds

Unpaid complete sessions wait while their subscription is incomplete. Expired sessions and unpaid incomplete_expired/canceled subscriptions enter cleanup. Cancellation saves the original refusal reason before any Stripe effect, so an interrupted cancel/refund later reports that same reason.

Only HTTP 404 or Stripe resource_missing is permanent. Every other Stripe failure, including invalid_request_error with 401/403, is transient and retains the row. Once the session is known paid, a missing session or subscription throws and keeps the row instead of treating it as already expired. Marked rows have no permanent-failure cleanup exit.

Refunds are limited to paid invoices of the draft's own subscription/customer. They inspect invoice payments and existing refunds and use the payment ID, successful refunded total and prior failed-refund ID as a stable idempotency key. Cleanup removes the row only after the full refund succeeds.

An unsettled refund moves the row to refund_pending, retaining its original reason and Stripe status. It releases the active-draft slot so the wizard can start over immediately. The organization page reports each changed refund status once; later permitted navigations continue reconciling the retained row. Account deletion stays pending while money cleanup remains unfinished.

The keyed ts-auth create still refuses deleted/closing organization IDs. Finished deletion markers remain for at least 24 hours after history completion, then follow the existing credential fence and dashboard verification.

## Payment methods and parked late-payment gap

Organization Checkout sets neither payment_method_types nor an automatic_payment_methods override. It uses Stripe Checkout's dynamic selection and the Stripe account's Dashboard payment-method defaults, subject to currency, customer and subscription-mode eligibility. Checkout always requests a payment method; it does not restrict the session to cards. The repository does not enumerate the live account's enabled methods. Delayed methods, such as eligible bank debits, are possible when enabled there.

A normal unpaid incomplete subscription remains pending. If a refusal cancels an unpaid subscription and removes its draft, a delayed payment can subsequently succeed. Draft customers have no committed customer index, so the current payment-event handler returns without completing an organization or scheduling that late refund. A retained refund row is reconciled on later navigation, but an already removed unpaid draft has no automatic late-payment refund path. PARKED: reconcile late success after unpaid refusal/cancellation, or separately review the account's enabled payment methods. This PR does not change payment methods.
