using Microsoft.Data.Sqlite;

namespace TerraBase.Storage.Indexes;

/// <summary>The durable lifecycle state of an index descriptor.</summary>
public enum SqliteIndexDescriptorState
{
    /// <summary>The descriptor has been proposed but no index build owns it.</summary>
    Candidate,

    /// <summary>An index build is backfilling the descriptor.</summary>
    Building,

    /// <summary>The index is complete and may be used by the planner.</summary>
    Active,

    /// <summary>The index remains available but is hidden from the planner.</summary>
    Invisible,

    /// <summary>The descriptor is retained for history and cannot be used.</summary>
    Retired,

    /// <summary>The descriptor is blocked until its build problem is resolved.</summary>
    Blocked,
}

/// <summary>The value type extracted by an index descriptor.</summary>
public enum SqliteIndexValueType
{
    /// <summary>A JSON boolean value.</summary>
    Boolean,

    /// <summary>A JSON integer value.</summary>
    WholeNumber,

    /// <summary>A JSON floating-point value.</summary>
    Number,

    /// <summary>A JSON string value.</summary>
    Text,

    /// <summary>An opaque byte value.</summary>
    Bytes,
}

/// <summary>A durable descriptor for one logical collection index.</summary>
public sealed record SqliteIndexDescriptor(
    string DescriptorId,
    string Tenant,
    string Collection,
    string Path,
    SqliteIndexValueType ValueType,
    SqliteIndexDescriptorState State,
    bool PlannerVisible,
    long BackfillWatermark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Creates a new descriptor in the candidate state.</summary>
    public static SqliteIndexDescriptor Candidate(
        string descriptorId,
        string tenant,
        string collection,
        string path,
        SqliteIndexValueType valueType)
    {
        ValidateIdentity(descriptorId, tenant, collection, path);
        var now = DateTimeOffset.UtcNow;
        return new(
            descriptorId,
            tenant,
            collection,
            path,
            valueType,
            SqliteIndexDescriptorState.Candidate,
            false,
            0,
            now,
            now);
    }

    private static void ValidateIdentity(string descriptorId, string tenant, string collection, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
    }
}

/// <summary>Signals a rejected durable index descriptor operation.</summary>
public sealed class SqliteIndexCatalogException : InvalidOperationException
{
    /// <summary>Creates a catalog operation exception.</summary>
    public SqliteIndexCatalogException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a catalog operation exception with its SQLite cause.</summary>
    public SqliteIndexCatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads and advances index descriptor state atomically in SQLite.</summary>
public sealed class SqliteIndexCatalog
{
    /// <summary>Creates a catalog over an existing, migrated SQLite connection.</summary>
    public SqliteIndexCatalog(SqliteConnection connection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
    }

    /// <summary>The SQLite connection used by this catalog.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Persists a new candidate descriptor.</summary>
    public async Task CreateAsync(
        SqliteIndexDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateNewDescriptor(descriptor);

        await using var command = Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO index_descriptors
                (descriptor_id, tenant, collection, path, value_type, state,
                 planner_visible, backfill_watermark, created_at, updated_at)
            VALUES
                ($descriptorId, $tenant, $collection, $path, $valueType, $state,
                 0, 0, $createdAt, $updatedAt);
            """;
        AddDescriptorIdentity(command, descriptor);
        command.Parameters.AddWithValue("$valueType", ValueTypeText(descriptor.ValueType));
        command.Parameters.AddWithValue("$state", StateText(descriptor.State));
        command.Parameters.AddWithValue("$createdAt", descriptor.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", descriptor.UpdatedAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException failure) when (failure.SqliteErrorCode == 19)
        {
            throw new SqliteIndexCatalogException(
                "The index descriptor identity or collection path is already registered.",
                failure);
        }
    }

    /// <summary>Reads one descriptor, or null when it does not exist.</summary>
    public async Task<SqliteIndexDescriptor?> FindAsync(
        string descriptorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorId);
        await using var command = Connection.CreateCommand();
        command.CommandText = DescriptorSelect + " WHERE descriptor_id = $descriptorId;";
        command.Parameters.AddWithValue("$descriptorId", descriptorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDescriptor(reader) : null;
    }

    /// <summary>Reads all descriptors for one collection in stable path order.</summary>
    public async Task<IReadOnlyList<SqliteIndexDescriptor>> ListAsync(
        string tenant,
        string collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        await using var command = Connection.CreateCommand();
        command.CommandText = DescriptorSelect + " WHERE tenant = $tenant AND collection = $collection ORDER BY path COLLATE BINARY, value_type COLLATE BINARY, descriptor_id COLLATE BINARY;";
        command.Parameters.AddWithValue("$tenant", tenant);
        command.Parameters.AddWithValue("$collection", collection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var descriptors = new List<SqliteIndexDescriptor>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            descriptors.Add(ReadDescriptor(reader));
        }

        return descriptors;
    }

    /// <summary>Claims a candidate descriptor for a backfill.</summary>
    public Task StartBuildAsync(
        string descriptorId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            descriptorId,
            SqliteIndexDescriptorState.Candidate,
            SqliteIndexDescriptorState.Building,
            plannerVisible: false,
            backfillWatermark: 0,
            cancellationToken);

    /// <summary>Records monotonic progress for an active backfill.</summary>
    public async Task AdvanceBuildAsync(
        string descriptorId,
        long backfillWatermark,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorId);
        ArgumentOutOfRangeException.ThrowIfNegative(backfillWatermark);
        await using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE index_descriptors
            SET backfill_watermark = $backfillWatermark, updated_at = $updatedAt
            WHERE descriptor_id = $descriptorId
              AND state = 'building'
              AND backfill_watermark <= $backfillWatermark;
            """;
        command.Parameters.AddWithValue("$descriptorId", descriptorId);
        command.Parameters.AddWithValue("$backfillWatermark", backfillWatermark);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new SqliteIndexCatalogException(
                "Index build progress was stale, regressive, or the descriptor is not building.");
        }
    }

    /// <summary>Publishes a completed build to the query planner.</summary>
    public Task ActivateAsync(
        string descriptorId,
        long backfillWatermark,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(backfillWatermark);
        return TransitionAsync(
            descriptorId,
            SqliteIndexDescriptorState.Building,
            SqliteIndexDescriptorState.Active,
            plannerVisible: true,
            backfillWatermark,
            cancellationToken);
    }

    /// <summary>Hides an active descriptor while retaining its build state.</summary>
    public Task HideAsync(
        string descriptorId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            descriptorId,
            SqliteIndexDescriptorState.Active,
            SqliteIndexDescriptorState.Invisible,
            plannerVisible: false,
            backfillWatermark: null,
            cancellationToken);

    /// <summary>Retires an invisible descriptor permanently.</summary>
    public Task RetireAsync(
        string descriptorId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            descriptorId,
            SqliteIndexDescriptorState.Invisible,
            SqliteIndexDescriptorState.Retired,
            plannerVisible: false,
            backfillWatermark: null,
            cancellationToken);

    private async Task TransitionAsync(
        string descriptorId,
        SqliteIndexDescriptorState expected,
        SqliteIndexDescriptorState next,
        bool plannerVisible,
        long? backfillWatermark,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorId);
        await using var command = Connection.CreateCommand();
        command.CommandText = backfillWatermark is null
            ? """
              UPDATE index_descriptors
              SET state = $next, planner_visible = $plannerVisible, updated_at = $updatedAt
              WHERE descriptor_id = $descriptorId AND state = $expected;
              """
            : """
              UPDATE index_descriptors
              SET state = $next, planner_visible = $plannerVisible,
                  backfill_watermark = $backfillWatermark, updated_at = $updatedAt
              WHERE descriptor_id = $descriptorId AND state = $expected
                AND backfill_watermark <= $backfillWatermark;
              """;
        command.Parameters.AddWithValue("$descriptorId", descriptorId);
        command.Parameters.AddWithValue("$expected", StateText(expected));
        command.Parameters.AddWithValue("$next", StateText(next));
        command.Parameters.AddWithValue("$plannerVisible", plannerVisible ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        if (backfillWatermark is not null)
        {
            command.Parameters.AddWithValue("$backfillWatermark", backfillWatermark.Value);
        }

        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new SqliteIndexCatalogException(
                $"Index descriptor transition {StateText(expected)} -> {StateText(next)} was not accepted.");
        }
    }

    private static void ValidateNewDescriptor(SqliteIndexDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DescriptorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Path);
        if (descriptor.State != SqliteIndexDescriptorState.Candidate
            || descriptor.PlannerVisible
            || descriptor.BackfillWatermark != 0)
        {
            throw new ArgumentException("New index descriptors must start as invisible candidates at watermark zero.", nameof(descriptor));
        }
    }

    private static SqliteIndexDescriptor ReadDescriptor(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseValueType(reader.GetString(4)),
            ParseState(reader.GetString(5)),
            reader.GetInt64(6) != 0,
            reader.GetInt64(7),
            ParseTimestamp(reader.GetString(8)),
            ParseTimestamp(reader.GetString(9)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static readonly string DescriptorSelect = """
        SELECT descriptor_id, tenant, collection, path, value_type, state,
               planner_visible, backfill_watermark, created_at, updated_at
        FROM index_descriptors
        """;

    private static void AddDescriptorIdentity(SqliteCommand command, SqliteIndexDescriptor descriptor)
    {
        command.Parameters.AddWithValue("$descriptorId", descriptor.DescriptorId);
        command.Parameters.AddWithValue("$tenant", descriptor.Tenant);
        command.Parameters.AddWithValue("$collection", descriptor.Collection);
        command.Parameters.AddWithValue("$path", descriptor.Path);
    }

    private static string StateText(SqliteIndexDescriptorState state) =>
        state switch
        {
            SqliteIndexDescriptorState.Candidate => "candidate",
            SqliteIndexDescriptorState.Building => "building",
            SqliteIndexDescriptorState.Active => "active",
            SqliteIndexDescriptorState.Invisible => "invisible",
            SqliteIndexDescriptorState.Retired => "retired",
            SqliteIndexDescriptorState.Blocked => "blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static SqliteIndexDescriptorState ParseState(string state) =>
        state switch
        {
            "candidate" => SqliteIndexDescriptorState.Candidate,
            "building" => SqliteIndexDescriptorState.Building,
            "active" => SqliteIndexDescriptorState.Active,
            "invisible" => SqliteIndexDescriptorState.Invisible,
            "retired" => SqliteIndexDescriptorState.Retired,
            "blocked" => SqliteIndexDescriptorState.Blocked,
            _ => throw new SqliteIndexCatalogException($"Unknown durable index descriptor state '{state}'."),
        };

    private static string ValueTypeText(SqliteIndexValueType valueType) =>
        valueType switch
        {
            SqliteIndexValueType.Boolean => "boolean",
            SqliteIndexValueType.WholeNumber => "integer",
            SqliteIndexValueType.Number => "number",
            SqliteIndexValueType.Text => "text",
            SqliteIndexValueType.Bytes => "bytes",
            _ => throw new ArgumentOutOfRangeException(nameof(valueType)),
        };

    private static SqliteIndexValueType ParseValueType(string valueType) =>
        valueType switch
        {
            "boolean" => SqliteIndexValueType.Boolean,
            "integer" => SqliteIndexValueType.WholeNumber,
            "number" => SqliteIndexValueType.Number,
            "text" => SqliteIndexValueType.Text,
            "bytes" => SqliteIndexValueType.Bytes,
            _ => throw new SqliteIndexCatalogException($"Unknown durable index value type '{valueType}'."),
        };
}
