using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed class HealthyRewriteCrashTests
{
    private const string CrashRoot = "TERRABASE_TEST_REWRITE_CRASH_ROOT";

    [Test]
    public async Task A_process_crash_in_catch_up_keeps_the_old_file_and_startup_removes_scratch()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await CrashChildAsync(childRoot, token);
            return;
        }
        var root = Directory.CreateTempSubdirectory("terrabase-rewrite-crash-").FullName;
        try
        {
            var executable = Environment.ProcessPath!;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            }
            var type = typeof(HealthyRewriteCrashTests);
            foreach (var argument in new[]
            {
                "--treenode-filter", $"/{type.Assembly.GetName().Name}/{type.Namespace}/{type.Name}/{nameof(A_process_crash_in_catch_up_keeps_the_old_file_and_startup_removes_scratch)}",
                "--no-progress", "--disable-logo", "--exit-on-process-exit", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--results-directory", Path.Combine(root, "results"),
            })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment[CrashRoot] = root;
            start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(token);
            var error = child.StandardError.ReadToEndAsync(token);
            try
            {
                await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(2), token);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }
            await Assert.That(child.ExitCode).IsNotEqualTo(0);
            await Assert.That(await output + await error).Contains("crashed in rewrite catch-up");
            var data = Path.Combine(root, "data");
            await Assert.That(Directory.EnumerateFiles(data, "*.rewriting*")).IsNotEmpty();
            await using var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = data);
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
            await Assert.That(store.Incarnation.ToString()).IsEqualTo(await File.ReadAllTextAsync(Path.Combine(root, "incarnation"), token));
            await Assert.That(Directory.EnumerateFiles(data, "*.rewriting*")).IsEmpty();
            await using (var read = await store.BeginTransactionAsync(write: false, token))
            {
                await Assert.That(await read.GetAsync("items", StorageFixture.Id("before"), token)).IsNotNull();
                await Assert.That(await read.GetAsync("items", StorageFixture.Id("during"), token)).IsNotNull();
            }
            _ = await engine.RewriteAsync(null, 0, token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CrashChildAsync(string root, CancellationToken token)
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = Path.Combine(root, "data"));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        await PutAsync("before");
        await File.WriteAllTextAsync(Path.Combine(root, "incarnation"), store.Incarnation.ToString(), token);
        var firstRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.RewriteRoundCompleted = round =>
        {
            if (round == 0)
            {
                firstRound.TrySetResult();
                release.Wait(token);
            }
            else
            {
                Environment.FailFast("crashed in rewrite catch-up");
            }
        };
        var rewrite = engine.RewriteAsync(null, 0, token).AsTask();
        try
        {
            await firstRound.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await PutAsync("during");
        }
        finally
        {
            release.Set();
        }
        _ = await rewrite;
        Assert.Fail("the child must crash before publication");

        async Task PutAsync(string name)
        {
            await using var write = await store.BeginTransactionAsync(write: true, token);
            await write.PutAsync("items", StorageFixture.Id(name), "{}"u8.ToArray(), token);
            await write.CommitAsync(token);
        }
    }
}
