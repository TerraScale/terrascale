using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupEmptySegmentTests
{
    [Test]
    public async Task Empty_wal_segments_authenticate_and_a_cancelled_restore_does_not_block_the_next_restore(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var requests = new ObserveCancelledRestore();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IStartupFilter>(requests);
        }, time);
        var database = await api.CreateManagedDatabaseAsync("main");
        requests.Path = database.Path + "/point-in-time/restore";
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items", cancellationToken);
        var text = new Faker().Lorem.Sentence(8);
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "before")), new { text }, cancellationToken);
        written.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);

        var file = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var snapshot = s3.Objects.Single(item => item.Key.StartsWith(file + "/snapshots/", StringComparison.Ordinal));
        var parts = snapshot.Key[(snapshot.Key.LastIndexOf('/') + 1)..].Split('-');
        await Assert.That(parts[2]).IsEqualTo("0000000000000000");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture));
        var segments = $"{file}/wal/{parts[1]}/";
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(segments, StringComparison.Ordinal))).IsFalse();
        var segment = string.Create(CultureInfo.InvariantCulture, $"{segments}0000000000000000-{at.ToUnixTimeMilliseconds():D13}");
        var keyId = Encoding.ASCII.GetString(snapshot.Value, 4, 26);
        var wrapped = Encoding.ASCII.GetString(s3.Objects[$"v1/keys/{database.Organization}/{database.Project}/{keyId}"]);
        var sealedEmpty = SealEmpty(segment, keyId, s3.Unwrap(wrapped));
        s3.StoreObject(segment, sealedEmpty);

        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s3.BeforeGet = async (key, token) =>
        {
            if (key == segment)
            {
                requested.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Closing the connection makes cancellation observable after the POST body has been consumed.
            using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(database.Path + "/point-in-time/restore"))
            {
                Content = JsonContent.Create(new { pointInTime = at, name = "cancelled" }),
            };
            var pending = api.Client.SendAsync(request, cancel.Token);
            await requested.Task.WaitAsync(cancellationToken);
            await cancel.CancelAsync();
            await Assert.That(async () => { using var response = await pending; }).Throws<OperationCanceledException>();
            await requests.Finished.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            release.TrySetResult();
            s3.BeforeGet = null;
        }

        using var cancelled = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/cancelled/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
        await Assert.That(cancelled.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name = "empty" }, cancellationToken);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var read = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/empty/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
        read.EnsureSuccessStatusCode();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);

        sealedEmpty[^1] ^= 0xff;
        using var rejected = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name = "bad-tag" }, cancellationToken);
        await ErrorAssert.ErrorAsync(rejected, HttpStatusCode.Conflict, "backup_unreadable",
            "continuous backup of this database cannot be read: an object fails authentication or its integrity check");
        using var absent = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/bad-tag/collections/items/documents/by-key?id={ManagementApi.DocumentId("before")}"), cancellationToken);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    // Independent format 1 fixture: authenticate the header, zero chunk index and one empty last chunk.
    private static byte[] SealEmpty(string objectKey, string keyId, byte[] dataKey)
    {
        const int headerBytes = 62;
        const int tagBytes = 16;
        var content = new byte[headerBytes + 8 + tagBytes];
        var header = content.AsSpan(0, headerBytes);
        "TBK1"u8.CopyTo(header);
        Encoding.ASCII.GetBytes(keyId, header.Slice(4, 26));
        var salt = header[30..];
        RandomNumberGenerator.Fill(salt);
        var words = content.AsSpan(headerBytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(words, 1u << 31);
        var associated = new byte[headerBytes + 16];
        header.CopyTo(associated);
        words.CopyTo(associated.AsSpan(headerBytes + 8));
        using var aes = new AesGcm(HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt.ToArray(),
            Encoding.UTF8.GetBytes("terrabase backup 1\0" + objectKey)), tagBytes);
        aes.Encrypt(new byte[12], [], [], content.AsSpan(headerBytes + 8, tagBytes), associated);
        return content;
    }

    // AWS can cancel its caller's wait without signalling the fake transport's token.
    private sealed class ObserveCancelledRestore : IStartupFilter
    {
        public string? Path { get; set; }

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                try
                {
                    await continuation(context);
                }
                finally
                {
                    if (context.Request.Path.Value == Path && context.RequestAborted.IsCancellationRequested)
                    {
                        Finished.TrySetResult();
                    }
                }
            });
            next(app);
        };
    }
}
