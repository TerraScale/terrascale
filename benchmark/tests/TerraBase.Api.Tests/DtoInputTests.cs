// <copyright file="DtoInputTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>The typed HTTP DTO input validation rows.</summary>
public sealed class DtoInputTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-dto-{Guid.NewGuid():N}.sock");

    /// <summary>An invalid base64 DTO field is a typed 400 and never reaches the engine.</summary>
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
        await Assert.That(engine.Received.Count).IsEqualTo(before);
    }
}
