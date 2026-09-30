namespace TerraScale.DotNet.Native;

/// <summary>Immutable producer contract identities packaged with the native client.</summary>
public static class NativeContract
{
    /// <summary>SHA256 of the packaged native client OpenAPI bytes.</summary>
    public const string OpenApiSha256 = "dc9936a2825b10e5da9eb7801964751bcd211bc8e7a197ca41736af73af7fd98";

    /// <summary>SHA256 of the packaged atomic effect vectors.</summary>
    public const string AtomicVectorsSha256 = "4ce4286b6e2eeae4ee15dec83f788f33cffdba7606a49021ec81afe0a4a9e816";

    /// <summary>SHA256 of the packaged Atlas native discovery wire identity.</summary>
    public const string AtlasWireIdentitySha256 = "b789d6fb7782e34ece3b516139856f2b6d2dea0543436820eefa50ff31355538";

    /// <summary>SHA256 of the packaged authenticated Atlas success fixture.</summary>
    public const string AtlasDiscoverySuccessSha256 = "d32d035bed11db296f3f911cc01f43cd6a3fafe208e005fe6abb35bc183759a0";

    /// <summary>SHA256 of the packaged authenticated Atlas forbidden fixture.</summary>
    public const string AtlasDiscoveryForbiddenSha256 = "3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c";
}
