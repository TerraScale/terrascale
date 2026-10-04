using Bogus;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Platform;

public sealed class StorageShutdownTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Stopping_under_load_closes_every_database_file()
    {
        using var tls = new ClientTls();
        var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var dataDirectory = server.DataDirectory;
        var serverDisposed = false;
        HttpClient? client = null;
        using var stop = new CancellationTokenSource();
        var load = new List<Task>();
        try
        {
            var http = client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read", "documents:write"));

            // Writers keep opening new databases, so first opens race the shutdown. Only writes that
            // succeeded count, and the server stops once enough have, while the writers still run.
            var written = 0;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            load.AddRange(Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
            {
                for (var round = 0; !stop.IsCancellationRequested; round++)
                {
                    var database = $"load-{worker}-{round}";
                    try
                    {
                        using var created = await http.PutAsync(new Uri($"/v1/databases/{database}", UriKind.Relative), null, stop.Token);
                        using var collection = await http.PostAsJsonAsync(
                            new Uri($"/v1/databases/{database}/collections", UriKind.Relative), new { name = "items" }, stop.Token);
                        using var put = await http.PutAsJsonAsync(
                            new Uri($"/v1/databases/{database}/collections/items/documents/{Guid.CreateVersion7()}", UriKind.Relative),
                            new { title = Faker.Commerce.ProductName() },
                            stop.Token);
                        if (created.IsSuccessStatusCode && collection.IsSuccessStatusCode && put.IsSuccessStatusCode
                            && Interlocked.Increment(ref written) == 64)
                        {
                            loaded.TrySetResult();
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                    {
                        // The server is stopping, or the test is.
                    }
                }
            })));

            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // The pool keeps database files open while the server runs, so the final check is not vacuous.
            await Assert.That(OpenFiles(dataDirectory)).IsNotEmpty();
            serverDisposed = true;
            await server.DisposeAsync();
        }
        finally
        {
            // Always stop and join the writers and stop the server, even when a step above failed,
            // so nothing outlives this test.
            await stop.CancelAsync();
            await Task.WhenAll(load);
            client?.Dispose();
            if (!serverDisposed)
            {
                await server.DisposeAsync();
            }
        }

        await Assert.That(OpenFiles(dataDirectory)).IsEmpty();
    }

    // Files of this process under the directory, from /proc/self/fd.
    private static List<string> OpenFiles(string directory) =>
        [.. new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(descriptor => descriptor.LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith(directory + "/", StringComparison.Ordinal))];
}
