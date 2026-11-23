namespace TerraBase.Storage;

internal sealed record TableColumn(string Name, int Index, string? DeclaredType, bool NotNull, int PrimaryKeyOrdinal, bool Generated, bool IsDeltaCounter)
{
    public bool IsPrimaryKey => PrimaryKeyOrdinal > 0;
}

internal sealed class TableSchema
{
    private readonly Dictionary<string, TableColumn> _byName;

    public TableSchema(
        string table,
        IReadOnlyList<TableColumn> columns,
        int storageColumnCount,
        bool withoutRowId,
        bool isVirtual,
        bool isShadow,
        bool hasSecondaryUnique,
        string? primaryKeyIndex)
    {
        Table = table;
        Columns = columns;
        StorageColumnCount = storageColumnCount;
        IsVirtual = isVirtual;
        IsShadow = isShadow;
        HasSecondaryUnique = hasSecondaryUnique;
        PrimaryKeyIndex = primaryKeyIndex;
        _byName = columns.ToDictionary(static column => column.Name, StringComparer.OrdinalIgnoreCase);
        var keys = columns.Where(static column => column.IsPrimaryKey).OrderBy(static column => column.PrimaryKeyOrdinal).ToArray();
        UsesImplicitRowId = keys.Length == 0 && !withoutRowId;
        PrimaryKeyIndices = [.. keys.Select(static column => column.Index)];
        PrimaryKeyColumnNames = UsesImplicitRowId ? ["rowid"] : [.. keys.Select(static column => column.Name)];
        DataColumns = [.. columns.Where(static column => !column.IsPrimaryKey && !column.Generated)];
        InsertableColumns = [.. columns.Where(static column => !column.Generated)];
        IntegerPrimaryKey = !withoutRowId && keys.Length == 1 && string.Equals(keys[0].DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase)
            ? keys[0]
            : null;
        HasDeltaColumns = DataColumns.Any(static column => column.IsDeltaCounter);
    }

    public string Table { get; }

    public IReadOnlyList<TableColumn> Columns { get; }

    public IReadOnlyList<TableColumn> DataColumns { get; }

    public IReadOnlyList<TableColumn> InsertableColumns { get; }

    public IReadOnlyList<int> PrimaryKeyIndices { get; }

    public IReadOnlyList<string> PrimaryKeyColumnNames { get; }

    public TableColumn? IntegerPrimaryKey { get; }

    public string? PrimaryKeyIndex { get; }

    public bool UsesImplicitRowId { get; }

    public bool IsVirtual { get; }

    public bool IsShadow { get; }

    public string? ContentOwner => IsShadow && Table.EndsWith("_content", StringComparison.OrdinalIgnoreCase) ? Table[..^"_content".Length] : null;

    public bool HasSecondaryUnique { get; }

    public bool HasDeltaColumns { get; }

    public int StorageColumnCount { get; }

    public TableColumn? FindColumn(string name) => _byName.GetValueOrDefault(name);

    public int DataOrdinal(TableColumn column)
    {
        for (var index = 0; index < DataColumns.Count; index++)
        {
            if (ReferenceEquals(DataColumns[index], column))
            {
                return index;
            }
        }

        return -1;
    }
}
