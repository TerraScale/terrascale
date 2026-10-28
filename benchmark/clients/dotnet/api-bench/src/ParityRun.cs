using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

internal sealed record ParityCredentials(string ApiKey, string Project);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ParityCredentials))]
internal partial class ParityCredentialsJson : JsonSerializerContext
{
}

internal sealed record ParityHeader(string Type, string Client, string Sdk, string SdkVersion, string Runtime, string[] Regions, string Home, string Database, string Evidence);
internal sealed record ParityOwned(string Type, string Database, string Home);
internal sealed record ParityCleanup(string Type, string Database, string Create, string Deletion, string? Status, bool ReconcileRequired, string RemoteErasure);
internal sealed record ParityOutcome(string Type, bool Accepted, string? Reason);

/// <summary>Transport and timing seams. <see cref="Live"/> is the only production configuration.</summary>
internal sealed record ParityRig(Func<ParityConnections, HttpMessageHandler> Data, Func<HttpMessageHandler> Auth, Func<int, ParityCellPlan> Plan,
    TimeSpan SetupLimit, TimeSpan CleanupLimit, TimeSpan RunLimit, TimeProvider Clock)
{
    internal static ParityRig Live => new(connections => connections.CreateHandler(), TerraBaseHttp.CreateHandler, ParityCellPlan.Live,
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), TimeProvider.System);
}

/// <summary>Owns one synthetic database, measures raw-h1/raw-h2/sdk in every discovered region, and always deletes it.</summary>
[SupportedOSPlatform("linux")]
internal static class ParityRun
{
    internal const string Evidence = "DelegatingHandler sends and counted TCP connects; SocketsHttpHandler internal wire replays are not observable";
    private const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static readonly Uri AuthOrigin = new("https://auth.terrascale.tech");
    private static readonly int[] Sizes = [256, 4096];
    private static readonly int[] Concurrency = [1, 16];
    private static readonly string[] Lanes = ["raw-h1", "raw-h2", "sdk"];

    /// <summary>0 when every cell is accepted and deletion was accepted; 1 otherwise; 2 for input refused before any network use.</summary>
    internal static async Task<int> RunAsync(string credentialsPath, string outputPath, Func<string[]> peers, ParityRig rig, TextWriter error, CancellationToken interrupt)
    {
        string[] regions;
        try
        {
            regions = peers();
        }
        catch (InvalidDataException refused)
        {
            // ParityPeers messages never contain file content.
            error.WriteLine($"api-bench: {refused.Message}");
            return 2;
        }
        if (Load(credentialsPath) is not { } credentials)
        {
            error.WriteLine("api-bench: parity credentials must be a private non-link file of 1-4096 bytes holding exactly {api_key, project}");
            return 2;
        }
        FileStream file;
        try
        {
            file = new FileStream(outputPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine("api-bench: parity output must be a new file; refusing to overwrite or follow a link");
            return 2;
        }

        await using var output = new StreamWriter(file) { AutoFlush = true };
        void Emit<T>(T row, JsonTypeInfo<T> info) => output.WriteLine(JsonSerializer.Serialize(row, info));
        var home = regions[0];
        var database = "api-perf-" + new TerraScaleIdGenerator().NewId().ToLowerInvariant();
        using var limit = new CancellationTokenSource(rig.RunLimit, rig.Clock);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(interrupt, limit.Token);
        var auth = new ParityAuth(rig.Auth());
        using var authHttp = new HttpClient(auth);
        var tokens = new Tokens(new TerraScaleAuthClient(authHttp, new TerraScaleAuthClientOptions { BaseAddress = AuthOrigin, InstanceSlug = "terrascale" }).Oidc,
            credentials.ApiKey);
        // Setup and cleanup are pinned to the alphabetically first region; auth is never pinned.
        using var setupHttp = new HttpClient(rig.Data(new ParityConnections()));
        var setup = new TerraBaseDocumentsClient(setupHttp, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = TerraBaseRegion.PublicOrigin,
            Region = home,
            AccessTokenProvider = tokens.GetAsync,
        });
        Emit(new ParityHeader("parity", Protocol.Client, Protocol.SdkName, Protocol.SdkVersion(), RuntimeInformation.FrameworkDescription, regions, home, database, Evidence),
            ParityJson.Default.ParityHeader);
        // The public recovery identity is on disk before any create is attempted.
        Emit(new ParityOwned("owned", database, home), ParityJson.Default.ParityOwned);

        var create = new StrongBox<string>("not_attempted");
        var phase = "setup";
        string? reason;
        ParityCleanup cleanup;
        try
        {
            var seeds = await SetupAsync(setup, tokens, credentials.Project, database, create, rig, run.Token);
            phase = "cells";
            reason = await CellsAsync(regions, credentials.Project, database, seeds, tokens, auth, rig, row => Emit(row, ParityJson.Default.ParityCellRow), run.Token);
        }
        catch (Exception failure)
        {
            reason = $"{phase}:{(interrupt.IsCancellationRequested ? "interrupted" : limit.IsCancellationRequested ? "run_limit" : Code(failure))}";
        }
        finally
        {
            cleanup = await CleanupAsync(setup, credentials.Project, database, create.Value!, rig);
            Emit(cleanup, ParityJson.Default.ParityCleanup);
        }

        // A late cancellation, including one after the final cell, cannot produce an accepted outcome.
        if (run.IsCancellationRequested)
            reason = $"{phase}:{(interrupt.IsCancellationRequested ? "interrupted" : "run_limit")}";
        var accepted = reason is null && cleanup.Deletion == "logical_accepted";
        Emit(new ParityOutcome("outcome", accepted, reason), ParityJson.Default.ParityOutcome);
        if (reason is not null) error.WriteLine($"api-bench: parity stopped ({reason}); evidence retained in the output");
        if (cleanup.ReconcileRequired) error.WriteLine($"api-bench: owned database {database} requires reconciliation ({cleanup.Deletion})");
        return accepted ? 0 : 1;
    }

    private static ParityCredentials? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // Reject zero-length special files (including FIFOs) before opening; a path swap remains outside this guard.
            if (!info.Exists || info.LinkTarget is not null || (info.UnixFileMode & GroupOrOther) != 0 || info.Length is < 1 or > 4096) return null;
            using var file = File.OpenRead(path);
            var buffer = new byte[4097];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = file.Read(buffer, length, buffer.Length - length)) > 0) length += read;
            if (length is < 1 or > 4096) return null;
            var bytes = buffer.AsMemory(0, length);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 2) return null;
            var credentials = JsonSerializer.Deserialize(bytes.Span, ParityCredentialsJson.Default.ParityCredentials);
            if (credentials is null || !TerraScaleIds.IsValid(credentials.Project)) return null;
            Tokens.ClientId(credentials.ApiKey);
            return credentials;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            // Parser messages can echo input bytes, so nothing from the exception is reported.
            return null;
        }
    }

    // Create, collection and both exact-size seeds share one deadline. The seed version is the write reply's.
    private static async Task<Dictionary<int, (string Id, ParityDocument Document)>> SetupAsync(TerraBaseDocumentsClient setup, Tokens tokens,
        string project, string database, StrongBox<string> create, ParityRig rig, CancellationToken run)
    {
        using var limit = new CancellationTokenSource(rig.SetupLimit, rig.Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run, limit.Token);
        try
        {
            await tokens.GetAsync(deadline.Token);
            create.Value = "uncertain";
            try
            {
                await setup.Project(project).CreateDatabaseAsync(database, allRegions: true, cancellationToken: deadline.Token);
            }
            catch (TerraBaseException refused) when (refused.StatusCode is { } status && (int)status < 500 && status != HttpStatusCode.RequestTimeout)
            {
                create.Value = "refused";
                throw;
            }
            create.Value = "created";
            var db = setup.Database(project, database);
            await db.CreateCollectionAsync(ParityTarget.Collection, deadline.Token);
            var ids = new TerraScaleIdGenerator();
            var seeds = new Dictionary<int, (string, ParityDocument)>();
            foreach (var size in Sizes)
            {
                var id = ids.NewId();
                var body = Payload.Json(0, size);
                var written = await db.PutDocumentAsync(ParityTarget.Collection, id, Payload.Parse(body), null, deadline.Token);
                seeds[size] = (id, new ParityDocument(id, written.Version, body));
            }
            return seeds;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !run.IsCancellationRequested)
        {
            throw new TimeoutException("parity setup deadline");
        }
    }

    // Three repeats per region, lane order reversed on even repeats; stops after emitting the first rejected cell.
    private static async Task<string?> CellsAsync(string[] regions, string project, string database, Dictionary<int, (string Id, ParityDocument Document)> seeds,
        Tokens tokens, ParityAuth auth, ParityRig rig, Action<ParityCellRow> emit, CancellationToken run)
    {
        foreach (var region in regions)
            for (var repeat = 1; repeat <= 3; repeat++)
                foreach (var size in Sizes)
                    foreach (var concurrency in Concurrency)
                        foreach (var lane in repeat % 2 == 0 ? Lanes.Reverse() : Lanes)
                        {
                            var target = new ParityTarget(lane, region, project, database, seeds[size].Id, size, seeds[size].Document);
                            using var observed = new ParityLane(target, tokens, rig.Data);
                            var row = await ParityCell.RunAsync(observed, target, repeat, rig.Plan(concurrency), auth, tokens, rig.Clock, run);
                            emit(row);
                            if (row.Rejection is { } rejection) return $"rejected:{rejection}";
                        }
        return null;
    }

    // An independent deadline: cleanup runs after failures, deadlines and signals. Uncertain create plus 404 still needs reconciliation.
    private static async Task<ParityCleanup> CleanupAsync(TerraBaseDocumentsClient setup, string project, string database, string create, ParityRig rig)
    {
        ParityCleanup Row(string deletion, string? status, bool reconcile) => new("cleanup", database, create, deletion, status, reconcile, "unverified");
        if (create == "not_attempted") return Row("not_attempted", null, false);
        using var deadline = new CancellationTokenSource(rig.CleanupLimit, rig.Clock);
        try
        {
            await setup.Project(project).DeleteDatabaseAsync(database, deadline.Token);
            return Row("logical_accepted", null, false);
        }
        catch (TerraBaseNotFoundException)
        {
            return Row("absent", null, create == "uncertain");
        }
        catch (Exception failure)
        {
            return Row("failed", deadline.IsCancellationRequested ? "timeout" : Code(failure), true);
        }
    }

    private static string Code(Exception error) =>
        error is TerraBaseException { StatusCode: { } status } ? ((int)status).ToString(CultureInfo.InvariantCulture) : Http.StatusOf(error);
}
