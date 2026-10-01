# CLI device authorization

`ts auth login` (also available as `terrascale auth login`) displays a code and polls until the user authorizes the
CLI at `/cli/approve`. The CLI starts no local server and registers no redirect
or callback URI. Approval may happen in a browser on another device.

1. The CLI posts `client_id=terrascale-cli` as form data to
   `/api/v1/dashboard/cli/device-authorization`.
2. The response contains an opaque `device_code`, a displayed `user_code`,
   `verification_uri`, `verification_uri_complete`, `expires_in=600`, and
   `interval=5`. Opening a link only pre-fills the code; it never approves it.
3. The user signs in to the dashboard, checks the code and account, and explicitly
   approves or denies. Approval requires their current password and CSRF token.
4. The CLI polls `/api/v1/dashboard/cli/token` with `client_id`, `device_code`,
   and `grant_type=urn:ietf:params:oauth:grant-type:device_code`.
5. `authorization_pending` continues polling. `slow_down` increases the interval
   by five seconds. `access_denied` and `expired_token` stop the login. Success
   returns a `Bearer` personal access token valid for 30 days, and the CLI saves
   it privately for immediate dashboard-command use.

Grants can be redeemed once. Logout, session expiry, or credential changes between
approval and redemption prevent issuance. The token has the user's existing
dashboard permissions and can be revoked from their profile's Access tokens tab.
Browser credentials never authenticate the public polling endpoints, and personal
tokens cannot authorize another device. The browser approval POST remains behind
CSRF protection. Codes are retained as SHA-256 digests; issuance and code lookup
are throttled, and at most 1,000 unexpired grants are held.

This implementation follows the dashboard's required in-memory demo mode. Pending
grants, accounts, sessions, and personal tokens belong to the current demo process
and are lost when it restarts. It does not claim persistent production state or
issue regional database credentials; native API login remains a separate flow.
