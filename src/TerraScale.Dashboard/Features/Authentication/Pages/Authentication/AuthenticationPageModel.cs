using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Authentication;

public sealed record AuthenticationPageModel : IPageModel
{
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
    public ImmutableArray<AuthResource> Resources => [.. AllResources.Where(resource => resource.Section == Section
        && (Search.Length == 0 || resource.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || resource.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || Current.Columns.Any(column => resource.Value(column).Contains(Search, StringComparison.OrdinalIgnoreCase))))];
    public int Count(string section) => AllResources.Count(resource => resource.Section == section);
    public string Label(string value) => AllResources.FirstOrDefault(resource => resource.Id == value)?.Name ?? value;

    public static AuthenticationPageModel From(DashboardStore store, string href, string section, string? toast = null,
        string? error = null, string search = "", string id = "", bool create = false) => new()
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
    };
}
