namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>A fake TerraBase: answers every request with <paramref name="send"/>; nothing leaves the process.</summary>
internal sealed class TerraBaseTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
