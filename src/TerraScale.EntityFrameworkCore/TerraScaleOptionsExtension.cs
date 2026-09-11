using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.EntityFrameworkCore;

internal sealed class TerraScaleOptionsExtension(TerraScaleEntityFrameworkCoreOptions options) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? info;
    public TerraScaleEntityFrameworkCoreOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));
    public DbContextOptionsExtensionInfo Info => info ??= new ExtensionInfo(this);
    public void ApplyServices(IServiceCollection services) { ArgumentNullException.ThrowIfNull(services); }
    public void Validate(IDbContextOptions options) { ArgumentNullException.ThrowIfNull(options); }
    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using TerraScaleEntityFrameworkCore ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["TerraScale:EntityFrameworkCore"] = "1";
    }
}
