using TerraBase.Features.Rules;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeViewer(RulesAuth Auth)
{
    public string Key { get; } = RulesReads.Identity(Auth);

    public bool Equals(ShapeViewer? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
}
