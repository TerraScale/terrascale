namespace TerraScale.DotNet;

/// <summary>A fully-bound public API route: the request path with query string.</summary>
internal readonly record struct TerraScaleBoundRoute(string RequestUri)
{
    public override string ToString() => RequestUri;
}
