using System.Diagnostics;
using System.Text;

namespace TerraBase.Storage;

internal readonly union StatementOutcome(SqlResultSet, SqlError, StrongRequired);

internal readonly union ReaderStatementOutcome(SqlResultSet, SqlError);

internal readonly record struct OpenSavepoint(string Name, int DdlCount, CaptureSavepoint Capture, SqliteStatus DdlFault);

internal sealed class WriteContext(ConsistencyMode mode, bool analyzeReads, CancellationToken cancellationToken)
{
    private List<string>? _ddl;
    private List<OpenSavepoint>? _savepoints;
    private HashSet<string>? _scannedTables;

    public CancellationToken CancellationToken { get; set; } = cancellationToken;

    public ConsistencyMode Mode { get; private set; } = mode;

    public bool AnalyzeReads { get; private set; } = analyzeReads;

    public StatementInfo Info { get; } = new();

    public List<SqlResultSet> Results { get; private set; } = [];

    public List<string> Ddl => _ddl ??= [];

    public IReadOnlyList<string> DdlView => _ddl is { Count: > 0 } ddl ? ddl : [];

    public List<OpenSavepoint> Savepoints => _savepoints ??= [];

    public HashSet<string> ScannedTables => _scannedTables ??= new(StringComparer.OrdinalIgnoreCase);

    public StrongRequiredReason? StrongReason { get; set; }

    public SqliteStatus DdlFault { get; set; } = SqliteStatus.Success;

    public bool IsSchemaChange => _ddl is { Count: > 0 };

    public void Reset(ConsistencyMode mode, bool analyzeReads, CancellationToken cancellationToken)
    {
        Mode = mode;
        AnalyzeReads = analyzeReads;
        CancellationToken = cancellationToken;
        Info.Reset();
        Results.Clear();
        _ddl?.Clear();
        _savepoints?.Clear();
        _scannedTables?.Clear();
        StrongReason = null;
        DdlFault = SqliteStatus.Success;
    }

    public List<SqlResultSet> DetachResults()
    {
        var results = Results;
        Results = [];
        return results;
    }

    public void AddScannedTables(string[] tables)
    {
        if (tables.Length > 0)
        {
            ScannedTables.UnionWith(tables);
        }
    }

    public string[] TakeScannedTablesArray()
    {
        if (_scannedTables is not { Count: > 0 } scanned)
        {
            return [];
        }

        var result = new string[scanned.Count];
        scanned.CopyTo(result);
        if (result.Length > 1)
        {
            Array.Sort(result, StringComparer.Ordinal);
        }

        return result;
    }

    public void RecordDdlFault(SqliteStatus status)
    {
        if (DdlFault.IsOk && !status.IsOk)
        {
            DdlFault = status;
        }
    }
}

internal sealed record StagedDescriptor(StagedWrite Staged, IReadOnlyList<ObservedRow> Observed, DateTimeOffset CreatedAt, DeltaBatch Batch);

internal sealed partial class DatabaseSession
{
    private static readonly SqlResultSet EmptyResult = new([], [], 0, 0);

    private static readonly SqliteStatus SingleStatement = new(SqliteNative.Auth, "end users send exactly one statement per entry");

    private static readonly SqliteStatus CreateTableAsSelect =
        SqliteStatus.Unsupported("CREATE TABLE ... AS SELECT is not supported; create the table with a primary key, then INSERT ... SELECT into it");

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

        var info = context.Info;
        var schemaGeneration = SchemaGeneration;
        if (statement.Scope is null && _writer.TryGetCachedUserStatement(sql, schemaGeneration) is { HasWriterAnalysis: true } cached)
        {
            if (!context.DdlFault.IsOk)
            {
                return context.DdlFault.ToSqlError(index);
            }

            if (context.Mode == ConsistencyMode.Fast && Gate(cached.WrittenTables) is { } cachedReason)
            {
                return new StrongRequired(DatabaseId, cachedReason);
            }

            if (context.AnalyzeReads)
            {
                CollectCachedScannedTables(context, cached);
            }

            info.Reset();
            var bound = _writer.BindUser(cached.Handle, statement);
            if (!bound.IsOk)
            {
                SqliteConnection.ResetCachedUserStatement(cached.Handle);
                return bound.ToSqlError(index);
            }

            _capture.Annotation = cached.Annotation;
            var stepped = _writer.StepCachedUser(cached, info, out var cachedResult, limitResults: true, context.CancellationToken);
            _capture.Annotation = null;
            if (!stepped.IsOk)
            {
                if (stepped.Primary == SqliteNative.Schema)
                {
                    BumpSchemaGeneration();
                    _writer.InvalidateSchemaCache();
                }

                return stepped.ToSqlError(index);
            }

            context.Results.Add(cachedResult);
            return cachedResult;
        }

        var bytes = Encoding.UTF8.GetBytes(sql);
        var offset = 0;
        var last = EmptyResult;
        var prepared = 0;
        while (offset < bytes.Length)
        {
            var isFirst = offset == 0;
            var remaining = bytes.AsSpan(offset);
            var status = _writer.PrepareUser(remaining, statement.Scope, info, out var handle, out var consumed, persistent: statement.Scope is null && isFirst);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            var isSingleStatement = isFirst && SqliteConnection.IsTrailingEmpty(remaining[consumed..]);
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

            var isExplain = SqliteConnection.IsExplain(handle);
            var savepoint = isExplain ? null : info.Savepoint;
            if (!context.DdlFault.IsOk && savepoint?.Operation != SavepointOperation.Rollback)
            {
                SqliteConnection.FinalizeStatement(handle);
                return context.DdlFault.ToSqlError(index);
            }

            if (context.Mode == ConsistencyMode.Fast && Gate(info) is { } reason)
            {
                SqliteConnection.FinalizeStatement(handle);
                return new StrongRequired(DatabaseId, reason);
            }

            var tokens = SqlTokenizer.Tokenize(segment);
            if ((SqlAnalyzer.RewriteSnowflakeInsert(segment, tokens, _capture.Resolve) ?? SqlAnalyzer.RewriteTriggerInserts(segment, tokens, _capture.Resolve)) is { } rewritten)
            {
                SqliteConnection.FinalizeStatement(handle);
                var rewrittenBytes = Encoding.UTF8.GetBytes(rewritten);
                status = _writer.PrepareUser(rewrittenBytes, statement.Scope, info, out handle, out var rewrittenConsumed, persistent: statement.Scope is null && isSingleStatement);
                if (!status.IsOk)
                {
                    return status.ToSqlError(index);
                }

                isSingleStatement &= SqliteConnection.IsTrailingEmpty(rewrittenBytes.AsSpan(rewrittenConsumed));
                segment = rewritten;
                tokens = SqlTokenizer.Tokenize(segment);
            }

            if (info.CreatedTable is not null && CreatesPopulatedTable(tokens))
            {
                SqliteConnection.FinalizeStatement(handle);
                return CreateTableAsSelect.ToSqlError(index);
            }

            var isDdl = !isExplain && info.IsDdl;
            var inserts = IsInsertOrReplace(tokens);
            string[]? computedScanned = null;
            var scannedCacheable = false;
            if (context.AnalyzeReads)
            {
                computedScanned = ComputeScannedTables(SqliteConnection.IsReadOnly(handle), info.ReadTables, info.WrittenTables, segment, inserts, out scannedCacheable);
                context.AddScannedTables(computedScanned);
            }

            var annotation = SqlAnalyzer.Annotate(tokens, _capture.Resolve);
            if (!_capture.Fault.IsOk)
            {
                _capture.Annotation = null;
                SqliteConnection.FinalizeStatement(handle);
                return _capture.Fault.ToSqlError(index);
            }

            var cacheable = statement.Scope is null && isSingleStatement && !info.IsUncacheable && !info.IsDdl && info.Savepoint is null && info.CreatedTable is null;
            if (cacheable && _writer.CacheUserStatement(sql, handle, info, hasWriterAnalysis: true, segment, inserts, annotation, schemaGeneration) is { } entry)
            {
                if (scannedCacheable && computedScanned is not null)
                {
                    entry.ScannedTables = computedScanned;
                }

                status = _writer.BindUser(entry.Handle, statement);
                if (!status.IsOk)
                {
                    SqliteConnection.ResetCachedUserStatement(entry.Handle);
                    return status.ToSqlError(index);
                }

                _capture.Annotation = entry.Annotation;
                status = _writer.StepCachedUser(entry, info, out var cachedResult, limitResults: true, context.CancellationToken);
                _capture.Annotation = null;
                if (!status.IsOk)
                {
                    if (status.Primary == SqliteNative.Schema)
                    {
                        BumpSchemaGeneration();
                        _writer.InvalidateSchemaCache();
                    }

                    return status.ToSqlError(index);
                }

                context.Results.Add(cachedResult);
                return cachedResult;
            }

            status = _writer.BindUser(handle, statement);
            if (!status.IsOk)
            {
                SqliteConnection.FinalizeStatement(handle);
                return status.ToSqlError(index);
            }

            var cookieBefore = 0L;
            if (isDdl)
            {
                status = _capture.MaterializeAllUnchanged();
                if (!status.IsOk)
                {
                    SqliteConnection.FinalizeStatement(handle);
                    return status.ToSqlError(index);
                }

                _writer.MarkSchemaDirty();
                status = _writer.ReadSchemaCookie(out cookieBefore);
                if (!status.IsOk)
                {
                    SqliteConnection.FinalizeStatement(handle);
                    return status.ToSqlError(index);
                }
            }

            _capture.Annotation = annotation;
            status = _writer.StepUser(handle, info, out var result, limitResults: true, context.CancellationToken);
            _capture.Annotation = null;
            SqliteConnection.FinalizeStatement(handle);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            if (isDdl)
            {
                status = _writer.ReadSchemaCookie(out var cookieAfter);
                if (!status.IsOk)
                {
                    BumpSchemaGeneration();
                    _writer.InvalidateSchemaCache();
                    context.RecordDdlFault(status);
                    return status.ToSqlError(index);
                }

                if (cookieAfter != cookieBefore)
                {
                    context.Ddl.Add(segment.Trim().TrimEnd(';'));
                }

                status = _writer.ValidateSchemaCache(force: true);
                if (!status.IsOk)
                {
                    return status.ToSqlError(index);
                }
            }

            if (savepoint is not null)
            {
                status = TrackSavepoint(context, savepoint);
                if (!status.IsOk)
                {
                    return status.ToSqlError(index);
                }
            }

            last = result;
        }

        context.Results.Add(last);
        return last;
    }

    private SqliteStatus TrackSavepoint(WriteContext context, UserSavepoint savepoint)
    {
        var savepoints = context.Savepoints;
        switch (savepoint.Operation)
        {
            case SavepointOperation.Begin:
                savepoints.Add(new OpenSavepoint(savepoint.Name, context.Ddl.Count, _capture.MarkSavepoint(), context.DdlFault));
                return SqliteStatus.Success;
            case SavepointOperation.Release when LastSavepoint(savepoints, savepoint.Name) is var released and >= 0:
                savepoints.RemoveRange(released, savepoints.Count - released);
                return SqliteStatus.Success;
            case SavepointOperation.Rollback when LastSavepoint(savepoints, savepoint.Name) is var target and >= 0:
                var open = savepoints[target];
                var rolledBackDdl = context.IsSchemaChange && context.Ddl.Count > open.DdlCount;
                if (rolledBackDdl)
                {
                    context.Ddl.RemoveRange(open.DdlCount, context.Ddl.Count - open.DdlCount);
                    BumpSchemaGeneration();
                }

                savepoints.RemoveRange(target + 1, savepoints.Count - target - 1);
                _capture.RestoreSavepoint(open.Capture);
                context.DdlFault = open.DdlFault;
                return _writer.ValidateSchemaCache(force: true);
            case SavepointOperation.Release or SavepointOperation.Rollback:
                return SqliteStatus.Success;
            default:
                throw new UnreachableException();
        }
    }

    private static int LastSavepoint(List<OpenSavepoint> savepoints, string name) =>
        savepoints.FindLastIndex(entry => AsciiIgnoreCaseComparer.Instance.Equals(entry.Name, name));

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

    private StrongRequiredReason? Gate(StatementInfo info) =>
        info.IsDdl ? StrongRequiredReason.SchemaChange : Gate(info.WrittenTables);

    private StrongRequiredReason? Gate(IReadOnlyCollection<string> writtenTables)
    {
        if (writtenTables.Count == 0)
        {
            return null;
        }

        if (_strict)
        {
            return StrongRequiredReason.StrictTable;
        }

        foreach (var table in writtenTables)
        {
            if (_capture.Resolve(table) is not { } schema)
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

    private static bool IsInsertOrReplace(List<SqlToken> tokens)
    {
        var verb = SqlAnalyzer.MainVerb(tokens);
        return verb >= 0 && (tokens[verb].Is("INSERT") || tokens[verb].Is("REPLACE"));
    }

    private static bool ContainsIgnoreCase(IReadOnlyCollection<string> names, string value)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void CollectCachedScannedTables(WriteContext context, CachedUserStatement cached)
    {
        var scanned = cached.ScannedTables;
        if (scanned is null)
        {
            scanned = ComputeScannedTables(cached.ReadOnly, cached.ReadTables, cached.WrittenTables, cached.AnalyzedSql, cached.IsInsertOrReplace, out var cacheable);
            if (cacheable)
            {
                cached.ScannedTables = scanned;
            }
        }

        context.AddScannedTables(scanned);
    }

    private string[] ComputeScannedTables(
        bool readOnly,
        IReadOnlyCollection<string> readTables,
        IReadOnlyCollection<string> writtenTables,
        string segment,
        bool inserts,
        out bool cacheable)
    {
        cacheable = true;
        HashSet<string>? scanned = null;
        foreach (var table in writtenTables)
        {
            if (_capture.Resolve(table) is { HasSecondaryUnique: true })
            {
                scanned ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _ = scanned.Add(table);
            }
        }

        if (readTables.Count == 0)
        {
            return scanned is null ? [] : [.. scanned];
        }

        if (readOnly)
        {
            if (scanned is null)
            {
                return [.. readTables];
            }

            scanned.UnionWith(readTables);
            return [.. scanned];
        }

        List<string> plan = [];
        if (!_writer.ExplainQueryPlan(segment, plan).IsOk)
        {
            cacheable = false;
            if (scanned is null)
            {
                return [.. readTables];
            }

            scanned.UnionWith(readTables);
            return [.. scanned];
        }

        Dictionary<string, int> accesses = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> pointLookups = new(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in plan)
        {
            if (PlanTable(detail) is not { } table)
            {
                continue;
            }

            if (!ContainsIgnoreCase(readTables, table))
            {
                if (scanned is null)
                {
                    return [.. readTables];
                }

                scanned.UnionWith(readTables);
                return [.. scanned];
            }

            accesses[table] = accesses.GetValueOrDefault(table) + 1;
            if (IsPointLookup(detail))
            {
                _ = pointLookups.Add(table);
            }
        }

        foreach (var table in readTables)
        {
            var readsOnlyWrittenRow = ContainsIgnoreCase(writtenTables, table) && accesses.GetValueOrDefault(table) switch
            {
                0 => true,
                1 => !inserts && pointLookups.Contains(table),
                _ => false,
            };
            if (!readsOnlyWrittenRow)
            {
                scanned ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _ = scanned.Add(table);
            }
        }

        return scanned is null ? [] : [.. scanned];
    }

    private static string? PlanTable(string detail)
    {
        var scan = detail.StartsWith("SCAN ", StringComparison.Ordinal) && !detail.StartsWith("SCAN CONSTANT ROW", StringComparison.Ordinal);
        if (!scan && !detail.StartsWith("SEARCH ", StringComparison.Ordinal))
        {
            return null;
        }

        var name = detail[(scan ? "SCAN ".Length : "SEARCH ".Length)..];
        var access = name.IndexOf(" USING ", StringComparison.Ordinal);
        return access >= 0 ? name[..access] : name;
    }

    private bool IsPointLookup(string detail)
    {
        if (!detail.StartsWith("SEARCH ", StringComparison.Ordinal))
        {
            return false;
        }

        var table = PlanTable(detail);
        var usingAt = detail.IndexOf(" USING ", StringComparison.Ordinal);
        var open = detail.LastIndexOf('(');
        var close = detail.LastIndexOf(')');
        if (table is null || usingAt < 0 || open < 0 || close < open || _capture.Resolve(table) is not { } schema)
        {
            return false;
        }

        var terms = detail[(open + 1)..close].Split(" AND ", StringSplitOptions.TrimEntries);
        if (!Array.TrueForAll(terms, static term => term.EndsWith("=?", StringComparison.Ordinal) && term.IndexOfAny(['<', '>', '!']) < 0))
        {
            return false;
        }

        var access = detail[(usingAt + 1)..];
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

    private SqliteStatus FinishStage(WriteContext context, out StagedDescriptor? descriptor)
    {
        descriptor = null;
        if (!context.DdlFault.IsOk)
        {
            _capture.End();
            return context.DdlFault;
        }

        var changes = _laneChanges;
        changes.Clear();
        var status = _capture.TakeNetChanges(changes);
        if (!status.IsOk)
        {
            changes.Clear();
            return status;
        }

        var snapshot = _context.Clock.Tick();
        var stamp = snapshot;
        var scope = WriterScope();
        scope.Reset();
        var observed = new List<ObservedRow>(changes.Count);
        var mutations = LocalStamper.Stamp(scope, changes, ref stamp, persist: false, observed, tables: null);
        changes.Clear();
        if (scope.Failed)
        {
            return scope.Failure;
        }

        var readKeys = observed.Count == 0 ? [] : new ReadKey[observed.Count];
        var writeKeys = observed.Count == 0 ? [] : new RowKey[observed.Count];
        for (var index = 0; index < observed.Count; index++)
        {
            var row = observed[index];
            readKeys[index] = new ReadKey(row.Key, row.RowHlc);
            writeKeys[index] = row.Key;
        }

        var scannedTables = context.TakeScannedTablesArray();
        var now = _context.Time.GetUtcNow();
        string[] ddl = context.IsSchemaChange ? [.. context.Ddl] : [];
        var batch = ddl.Length == 0 && mutations.Count == 0 ? DeltaBatch.Empty : new DeltaBatch(ddl, mutations);
        var payload = MutationBatchCodec.Encode(batch);
        var staged = new StagedWrite(
            new StagedWriteToken(Ulid.NewUlid(now)),
            DatabaseId,
            Ulid.NewUlid(now),
            snapshot,
            SchemaEpoch,
            readKeys,
            scannedTables,
            writeKeys,
            context.IsSchemaChange,
            [.. context.Results],
            payload);
        descriptor = new StagedDescriptor(staged, observed, now, batch);
        return SqliteStatus.Success;
    }

    private void RegisterStaged(StagedDescriptor descriptor)
    {
        var retained = descriptor with { Staged = descriptor.Staged with { ResultSets = [] } };
        lock (_stagedGate)
        {
            DropExpiredStaged();
            _staged[descriptor.Staged.Token.Value] = retained;
        }
    }

    private StagedDescriptor? TakeStaged(StagedWriteToken token)
    {
        lock (_stagedGate)
        {
            DropExpiredStaged();
            return _staged.Remove(token.Value, out var descriptor) ? descriptor : null;
        }
    }

    private void DropExpiredStaged()
    {
        var now = _context.Time.GetUtcNow();
        foreach (var expired in _staged.Where(pair => now - pair.Value.CreatedAt > Options.StagedWriteTtl).Select(static pair => pair.Key).ToArray())
        {
            _ = _staged.Remove(expired);
        }
    }

    internal ReaderStatementOutcome ExecuteOnReader(SqliteConnection reader, SqlStatement statement, int index, StatementInfo info, HashSet<string>? readTables, bool limitResults, CancellationToken cancellationToken)
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

        var schemaGeneration = SchemaGeneration;
        if (statement.Scope is null && reader.TryGetCachedUserStatement(sql, schemaGeneration) is { } cached)
        {
            info.Reset();
            readTables?.UnionWith(cached.ReadTables);
            return ExecuteCachedOnReader(reader, cached, statement, index, info, limitResults, cancellationToken);
        }

        var bytes = Encoding.UTF8.GetBytes(sql);
        var offset = 0;
        var last = EmptyResult;
        var prepared = 0;
        while (offset < bytes.Length)
        {
            var isFirst = offset == 0;
            var remaining = bytes.AsSpan(offset);
            var status = reader.PrepareUser(remaining, statement.Scope, info, out var handle, out var consumed, persistent: statement.Scope is null && isFirst);
            if (!status.IsOk)
            {
                return status.ToSqlError(index);
            }

            var isSingleStatement = isFirst && SqliteConnection.IsTrailingEmpty(remaining[consumed..]);
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
            var cacheable = statement.Scope is null && isSingleStatement && !info.IsUncacheable && !info.IsDdl && info.Savepoint is null && info.CreatedTable is null;
            if (cacheable && reader.CacheUserStatement(sql, handle, info, hasWriterAnalysis: false, sql, isInsertOrReplace: false, annotation: null, schemaGeneration) is { } entry)
            {
                return ExecuteCachedOnReader(reader, entry, statement, index, info, limitResults, cancellationToken);
            }

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

    private ReaderStatementOutcome ExecuteCachedOnReader(
        SqliteConnection reader,
        CachedUserStatement cached,
        SqlStatement statement,
        int index,
        StatementInfo info,
        bool limitResults,
        CancellationToken cancellationToken)
    {
        var status = reader.BindUser(cached.Handle, statement);
        if (!status.IsOk)
        {
            SqliteConnection.ResetCachedUserStatement(cached.Handle);
            return status.ToSqlError(index);
        }

        status = reader.StepCachedUser(cached, info, out var result, limitResults, cancellationToken);
        if (!status.IsOk)
        {
            if (status.Primary == SqliteNative.Schema)
            {
                BumpSchemaGeneration();
                reader.InvalidateSchemaCache();
            }

            return status.ToSqlError(index);
        }

        return result;
    }
}
