using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrLab;

internal static class PitrTarget
{
    internal static async Task PrepareAsync(string directory, PitrTargetIdentity identity, Action<Dictionary<string, string>> configure,
        Action<IServiceCollection>? services, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        if (!Ulid.TryParse(identity.Organization, out _) || !Ulid.TryParse(identity.Project, out _) || !Ulid.TryParse(identity.Database, out _)
            || !Ulid.TryParse(identity.Owner, out _))
        {
            throw new InvalidDataException("target identities must be ULIDs");
        }

        await using var api = await ManagementApi.StartAsync(settings =>
        {
            configure(settings);
            settings["data-directory"] = Path.GetFullPath(directory);
            settings["region"] = "local";
            settings["node-name"] = "local";
        }, collection =>
        {
            services?.Invoke(collection);
            UseConfiguredMasterKey(collection);
            ManualBackupHosting.Configure(collection);
        }, time, cancellationToken: cancellationToken, userId: identity.Owner);
        var tenant = new TenantContext(identity.Organization, PlanTier.Enterprise, identity.Project, identity.Database);
        await CatalogFixture.RegisterProjectAsync(api, identity.Owner, identity.Organization, identity.Project, cancellationToken);
        await api.SetPlanAsync(identity.Owner, identity.Organization, "enterprise", cancellationToken);
        await CatalogFixture.RegisterAsync(api, identity.Owner, tenant, identity.Name, [], cancellationToken);
    }

    internal static async Task RunAsync(PitrJob job, CancellationToken cancellationToken)
    {
        var identity = job.Target ?? throw new InvalidDataException("target is required");
        var directory = job.DataDirectory ?? throw new InvalidDataException("data_directory is required");
        var output = job.Out ?? throw new InvalidDataException("out is required");
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["backup-endpoint"] = "AWS_ENDPOINT_URL_S3",
            ["backup-region"] = "AWS_REGION",
            ["backup-bucket"] = "BUCKET_NAME",
            ["backup-access-key-id"] = "AWS_ACCESS_KEY_ID",
            ["backup-secret-key"] = "AWS_SECRET_ACCESS_KEY",
            ["master-key"] = "TERRABASE_MASTER_KEY",
        };
        var values = names.ToDictionary(pair => pair.Key,
            pair => Environment.GetEnvironmentVariable(pair.Value) is { Length: > 0 } value ? value : throw new InvalidDataException(pair.Value + " is required"));
        await PrepareAsync(directory, identity, settings =>
        {
            foreach (var (key, value) in values)
            {
                settings[key] = value;
            }
        }, null, cancellationToken);
        await PitrJson.WriteAtomicAsync(output, identity, PitrJson.Default.PitrTargetIdentity, cancellationToken);
        Console.WriteLine("prepare-target: isolated tenant catalog prepared");
    }

    internal static async Task CheckAsync(string work, string sourceRoot, ManagedDatabaseRef database, string owner, DateTimeOffset at,
        Dictionary<string, string> texts, byte[] masterKey, TimeProvider time, Func<X509Certificate2, SocketsHttpHandler> handler,
        Action<string, bool, string> check, CancellationToken cancellationToken)
    {
        var root = Path.Combine(work, "fresh-target-bucket");
        var directory = Path.Combine(work, "fresh-target-data");
        var source = PitrSources.Open(new PitrSource("dir", sourceRoot));
        try
        {
            await PitrDownload.RunAsync(source,
                [$"v1/{database.Organization}/{database.Project}/{database.Id}/", $"v1/keys/{database.Organization}/{database.Project}/"],
                root, Path.Combine(work, "fresh-target-manifest.json"), cancellationToken);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }

        await using var serve = await S3Serve.StartAsync(root, "pitrlab-target", null, Path.Combine(work, "fresh-target-s3.log"), cancellationToken);
        void Configure(Dictionary<string, string> settings)
        {
            settings["data-directory"] = directory;
            settings["region"] = "local";
            settings["node-name"] = "local";
            settings["backup-endpoint"] = $"https://127.0.0.1:{serve.Endpoint.Port}";
            settings["backup-region"] = S3Serve.Region;
            settings["backup-bucket"] = "pitrlab-target";
            settings["backup-access-key-id"] = "pitrlab";
            settings["backup-secret-key"] = "pitrlab-local";
            settings["master-key"] = Convert.ToBase64String(masterKey);
        }
        void Trust(IServiceCollection services)
        {
            services.AddHttpClient(S3ObjectStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler(serve.Authority));
        }
        void Services(IServiceCollection services)
        {
            Trust(services);
            UseConfiguredMasterKey(services);
            ManualBackupHosting.Configure(services);
        }

        var identity = new PitrTargetIdentity(database.Organization, database.Project, database.Id, database.Name, owner);
        await PrepareAsync(directory, identity, Configure, Trust, cancellationToken, time);
        await PrepareAsync(directory, identity, Configure, Trust, cancellationToken, time);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task VerifyAsync(ManagementApi target, bool compare)
        {
            using var data = await target.CreateClientAsync(database.Organization, "documents:read");
            var matched = 0;
            foreach (var (name, text) in texts)
            {
                using var read = await data.GetAsync(ManagementApi.Uri(
                    $"/v1/projects/{database.Project}/databases/fresh-restored/collections/people/documents/by-key?id={ManagementApi.DocumentId(name)}"), cancellationToken);
                if (read.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }
                var document = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var version = document.GetProperty("version").GetString()!;
                if (document.GetProperty("document").GetProperty("text").GetString() == text && (!compare || versions[name] == version))
                {
                    matched++;
                }
                versions[name] = version;
            }
            check(compare ? "fresh-target.restart" : "fresh-target.restore", matched == texts.Count,
                $"{matched}/{texts.Count} documents match their text{(compare ? " and pre-restart version" : "")}");
        }

        await using (var target = await ManagementApi.StartAsync(Configure, Services, time, cancellationToken: cancellationToken, userId: owner))
        {
            using var data = await target.CreateClientAsync(database.Organization, "documents:read");
            using var absent = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "person-0")), cancellationToken);
            check("fresh-target.empty", absent.StatusCode == HttpStatusCode.NotFound, "the prepared source registration has no source documents");
            using var restored = await target.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
                new { pointInTime = at, name = "fresh-restored" }, cancellationToken);
            var answer = await restored.Content.ReadAsStringAsync(cancellationToken);
            check("fresh-target.created", restored.StatusCode == HttpStatusCode.Created, $"restore into an isolated catalog: {(int)restored.StatusCode}, {answer}");
            await VerifyAsync(target, compare: false);
        }
        await using (var target = await ManagementApi.StartAsync(Configure, Services, time, cancellationToken: cancellationToken, userId: owner))
        {
            await VerifyAsync(target, compare: true);
        }
    }

    internal static void UseConfiguredMasterKey(IServiceCollection services) =>
        services.Replace(ServiceDescriptor.KeyedSingleton<IMasterKey>(EngineExtensions.DatabaseMasterKey,
            (provider, _) => provider.GetRequiredService<IMasterKey>()));
}
