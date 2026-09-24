namespace TerraScale.Dashboard.Features.Shared.Pages.NotFound;

/// <summary>A standalone error page that can be shown without a dashboard session.</summary>
public sealed record NotFoundPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
