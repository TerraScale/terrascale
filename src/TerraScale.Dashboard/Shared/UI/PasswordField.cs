namespace TerraScale.Dashboard.Shared.UI;

public sealed record PasswordField(string Id, string Autocomplete, string Error = "", string Placeholder = "", int MinLength = 0, string HintId = "", bool Autofocus = false)
{
    public string? DescribedBy => string.Join(' ', new[] { HintId, Error.Length != 0 ? Id + "-error" : "" }.Where(value => value.Length != 0)) is { Length: > 0 } value ? value : null;
}
