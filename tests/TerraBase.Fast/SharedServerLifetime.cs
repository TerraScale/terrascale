namespace TerraBase.Fast;

public static class SharedServerLifetime
{
    [Before(HookType.Assembly)]
    public static async Task InitializeAsync()
    {
        await SharedServer.WarmAsync();
        _ = await SqlTenantFixture.GetAsync();
    }
}
