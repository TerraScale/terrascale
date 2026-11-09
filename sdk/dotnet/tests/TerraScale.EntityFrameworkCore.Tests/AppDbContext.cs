using Microsoft.EntityFrameworkCore;

namespace TerraScale.EntityFrameworkCore.Tests;

public enum AccountStatus
{
    Pending,
    Active,
    Suspended,
}

public sealed class Account
{
    public required string Id { get; init; }

    public required string Email { get; set; }

    public required string Version { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public AccountStatus Status { get; set; }

    public bool Verified { get; set; }

    public byte[]? Avatar { get; set; }

    public required Preferences Preferences { get; set; }

    public List<Session> Sessions { get; } = [];
}

public sealed record Preferences(string Theme, int PageSize);

public sealed class Session
{
    public required string Id { get; init; }

    public string AccountId { get; set; } = "";

    public required string Device { get; set; }

    public required DateTime ExpiresAt { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.HasIndex(row => row.Email).IsUnique();
            account.Property(row => row.Version).IsConcurrencyToken();
            account.ComplexProperty(row => row.Preferences, preferences => preferences.ToJson());
            account.HasMany(row => row.Sessions).WithOne().HasForeignKey(session => session.AccountId);
            account.ToTable(table => table.HasCheckConstraint("CK_Accounts_Email", "instr(\"Email\", '@') > 1"));
        });
        modelBuilder.Entity<Session>().HasIndex(session => session.ExpiresAt);
    }
}
