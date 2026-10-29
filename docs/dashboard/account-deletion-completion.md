# Account deletion completion

A confirmed account deletion persists its authorized cleanup scope and a random
ts-auth deletion receipt in `account_deletions`, encrypted with the dashboard
session key. The HttpOnly deletion cookie continues only that operation, including
after ts-auth revokes the account or the dashboard session cookie disappears.
The receipt and retained credential never enter browser HTML or URLs.

The dashboard redirects to `/account-deletion`, which automatically checks the
receipt using CSRF-protected POSTs. Only explicit completion permits account,
identity, token and session cleanup. Each originally sole organization's billing,
Stripe customer mapping and projection additionally require its own deletion
proof; surviving shared organizations retain their records. Authentication errors,
unknown receipts, conflicts and transport failures never certify deletion.
An explicitly refused operation can be requested again after resolving its cause.
Paid subscription cancellation retains the existing immediate cancellation policy.

Completion ends on the dashboard-hosted `/account-deleted` page without starting
a new sign-in flow. Pending project and organization forms continue automatically
with their original organization scope. Project deletion markers persist outside
the hydrated project list until dashboard finalization. No storage engine format
bump or production reset is required; the new collection is seeded at startup.
