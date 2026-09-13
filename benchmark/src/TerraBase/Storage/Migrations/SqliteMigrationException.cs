namespace TerraBase.Storage.Migrations;

/// <summary>Reports a rejected or inconsistent durable collection migration.</summary>
public sealed class SqliteMigrationException : InvalidOperationException
{
    /// <summary>Creates a migration failure with a human-readable message.</summary>
    /// <param name="message">The failure description.</param>
    public SqliteMigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a migration failure with its underlying cause.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying database failure.</param>
    public SqliteMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
