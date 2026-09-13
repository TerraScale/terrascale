using System.Text.Json;
using TerraScale.DotNet;

internal static class JourneyChecks
{
    internal static bool IsStaleDenial(System.Net.HttpStatusCode? status, string? code) =>
        status == System.Net.HttpStatusCode.PreconditionFailed && code == "precondition_failed";
    // The SDK requires a strong ETag and checks it against the write revision;
    // ChangeToken is that authoritative unquoted revision for both reads/writes.
    internal static void SameReceipt(TerraScaleDocument first, TerraScaleDocument next)
    {
        if (string.IsNullOrEmpty(first.ChangeToken) || first.ChangeToken != next.ChangeToken ||
            first.Id != next.Id || first.DatabaseId != next.DatabaseId || first.Collection != next.Collection)
            throw new InvalidOperationException("authoritative receipt/revision changed");
    }

    internal static void Body(TerraScaleDocument document, string runId, int value)
    {
        if (document.Value.GetProperty("sdk").GetString() != "dotnet" ||
            document.Value.GetProperty("runId").GetString() != runId || document.Value.GetProperty("value").GetInt32() != value)
            throw new InvalidOperationException("document effect did not match the requested body");
    }

    internal static bool ReactiveValue(TerraScaleReactiveSnapshot snapshot, ulong initialRevision, string id, string runId)
    {
        if (snapshot.Revision <= initialRevision) return false;
        var item = snapshot.Items.SingleOrDefault(item => item.Identity == id);
        if (item is null) return false;
        bool Field(string name, object expected)
        {
            var field = item.Fields.SingleOrDefault(field => field.Path == name);
            if (field is null) return false;
            using var json = JsonDocument.Parse(field.Value);
            return JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(expected));
        }
        return Field("sdk", "dotnet") && Field("runId", runId) && Field("value", 5);
    }

    internal static async Task ObserveAsync<T>(Func<CancellationToken, Task<T>> read, Func<bool> matches, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // WaitAsync also bounds a broken consumer that ignores cancellation.
            var result = await read(deadline.Token).WaitAsync(deadline.Token);
            if (result is not null && matches()) return;
        }
    }

    internal static async Task CancellationAsync<T>(Func<CancellationToken, Task<T>> read, TimeSpan timeout)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await read(cancelled.Token).WaitAsync(timeout); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("reactive cancellation was ignored");
    }

    internal static async Task CleanupAsync(IEnumerable<string> ids, Func<string, Task> delete)
    {
        var failures = new List<Exception>();
        foreach (var id in ids)
            try { await delete(id); }
            catch (Exception ex) { failures.Add(new InvalidOperationException($"cleanup {id}: {ex.GetType().Name}")); }
        if (failures.Count > 0) throw new AggregateException("owned cleanup failed", failures);
    }
}
