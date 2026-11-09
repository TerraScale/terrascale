using TUnit.Core.Interfaces;

namespace TerraScale.EntityFrameworkCore.Tests;

public sealed class TerraBaseFixture : IAsyncInitializer, IAsyncDisposable
{
    public TerraBaseServer Server { get; } = new();

    public Task InitializeAsync() => Server.StartAsync();

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
