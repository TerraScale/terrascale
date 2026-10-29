using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupBoundaryTests
{
    [Test]
    [Arguments(16 * 1024)]
    [Arguments(16 * 1024 + 1)]
    public async Task Generated_wrapped_key_boundary_rejects_before_publication_and_restores_on_a_cold_node(int wrappedBytes)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        var directory = Directory.CreateTempSubdirectory("terrabase-key-boundary-").FullName;
        var coldDirectory = Directory.CreateTempSubdirectory("terrabase-key-boundary-cold-").FullName;
        var text = new Faker().Lorem.Sentence(12);
        try
        {
            ManagedDatabaseRef database;
            string user;
            string key;
            DateTimeOffset latest;
            await using (var api = await StartAsync(s3, time, directory, 16 * 1024))
            {
                database = await api.CreateManagedDatabaseAsync();
                user = api.User;
                await SetCustomerAsync(api, database, cancellationToken);
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, database, "people");
                await PutAsync(data, database, text, cancellationToken);
                var keys = $"v1/keys/{database.Organization}/{database.Project}/";
                var uploads = 0;
                s3.ObjectRequested = (customer, method, requested) =>
                {
                    if (customer && method == HttpMethod.Put
                        && (requested.StartsWith(keys, StringComparison.Ordinal) || requested.StartsWith(FilePrefix(database) + "/", StringComparison.Ordinal)))
                    {
                        Interlocked.Increment(ref uploads);
                    }
                };

                var master = (PaddedMasterKey)api.Server.Services.GetRequiredService<IMasterKey>();
                var generatedBeforeShipping = master.GeneratedKeys.Length;
                master.WrappedBytes = wrappedBytes;
                await ShipAsync(api, time, cancellationToken);
                if (wrappedBytes > 16 * 1024)
                {
                    var rejectedKeys = master.GeneratedKeys[generatedBeforeShipping..];
                    await Assert.That(rejectedKeys.Length).IsGreaterThan(0);
                    await Assert.That(rejectedKeys.All(generated => generated.All(value => value == 0))).IsTrue();
                    await Assert.That(uploads).IsEqualTo(0);
                    await Assert.That(s3.CustomerObjects.Keys.Any(requested => requested.StartsWith(keys, StringComparison.Ordinal))).IsFalse();
                    await Assert.That(s3.CustomerObjects.Keys.Any(requested => requested.StartsWith(FilePrefix(database) + "/", StringComparison.Ordinal))).IsFalse();
                    using var status = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/backup"), cancellationToken);
                    status.EnsureSuccessStatusCode();
                    var files = (await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("files");
                    await Assert.That(files.EnumerateArray().Single(file => file.GetProperty("file").GetString() == FilePrefix(database))
                        .GetProperty("lastError").GetString()).IsEqualTo(nameof(InvalidDataException));
                    using var absent = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

                    master.WrappedBytes = 16 * 1024;
                    time.Advance(TimeSpan.FromMinutes(6));
                    await ShipAsync(api, time, cancellationToken);
                    await Assert.That(master.GeneratedKeys.Length).IsGreaterThan(generatedBeforeShipping + rejectedKeys.Length);
                    await Assert.That(rejectedKeys.All(generated => generated.All(value => value == 0))).IsTrue();
                }

                await Assert.That(uploads).IsGreaterThan(0);
                key = s3.CustomerObjects.Keys.Single(requested => requested.StartsWith(keys, StringComparison.Ordinal));
                await Assert.That(s3.CustomerObjects[key].Length).IsEqualTo(16 * 1024);
                latest = await LatestAsync(api.Client, database, cancellationToken);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Copy(file, Path.Combine(coldDirectory, Path.GetFileName(file)));
            }

            var keyReads = 0;
            s3.ObjectRequested = (customer, method, requested) =>
            {
                if (customer && method == HttpMethod.Get && requested == key)
                {
                    Interlocked.Increment(ref keyReads);
                }
            };
            var generatedBeforeRestore = s3.GeneratedKeys;
            await using var cold = await StartAsync(s3, time, coldDirectory, 16 * 1024);
            await Assert.That(File.Exists(Path.Combine(coldDirectory, database.Organization, database.Project, database.Id + ".db"))).IsFalse();
            using var owner = cold.As(user);
            var restored = await RestoreAsync(owner, database, latest, cancellationToken);
            using var reader = await cold.CreateClientAsync(database.Organization, "documents:read");
            await AssertDocumentAsync(reader, restored, text, cancellationToken);
            await Assert.That(keyReads).IsEqualTo(1);
            await Assert.That(((PaddedMasterKey)cold.Server.Services.GetRequiredService<IMasterKey>()).Unwraps).IsEqualTo(3);
            await Assert.That(s3.GeneratedKeys).IsEqualTo(generatedBeforeRestore);
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(coldDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Long_xml_escaped_customer_keys_paginate_within_the_response_bound_and_restore_actual_documents()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var database = await api.CreateManagedDatabaseAsync();
        await SetCustomerAsync(api, database, cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, text, cancellationToken);
        var prefix = FilePrefix(database) + "/snapshots/";
        var escapedKeyBytes = 0;
        for (var index = 0; index < 1000; index++)
        {
            var key = (prefix + "&" + index.ToString("D4", CultureInfo.InvariantCulture)).PadRight(1024, '&');
            s3.SeedObject(customer: true, key, []);
            escapedKeyBytes += Encoding.ASCII.GetByteCount(key.Replace("&", "&amp;", StringComparison.Ordinal));
        }

        await Assert.That(escapedKeyBytes).IsGreaterThan(2 * 1024 * 1024);
        var junkOperations = 0;
        s3.ObjectRequested = (customer, method, key) =>
        {
            if (customer && (method == HttpMethod.Get || method == HttpMethod.Delete)
                && key.StartsWith(prefix + "&", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref junkOperations);
            }
        };
        s3.ObservedListPrefix = prefix;
        await ShipAsync(api, time, cancellationToken);
        var listed = s3.ListRequests;
        var latest = await LatestAsync(api.Client, database, cancellationToken);
        await Assert.That(s3.ListRequests - listed).IsEqualTo(5);
        listed = s3.ListRequests;
        var restored = await RestoreAsync(api.Client, database, latest, cancellationToken);
        await Assert.That(s3.ListRequests - listed).IsEqualTo(5);
        await AssertDocumentAsync(data, restored, text, cancellationToken);
        await Assert.That(junkOperations).IsEqualTo(0);
        await Assert.That(s3.CustomerObjects.Keys.Count(key => key.StartsWith(prefix + "&", StringComparison.Ordinal))).IsEqualTo(1000);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time, string? directory = null, int? wrappedBytes = null)
    {
        var api = await ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            if (directory is not null)
            {
                settings["data-directory"] = directory;
            }
        }, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            if (wrappedBytes is { } size)
            {
                services.Replace(ServiceDescriptor.Singleton<IMasterKey>(provider =>
                    new PaddedMasterKey(new MasterKey(provider.GetRequiredService<BackupSettings>().MasterKey), size)));
            }
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static async Task SetCustomerAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken cancellationToken)
    {
        using var response = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "kept")), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DateTimeOffset> LatestAsync(HttpClient client, ManagedDatabaseRef database, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(HttpClient client, ManagedDatabaseRef database, DateTimeOffset latest, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "boundary-restored", destination = "customer" }, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return new(database.Organization, database.Project, restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
    }

    private static async Task AssertDocumentAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken cancellationToken)
    {
        using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "kept")), cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
            .GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static string FilePrefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class PaddedMasterKey(MasterKey inner, int wrappedBytes) : IMasterKey
    {
        private readonly List<byte[]> _generated = [with(capacity: 8)];
        private int _unwraps;

        public int WrappedBytes { get; set; } = wrappedBytes;

        public int Unwraps => Volatile.Read(ref _unwraps);

        public byte[][] GeneratedKeys
        {
            get
            {
                lock (_generated)
                {
                    return [.. _generated];
                }
            }
        }

        public async Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken)
        {
            var (key, wrapped) = await inner.GenerateDataKeyAsync(cancellationToken);
            lock (_generated)
            {
                if (_generated.Count >= 8)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException("too many generated keys in the boundary fixture");
                }

                _generated.Add(key);
            }

            return (key, wrapped.PadRight(WrappedBytes));
        }

        public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unwraps);
            return inner.UnwrapAsync(wrapped, cancellationToken);
        }
    }
}
