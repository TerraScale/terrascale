using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>An operation class a discovered regional endpoint currently accepts.</summary>
public enum TerraScaleRoutingCapability
{
    /// <summary>Document and query reads.</summary>
    Read,

    /// <summary>Mutations; a draining replica loses this capability.</summary>
    Write,

    /// <summary>Parameterized SQL reads.</summary>
    Sql,

    /// <summary>Reactive view attach and delivery.</summary>
    Reactive
}

/// <summary>One regional endpoint of an Atlas routing document, ranked best-first.</summary>
public sealed record TerraScaleRoutingEndpoint(
    string Region,
    Uri Url,
    int Priority,
    IReadOnlyList<TerraScaleRoutingCapability> Capabilities);

/// <summary>
/// The routing document Atlas serves for one database and branch
/// (ADR 0050 §2). Endpoints are the only alternatives the SDK may fail over
/// to — never a region outside the document, never an ineligible replica.
/// </summary>
public sealed record TerraScaleRoutingDocument(
    string Database,
    string Branch,
    long Revision,
    string? Residency,
    IReadOnlyList<string>? ResidencyRegions,
    bool PlacementKnown,
    int RefreshSeconds,
    IReadOnlyList<TerraScaleRoutingEndpoint> Endpoints);

/// <summary>The region catalog Atlas serves for catalog selection (ADR 0050 §8).</summary>
public sealed record TerraScaleRegionCatalog(
    IReadOnlyList<TerraScaleRegion> Regions,
    IReadOnlyDictionary<string, Uri> Hosts);

/// <summary>The regional base a resolved route targets.</summary>
public sealed record TerraScaleRoutingBase(Uri BaseAddress, string Region);

/// <summary>
/// The routing scope a cached document is keyed by (ADR 0050 §5.2): routing
/// learned for one origin, database, branch, project, or tenant never serves
/// another.
/// </summary>
internal sealed record TerraScaleRoutingScope(
    string Origin,
    string Database,
    string Branch,
    string? ProjectId,
    string? TenantId)
{
    public string Key => $"{Origin}|{Database}|{Branch}|p={ProjectId ?? string.Empty}|t={TenantId ?? string.Empty}";
}

/// <summary>
/// Strict validation of Atlas routing documents and region catalogs at the
/// trust boundary. Any structural doubt rejects the document: routing never
/// widens on malformed input, and a hostile document must never steer traffic
/// outside the regional API family (or dev loopback origins when the client
/// itself runs against loopback).
/// </summary>
internal static class TerraScaleRoutingValidation
{
    public const int MinRefreshSeconds = 1;
    public const int MaxRefreshSeconds = 3600;
    public const int MaxEndpoints = 20;

    internal const string RegionalApiDomain = "api.terrascale.tech";

    internal static string RegionalApiHostname(string region) => $"{region.ToLowerInvariant()}.{RegionalApiDomain}";

    /// <summary>Loopback and <c>*.localhost</c> origins: the dev worker publishes these from its DEV_ORIGINS.</summary>
    internal static bool IsDevOrigin(Uri uri) =>
        uri.Host is "localhost" or "127.0.0.1" or "[::1]" ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Producer database-name format (ADR 0050 §2): 1–63 <c>[a-z0-9-]</c>, ending alnum.</summary>
    internal static bool IsValidDatabaseName(string value)
    {
        if (value.Length is < 1 or > 63)
        {
            return false;
        }

        var first = value[0];
        var last = value[^1];
        if (first is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')) ||
            last is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates one discovered endpoint URL: HTTPS on the
    /// <c>{region}.api.terrascale.tech</c> family only, or an HTTP dev origin
    /// when the client itself targets loopback. The canonical origin is
    /// returned; anything else is rejected.
    /// </summary>
    internal static bool TryValidateEndpointUrl(string? raw, string region, bool loopbackClient, out Uri url)
    {
        url = null!;
        if (raw is null || raw.Length is < 1 or > 2048)
        {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            parsed.UserInfo.Length != 0 ||
            parsed.Query.Length != 0 ||
            parsed.Fragment.Length != 0)
        {
            return false;
        }

        var expectedHostname = RegionalApiHostname(region);
        if (parsed.Scheme == Uri.UriSchemeHttps &&
            string.Equals(parsed.Host, expectedHostname, StringComparison.OrdinalIgnoreCase))
        {
            url = CanonicalOrigin(parsed);
            return true;
        }

        if (loopbackClient && parsed.Scheme == Uri.UriSchemeHttp && IsDevOrigin(parsed))
        {
            url = CanonicalOrigin(parsed);
            return true;
        }

        return false;
    }

    private static Uri CanonicalOrigin(Uri uri)
    {
        var authority = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        return new Uri($"{uri.Scheme}://{authority}/", UriKind.Absolute);
    }

    /// <summary>
    /// Strictly parses a discovery response. Returns null when the document
    /// is malformed, the identity echo mismatches, or any endpoint fails
    /// validation.
    /// </summary>
    internal static TerraScaleRoutingDocument? TryParseRoutingDocument(
        string payload,
        string expectedDatabase,
        string expectedBranch,
        bool loopbackClient)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                return null;
            }

            if (!TryReadString(root, "database", out var database) || database != expectedDatabase ||
                !TryReadString(root, "branch", out var branch) || branch != expectedBranch)
            {
                return null;
            }

            string? residency = null;
            IReadOnlyList<string>? residencyRegions = null;
            if (root.TryGetProperty("residency", out var residencyValue) && residencyValue.ValueKind is not JsonValueKind.Null)
            {
                if (residencyValue.ValueKind == JsonValueKind.String)
                {
                    residency = residencyValue.GetString();
                    if (residency is null || residency.Length is < 1 or > 256)
                    {
                        return null;
                    }
                }
                else if (residencyValue.ValueKind == JsonValueKind.Array)
                {
                    if (residencyValue.GetArrayLength() is < 1 or > 20)
                    {
                        return null;
                    }

                    var regions = new List<string>();
                    foreach (var item in residencyValue.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        var code = item.GetString()!;
                        if (!TerraScaleRegionCodes.IsKnown(code) || regions.Contains(code))
                        {
                            return null;
                        }

                        regions.Add(code);
                    }

                    residencyRegions = regions;
                }
                else
                {
                    return null;
                }
            }

            if (!TryReadString(root, "placement", out var placement) ||
                placement is not ("known" or "unknown"))
            {
                return null;
            }

            if (!root.TryGetProperty("revision", out var revision) ||
                revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt64(out var revisionValue) ||
                revisionValue < 0)
            {
                return null;
            }

            if (!root.TryGetProperty("refreshSeconds", out var refresh) ||
                refresh.ValueKind != JsonValueKind.Number ||
                !refresh.TryGetInt32(out var refreshSeconds) ||
                refreshSeconds is < 1 or > 86400)
            {
                return null;
            }

            if (!root.TryGetProperty("endpoints", out var endpointsValue) || endpointsValue.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var endpointCount = endpointsValue.GetArrayLength();
            if (endpointCount is < 1 or > MaxEndpoints)
            {
                return null;
            }

            var endpoints = new List<TerraScaleRoutingEndpoint>(endpointCount);
            var seenRegions = new HashSet<string>(StringComparer.Ordinal);
            var hasPriorityOne = false;
            foreach (var entry in endpointsValue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryReadString(entry, "region", out var region) ||
                    !TerraScaleRegionCodes.IsKnown(region) ||
                    !seenRegions.Add(region))
                {
                    return null;
                }

                if (!entry.TryGetProperty("priority", out var priority) ||
                    priority.ValueKind != JsonValueKind.Number ||
                    !priority.TryGetInt32(out var priorityValue) ||
                    priorityValue is < 1 or > MaxEndpoints)
                {
                    return null;
                }

                if (!TryReadString(entry, "url", out var rawUrl) ||
                    !TryValidateEndpointUrl(rawUrl, region, loopbackClient, out var url))
                {
                    return null;
                }

                if (!entry.TryGetProperty("capabilities", out var capabilitiesValue) ||
                    capabilitiesValue.ValueKind != JsonValueKind.Array ||
                    capabilitiesValue.GetArrayLength() == 0)
                {
                    return null;
                }

                var capabilities = new HashSet<TerraScaleRoutingCapability>();
                foreach (var capability in capabilitiesValue.EnumerateArray())
                {
                    if (capability.ValueKind != JsonValueKind.String ||
                        !TryParseCapability(capability.GetString()!, out var parsedCapability))
                    {
                        return null;
                    }

                    capabilities.Add(parsedCapability);
                }

                hasPriorityOne |= priorityValue == 1;
                endpoints.Add(new TerraScaleRoutingEndpoint(region, url, priorityValue, capabilities.ToList()));
            }

            if (!hasPriorityOne)
            {
                return null;
            }

            var clampedRefresh = Math.Clamp(refreshSeconds, MinRefreshSeconds, MaxRefreshSeconds);
            return new TerraScaleRoutingDocument(
                expectedDatabase,
                expectedBranch,
                revisionValue,
                residency,
                residencyRegions,
                placement == "known",
                clampedRefresh,
                endpoints);
        }
    }

    /// <summary>
    /// Strictly parses a region-catalog response. Hosts obey the same
    /// endpoint-URL trust rules as routing documents.
    /// </summary>
    internal static TerraScaleRegionCatalog? TryParseRegionCatalog(string payload, bool loopbackClient)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("regions", out var regionsValue) ||
                regionsValue.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var regionCount = regionsValue.GetArrayLength();
            if (regionCount is < 1 or > MaxEndpoints)
            {
                return null;
            }

            var regions = new List<TerraScaleRegion>(regionCount);
            foreach (var entry in regionsValue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!TryReadString(entry, "code", out var code) || !TerraScaleRegionCodes.IsKnown(code) ||
                    !TryReadString(entry, "name", out var name) || name.Length is < 1 or > 128 ||
                    !TryReadString(entry, "country", out var country) || country.Length != 2 ||
                    country.Any(character => character is < 'A' or > 'Z'))
                {
                    return null;
                }

                if (!entry.TryGetProperty("latitude", out var latitude) ||
                    latitude.ValueKind != JsonValueKind.Number ||
                    !latitude.TryGetDouble(out var latitudeValue) ||
                    !double.IsFinite(latitudeValue) ||
                    Math.Abs(latitudeValue) > 90)
                {
                    return null;
                }

                if (!entry.TryGetProperty("longitude", out var longitude) ||
                    longitude.ValueKind != JsonValueKind.Number ||
                    !longitude.TryGetDouble(out var longitudeValue) ||
                    !double.IsFinite(longitudeValue) ||
                    Math.Abs(longitudeValue) > 180)
                {
                    return null;
                }

                if (!entry.TryGetProperty("available", out var available) ||
                    available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                if (regions.Any(existing => existing.Code == code))
                {
                    return null;
                }

                regions.Add(new TerraScaleRegion(code, name, country, latitudeValue, longitudeValue, available.GetBoolean()));
            }

            if (!root.TryGetProperty("hosts", out var hostsValue) || hostsValue.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hosts = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var host in hostsValue.EnumerateObject())
            {
                if (!TerraScaleRegionCodes.IsKnown(host.Name))
                {
                    return null;
                }

                if (host.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                // Atlas catalogs publish bare hostnames; discovery endpoints require absolute URLs.
                var rawHost = host.Value.GetString()!;
                var origin = string.Equals(rawHost, RegionalApiHostname(host.Name), StringComparison.OrdinalIgnoreCase)
                    ? $"https://{rawHost}" : rawHost;
                if (!TryValidateEndpointUrl(origin, host.Name, loopbackClient, out var url))
                {
                    return null;
                }

                hosts[host.Name] = url;
            }

            foreach (var region in regions)
            {
                if (!hosts.ContainsKey(region.Code))
                {
                    return null;
                }
            }

            return new TerraScaleRegionCatalog(regions, hosts);
        }
    }

    private static bool TryParseCapability(string value, out TerraScaleRoutingCapability capability)
    {
        switch (value)
        {
            case "read":
                capability = TerraScaleRoutingCapability.Read;
                return true;
            case "write":
                capability = TerraScaleRoutingCapability.Write;
                return true;
            case "sql":
                capability = TerraScaleRoutingCapability.Sql;
                return true;
            case "reactive":
                capability = TerraScaleRoutingCapability.Reactive;
                return true;
            default:
                capability = default;
                return false;
        }
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String)
        {
            value = result.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
