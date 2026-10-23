using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupRequiredTests
{
    [Test]
    public async Task Backupz_answers_200_only_after_every_file_this_node_ships_reached_the_bucket()
    {
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        });
        using var before = await server.Client.GetAsync(new Uri("/backupz", UriKind.Relative));

        await server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var after = await server.Client.GetAsync(new Uri("/backupz", UriKind.Relative));
        var body = await after.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("shipping");
        await Assert.That(after.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/snapshots/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Backupz_answers_503_in_development_without_backup()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        using var response = await server.Client.GetAsync(new Uri("/backupz", UriKind.Relative));
        using var anonymous = await client.GetAsync(new Uri("/backupz", UriKind.Relative));
        var body = await anonymous.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        // Anonymous public callers never see the build revision, storage format or node name.
        await Assert.That(body.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["status", "version", "region"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Outside_development_a_node_without_the_tigris_bucket_and_master_key_refuses_to_start(bool unrelatedAwsVariables)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-required-").FullName;
        try
        {
            // Like RegionTests, run the real binary; the child inherits the suite's low CPU priority.
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            foreach (var name in new[] { "AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "TERRABASE_MASTER_KEY" })
            {
                start.Environment.Remove(name);
            }

            if (unrelatedAwsVariables)
            {
                // Other tooling's AWS variables are not an attached bucket: still "no backup", not a partial backup setting.
                start.Environment["AWS_REGION"] = "us-east-1";
                start.Environment["AWS_ACCESS_KEY_ID"] = "unrelated";
            }

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
                await Assert.That(await error).Contains("AWS_ENDPOINT_URL_S3");
                await Assert.That(await error).Contains("TERRABASE_MASTER_KEY");
                await Assert.That(await error).DoesNotContain("must be set together");
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
