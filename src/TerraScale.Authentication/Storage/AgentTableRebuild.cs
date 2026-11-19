using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

public static class AgentTableRebuild
{
    public const int Batch = 500;

    private const string ParentAgent = "EXISTS (SELECT 1 FROM agents_rebuilt AS p WHERE p.project_id = o.project_id AND p.id = o.agent_id)";

    private static readonly AgentTable[] Tables =
    [
        new("agents", ["project_id", "id"],
            "project_id, id, owner_user_id, name, status, allowed_scopes, allow_self_subject, created_at, expires_at, revoked_at, last_used_at, owner_auth_epoch, owner_auth_stamp, version", ""),
        new("agent_keys", ["project_id", "kid"], "project_id, kid, agent_id, public_jwk, thumbprint, created_at, revoked_at, version", " AND " + ParentAgent),
        new("agent_assertions", ["project_id", "agent_id", "jti"], "project_id, agent_id, jti, keep_until, version", " AND " + ParentAgent),
        new("agent_enrollments", ["project_id", "id"],
            "project_id, id, user_code_hash, name, requested_scopes, allow_self_subject, public_jwk, thumbprint, status, created_at, expires_at, user_id, agent_id, kid, last_poll_at, last_proof_issued_at, version",
            " AND (o.agent_id IS NULL OR " + ParentAgent + ")"),
    ];

    private static readonly string[] Indexes =
    [
        "CREATE INDEX \"IX_agents_owner_user_id\" ON \"agents\" (\"owner_user_id\")",
        "CREATE INDEX \"IX_agents_project_id_owner_user_id\" ON \"agents\" (\"project_id\", \"owner_user_id\")",
        "CREATE INDEX \"IX_agent_keys_project_id_agent_id\" ON \"agent_keys\" (\"project_id\", \"agent_id\")",
        "CREATE UNIQUE INDEX \"IX_agent_keys_project_id_thumbprint\" ON \"agent_keys\" (\"project_id\", \"thumbprint\")",
        "CREATE INDEX \"IX_agent_assertions_keep_until\" ON \"agent_assertions\" (\"keep_until\")",
        "CREATE INDEX \"IX_agent_enrollments_expires_at\" ON \"agent_enrollments\" (\"expires_at\")",
        "CREATE INDEX \"IX_agent_enrollments_project_id_agent_id\" ON \"agent_enrollments\" (\"project_id\", \"agent_id\")",
        "CREATE UNIQUE INDEX \"IX_agent_enrollments_project_id_user_code_hash\" ON \"agent_enrollments\" (\"project_id\", \"user_code_hash\")",
        "CREATE INDEX \"IX_agent_enrollments_user_id\" ON \"agent_enrollments\" (\"user_id\")",
    ];

    public static async Task RunAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!await PendingAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        foreach (var table in Tables)
        {
            var batch = table.CopyBatch(Batch);
            int copied;
            do
            {
                try
                {
                    copied = await db.Database.ExecuteSqlRawAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (TerraBaseException error) when (error.ErrorCode == TerraBaseSqlErrorCodes.UndefinedTable)
                {
                    if (await PendingAsync(db, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }

                    return;
                }
            }
            while (copied > 0);
        }

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            if (await PendingAsync(db, token).ConfigureAwait(false))
            {
                string[] swap =
                [
                    .. Tables.SelectMany(table => new[] { table.DeleteStale(), table.InsertMissing() }),
                    .. Tables.AsEnumerable().Reverse().Select(table => $"DROP TABLE {table.Name}"),
                    .. Tables.Select(table => $"ALTER TABLE {table.Rebuilt} RENAME TO {table.Name}"),
                    .. Indexes,
                ];
                foreach (var statement in swap)
                {
                    await db.Database.ExecuteSqlRawAsync(statement, token).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PendingAsync(AuthDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<int>("SELECT count(*) AS \"Value\" FROM sqlite_schema WHERE type = 'table' AND name = 'agents_rebuilt'")
            .SingleAsync(cancellationToken).ConfigureAwait(false) > 0;

    private sealed record AgentTable(string Name, string[] Key, string Columns, string Parent)
    {
        public string Rebuilt => this.Name + "_rebuilt";

        public string CopyBatch(int limit) =>
            $"INSERT OR IGNORE INTO {this.Rebuilt} ({this.Columns}) SELECT {this.Columns} FROM {this.Name} AS o WHERE NOT EXISTS (SELECT 1 FROM {this.Rebuilt} AS r WHERE {this.Match("r")}){this.Parent} ORDER BY {string.Join(", ", this.Key.Select(column => "o." + column))} LIMIT {limit}";

        public string DeleteStale() =>
            $"DELETE FROM {this.Rebuilt} WHERE NOT EXISTS (SELECT 1 FROM {this.Name} AS o WHERE {this.Match(this.Rebuilt)} AND o.version = {this.Rebuilt}.version)";

        public string InsertMissing() =>
            $"INSERT INTO {this.Rebuilt} ({this.Columns}) SELECT {this.Columns} FROM {this.Name} AS o WHERE NOT EXISTS (SELECT 1 FROM {this.Rebuilt} AS r WHERE {this.Match("r")})";

        private string Match(string rebuilt) => string.Join(" AND ", this.Key.Select(column => $"{rebuilt}.{column} = o.{column}"));
    }
}
