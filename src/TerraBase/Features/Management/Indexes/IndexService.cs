using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Queries;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Indexes;

/// <remarks>Indexes live in the managed database's storage; recommendations come from the <see cref="QueryFieldUsage"/> records the SQL endpoint keeps.</remarks>
internal sealed partial class IndexService(IStorageEngine engine, QueryFieldRecorder recorder, VectorIndexBuilder builder) : IIndexService
{
    private const int MaxNameLength = 63;
    private const int HashLength = 8;

    public async ValueTask<IndexListBody> ListAsync(TenantContext database, string? collection, CancellationToken cancellationToken)
    {
        var indexes = await ListIndexesAsync(database, cancellationToken).ConfigureAwait(false);
        return new IndexListBody([.. indexes.Where(index => collection is null || index.Collection == collection).Select(IndexBody.From)]);
    }

    public async ValueTask<CreateIndexResult> CreateAsync(TenantContext database, IndexDefinition index, CancellationToken cancellationToken)
    {
        var result = await WriteAsync<CreateIndexResult>(database, new NotFound(), async transaction =>
        {
            var indexes = await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false);
            if (indexes.Any(other => other.Name == index.Name || (other.Kind == index.Kind && other.Collection == index.Collection && other.Fields.SequenceEqual(index.Fields)))
                || !await transaction.CreateIndexAsync(index, cancellationToken).ConfigureAwait(false))
            {
                return IndexErrors.Exists;
            }

            // A vector index over existing documents answers "building" until the builder has added them.
            return IndexBody.From((await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false)).First(created => created.Name == index.Name));
        }, cancellationToken).ConfigureAwait(false);
        if (result is IndexBody { State: "building" })
        {
            builder.Start(database);
        }

        return result;
    }

    public ValueTask<DropIndexResult> DropAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        WriteAsync<DropIndexResult>(database, new NotFound(), async transaction =>
            await transaction.DropIndexAsync(name, cancellationToken).ConfigureAwait(false) ? new Done() : new NotFound(), cancellationToken);

    /// <remarks>
    /// A database keeps at most <see cref="QueryFieldUsage.MaxRecords"/> usage records, so this reads a bounded set. Counts
    /// not saved yet are added from memory; reading never writes to the catalog.
    /// </remarks>
    public async ValueTask<RecommendIndexesResult> RecommendAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var read = await UsagesAsync(database, cancellationToken).ConfigureAwait(false);
        if (read is not List<QueryFieldUsage> usages)
        {
            return read switch
            {
                Unavailable error => error,
                List<QueryFieldUsage> => throw new UnreachableException(),
            };
        }

        var leading = (await ListIndexesAsync(database, cancellationToken).ConfigureAwait(false)).Where(index => index.Kind == "btree")
            .Select(index => (index.Collection, index.Fields[0].Path))
            .ToHashSet();
        return new IndexRecommendationListBody([.. usages
            .Where(usage => !leading.Contains((usage.Collection, usage.Field)))
            .Select(usage => new IndexRecommendationBody(
                QueryFieldUsage.Key(usage.Collection, usage.Field),
                usage.Collection,
                [usage.Field],
                string.Create(CultureInfo.InvariantCulture, $"{usage.Queries} queries filtered or sorted on this field without an index"),
                usage.Queries))
            .OrderByDescending(recommendation => recommendation.Queries)
            .ThenBy(recommendation => recommendation.Id, StringComparer.Ordinal)]);
    }

    public async ValueTask<ApplyIndexRecommendationResult> ApplyAsync(TenantContext database, string recommendationId, CancellationToken cancellationToken)
    {
        var read = await UsagesAsync(database, cancellationToken).ConfigureAwait(false);
        if (read is not List<QueryFieldUsage> usages)
        {
            return read switch
            {
                Unavailable error => error,
                List<QueryFieldUsage> => throw new UnreachableException(),
            };
        }

        var usage = usages.FirstOrDefault(usage => QueryFieldUsage.Key(usage.Collection, usage.Field) == recommendationId);
        if (usage is null || !DocumentField.TryParse(usage.Field, out var field))
        {
            return new NotFound();
        }

        return await WriteAsync<ApplyIndexRecommendationResult>(database, new NotFound(), async transaction =>
        {
            if (Leading(await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false), usage) is { } existing)
            {
                return new AppliedIndexRecommendation("already-applied", IndexBody.From(existing));
            }

            var index = new IndexDefinition(DefaultName(usage.Collection, [field]), usage.Collection, [field]);
            return await transaction.CreateIndexAsync(index, cancellationToken).ConfigureAwait(false)
                ? new AppliedIndexRecommendation("applied", IndexBody.From(index))
                : IndexErrors.DefaultNameTaken;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="name"/> is a valid index name.</summary>
    internal static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>
    /// The collection and fields joined by <c>_</c>, for example <c>people_email</c>, when each is only lowercase letters
    /// and digits, so <c>_</c> can only mark a boundary. Otherwise every part is escaped (<c>_</c> → <c>_u</c>,
    /// <c>.</c> → <c>_d</c>, <c>-</c> → <c>_h</c>, an upper-case letter → <c>_c</c> and the letter), the parts are joined by
    /// <c>__</c>, which no escaped part contains (so no plain name either), and a hash of the exact definition is appended:
    /// <c>people_ua__email_…</c> and <c>people__a_uemail_…</c>. The hash includes the kind, distinguishing text and B-tree
    /// definitions with identical readable names, and names cut to 63 characters (the cut keeps room for the fields).
    /// </summary>
    internal static string DefaultName(string collection, IEnumerable<DocumentField> fields, string kind = "btree")
    {
        string[] parts = [collection, .. fields.Select(field => field.Path)];
        var joined = string.Join('_', parts);
        if (kind == "btree" && !parts.Any(NotNameCharacter().IsMatch) && IsValidName(joined))
        {
            return joined;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + string.Join('\0', parts))))[..HashLength];
        var name = (kind == "text" ? "text__" : "") + string.Join("__", parts.Select(Escape));
        var readableLength = MaxNameLength - HashLength - 1;
        if (name.Length > readableLength)
        {
            var fieldNames = string.Join("__", parts.Skip(1).Select(Escape));
            fieldNames = fieldNames[..Math.Min(fieldNames.Length, readableLength - 3)];
            name = name[..(readableLength - fieldNames.Length - 2)] + "__" + fieldNames;
        }

        return $"{name}_{hash}";
    }

    // Collections are [a-z0-9_-] and field paths [A-Za-z0-9_.]; every escape starts with _ and no escape is _ itself.
    private static string Escape(string part)
    {
        var escaped = new StringBuilder(part.Length);
        foreach (var character in part)
        {
            _ = character switch
            {
                '_' => escaped.Append("_u"),
                '.' => escaped.Append("_d"),
                '-' => escaped.Append("_h"),
                >= 'A' and <= 'Z' => escaped.Append("_c").Append(char.ToLowerInvariant(character)),
                _ => escaped.Append(character),
            };
        }

        return escaped.ToString();
    }

    // The saved usage records of a database with the counts not saved yet; reading never writes to the catalog.
    private ValueTask<QueryFieldUsagesResult> UsagesAsync(TenantContext database, CancellationToken cancellationToken) =>
        recorder.ReadAsync(database, cancellationToken);

    // The index of the usage's collection whose first key is the usage's field.
    private static IndexDefinition? Leading(IReadOnlyList<IndexDefinition> indexes, QueryFieldUsage usage) =>
        indexes.FirstOrDefault(index => index.Kind == "btree" && index.Collection == usage.Collection && index.Fields[0].Path == usage.Field);

    private async ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return [];
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A committed write transaction on the database's storage; missing when the storage does not exist.
    private async ValueTask<T> WriteAsync<T>(
        TenantContext database, T missing, Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken)
    {
        if (await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return missing;
        }

        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("[^a-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NotNameCharacter();
}
