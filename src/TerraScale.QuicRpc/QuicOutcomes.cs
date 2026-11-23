using System.Net;
using TerraScale.Contracts.Continuity;

namespace TerraScale.QuicRpc;

public sealed record QuicUnavailable(string Message, string Code = "peer_unreachable");

public sealed record QuicTimeout(string Message, string Code = "request_timeout");

public sealed record QuicSent;

public readonly union QuicCallOutcome<TRes>(TRes, QuicUnavailable, QuicTimeout, QuicRemoteError);

public readonly union QuicPushOutcome(QuicSent, QuicUnavailable, QuicTimeout);

public readonly union QuicHandlerOutcome<TRes>(TRes, QuicRemoteError);

public readonly union QuicHostStartOutcome(IPEndPoint, QuicUnavailable);

public readonly union ChangefeedSubscriptionDecodeOutcome(ChangefeedSubscription, QuicRemoteError);

public readonly union ChangefeedEntryDecodeOutcome(ChangefeedEntry, QuicRemoteError);
