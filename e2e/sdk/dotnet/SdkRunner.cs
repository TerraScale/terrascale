using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

[assembly: SupportedOSPlatform("linux")]

namespace TerraScale.E2E;

public sealed record Item(string Id, long N);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Item))]
internal sealed partial class RunnerJson : JsonSerializerContext;

public sealed class SdkRunner(TerraBaseSqlClient client, string project, string run, string table, TerraBaseSqlDatabase? database)
{
    private TerraBaseSqlSession? session;
    private TerraBaseManagedDatabase? agent;

    private TerraBaseSqlDatabase Db => database ?? throw new InvalidOperationException("Groups a and c have a database.");

    private TerraBaseSqlSession Session => session ?? throw new InvalidOperationException("The session is not connected.");

    private TerraBaseManagedDatabase Agent => agent ?? throw new InvalidOperationException("No agent database was created.");

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var spec = JsonDocument.Parse(await File.ReadAllBytesAsync(Required("E2E_OPERATIONS"), cancellationToken));
        var run = Required("E2E_RUN_ID");
        var group = Required("E2E_GROUP");
        var table = Text(spec.RootElement, "table").Replace("{sdk}", "dotnet", StringComparison.Ordinal).Replace("{run}", run, StringComparison.Ordinal);
        var project = Required("TERRASCALE_PROJECT_ID");
        var client = TerraBaseSqlClient.FromEnvironment();
        var runner = new SdkRunner(client, project, run, table, group is "a" or "c" ? client.Database(project, Required("TERRASCALE_DATABASE_NAME")) : null);

        var failed = false;
        foreach (var op in spec.RootElement.GetProperty("operations").EnumerateArray())
        {
            if (Text(op, "group") != group || op.TryGetProperty("needs", out _) && !(Environment.GetEnvironmentVariable("E2E_REGIONS") == "2" && Text(op, "id").StartsWith("placement.", StringComparison.Ordinal)))
            {
                continue;
            }

            var started = TimeProvider.System.GetTimestamp();
            string? error = null;
            try
            {
                await runner.RunAsync(op, cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                failed = true;
                error = $"{failure.GetType().Name}: {failure.Message}";
            }

            Report(Text(op, "id"), error, TimeProvider.System.GetElapsedTime(started));
        }

        return failed ? 1 : 0;
    }

    private Task RunAsync(JsonElement op, CancellationToken cancellationToken) => Text(op, "id") switch
    {
        "auth.manage_user" => ManageUserAsync(cancellationToken),
        "auth.user_profiles" => UserProfilesAsync(op, cancellationToken),
        "auth.organizations" => OrganizationsAsync(cancellationToken),
        "sql.execute.positional" => ExecutePositionalAsync(op, cancellationToken),
        "sql.execute.named" => ExecuteNamedAsync(op, cancellationToken),
        "sql.execute.statement" => ExecuteStatementAsync(op, cancellationToken),
        "sql.query" => QueryAsync(op, cancellationToken),
        "sql.query.typed" => QueryTypedAsync(op, cancellationToken),
        "sql.batch" => BatchAsync(op, cancellationToken),
        "sql.refused" => RefusedStatementAsync(op, cancellationToken),
        "values.roundtrip" => ValuesAsync(op, cancellationToken),
        "ws.connect" => ConnectAsync(cancellationToken),
        "ws.statements" => SessionStatementsAsync(op, cancellationToken),
        "ws.begin_commit" => SessionEndAsync(op, commit: true, cancellationToken),
        "ws.rollback" => SessionEndAsync(op, commit: false, cancellationToken),
        "ws.transaction.commit" => TransactionCommitAsync(op, cancellationToken),
        "ws.transaction.rethrow" => TransactionRethrowAsync(op, cancellationToken),
        "ws.in_transaction" => InTransactionAsync(cancellationToken),
        "ws.close" => CloseAsync(op, cancellationToken),
        "conflicts.query" => ConflictsAsync(op, cancellationToken),
        "ids.new_validate" => IdsAsync(op),
        "origin.rule" => OriginRuleAsync(op, cancellationToken),
        "agent.create_ttl" => CreateWithTtlAsync(op, cancellationToken),
        "agent.set_ttl" => SetTtlAsync(op, cancellationToken),
        "agent.clear_ttl" => ClearTtlAsync(cancellationToken),
        "agent.scoped_key" => ScopedKeyAsync(op, cancellationToken),
        "agent.delete" => DeleteAsync(op, cancellationToken),
        "placement.create" => PlacementCreateAsync(cancellationToken),
        "placement.set" => PlacementSetAsync(cancellationToken),
        "agent.fork" => ForkAsync(op, cancellationToken),
        "agent.scoped_key_outside" => ScopedKeyOutsideAsync(op, cancellationToken),
        var other => throw new InvalidOperationException($"No implementation for {other}."),
    };

    private async Task ManageUserAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.terrascale.tech"),
            InstanceSlug = "terrascale",
        });
        var read = Required("E2E_AUTH_READ_KEY");
        var write = Required("E2E_AUTH_WRITE_KEY");
        var user = Required("E2E_AUTH_USER");
        var listed = await auth.Projects.ListUsersAsync(read, project, cancellationToken: cancellationToken);
        Same(listed.Items.Any(item => item.Id == user) ? "found" : "missing", "found", "read key lists user");
        var refusal = await RefusedAsync<TerraScaleAuthException>(() => auth.Projects.DisableUserAsync(read, project, user, cancellationToken));
        Same(((int?)refusal.StatusCode)?.ToString(CultureInfo.InvariantCulture) ?? "missing", "403", "read key cannot disable");
        Same((await auth.Projects.DisableUserAsync(write, project, user, cancellationToken)).Disabled ? "disabled" : "enabled", "disabled", "write key disables");
        Same((await auth.Projects.EnableUserAsync(write, project, user, cancellationToken)).Disabled ? "disabled" : "enabled", "enabled", "write key enables");
        var key = await auth.Projects.CreateUserApiKeyAsync(write, project, user, new("journey", ["invoices:read"]), cancellationToken);
        var secret = key.Key ?? throw new InvalidOperationException("The key secret is missing.");
        RegisterSecret("user-api-key-dotnet", secret);
        var keys = await auth.Projects.ListUserApiKeysAsync(read, project, user, cancellationToken);
        Same(keys.Items.Any(item => item.Id == key.Id) ? "found" : "missing", "found", "list user key");
        var verified = await auth.Projects.VerifyUserApiKeyAsync(read, project, new(secret), cancellationToken);
        Same(verified.Valid && verified.UserId == user && verified.Scopes is ["invoices:read"] ? "valid" : "invalid", "valid", "verify user key");
        await auth.Projects.RevokeUserApiKeyAsync(write, project, user, key.Id, cancellationToken);
        Same((await auth.Projects.VerifyUserApiKeyAsync(read, project, new(secret), cancellationToken)).Valid ? "valid" : "invalid", "invalid", "revoked user key");

    }

    private async Task UserProfilesAsync(JsonElement op, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "terrascale" }).Projects;
        var write = Required("E2E_AUTH_WRITE_KEY");
        var created = await auth.CreateUserAsync(write, project, new($"profile-dotnet-{run}@example.com", FirstName: "SDK",
            PublicMetadata: JsonElement.Parse("""{"keep":1,"remove":true}""")), cancellationToken);
        Same(created.FirstName ?? throw new InvalidOperationException("Missing first name"), "SDK", "created profile");
        var changed = await auth.UpdateUserAsync(write, project, created.Id, new(PublicMetadata: JsonElement.Parse("""{"remove":null,"added":"yes"}"""),
            PrivateMetadata: JsonElement.Parse("""{"backend":true}""")), cancellationToken);
        Same(changed.PublicMetadata.GetProperty("keep").GetRawText(), "1", "metadata preserved");
        Same(changed.PublicMetadata.TryGetProperty("remove", out _) ? "present" : "absent", "absent", "metadata removed");
        Same(changed.PublicMetadata.GetProperty("added").GetString() ?? throw new InvalidOperationException("Missing metadata"), "yes", "metadata added");
        var imported = await auth.ImportUsersAsync(write, project, new([
            new($"import-dotnet-{run}@example.com", true, Text(op, "password_hash")),
            new($"invalid-dotnet-{run}@example.com", false, "malformed")
        ]), cancellationToken);
        Same(imported.Items.Count.ToString(CultureInfo.InvariantCulture), "1", "import valid row");
        Same(imported.Errors.Single().Index.ToString(CultureInfo.InvariantCulture), "1", "refuse malformed row");
        await auth.DeleteUserAsync(write, project, imported.Items[0].User.Id, cancellationToken);
        await auth.DeleteUserAsync(write, project, created.Id, cancellationToken);
    }

    private async Task OrganizationsAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "terrascale" }).Projects;
        var read = Required("E2E_AUTH_READ_KEY");
        var write = Required("E2E_AUTH_WRITE_KEY");
        var user = Required("E2E_AUTH_USER");
        var created = await auth.CreateOrganizationAsync(write, project, new("SDK organization", table.Replace('_', '-'), user, null, null, null, null, "off"), cancellationToken);
        try
        {
            Same((await auth.GetOrganizationAsync(read, project, created.Id, cancellationToken)).Id, created.Id, "organization read");
            Same((await auth.ListOrganizationsAsync(read, project, cancellationToken: cancellationToken)).Items.Any(item => item.Id == created.Id) ? "true" : "false", "true", "organization listed");
            Same((await auth.ListOrganizationMembersAsync(read, project, created.Id, cancellationToken: cancellationToken)).Items.Any(item => item.UserId == user && item.Role == "admin") ? "true" : "false", "true", "initial admin");
            await auth.CreateOrganizationInvitationAsync(write, project, created.Id, new("org-" + run + "@example.com", "member"), cancellationToken);
            Same((await auth.ListOrganizationInvitationsAsync(read, project, created.Id, cancellationToken: cancellationToken)).Items.Any(item => item.Status == "pending") ? "true" : "false", "true", "invitation listed");
        }
        finally
        {
            await auth.DeleteOrganizationAsync(write, project, created.Id, cancellationToken);
        }
    }

    private async Task ExecutePositionalAsync(JsonElement op, CancellationToken cancellationToken)
    {
        await Db.ExecuteAsync(Fill(op, "setup"), cancellationToken: cancellationToken);
        var result = await Db.ExecuteAsync(Fill(op, "sql"), Args(op, "args"), cancellationToken);
        Same(result.RowsAffected.ToString(CultureInfo.InvariantCulture), Canonical(op.GetProperty("rowsAffected")), "rows affected");
    }

    private async Task ExecuteNamedAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var named = op.GetProperty("named").EnumerateObject().ToDictionary(property => property.Name, property => Arg(property.Value), StringComparer.Ordinal);
        var result = await Db.ExecuteAsync(Fill(op, "sql"), named, cancellationToken);
        Same(result.RowsAffected.ToString(CultureInfo.InvariantCulture), Canonical(op.GetProperty("rowsAffected")), "rows affected");
    }

    private async Task ExecuteStatementAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var result = await Db.ExecuteAsync(new TerraBaseSqlStatement(Fill(op, "sql"), Args(op, "args")), cancellationToken);
        Same(result.RowsAffected.ToString(CultureInfo.InvariantCulture), Canonical(op.GetProperty("rowsAffected")), "rows affected");
        await CheckAsync(op, cancellationToken);
    }

    private async Task QueryAsync(JsonElement op, CancellationToken cancellationToken) =>
        Same(Rows(await Db.QueryAsync(Fill(op, "sql"), cancellationToken: cancellationToken)), Canonical(op.GetProperty("rows")), "rows");

    private async Task QueryTypedAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var typed = await Db.QueryAsync(Fill(op, "sql"), Args(op, "args"), RunnerJson.Default.Item, cancellationToken);
        var expected = op.GetProperty("objects").EnumerateArray().Select(item => $"{Text(item, "id")}:{item.GetProperty("n").GetInt64()}");
        Same(string.Join(',', typed.Select(item => $"{item.Id}:{item.N}")), string.Join(',', expected), "typed rows");
    }

    private async Task BatchAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var results = await Db.BatchAsync(Statements(op, "statements"), cancellationToken);
        Same(results.Count.ToString(CultureInfo.InvariantCulture), op.GetProperty("statements").GetArrayLength().ToString(CultureInfo.InvariantCulture), "results");
        Same(Rows(results[^1].Rows), Canonical(op.GetProperty("lastRows")), "last rows");
        var refusal = await RefusedAsync<TerraBaseSqlException>(() => Db.BatchAsync(Statements(op, "failing"), cancellationToken));
        Same($"{refusal.Code}:{refusal.Statement}", $"{Text(op, "code")}:{op.GetProperty("statement").GetInt32()}", "batch refusal");
        await CheckAsync(op, cancellationToken);
    }

    private async Task RefusedStatementAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var refusal = await RefusedAsync<TerraBaseSqlException>(() => Db.ExecuteAsync(Fill(op, "sql"), cancellationToken: cancellationToken));
        Same(
            $"{refusal.Code}:{refusal.Statement}:{refusal.Sqlstate}:{(int?)refusal.StatusCode}",
            $"{Text(op, "code")}:{op.GetProperty("statement").GetInt32()}:{Text(op, "sqlstate")}:{op.GetProperty("status").GetInt32()}",
            "SqlError");
    }

    private async Task ValuesAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var values = op.GetProperty("values");
        await Db.ExecuteAsync(Fill(op, "setup"), cancellationToken: cancellationToken);
        TerraBaseSqlValue[] args =
        [
            Text(values, "k"),
            values.GetProperty("n").GetInt64(),
            long.Parse(Text(values, "big"), CultureInfo.InvariantCulture),
            values.GetProperty("r").GetDouble(),
            values.GetProperty("flag").GetBoolean(),
            Text(values, "t"),
            TerraBaseSqlValue.FromBytes(Convert.FromBase64String(Text(values, "b"))),
            (string?)null,
        ];
        await Db.ExecuteAsync(Fill(op, "sql"), args, cancellationToken);
        var row = (await Db.QueryAsync(Fill(op, "select"), cancellationToken: cancellationToken))[0];
        var actual = string.Join('|',
            row.GetString("k"),
            row.GetInt64("n").ToString(CultureInfo.InvariantCulture),
            row.GetInt64("big").ToString(CultureInfo.InvariantCulture),
            row.GetDouble("r").ToString(CultureInfo.InvariantCulture),
            row.GetInt64("flag").ToString(CultureInfo.InvariantCulture),
            row.GetString("t"),
            Convert.ToBase64String(row.GetBytes("b")),
            row.IsNull("absent") ? "null" : "value");
        var expected = string.Join('|',
            Text(values, "k"),
            values.GetProperty("n").GetInt64().ToString(CultureInfo.InvariantCulture),
            Text(values, "big"),
            values.GetProperty("r").GetDouble().ToString(CultureInfo.InvariantCulture),
            op.GetProperty("storedFlag").GetInt64().ToString(CultureInfo.InvariantCulture),
            Text(values, "t"),
            Text(values, "b"),
            "null");
        Same(actual, expected, "values");
    }

    private async Task ConnectAsync(CancellationToken cancellationToken) => session = await Db.ConnectAsync(cancellationToken);

    private async Task SessionStatementsAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var result = await Session.ExecuteAsync(Fill(op, "sql"), cancellationToken: cancellationToken);
        Same(result.RowsAffected.ToString(CultureInfo.InvariantCulture), "1", "rows affected");
        Same(Rows(await Session.QueryAsync(Fill(op, "query"), cancellationToken: cancellationToken)), Canonical(op.GetProperty("rows")), "rows");
        var results = await Session.BatchAsync(Statements(op, "statements"), cancellationToken);
        Same(Rows(results[^1].Rows), Canonical(op.GetProperty("lastRows")), "last rows");
    }

    private async Task SessionEndAsync(JsonElement op, bool commit, CancellationToken cancellationToken)
    {
        await Session.BeginAsync(cancellationToken);
        await Session.ExecuteAsync(Fill(op, "sql"), cancellationToken: cancellationToken);
        await (commit ? Session.CommitAsync(cancellationToken) : Session.RollbackAsync(cancellationToken));
        await CheckAsync(op, cancellationToken);
    }

    private async Task TransactionCommitAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var sql = Fill(op, "sql");
        await Session.TransactionAsync(async (inside, token) => await inside.ExecuteAsync(sql, cancellationToken: token), cancellationToken);
        await CheckAsync(op, cancellationToken);
    }

    private async Task TransactionRethrowAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var sql = Fill(op, "sql");
        var thrown = new InvalidOperationException(Text(op, "message"));
        var refusal = await RefusedAsync<InvalidOperationException>(() => Session.TransactionAsync(async (inside, token) =>
        {
            await inside.ExecuteAsync(sql, cancellationToken: token);
            throw thrown;
        }, cancellationToken));
        Same(ReferenceEquals(refusal, thrown) ? "rethrown" : refusal.ToString(), "rethrown", "the transaction's exception");
        await CheckAsync(op, cancellationToken);
    }

    private async Task InTransactionAsync(CancellationToken cancellationToken)
    {
        await Session.BeginAsync(cancellationToken);
        var inside = Session.InTransaction;
        await Session.CommitAsync(cancellationToken);
        Same($"{inside}:{Session.InTransaction}", "True:False", "inTransaction");
    }

    private async Task CloseAsync(JsonElement op, CancellationToken cancellationToken)
    {
        await Session.DisposeAsync();
        await RefusedAsync<ObjectDisposedException>(() => Session.ExecuteAsync(Text(op, "sql"), cancellationToken: cancellationToken));
    }

    private async Task ConflictsAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var now = TimeProvider.System.GetUtcNow();
        TerraBaseSqlValue[] window = [table, now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture), now.AddHours(1).ToString("O", CultureInfo.InvariantCulture)];
        Same(Rows(await Db.QueryAsync(Text(op, "sql"), window, cancellationToken)), Canonical(op.GetProperty("rows")), "conflict rows");
    }

    private static Task IdsAsync(JsonElement op)
    {
        var id = new TerraScaleIdGenerator().NewId();
        Same($"{TerraScaleIds.IsValid(id)}:{TerraScaleIds.IsValid(Text(op, "invalid"))}", "True:False", "validation");
        return Task.CompletedTask;
    }

    private static async Task OriginRuleAsync(JsonElement op, CancellationToken cancellationToken)
    {
        using var file = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(Path.GetDirectoryName(Required("E2E_OPERATIONS")), Text(op, "cases")), cancellationToken));
        var cases = file.RootElement.GetProperty("cases");
        List<string> wrong = [];
        var index = 0;
        foreach (var item in cases.EnumerateArray())
        {
            var raw = item[0].GetString() ?? throw new InvalidOperationException("A case has no base URL.");
            var address = Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ? parsed : null;
            Compare(wrong, index, raw, "api", item[1].GetString(), address is null ? null : await ApiOriginAsync(address, cancellationToken));
            Compare(wrong, index, raw, "auth", item[2].GetString(), address is null ? null : AuthOrigin(address));
            index++;
        }

        if (wrong.Count > 0)
        {
            throw new InvalidOperationException($"origin rule: {wrong.Count} of {2 * cases.GetArrayLength()} checks differ: {string.Join("; ", wrong)}");
        }
    }

    private static void Compare(List<string> wrong, int index, string raw, string column, string? expected, Uri? got)
    {
        var same = (got, expected) switch
        {
            (null, null) => true,
            ({ } origin, { } want) => Uri.Compare(origin, new Uri(want), UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.Ordinal) == 0,
            _ => false,
        };
        if (!same)
        {
            wrong.Add($"#{index} \"{raw[..Math.Min(raw.Length, 60)]}\" {column}: expected {expected ?? "refused"}, got {got?.GetLeftPart(UriPartial.Authority) ?? "refused"}");
        }
    }

    private static async Task<Uri?> ApiOriginAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            return await new TerraBaseStaticEndpointResolver(address).ResolveAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Uri? AuthOrigin(Uri address)
    {
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        try
        {
            _ = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = address, InstanceSlug = "terrascale" });
        }
        catch (ArgumentException)
        {
            return null;
        }

        return http.BaseAddress;
    }

    private async Task CreateWithTtlAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var ttl = op.GetProperty("ttlSeconds").GetInt32();
        var before = TimeProvider.System.GetUtcNow();
        var created = await client.CreateDatabaseAsync(project, Fill(op, "name"), ttl, cancellationToken: cancellationToken);
        var after = TimeProvider.System.GetUtcNow();
        Same(created.Name, Fill(op, "name"), "name");
        var expiry = created.ExpiresAt ?? throw new InvalidOperationException("the database has no expiry");
        var earliest = before.AddSeconds(ttl).ToUnixTimeSeconds();
        var latest = after.AddSeconds(ttl + 5).ToUnixTimeSeconds();
        var at = expiry.ToUnixTimeSeconds();
        Same(at >= earliest && at <= latest ? "within ttl" : $"{at} outside {earliest}..{latest}", "within ttl", "expiry the server reports");
        agent = created;
    }

    private async Task SetTtlAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var updated = await client.SetDatabaseTtlAsync(project, Agent.Id, op.GetProperty("ttlSeconds").GetInt32(), cancellationToken);
        Same(updated.ExpiresAt > Agent.ExpiresAt ? "later" : "not later", "later", "expiry");
    }

    private async Task ClearTtlAsync(CancellationToken cancellationToken)
    {
        var cleared = await client.ClearDatabaseTtlAsync(project, Agent.Id, cancellationToken);
        Same(cleared.ExpiresAt is null ? "none" : "expires", "none", "expiry");
    }

    private async Task ScopedKeyAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var scoped = await client.CreateScopedKeyAsync(Agent.Id, Text(op, "access"), project, op.GetProperty("ttlSeconds").GetInt32(), cancellationToken);
        RegisterSecret("scoped-key-dotnet", scoped.Key);
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var narrow = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = scoped.Key });
        await narrow.Database(project, Agent.Id).ExecuteAsync(Text(op, "sql"), cancellationToken: cancellationToken);
        var mint = await StatusAsync(() => narrow.CreateScopedKeyAsync(Agent.Id, "read", project, cancellationToken: cancellationToken));
        Same(mint, Canonical(op.GetProperty("mintStatus")), "minting with a scoped key");
    }

    private static void RegisterSecret(string name, string value)
    {
        var folder = Environment.GetEnvironmentVariable("E2E_SECRETS") ?? throw new InvalidOperationException("E2E_SECRETS names the folder of generated secrets.");
        using var file = new FileStream(Path.Combine(folder, name), new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        file.Write(Encoding.UTF8.GetBytes(value));
    }

    private async Task DeleteAsync(JsonElement op, CancellationToken cancellationToken)
    {
        await client.DeleteDatabaseAsync(project, Agent.Id, cancellationToken);
        var deleted = await StatusAsync(() => client.Database(project, Agent.Id).ExecuteAsync(Text(op, "sql"), cancellationToken: cancellationToken));
        Same(deleted, Canonical(op.GetProperty("deletedStatus")), "the deleted database");
    }

    private async Task ForkAsync(JsonElement op, CancellationToken cancellationToken)
    {
        await Db.ExecuteAsync(Fill(op, "setup"), cancellationToken: cancellationToken);
        await Db.ExecuteAsync(Fill(op, "sql"), cancellationToken: cancellationToken);
        var fork = await client.ForkDatabaseAsync(project, Required("TERRASCALE_DATABASE_NAME"), Fill(op, "name"), cancellationToken: cancellationToken);
        Same($"{fork.Name} from {fork.BranchedFrom}", $"{Fill(op, "name")} from {Required("E2E_DATABASE_ID")}", "fork");
        Same(Rows(await client.Database(project, fork.Id).QueryAsync(Fill(op, "check"), cancellationToken: cancellationToken)), Canonical(op.GetProperty("checkRows")), "rows of the fork");
        await client.DeleteDatabaseAsync(project, fork.Id, cancellationToken);
    }

    private async Task ScopedKeyOutsideAsync(JsonElement op, CancellationToken cancellationToken)
    {
        var source = Required("E2E_DATABASE_ID");
        var scoped = await client.CreateScopedKeyAsync(source, Text(op, "access"), project, op.GetProperty("ttlSeconds").GetInt32(), cancellationToken);
        RegisterSecret("scoped-key-dotnet-outside", scoped.Key);
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var narrow = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = scoped.Key });
        await narrow.Database(project, source).QueryAsync(Text(op, "sql"), cancellationToken: cancellationToken);
        var refusal = await RefusedAsync<TerraBaseException>(() => narrow.Database(project, Required("E2E_OTHER_DATABASE")).QueryAsync(Text(op, "sql"), cancellationToken: cancellationToken));
        Same($"{((int?)refusal.StatusCode)?.ToString(CultureInfo.InvariantCulture)} {refusal.ErrorCode}", $"{Canonical(op.GetProperty("status"))} {Text(op, "code")}", "the other database");
    }

    private async Task CheckAsync(JsonElement op, CancellationToken cancellationToken) =>
        Same(Rows(await Db.QueryAsync(Fill(op, "check"), cancellationToken: cancellationToken)), Canonical(op.GetProperty("checkRows")), "rows after the call");

    private string Fill(JsonElement op, string key) => Fill(Text(op, key));

    private string Fill(string text) =>
        text.Replace("{table}", table, StringComparison.Ordinal).Replace("{sdk}", "dotnet", StringComparison.Ordinal).Replace("{run}", run, StringComparison.Ordinal);

    private TerraBaseSqlStatement[] Statements(JsonElement op, string key) =>
        [.. op.GetProperty(key).EnumerateArray().Select(sql => new TerraBaseSqlStatement(Fill(sql.GetString() ?? throw new InvalidOperationException($"{key} holds a non-string."))))];


    private async Task PlacementCreateAsync(CancellationToken cancellationToken)
    {
        var created = await client.CreateDatabaseAsync(project, $"e2e-dotnet-{run}-placed", placement: TerraBaseDatabasePlacement.In("gru", "fra"), cancellationToken: cancellationToken);
        try
        {
            Same(string.Join(",", created.Regions), "fra,gru", "created regions");
            var repeated = await client.SetDatabasePlacementAsync(project, created.Id, placement: TerraBaseDatabasePlacement.In("fra", "gru"), cancellationToken: cancellationToken);
            if (repeated.PlacementVersion != created.PlacementVersion) throw new InvalidOperationException("An unchanged placement changed its version.");
        }
        finally
        {
            await client.DeleteDatabaseAsync(project, created.Id, cancellationToken);
        }
    }

    private async Task PlacementSetAsync(CancellationToken cancellationToken)
    {
        var created = await client.CreateDatabaseAsync(project, $"e2e-dotnet-{run}-move", placement: TerraBaseDatabasePlacement.In("gru"), cancellationToken: cancellationToken);
        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var regional = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
        {
            ApiKey = Required("TERRASCALE_API_KEY"),
            BaseAddress = new Uri("https://fra.api.terrascale.tech"),
            AuthAddress = new Uri("https://auth.terrascale.tech"),
        });
        var placed = client;
        try
        {
            await client.Database(project, created.Id).ExecuteAsync("CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)", cancellationToken: cancellationToken);
            await client.Database(project, created.Id).ExecuteAsync("INSERT INTO placement_rows VALUES (1)", cancellationToken: cancellationToken);
            var added = await client.SetDatabasePlacementAsync(project, created.Id, placement: TerraBaseDatabasePlacement.In("gru", "fra"), cancellationToken: cancellationToken);
            Same(string.Join(",", added.Regions), "fra,gru", "added region");
            if (added.PlacementVersion <= created.PlacementVersion) throw new InvalidOperationException("Membership did not raise placement version.");
            var progress = await client.Database(project, created.Id).GetRegionsAsync(cancellationToken);
            Same(string.Join(",", progress.Regions.Select(row => row.Region)), "fra,gru", "status regions");
            Same(progress.Regions.Single(row => row.Region == "gru").Role ?? "", "writer", "writer role");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            TerraBaseManagedDatabase updated;
            for (; ; )
            {
                try
                {
                    updated = await client.SetDatabasePlacementAsync(project, created.Id, placement: TerraBaseDatabasePlacement.In("fra"), cancellationToken: deadline.Token);
                    break;
                }
                catch (TerraBaseUnavailableException error) when (error.ErrorCode == "replica_not_ready")
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                }
            }
            placed = regional;
            Same(string.Join(",", updated.Regions), "fra", "dropped writer");
            if (updated.PlacementVersion <= added.PlacementVersion) throw new InvalidOperationException("Writer did not raise placement version.");
            var after = await regional.Database(project, created.Id).GetRegionsAsync(cancellationToken);
            Same(string.Join(",", after.Regions.Select(row => $"{row.Region}:{row.Role}")), "fra:writer", "moved status");
            var refused = await RefusedAsync<TerraBaseWrongRegionException>(() => client.Database(project, created.Id).GetRegionsAsync(cancellationToken));
            Same(refused.ErrorCode ?? "", "wrong_region", "nonmember endpoint");
            var rows = await regional.Database(project, created.Id).QueryAsync("SELECT value FROM placement_rows", cancellationToken: cancellationToken);
            if (rows.Count != 1 || rows[0].GetInt64(0) != 1) throw new InvalidOperationException("Data did not survive placement.");
            Console.WriteLine("dotnet placement: create [gru,fra], add fra, repeat drop gru -> 200, status fra writer, 421 wrong_region");
        }
        finally
        {
            await placed.DeleteDatabaseAsync(project, created.Id, cancellationToken);
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is not set.");

    private static string Text(JsonElement element, string key) =>
        element.GetProperty(key).GetString() ?? throw new InvalidOperationException($"{key} is not a string.");

    private static TerraBaseSqlValue Arg(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => (string?)null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array => throw new InvalidOperationException($"No SQL value for {value.ValueKind}."),
        _ => throw new UnreachableException(),
    };

    private static TerraBaseSqlValue[] Args(JsonElement op, string key) => [.. op.GetProperty(key).EnumerateArray().Select(Arg)];

    private static string Canonical(JsonElement element) => Json(element.WriteTo);

    private static string Rows(IReadOnlyList<TerraBaseSqlRecord> rows) => Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartArray();
            for (var ordinal = 0; ordinal < row.Columns.Count; ordinal++)
            {
                row[ordinal].WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    });

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Same(string actual, string expected, string what)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
        }
    }

    private static async Task<TException> RefusedAsync<TException>(Func<Task> work)
        where TException : Exception
    {
        try
        {
            await work();
        }
        catch (TException refusal)
        {
            return refusal;
        }

        throw new InvalidOperationException("The call was accepted.");
    }

    private static async Task<string> StatusAsync(Func<Task> work) =>
        ((int?)(await RefusedAsync<HttpRequestException>(work)).StatusCode)?.ToString(CultureInfo.InvariantCulture) ?? "no status";

    private static void Report(string id, string? error, TimeSpan elapsed) => Console.WriteLine(Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteBoolean("ok", error is null);
        writer.WriteNumber("ms", (long)elapsed.TotalMilliseconds);
        if (error is not null)
        {
            writer.WriteString("error", error);
        }

        writer.WriteEndObject();
    }));
}
