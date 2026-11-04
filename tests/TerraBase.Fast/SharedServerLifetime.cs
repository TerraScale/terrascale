using TerraBase.Testing;

namespace TerraBase.Fast;

public static class SharedServerLifetime
{
    [Before(HookType.Assembly)]
    public static Task StartAsync() => SharedServer.WarmAsync();
}
