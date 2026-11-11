namespace TerraScale.Dashboard.Features.Analytics;

public sealed record FilterField(string Name, string Value);
public sealed record FilterOption(string Value, string Label);
public sealed record FilterControl(string Id, string Label, string Action, string Name, string Value, IReadOnlyList<FilterOption> Options, IReadOnlyList<FilterField> Carried)
{
    public static IReadOnlyList<FilterOption> Ranges { get; } = [new("1h", "1h"), new("24h", "24h"), new("7d", "7d"), new("30d", "30d")];
    public string ValueLabel => Options.FirstOrDefault(option => option.Value == Value)?.Label ?? Value;
    public string Href(FilterOption option)
    {
        return Action + "?" + string.Join("&", Carried.Append(new(Name, option.Value)).Select(field => Uri.EscapeDataString(field.Name) + "=" + Uri.EscapeDataString(field.Value)));
    }
}
