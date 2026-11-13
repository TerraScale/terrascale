namespace TerraScale.Authentication.Storage;

public static class HomeReads
{
    public const string Header = "TerraScale-Write";

    public sealed class Handler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Headers.Remove(Header);
            request.Headers.Add(Header, "home");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
