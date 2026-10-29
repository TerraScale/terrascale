using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var url = new Uri(Environment.GetEnvironmentVariable("DATABASE_URL") ?? throw new InvalidOperationException("DATABASE_URL is required"));
var surface = Environment.GetEnvironmentVariable("MATRIX_SURFACE") ?? "unknown";
var repeats = int.Parse(Environment.GetEnvironmentVariable("MATRIX_REPEAT") ?? "3", CultureInfo.InvariantCulture);
var credentials = url.UserInfo.Split(':', 2);
var password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null;
var connection = new NpgsqlConnectionStringBuilder
{
    Host = url.Host,
    Port = url.Port > 0 ? url.Port : 5432,
    Username = Uri.UnescapeDataString(credentials[0]),
    Password = password,
    Database = Uri.UnescapeDataString(url.AbsolutePath.TrimStart('/')),
    SslMode = url.Query.Contains("sslmode=disable", StringComparison.Ordinal) ? SslMode.Disable : SslMode.Prefer,
}.ConnectionString;
var autoPrepared = new NpgsqlConnectionStringBuilder(connection) { MaxAutoPrepare = 8, AutoPrepareMinUsages = 2 }.ConnectionString;
var failures = 0;

for (var repeat = 1; repeat <= repeats; repeat++)
{
    var prefix = "ef_" + Ulid.NewUlid().ToString().ToLowerInvariant();
    var email = $"ada-{repeat}@example.test";
    await Check("connect", async stage =>
    {
        await using var db = Matrix(connection);
        await db.Database.OpenConnectionAsync();
        return $"server {db.Database.GetDbConnection().ServerVersion}";
    });
    await Check("migrate_table", stage => Migrate(TablesMigration.Id));
    await Check("migrate_fk", stage => Migrate(ForeignKeyMigration.Id));
    await Check("migrate_index", async stage =>
    {
        var detail = await Migrate(null);
        stage("pending model changes");
        await using var db = Matrix(connection);
        return db.Database.HasPendingModelChanges() ? throw new InvalidOperationException("model differs from snapshot") : detail;
    });
    await Check("introspect", stage => Task.FromResult(Introspect(connection, prefix)));
    await Check("crud", async stage =>
    {
        string authorId;
        await using (var db = Matrix(connection))
        {
            stage("insert graph");
            var author = new Author { Name = "Ada", Email = email };
            author.Posts.Add(new Post { Title = "First", Published = true, CreatedAt = DateTime.UtcNow });
            author.Posts.Add(new Post { Title = "Draft", CreatedAt = DateTime.UtcNow.AddMinutes(1) });
            db.Authors.Add(author);
            Expect(await db.SaveChangesAsync() == 3, "SaveChanges wrote a different row count");
            authorId = author.Id;
        }

        await using (var db = Matrix(connection))
        {
            stage("query");
            var author = await db.Authors.Include(a => a.Posts.Where(p => p.Published)).SingleAsync(a => a.Email == email);
            var drafts = await db.Posts.CountAsync(p => !p.Published && p.Author!.Email == email);
            var latest = await db.Posts.Where(p => p.AuthorId == authorId).OrderByDescending(p => p.CreatedAt).Select(p => p.Title).FirstAsync();
            Expect(author.Posts.Count == 1 && drafts == 1 && latest == "Draft", $"published={author.Posts.Count} drafts={drafts} latest={latest}");
            stage("tracked update");
            author.Name = "Ada L.";
            await db.SaveChangesAsync();
            stage("bulk update");
            Expect(await db.Posts.Where(p => p.AuthorId == authorId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Published, true)) == 2, "ExecuteUpdate count");
        }

        await using (var db = Matrix(connection))
        {
            stage("read back");
            var name = await db.Authors.Where(a => a.Id == authorId).Select(a => a.Name).SingleAsync();
            Expect(name == "Ada L." && !await db.Posts.AnyAsync(p => p.AuthorId == authorId && !p.Published), $"name={name}");
            stage("cascade delete");
            db.Authors.Remove(await db.Authors.SingleAsync(a => a.Id == authorId));
            await db.SaveChangesAsync();
            Expect(!await db.Posts.AnyAsync(p => p.AuthorId == authorId), "posts survived their author");
            stage("bulk delete");
            db.Authors.Add(new Author { Name = "Bulk", Email = $"bulk-{repeat}@example.test" });
            await db.SaveChangesAsync();
            Expect(await db.Authors.Where(a => a.Email.StartsWith("bulk-")).ExecuteDeleteAsync() == 1, "ExecuteDelete count");
        }

        return "insert graph, include/count/order query, tracked and bulk update, cascade and bulk delete";
    });
    await Check("transactions", async stage =>
    {
        foreach (var commit in (bool[])[true, false])
        {
            stage(commit ? "commit" : "rollback");
            var address = $"tx-{commit}-{repeat}@example.test";
            await using (var db = Matrix(connection))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.Authors.Add(new Author { Name = "Tx", Email = address });
                await db.SaveChangesAsync();
                await (commit ? transaction.CommitAsync() : transaction.RollbackAsync());
            }

            await using var check = Matrix(connection);
            Expect(await check.Authors.AnyAsync(a => a.Email == address) == commit, $"visible={!commit} after {(commit ? "commit" : "rollback")}");
        }

        return "committed row visible, rolled back row absent";
    });
    await Check("json", async stage =>
    {
        await using var db = Matrix(connection);
        stage("insert jsonb");
        db.Authors.Add(new Author { Name = "Json", Email = $"json-{repeat}@example.test", Profile = """{"tier": "gold", "score": 9}""" });
        await db.SaveChangesAsync();
        stage("jsonb containment filter");
        var found = await db.Authors.Where(a => EF.Functions.JsonContains(a.Profile!, """{"tier": "gold"}""")).Select(a => a.Profile).SingleAsync();
        using var document = JsonDocument.Parse(found!);
        Expect(document.RootElement.GetProperty("score").GetInt32() == 9, $"profile={found}");
        return "jsonb insert, @> filter and round trip";
    });
    await Check("prepared", async stage =>
    {
        await using var db = Matrix(autoPrepared);
        stage("explicit prepare");
        var raw = (NpgsqlConnection)db.Database.GetDbConnection();
        await raw.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{prefix}_authors\" WHERE \"Email\" LIKE $1", raw);
        command.Parameters.Add(new NpgsqlParameter { Value = "tx-%" });
        await command.PrepareAsync();
        var counts = new[] { await command.ExecuteScalarAsync(), await command.ExecuteScalarAsync() };
        Expect(command.IsPrepared && counts.All(c => Convert.ToInt64(c, CultureInfo.InvariantCulture) == 1), $"prepared={command.IsPrepared} counts={string.Join(',', counts)}");
        stage("auto-prepared LINQ");
        for (var i = 0; i < 3; i++)
        {
            Expect(await db.Authors.CountAsync(a => a.Email == $"tx-True-{repeat}@example.test") == 1, $"execution {i}");
        }

        return "explicit Prepare twice, LINQ 3x under auto-prepare";
    });
    await Check("errors", async stage =>
    {
        stage("unique 23505");
        await ExpectState("23505", async () =>
        {
            await using var db = Matrix(connection);
            db.Authors.AddRange(new Author { Name = "A", Email = $"dup-{repeat}" }, new Author { Name = "B", Email = $"dup-{repeat}" });
            await db.SaveChangesAsync();
        });
        stage("foreign key 23503");
        await ExpectState("23503", async () =>
        {
            await using var db = Matrix(connection);
            db.Posts.Add(new Post { AuthorId = Ulid.NewUlid().ToString(), Title = "Orphan", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        stage("undefined table 42P01");
        await ExpectState("42P01", async () =>
        {
            await using var db = Matrix(connection);
            await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM ef_missing_table");
        });
        return "23505 23503 42P01";
    });

    MatrixContext Matrix(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MatrixContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(prefix + "_history"))
            .LogTo(message => Console.Error.WriteLine(Redact(message)), [RelationalEventId.CommandError])
            .ReplaceService<IModelCacheKeyFactory, PrefixModelCacheKey>()
            .ReplaceService<IMigrationsAssembly, StagedMigrations>()
            .Options;
        return new MatrixContext(options, prefix);
    }

    async Task<string> Migrate(string? target)
    {
        await using var db = Matrix(connection);
        if (target is null)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var expected = target ?? IndexesMigration.Id;
        Expect(applied.LastOrDefault() == expected, $"history ends at {applied.LastOrDefault()}");
        return $"applied {string.Join(',', applied)}";
    }

    async Task Check(string name, Func<Action<string>, Task<string>> body)
    {
        var stage = "";
        string status;
        string detail;
        try
        {
            detail = await body(next => stage = next);
            status = "pass";
        }
        catch (Exception error)
        {
            detail = (stage.Length > 0 ? stage + ": " : "") + Describe(error);
            status = "fail";
            failures++;
            Console.Error.WriteLine(Redact($"{name} repeat {repeat}: {error}"));
        }

        Console.WriteLine(JsonSerializer.Serialize(new Result(surface, "efcore", repeat, name, status, Redact(detail)), ResultJson.Default.Result));
    }

    string Redact(string text)
    {
        return password is { Length: > 0 } ? text.Replace(password, "***", StringComparison.Ordinal) : text;
    }
}

return failures == 0 ? 0 : 1;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string Introspect(string connection, string prefix)
{
    var services = new ServiceCollection().AddLogging();
    var provider = typeof(NpgsqlDbContextOptionsBuilderExtensions).Assembly;
    var design = provider.GetCustomAttribute<DesignTimeProviderServicesAttribute>() ?? throw new InvalidOperationException("no design-time services");
    ((IDesignTimeServices)Activator.CreateInstance(provider.GetType(design.TypeName, throwOnError: true)!)!).ConfigureDesignTimeServices(services);
    using var scope = services.BuildServiceProvider();
    var model = scope.GetRequiredService<IDatabaseModelFactory>().Create(connection, new DatabaseModelFactoryOptions([prefix + "_authors", prefix + "_posts"]));
    var authors = model.Tables.FirstOrDefault(t => t.Name == prefix + "_authors");
    var posts = model.Tables.FirstOrDefault(t => t.Name == prefix + "_posts");
    Expect(authors is not null && posts is not null, $"tables={string.Join(',', model.Tables.Select(t => t.Name))}");
    Expect(authors!.PrimaryKey?.Columns.SingleOrDefault()?.Name == "Id", "authors primary key");
    Expect(posts!.ForeignKeys.Any(k => k.PrincipalTable.Name == authors.Name && k.OnDelete == ReferentialAction.Cascade), "cascading posts foreign key");
    Expect(authors.Indexes.Any(i => i.IsUnique && i.Columns.Single().Name == "Email"), "unique Email index");
    Expect(posts.Indexes.Any(i => i.Columns.Select(c => c.Name).SequenceEqual(["AuthorId", "CreatedAt"])), "composite posts index");
    var profile = authors.Columns.FirstOrDefault(c => c.Name == "Profile")?.StoreType;
    Expect(profile == "jsonb", $"Profile store type {profile}");
    return "tables, primary key, cascading foreign key, unique and composite indexes, jsonb column";
}

static async Task ExpectState(string state, Func<Task> body)
{
    try
    {
        await body();
    }
    catch (Exception error) when (Postgres(error) is { } postgres)
    {
        Expect(postgres.SqlState == state, $"got {postgres.SqlState}: {postgres.MessageText}");
        return;
    }

    throw new InvalidOperationException("no error raised");
}

static PostgresException? Postgres(Exception? error)
{
    return error switch
    {
        null => null,
        PostgresException postgres => postgres,
        _ => Postgres(error.InnerException),
    };
}

static string Describe(Exception error)
{
    return Postgres(error) is { } postgres
        ? $"{postgres.SqlState}: {postgres.MessageText}"
        : $"{error.GetType().Name}: {error.Message}{(error.InnerException is { } inner ? $" <- {inner.GetType().Name}: {inner.Message}" : "")}";
}

internal sealed class Author
{
    public string Id { get; set; } = Ulid.NewUlid().ToString();
    public required string Name { get; set; }
    public required string Email { get; set; }
    public string? Profile { get; set; }
    public List<Post> Posts { get; } = [];
}

internal sealed class Post
{
    public string Id { get; set; } = Ulid.NewUlid().ToString();
    public string AuthorId { get; set; } = "";
    public Author? Author { get; set; }
    public required string Title { get; set; }
    public bool Published { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class MatrixContext(DbContextOptions<MatrixContext> options, string prefix) : DbContext(options)
{
    public string Prefix { get; } = prefix;
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Post> Posts => Set<Post>();

    internal static void Build(ModelBuilder modelBuilder, string prefix)
    {
        modelBuilder.Entity<Author>(author =>
        {
            author.ToTable(prefix + "_authors");
            author.HasKey(a => a.Id);
            author.Property(a => a.Id).HasMaxLength(26).HasColumnType("character varying(26)");
            author.Property(a => a.Name).IsRequired().HasColumnType("text");
            author.Property(a => a.Email).IsRequired().HasColumnType("text");
            author.Property(a => a.Profile).HasColumnType("jsonb");
            author.HasIndex(a => a.Email).IsUnique().HasDatabaseName(prefix + "_authors_email");
        });
        modelBuilder.Entity<Post>(post =>
        {
            post.ToTable(prefix + "_posts");
            post.HasKey(p => p.Id);
            post.Property(p => p.Id).HasMaxLength(26).HasColumnType("character varying(26)");
            post.Property(p => p.AuthorId).IsRequired().HasMaxLength(26).HasColumnType("character varying(26)");
            post.Property(p => p.Title).IsRequired().HasColumnType("text");
            post.Property(p => p.Published).HasColumnType("boolean");
            post.Property(p => p.CreatedAt).HasColumnType("timestamp with time zone");
            post.HasIndex(p => new { p.AuthorId, p.CreatedAt }).HasDatabaseName(prefix + "_posts_author_created");
            post.HasOne(p => p.Author).WithMany(a => a.Posts).HasForeignKey(p => p.AuthorId)
                .HasConstraintName(prefix + "_posts_author").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Build(modelBuilder, Prefix);
    }
}

internal sealed class PrefixModelCacheKey : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return (context.GetType(), ((MatrixContext)context).Prefix, designTime);
    }
}

internal sealed class StagedMigrations(ICurrentDbContext current) : IMigrationsAssembly
{
    private static readonly Dictionary<string, TypeInfo> Stages = new()
    {
        [TablesMigration.Id] = typeof(TablesMigration).GetTypeInfo(),
        [ForeignKeyMigration.Id] = typeof(ForeignKeyMigration).GetTypeInfo(),
        [IndexesMigration.Id] = typeof(IndexesMigration).GetTypeInfo(),
    };

    private readonly string _prefix = ((MatrixContext)current.Context).Prefix;

    public IReadOnlyDictionary<string, TypeInfo> Migrations => Stages;
    public ModelSnapshot ModelSnapshot => new Snapshot(_prefix);
    public Assembly Assembly => typeof(StagedMigrations).Assembly;

    public string? FindMigrationId(string nameOrId)
    {
        return Stages.Keys.FirstOrDefault(id => id == nameOrId || id.EndsWith("_" + nameOrId, StringComparison.Ordinal));
    }

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        var migration = (Migration)Activator.CreateInstance(migrationClass, _prefix)!;
        migration.ActiveProvider = activeProvider;
        return migration;
    }
}

internal sealed class Snapshot(string prefix) : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 63);
        modelBuilder.UseIdentityByDefaultColumns();
        MatrixContext.Build(modelBuilder, prefix);
    }
}

[Migration(Id)]
internal sealed class TablesMigration(string prefix) : Migration
{
    internal const string Id = "20261029000001_Tables";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: prefix + "_authors",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
                Profile = table.Column<string>(type: "jsonb", nullable: true),
            },
            constraints: table => table.PrimaryKey($"PK_{prefix}_authors", x => x.Id));
    }
}

[Migration(Id)]
internal sealed class ForeignKeyMigration(string prefix) : Migration
{
    internal const string Id = "20261029000002_ForeignKey";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: prefix + "_posts",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                AuthorId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                Published = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey($"PK_{prefix}_posts", x => x.Id);
                table.ForeignKey(prefix + "_posts_author", x => x.AuthorId, prefix + "_authors", "Id", onDelete: ReferentialAction.Cascade);
            });
    }
}

[Migration(Id)]
internal sealed class IndexesMigration(string prefix) : Migration
{
    internal const string Id = "20261029000003_Indexes";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(name: prefix + "_authors_email", table: prefix + "_authors", column: "Email", unique: true);
        migrationBuilder.CreateIndex(name: prefix + "_posts_author_created", table: prefix + "_posts", columns: ["AuthorId", "CreatedAt"]);
    }
}

internal sealed record Result(string Surface, string Tool, int Repeat, string Check, string Status, string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Result))]
internal sealed partial class ResultJson : JsonSerializerContext;
