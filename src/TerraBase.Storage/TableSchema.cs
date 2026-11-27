namespace TerraBase.Storage;

internal sealed record TableColumn(string Name, int Index, string? DeclaredType, bool NotNull, int PrimaryKeyOrdinal, bool Generated, bool IsDeltaCounter)
{
    public bool IsPrimaryKey => PrimaryKeyOrdinal > 0;
}

internal sealed class TableSchema
{
    private readonly Dictionary<string, TableColumn> _byName;
    private readonly int[] _dataOrdinals;

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
        _byName = new Dictionary<string, TableColumn>(columns.Count, StringComparer.OrdinalIgnoreCase);
        var hasGenerated = false;
        var maxIndex = storageColumnCount;
        foreach (var column in columns)
        {
            _byName[column.Name] = column;
            if (column.Generated)
            {
                hasGenerated = true;
            }

            if (column.Index >= maxIndex)
            {
                maxIndex = column.Index + 1;
            }
        }

        HasVirtualGeneratedColumns = hasGenerated;
        var keys = columns.Where(static column => column.IsPrimaryKey).OrderBy(static column => column.PrimaryKeyOrdinal).ToArray();
        UsesImplicitRowId = keys.Length == 0 && !withoutRowId;
        PrimaryKeyIndices = [.. keys.Select(static column => column.Index)];
        PrimaryKeyColumnNames = UsesImplicitRowId ? ["rowid"] : [.. keys.Select(static column => column.Name)];
        var dataColumns = columns.Where(static column => !column.IsPrimaryKey && !column.Generated).ToArray();
        DataColumns = dataColumns;
        var dataIndices = new int[dataColumns.Length];
        _dataOrdinals = new int[maxIndex];
        Array.Fill(_dataOrdinals, -1);
        for (var index = 0; index < dataColumns.Length; index++)
        {
            var columnIndex = dataColumns[index].Index;
            dataIndices[index] = columnIndex;
            if ((uint)columnIndex < (uint)_dataOrdinals.Length)
            {
                _dataOrdinals[columnIndex] = index;
            }
        }

        DataColumnIndices = dataIndices;
        InsertableColumns = [.. columns.Where(static column => !column.Generated)];
        IntegerPrimaryKey = !withoutRowId && keys.Length == 1 && string.Equals(keys[0].DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase)
            ? keys[0]
            : null;
        HasDeltaColumns = dataColumns.Any(static column => column.IsDeltaCounter);
        Sql = new TableSql(this);
    }

    public string Table { get; }

    public IReadOnlyList<TableColumn> Columns { get; }

    public IReadOnlyList<TableColumn> DataColumns { get; }

    public int[] DataColumnIndices { get; }

    public IReadOnlyList<TableColumn> InsertableColumns { get; }

    public IReadOnlyList<int> PrimaryKeyIndices { get; }

    public IReadOnlyList<string> PrimaryKeyColumnNames { get; }

    public TableColumn? IntegerPrimaryKey { get; }

    public string? PrimaryKeyIndex { get; }

    public bool UsesImplicitRowId { get; }

    public bool IsVirtual { get; }

    public bool IsShadow { get; }

    public bool HasVirtualGeneratedColumns { get; }

    public string? ContentOwner => IsShadow && Table.EndsWith("_content", StringComparison.OrdinalIgnoreCase) ? Table[..^"_content".Length] : null;

    public bool HasSecondaryUnique { get; }

    public bool HasDeltaColumns { get; }

    public int StorageColumnCount { get; }

    public TableSql Sql { get; }

    public TableColumn? FindColumn(string name) => _byName.GetValueOrDefault(name);

    public int DataOrdinal(TableColumn column)
    {
        var ordinal = (uint)column.Index < (uint)_dataOrdinals.Length ? _dataOrdinals[column.Index] : -1;
        return ordinal >= 0 && ReferenceEquals(DataColumns[ordinal], column) ? ordinal : -1;
    }
}
