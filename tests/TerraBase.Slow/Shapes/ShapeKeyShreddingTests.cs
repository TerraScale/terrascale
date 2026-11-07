using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

public sealed class ShapeKeyShreddingTests
{
    [Test]
    public async Task A_deleted_database_cannot_open_its_shape_objects(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3 { R2 = true };
        await using var db = await SqlNativeDatabase.StartAsync(services =>
        {
            s3.RoutePlatform(services);
            ManualBackupHosting.Configure(services);
        }, s3.Configure);
        var shape = new TableShapeClient(db.Reader, db.Database.Data, "notes");
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)",
            "INSERT INTO notes (body) VALUES ('first'), ('second')");
        await shape.SyncAsync();
        var handle = shape.Handle!;
        var bootstrap = await shape.GetAsync(new Uri($"{shape.Path}?offset=-1", UriKind.Relative));
        var first = bootstrap.Location!.OriginalString.Split('?')[1].Split('&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal))[7..];
        JsonElement[] objects = [];
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var manifest = await db.Reader.GetAsync(new Uri($"{shape.Path}/manifest?handle={handle}&offset={first}", UriKind.Relative), token);
            await Assert.That(manifest.StatusCode).IsEqualTo(HttpStatusCode.OK);
            objects = [.. (await manifest.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("objects").EnumerateArray()];
            return objects.Length > 0;
        }, timeout: TimeSpan.FromSeconds(30));

        var opened = new List<string>();
        foreach (var item in objects)
        {
            var key = await KeyAsync(db, shape, handle, item, cancellationToken);
            await Assert.That(key).IsNotNull();
            opened.Add(Encoding.UTF8.GetString(Open(s3.Objects[item.GetProperty("object").GetString()!], handle, item.GetProperty("offset").GetString()!, key!)));
        }

        await Assert.That(opened.Any(text => text.Contains("second", StringComparison.Ordinal))).IsTrue();

        var tenant = new TenantContext(db.Database.Organization, PlanTier.Hobby, db.Database.Project, db.Database.Id);
        using (var deleted = await db.Api.Client.DeleteAsync(ManagementApi.Uri(db.Database.Path), cancellationToken))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        await db.Api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(cancellationToken);
        var keys = db.Api.Server.Services.GetRequiredService<DataKeys>();
        foreach (var item in objects)
        {
            await Assert.That(await KeyAsync(db, shape, handle, item, cancellationToken)).IsNull();
            await Assert.That(() => (Task)keys.GetAsync(BackupLayout.KeyScope(tenant), item.GetProperty("key_id").GetString()!, cancellationToken))
                .Throws<DatabaseShreddedException>();
        }
    }

    private static async Task<byte[]?> KeyAsync(SqlNativeDatabase db, TableShapeClient shape, string handle, JsonElement item, CancellationToken cancellationToken)
    {
        using var response = await db.Reader.GetAsync(new Uri(
            $"{shape.Path}/keys?handle={handle}&epoch={item.GetProperty("epoch").GetString()}&key_id={item.GetProperty("key_id").GetString()}",
            UriKind.Relative), cancellationToken);
        return response.StatusCode == HttpStatusCode.OK
            ? Base64Url.DecodeFromChars((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("key").GetString())
            : null;
    }

    /// <summary>The client side of TBS1 (<see cref="ShapeCrypto.Seal"/>): authenticate every frame and return the plaintext.</summary>
    private static byte[] Open(byte[] sealedBytes, string handle, string offset, byte[] epochKey)
    {
        const int header = ShapeCrypto.HeaderBytes;
        const int tag = 16;
        const uint last = 1u << 31;
        var salt = sealedBytes.AsSpan(header - 32, 32).ToArray();
        var objectKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, salt, Encoding.UTF8.GetBytes("TBS1 object\0" + handle + "\0" + offset));
        using var aes = new AesGcm(objectKey, tag);
        using var plain = new MemoryStream();
        var associated = new byte[header + 12];
        sealedBytes.AsSpan(0, header).CopyTo(associated);
        Span<byte> nonce = stackalloc byte[12];
        var at = header;
        for (ulong index = 0; ; index++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(sealedBytes.AsSpan(at));
            var length = (int)(word & ~last);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
            BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(header), index);
            BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(header + 8), word);
            var frame = new byte[length];
            aes.Decrypt(nonce, sealedBytes.AsSpan(at + 4, length), sealedBytes.AsSpan(at + 4 + length, tag), frame, associated);
            plain.Write(frame);
            at += 4 + length + tag;
            if ((word & last) != 0)
            {
                return at == sealedBytes.Length ? plain.ToArray() : throw new InvalidDataException("bytes after the last TBS1 frame");
            }
        }
    }
}
