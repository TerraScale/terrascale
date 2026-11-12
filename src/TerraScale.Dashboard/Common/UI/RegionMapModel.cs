using System.Diagnostics;

namespace TerraScale.Dashboard.Common.UI
{
    public enum RegionMapMode
    {
        Select,
        ReadOnly,
    }

    public enum RegionMapFit
    {
        Form,
        Page,
    }

    public sealed record RegionMapRegion(string Code, string Name = "", string Country = "", string Detail = "")
    {
        public string Key => Code.ToLowerInvariant();
        public string Title => Regions.Name(Code) is { Length: > 0 } known ? known : Name.Length > 0 ? Name : Key;
        public string Flag => Regions.Flag(Regions.Country(Code) is { Length: > 0 } known ? known : Country);
    }

    public sealed record RegionMapModel(string Id, RegionMapMode Mode, IReadOnlyList<RegionMapRegion> Regions, IReadOnlyList<string> Selected)
    {
        public RegionMapFit Fit { get; init; } = RegionMapFit.Form;
        public string Label { get; init; } = "Regions";
        public string LabelledBy { get; init; } = "";
        public string Field { get; init; } = "regions";
        public string OrderField { get; init; } = "";
        public bool Exclusive { get; init; }
        public bool Bleed { get; init; }
        public string Minimum { get; init; } = "";

        public static RegionMapModel Select(string id, IEnumerable<string> regions, IEnumerable<string> selected)
        {
            return new(id, RegionMapMode.Select, Rows(regions), [.. selected]) { Fit = RegionMapFit.Form };
        }

        public static RegionMapModel ReadOnly(string id, IEnumerable<string> regions)
        {
            var rows = Rows(regions);
            return new(id, RegionMapMode.ReadOnly, rows, [.. rows.Select(row => row.Code)]) { Fit = RegionMapFit.Page };
        }

        public string FitName => Fit switch
        {
            RegionMapFit.Form => "form",
            RegionMapFit.Page => "page",
            _ => throw new UnreachableException(),
        };

        public static IReadOnlyList<RegionMapRegion> Rows(IEnumerable<string> regions)
        {
            return Rows(regions.Select(code => new RegionMapRegion(code)));
        }

        public static IReadOnlyList<RegionMapRegion> Rows(IEnumerable<RegionMapRegion> regions)
        {
            return [.. regions.Where(region => region.Code.Length > 0)
                .DistinctBy(region => region.Code, StringComparer.OrdinalIgnoreCase)
                .OrderBy(region => UI.Regions.Rank(region.Code)).ThenBy(region => region.Code, StringComparer.OrdinalIgnoreCase)];
        }

        public bool Selects => Mode == RegionMapMode.Select;

        public IReadOnlyList<RegionMapRegion> Shown => Rows(Regions.Concat(Selected.Select(code => new RegionMapRegion(code))));

        public bool IsSelected(RegionMapRegion region)
        {
            return Selected.Contains(region.Code, StringComparer.OrdinalIgnoreCase);
        }

        public string Order => string.Join(' ', Selected.Select(code => code.ToLowerInvariant()).Distinct(StringComparer.Ordinal));
    }
}
