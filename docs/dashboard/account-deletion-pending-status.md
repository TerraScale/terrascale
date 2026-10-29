# Account deletion pending status

The account progress page reads the existing opaque ts-auth receipt status. A `202`
means account closure is pending; confirmation from required storage regions is one
necessary condition. The boolean response cannot identify a particular step or
region. The page names no region and makes no physical-erasure claim.

The feature-owned status form posts to the dashboard-internal
`/api/v1/dashboard/profile/delete/status`. It checks receipt status only and uses
existing dashboard cleanup after explicit `204` completion. It never calls account
DELETE, billing or re-admission, including after receipt `401`/`403`/`404`/`409`. Initiation,
billing ordering and the legacy continuation remain unchanged. Fresh-proof/scope
admission remains owned by P06/TS-1106 and the separate preparation-contract work.

A ten-second budget covers journal loading, status reads and confirmed cleanup.
The page offers five automatic checks with backoff capped at 30 seconds, then pauses
for a visible manual check. Permanent receipt/authorization errors retain the journal
and stop automatic checks; temporary failures remain recoverable. All checks require
the existing opaque operation cookie, POSTs retain CSRF checks, and pages use no-store.
No typed auth/SDK status fields or public service API contracts changed.
