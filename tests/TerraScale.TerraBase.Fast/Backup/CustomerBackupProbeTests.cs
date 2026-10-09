using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupProbeTests
{
    [Test]
    [Arguments("oversized", false)]
    [Arguments("oversized", true)]
    [Arguments("mismatch", false)]
    [Arguments("mismatch", true)]
    [Arguments("short", true)]
    [Arguments("empty", true)]
    public async Task Invalid_probe_bodies_are_bounded_rejected_and_cleaned_up(string kind, bool chunked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services));
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        var payload = kind switch
        {
            "oversized" => new byte[1024 * 1024],
            "mismatch" => Encoding.ASCII.GetBytes("terraBase backup access check"),
            "short" => expected[..^1],
            "empty" => [],
            _ => throw new ArgumentException("unknown probe test body", nameof(kind)),
        };
        if (kind == "oversized")
        {
            expected.CopyTo(payload, 0);
        }

        using var body = new ProbeBody(payload, chunked);
        long? contentLength = null;
        oci.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        oci.CustomerGetResponseCreated = (key, response) =>
        {
            if (IsProbe(key))
            {
                contentLength = response.Content.Headers.ContentLength;
                response.Headers.TransferEncodingChunked = chunked ? true : null;
            }
        };
        var deletes = 0;
        var lists = 0;
        oci.ObjectRequested = (customer, method, key) =>
        {
            if (customer && method == HttpMethod.Delete && IsProbe(key))
            {
                Interlocked.Increment(ref deletes);
            }
            if (customer && method == HttpMethod.Get && key.Length == 0)
            {
                Interlocked.Increment(ref lists);
            }
        };

        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination(), token), HttpStatusCode.ServiceUnavailable,
            "unavailable", "customer backup bucket or OCI Vault could not be accessed");
        await Assert.That(body.BytesRead).IsEqualTo(Math.Min(payload.Length, expected.Length + 1));
        await Assert.That(body.LargestRequest).IsLessThanOrEqualTo(expected.Length + 1);
        await Assert.That(body.Disposed).IsTrue();
        await Assert.That(contentLength is null).IsEqualTo(chunked);
        await Assert.That(Volatile.Read(ref deletes)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref lists)).IsEqualTo(0);
        await Assert.That(oci.CustomerObjects.Count).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(0);
        using var absent = await api.Client.GetAsync(route, token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        oci.CustomerContentOverride = null;
        oci.CustomerGetResponseCreated = null;
        using var recovered = await api.Client.PutAsJsonAsync(route, Destination(), token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(oci.CustomerObjects.Count).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exact_probe_bytes_succeed_across_fragmented_reads_and_cleanup(bool chunked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var oci = new FakeOci();
        await using var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services => Manual(oci, services));
        var database = await api.CreateManagedDatabaseAsync();
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        using var body = new ProbeBody(expected, chunked);
        oci.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        oci.CustomerGetResponseCreated = (key, response) =>
        {
            if (IsProbe(key))
            {
                response.Headers.TransferEncodingChunked = chunked ? true : null;
            }
        };

        using var saved = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(), token);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.BytesRead).IsEqualTo(expected.Length);
        await Assert.That(body.LargestRequest).IsLessThanOrEqualTo(expected.Length + 1);
        await Assert.That(body.Reads).IsGreaterThan(1);
        await Assert.That(body.Disposed).IsTrue();
        await Assert.That(oci.CustomerObjects.Count).IsEqualTo(0);
        await Assert.That(oci.Secrets.Count).IsEqualTo(1);
        await Assert.That(oci.BadSignatures).IsEqualTo(0);
    }

    private static bool IsProbe(string key) => key.Contains("/.probe/", StringComparison.Ordinal);

    private static object Destination() => new
    {
        endpoint = FakeOci.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeOci.CustomerBucket,
        accessKeyId = FakeOci.CustomerAccessKey,
        secretKey = FakeOci.CustomerSecretKey,
        mode = "customer",
    };

    private static void Manual(FakeOci oci, IServiceCollection services)
    {
        oci.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private sealed class ProbeBody(byte[] bytes, bool chunked) : MemoryStream(bytes)
    {
        public override bool CanSeek => !chunked;
        public int BytesRead { get; private set; }
        public int LargestRequest { get; private set; }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            Reads++;
            var read = base.Read(buffer.Span[..Math.Min(buffer.Length, 7)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed |= disposing;
        }
    }
}
