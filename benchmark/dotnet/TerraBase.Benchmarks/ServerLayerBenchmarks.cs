using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Atlas;
using TerraBase.Features.Backup;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Clock;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Rows;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Postgres.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.ControlPlane;
using TerraScale.Contracts.Http;
using TerraScale.Contracts.Storage;
using TerraScale.Endpoints;
using TerraScale.QuicRpc;

namespace TerraBase.Benchmarks;

public static class ServerLayerBenchmarks
{
    private const string Suite = "server";

    private const string BenchDatabaseName = "benchdb";

    private const string KeyId = "bench-es256-1";

    private static readonly string BenchAudience = new UriBuilder(Uri.UriSchemeHttps, "api.terrascale.tech").Uri.AbsoluteUri.TrimEnd('/');

    private static readonly string[] BenchAudiences = [BenchAudience];

    private static readonly string[] BenchRegions = [TerraBaseOptions.LocalIdentity];

    private static readonly string[] RuleColumns = ["id", "owner_id", "title", "score"];

    private static readonly string[] RuleWritable = ["owner_id", "title", "score"];

    private static readonly string[] RuleValidate = ["length(new.title) > 0", "new.score >= 0"];

    private static readonly byte[] SampleBlob = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x23, 0x45, 0x67];

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    public static async Task RunAsync(BenchRunnerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.HasMatchingBenchmark(
            Suite,
            "auth_jwt_verify",
            "auth_apikey_find",
            "catalog_resolve_data",
            "sql_plan_and_wire_json",
            "rules_compile_and_enforce",
            "rows_crud_and_batch",
            "http_sql_rest_read",
            "http_sql_rest_write",
            "http_sql_rest_batch",
            "http_sql_rest_concurrent_16",
            "http2_sql_multiplexed_32",
            "ws_sql_v2_tx_loopback",
            "ws_sql_v2_sustained_pipeline_16"))
        {
            return;
        }

        QuicPlatform.EnsureInitialized();

        await using var oidc = await OidcLoopbackHost.StartAsync(cancellationToken);
        await using var host = await TerraBaseLoopbackHost.StartAsync(ctx.RunDir("server"), oidc.Issuer, cancellationToken);

        var now = TimeProvider.System.GetUtcNow();
        var organizationId = Ulid.NewUlid(now);
        var projectId = Ulid.NewUlid(now);
        var databaseId = Ulid.NewUlid(now);
        var ownerUserId = Ulid.NewUlid(now).ToString();
        var endUserId = Ulid.NewUlid(now).ToString();
        var otherUserId = Ulid.NewUlid(now).ToString();

        var seeded = await SeedCatalogAndDataAsync(host, organizationId, projectId, databaseId, ownerUserId, endUserId, otherUserId, now, nameSuffix: null, cancellationToken);

        if (ctx.ShouldRunBenchmark(Suite, "auth_jwt_verify"))
        {
            await RunJwtVerifyAsync(ctx, host, oidc, organizationId, projectId, ownerUserId, now, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "auth_apikey_find"))
        {
            await RunApiKeyFindAsync(ctx, host, seeded.IssuedKey, databaseId, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "catalog_resolve_data"))
        {
            await RunCatalogResolveDataAsync(ctx, host, seeded.ApiKeyCaller, projectId, databaseId, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "sql_plan_and_wire_json"))
        {
            RunSqlPlanAndWireJson(ctx);
        }

        if (ctx.ShouldRunBenchmark(Suite, "rules_compile_and_enforce"))
        {
            await RunRulesCompileAndEnforceAsync(ctx, host, databaseId, endUserId, seeded.TenantSession, seeded.DeployedRules.Version, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "rows_crud_and_batch"))
        {
            await RunRowsCrudAndBatchAsync(ctx, host, projectId, endUserId, cancellationToken);
        }

        if (ctx.HasMatchingBenchmark(Suite, "http_sql_rest_read", "http_sql_rest_write", "http_sql_rest_batch", "http_sql_rest_concurrent_16"))
        {
            await RunHttpSqlRestLoopbackAsync(ctx, host, projectId, seeded.IssuedKey.Secret, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "http2_sql_multiplexed_32"))
        {
            await RunHttp2SqlMultiplexed32Async(ctx, host, ownerUserId, endUserId, otherUserId, now, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "ws_sql_v2_tx_loopback"))
        {
            await RunWsSqlV2TxLoopbackAsync(ctx, host, projectId, seeded.IssuedKey.Secret, cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "ws_sql_v2_sustained_pipeline_16"))
        {
            await RunWsSqlV2SustainedPipeline16Async(ctx, host, projectId, seeded.IssuedKey.Secret, cancellationToken);
        }
    }

    private static async Task<SeededServerState> SeedCatalogAndDataAsync(
        TerraBaseLoopbackHost host,
        Ulid organizationId,
        Ulid projectId,
        Ulid databaseId,
        string ownerUserId,
        string endUserId,
        string otherUserId,
        DateTimeOffset now,
        string? nameSuffix,
        CancellationToken cancellationToken)
    {
        var catalog = host.Services.GetRequiredService<CatalogStore>();
        var apiKeys = host.Services.GetRequiredService<ApiKeyStore>();
        var tenants = host.Services.GetRequiredService<TenantDatabases>();
        var access = host.Services.GetRequiredService<DatabaseAccess>();
        var coordinator = host.Services.GetRequiredService<IClusterCoordinator>();
        var rulesStore = host.Services.GetRequiredService<RulesStore>();

        var orgName = nameSuffix is null ? "bench-org" : string.Concat("bench-org-", nameSuffix);
        var projectName = nameSuffix is null ? "bench-project" : string.Concat("bench-project-", nameSuffix);
        var keyName = nameSuffix is null ? "bench-key" : string.Concat("bench-key-", nameSuffix);

        var orgCreated = await catalog.CreateOrganizationAsync(
            new OrganizationRecord(organizationId, orgName, ownerUserId, now, "enterprise", Suspended: false),
            cancellationToken);
        var (organization, orgError) = orgCreated switch
        {
            OrganizationRecord record => (record, (string?)null),
            StateConflict conflict => ((OrganizationRecord?)null, conflict.Message),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (organization is null)
        {
            throw new InvalidOperationException($"Failed to seed organization: {orgError}");
        }

        var projectCreated = await catalog.CreateProjectAsync(
            new ProjectRecord(projectId, organizationId, projectName, now, "enterprise"),
            cancellationToken);
        var (project, projectError) = projectCreated switch
        {
            ProjectRecord record => (record, (string?)null),
            StateConflict conflict => ((ProjectRecord?)null, conflict.Message),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (project is null)
        {
            throw new InvalidOperationException($"Failed to seed project: {projectError}");
        }

        var dbRecord = new DatabaseRecord(
            databaseId,
            projectId,
            BenchDatabaseName,
            BenchRegions,
            AllRegions: false,
            ConsistencyMode.Fast,
            Strict: false,
            ControlDatabaseState.Active,
            tenants.ActiveKeyId,
            BranchedFrom: null,
            PreviousName: null,
            now,
            ExpiresAt: null,
            Version: 1,
            StorageLimit: 0);
        var dbInserted = await catalog.InsertDatabaseAsync(dbRecord, cancellationToken);
        var (database, dbError) = dbInserted switch
        {
            DatabaseRecord record => (record, (string?)null),
            StateConflict conflict => ((DatabaseRecord?)null, conflict.Message),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (database is null)
        {
            throw new InvalidOperationException($"Failed to seed database record: {dbError}");
        }

        var keyCreated = await apiKeys.CreateAsync(keyName, Scopes.ApiKey, organizationId, projectId, databaseId, expiresAt: null, cancellationToken);
        var (issuedKey, keyError) = keyCreated switch
        {
            IssuedApiKey issued => (issued, (string?)null),
            StateConflict conflict => ((IssuedApiKey?)null, conflict.Message),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (issuedKey is null)
        {
            throw new InvalidOperationException($"Failed to seed API key: {keyError}");
        }

        var opened = await access.OpenAsync(database, cancellationToken);
        var (session, openError) = opened switch
        {
            ISqlDatabaseSession ready => (ready, (string?)null),
            BackendUnavailable unavailable => ((ISqlDatabaseSession?)null, unavailable.Message),
        };
        if (session is null)
        {
            throw new InvalidOperationException($"Failed to open tenant database: {openError}");
        }

        var ddlRequest = new SqlStatementRequest(
            databaseId,
            [SqlBuild.Statement("CREATE TABLE items (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL, score INTEGER NOT NULL)")],
            SqlAccess.Write,
            ConsistencyMode.Strong,
            CausalBookmark.Empty);
        var ddlOutcome = await coordinator.ExecuteWriteAsync(session, ddlRequest, cancellationToken);
        AssertClusterWriteSucceeded(ddlOutcome, "seed DDL");

        List<SqlStatement> inserts = new(200);
        for (var id = 1; id <= 200; id++)
        {
            var owner = id % 2 == 0 ? endUserId : otherUserId;
            inserts.Add(SqlBuild.Statement(
                "INSERT INTO items (id, owner_id, title, score) VALUES (?, ?, ?, ?)",
                SqlBuild.Integer(id),
                SqlBuild.Text(owner),
                SqlBuild.Text(string.Concat("item-", id.ToString(CultureInfo.InvariantCulture))),
                SqlBuild.Integer(id * 10)));
        }

        var seedRequest = new SqlStatementRequest(databaseId, inserts, SqlAccess.Write, ConsistencyMode.Fast, CausalBookmark.Empty);
        var seedOutcome = await coordinator.ExecuteWriteAsync(session, seedRequest, cancellationToken);
        AssertClusterWriteSucceeded(seedOutcome, "seed rows");

        var rulesSet = new RulesSet(new Dictionary<string, RulesTable>(StringComparer.Ordinal)
        {
            ["items"] = new RulesTable(
                Read: "row.owner_id = auth.uid AND row.score >= 0",
                Create: "new.owner_id = auth.uid",
                Update: "row.owner_id = auth.uid",
                Delete: "row.owner_id = auth.uid",
                Columns: RuleColumns,
                Writable: RuleWritable,
                Validate: RuleValidate),
        });
        var deployedOutcome = await rulesStore.DeployAsync(databaseId, rulesSet, ifMatch: null, ownerUserId, cancellationToken);
        var (deployedRules, rulesError) = deployedOutcome switch
        {
            StoredRules stored => (stored, (string?)null),
            StaleRules => ((StoredRules?)null, "stale rules"),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (deployedRules is null)
        {
            throw new InvalidOperationException($"Failed to deploy RLS rules: {rulesError}");
        }

        return new SeededServerState(database, issuedKey, new ApiKeyCaller(issuedKey.Key), session, deployedRules);
    }

    private static async Task RunJwtVerifyAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        OidcLoopbackHost oidc,
        Ulid organizationId,
        Ulid projectId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var verifier = host.Services.GetRequiredService<TokenVerifier>();
        var token = oidc.SignProjectToken(userId, projectId, organizationId, BenchAudience, now);

        await ctx.MeasureAsync(
            Suite,
            "auth_jwt_verify",
            warmup: 20,
            iterations: 500,
            async _ =>
            {
                var outcome = await verifier.VerifyAsync(token, cancellationToken);
                var (caller, error) = outcome switch
                {
                    Caller verified => (verified, (string?)null),
                    CredentialRejected rejected => ((Caller?)null, rejected.Message),
                    BackendUnavailable unavailable => (null, unavailable.Message),
                };
                if (caller is not ProjectCaller projectCaller || projectCaller.ProjectId != projectId || !projectCaller.Scopes.Contains(Scopes.SqlWrite))
                {
                    throw new InvalidOperationException($"JWT verification failed: {error ?? "unexpected caller type"}");
                }
            },
            note: "ES256 JWT verified via local OIDC JWKS HTTP loopback + TokenVerifier.VerifyAsync",
            cancellationToken: cancellationToken);
    }

    private static async Task RunApiKeyFindAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        IssuedApiKey issuedKey,
        Ulid databaseId,
        CancellationToken cancellationToken)
    {
        var store = host.Services.GetRequiredService<ApiKeyStore>();

        await ctx.MeasureAsync(
            Suite,
            "auth_apikey_find",
            warmup: 50,
            iterations: 500,
            async _ =>
            {
                var outcome = await store.FindAsync(issuedKey.Secret, cancellationToken);
                var (record, error) = outcome switch
                {
                    ApiKeyRecord found => (found, (string?)null),
                    ResourceNotFound missing => ((ApiKeyRecord?)null, missing.Message),
                    BackendUnavailable unavailable => (null, unavailable.Message),
                };
                if (record is null || record.Id != issuedKey.Key.Id || record.DatabaseId != databaseId)
                {
                    throw new InvalidOperationException($"ApiKeyStore.FindAsync failed: {error ?? "unexpected key record"}");
                }
            },
            note: "SHA-256 hash + ApiKeyStore.FindAsync against real SystemDatabase",
            cancellationToken: cancellationToken);
    }

    private static async Task RunCatalogResolveDataAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        ApiKeyCaller caller,
        Ulid projectId,
        Ulid databaseId,
        CancellationToken cancellationToken)
    {
        var access = host.Services.GetRequiredService<DatabaseAccess>();
        var catalog = host.Services.GetRequiredService<CatalogStore>();
        var projectRef = projectId.ToString();

        await ctx.MeasureAsync(
            Suite,
            "catalog_resolve_data",
            warmup: 50,
            iterations: 500,
            async _ =>
            {
                var resolution = await access.DataAsync(caller, projectRef, BenchDatabaseName, cancellationToken);
                var (resolved, resolveError) = resolution switch
                {
                    ResolvedDatabase target => (target, (string?)null),
                    InvalidInput invalid => ((ResolvedDatabase?)null, invalid.Message),
                    CredentialRejected rejected => (null, rejected.Message),
                    CallerForbidden forbidden => (null, forbidden.Message),
                    ResourceNotFound missing => (null, missing.Message),
                    BackendUnavailable unavailable => (null, unavailable.Message),
                };
                if (resolved is null || resolved.Database.Id != databaseId || !resolved.CanWrite)
                {
                    throw new InvalidOperationException($"DatabaseAccess.DataAsync failed: {resolveError ?? "wrong resolution"}");
                }

                var lookup = await catalog.GetDatabaseAsync(databaseId, cancellationToken);
                var (record, lookupError) = lookup switch
                {
                    DatabaseRecord found => (found, (string?)null),
                    ResourceNotFound missing => ((DatabaseRecord?)null, missing.Message),
                    BackendUnavailable unavailable => (null, unavailable.Message),
                };
                if (record is null || record.Name != BenchDatabaseName)
                {
                    throw new InvalidOperationException($"CatalogStore.GetDatabaseAsync failed: {lookupError ?? "wrong record"}");
                }

                var opened = await access.OpenAsync(resolved.Database, cancellationToken);
                var (session, openError) = opened switch
                {
                    ISqlDatabaseSession ready => (ready, (string?)null),
                    BackendUnavailable unavailable => ((ISqlDatabaseSession?)null, unavailable.Message),
                };
                if (session is null || session.DatabaseId != databaseId || !session.IsUsable)
                {
                    throw new InvalidOperationException($"DatabaseAccess.OpenAsync failed: {openError ?? "unusable session"}");
                }
            },
            note: "DatabaseAccess.DataAsync + CatalogStore.GetDatabaseAsync + DatabaseAccess.OpenAsync",
            cancellationToken: cancellationToken);
    }

    private static void RunSqlPlanAndWireJson(BenchRunnerContext ctx)
    {
        using var argDoc = JsonDocument.Parse("{\"id\":42,\"minScore\":100,\"delta\":5,\"owner\":\"01J00000000000000000000001\",\"title\":\"alpha\",\"blob\":{\"base64\":\"3q2+7wEjRWc=\"}}");
        var root = argDoc.RootElement;
        var named = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["min_score"] = root.GetProperty("minScore").Clone(),
        };
        SqlHttpStatement[] httpStatements =
        [
            new("SELECT id, owner_id, title, score FROM items WHERE id = ? AND score >= :min_score", [root.GetProperty("id").Clone()], named),
            new("UPDATE items SET score = score + ? WHERE id = ?", [root.GetProperty("delta").Clone(), root.GetProperty("id").Clone()], null),
            new("INSERT INTO items (owner_id, title, score) VALUES (?, ?, ?)", [root.GetProperty("owner").Clone(), root.GetProperty("title").Clone(), root.GetProperty("minScore").Clone()], null),
        ];

        List<IReadOnlyList<SqlValue>> sampleRows = new(32);
        for (var i = 0; i < 32; i++)
        {
            sampleRows.Add(
            [
                SqlValue.FromIntegral(i + 1),
                SqlValue.FromReal((i + 1) * 1.25),
                SqlValue.FromText(string.Concat("row-title-", i.ToString(CultureInfo.InvariantCulture))),
                i % 4 == 0 ? SqlValue.Null : SqlValue.FromBlob(SampleBlob),
            ]);
        }

        var converter = new SqlRowsConverter();
        var buffer = new ArrayBufferWriter<byte>(4096);

        ctx.MeasureSync(
            Suite,
            "sql_plan_and_wire_json",
            warmup: 200,
            iterations: 2000,
            _ =>
            {
                var parsed = SqlWire.Statements(httpStatements);
                var (statements, wireError) = parsed switch
                {
                    IReadOnlyList<SqlStatement> list => (list, (string?)null),
                    SqlRefusal refusal => ((IReadOnlyList<SqlStatement>?)null, refusal.Message),
                };
                if (statements is null || statements.Count != 3)
                {
                    throw new InvalidOperationException($"SqlWire.Statements failed: {wireError ?? "wrong count"}");
                }

                var planned = SqlPlan.Batch(statements, allowTransactionControl: false);
                var (batch, planError) = planned switch
                {
                    SqlBatch ok => (ok, (string?)null),
                    SqlRefusal refusal => ((SqlBatch?)null, refusal.Message),
                };
                if (batch is null || batch.Statements.Count != 3 || !batch.Writes)
                {
                    throw new InvalidOperationException($"SqlPlan.Batch failed: {planError ?? "unexpected batch"}");
                }

                buffer.ResetWrittenCount();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    converter.Write(writer, sampleRows, JsonSerializerOptions.Web);
                }

                var written = buffer.WrittenSpan;
                if (written.Length < 64 || written[0] != (byte)'[' || written[^1] != (byte)']')
                {
                    throw new InvalidOperationException("SqlRowsConverter produced invalid JSON output");
                }
            },
            note: "SqlWire.Statements (3 stmts) + SqlPlan.Batch + SqlRowsConverter (32 rows x 4 mixed cells)");
    }

    private static async Task RunRulesCompileAndEnforceAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        Ulid databaseId,
        string endUserId,
        ISqlDatabaseSession session,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var enforcer = host.Services.GetRequiredService<RulesEnforcer>();
        var auth = new RulesAuth(endUserId, FrozenDictionary<string, RulesClaim>.Empty, OrgId: null, OrgRole: null, IsAnonymous: false);
        var catalogReader = RulesEnforcer.Catalog(session);

        var batchOutcome = SqlPlan.Batch(
            [
                SqlBuild.Statement("SELECT id, title, score FROM items WHERE id = ?", SqlBuild.Integer(2)),
                SqlBuild.Statement("UPDATE items SET score = score + 1 WHERE id = ?", SqlBuild.Integer(2)),
            ],
            allowTransactionControl: false);
        var (planned, planError) = batchOutcome switch
        {
            SqlBatch ok => (ok, (string?)null),
            SqlRefusal refusal => ((SqlBatch?)null, refusal.Message),
        };
        if (planned is null)
        {
            throw new InvalidOperationException($"Failed to plan RLS statements: {planError}");
        }

        var renderTime = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        await ctx.MeasureAsync(
            Suite,
            "rules_compile_and_enforce",
            warmup: 50,
            iterations: 500,
            async _ =>
            {
                var compiledOutcome = RulesCompiler.Compile("row.owner_id = auth.uid AND row.score >= 0", RuleColumns, RulesOperation.Read);
                var (compiled, compileError) = compiledOutcome switch
                {
                    CompiledCondition ok => (ok, (string?)null),
                    InvalidInput invalid => ((CompiledCondition?)null, invalid.Message),
                };
                if (compiled is null || string.IsNullOrWhiteSpace(RulesCompiler.Render(compiled, auth, renderTime)))
                {
                    throw new InvalidOperationException($"RulesCompiler failed: {compileError ?? "empty render"}");
                }

                var enforcedOutcome = await enforcer.ApplyAsync(databaseId, auth, planned.Statements, cancellationToken, catalogReader);
                var (enforced, enforceError) = enforcedOutcome switch
                {
                    EnforcedSql ok => (ok, (string?)null),
                    SqlRefusal refusal => ((EnforcedSql?)null, refusal.Message),
                };
                if (enforced is null
                    || enforced.Statements.Count != 2
                    || !string.Equals(enforced.RulesVersion, expectedVersion, StringComparison.Ordinal)
                    || !enforced.Statements[0].Sql.Contains("_tb_rule_0", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"RulesEnforcer.ApplyAsync failed: {enforceError ?? "missing guard CTE"}");
                }
            },
            note: "RulesCompiler.Compile/Render + RulesEnforcer.ApplyAsync (SELECT + UPDATE) against live RulesStore & SQLite schema",
            cancellationToken: cancellationToken);
    }

    private static async Task RunRowsCrudAndBatchAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        Ulid projectId,
        string endUserId,
        CancellationToken cancellationToken)
    {
        var rowsService = host.Services.GetRequiredService<RowsService>();
        var auth = new RulesAuth(endUserId, FrozenDictionary<string, RulesClaim>.Empty, OrgId: null, OrgRole: null, IsAnonymous: false);
        var caller = new EndUserCaller(projectId, Database: null, auth);
        var projectRef = projectId.ToString();

        using var templateDoc = JsonDocument.Parse(
            string.Concat("{\"owner\":\"", endUserId, "\",\"title\":\"bench-row\",\"updatedTitle\":\"bench-row-updated\",\"score\":150,\"updatedScore\":250}"));
        var root = templateDoc.RootElement;
        var createValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["owner_id"] = root.GetProperty("owner").Clone(),
            ["title"] = root.GetProperty("title").Clone(),
            ["score"] = root.GetProperty("score").Clone(),
        };
        var updateValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["title"] = root.GetProperty("updatedTitle").Clone(),
            ["score"] = root.GetProperty("updatedScore").Clone(),
        };
        RowCommand[] batchCreateCommands =
        [
            new(RowAction.Create, "items", null, createValues),
            new(RowAction.Create, "items", null, createValues),
            new(RowAction.Create, "items", null, createValues),
        ];

        await ctx.MeasureAsync(
            Suite,
            "rows_crud_and_batch",
            warmup: 10,
            iterations: 100,
            async iteration =>
            {
                _ = iteration;
                var created = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, [new RowCommand(RowAction.Create, "items", null, createValues)], batch: false, cancellationToken),
                    1,
                    "create");
                if (!long.TryParse(created[0].Key, NumberStyles.None, CultureInfo.InvariantCulture, out var createdKey) || createdKey <= 0)
                {
                    throw new InvalidOperationException($"Invalid created row key: {created[0].Key}");
                }

                var read = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, [new RowCommand(RowAction.Read, "items", createdKey, null)], batch: false, cancellationToken),
                    1,
                    "read");
                if (read[0].Row is not { Count: 4 })
                {
                    throw new InvalidOperationException("Row read returned unexpected column count");
                }

                _ = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, [new RowCommand(RowAction.Update, "items", createdKey, updateValues)], batch: false, cancellationToken),
                    1,
                    "update");

                _ = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, [new RowCommand(RowAction.Delete, "items", createdKey, null)], batch: false, cancellationToken),
                    1,
                    "delete");

                var batchCreated = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, batchCreateCommands, batch: true, cancellationToken),
                    3,
                    "batch create");

                RowCommand[] batchDeleteCommands =
                [
                    new(RowAction.Delete, "items", long.Parse(batchCreated[0].Key, CultureInfo.InvariantCulture), null),
                    new(RowAction.Delete, "items", long.Parse(batchCreated[1].Key, CultureInfo.InvariantCulture), null),
                    new(RowAction.Delete, "items", long.Parse(batchCreated[2].Key, CultureInfo.InvariantCulture), null),
                ];
                _ = ExpectRowsAnswers(
                    await rowsService.RunAsync(caller, projectRef, BenchDatabaseName, batchDeleteCommands, batch: true, cancellationToken),
                    3,
                    "batch delete");
            },
            note: "EndUserCaller with live RLS + Validate: Create/Read/Update/Delete + 3-op batch create & batch delete",
            cancellationToken: cancellationToken);
    }

    private static async Task RunHttpSqlRestLoopbackAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        Ulid projectId,
        string apiKeySecret,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 32 };
        using var http = new HttpClient(handler)
        {
            BaseAddress = host.ClientUri,
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKeySecret);

        var executePath = $"/v1/projects/{projectId}/databases/{BenchDatabaseName}/sql/execute";

        if (ctx.ShouldRunBenchmark(Suite, "http_sql_rest_read"))
        {
            await ctx.MeasureAsync(
                Suite,
                "http_sql_rest_read",
                warmup: 20,
                iterations: 200,
                async iteration =>
                {
                    var id = (iteration % 200) + 1;
                    var body = $"{{\"statements\":[{{\"sql\":\"SELECT id, title, score FROM items WHERE id = ?\",\"args\":[{id.ToString(CultureInfo.InvariantCulture)}]}}]}}";
                    await ExecuteRestAndAssertAsync(http, executePath, body, expectedStatements: 1, expectedFirstRows: 1, expectedFirstAffected: null, cancellationToken);
                },
                note: "Kestrel HTTP POST /sql/execute single-row SELECT",
                cancellationToken: cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "http_sql_rest_write"))
        {
            await ctx.MeasureAsync(
                Suite,
                "http_sql_rest_write",
                warmup: 20,
                iterations: 200,
                async iteration =>
                {
                    var id = (iteration % 200) + 1;
                    var score = 1000 + iteration;
                    var body = $"{{\"statements\":[{{\"sql\":\"UPDATE items SET score = ? WHERE id = ?\",\"args\":[{score.ToString(CultureInfo.InvariantCulture)},{id.ToString(CultureInfo.InvariantCulture)}]}}]}}";
                    await ExecuteRestAndAssertAsync(http, executePath, body, expectedStatements: 1, expectedFirstRows: 0, expectedFirstAffected: 1, cancellationToken);
                },
                note: "Kestrel HTTP POST /sql/execute single-row UPDATE",
                cancellationToken: cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "http_sql_rest_batch"))
        {
            await ctx.MeasureAsync(
                Suite,
                "http_sql_rest_batch",
                warmup: 15,
                iterations: 150,
                async iteration =>
                {
                    var idA = (iteration % 100) + 1;
                    var idB = idA + 100;
                    var score = 2000 + iteration;
                    var body =
                        "{\"statements\":["
                        + $"{{\"sql\":\"SELECT id, score FROM items WHERE id = ?\",\"args\":[{idA.ToString(CultureInfo.InvariantCulture)}]}},"
                        + $"{{\"sql\":\"SELECT id, score FROM items WHERE id = ?\",\"args\":[{idB.ToString(CultureInfo.InvariantCulture)}]}},"
                        + $"{{\"sql\":\"UPDATE items SET score = ? WHERE id = ?\",\"args\":[{score.ToString(CultureInfo.InvariantCulture)},{idA.ToString(CultureInfo.InvariantCulture)}]}},"
                        + $"{{\"sql\":\"UPDATE items SET score = ? WHERE id = ?\",\"args\":[{score.ToString(CultureInfo.InvariantCulture)},{idB.ToString(CultureInfo.InvariantCulture)}]}},"
                        + $"{{\"sql\":\"SELECT count(*) FROM items WHERE id IN (?, ?)\",\"args\":[{idA.ToString(CultureInfo.InvariantCulture)},{idB.ToString(CultureInfo.InvariantCulture)}]}}"
                        + "]}";
                    await ExecuteRestAndAssertAsync(http, executePath, body, expectedStatements: 5, expectedFirstRows: 1, expectedFirstAffected: null, cancellationToken);
                },
                note: "Kestrel HTTP POST /sql/execute 5-statement mixed read/write batch",
                cancellationToken: cancellationToken);
        }

        if (ctx.ShouldRunBenchmark(Suite, "http_sql_rest_concurrent_16"))
        {
            await ctx.MeasureAsync(
                Suite,
                "http_sql_rest_concurrent_16",
                warmup: 5,
                iterations: 40,
                async iteration =>
                {
                    var tasks = new Task[16];
                    for (var worker = 0; worker < 16; worker++)
                    {
                        var id = ((iteration * 16 + worker) % 200) + 1;
                        var body = $"{{\"statements\":[{{\"sql\":\"SELECT id, title, score FROM items WHERE id = ?\",\"args\":[{id.ToString(CultureInfo.InvariantCulture)}]}}]}}";
                        tasks[worker] = ExecuteRestAndAssertAsync(http, executePath, body, expectedStatements: 1, expectedFirstRows: 1, expectedFirstAffected: null, cancellationToken);
                    }

                    await Task.WhenAll(tasks);
                },
                note: "16 concurrent Kestrel HTTP POST /sql/execute reads per iter",
                cancellationToken: cancellationToken);
        }
    }

    private static async Task RunHttp2SqlMultiplexed32Async(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        string ownerUserId,
        string endUserId,
        string otherUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const int Streams = 32;
        const int TenantCount = 4;

        var executePaths = new string[TenantCount];
        var authHeaders = new AuthenticationHeaderValue[TenantCount];
        for (var t = 0; t < TenantCount; t++)
        {
            var orgId = Ulid.NewUlid(now);
            var projId = Ulid.NewUlid(now);
            var dbId = Ulid.NewUlid(now);
            var tenantState = await SeedCatalogAndDataAsync(
                host,
                orgId,
                projId,
                dbId,
                ownerUserId,
                endUserId,
                otherUserId,
                now,
                nameSuffix: string.Concat("h2-", t.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);
            executePaths[t] = $"/v1/projects/{projId}/databases/{BenchDatabaseName}/sql/execute";
            authHeaders[t] = new AuthenticationHeaderValue("Bearer", tenantState.IssuedKey.Secret);
        }

        using var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = false,
            MaxConnectionsPerServer = 1,
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = host.InternalUri,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };
        _ = http.DefaultRequestHeaders.TryAddWithoutValidation(InternalCredential.HeaderName, host.InternalPeerCredential);

        var payloads = new byte[200][];
        for (var i = 0; i < payloads.Length; i++)
        {
            var id = i + 1;
            payloads[i] = Encoding.UTF8.GetBytes($"{{\"statements\":[{{\"sql\":\"SELECT id, title, score FROM items WHERE id = ?\",\"args\":[{id.ToString(CultureInfo.InvariantCulture)}]}}]}}");
        }

        var tasks = new Task[Streams];
        await ctx.MeasureAsync(
            Suite,
            "http2_sql_multiplexed_32",
            warmup: 5,
            iterations: 40,
            async iteration =>
            {
                for (var stream = 0; stream < Streams; stream++)
                {
                    var tenant = stream % TenantCount;
                    var payload = payloads[((iteration * Streams) + stream) % payloads.Length];
                    tasks[stream] = ExecuteHttp2RestAndAssertAsync(http, executePaths[tenant], authHeaders[tenant], payload, cancellationToken);
                }

                await Task.WhenAll(tasks);
            },
            note: "32 multiplexed h2c /sql/execute streams over 1 HTTP/2 connection across 4 tenants",
            cancellationToken: cancellationToken,
            unitsPerOp: Streams,
            unitLabel: "streams");
    }

    private static async Task ExecuteHttp2RestAndAssertAsync(
        HttpClient http,
        string path,
        AuthenticationHeaderValue authorization,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ReadOnlyMemoryContent(payload),
        };
        request.Headers.Authorization = authorization;
        request.Content.Headers.ContentType = JsonMediaType;

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"HTTP/2 /sql/execute returned {(int)response.StatusCode}: {errorBody}");
        }

        if (response.Version != HttpVersion.Version20)
        {
            throw new InvalidOperationException($"Expected HTTP/2.0 response, got HTTP/{response.Version}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() != 1 || results[0].GetProperty("rows").GetArrayLength() != 1)
        {
            throw new InvalidOperationException("HTTP/2 /sql/execute returned unexpected result shape");
        }
    }

    private static async Task ExecuteRestAndAssertAsync(
        HttpClient http,
        string path,
        string jsonBody,
        int expectedStatements,
        int expectedFirstRows,
        long? expectedFirstAffected,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"HTTP /sql/execute returned {(int)response.StatusCode}: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() != expectedStatements)
        {
            throw new InvalidOperationException($"Expected {expectedStatements.ToString(CultureInfo.InvariantCulture)} result sets, got {results.GetArrayLength().ToString(CultureInfo.InvariantCulture)}");
        }

        var first = results[0];
        var actualRows = first.GetProperty("rows").GetArrayLength();
        if (actualRows != expectedFirstRows)
        {
            throw new InvalidOperationException($"Expected {expectedFirstRows.ToString(CultureInfo.InvariantCulture)} rows, got {actualRows.ToString(CultureInfo.InvariantCulture)}");
        }

        if (expectedFirstAffected is { } affected)
        {
            var actualAffected = first.GetProperty("rows_affected").GetInt64();
            if (actualAffected != affected)
            {
                throw new InvalidOperationException($"Expected rows_affected={affected.ToString(CultureInfo.InvariantCulture)}, got {actualAffected.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static async Task RunWsSqlV2TxLoopbackAsync(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        Ulid projectId,
        string apiKeySecret,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(SqlWsFrameTypes.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", string.Concat("Bearer ", apiKeySecret));
        var wsUri = new UriBuilder("ws", IPAddress.Loopback.ToString(), host.ClientPort, $"/v1/projects/{projectId}/databases/{BenchDatabaseName}/sql/ws").Uri;
        await socket.ConnectAsync(wsUri, cancellationToken);
        if (!string.Equals(socket.SubProtocol, SqlWsFrameTypes.Subprotocol, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WebSocket subprotocol {SqlWsFrameTypes.Subprotocol}, got '{socket.SubProtocol}'");
        }

        var receiveBuffer = new byte[64 * 1024];
        var nextFrameId = 1L;

        try
        {
            await ctx.MeasureAsync(
                Suite,
                "ws_sql_v2_tx_loopback",
                warmup: 15,
                iterations: 150,
                async iteration =>
                {
                    var baseId = nextFrameId;
                    for (var p = 0; p < 4; p++)
                    {
                        var rowId = ((iteration * 4 + p) % 200) + 1;
                        var frame = new SqlWsClientFrame(
                            SqlWsFrameTypes.Execute,
                            nextFrameId++,
                            [new SqlHttpStatement("SELECT id, title, score FROM items WHERE id = ?", [JsonNumber(rowId)], null)],
                            Consistency: null,
                            Bookmark: null);
                        await SendWsFrameAsync(socket, frame, cancellationToken);
                    }

                    for (var p = 0; p < 4; p++)
                    {
                        var reply = await ReceiveWsFrameAsync(socket, receiveBuffer, cancellationToken);
                        if (reply.Type != SqlWsFrameTypes.Result
                            || reply.Id != baseId + p
                            || reply.Error is not null
                            || reply.Results is not [{ Rows.Count: 1 }])
                        {
                            throw new InvalidOperationException($"Pipelined WS execute failed: type={reply.Type}, id={reply.Id.ToString(CultureInfo.InvariantCulture)}, err={reply.Error?.Message}");
                        }
                    }

                    var targetRowId = (iteration % 200) + 1;
                    var newScore = 5000 + iteration;

                    var beginId = nextFrameId++;
                    await SendWsFrameAsync(socket, new SqlWsClientFrame(SqlWsFrameTypes.Begin, beginId, null, "fast", null), cancellationToken);
                    var beginReply = await ReceiveWsFrameAsync(socket, receiveBuffer, cancellationToken);
                    if (beginReply.Type != SqlWsFrameTypes.Result || beginReply.Id != beginId || !beginReply.Transaction || beginReply.Error is not null)
                    {
                        throw new InvalidOperationException($"WS begin failed: {beginReply.Error?.Message ?? beginReply.Type}");
                    }

                    var updateId = nextFrameId++;
                    await SendWsFrameAsync(
                        socket,
                        new SqlWsClientFrame(
                            SqlWsFrameTypes.Execute,
                            updateId,
                            [new SqlHttpStatement("UPDATE items SET score = ? WHERE id = ?", [JsonNumber(newScore), JsonNumber(targetRowId)], null)],
                            null,
                            null),
                        cancellationToken);
                    var updateReply = await ReceiveWsFrameAsync(socket, receiveBuffer, cancellationToken);
                    if (updateReply.Type != SqlWsFrameTypes.Result
                        || updateReply.Id != updateId
                        || !updateReply.Transaction
                        || updateReply.Error is not null
                        || updateReply.Results is not [{ RowsAffected: 1 }])
                    {
                        throw new InvalidOperationException($"WS tx update failed: {updateReply.Error?.Message ?? updateReply.Type}");
                    }

                    var verifyId = nextFrameId++;
                    await SendWsFrameAsync(
                        socket,
                        new SqlWsClientFrame(
                            SqlWsFrameTypes.Execute,
                            verifyId,
                            [new SqlHttpStatement("SELECT score FROM items WHERE id = ?", [JsonNumber(targetRowId)], null)],
                            null,
                            null),
                        cancellationToken);
                    var verifyReply = await ReceiveWsFrameAsync(socket, receiveBuffer, cancellationToken);
                    if (verifyReply.Type != SqlWsFrameTypes.Result
                        || verifyReply.Id != verifyId
                        || !verifyReply.Transaction
                        || verifyReply.Error is not null
                        || verifyReply.Results is not [{ Rows: [[var scoreCell]] }]
                        || scoreCell.GetInt64() != newScore)
                    {
                        throw new InvalidOperationException($"WS tx verify SELECT failed: {verifyReply.Error?.Message ?? verifyReply.Type}");
                    }

                    var commitId = nextFrameId++;
                    await SendWsFrameAsync(socket, new SqlWsClientFrame(SqlWsFrameTypes.Commit, commitId, null, null, null), cancellationToken);
                    var commitReply = await ReceiveWsFrameAsync(socket, receiveBuffer, cancellationToken);
                    if (commitReply.Type != SqlWsFrameTypes.Result
                        || commitReply.Id != commitId
                        || commitReply.Transaction
                        || commitReply.Error is not null
                        || string.IsNullOrEmpty(commitReply.Bookmark))
                    {
                        throw new InvalidOperationException($"WS commit failed: {commitReply.Error?.Message ?? commitReply.Type}");
                    }
                },
                note: "terrascale.sql.v2 WebSocket loopback: 4 pipelined SELECTs + BEGIN/UPDATE/SELECT/COMMIT tx",
                cancellationToken: cancellationToken);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
    }

    private static async Task RunWsSqlV2SustainedPipeline16Async(
        BenchRunnerContext ctx,
        TerraBaseLoopbackHost host,
        Ulid projectId,
        string apiKeySecret,
        CancellationToken cancellationToken)
    {
        const int ConnectionCount = 4;
        const int PipelineDepth = 16;
        const int TotalOpsPerIter = ConnectionCount * PipelineDepth;

        var wsUri = new UriBuilder("ws", IPAddress.Loopback.ToString(), host.ClientPort, $"/v1/projects/{projectId}/databases/{BenchDatabaseName}/sql/ws").Uri;
        var cachedIds = new JsonElement[200];
        for (var i = 0; i < cachedIds.Length; i++)
        {
            cachedIds[i] = JsonNumber(i + 1L);
        }

        var workers = new WsPipelineWorker[ConnectionCount];
        var connected = 0;
        try
        {
            for (var i = 0; i < ConnectionCount; i++)
            {
                var socket = new ClientWebSocket();
                socket.Options.AddSubProtocol(SqlWsFrameTypes.Subprotocol);
                socket.Options.SetRequestHeader("Authorization", string.Concat("Bearer ", apiKeySecret));
                await socket.ConnectAsync(wsUri, cancellationToken);
                if (!string.Equals(socket.SubProtocol, SqlWsFrameTypes.Subprotocol, StringComparison.Ordinal))
                {
                    socket.Dispose();
                    throw new InvalidOperationException($"Expected WebSocket subprotocol {SqlWsFrameTypes.Subprotocol}, got '{socket.SubProtocol}'");
                }

                workers[i] = new WsPipelineWorker(socket, (i + 1L) * 1_000_000L);
                connected++;
            }

            var tasks = new Task[ConnectionCount];
            await ctx.MeasureAsync(
                Suite,
                "ws_sql_v2_sustained_pipeline_16",
                warmup: 5,
                iterations: 50,
                async iteration =>
                {
                    for (var w = 0; w < ConnectionCount; w++)
                    {
                        var worker = workers[w];
                        var offset = (iteration * TotalOpsPerIter) + (w * PipelineDepth);
                        tasks[w] = RunWorkerPipelineBatchAsync(worker, cachedIds, offset, PipelineDepth, cancellationToken);
                    }

                    await Task.WhenAll(tasks);
                },
                note: "4 concurrent ClientWebSocket x 16 pipelined SQL ops (64 ops/iter); reusable buffers",
                cancellationToken: cancellationToken,
                unitsPerOp: TotalOpsPerIter,
                unitLabel: "ops");
        }
        finally
        {
            for (var i = 0; i < connected; i++)
            {
                var socket = workers[i].Socket;
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }

                socket.Dispose();
            }
        }
    }

    private static async Task RunWorkerPipelineBatchAsync(
        WsPipelineWorker worker,
        JsonElement[] cachedIds,
        int startOffset,
        int pipelineDepth,
        CancellationToken cancellationToken)
    {
        var baseFrameId = worker.NextFrameId;
        for (var i = 0; i < pipelineDepth; i++)
        {
            var idArg = cachedIds[(startOffset + i) % cachedIds.Length];
            var frame = new SqlWsClientFrame(
                SqlWsFrameTypes.Execute,
                worker.NextFrameId++,
                [new SqlHttpStatement("SELECT id, title, score FROM items WHERE id = ?", [idArg], null)],
                Consistency: null,
                Bookmark: null);
            worker.SendBuffer.ResetWrittenCount();
            using (var writer = new Utf8JsonWriter(worker.SendBuffer))
            {
                JsonSerializer.Serialize(writer, frame, TerraScaleContractsJsonContext.Default.SqlWsClientFrame);
            }

            await worker.Socket.SendAsync(worker.SendBuffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        for (var i = 0; i < pipelineDepth; i++)
        {
            var reply = await ReceiveWsFrameReusableAsync(worker.Socket, worker.ReceiveChunk, worker.AssemblyBuffer, cancellationToken);
            if (reply.Type != SqlWsFrameTypes.Result
                || reply.Id != baseFrameId + i
                || reply.Error is not null
                || reply.Results is not [{ Rows.Count: 1 }])
            {
                throw new InvalidOperationException($"Sustained WS pipeline failed: type={reply.Type}, id={reply.Id.ToString(CultureInfo.InvariantCulture)}, err={reply.Error?.Message}");
            }
        }
    }

    private static async Task<SqlWsServerFrame> ReceiveWsFrameReusableAsync(
        ClientWebSocket socket,
        byte[] chunkBuffer,
        ArrayBufferWriter<byte> assemblyBuffer,
        CancellationToken cancellationToken)
    {
        assemblyBuffer.ResetWrittenCount();
        while (true)
        {
            var received = await socket.ReceiveAsync(chunkBuffer.AsMemory(), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"WebSocket closed unexpectedly: {socket.CloseStatus} {socket.CloseStatusDescription}");
            }

            if (received.EndOfMessage && assemblyBuffer.WrittenCount == 0)
            {
                var fastFrame = JsonSerializer.Deserialize(chunkBuffer.AsSpan(0, received.Count), TerraScaleContractsJsonContext.Default.SqlWsServerFrame);
                if (fastFrame is null)
                {
                    throw new InvalidOperationException("WebSocket returned null frame");
                }

                return fastFrame;
            }

            assemblyBuffer.Write(chunkBuffer.AsSpan(0, received.Count));
            if (received.EndOfMessage)
            {
                var frame = JsonSerializer.Deserialize(assemblyBuffer.WrittenSpan, TerraScaleContractsJsonContext.Default.SqlWsServerFrame);
                if (frame is null)
                {
                    throw new InvalidOperationException("WebSocket returned null frame");
                }

                return frame;
            }
        }
    }

    private sealed class WsPipelineWorker(ClientWebSocket socket, long initialFrameId)
    {
        public ClientWebSocket Socket { get; } = socket;

        public long NextFrameId { get; set; } = initialFrameId;

        public ArrayBufferWriter<byte> SendBuffer { get; } = new(4096);

        public byte[] ReceiveChunk { get; } = new byte[64 * 1024];

        public ArrayBufferWriter<byte> AssemblyBuffer { get; } = new(8192);
    }

    private static JsonElement JsonNumber(long value)
    {
        using var doc = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture));
        return doc.RootElement.Clone();
    }

    private static Task SendWsFrameAsync(ClientWebSocket socket, SqlWsClientFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, TerraScaleContractsJsonContext.Default.SqlWsClientFrame);
        return socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<SqlWsServerFrame> ReceiveWsFrameAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"WebSocket closed unexpectedly: {socket.CloseStatus} {socket.CloseStatusDescription}");
            }

            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                var frame = JsonSerializer.Deserialize(message.GetBuffer().AsSpan(0, (int)message.Length), TerraScaleContractsJsonContext.Default.SqlWsServerFrame);
                if (frame is null)
                {
                    throw new InvalidOperationException("WebSocket returned null frame");
                }

                return frame;
            }
        }
    }

    private static IReadOnlyList<RowAnswer> ExpectRowsAnswers(RowsOutcome outcome, int expectedCount, string label)
    {
        var (answers, error) = outcome switch
        {
            IReadOnlyList<RowAnswer> ok => (ok, (string?)null),
            RowRefused refused => ((IReadOnlyList<RowAnswer>?)null, $"{refused.Code}: {refused.Message}"),
            SqlRefusal refusal => (null, $"{refusal.Code}: {refusal.Message}"),
            InvalidInput invalid => (null, invalid.Message),
            CredentialRejected rejected => (null, rejected.Message),
            CallerForbidden forbidden => (null, forbidden.Message),
            ResourceNotFound missing => (null, missing.Message),
            BackendUnavailable unavailable => (null, unavailable.Message),
        };
        if (answers is null || answers.Count != expectedCount)
        {
            throw new InvalidOperationException($"RowsService.RunAsync ({label}) failed: {error ?? $"expected {expectedCount.ToString(CultureInfo.InvariantCulture)} answers"}");
        }

        return answers;
    }

    private static void AssertClusterWriteSucceeded(ClusterWriteOutcome outcome, string label)
    {
        var error = outcome switch
        {
            SqlWriteResult => (string?)null,
            SqlError sql => sql.Message,
            SerializationConflict conflict => conflict.Reason,
            SchemaMismatch => "schema mismatch",
            ClusterUnavailable unavailable => unavailable.Message,
            ClusterTimeout timeout => timeout.Message,
            StorageBusy busy => busy.Message,
        };
        if (error is not null)
        {
            throw new InvalidOperationException($"Cluster write failed ({label}): {error}");
        }
    }

    private static int AllocateLoopbackTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int AllocateLoopbackUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return udp.Client.LocalEndPoint is IPEndPoint endPoint
            ? endPoint.Port
            : throw new InvalidOperationException("Failed to allocate loopback UDP port");
    }

    private sealed record SeededServerState(
        DatabaseRecord Database,
        IssuedApiKey IssuedKey,
        ApiKeyCaller ApiKeyCaller,
        ISqlDatabaseSession TenantSession,
        StoredRules DeployedRules);

    private sealed class OidcLoopbackHost : IAsyncDisposable
    {
        private static readonly JsonWebTokenHandler TokenHandler = new();

        private readonly WebApplication _app;
        private readonly ECDsa _signingKey;
        private readonly SigningCredentials _credentials;

        private OidcLoopbackHost(WebApplication app, ECDsa signingKey, string issuer)
        {
            _app = app;
            _signingKey = signingKey;
            _credentials = new SigningCredentials(new ECDsaSecurityKey(signingKey) { KeyId = KeyId }, SecurityAlgorithms.EcdsaSha256);
            Issuer = issuer;
        }

        public string Issuer { get; }

        public static async Task<OidcLoopbackHost> StartAsync(CancellationToken cancellationToken)
        {
            var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var ecParams = signingKey.ExportParameters(includePrivateParameters: false);
            if (ecParams.Q.X is not { } xBytes || ecParams.Q.Y is not { } yBytes)
            {
                signingKey.Dispose();
                throw new InvalidOperationException("Failed to export EC P-256 public coordinates");
            }

            var jwksJson = string.Concat(
                "{\"keys\":[{\"kty\":\"EC\",\"crv\":\"P-256\",\"use\":\"sig\",\"alg\":\"ES256\",\"kid\":\"",
                KeyId,
                "\",\"x\":\"",
                Base64Url.EncodeToString(xBytes),
                "\",\"y\":\"",
                Base64Url.EncodeToString(yBytes),
                "\"}]}");
            var jwksBytes = Encoding.UTF8.GetBytes(jwksJson);
            byte[] discoveryBytes = [];

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));

            var app = builder.Build();
            app.Run(async context =>
            {
                context.Response.ContentType = "application/json";
                if (context.Request.Path == "/.well-known/openid-configuration")
                {
                    await context.Response.Body.WriteAsync(discoveryBytes, context.RequestAborted);
                    return;
                }

                if (context.Request.Path == "/.well-known/jwks.json")
                {
                    await context.Response.Body.WriteAsync(jwksBytes, context.RequestAborted);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
            });

            await app.StartAsync(cancellationToken);
            var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            if (feature?.Addresses.FirstOrDefault() is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
                signingKey.Dispose();
                throw new InvalidOperationException("Failed to resolve OIDC loopback listener port");
            }

            var issuer = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), uri.Port).Uri.AbsoluteUri.TrimEnd('/');
            discoveryBytes = Encoding.UTF8.GetBytes(string.Concat("{\"issuer\":\"", issuer, "\",\"jwks_uri\":\"", issuer, "/.well-known/jwks.json\"}"));
            return new OidcLoopbackHost(app, signingKey, issuer);
        }

        public string SignProjectToken(string userId, Ulid projectId, Ulid organizationId, string audience, DateTimeOffset now)
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = audience,
                TokenType = "at+jwt",
                NotBefore = now.AddMinutes(-1).UtcDateTime,
                IssuedAt = now.UtcDateTime,
                Expires = now.AddHours(1).UtcDateTime,
                SigningCredentials = _credentials,
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [JwtRegisteredClaimNames.Sub] = userId,
                    ["project_id"] = projectId.ToString(),
                    ["org_id"] = organizationId.ToString(),
                    ["scope"] = string.Join(' ', Scopes.ApiKey),
                },
            };
            return TokenHandler.CreateToken(descriptor);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            _signingKey.Dispose();
        }
    }

    private sealed class TerraBaseLoopbackHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TerraBaseLoopbackHost(WebApplication app, int clientPort, int internalPort, string internalPeerCredential)
        {
            _app = app;
            ClientPort = clientPort;
            ClientUri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), clientPort).Uri;
            InternalPort = internalPort;
            InternalUri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), internalPort).Uri;
            InternalPeerCredential = internalPeerCredential;
        }

        public int ClientPort { get; }

        public Uri ClientUri { get; }

        public int InternalPort { get; }

        public Uri InternalUri { get; }

        public string InternalPeerCredential { get; }

        public IServiceProvider Services => _app.Services;

        public static async Task<TerraBaseLoopbackHost> StartAsync(string dataDirectory, string authIssuer, CancellationToken cancellationToken)
        {
            var clientPort = AllocateLoopbackTcpPort();
            var internalPort = AllocateLoopbackTcpPort();
            var metricsPort = AllocateLoopbackTcpPort();
            var postgresPort = AllocateLoopbackTcpPort();
            var quicPort = AllocateLoopbackUdpPort();

            var options = new TerraBaseOptions
            {
                Port = clientPort,
                InternalPort = internalPort,
                MetricsPort = metricsPort,
                PostgresPort = postgresPort,
                QuicPort = quicPort,
                DataDirectory = dataDirectory,
                EnvironmentName = Environments.Development,
                Region = TerraBaseOptions.LocalIdentity,
                MachineId = TerraBaseOptions.LocalIdentity,
                AuthIssuer = authIssuer,
                AuthAudiences = BenchAudiences,
                MemoryBudget = 256L * 1024 * 1024,
                StorageThreads = Math.Clamp(Environment.ProcessorCount, 1, 8),
                DatabaseKeyring = KeyringSettings.Single(RandomNumberGenerator.GetBytes(32)),
            };

            var formatError = StorageFormatMarker.CheckOrCreate(options.DataDirectory, StorageFormat.Id) switch
            {
                StorageFormatAccepted => (string?)null,
                StoredFormatMismatch mismatch => $"storage format mismatch: {mismatch.Recorded} vs {mismatch.Expected}",
                StorageFormatMarkerUnavailable unavailable => unavailable.Message,
            };
            if (formatError is not null)
            {
                throw new InvalidOperationException(formatError);
            }

            var peerSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var previousPeerSecret = Environment.GetEnvironmentVariable(InternalCredential.EnvironmentVariable);
            InternalCredentialOutcome credentialOutcome;
            try
            {
                Environment.SetEnvironmentVariable(InternalCredential.EnvironmentVariable, peerSecret);
                credentialOutcome = InternalCredential.FromProcess(options);
            }
            finally
            {
                Environment.SetEnvironmentVariable(InternalCredential.EnvironmentVariable, previousPeerSecret);
            }

            var (credential, credentialError) = credentialOutcome switch
            {
                InternalCredential ok => (ok, (string?)null),
                ConfigurationError error => ((InternalCredential?)null, error.Message),
            };
            if (credential is null)
            {
                throw new InvalidOperationException($"InternalCredential failed: {credentialError}");
            }

            var keyringOutcome = DatabaseKeyring.Create(options);
            var (keyring, keyringError) = keyringOutcome switch
            {
                DatabaseKeyring ok => (ok, (string?)null),
                ConfigurationError error => ((DatabaseKeyring?)null, error.Message),
            };
            if (keyring is null)
            {
                throw new InvalidOperationException($"DatabaseKeyring failed: {keyringError}");
            }

            var clusterOutcome = ClusterOptions.Create(
                new ClusterSettings(options.MachineId, options.Region, options.DataDirectory, options.QuicPort, options.InternalPort, null, credential.CorrelationKey),
                TimeProvider.System);
            var (cluster, clusterError) = clusterOutcome switch
            {
                ClusterOptions ok => (ok, (string?)null),
                ClusterConfigurationError error => ((ClusterOptions?)null, error.Message),
            };
            if (cluster is null)
            {
                throw new InvalidOperationException($"ClusterOptions failed: {clusterError}");
            }

            var callers = new CallerRegistry(FrozenDictionary<string, CallerKey>.Empty, 0);
            var prepared = new PreparedStartup(options, credential, callers, keyring, cluster, new ContinuityOptions());

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = options.EnvironmentName });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
                kestrel.AddTerraBaseListeners(options, Listeners.InternalAddress(prepared.Cluster, options));
            });

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new HostFaults());
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(prepared.Credential);
            builder.Services.AddSingleton(prepared.Callers);
            builder.Services.ConfigureHttpJsonOptions(json =>
            {
                json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                json.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonTypeInfoResolver.Combine(
                    TerraScaleContractsJsonContext.Default, SqlJsonContext.Default, SqlSocketJsonContext.Default, ManagementJsonContext.Default, BackupJsonContext.Default,
                    RulesJsonContext.Default, RowsJsonContext.Default, ApiKeysJsonContext.Default, HealthJsonContext.Default, UsageJsonContext.Default, LogsJsonContext.Default,
                    RegionsJsonContext.Default, PressureJsonContext.Default, ClockJsonContext.Default, BackupDestinationJson.Default));
            });
            builder.Services.AddHttpClient(TokenVerifier.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
            builder.Services.AddHttpClient(AtlasPublisher.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
            builder.Services.AddHttpClient(CloseProjectEndpoint.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(15));
            builder.Services.AddHttpClient(BackupDestinationProbe.ClientName, static client => client.Timeout = TimeSpan.FromSeconds(20))
                .ConfigurePrimaryHttpMessageHandler(BackupDestinationNetwork.Handler);
            builder.Services.AddSingleton<BackupDestinationProbe>();
            builder.Services.AddSingleton<BackupDestinationStore>();
            builder.Services.AddTerraScaleEndpoints(endpoints =>
            {
                endpoints.Title = "TerraBase API";
                endpoints.Version = "v2";
                endpoints.MapTerraBaseErrors();
            });
            builder.Services.AddTerraBaseTelemetry();
            builder.Services.AddTerraBaseClientCors();
            builder.Services.AddTerraBaseNode(prepared);
            builder.Services.AddTerraBaseFeatures();
            builder.Services.AddTerraBasePostgres(new PostgresServerOptions(Tls: null));

            var app = builder.Build();
            prepared.Callers.Observe(app.Services.GetRequiredService<IMeterFactory>(), app.Services.GetRequiredService<TimeProvider>());
            _ = app.Services.GetRequiredService<TenantSessions>();
            app.UseRouting();
            app.UseTerraBaseSocketInput();
            app.UseWebSockets();
            app.UseTerraBaseClientCors();
            app.UseTerraBaseListeners();
            app.UseTerraScaleRequestIdentity(() => Ulid.NewUlid().ToString(), prepared.Credential.CorrelationKey);
            app.UseTerraBaseCallers();
            app.UseTerraBasePressure();
            app.UseTerraBaseRequestLogs();
            app.UseTerraBaseTenantQos();
            app.MapTerraScaleEndpoints();

            await app.StartAsync(cancellationToken);
            var bootstrap = app.Services.GetRequiredService<SystemBootstrap>();
            if (bootstrap.Failure is { } failure)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
                throw new InvalidOperationException($"TerraBase SystemBootstrap failed: {failure.Message}");
            }

            return new TerraBaseLoopbackHost(app, clientPort, internalPort, peerSecret);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
