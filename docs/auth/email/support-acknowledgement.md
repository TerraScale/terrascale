# Support acceptance and acknowledgement

`POST /v1/{slug}/email/support` retains dashboard ClientBasic authentication,
platform user lookup, current organization/project access checks and the
per-user rate limit. Repeated requests pass the same authorization checks before
reading the ledger; a request ID belonging to another user returns 404.

The existing `email_support_deliveries` collection tracks two independent
outcomes in the same document. `status` describes the message to support.
After provider acceptance, a conditional write persists `status: accepted`
and reserves the acknowledgement with `acknowledgementStatus: sending` before
calling its sender. A failed or cancelled acknowledgement cannot replace the
saved support acceptance. The acknowledgement's returned outcome is then saved
separately. An unexpected sender exception is recorded as `unknown` unless the
request cancellation token is cancelled.

The HTTP response keeps its existing `status` and adds the optional
`acknowledgementStatus` field, omitted when no acknowledgement was reserved.
Both fields use `accepted`, `retryable`, `rejected` or `unknown`. Internal
`sending` reservations are reported as `unknown`. For example, an accepted
support message with a refused acknowledgement returns
`{"status":"accepted","acknowledgementStatus":"rejected"}`. Neither accepted
outcome proves inbox delivery.

Repeats return the saved outcomes and send neither message again, including an
acknowledgement recorded as retryable. Only a support outcome of `retryable`
(the provider confirms no send occurred) deletes the reservation and permits a
later request with that ID to attempt support delivery again. Rejected and
ambiguous support outcomes retain their reservation. There is no automatic
provider retry, recovery worker, storage migration or new collection.

A process interruption or storage failure after a provider call can still lose
its final outcome. If the support acceptance write did not complete, repeats
report support `unknown` and do not resend. If it completed but acknowledgement
completion was not saved, repeats report support `accepted` and acknowledgement
`unknown`, without resending. A reservation may therefore suppress an
acknowledgement that was never actually sent. Storage write failures are surfaced
as request failures; the endpoint does not claim a final saved outcome when its
conditional write fails.

Fast HTTP integration coverage uses real sign-in and isolated TerraBase
databases with a controlled sender. It gates acknowledgement completion to
check the persisted support acceptance and a concurrent repeat, then checks
independent acknowledgement outcomes, exceptions and support retry behavior.
No live provider sends are required.
