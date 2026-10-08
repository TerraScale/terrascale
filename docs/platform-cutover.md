# TS-599 platform cutover

PR #190 already serves the platform REST API, OpenAPI and MCP on their public
hosts. Their Cloudflare records and Fly certificates are documented in
[platform-api.md](platform-api.md#hosts). This does not complete TS-599.

Native password, email-code, sign-up and password-reset calls use ts-go's
`tsauth.Headless` client. The dashboard renders `/login`, `/login/code`,
`/onboarding` and `/reset-password` without discovering ts-auth first. An
issuer outage cannot send these pages to hosted sign-in. `/login/sso` remains
the explicit redirect flow. Signed ID tokens are still verified before a
dashboard session starts; ts-auth keeps the credentials.

The operator `import-org` command and its identity-creation helper are removed.
System administrators still see service databases through the sign-in inventory
mirror, which now requires an existing account. The local administrator grant
remains until ts-auth serves its platform administrator claim.

## Remaining cutover

1. Replace dashboard account/organization/project/member/key aggregates with
   live ts-auth SDK reads and writes. Keep only platform preferences, plans,
   billing, subscriptions and caps in the dashboard's TerraBase database.
   Remove identity provisioning, PATs and local device grants once their
   consumers use ts-auth. Organizations, projects, memberships,
   invitations and API keys are served by ts-auth and have ts-go clients.
2. Coordinate the data-plane switch with TS-589/593 and TS-602. TerraBase's
   current user authentication uses `sub` with its own membership directory;
   it does not authorize `org_id`, `project_id` and scopes from project
   tokens. Replacing the dashboard's IDs alone would leave its databases
   unreachable. PR #207 now provides interim TerraBase provisioning and
   project-based usage aggregation while this authorization cutover is pending.
3. Finish TS-598 contract v1.1 before retiring platform identity: `billing:*`,
   `usage:read`, `platform_admin` and project suspension are documented but
   not served. Removing the local administrator flag before the claim ships
   would remove access to the system database console (TS-611).
4. Read period usage through ts-go and enforce spending caps as `ts-platform`
   using ts-auth's project suspension API. The current SDK database usage
   method has lifetime totals only; the dashboard still has a direct HTTP
   period-usage adapter. Saved caps currently do not suspend projects.
5. Replace MCP PATs with ts-auth OAuth once ts-auth serves resource indicators
   and client registration. That is explicitly a follow-up in its platform
   contract. Propagate a credential usable by the auth, TerraBase and platform
   SDKs through tool calls; current PATs cannot call those APIs as a user.

The upstream status above was checked against ts-auth `31c6bea`, TerraBase
`263598b` and ts-go `3dfbe22` on 2026-10-07. ts-auth's `docs/parity.md` and
`docs/platform-contract.md` distinguish served operations from future
contracts. Do not drop identity collections or switch production credentials
until the corresponding SDK-backed integration and live cross-product E2E
flow pass.
