using System.Diagnostics;

namespace TerraBase.Fast.Hosting;

public sealed class DevelopmentPeersTests
{
    [Test]
    public async Task Development_with_a_peers_file_refuses_to_start()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-dev-peers-").FullName;
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "node-a region-a http://127.0.0.1:8081/\n");
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["TS_REGION"] = "region-a";
            start.Environment["TS_NODE"] = "node-a";
            start.Environment["TS_PEERS_FILE"] = peers;
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add("--port=0");
            start.ArgumentList.Add("--internal-port=0");
            start.ArgumentList.Add("--metrics-port=0");
            start.ArgumentList.Add("--postgres-port=0");
            start.ArgumentList.Add($"--data-directory={directory}");

            using var process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            try
            {
                var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
                var output = process.StandardOutput.ReadToEndAsync(token);
                var error = process.StandardError.ReadToEndAsync(token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(token));

                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await output).IsEmpty();
                await Assert.That(await error).Contains("DOTNET_ENVIRONMENT=Development cannot start when TS_PEERS_FILE is set");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
