using System.Net;

namespace TerraScale.DotNet.Native;

/// <summary>Fixed safe endpoint-admission failure metadata; no producer text or credential is retained.</summary>
public sealed class NativeAdmissionException : InvalidOperationException
{
    /// <summary>Constructs a trusted provider's bounded safe code and optional HTTP status.</summary>
    public NativeAdmissionException(string code, int? status = null) : base("Native endpoint admission is unavailable.")
    {
        if (string.IsNullOrEmpty(code) || code.Length > 64 || code.Any(character =>
                !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_'))
            throw new ArgumentException("Invalid admission error code.", nameof(code));
        Code = code;
        Status = status;
    }

    /// <summary>The fixed admission code supplied by the trusted provider.</summary>
    public string Code { get; }
    /// <summary>The optional discovery HTTP status; local failures have no synthetic status.</summary>
    public int? Status { get; }
}

/// <summary>Resolves current scoped credential and capability admission immediately before a finite request.</summary>
public interface INativeAdmissionProvider
{
    /// <summary>Admits only the requested provisioned scope and exact capabilities using the current credential.</summary>
    ValueTask<NativeAdmission> ResolveAsync(
        NativeScope scope,
        string apiKey,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken);
}

/// <summary>A scope/credential/capability admission whose lease is checked immediately before send.</summary>
public sealed class NativeAdmission
{
    private readonly Action assertCurrent;

    /// <summary>Binds a validated origin to the trusted provider's immediate freshness assertion.</summary>
    public NativeAdmission(string origin, Action assertCurrent)
        : this(origin, assertCurrent, development: false)
    {
    }

    internal NativeAdmission(string origin, Action assertCurrent, bool development)
    {
        Origin = NativeOrigin.Validate(origin, development);
        this.assertCurrent = assertCurrent ?? throw new ArgumentNullException(nameof(assertCurrent));
    }

    /// <summary>The exact previously admitted native origin.</summary>
    public Uri Origin { get; }
    /// <summary>Checks the retained admission immediately before dispatch.</summary>
    public void AssertCurrent() => assertCurrent();
    /// <inheritdoc />
    public override string ToString() => "Native endpoint admission";
}

/// <summary>Validates explicit native data origins without discovering or inferring eligibility.</summary>
public static class NativeOrigin
{
    /// <summary>Accepts canonical regional TerraScale origins or explicitly provisioned independent HTTPS origins; loopback requires explicit development mode.</summary>
    public static Uri Validate(string origin, bool development = false)
    {
        var uri = NativeOriginValidation.Validate(origin, development);
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        const string zone = ".terrascale.tech";
        if (host == "terrascale.tech" || host.EndsWith(zone, StringComparison.Ordinal))
        {
            var region = host.EndsWith(zone, StringComparison.Ordinal) ? host[..^zone.Length].ToUpperInvariant() : string.Empty;
            if (!uri.IsDefaultPort || !NativeRegionalOrigin.IsRegionCode(region) ||
                !NativeRegionalOrigin.IsTrustedMemberHost(origin.TrimEnd('/'), region))
                throw new ArgumentException("TerraScale native origins must be canonical nonreserved regional HTTPS origins.", nameof(origin));
        }

        return uri;
    }
}

/// <summary>Validates trusted Atlas authority configuration separately from native data origins.</summary>
internal static class NativeAtlasOrigin
{
    internal static Uri Validate(Uri origin) => NativeOriginValidation.Validate(origin.AbsoluteUri, development: false);
}

internal static class NativeOriginValidation
{
    internal static Uri Validate(string origin, bool development)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Native origin must be bare and contain no credentials.", nameof(origin));

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "api.terrascale.tech" || host.EndsWith(".api.terrascale.tech", StringComparison.Ordinal))
            throw new ArgumentException("The test API host is not a production origin.", nameof(origin));

        var loopback = host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
        if (uri.Scheme != Uri.UriSchemeHttps && !(development && loopback && uri.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("Native origins require HTTPS; HTTP loopback requires explicit development mode.", nameof(origin));
        if (!development && loopback)
            throw new ArgumentException("Loopback origins require explicit development mode.", nameof(origin));
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }
}

/// <summary>The caller supplies a previously admitted direct origin; this performs no discovery or fallback.</summary>
public sealed class NativeDirectAdmissionProvider(string admittedOrigin, bool development = false) : INativeAdmissionProvider
{
    private readonly Uri origin = NativeOrigin.Validate(admittedOrigin, development);

    /// <inheritdoc />
    public ValueTask<NativeAdmission> ResolveAsync(NativeScope scope, string apiKey, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NativeAdmission(origin.ToString(), static () => { }, development));
    }

    internal string Origin => origin.GetLeftPart(UriPartial.Authority);
}
