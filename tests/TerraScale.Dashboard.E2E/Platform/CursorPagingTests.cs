using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Platform.Http;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class CursorPagingTests
{
    [Test]
    public async Task TraversalUsesStableIdsDespiteRenameDeleteInsertionAndPageSizeChanges()
    {
        var rows = new[] { (Id: "c", Name: "A"), (Id: "a", Name: "Z"), (Id: "b", Name: "Z") };
        var first = CursorPaging.Page(rows, row => row.Id, "scope", 1);
        var next = CursorPaging.Page(rows.Where(row => row.Id != "a").Append((Id: "d", Name: "A")).Select(row => (row.Id, Name: "Renamed")), row => row.Id, "scope", 2, first.NextCursor!);
        var last = CursorPaging.Page(rows.Append((Id: "d", Name: "A")), row => row.Id, "scope", 1, next.NextCursor!);
        await Assert.That(first.Items.Select(row => row.Id)).IsEquivalentTo(new[] { "a" });
        await Assert.That(next.Items.Select(row => row.Id)).IsEquivalentTo(new[] { "b", "c" });
        await Assert.That(last.Items.Single().Id).IsEqualTo("d");
        await Assert.That(last.HasMore).IsFalse();
        await Assert.That(last.NextCursor).IsNull();
        await Assert.That(CursorPaging.Page(Array.Empty<string>(), id => id, "scope").HasMore).IsFalse();
    }

    [Test]
    public async Task RejectsCrossScopeAndMalformedCursorsAndInvalidOrRepeatedLimits()
    {
        var cursor = CursorPaging.Page(new[] { "a", "b" }, id => id, "scope", 1).NextCursor!;
        foreach (var after in new[] { cursor.Replace("scope", "other", StringComparison.Ordinal), "garbage", "1.scope.!!", "1.scope." })
        {
            AccountAccessException? failure = null;
            try { CursorPaging.Page(new[] { "a", "b" }, id => id, "scope", 1, after); }
            catch (AccountAccessException error) { failure = error; }
            await Assert.That(failure?.StatusCode).IsEqualTo(422);
        }
        foreach (var query in new[] { "?limit=0", "?limit=101", "?limit=-1", "?limit=no", "?limit=1&limit=2", "?after=a&after=b" })
        {
            var http = new DefaultHttpContext();
            http.Request.QueryString = new(query);
            AccountAccessException? failure = null;
            try { CursorPaging.Read(http.Request); }
            catch (AccountAccessException error) { failure = error; }
            await Assert.That(failure?.StatusCode).IsEqualTo(422);
        }
    }
}
