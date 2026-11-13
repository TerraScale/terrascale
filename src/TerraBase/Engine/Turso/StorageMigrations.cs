using System.Globalization;
using TerraBase.Configuration;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal static class StorageMigrations
{
    internal const string PreviousId = "23-41a88901325a";

    internal sealed record Sql(int From, int To, string Released, string Statement);

    private static readonly Sql[] Steps = [new(23, 24, "2026-11-12", "SELECT 1;")];

    internal static void RequirePath(int version)
    {
        if (version > StorageFormat.Version)
        {
            throw new TerraBaseConfigurationException(
                $"storage format {version}: this build is older than the data; roll forward or restore the pre-upgrade backup");
        }

        for (var from = version; from < StorageFormat.Version; from++)
        {
            if (!Steps.Any(step => step.From == from && step.To == from + 1))
            {
                throw new TerraBaseConfigurationException($"storage format {version} has no migration path to {StorageFormat.Version}; restore a supported backup");
            }
        }
    }

    internal static int FileVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void Apply(string name, SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = FileVersion(connection);
        var from = version == 1 ? 23 : version;
        RequirePath(from);
        if (from == StorageFormat.Version)
        {
            return;
        }

        using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "PRAGMA synchronous = FULL;";
            settings.ExecuteNonQuery();
        }

        foreach (var step in Steps.Where(step => step.From >= from && step.To <= StorageFormat.Version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = step.Statement + $" PRAGMA user_version = {step.To};";
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }

        if (FileVersion(connection) != StorageFormat.Version)
        {
            throw new InvalidDataException($"database '{name}' did not reach storage format {StorageFormat.Version}");
        }
    }
}
