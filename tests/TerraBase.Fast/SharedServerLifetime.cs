namespace TerraBase.Fast;

/// <summary>The assembly owns shared host startup; test durations cover their isolated journeys.</summary>
public static class SharedServerLifetime
{
    [Before(HookType.Assembly)]
    public static async Task InitializeAsync()
    {
        await SharedServer.WarmAsync();
        _ = await SqlTenantFixture.GetAsync();
    }
}
