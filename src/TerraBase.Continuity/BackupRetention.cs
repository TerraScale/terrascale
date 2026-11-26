using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Continuity;

public static class BackupRetention
{
    public const int MaxDays = 3650;

    public static int PlanCapDays(string plan) => plan switch
    {
        "hobby" => 1,
        "starter" => 3,
        "pro" => 7,
        "team" => 14,
        PlatformIdentity.PlanKey => MaxDays,
        _ => 30,
    };
}
