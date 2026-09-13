// <copyright file="DtoInputTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>The typed HTTP DTO input validation rows.</summary>
public sealed class DtoInputTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-dto-{Guid.NewGuid():N}.sock");

    /// <summary>An invalid base64 DTO field is a typed 400 and does not dispatch an operation frame.</summary>
    [Test]
    public async Task InvalidBase64IsTypedBadRequestBeforeEngineDispatch()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/families/counter/total/read",
            new { requestCodec = "counter/1", request = "not-base64" });

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("input-invalid");
        await Assert.That(body.RootElement.GetProperty("detail").GetString()).IsEqualTo("request bytes must be valid base64");
        await Assert.That(engine.Received.Skip(before).All(frame => !frame.EndsWith(":FamilyReadRequest", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Family route identifiers use the same bounded scope policy as document routes.</summary>
    [Test]
    public async Task InvalidFamilyScopeIsTypedUnprocessableBeforeEngineDispatch()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/families/not%2Ffamily/total/read",
            new { requestCodec = "counter/1", request = Convert.ToBase64String([1, 2, 3]) });

        await Assert.That(status).IsEqualTo(422);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("scope-invalid");
        await Assert.That(engine.Received.Skip(before).All(frame => !frame.EndsWith(":FamilyReadRequest", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Blank query text is typed as bad input before an engine operation is allocated.</summary>
    [Test]
    public async Task BlankQueryTextIsTypedBadRequestBeforeEngineDispatch()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var (status, body) = await HostHarness.SendAsync(caller, "/v1/query", new { text = "  " });

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("input-invalid");
        await Assert.That(engine.Received.Skip(before).All(frame => !frame.EndsWith(":QueryExecuteRequest", StringComparison.Ordinal))).IsTrue();
    }
}
