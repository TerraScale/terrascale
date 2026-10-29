using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

internal sealed partial class DatabaseChanges
{
    private async ValueTask<IResult> StreamEndUserAsync(
        HttpContext context, TenantContext database, FeedCursor? cursor, bool head, IReadOnlySet<string>? collections,
        ProjectAccess access, CancellationToken cancellationToken)
    {
        if (cursor is not null && cursor.Issued.Value < FeedCursor.At(time.GetUtcNow() - FeedCursor.Retention).Value)
        {
            return ErrorResponse.From(Expired);
        }

        if (cursor is not null && cursor.Issued.Value > FeedCursor.At(time.GetUtcNow()).Value)
        {
            cursor = cursor with { Issued = FeedCursor.At(time.GetUtcNow()) };
        }

        var key = new DatabaseKey(database.OrganizationId, database.ProjectId, database.DatabaseId!);
        if (!TryOpen(key))
        {
            context.Response.Headers.RetryAfter = "1";
            return ErrorResponse.From(Busy);
        }

        _streams.Add(1);
        var lasts = context.Features.Get<ProjectToken>() is { } token && token.ExpiresAt - time.GetUtcNow() < MaxLifetime
            ? token.ExpiresAt - time.GetUtcNow() : MaxLifetime;
        using var expiry = new CancellationTokenSource(lasts > TimeSpan.Zero ? lasts : TimeSpan.Zero, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, lifetime.ApplicationStopping);
        var errorBuffer = new ArrayBufferWriter<byte>();
        ReadOnlyMemory<byte> scope = default;
        var started = false;
        try
        {
            while (true)
            {

                using var results = QosContext.Enter(database, memory);
                if (await engine.OpenDatabaseAsync(database, stop.Token).ConfigureAwait(false) is not { } store)
                {
                    return ErrorResponse.From(DocumentRoute.UnknownDatabase);
                }

                var sequence = store.CommitSequence;
                var transaction = await store.BeginTransactionAsync(write: false, stop.Token).ConfigureAwait(false);
                FeedPage page;
                FeedPolicy policy;
                await using (transaction.ConfigureAwait(false))
                {
                    policy = await ReadPolicyAsync(transaction, database, collections, access, stop.Token).ConfigureAwait(false);
                    if (policy.Collections.Count == 0)
                    {
                        if (!started)
                        {
                            return ErrorResponse.From(new Forbidden("the collection rules deny this operation", "access_denied"));
                        }

                        await FailAsync(context, errorBuffer, "rules_changed", "collection permissions changed; discard local state and read again", stop.Token).ConfigureAwait(false);
                        return TypedResults.Empty;
                    }

                    if (scope.IsEmpty)
                    {
                        scope = policy.Scope;
                        var text = context.Request.Headers.TryGetValue("Last-Event-ID", out var last) ? last.ToString()
                            : context.Request.Query.TryGetValue("cursor", out var query) ? query.ToString() : null;
                        if (cursor is not null && (text is null || ChangeCursor.Decode(text, out var previous) is null || !previous.Span.SequenceEqual(scope.Span)))
                        {
                            return ErrorResponse.From(Expired);
                        }
                    }
                    else if (!scope.Span.SequenceEqual(policy.Scope.Span))
                    {
                        await FailAsync(context, errorBuffer, "rules_changed", "collection permissions changed; discard local state and read again", stop.Token).ConfigureAwait(false);
                        return TypedResults.Empty;
                    }

                    if (head)
                    {
                        cursor = new FeedCursor(await transaction.GetFeedHeadAsync(stop.Token).ConfigureAwait(false), FeedCursor.At(time.GetUtcNow()));
                        head = false;
                    }

                    page = await transaction.ReadFeedAsync(cursor, collections, PageEvents, PageBytes, stop.Token).ConfigureAwait(false);
                    var visible = new List<FeedEvent>();
                    foreach (var change in page.Events)
                    {
                        if (!policy.Collections.TryGetValue(change.Collection, out var grant)
                            || change.CollectionGeneration is null || change.CollectionGeneration != grant.Collection.Id)
                        {
                            continue;
                        }

                        if (CollectionRuleAccess.CollectionWide(grant.Rules.Read, access))
                        {
                            visible.Add(change);
                        }
                        else if (!change.Deleted && change.Document is { } json && CollectionRuleAccess.CanRead(grant.Rules, json, access))
                        {
                            visible.Add(change);
                        }
                        else if (CollectionRuleAccess.ProofMatches(change.AccessBefore, grant.Collection, grant.Rules, access)
                            || CollectionRuleAccess.ProofMatches(change.AccessAfter, grant.Collection, grant.Rules, access))
                        {
                            visible.Add(change with { Deleted = true, Document = null });
                        }
                    }

                    page = page with { Events = visible };
                }
                if (store.CommitSequence != sequence)
                {
                    continue;
                }

                if (page.Expired)
                {
                    if (!started)
                    {
                        return ErrorResponse.From(Expired);
                    }

                    await FailAsync(context, errorBuffer, Expired.Code, Expired.Message, stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                var origins = Math.Max(cursor?.Stamps.Count ?? 0, page.Scanned.Count);
                var cursorBytes = 4L * ((128 + (56 * origins) + 2) / 3);
                var bytes = page.Events.Sum(change => (long)(change.Document?.Length ?? 0)
                    + (6L * (change.Collection.Length + change.Id.Length)) + 256 + (4 * cursorBytes));
                QosContext.ReserveResult((3 * bytes) + (16 * cursorBytes) + (256 * 1024));
                var issued = FeedCursor.At(time.GetUtcNow());
                var next = new FeedCursor(page.Scanned, issued) { Issuer = page.Issuer, Machine = options.MachineId, Prefixes = page.Prefixes };
                var buffer = new ArrayBufferWriter<byte>();
                if (!started)
                {
                    started = true;
                    context.Features.Get<QosRequestLease>()?.Lease.Dispose();
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["X-Accel-Buffering"] = "no";
                    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    Cursor(buffer, cursor ?? new FeedCursor(new Dictionary<Engine.Lattices.ReplicaId, Engine.Lattices.Hlc>(), issued), scope);
                    await SendAsync(context, buffer.WrittenMemory, stop.Token).ConfigureAwait(false);
                    buffer.ResetWrittenCount();
                }

                var cursorText = ChangeCursor.Encode(next, scope);
                foreach (var change in page.Events)
                {
                    Change(buffer, change, ReferenceEquals(change, page.Events[^1]) ? cursorText : null);
                    await SendAsync(context, buffer.WrittenMemory, stop.Token).ConfigureAwait(false);
                    buffer.ResetWrittenCount();
                }

                Cursor(buffer, next, scope);
                await SendAsync(context, buffer.WrittenMemory, stop.Token).ConfigureAwait(false);
                cursor = next;
                if (!page.More)
                {
                    await WaitAsync(store, sequence, time.GetUtcNow() + Heartbeat, stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (QosMemoryException) when (!stop.IsCancellationRequested)
        {
            var busy = new QuotaExceeded("organization memory budget exceeded", "tenant_memory");
            if (!started)
            {
                return ErrorResponse.From(busy);
            }
            await FailAsync(context, errorBuffer, busy.Code, busy.Message, stop.Token).ConfigureAwait(false);
        }
        catch (StorageBusyException error) when (!stop.IsCancellationRequested)
        {
            if (!started)
            {
                return ErrorResponse.From(new Unavailable(error.Message, "storage_busy"));
            }
            await FailAsync(context, errorBuffer, "storage_busy", error.Message, stop.Token).ConfigureAwait(false);
        }
        catch (IOException) when (context.Response.HasStarted)
        {
            context.Abort();
        }
        finally
        {
            Close(key);
            _streams.Add(-1);
        }

        return TypedResults.Empty;
    }

    private static async ValueTask<FeedPolicy> ReadPolicyAsync(
        IStorageTransaction transaction, TenantContext database, IReadOnlySet<string>? selected, ProjectAccess access, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(Convert.ToHexString(ChangeCursor.Scope(database, selected).Span));
        Add("actor");
        Add(access.Actor);
        Add("roles");
        Add(access.Roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var role in access.Roles.Order(StringComparer.Ordinal))
        {
            Add(role);
        }

        Add("collections");
        var grants = new Dictionary<string, FeedGrant>(StringComparer.Ordinal);
        if (selected is not null)
        {
            foreach (var name in selected.Order(StringComparer.Ordinal))
            {
                await InspectAsync(name).ConfigureAwait(false);
            }
        }
        else
        {
            string? after = null;
            bool more;
            do
            {
                (var names, more) = await transaction.ListCollectionsAsync(after, 200, cancellationToken).ConfigureAwait(false);
                foreach (var collection in names)
                {
                    await InspectAsync(collection.Name).ConfigureAwait(false);
                    after = collection.Name;
                }
            }
            while (more);
        }

        Add("end");
        return new FeedPolicy(hash.GetHashAndReset().AsMemory(0, 16), grants);

        async ValueTask InspectAsync(string name)
        {
            if (CollectionName.IsReserved(name))
            {
                return;
            }

            var collection = await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false);
            var rules = await transaction.GetCollectionRulesAsync(name, cancellationToken).ConfigureAwait(false);
            Add("collection");
            Add(name);
            Add(collection?.Id ?? string.Empty);
            Add(rules?.Fingerprint() ?? "deny");
            if (collection is not null && rules is not null && CollectionRuleAccess.Granted(rules, access, write: false))
            {
                QosContext.ReserveResult(32 * 1024);
                grants.Add(name, new FeedGrant(collection, rules));
            }
        }

        void Add(string value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, Encoding.UTF8.GetByteCount(value));
            hash.AppendData(length);
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        }
    }

    private sealed record FeedGrant(CollectionInfo Collection, CollectionAccessRules Rules);
    private sealed record FeedPolicy(ReadOnlyMemory<byte> Scope, IReadOnlyDictionary<string, FeedGrant> Collections);
}
