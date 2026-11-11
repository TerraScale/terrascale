# Profile status notices

Saving profile details or the language preference redirects to the Profile tab
with an explicit success notice announced through the existing polite status
region. The notice distinguishes “Profile saved.” from “Language preference
saved.” Revoking a personal access token from the Access tokens tab redirects
back to that tab with “Access token revoked.”; token management (list, create,
revoke) lives only on the Access tokens tab. Unknown status values do not
become messages, and each notice appears only on its own tab.

Validation failures retain the existing error response and drafted profile
fields, without a success notice. No authentication, storage or API-body
contract changes are introduced.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
