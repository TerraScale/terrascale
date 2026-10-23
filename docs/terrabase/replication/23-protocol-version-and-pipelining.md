# 23. Peer stream protocol version (RT2 lane I)

Base: the `Push` stream of [8. Transport](08-transport.md), as built in `PeerStreams` (sender) and `PeerStreamReceiver`.

## Version negotiation

- `Hello.protocol` (field 5) and `Welcome.protocol` (field 2) carry the newest protocol each side speaks
  (`ReplicationProtocol.Version`). A build from before the fields sends neither, which reads as 0 and means protocol 1.
- A stream uses the lower of the two (`ReplicationProtocol.Negotiate`). The sender keeps it on the link and sends only
  what that protocol has.
- A receiver ignores a `PushFrame` whose kind it does not know (`KindCase.None`) and keeps the stream. A sender already
  ignored an `AckFrame` of an unknown kind. So a newer frame kind never ends an older peer's stream.

| Version | Adds |
| --- | --- |
| 1 | The stream as before the fields: one batch per database in flight. |

## Rolling deploy

Additive proto fields only; no wire or storage format change. An old receiver ignores `Hello.protocol` and welcomes
without a version, so a new sender uses protocol 1 with it. An old sender sends no version and gets protocol 1 behaviour
from a new receiver. No format bump.
