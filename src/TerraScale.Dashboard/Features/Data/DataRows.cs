using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Data;

public sealed class SavedQueryRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string DatabaseId { get; init; }

    public required string Name { get; init; }

    public required string Sql { get; init; }

    public required DateTime CreatedAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<SavedQueryRow>
    {
        public void Configure(EntityTypeBuilder<SavedQueryRow> builder)
        {
            builder.ToTable("saved_queries", table =>
            {
                table.HasCheckConstraint("ck_saved_queries_id", "length(id) = 26");
                table.HasCheckConstraint("ck_saved_queries_scope", "length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) IN (0, 26)");
                table.HasCheckConstraint("ck_saved_queries_name", "length(CAST(name AS BLOB)) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_saved_queries_sql", "length(sql) > 0");
            });
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Name).UseCollation("NOCASE");
            builder.HasIndex(row => new { row.OrganizationId, row.ProjectId, row.DatabaseId, row.Name }).IsUnique();
        }
    }
}

public sealed class SqlWorkspaceRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string DatabaseId { get; init; }

    public required string UserId { get; init; }

    public required string Draft { get; set; }

    public required string[] History { get; set; }

    public required string HistoryEpoch { get; set; }

    public SqlRunReceipt? Receipt { get; set; }

    public required string Version { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<SqlWorkspaceRow>
    {
        public void Configure(EntityTypeBuilder<SqlWorkspaceRow> builder)
        {
            builder.ToTable("sql_workspaces", table =>
            {
                table.HasCheckConstraint("ck_sql_workspaces_id", "length(id) = 26");
                table.HasCheckConstraint("ck_sql_workspaces_scope", "length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) = 26");
                table.HasCheckConstraint("ck_sql_workspaces_draft", $"length(CAST(draft AS BLOB)) <= {SqlWorkspace.SqlBytes}");
                table.HasCheckConstraint("ck_sql_workspaces_history", $"json_array_length(history) <= {SqlWorkspace.HistoryCount}");
                table.HasCheckConstraint("ck_sql_workspaces_version", "length(version) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.OrganizationId, row.ProjectId, row.DatabaseId, row.UserId }).IsUnique();
            builder.HasIndex(row => row.UserId);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.Property(row => row.History).HasConversion(
                history => JsonSerializer.Serialize(history, DataRowJson.Default.StringArray),
                json => JsonSerializer.Deserialize(json, DataRowJson.Default.StringArray)!,
                new ValueComparer<string[]>((left, right) => left!.SequenceEqual(right!), history => history.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
                    history => history.ToArray()));
            builder.Property(row => row.Receipt).HasConversion(
                receipt => receipt == null ? null : JsonSerializer.Serialize(receipt, DataRowJson.Default.SqlRunReceipt),
                json => json == null ? null : JsonSerializer.Deserialize(json, DataRowJson.Default.SqlRunReceipt));
        }
    }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SqlRunReceipt))]
internal sealed partial class DataRowJson : JsonSerializerContext;
