using System.Text;

namespace TerraBase.Storage;

internal readonly union StatementOutcome(SqlResultSet, SqlError, StrongRequired);

internal readonly union ReaderStatementOutcome(SqlResultSet, SqlError);

internal sealed class WriteContext(ConsistencyMode mode, bool analyzeReads, CancellationToken cancellationToken)
{
    public CancellationToken CancellationToken { get; set; } = cancellationToken;

    public ConsistencyMode Mode { get; } = mode;

    public bool AnalyzeReads { get; } = analyzeReads;

    public StatementInfo Info { get; } = new();

    public List<SqlResultSet> Results { get; } = [];

    public List<string> Ddl { get; } = [];

    public HashSet<string> ScannedTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public StrongRequiredReason? StrongReason { get; set; }

    public bool IsSchemaChange => Ddl.Count > 0;
}

internal sealed record StagedDescriptor(StagedWrite Staged, IReadOnlyList<ObservedRow> Observed, DateTimeOffset CreatedAt);

internal sealed partial class DatabaseSession
{
    private static readonly SqlResultSet EmptyResult = new([], [], 0, 0);

    private static readonly SqliteStatus SingleStatement = new(SqliteNative.Auth, "end users send exactly one statement per entry");

    private StatementOutcome ExecuteStatement(WriteContext context, SqlStatement statement, int index)
    {
        if (!_capture.Fault.IsOk)
        {
            return _capture.Fault.ToSqlError(index);
        }

        if (!SqlLimits.StatementFits(statement.Sql))
        {
            return SqlLimits.StatementTooLong(index);
        }

        var exposure = ConflictsTable.Expose(statement.Sql, out var sql);
        if (!exposure.IsOk)
        {
            return exposure.ToSqlError(index);
        }

        var bytes = Encoding.UTF8.GetBytes(sql);
        var offset = 0;
        var last = EmptyResult;
        var info = context.Info;
        var prepared = 0;
        while (offset < bytes.Length)
        {
            var remaining = bytes.AsSpan(offset);
            var status = _writer.PrepareUser(remaining, statement.Scope, info, out var handle, out var consumed);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            var segment = Encoding.UTF8.GetString(remaining[..consumed]);
            offset += Math.Max(consumed, 1);
            if (handle == 0)
            {
                continue;
            }

            if (statement.Scope is not null && prepared++ > 0)
            {
                SqliteConnection.FinalizeStatement(handle);
                return SingleStatement.ToSqlError(index);
            }

            if (context.Mode == ConsistencyMode.Fast && Gate(info) is { } reason)
            {
                SqliteConnection.FinalizeStatement(handle);
                return new StrongRequired(DatabaseId, reason);
            }

            var tokens = SqlTokenizer.Tokenize(segment);
            if (SqlAnalyzer.RewriteSnowflakeInsert(segment, tokens, _writer.Schema) is { } rewritten)
            {
                SqliteConnection.FinalizeStatement(handle);
                status = _writer.PrepareUser(Encoding.UTF8.GetBytes(rewritten), statement.Scope, info, out handle, out _);
                if (!status.IsOk)
                {
                    return status.ToSqlError(index);
                }

                segment = rewritten;
                tokens = SqlTokenizer.Tokenize(segment);
            }

            var isDdl = info.IsDdl;
            if (context.AnalyzeReads)
            {
                CollectScannedTables(context, handle, segment);
            }

            status = _writer.BindUser(handle, statement);
            if (!status.IsOk)
            {
                SqliteConnection.FinalizeStatement(handle);
                return status.ToSqlError(index);
            }

            string? populatedTable = null;
            if (_writer.HasUserLimits && info.CreatedTable is { } table && CreatesPopulatedTable(tokens))
            {
                status = _writer.QueryFirst("SELECT 1 FROM main.sqlite_schema WHERE name = ? COLLATE NOCASE", out var existing, SqlValue.FromText(table));
                if (!status.IsOk)
                {
                    SqliteConnection.FinalizeStatement(handle);
                    return status.ToSqlError(index);
                }

                if (existing is null)
                {
                    populatedTable = table;
                }
            }

            _capture.Annotation = SqlAnalyzer.Annotate(tokens, _writer.Schema);
            status = _writer.StepUser(handle, info, out var result, limitResults: true, context.CancellationToken);
            _capture.Annotation = null;
            SqliteConnection.FinalizeStatement(handle);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            if (isDdl)
            {
                context.Ddl.Add(segment.Trim().TrimEnd(';'));
                if (populatedTable is not null)
                {
                    status = _writer.CheckCreatedTable(populatedTable, _capture);
                    if (!status.IsOk)
                    {
                        return status.ToSqlError(index);
                    }
                }

                _ = _writer.ValidateSchemaCache();
            }

            last = result;
        }

        context.Results.Add(last);
        return last;
    }

    private static bool CreatesPopulatedTable(List<SqlToken> tokens)
    {
        var start = tokens.FindIndex(static token => !token.IsSymbol(";"));
        if (start < 0 || !tokens[start].Is("CREATE"))
        {
            return false;
        }

        var source = tokens.FindIndex(start + 1, static token => token.Is("AS") || token.IsSymbol("("));
        return source >= 0 && tokens[source].Is("AS");
    }

    private StrongRequiredReason? Gate(StatementInfo info)
    {
        if (info.IsDdl)
        {
            return StrongRequiredReason.SchemaChange;
        }

        if (info.WrittenTables.Count == 0)
        {
            return null;
        }

        if (_strict)
        {
            return StrongRequiredReason.StrictTable;
        }

        foreach (var table in info.WrittenTables)
        {
            if (_writer.Schema(table) is not { } schema)
            {
                continue;
            }

            if (schema.HasSecondaryUnique)
            {
                return StrongRequiredReason.SecondaryUniqueIndex;
            }

            if (schema.UsesImplicitRowId || schema.IsVirtual)
            {
                return StrongRequiredReason.StrictTable;
            }
        }

        return null;
    }

    private void CollectScannedTables(WriteContext context, nint handle, string segment)
    {
        var info = context.Info;
        foreach (var table in info.WrittenTables)
        {
            if (_writer.Schema(table) is { HasSecondaryUnique: true })
            {
                context.ScannedTables.Add(table);
            }
        }

        if (info.ReadTables.Count == 0)
        {
            return;
        }

        if (SqliteConnection.IsReadOnly(handle))
        {
            context.ScannedTables.UnionWith(info.ReadTables);
            return;
        }

        List<string> plan = [];
        if (!_writer.ExplainQueryPlan(segment, plan).IsOk || !plan.TrueForAll(IsPointLookup))
        {
            context.ScannedTables.UnionWith(info.ReadTables);
        }
    }

    private bool IsPointLookup(string detail)
    {
        if (detail.StartsWith("SCAN CONSTANT ROW", StringComparison.Ordinal))
        {
            return true;
        }

        if (detail.StartsWith("SCAN ", StringComparison.Ordinal))
        {
            return false;
        }

        if (!detail.StartsWith("SEARCH ", StringComparison.Ordinal))
        {
            return true;
        }

        var parts = detail.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var open = detail.LastIndexOf('(');
        var close = detail.LastIndexOf(')');
        if (parts.Length < 3 || open < 0 || close < open || _writer.Schema(parts[1]) is not { } schema)
        {
            return false;
        }

        var terms = detail[(open + 1)..close].Split(" AND ", StringSplitOptions.TrimEntries);
        if (!Array.TrueForAll(terms, static term => term.EndsWith("=?", StringComparison.Ordinal) && term.IndexOfAny(['<', '>', '!']) < 0))
        {
            return false;
        }

        var access = parts[2];
        if (access.StartsWith("USING INTEGER PRIMARY KEY", StringComparison.Ordinal) || access.StartsWith("USING PRIMARY KEY", StringComparison.Ordinal))
        {
            return terms.Length >= schema.PrimaryKeyColumnNames.Count;
        }

        var index = access.StartsWith("USING COVERING INDEX ", StringComparison.Ordinal) ? access["USING COVERING INDEX ".Length..]
            : access.StartsWith("USING INDEX ", StringComparison.Ordinal) ? access["USING INDEX ".Length..]
            : string.Empty;
        var name = index.Split(' ', 2)[0];
        return name.Length > 0 && string.Equals(name, schema.PrimaryKeyIndex, StringComparison.Ordinal) && terms.Length >= schema.PrimaryKeyColumnNames.Count;
    }

    private SqliteStatus FinishLocal(WriteContext context, ref long nextSequence, ref ulong epoch, out CommitMetadata? commit)
    {
        commit = null;
        List<NetChange> changes = [];
        var status = _capture.TakeNetChanges(changes);
        if (!status.IsOk)
        {
            return status;
        }

        if (changes.Count == 0 && context.Ddl.Count == 0)
        {
            return SqliteStatus.Success;
        }

        var hlc = _context.Clock.Tick();
        var scope = new WriteScope(_writer);
        Dictionary<string, HlcTimestamp> tables = new(StringComparer.OrdinalIgnoreCase);
        var mutations = LocalStamper.Stamp(scope, changes, ref hlc, persist: true, [], tables);
        _context.Clock.Restore(hlc);
        var sequence = nextSequence;
        var nextEpoch = context.IsSchemaChange ? epoch + 1 : epoch;
        MetaStore.TouchTables(scope, tables);
        MetaStore.SaveVersion(scope, Region, sequence, nextEpoch);
        if (scope.Failed)
        {
            return scope.Failure;
        }

        var payload = MutationBatchCodec.Encode(new DeltaBatch(context.Ddl, mutations));
        commit = new CommitMetadata(Ulid.NewUlid(_context.Time.GetUtcNow()), Region, sequence, hlc, nextEpoch, context.Mode, BookmarkWith(Region, sequence), payload);
        nextSequence = sequence + 1;
        epoch = nextEpoch;
        return SqliteStatus.Success;
    }

    private SqliteStatus FinishStage(WriteContext context, out StagedDescriptor? descriptor)
    {
        descriptor = null;
        List<NetChange> changes = [];
        var status = _capture.TakeNetChanges(changes);
        if (!status.IsOk)
        {
            return status;
        }

        var snapshot = _context.Clock.Tick();
        var stamp = snapshot;
        var scope = new WriteScope(_writer);
        List<ObservedRow> observed = [];
        var mutations = LocalStamper.Stamp(scope, changes, ref stamp, persist: false, observed, new Dictionary<string, HlcTimestamp>(StringComparer.OrdinalIgnoreCase));
        if (scope.Failed)
        {
            return scope.Failure;
        }

        var payload = MutationBatchCodec.Encode(new DeltaBatch(context.Ddl, mutations));
        var staged = new StagedWrite(
            new StagedWriteToken(Ulid.NewUlid(_context.Time.GetUtcNow())),
            DatabaseId,
            Ulid.NewUlid(_context.Time.GetUtcNow()),
            snapshot,
            SchemaEpoch,
            [.. observed.Select(static row => new ReadKey(row.Key, row.RowHlc))],
            [.. context.ScannedTables.Order(StringComparer.Ordinal)],
            [.. observed.Select(static row => row.Key)],
            context.IsSchemaChange,
            [.. context.Results],
            payload);
        descriptor = new StagedDescriptor(staged, observed, _context.Time.GetUtcNow());
        return SqliteStatus.Success;
    }

    private void RegisterStaged(StagedDescriptor descriptor)
    {
        var now = _context.Time.GetUtcNow();
        lock (_stagedGate)
        {
            foreach (var expired in _staged.Where(pair => now - pair.Value.CreatedAt > Options.StagedWriteTtl).Select(static pair => pair.Key).ToArray())
            {
                _ = _staged.Remove(expired);
            }

            _staged[descriptor.Staged.Token.Value] = descriptor;
        }
    }

    private StagedDescriptor? TakeStaged(StagedWriteToken token)
    {
        lock (_stagedGate)
        {
            return _staged.Remove(token.Value, out var descriptor) ? descriptor : null;
        }
    }

    internal static ReaderStatementOutcome ExecuteOnReader(SqliteConnection reader, SqlStatement statement, int index, StatementInfo info, HashSet<string>? readTables, bool limitResults, CancellationToken cancellationToken)
    {
        if (!SqlLimits.StatementFits(statement.Sql))
        {
            return SqlLimits.StatementTooLong(index);
        }

        var exposure = ConflictsTable.Expose(statement.Sql, out var sql);
        if (!exposure.IsOk)
        {
            return exposure.ToSqlError(index);
        }

        var bytes = Encoding.UTF8.GetBytes(sql);
        var offset = 0;
        var last = EmptyResult;
        var prepared = 0;
        while (offset < bytes.Length)
        {
            var remaining = bytes.AsSpan(offset);
            var status = reader.PrepareUser(remaining, statement.Scope, info, out var handle, out var consumed);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            offset += Math.Max(consumed, 1);
            if (handle == 0)
            {
                continue;
            }

            if (statement.Scope is not null && prepared++ > 0)
            {
                SqliteConnection.FinalizeStatement(handle);
                return SingleStatement.ToSqlError(index);
            }

            readTables?.UnionWith(info.ReadTables);
            status = reader.BindUser(handle, statement);
            if (status.IsOk)
            {
                status = reader.StepUser(handle, info, out last, limitResults, cancellationToken);
            }

            SqliteConnection.FinalizeStatement(handle);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }
        }

        return last;
    }
}
