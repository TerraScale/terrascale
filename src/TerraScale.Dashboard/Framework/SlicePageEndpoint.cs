using RazorSlices;
using TerraScale.Endpoints;
using System.Diagnostics;

namespace TerraScale.Dashboard;

/// <summary>
/// Page framework: one feature folder per surface, one code file per page
/// holding its model + endpoint, one slice template next to it.
///
///   Features/Databases/DatabasesPage.cs      (model record + this endpoint)
///   Features/Databases/DatabasesSlice.cshtml (template)
///
/// The endpoint binds the generated slice proxy at compile time:
///
///   public sealed class DatabasesPageEndpoint(DashboardStore store)
///       : SlicePageEndpoint<DatabasesSlice, DatabasesPageModel>(store, "/databases")
///   {
///       protected override DatabasesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
///   }
/// </summary>
public abstract class SlicePageEndpoint<TSliceProxy, TModel>(DashboardStore store, string route)
    : EndpointWithoutRequest<IResult>
    where TSliceProxy : IRazorSliceProxy<TModel>
    where TModel : class, IPageModel
{
    protected DashboardStore Store { get; } = store;

    protected string PageRoute { get; } = route;

    /// <summary>Builds the page model. Keep data shaping here, markup in the slice.</summary>
    protected abstract TModel Load();

    public override void Configure()
    {
        Get(PageRoute);
        Summary($"Renders the {DisplayName} page");
        Tags("Pages");
    }

    public sealed override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = TSliceProxy.CreateSlice(Load());
        stopwatch.Stop();
        HttpContext.Response.Headers.Append(
            "Server-Timing",
            FormattableString.Invariant($"model;dur={stopwatch.Elapsed.TotalMilliseconds:0.##}"));
        return Task.FromResult<IResult>(new TimedPageResult(result));
    }

    private string DisplayName => GetType().Name.EndsWith("PageEndpoint", StringComparison.Ordinal)
        ? GetType().Name[..^"PageEndpoint".Length]
        : GetType().Name;
}
