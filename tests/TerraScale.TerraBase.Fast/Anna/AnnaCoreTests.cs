using System.Diagnostics;
using System.Net;
using Bogus;
using TerraBase.Configuration;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;

namespace TerraBase.Fast.Anna;

// TS-634 slice 2: the Anna core behind the core switch. Point operations run on the per-core actors and must answer
// as the SQLite core does; everything that needs the disk tier answers 503; the core runs only in Development.
public sealed class AnnaCoreTests
{
    [Test]
    [Arguments("sqlite")]
    [Arguments("anna")]
    public async Task Point_operations_answer_the_same_on_both_cores(string core)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["core"] = core);
        var id = DocumentApi.NewId();
        var first = new Faker().Random.Int(1, 1000);

        var created = await api.PutAsync(id, new { n = first }, DocumentApi.IfNoneMatch("*"));
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Json.GetProperty("durability").GetString()).IsEqualTo(core == "anna" ? "memory" : "local_durable");

        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.ETag).IsEqualTo(created.ETag);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(first);

        await Assert.That((await api.PutAsync(id, new { n = 0 }, DocumentApi.IfNoneMatch("*"))).Code).IsEqualTo("precondition_failed");
        await Assert.That((await api.PutAsync(id, new { n = 0 }, DocumentApi.IfMatch("stale"))).Code).IsEqualTo("precondition_failed");

        var patched = await api.SendRawAsync(
            HttpMethod.Patch, api.Document(id), """{"ops":[{"op":"inc","path":"/n","by":1}]}""", DocumentPatch.OpsMediaType, DocumentApi.IfMatch(read.ETag!));
        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(patched.ETag).IsNotEqualTo(read.ETag);
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(first + 1);
        var conflict = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"ops":[{"op":"inc","path":"/n","by":"x"}]}""", DocumentPatch.OpsMediaType);
        await Assert.That(conflict.Status).IsEqualTo(HttpStatusCode.BadRequest);

        var replaced = await api.PutAsync(id, new { n = -1 }, DocumentApi.IfMatch(patched.ETag!));
        await Assert.That(replaced.Status).IsEqualTo(HttpStatusCode.OK);

        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(id), configure: DocumentApi.IfMatch(replaced.ETag!));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var gone = await api.GetAsync(id);
        await Assert.That((gone.Status, gone.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Document(id))).Code).IsEqualTo("not_found");
        await Assert.That((await api.PutAsync(id, new { n = 2 })).Status).IsEqualTo(HttpStatusCode.Created);

        var generated = await api.SendAsync(HttpMethod.Post, api.Documents, new { n = 3 });
        await Assert.That(generated.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.SendAsync(HttpMethod.Get, generated.Location!.OriginalString)).Status).IsEqualTo(HttpStatusCode.OK);
        var legacy = await api.GetAsync("not-a-uuid");
        await Assert.That((legacy.Status, legacy.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        var unknown = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/missing/documents/{id}");
        await Assert.That((unknown.Status, unknown.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
    }

    [Test]
    [Arguments("sqlite")]
    [Arguments("anna")]
    public async Task Concurrent_increments_of_one_document_all_count(string core)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["core"] = core);
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 0 })).Status).IsEqualTo(HttpStatusCode.Created);

        // One actor owns the document and runs its writes in order, so no increment is lost without any lock.
        const int Increments = 40;
        var patches = await Task.WhenAll(Enumerable.Range(0, Increments).Select(_ =>
            api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"ops":[{"op":"inc","path":"/n","by":1}]}""", DocumentPatch.OpsMediaType)));

        await Assert.That(patches.All(patch => patch.Status == HttpStatusCode.OK)).IsTrue();
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(Increments);
    }

    [Test]
    public async Task Lists_queries_transactions_and_shapes_answer_503_on_the_anna_core()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["core"] = "anna");
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var put = new { op = "put", collection = DocumentApi.Collection, id = DocumentApi.NewId(), document = new { n = 1 } };
        foreach (var response in new[]
        {
            await api.SendAsync(HttpMethod.Get, api.Documents),
            await api.QueryAsync(new { }),
            await api.TransactAsync(put),
            await DocumentApi.SendAsync(shapes, HttpMethod.Get, api.Shape + "?offset=-1"),
        })
        {
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "unavailable"));
        }
    }

    [Test]
    public async Task Dropping_a_collection_forgets_its_documents_on_the_anna_core()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["core"] = "anna");
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);

        await Assert.That((await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/{DocumentApi.Collection}")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection })).Status).IsEqualTo(HttpStatusCode.Created);

        var read = await api.GetAsync(id);
        await Assert.That((read.Status, read.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
    }

    [Test]
    [Arguments("Production", "anna", "core=anna runs only in Development")]
    [Arguments("Development", "turso", "core must be sqlite or anna")]
    public async Task The_real_process_refuses_an_unusable_core(string environment, string core, string message)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-core-").FullName;
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_ENVIRONMENT"] = environment;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            // Every setting once: the ones under test, the rest cleared so the suite's environment cannot leak in.
            foreach (var name in TerraBaseOptions.SettingNames)
            {
                var value = name switch
                {
                    "admin-port" => "0",
                    "data-directory" => directory,
                    "region" => "gru-1",
                    "core" => core,
                    _ => string.Empty,
                };
                start.ArgumentList.Add($"--{name}={value}");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            try
            {
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token), error, process.WaitForExitAsync(timeout.Token));

                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await error).Contains(message);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
