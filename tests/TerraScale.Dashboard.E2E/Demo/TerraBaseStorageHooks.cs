namespace TerraScale.Dashboard.E2E.Demo;

public static class TerraBaseStorageHooks
{
    [Before(HookType.Assembly)]
    public static void Start(AssemblyHookContext context)
    {
        _ = TerraBaseStorage.SharedAsync();
    }

    [After(HookType.Assembly)]
    public static Task StopAsync(AssemblyHookContext context)
    {
        return TerraBaseStorage.DisposeSharedAsync();
    }
}
