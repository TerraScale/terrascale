namespace TerraScale.Dashboard.E2E.Demo;

public static class TerraBaseStorageHooks
{
    [After(HookType.Assembly)]
    public static Task StopAsync(AssemblyHookContext context)
    {
        return TerraBaseStorage.DisposeSharedAsync();
    }
}
