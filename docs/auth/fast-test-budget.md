# Fast test budget

Every Fast test must complete in under ten seconds. The ten-second shipping gate includes each journey's setup and assertions, with normal parallelism.

Management tests seed real users and server-side sessions, then obtain tokens over HTTP authorization and PKCE redemption. The dedicated hosted sign-in, sign-up, MFA and OIDC journeys continue to prove authentication. Management operations still pass through the real HTTP authentication and authorization middleware and persist through the SDK to TerraBase.

The assembly fixture owns one TerraBase process and an Enterprise setup organization and credential. Each test owns a separate project and database. This removes repeated organization, plan and credential setup without sharing application state. Disposing the fixture removes its process and ephemeral data directory.

Fast retains account deletion, refusal of the deleted user's access token, removal of its identity and reuse of its email. The full sole-organization/shared-organization erasure journey and dashboard deletion handoff across restart run unchanged in Slow. Happy paths, role and tenant refusals, last-owner safety, API-key revocation, scoped-key restrictions and cross-replica token revocation remain covered. Validation matrices and repeated authentication setup do not belong in management journeys.

The three-replica, multi-generation refresh replay drill also runs unchanged in Slow. Fast retains concurrent authorization-code and refresh redemption across two replicas and rejection of the resulting revoked tokens. MFA enrollment/sign-in/disable and recovery-code replay have separate Fast tests; both use real hosted endpoints. Sign-out and email-change tests seed their initial sessions but prove the subsequent sign-in through the hosted email flow.
