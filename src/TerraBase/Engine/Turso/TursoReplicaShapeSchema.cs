namespace TerraBase.Engine.Turso;

internal sealed class TursoReplicaShapeSchema
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Generation, long HomeCookie, long LocalCookie)> _tables = new(StringComparer.Ordinal);

    internal bool Contains(string table, string generation, long homeCookie, long localCookie)
    {
        lock (_gate)
        {
            return _tables.TryGetValue(table, out var stored) && stored == (generation, homeCookie, localCookie);
        }
    }

    internal void Remember(string table, string generation, long homeCookie, long localCookie)
    {
        lock (_gate)
        {
            if (_tables.Count == 128 && !_tables.ContainsKey(table))
            {
                _tables.Clear();
            }

            _tables[table] = (generation, homeCookie, localCookie);
        }
    }
}
