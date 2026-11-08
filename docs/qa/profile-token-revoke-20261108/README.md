# Profile → Access tokens owns token revocation (TS-1373)

Before/after screenshots at 1280 px on a synthetic demo host (the E2E
`DemoEnvironment` booted on loopback HTTP with one seeded live personal access
token, signed in as `user@example.com` through the test-only hosted sign-in
stand-in). `before-*` is `origin/main` (4ebdfc693); `after-*` is this change.

## What changed

| Evidence | Before | After |
| --- | --- | --- |
| Access tokens tab rows | Name, token, created, expiry — no action | Adds an Actions column: Revoke button + confirm dialog (same dialog markup and CSRF hidden input as the former Danger card) |
| Danger tab | "Revoke access tokens" card duplicating the token list | Card removed; the tab holds only Delete account |
| After revoking | Redirect to `/profile?tab=danger` (no feedback) | Redirect to `/profile?tab=access-tokens&status=token-revoked` with "Access token revoked." announced in the polite status region |

## Files

- `before-1280.png` — main: Access tokens tab with no Revoke action.
- `before-danger-1280.png` — main: the duplicate "Revoke access tokens" card
  under Danger.
- `access-tokens-1280.png` — after: Revoke in every row of the Access tokens
  table.
- `access-tokens-revoke-dialog-1280.png` — after: the confirm dialog open from
  that tab ("Revoke 'Claude on work laptop'? … cannot be undone").
- `after-revoke-1280.png` — after confirming: the browser lands on
  `/profile?tab=access-tokens&status=token-revoked` with the status notice and
  the token gone from the table.
- `after-revoke-tab-1280.png` — the same tab revisited: empty token list, no
  stale notice.
- `danger-tab-1280.png` — after: Danger tab shows only Delete account; the
  duplicate token list is gone.
