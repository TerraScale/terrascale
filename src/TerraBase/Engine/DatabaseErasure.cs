namespace TerraBase.Engine;

// Shared SYSTEM collection: management records authority atomically; storage enforces it at publication.
internal static class DatabaseErasure
{
    internal const string Collection = "database_purges";
}
