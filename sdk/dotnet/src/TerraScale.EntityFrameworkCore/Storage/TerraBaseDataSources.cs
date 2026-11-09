using System.Data.Common;
using System.Globalization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseDataSources : IDisposable
{
    private const int Capacity = 64;
    private static readonly string[] Keys = ["Endpoint", "Region", "Project", "Database", "ApiKey", "AuthEndpoint"];
    private readonly Lock gate = new();
    private readonly SocketsHttpHandler handler = TerraBaseHttp.CreateHandler();
    private readonly OrderedDictionary<string, TerraBaseSqlDatabase> databases = new(StringComparer.Ordinal);

    public TerraBaseSqlDatabase Database(string connectionString)
    {
        lock (gate)
        {
            if (databases.Remove(connectionString, out var cached))
            {
                databases.Add(connectionString, cached);
                return cached;
            }

            var database = Open(connectionString);
            if (databases.Count == Capacity) databases.RemoveAt(0);
            databases.Add(connectionString, database);
            return database;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            databases.Clear();
            handler.Dispose();
        }
    }

    private TerraBaseSqlDatabase Open(string connectionString)
    {
        var settings = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in settings.Keys)
        {
            if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The TerraBase connection string has an unknown key '{key}'; use {string.Join(", ", Keys)}.", nameof(connectionString));
            }
        }

        var options = new TerraBaseSqlClientOptions
        {
            BaseAddress = Address(settings, "Endpoint"),
            Region = Value(settings, "Region"),
            ApiKey = Value(settings, "ApiKey") ?? throw Missing("ApiKey"),
            AuthAddress = Address(settings, "AuthEndpoint"),
        };
        return new TerraBaseSqlClient(new HttpClient(handler, disposeHandler: false), options)
            .Database(Value(settings, "Project") ?? throw Missing("Project"), Value(settings, "Database") ?? throw Missing("Database"));
    }

    private static Uri? Address(DbConnectionStringBuilder settings, string key) => Value(settings, key) is { } text ? new Uri(text, UriKind.Absolute) : null;

    private static string? Value(DbConnectionStringBuilder settings, string key) =>
        settings.TryGetValue(key, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private static ArgumentException Missing(string key) => new($"The TerraBase connection string needs {key}.", "connectionString");
}
