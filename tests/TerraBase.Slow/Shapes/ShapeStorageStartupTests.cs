using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

public sealed class ShapeStorageStartupTests
{
    [Test]
    [Arguments("all")]
    [Arguments("shapes-access-key-id")]
    [Arguments("shapes-secret-access-key")]
    [Arguments("shapes-cdn-api-token")]
    [Arguments("invalid")]
    [Arguments("unreadable")]
    public async Task Production_refuses_missing_or_invalid_shape_credentials_even_with_valid_backup_credentials(string missing)
    {
        var directory = Directory.CreateTempSubdirectory("shape-startup-").FullName;
        try
        {
            var values = new Dictionary<string, string>();
            new FakeS3().Configure(values);
            values["data-directory"] = directory;
            foreach (var key in ShapeStorageSettings.Keys)
            {
                if (missing == "all" || missing == key)
                {
                    values.Remove(key);
                }
            }
            if (missing == "invalid")
            {
                values["shapes-cdn-api-token"] = "invalid synthetic token";
            }
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("TERRABASE_", StringComparison.Ordinal)
                || key.StartsWith("AWS_", StringComparison.Ordinal) || key.StartsWith("TS_", StringComparison.Ordinal) || key == "BUCKET_NAME").ToArray())
            {
                start.Environment.Remove(key);
            }
            if (missing == "unreadable")
            {
                start.Environment["TERRABASE_SHAPES_CDN_API_TOKEN_FILE"] = Path.Combine(directory, "missing-token");
            }
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            foreach (var key in TerraBaseOptions.SettingNames)
            {
                start.ArgumentList.Add($"--{key}={values.GetValueOrDefault(key, string.Empty)}");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellation = deadline.Token;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("TerraBase did not start");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellation);
                var error = process.StandardError.ReadToEndAsync(cancellation);
                await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation));
                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await output).IsEmpty();
                await Assert.That(await error).Contains(missing is "all" or "unreadable" ? "TERRABASE_SHAPES_" : "shapes-access-key-id");
                await Assert.That(await error).DoesNotContain("invalid synthetic token");
                await Assert.That(Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
