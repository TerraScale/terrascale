namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SchemaCollection(string Name, SchemaField[] Fields, SchemaIndex[] Indexes, int? HistoryRetentionDays = null);
