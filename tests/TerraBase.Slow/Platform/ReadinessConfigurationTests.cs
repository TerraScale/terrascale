using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class ReadinessConfigurationTests
{
    [Test]
    [Arguments("", 1)]
    [Arguments("1", 1)]
    [Arguments("60", 60)]
    public async Task Readiness_cache_defaults_and_boundaries_start_a_real_listener(string configured, int expected)
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["readiness-cache-seconds"] = configured);
        using var response = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.Services.GetRequiredService<TerraBaseOptions>().ReadinessCacheTtl).IsEqualTo(TimeSpan.FromSeconds(expected));
    }

    [Test]
    [Arguments("0", false)]
    [Arguments("-1", false)]
    [Arguments("61", false)]
    [Arguments("1.5", false)]
    [Arguments("secret-test-value", false)]
    [Arguments("2147483648", false)]
    [Arguments("0", true)]
    public async Task Invalid_readiness_cache_settings_stop_the_real_process(string configured, bool fromEnvironment)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-readiness-config-").FullName;
        try
        {
            // Only the child environment changes; each parallel test owns its process and directory.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["TERRABASE_READINESS_CACHE_SECONDS"] = configured;
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            foreach (var name in TerraBaseOptions.SettingNames)
            {
                if (name == "readiness-cache-seconds" && fromEnvironment)
                {
                    continue;
                }

                var value = name switch
                {
                    "port" or "internal-port" or "metrics-port" or "postgres-port" => "0",
                    "data-directory" => directory,
                    "region" => "test-region",
                    "readiness-cache-seconds" => configured,
                    _ => string.Empty,
                };
                start.ArgumentList.Add($"--{name}={value}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));

                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await output).IsEmpty();
                await Assert.That((await error).Trim()).IsEqualTo("terrabase: readiness-cache-seconds must be an integer between 1 and 60");
                await Assert.That(Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
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
