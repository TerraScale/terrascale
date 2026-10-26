using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// Per-scenario fault injection at the persistent-state boundary, shared by its two HTTP hosts.
internal sealed class SqlWorkspaceState
{
    private Wire? wire;
    public bool FailWrites { get; set; }
    public bool FailDeletes { get; set; }
    public bool FailNextMarkerWrite { get; set; }
    public int DeleteFailures { get; private set; }
    public bool PauseNextWrite { get; set; }
    public TaskCompletionSource WritePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ResumeWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AccountContext stale = null!;
    private int importPause;
    private bool pauseImportCommit;
    private string importDatabase = "";
    private string importOrganization = "";
    private string importMarker = "";
    public TaskCompletionSource ImportPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ResumeImport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ImportCasConflicts { get; private set; }
    public void PauseImport(string organizationId, string projectId, string databaseId, bool beforeCommit)
    {
        importMarker = DatabaseWorkspaceCleanup.Key(new(organizationId, projectId, databaseId));
        importDatabase = databaseId;
        importOrganization = organizationId;
        pauseImportCommit = beforeCommit;
        Volatile.Write(ref importPause, 1);
    }
    public bool ConflictNextWrite { get; set; }
    public int Conflicts { get; private set; }
    public async Task<int> RecordCountAsync() => (await wire!.ListAsync(SqlWorkspace.Collection, default)).Count;
    public void Configure(WebApplicationBuilder builder)
    {
        wire ??= new((IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!, this);
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(wire));
        builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthDeletions>(new EmptyDeletions()));
    }
    public void MapRoutes(WebApplication app)
    {
        app.MapGet("/__test/sql-projection", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = (await AccountRequest.RequireAsync(http, sessions, accounts))!;
            var scope = DatabaseService.Scope(context);
            var document = (await wire!.GetAsync(AccountService.OrganizationsCollection, scope.OrganizationId, default))!;
            return Results.Json(AccountService.Read<OrganizationRecord>(document).Projects[scope.ProjectId].Databases.Keys);
        });
        app.MapPost("/__test/sql-marker-capacity", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = (await AccountRequest.RequireAsync(http, sessions, accounts))!;
            var scope = DatabaseService.Scope(context);
            var before = (await wire!.GetAsync(AccountService.OrganizationsCollection, scope.OrganizationId, default))!.Document.GetRawText();
            for (var index = 0; index < 4100; index++)
            {
                var marker = new DatabaseWorkspaceDeletion(scope.OrganizationId, scope.ProjectId, AccountService.NewId());
                await DatabaseWorkspaceCleanup.RememberAsync(wire, marker, default);
                await DatabaseWorkspaceCleanup.RememberAsync(wire, marker, default);
            }
            var markers = await wire.ListAsync(DatabaseWorkspaceCleanup.Collection, default);
            var after = (await wire.GetAsync(AccountService.OrganizationsCollection, scope.OrganizationId, default))!.Document.GetRawText();
            return Results.Json(new
            {
                count = markers.Count,
                unchanged = before == after,
                bounded = markers.All(row => row.Document.EnumerateObject().Count() == 3 && System.Text.Encoding.UTF8.GetByteCount(row.Document.GetRawText()) < 160),
                scoped = markers.All(row => DatabaseWorkspaceCleanup.Read(row) is var value && value.OrganizationId == scope.OrganizationId && value.ProjectId == scope.ProjectId)
            });
        });
        app.MapPost("/__test/sql-replay", async () =>
        {
            using var consumer = ActivatorUtilities.CreateInstance<DeletionReconciler>(app.Services);
            await consumer.RunOnceAsync(default);
            return Results.NoContent();
        });
        app.MapPost("/__test/sql-scope", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            stale = (await AccountRequest.RequireAsync(http, sessions, accounts))!;
            return Results.NoContent();
        });
        app.MapPost("/__test/sql-external-delete/{id}", async (string id, HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) =>
        {
            var context = (await AccountRequest.RequireAsync(http, sessions, accounts))!;
            var database = DatabaseService.Database(context, id);
            await backend.DeleteAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, default), id, default);
            return Results.NoContent();
        });
    }
    private sealed class EmptyDeletions : ITsAuthDeletions
    {
        public Task<IReadOnlyList<TerraScaleAuthDeletionProof>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TerraScaleAuthDeletionProof>>([]);
        public Task AcknowledgeAsync(TerraScaleAuthDeletionProof proof, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Wire(IDashboardState inner, SqlWorkspaceState control) : IDashboardState
    {
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
        Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct) =>
            inner.ReadPageAsync(collection, filter, limit, cursor, ct);
        Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct) =>
            inner.TransactAsync(operations, ct);
        public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct)
        {
            if (collection == AccountService.OrganizationsCollection && id == control.importOrganization && !control.pauseImportCommit &&
                Interlocked.CompareExchange(ref control.importPause, 0, 2) == 2)
            {
                // After the absent preflight check and placement read, but before the CAS
                // attempt reads metadata. Resuming must read B's already completed deletion.
                control.ImportPaused.TrySetResult();
                await control.ResumeImport.Task.WaitAsync(ct);
            }
            var document = await inner.GetAsync(collection, id, ct);
            if (collection == DatabaseWorkspaceCleanup.Collection && id == control.importMarker && document is null && !control.pauseImportCommit)
                Interlocked.CompareExchange(ref control.importPause, 2, 1);
            return document;
        }
        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);
        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct) => inner.QueryPageAsync(collection, query, ct);
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);
        public Task DeleteListedAsync(string collection, TerraBaseDocument document, CancellationToken ct)
        {
            if (collection == SqlWorkspace.Collection && control.FailDeletes) { control.DeleteFailures++; throw new IOException("Synthetic workspace cleanup failure."); }
            return inner.DeleteListedAsync(collection, document, ct);
        }
        public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition condition, CancellationToken ct)
        {
            if (collection == DatabaseWorkspaceCleanup.Collection && control.FailNextMarkerWrite)
            {
                control.FailNextMarkerWrite = false;
                throw new IOException("Synthetic deletion marker failure after remote delete.");
            }
            if (collection == SqlWorkspace.Collection)
            {
                if (control.FailWrites) throw new IOException("Synthetic SQL state failure.");
                if (control.PauseNextWrite)
                {
                    control.PauseNextWrite = false;
                    control.WritePaused.TrySetResult();
                    await control.ResumeWrite.Task.WaitAsync(ct);
                }
                if (control.ConflictNextWrite)
                {
                    control.ConflictNextWrite = false;
                    control.Conflicts++;
                    // Advance the same record so the original CAS fails naturally in the provider.
                    await inner.PutAsync(collection, id, document, condition, ct);
                }
            }
            var pausedImport = collection == AccountService.OrganizationsCollection && control.pauseImportCommit &&
                document.TryGetProperty("Projects", out var projects) && projects.EnumerateObject().Any(project =>
                    project.Value.GetProperty("Databases").TryGetProperty(control.importDatabase, out _)) &&
                Interlocked.CompareExchange(ref control.importPause, 0, 1) == 1;
            if (pausedImport)
            {
                control.ImportPaused.TrySetResult();
                await control.ResumeImport.Task.WaitAsync(ct);
            }
            try { await inner.PutAsync(collection, id, document, condition, ct); }
            catch (TerraBasePreconditionFailedException) when (pausedImport) { control.ImportCasConflicts++; throw; }
        }
    }
}
