using System.Data.Common;
using System.Globalization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Storage;

internal sealed class TerraBaseDataSources : IDisposable
{
    private static readonly string[] Keys = ["Endpoint", "Region", "Project", "Database", "ApiKey"];
    private readonly Lock gate = new();
    private readonly Dictionary<string, TerraBaseSqlDatabase> databases = new(StringComparer.Ordinal);
    private readonly List<HttpClient> clients = [];

    public TerraBaseSqlDatabase Database(string connectionString)
    {
        lock (gate)
        {
            if (databases.TryGetValue(connectionString, out var database)) return database;
            var settings = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (string key in settings.Keys)
            {
                if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"The TerraBase connection string has an unknown key '{key}'; use {string.Join(", ", Keys)}.", nameof(connectionString));
                }
            }

            var endpoint = Value(settings, "Endpoint");
            var options = new TerraBaseSqlClientOptions
            {
                BaseAddress = endpoint is null ? null : new Uri(endpoint, UriKind.Absolute),
                Region = Value(settings, "Region"),
                ApiKey = Value(settings, "ApiKey") ?? throw Missing("ApiKey"),
            };
            var http = new HttpClient(TerraBaseHttp.CreateHandler());
            try
            {
                database = new TerraBaseSqlClient(http, options).Database(Value(settings, "Project") ?? throw Missing("Project"), Value(settings, "Database") ?? throw Missing("Database"));
            }
            catch
            {
                http.Dispose();
                throw;
            }

            clients.Add(http);
            databases.Add(connectionString, database);
            return database;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var client in clients) client.Dispose();
            clients.Clear();
            databases.Clear();
        }
    }

    private static string? Value(DbConnectionStringBuilder settings, string key) =>
        settings.TryGetValue(key, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private static ArgumentException Missing(string key) => new($"The TerraBase connection string needs {key}.", "connectionString");
}
