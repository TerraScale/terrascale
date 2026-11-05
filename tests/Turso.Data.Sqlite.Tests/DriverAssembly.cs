namespace Turso.Data.Sqlite.Tests;

/// <summary>The assembly owns native driver initialization; every regression owns its database.</summary>
public static class DriverAssembly
{
    /// <summary>Loads the native driver before the independent regression databases open in parallel.</summary>
    [Before(HookType.Assembly)]
    public static void Initialize()
    {
        using var database = new TemporaryDatabase();
    }
}
