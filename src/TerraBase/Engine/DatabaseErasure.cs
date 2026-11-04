namespace TerraBase.Engine;

// Shared SYSTEM record kind: management records authority atomically; storage enforces it at publication.
internal static class DatabaseErasure
{
    internal const string Kind = "database_purges";
}
