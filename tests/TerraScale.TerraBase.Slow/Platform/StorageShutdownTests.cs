using Bogus;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class StorageShutdownTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Stopping_under_load_closes_every_database_file()
    {
        var api = await ManagementApi.StartAsync();
        var dataDirectory = api.Server.DataDirectory;
        var serverDisposed = false;
        Tenant? tenant = null;
        using var stop = new CancellationTokenSource();
        var load = new List<Task>();
        try
        {
            tenant = await api.CreateTenantAsync("documents:read", "documents:write");
            var (http, project) = (tenant.Data, tenant.ProjectId);

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
                        using var created = await http.PutAsync(new Uri($"/v1/projects/{project}/databases/{database}", UriKind.Relative), null, stop.Token);
                        using var collection = await http.PostAsJsonAsync(
                            new Uri($"/v1/projects/{project}/databases/{database}/collections", UriKind.Relative), new { name = "items" }, stop.Token);
                        using var put = await http.PutAsJsonAsync(
                            new Uri($"/v1/projects/{project}/databases/{database}/collections/items/documents/{Guid.CreateVersion7()}", UriKind.Relative),
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
            await api.DisposeAsync();
        }
        finally
        {
            // Always stop and join the writers and stop the server, even when a step above failed,
            // so nothing outlives this test.
            await stop.CancelAsync();
            await Task.WhenAll(load);
            tenant?.Dispose();
            if (!serverDisposed)
            {
                await api.DisposeAsync();
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
