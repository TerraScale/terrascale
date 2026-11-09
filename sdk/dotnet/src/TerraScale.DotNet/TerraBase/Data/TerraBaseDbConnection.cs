using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An ADO.NET connection to one <see cref="TerraBaseSqlDatabase"/>, for Dapper and other ADO.NET callers.</summary>
public sealed class TerraBaseDbConnection(TerraBaseSqlDatabase database) : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    /// <summary>The database every command of this connection runs against.</summary>
    internal TerraBaseSqlDatabase SqlDatabase { get; } = database;

    /// <summary>Always empty; the connection is built from a <see cref="TerraBaseSqlDatabase"/>, not a connection string.</summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => "";

        set => throw new NotSupportedException("Build the connection from a TerraBaseSqlDatabase instead of a connection string.");
    }

    /// <summary>Always empty.</summary>
    public override string Database => "";

    /// <summary>Always empty.</summary>
    public override string DataSource => "";

    /// <summary>Not part of the SQL API.</summary>
    public override string ServerVersion => throw new NotSupportedException("The server version is not part of the SQL API.");

    /// <summary>The connection state.</summary>
    public override ConnectionState State => state;

    /// <summary>Unsupported: one connection addresses one database.</summary>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("Create another connection for another database.");

    /// <summary>Closes the connection. No work is pending; commands complete on their own.</summary>
    public override void Close() => state = ConnectionState.Closed;

    /// <summary>Marks the connection open. Opening performs no network work.</summary>
    public override void Open() => state = ConnectionState.Open;

    /// <summary>Marks the connection open. Opening performs no network work.</summary>
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    /// <summary>Transactions arrive in the next SDK release.</summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("transactions arrive in the next SDK release");

    /// <summary>Creates a command bound to this connection.</summary>
    protected override DbCommand CreateDbCommand() => new TerraBaseDbCommand(this);
}
