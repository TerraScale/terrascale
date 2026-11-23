using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Benchmark;

internal sealed record ScenarioResult(
    string Name,
    int Iterations,
    double OpsPerSec,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MeanMs,
    double MinMs,
    double MaxMs);

internal sealed record SdkReport(string Sdk, IReadOnlyList<ScenarioResult> Scenarios);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SdkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;

internal sealed class BenchItem
{
    public long Id { get; set; }

    public string Tag { get; set; } = string.Empty;

    public long Amount { get; set; }
}

internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
{
    public DbSet<BenchItem> Items => Set<BenchItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<BenchItem>(entity =>
        {
            entity.ToTable("bench_items");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(row => row.Tag).HasColumnName("tag").IsRequired();
            entity.Property(row => row.Amount).HasColumnName("amount");
        });
    }
}
