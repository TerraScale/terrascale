using MessagePack;
using MessagePack.Resolvers;

namespace TerraScale.TerraBase.Contracts
{
    /// <summary>
    /// The dashboard-to-TerraBase MagicOnion transport. TerraBase serves these services only on its internal listener
    /// (the node's NetBird address, h2c), where every call carries the shared peer credential. A call that also carries a
    /// ts-auth bearer token acts with that token's scopes, exactly as on the public client listener; a call without one acts
    /// as the platform operator.
    /// </summary>
    public static class TerraBaseRpc
    {
        /// <summary>The shared peer credential (TERRABASE_PEER_SECRET), the same header the internal HTTP routes require.</summary>
        public const string PeerCredentialHeader = "x-terrabase-peer-credential";

        /// <summary>The caller's ts-auth access token, <c>Bearer &lt;token&gt;</c>.</summary>
        public const string AuthorizationHeader = "authorization";

        /// <summary>The binary trailer that carries a <see cref="TerraBaseFault"/>.</summary>
        public const string FaultTrailer = "terrabase-fault-bin";

        /// <summary>The largest message either side accepts: an 8 MiB document page plus envelope.</summary>
        public const int MaxMessageBytes = 16 * 1024 * 1024;

        /// <summary>The MessagePack options both sides use; JSON values travel as their UTF-8 text.</summary>
        public static MessagePackSerializerOptions SerializerOptions { get; } = MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create([JsonElementFormatter.Instance, NullableJsonElementFormatter.Instance], [StandardResolver.Instance]))
            .WithSecurity(MessagePackSecurity.UntrustedData);
    }
}
