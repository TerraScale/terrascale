namespace TerraBase.Continuity;

public static class BackupRetention
{
    public static int PlanCapDays(string plan) => plan switch
    {
        "hobby" => 1,
        "starter" => 3,
        "pro" => 7,
        "team" => 14,
        _ => 30,
    };
}
