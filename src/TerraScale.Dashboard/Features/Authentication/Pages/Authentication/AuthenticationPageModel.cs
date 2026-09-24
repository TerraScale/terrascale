using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Authentication;

public sealed record AuthenticationPageModel : IPageModel
{
    public const int PageSize = 10;

    public required ShellModel Shell { get; init; }
    public required AuthSection Current { get; init; }
    public required AuthInstanceState Instance { get; init; }
    public required string InstanceSlug { get; init; }
    public required ImmutableArray<AuthResource> AllResources { get; init; }
    public string Section => Current.Key;
    public string InstanceName => Instance.Name;
    public ImmutableArray<AuthSection> Sections => [.. AuthSection.All];
    public string Issuer => $"https://auth.terrascale.tech/{InstanceSlug}";
    public IReadOnlyList<string> Roles { get; } = ["organization:owner", "project:admin"];
    public IReadOnlyList<string> Permissions { get; } = ["members:read", "invitations:write", "projects:read", "usage:read"];
    public string Search { get; init; } = "";
    public string? Toast { get; init; }
    public string? Error { get; init; }
    public bool CreateRequested { get; init; }
    public AuthResource? Selected { get; init; }
    public IReadOnlyDictionary<string, string>? FormValues { get; init; }
    public string? Secret { get; init; }
    public ImmutableArray<AuthResource> FilteredResources => [.. AllResources.Where(resource => resource.Section == Section
        && (Search.Length == 0 || resource.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || resource.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || Current.Columns.Any(column => resource.Value(column).Contains(Search, StringComparison.OrdinalIgnoreCase))))];
    public int RequestedPage { get; init; } = 1;
    public int Total => FilteredResources.Length;
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public int Page => Math.Clamp(RequestedPage, 1, PageCount);
    public int First => Total is 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int Last => Math.Min(Page * PageSize, Total);
    public ImmutableArray<AuthResource> Resources => [.. FilteredResources.Skip((Page - 1) * PageSize).Take(PageSize)];
    public int Count(string section) => AllResources.Count(resource => resource.Section == section);
    public string Label(string value) => AllResources.FirstOrDefault(resource => resource.Id == value)?.Name ?? value;

    public static AuthenticationPageModel From(DashboardStore store, string href, string section, string? toast = null,
        string? error = null, string search = "", string id = "", bool create = false, int page = 1) => new()
    {
        Shell = ShellFactory.Create(store, href),
        Current = AuthSection.Find(section)!,
        Instance = store.AuthInstance,
        // IDs keep the demo issuer stable when a project is renamed.
        InstanceSlug = store.ActiveProjectId,
        AllResources = store.AuthInstance.Resources,
        Search = search,
        Toast = toast,
        Error = error,
        CreateRequested = create,
        Selected = store.AuthInstance.Resources.FirstOrDefault(resource => resource.Section == section && resource.Id == id),
        Secret = store.TakeAuthSecret(),
        RequestedPage = page,
    };

    public string Href(int page)
    {
        var query = new List<string>();
        if (Search.Length > 0) query.Add($"q={Uri.EscapeDataString(Search)}");
        if (page > 1) query.Add($"page={page}");
        return query.Count is 0 ? Current.Href : Current.Href + "?" + string.Join('&', query);
    }
}
