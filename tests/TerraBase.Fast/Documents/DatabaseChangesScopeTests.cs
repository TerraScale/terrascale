using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;

namespace TerraBase.Fast.Documents;

public sealed class DatabaseChangesScopeTests
{
    [Test]
    [Arguments("items", "other", HttpStatusCode.Gone)]
    [Arguments("items,other", "other,items", HttpStatusCode.OK)]
    public async Task A_cursor_requires_the_same_collection_set(string before, string after, HttpStatusCode expected)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var id = DocumentApi.NewId();
        (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/other/documents/by-key?id={id}", new { value = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var path = api.DatabasePath() + "/changes";
        var cursor = await CursorAsync(api.Client, $"{path}?cursor=now&collections={before}", token);
        using var resumed = await api.Client.GetAsync(new Uri($"{path}?cursor={Uri.EscapeDataString(cursor)}&collections={after}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(resumed.StatusCode).IsEqualTo(expected);
        if (expected == HttpStatusCode.Gone)
        {
            await ExpiredAsync(resumed, token);
        }
    }

    [Test]
    public async Task A_cursor_cannot_resume_another_authorized_database()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var other = api.DatabasePath("other");
        (await api.SendAsync(HttpMethod.Put, other, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var cursor = await CursorAsync(api.Client, api.DatabasePath() + "/changes?cursor=now", token);
        using var resumed = await api.Client.GetAsync(new Uri($"{other}/changes?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await ExpiredAsync(resumed, token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stolen_or_forged_cursor_cannot_read_another_tenants_database(bool forgeScope)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        using var other = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        var path = $"/v1/projects/{other.ProjectId}/databases/db";
        using var created = await other.Data.PutAsync(new Uri(path, UriKind.Relative), null, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var cursor = await CursorAsync(other.Data, path + "/changes?cursor=now", token);
        if (forgeScope)
        {
            cursor = ChangeCursor.Encode(ChangeCursor.Decode(cursor)!, ChangeCursor.Scope(await api.TenantOfAsync(cancellationToken: token), null));
        }

        using var response = await api.Client.GetAsync(new Uri($"{path}/changes?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_database");
    }

    private static async Task ExpiredAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("cursor_expired");
    }

    private static async Task<string> CursorAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                return line[4..];
            }
        }

        throw new EndOfStreamException("the changes stream ended before its cursor");
    }
}
