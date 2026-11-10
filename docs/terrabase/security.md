# Listener metadata

A route with no listener metadata falls back to closed, and admin routes declare `ListenerMetadata.Internal` explicitly.

NATS error 10074 is not retried because the stream wants more replicas than the cluster has.
