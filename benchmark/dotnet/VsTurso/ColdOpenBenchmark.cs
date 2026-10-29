using System.Diagnostics;
using System.Globalization;

namespace TerraBase.E2E
{
    [Category("benchmark-host")]
    public sealed class ColdOpenBenchmark
    {
        [Test]
        [Timeout(30 * 60 * 1000)]
        public async Task Measures_first_reads_of_a_new_database(CancellationToken cancellationToken)
        {
            var output = Environment.GetEnvironmentVariable("TB_COLDOPEN_OUT");
            Skip.When(string.IsNullOrEmpty(output), "host benchmark: set TB_COLDOPEN_OUT");
            await using var log = new StreamWriter(output!, append: false) { AutoFlush = true };
            await using var cluster = await Cluster.StartAsync();
            var gru = cluster[Cluster.Gru];

            async Task TimeAsync(string phase, string step, Func<CancellationToken, Task<HttpResponseMessage>> call)
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(TimeSpan.FromSeconds(60));
                var watch = Stopwatch.StartNew();
                string status;
                try
                {
                    using var response = await call(bounded.Token);
                    await response.Content.ReadAsStringAsync(bounded.Token);
                    status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                }
                catch (OperationCanceledException)
                {
                    status = "timeout60s";
                }

                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{phase}\t{step}\t{status}\t{watch.Elapsed.TotalMilliseconds:F1}"));
            }

            async Task RoundAsync(string phase)
            {
                for (var n = 0; n < 3; n++)
                {
                    var name = $"{phase}{n}";
                    var path = $"{cluster.ProjectPath}/databases/{name}";
                    await TimeAsync(phase, "create", token => gru.PostAsJsonAsync(new Uri($"{cluster.ProjectPath}/databases", UriKind.Relative),
                        new { name, homeRegion = Cluster.Gru, replicaRegions = Array.Empty<string>() }, token));
                    foreach (var attempt in new[] { "first", "second" })
                    {
                        await TimeAsync(phase, attempt + " record", token => gru.GetAsync(new Uri(path, UriKind.Relative), token));
                        await TimeAsync(phase, attempt + " collections", token => gru.GetAsync(new Uri(path + "/collections", UriKind.Relative), token));
                        await TimeAsync(phase, attempt + " backups", token => gru.GetAsync(new Uri(path + "/backups?limit=100", UriKind.Relative), token));
                    }
                }
            }

            await RoundAsync("local");
            await TimeAsync("local", "existing collections", token => gru.GetAsync(new Uri(cluster.DatabasePath + "/collections", UriKind.Relative), token));
        }
    }
}
