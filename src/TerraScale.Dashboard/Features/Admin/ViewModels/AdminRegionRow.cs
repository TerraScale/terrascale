namespace TerraScale.Dashboard.Features.Admin.ViewModels;

public sealed record AdminRegionRow(string Name, string Code, string Country, bool Available, int DatabaseCount);
