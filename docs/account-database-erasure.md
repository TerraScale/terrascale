# Account deletion with databases

Account deletion accepts organizations that still contain databases. After email
confirmation and the shared-organization ownership check, the dashboard cancels
paid plans of sole-member organizations immediately, without a refund, and hands
the account deletion to ts-auth using the existing durable deletion receipt.

The consent covers every sole-member organization, including Personal, and
automatic erasure of its projects, databases and data from all replicas.
Shared organizations retain their data and plans; a sole owner must transfer
ownership before leaving. Retained backups may outlive account deletion until
their retention period expires; completion does not promise immediate backup purge.

The dashboard does not orchestrate storage erasure or infer completion from an
empty database list. ts-auth owns project erasure through TerraBase. Dashboard
cleanup still requires explicit account completion and each organization's own
deletion proof, as described in [Account deletion completion](account-deletion-completion.md).
No API, storage format, dependency or configuration changes are required here.
Deploy the corresponding ts-auth and TerraBase erasure support before this change.

`AccountDeletionJourneyTests` creates a database, collection and document through
HTTP before confirming deletion, then checks ts-auth's sole-organization removal,
retained shared organizations and dashboard account/token/session cleanup. Its
test-only providers verify delegation, not physical erasure across live replicas
or backup expiry; those require ts-auth/TerraBase integration evidence.
