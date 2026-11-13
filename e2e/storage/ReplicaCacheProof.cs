using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sync;
using TerraBase.Hosting;
using TerraBase.Tenancy;

internal static class ReplicaCacheProof
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        long rebootstraps = 0;
        var observed = false;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Name == "terrabase.replica.rebootstraps")
                {
                    observed = true;
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref rebootstraps, measurement));
        listener.Start();
        var source = new Uri(await Console.In.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("missing home URI"));
        var identity = (await Console.In.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("missing database identity")).Split('/');
        var database = new TenantContext(identity[0], PlanTier.Hobby, identity[1], identity[2]);
        var command = await Console.In.ReadLineAsync(cancellationToken);
        if (command == "write")
        {
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("the home database is missing");
            await using var transaction = await store.BeginTransactionAsync(true, cancellationToken);
            _ = await transaction.ExecuteStatementAsync(new SqlStatement("INSERT INTO format_proof VALUES (2, 'replicated-after-upgrade');",
                [], new Dictionary<string, object?>(), 10, 1024, TimeSpan.FromSeconds(5)), cancellationToken);
            _ = await transaction.CommitAsync(cancellationToken);
            Console.WriteLine("CACHE_WRITE committed");
            return;
        }
        using var transport = new PageCounter(services.GetRequiredService<InternalCredential>());
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(60) };
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        await replicas.OpenAsync(database, source, http, cancellationToken);
        var options = services.GetRequiredService<TerraBaseOptions>();
        var path = Path.Combine(options.DataDirectory, ".replicas", identity[0], identity[1], identity[2] + ".db");
        var before = await File.ReadAllBytesAsync(path, cancellationToken);
        Console.WriteLine($"CACHE_OPEN {before.Length} {Convert.ToHexString(SHA256.HashData(before))}");
        if (command == "pull")
        {
            transport.Pages = 0;
            var applied = await replicas.PullAsync(database, true, cancellationToken);
            if (applied is not { Changed: true } || transport.Pages == 0)
            {
                throw new InvalidOperationException("the follower did not apply new pages");
            }
            await using var reader = await replicas.BeginReadAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("the populated cache cannot be read");
            var rows = await reader.ExecuteAsync(new SqlStatement("SELECT value FROM format_proof ORDER BY id;", [],
                new Dictionary<string, object?>(), 10, 1024, TimeSpan.FromSeconds(5)), cancellationToken);
            if (rows.Rows.Count != 2 || rows.Rows[0][0] is not "written-by-previous"
                || rows.Rows[1][0] is not "replicated-after-upgrade")
            {
                throw new InvalidOperationException("the follower did not read the new row");
            }
        }
        if (!observed || rebootstraps != 0)
        {
            throw new InvalidOperationException("the rebootstrap counter was missing or advanced");
        }
        Console.WriteLine($"CACHE_DONE rebootstraps={rebootstraps} pages={transport.Pages}");
    }

    private sealed class PageCounter(InternalCredential credential) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        public int Pages { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            credential.Sign(request.Headers);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                var original = response.Content;
                var bytes = await original.ReadAsByteArrayAsync(cancellationToken);
                using var stream = new MemoryStream(bytes);
                _ = PullUpdatesHeader.Parser.ParseDelimitedFrom(stream);
                while (stream.Position < stream.Length)
                {
                    _ = PageData.Parser.ParseDelimitedFrom(stream);
                    Pages++;
                }
                response.Content = new ByteArrayContent(bytes);
                foreach (var header in original.Headers)
                {
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                original.Dispose();
            }
            return response;
        }
    }
}
