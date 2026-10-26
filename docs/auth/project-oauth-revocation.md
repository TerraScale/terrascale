# Project OAuth grant revocation

New project OAuth applications may use their issuer's published `revocation_endpoint` (`POST /{project}/oauth/revoke`). Send the token and `client_id` as an OAuth form; confidential clients also authenticate with their secret. Revoking a valid refresh or access token ends that authorization grant: its refresh tokens and online userinfo access stop working. Independently validated access tokens remain valid until their expiry.

Previously, project registration omitted the revocation endpoint permission and a valid request returned `unauthorized_client`. The management request and SDK payloads are unchanged. Greenfield registration grants the permission without a migration of existing client records; replace test registrations when qualifying the fix. Other projects and other clients retain their own authorization boundaries.
