# Organization checkout

Business organization creation waits for a paid Stripe Checkout Session and an active or trialing subscription with the selected published plan and a validated payment method. The wizard, direct dashboard creation endpoint and MCP create tool all start this draft flow. No organization, membership, audit entry or project is created while choosing a name or plan or while checkout is open.

Stripe creates the session before the dashboard inserts its pending row, with the draft ULID as its idempotency key. The row requires a unique session ID, a published paid plan and a billing interval. A concurrent insert adopts the winning row and expires its unused session. A Stripe idempotency conflict reads the existing session; an unresolved race returns a retryable 409.

Completion runs on the authenticated Stripe return, once per request on full signed-in dashboard HTML GET navigations, and in the MCP create and organization-list tools. Account context reads, POSTs, fragments, assets and API requests never trigger it. Optional-panel error handling logs a completion failure and leaves the page usable with the pending row intact. The organization page adds one status notice and a retry link without replacing its content.

An expired session removes its pending row. Only the wizard cancel return with that draft's ID restores its name, plan and interval. A mismatched Stripe session or subscription, an inactive subscription or missing payment method removes the draft and reports a refusal once. A keyed ts-auth create refuses deleted or closing organization IDs with 409; completed organization deletion records remain as tombstones. Account deletion expires an open draft session, cancels a subscription if payment raced expiration, and removes the pending row before requesting identity deletion.

Successful completion uses the session ID as the caller-scoped ts-auth creation key, inserts or adopts dashboard state, binds Stripe metadata and indexes, reconciles billing, and removes the pending row last. Repeating it yields one organization, one owner membership and one creation audit.
