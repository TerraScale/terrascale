namespace TerraScale.Authentication.Storage;

public static class HomeReads
{
    public const string Header = "TerraScale-Write";

    private static readonly AsyncLocal<bool> Pinned = new();

    public static bool Active => Pinned.Value;

    public static Scope Begin()
    {
        var previous = Pinned.Value;
        Pinned.Value = true;
        return new Scope(previous);
    }

    public readonly struct Scope(bool previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }

    public sealed class Handler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Active)
            {
                request.Headers.Remove(Header);
                request.Headers.Add(Header, "home");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
