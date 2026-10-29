using System.Globalization;

namespace TerraScale.Dashboard.Common.Usage
{
    public sealed record UsagePlacement(int? RegionCount, string Label)
    {
        public static UsagePlacement For(ProjectRecord? project)
        {
            var databases = project?.Databases.Values.ToArray() ?? [];
            if (databases.Any(database => database.ProjectId != project!.Id))
            {
                throw new IOException("Database placement scope is invalid.");
            }
            // AllRegions records can contain only home, not the current serving topology.
            if (databases.Any(database => database.AllRegions))
            {
                return new(null, "All serving regions (count unavailable)");
            }

            var placements = databases.Select(database => database.Regions.Prepend(database.Region)
                .Select(region => region.Trim()).Where(region => region.Length > 0).ToArray()).ToArray();
            if (placements.Any(regions => regions.Length == 0))
            {
                return new(null, "Placement unavailable");
            }

            var count = placements.SelectMany(regions => regions).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new(count, count.ToString(CultureInfo.InvariantCulture));
        }
    }
}
