namespace TerraScale.DotNet.Native;

/// <summary>Immutable producer contract identities packaged with the native client.</summary>
public static class NativeContract
{
    /// <summary>SHA256 of the packaged native client OpenAPI bytes.</summary>
    public const string OpenApiSha256 = "563a66449d8c2eeb4306b9bb9d994cabb86c14bd4c469f785dabe5afb9f6ef3d";

    /// <summary>SHA256 of the packaged atomic effect vectors.</summary>
    public const string AtomicVectorsSha256 = "4ce4286b6e2eeae4ee15dec83f788f33cffdba7606a49021ec81afe0a4a9e816";

    /// <summary>SHA256 of the packaged Atlas native discovery wire identity.</summary>
    public const string AtlasWireIdentitySha256 = "b789d6fb7782e34ece3b516139856f2b6d2dea0543436820eefa50ff31355538";

    /// <summary>SHA256 of the packaged derived production-origin Atlas success codec fixture.</summary>
    public const string AtlasDiscoverySuccessSha256 = "82fbc6518a5e8e825879243c45a86e6e66f44323a6dcd3ad8280c8109aa671ec";

    /// <summary>SHA256 of the packaged authenticated Atlas forbidden fixture.</summary>
    public const string AtlasDiscoveryForbiddenSha256 = "3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c";
}
