# Account deletion with databases

Account deletion accepts organizations that still contain databases. After email
confirmation and the shared-organization ownership check, the dashboard cancels
paid plans of sole-member organizations immediately, without a refund, and hands
the account deletion to ts-auth using the existing durable deletion receipt.

The consent covers every sole-member organization, including Personal, and
automatic erasure of its projects, databases and data from every region.
Shared organizations retain their data and plans; a sole owner must transfer
ownership before leaving. Retained backups may outlive account deletion until
their retention period expires; completion does not promise immediate backup purge.

The dashboard does not orchestrate storage erasure or infer completion from an
empty database list. ts-auth owns project erasure through TerraBase. Dashboard
cleanup still requires explicit account completion and each organization's own
deletion proof, as described in [Account deletion completion](account-deletion-completion.md).
No API, storage format, dependency or configuration changes are required here.
Deploy the corresponding ts-auth and TerraBase erasure support before this change.

A push stream that returns NATS error 10074 is not retried, because the stream
wants more replicas than the cluster has.
