using System.Text.Json;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E
{
    // Authenticated source observations are read-only test data. They do not
    // qualify a producer scheduler, provision a restore, or supply private keys.
    internal sealed class ReferenceDatabaseFacts : IBackupBackend
    {
        private readonly Dictionary<string, CapturedDatabase> databases = new(StringComparer.Ordinal);

        public static async Task<ReferenceDatabaseFacts> ReadAsync(string root, ReferencePersonas personas)
        {
            using var manifest = await ReferenceFixtures.ReadManifestAsync(root);
            var source = manifest.RootElement;
            var facts = new ReferenceDatabaseFacts();
            facts.ReadPersona(source.GetProperty("routes"), personas.Routes);
            facts.ReadPersona(source.GetProperty("seeded"), personas.Seeded);
            return facts;
        }

        private void ReadPersona(JsonElement persona, ReferencePrincipal principal)
        {
            if (persona.GetProperty("identity").GetProperty("userId").GetString() != principal.Subject)
            {
                throw new InvalidDataException("The database observations belong to another source identity.");
            }

            var projects = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var organization in persona.GetProperty("context").GetProperty("organizations").EnumerateArray())
            {
                var organizationId = Id(organization, "id");
                foreach (var project in organization.GetProperty("projects").EnumerateArray())
                {
                    if (!projects.TryAdd(ProjectKey(organizationId, Id(project, "id")), project))
                    {
                        throw new InvalidDataException("The source contains a duplicate project scope.");
                    }
                }
            }
            var inventory = new Dictionary<string, (string OrganizationId, string ProjectId, JsonElement Value)>(StringComparer.Ordinal);
            var seenProjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in persona.GetProperty("databaseInventories").EnumerateArray())
            {
                var organizationId = Id(entry, "organizationId");
                var projectId = Id(entry, "projectId");
                var key = ProjectKey(organizationId, projectId);
                if (!projects.TryGetValue(key, out var project) || !seenProjects.Add(key))
                {
                    throw new InvalidDataException("A database inventory is duplicate or outside its captured project.");
                }

                var rows = Rows(entry.GetProperty("result").GetProperty("databases"));
                if (project.GetProperty("databases").GetInt32() != rows.Length)
                {
                    throw new InvalidDataException("The source project and database counts disagree.");
                }

                foreach (var row in rows)
                {
                    var id = Id(row, "id");
                    if (!inventory.TryAdd(id, (organizationId, projectId, row)))
                    {
                        throw new InvalidDataException("The source contains a duplicate database identity.");
                    }

                    if (string.IsNullOrWhiteSpace(row.GetProperty("name").GetString()) || row.GetProperty("createdAt").GetDateTimeOffset() == default ||
                        row.GetProperty("status").GetString() is not ("ready" or "provisioning" or "creating" or "pending" or "failed"))
                    {
                        throw new InvalidDataException("The source database metadata is incomplete.");
                    }

                    var placement = row.GetProperty("placement").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    if (placement.Length == 0 || placement[0] != row.GetProperty("region").GetString() ||
                        placement.Any(string.IsNullOrWhiteSpace) || placement.Distinct(StringComparer.Ordinal).Count() != placement.Length)
                    {
                        throw new InvalidDataException("The source database placement is ambiguous.");
                    }
                }
            }
            if (!seenProjects.SetEquals(projects.Keys))
            {
                throw new InvalidDataException("The source database inventory omits a project.");
            }

            var seenDatabases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in persona.GetProperty("databaseDetails").EnumerateArray())
            {
                var id = Id(entry, "databaseId");
                if (!inventory.TryGetValue(id, out var listed) || !seenDatabases.Add(id) ||
                    listed.OrganizationId != Id(entry, "organizationId") || listed.ProjectId != Id(entry, "projectId"))
                {
                    throw new InvalidDataException("A database detail is duplicate or outside its captured inventory.");
                }

                var detail = entry.GetProperty("detail");
                foreach (var field in new[] { "id", "name", "status", "region" })
                {
                    if (detail.GetProperty(field).GetString() != listed.Value.GetProperty(field).GetString())
                    {
                        throw new InvalidDataException("The source database summary and detail disagree.");
                    }
                }

                if (detail.GetProperty("createdAt").GetDateTimeOffset() != listed.Value.GetProperty("createdAt").GetDateTimeOffset() ||
                    !detail.GetProperty("placement").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
                        listed.Value.GetProperty("placement").EnumerateArray().Select(item => item.GetString())))
                {
                    throw new InvalidDataException("The source database date or placement disagrees.");
                }

                var backups = entry.GetProperty("backups");
                // The captured public backup view omits provider trigger and scope
                // fields. An empty observed list needs neither invented field.
                if (Rows(backups.GetProperty("backups")).Length != 0)
                {
                    throw new InvalidDataException("Importing nonempty source backups requires their unexposed producer completion metadata.");
                }

                ProducerBackupPolicy? policy = null;
                if (backups.TryGetProperty("policy", out var saved))
                {
                    if (saved.GetProperty("incrementalBackupCron").GetString() != "")
                    {
                        throw new InvalidDataException("The source incremental schedule has no admitted producer contract.");
                    }

                    policy = new(saved.GetProperty("fullBackupCron").GetString(), saved.GetProperty("retentionDays").GetInt32(),
                        saved.TryGetProperty("updatedAt", out var updated) ? updated.GetDateTimeOffset() : null);
                }
                var captured = new CapturedDatabase(listed.OrganizationId, listed.ProjectId, id, detail.GetProperty("region").GetString()!,
                    policy, new HashSet<string>(StringComparer.Ordinal) { principal.Subject });
                if (databases.TryGetValue(id, out var prior))
                {
                    var comparable = prior with { Subjects = captured.Subjects };
                    if (comparable != captured)
                    {
                        throw new InvalidDataException("Two personas reported different facts for the same database.");
                    }

                    prior.Subjects.Add(principal.Subject);
                }
                else
                {
                    databases.Add(id, captured);
                }
            }
            if (!seenDatabases.SetEquals(inventory.Keys))
            {
                throw new InvalidDataException("The source database details omit an inventory identity.");
            }
        }

        public Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); _ = Database(scope, home, databaseId); return Task.FromResult<IReadOnlyList<ProducerBackup>>([]); }
        public Task<BackupBackendPage> ListPageAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? after, int limit, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); _ = Database(scope, home, databaseId); return Task.FromResult(new BackupBackendPage([], null)); }
        public Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Preserve the source's zero/unsaved policy exactly. Production validates
            // it independently and never turns it into a usable retention setting.
            return Task.FromResult(Database(scope, home, databaseId).Policy ?? throw new HttpRequestException("The source did not report a saved backup policy."));
        }
        public Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken ct)
        {
            return Task.FromResult<BackupDestinationView?>(null);
        }

        public Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket, string accessKeyId, string secretKey, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? cron, int retentionDays, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task<ProducerRestoreOperation> StartRestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task<ProducerRestoreOperation> RestoreOperationAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string operationId, CancellationToken ct)
        {
            throw ReadOnly();
        }

        public Task<ManagedDatabase> RestoreTargetAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string targetName, CancellationToken ct)
        {
            throw ReadOnly();
        }

        private CapturedDatabase Database(DatabaseScope scope, DatabaseRegion home, string id)
        {
            return databases.TryGetValue(id, out var captured) && captured.OrganizationId == scope.OrganizationId && captured.ProjectId == scope.ProjectId &&
            captured.Subjects.Contains(scope.UserId) && captured.Region == home.Code ? captured : throw new AccountAccessException("Captured database observations are unavailable in this scope.", 404);
        }

        private static AccountAccessException ReadOnly()
        {
            return new("Captured database observations do not authorize producer mutations.", 503);
        }

        private static string Id(JsonElement value, string name)
        {
            return TerraScaleIds.Validate(value.GetProperty(name).GetString());
        }

        private static string ProjectKey(string org, string project)
        {
            return org + "/" + project;
        }

        private static JsonElement[] Rows(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Null ? [] : value.EnumerateArray().ToArray();
        }

        private sealed record CapturedDatabase(string OrganizationId, string ProjectId, string Id, string Region,
            ProducerBackupPolicy? Policy, HashSet<string> Subjects);
    }
}
