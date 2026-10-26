using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Imports;

// Current TerraBase generated-ULID import contract through the official SDK's import client: the destination's home-region client listener,
// pinned to that region, as a ts-auth project access token. The destination itself comes from the
// dashboard's createDatabase path, which also owns the plan's database-count limit.
public sealed partial class ImportService(DatabaseService databases, IHttpClientFactory http, IProjectAccessTokens projectTokens)
{
    public const long MaxSourceBytes = 1L << 30;
    public const int MaxPartBytes = TerraBaseDatabase.MaxImportSourcePartBytes;
    // Exactly each route's scopes (contract section 2); the ts-auth exchange grants them by role on every call.
    private static readonly string[] Read = ["databases:read"];
    private static readonly string[] Cancel = ["databases:write"];
    private static readonly string[] Write = ["databases:write", "documents:write"];
    public static readonly (TerraBaseImportFormat Key, string Label)[] Formats = [(TerraBaseImportFormat.PostgresPlain, "PostgreSQL"), (TerraBaseImportFormat.Sqlite, "Turso or SQLite"), (TerraBaseImportFormat.Ndjson, "NDJSON")];
    // Mappings whose JSON value is not the source's own type (contract section 5).
    private static readonly Dictionary<string, string> TypeChanges = new(StringComparer.Ordinal)
    {
        ["decimal_string"] = "string with the exact digits, no longer a number",
        ["base64"] = "base64 string of the bytes",
        ["date"] = "string YYYY-MM-DD",
        ["timestamp"] = "string without a time zone",
        ["timestamptz"] = "string in UTC",
        ["text"] = "string with the exact dumped text"
    };

    // The contract's wire name, e.g. awaiting_source, from the SDK enum's own converter.
    public static string Wire<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value)[1..^1];
    public static string FormatLabel(TerraBaseImportFormat format) => Formats.First(item => item.Key == format).Label;
    public static bool Live(TerraBaseImportJob job) => job.State is TerraBaseImportState.Analyzing or TerraBaseImportState.Queued or TerraBaseImportState.Running or TerraBaseImportState.Validating;
    public static string Tone(TerraBaseImportState state) => state switch
    {
        TerraBaseImportState.Completed => "success",
        TerraBaseImportState.Failed or TerraBaseImportState.Rejected => "danger",
        TerraBaseImportState.Cancelled => "neutral",
        TerraBaseImportState.Ready or TerraBaseImportState.AwaitingSource => "warning",
        _ => "info"
    };

    // Contract v1 has no listImports: each project database is asked for its import.
    public async Task<(IReadOnlyList<TerraBaseImportJob> Jobs, bool Partial)> JobsAsync(AccountContext context, CancellationToken ct)
    {
        context = await databases.CurrentAsync(context, ct);
        var scope = DatabaseService.Scope(context);
        var reads = await Task.WhenAll(context.Project!.Databases.Values.Select(async database =>
        {
            try { return (Job: Validate(scope, await Import(context, await databases.HomeAsync(context, database, ct), database.Name, Read).GetImportAsync(ct), database.Name), Failed: false); }
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_import") { return (Job: (TerraBaseImportJob?)null, Failed: false); }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException && !ct.IsCancellationRequested)
            { return (Job: null, Failed: true); }
        }));
        return (reads.Where(read => read.Job is not null).Select(read => read.Job!).OrderByDescending(job => job.CreatedAt).ToArray(), reads.Any(read => read.Failed));
    }

    public async Task<TerraBaseImportJob> JobAsync(AccountContext context, string database, CancellationToken ct)
    {
        database = Name(database);
        context = await databases.CurrentAsync(context, ct);
        var (scope, home) = await RouteAsync(context, database, ct);
        return Validate(scope, await Import(context, home, database, Read).GetImportAsync(ct), database);
    }

    // The guided flow: a fresh import-<ULID> destination from the existing createDatabase (home region
    // only, plan limits included), then createImport. A resubmitted form carries the same name, so it
    // reuses that destination and TerraBase answers the identical createImport with the same job.
    public async Task<TerraBaseImportJob> CreateAsync(AccountContext context, string database, string format, long bytes, string sha256, CancellationToken ct)
    {
        context = await databases.CurrentAsync(context, ct);
        DatabaseService.DemandProvision(context.Organization!, context.UserId);
        if (!FreshName().IsMatch(database)) throw new AccountAccessException("The database field must use the destination name generated by this page. Custom database names are not supported. Reload the page to start a new import.", 422);
        if (!Formats.Any(item => Wire(item.Key) == format)) throw new AccountAccessException("Choose a PostgreSQL dump, a Turso or SQLite file, or an NDJSON file.", 422);
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
            throw new AccountAccessException("Choose the export file again so the browser can fingerprint it. This step needs JavaScript.", 422);
        if (bytes is < 1 or > MaxSourceBytes) throw new AccountAccessException("Exports must be between 1 byte and 1 GiB.", 422);
        var options = new TerraBaseImportOptions([]);
        var destination = context.Project!.Databases.Values.FirstOrDefault(item => item.Name == database)
            ?? await databases.CreateAsync(context, database, [context.Project.Region], false, ct);
        var scope = DatabaseService.Scope(context);
        var job = await Import(context, await databases.HomeAsync(context, destination, ct), database, Write)
            .CreateImportAsync(Formats.First(item => Wire(item.Key) == format).Key, bytes, sha256, options, ct);
        return Validate(scope, job, database);
    }

    // ts-auth grants (or refuses: viewer 403, nonmember 404) this part's write authority before any of its bytes are read.
    public async Task<string> AuthorizeUploadAsync(AccountContext context, CancellationToken ct)
    {
        context = await Manage(context, ct);
        return await projectTokens.GetAsync(context.Session, context.Organization!.Id, context.Project!.Id, Write, ct);
    }

    // Sent once with the token granted for this part; a failed part is never retried here.
    public async Task<TerraBaseImportJob> UploadAsync(AccountContext context, string database, long offset, ReadOnlyMemory<byte> part, string token, CancellationToken ct)
    {
        database = Name(database);
        context = await Manage(context, ct);
        var (scope, home) = await RouteAsync(context, database, ct);
        return Validate(scope, await Import(context, home, database, Write, TimeSpan.FromMinutes(2), token).UploadImportSourceAsync(offset, part, ct), database);
    }

    public async Task<TerraBaseImportJob> UpdateAsync(AccountContext context, string database, IReadOnlyList<string> exclude, CancellationToken ct)
    {
        database = Name(database);
        var options = new TerraBaseImportOptions(exclude);
        if (exclude.Count > 1000 || exclude.Any(item => item.Length is 0 or > 512)) throw new AccountAccessException("Exclude at most 1000 tables and columns.", 422);
        context = await Manage(context, ct);
        var (scope, home) = await RouteAsync(context, database, ct);
        return Validate(scope, await Import(context, home, database, Write).UpdateImportAsync(options, ct), database);
    }

    // Start only the exact preview the user reviewed; TerraBase refuses any other with preview_changed.
    public async Task<TerraBaseImportJob> StartAsync(AccountContext context, string database, string previewId, bool confirmed, CancellationToken ct)
    {
        database = Name(database);
        context = await Manage(context, ct);
        var (scope, home) = await RouteAsync(context, database, ct);
        var job = Validate(scope, await Import(context, home, database, Read).GetImportAsync(ct), database);
        if (job.State is TerraBaseImportState.Queued or TerraBaseImportState.Running && job.ConfirmedPreviewId == previewId) return job;
        if (job.State != TerraBaseImportState.Ready || job.Preview is null) throw new AccountAccessException("This import has no preview ready to start.", 409);
        if (job.Preview.Id != previewId) throw new AccountAccessException("The preview changed after you reviewed it. Review the current preview and confirm again.", 409);
        if (job.Preview.Blocking > 0) throw new AccountAccessException("Resolve the blocking issues before starting.", 409);
        if (!confirmed) throw new AccountAccessException("Confirm that you reviewed this preview and accept its differences.", 422);
        return Validate(scope, await Import(context, home, database, Write).StartImportAsync(previewId, ct), database);
    }

    public Task<TerraBaseImportJob> CancelAsync(AccountContext context, string database, CancellationToken ct) => ChangeAsync(context, database, Cancel, (import, token) => import.CancelImportAsync(token), ct);
    public Task<TerraBaseImportJob> ResumeAsync(AccountContext context, string database, CancellationToken ct) => ChangeAsync(context, database, Write, (import, token) => import.ResumeImportAsync(token), ct);

    public static ImportDifferences Differences(TerraBaseImportJob job)
    {
        var tables = job.Preview?.Tables.Where(table => !table.Excluded).ToArray() ?? [];
        return new(
            tables.SelectMany(table => table.Columns.Where(column => !column.Excluded && Changes(column.Mapping))
                .Select(column => $"{table.Source}.{column.Name}: {column.SourceType} is stored as a {Describe(column.Mapping)}.")).ToArray(),
            tables.SelectMany(table => table.Constraints.Where(constraint => !constraint.Enforced).Select(constraint =>
                $"{table.Source}: {Kind(constraint.Kind)} {constraint.Name ?? "(unnamed)"} ({string.Join(", ", constraint.Columns)}{(constraint.References is null ? "" : " → " + constraint.References)}) is recorded but not enforced.")).ToArray(),
            (job.Preview?.Tables.Where(table => table.Excluded).Select(table => table.Source + " is not imported.") ?? [])
                .Concat(tables.SelectMany(table => table.Columns.Where(column => column.Excluded).Select(column => $"{table.Source}.{column.Name} is not imported."))).ToArray(),
            tables.Select(table => $"{table.Source}: {Ids(job.Source.Format)}.").ToArray(),
            tables.SelectMany(table => table.Issues.Where(issue => issue.Severity != "blocking").Select(Issue(table))).ToArray());
    }

    // NDJSON lines in the export shape keep their id (import-ndjson guide); every other row gets a generated ULID.
    public static string Ids(TerraBaseImportFormat format) => format == TerraBaseImportFormat.Ndjson
        ? "lines exported as {collection, id, document} keep their id; other lines get a deterministic generated ULID"
        : "every row gets a deterministic generated ULID; source primary keys remain document fields";

    // SQLite reports the storage classes it saw, joined by "+", or "null" when every value is NULL.
    private static bool Changes(string mapping) => TypeChanges.ContainsKey(mapping) || mapping.Contains('+');
    public static string Describe(string mapping) => mapping.Contains('+') ? "value whose JSON type differs between rows (" + mapping.Replace("+", ", ") + ")"
        : TypeChanges.GetValueOrDefault(mapping) ?? mapping switch
        {
            "integer" => "number, exact 64-bit digits",
            "float" => "number",
            "boolean" => "true or false",
            "string" => "string",
            "json" => "embedded JSON value",
            "null" => "null (every value is NULL)",
            _ => mapping
        };
    public static Func<TerraBaseImportIssue, string> Issue(TerraBaseImportTablePlan table) => issue =>
        $"{table.Source}{(issue.Column is null ? "" : "." + issue.Column)}{(issue.Row is null ? "" : " row " + issue.Row.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}: {issue.Message}";
    public static string Kind(string kind) => kind.Replace('_', ' ');

    public static string NewDatabaseName()
    {
        // A ULID: 48-bit milliseconds and 80 random bits in lowercase Crockford base32.
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var value = new System.Numerics.BigInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) << 80 |
            new System.Numerics.BigInteger(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10), isUnsigned: true, isBigEndian: true);
        return "import-" + string.Concat(Enumerable.Range(0, 26).Select(index => alphabet[(int)(value >> (5 * (25 - index)) & 31)]));
    }

    // One exchange per SDK request; the audience stays TerraBase's global one, the pin names the home region.
    private TerraBaseDatabase Import(AccountContext context, DatabaseRegion home, string database, string[] scopes, TimeSpan? timeout = null, string? granted = null)
    {
        var client = http.CreateClient("platform");
        if (timeout is not null) client.Timeout = Timeout.InfiniteTimeSpan;
        return new TerraBaseDocumentsClient(client, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = granted is not null ? _ => ValueTask.FromResult(granted)
                : token => new(projectTokens.GetAsync(context.Session, context.Organization!.Id, context.Project!.Id, scopes, token)),
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(30)
        }).Database(context.Project!.Id, database);
    }

    private async Task<TerraBaseImportJob> ChangeAsync(AccountContext context, string database, string[] scopes, Func<TerraBaseDatabase, CancellationToken, Task<TerraBaseImportJob>> change, CancellationToken ct)
    {
        database = Name(database);
        context = await Manage(context, ct);
        var (scope, home) = await RouteAsync(context, database, ct);
        return Validate(scope, await change(Import(context, home, database, scopes), ct), database);
    }

    // Cached exchanged tokens cannot carry a previous role into the next request.
    private async Task<AccountContext> Manage(AccountContext context, CancellationToken ct)
    {
        context = await databases.CurrentAsync(context, ct);
        if (!context.CanWriteProjectData) throw new AccountAccessException("Your project role does not allow this import action.");
        return context;
    }

    // An import lives in its destination's home region.
    private async Task<(DatabaseScope, DatabaseRegion)> RouteAsync(AccountContext context, string database, CancellationToken ct)
    {
        var destination = context.Project!.Databases.Values.SingleOrDefault(item => item.Name == database && item.ProjectId == context.Project.Id)
            ?? throw new AccountAccessException("This import is unavailable.", 404);
        return (DatabaseService.Scope(context), await databases.HomeAsync(context, destination, ct));
    }

    [System.Text.RegularExpressions.GeneratedRegex("^import-[0-9a-hjkmnp-tv-z]{26}$")]
    private static partial System.Text.RegularExpressions.Regex FreshName();

    private static string Name(string database)
    {
        try { return DatabaseService.ValidName(database); }
        catch (AccountAccessException) { throw new AccountAccessException("This import is unavailable.", 404); }
    }

    // The SDK checks shapes; this checks the job belongs to the request.
    private static TerraBaseImportJob Validate(DatabaseScope scope, TerraBaseImportJob job, string database)
    {
        if (job.Project != scope.ProjectId || job.Database != database || job.Source.ReceivedBytes < 0 || job.Source.ReceivedBytes > job.Source.Bytes ||
            job.Source.Sha256 is not { Length: 64 } || !job.Source.Sha256.All(char.IsAsciiHexDigitLower) ||
            job.State == TerraBaseImportState.Ready && job.Preview is null || job.State == TerraBaseImportState.Failed && job.Error is null)
            throw new HttpRequestException("The import service returned an invalid import.");
        return job;
    }
}
