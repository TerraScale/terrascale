using System.Diagnostics;
using TerraBase.Configuration;

namespace TerraBase.Fast.Hosting;

public sealed class ProductionBackupBucketTests
{
    [Test]
    public async Task Production_without_a_backup_bucket_refuses_to_start()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-prod-bucket-").FullName;
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            foreach (var variable in BackupSettings.TigrisVariables)
            {
                start.Environment[variable.Variable] = "";
            }

            start.Environment["TERRABASE_MASTER_KEY"] = "";
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
                await Assert.That(await error).Contains("BUCKET_NAME");
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
