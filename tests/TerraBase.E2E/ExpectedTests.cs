using System.Net;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.E2E;

/// <summary>
/// Controls for <see cref="Expected"/>: a listed gap counts as an expected failure only when its scenario fails its own way
/// with the gap's signature. A 5xx, a crash, a database lock or a harness timeout fails the test even when its message
/// carries the signature.
/// </summary>
public sealed class ExpectedTests
{
    private const string Gap = "a control gap";
    private const string Signature = "the control's own failure";

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_5xx_answer_is_never_the_gap()
    {
        await RefusedAsync(() => throw new HttpRequestException($"POST /control answered 500: {Signature}", null, HttpStatusCode.InternalServerError));
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_null_reference_is_never_the_gap()
    {
        // A real one, as a harness race left it (PeerLink's closed client).
        var refused = await RefusedAsync(() => Task.FromResult(Absent()!.Length));
        await Assert.That(refused.InnerException).IsTypeOf<NullReferenceException>();
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_database_lock_is_never_the_gap()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("terrabase-expected-").FullName, "locked.db");
        TestMasterKey.Create(file);
        await using var holder = new SqliteConnection(TestMasterKey.ConnectionString(file, SqliteOpenMode.ReadWriteCreate));
        await holder.OpenAsync();
        await using (var create = holder.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (x INTEGER)";
            _ = await create.ExecuteNonQueryAsync();
        }

        await using var hold = await holder.BeginTransactionAsync();
        await using (var write = holder.CreateCommand())
        {
            write.CommandText = "INSERT INTO t VALUES (1)";
            _ = await write.ExecuteNonQueryAsync();
        }

        var refused = await RefusedAsync(async () =>
        {
            await using var other = new SqliteConnection(TestMasterKey.ConnectionString(file));
            await other.OpenAsync();
            await using var write = other.CreateCommand();
            write.CommandText = "INSERT INTO t VALUES (2)";
            _ = await write.ExecuteNonQueryAsync();
        });
        await Assert.That(refused.InnerException?.Message).Contains("database is locked");
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_convergence_without_the_signature_is_never_the_gap()
    {
        await RefusedAsync(() => throw new NotConvergedException("the database served on sjc did not converge within 60 s"));
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_harness_timeout_is_never_the_gap()
    {
        await RefusedAsync(() => throw new TimeoutException($"the gru process did not start: {Signature}"));
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task The_scenario_failing_its_own_way_is_the_gap()
    {
        foreach (var own in new Exception[]
        {
            new ScenarioException(Signature),
            new NotConvergedException($"{Signature} did not converge within 60 s"),
            new HttpRequestException($"POST /control answered 404: {Signature}", null, HttpStatusCode.NotFound),
            new HttpRequestException($"POST /control answered 501: {Signature}", null, HttpStatusCode.NotImplemented),
            new NotSupportedException("a hook not wired yet"),
        })
        {
            var failure = await Expected.FailureAsync(Gap, Signature, () => throw own);
            await Assert.That(failure).IsSameReferenceAs(own);
        }
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_passing_scenario_has_landed()
    {
        var landed = await Assert.ThrowsAsync<InvalidOperationException>(() => Expected.FailureAsync(Gap, Signature, () => Task.CompletedTask));
        await Assert.That(landed!.Message).Contains("has landed");
    }

    private static string? Absent() => null;

    private static async Task<UnexpectedFailureException> RefusedAsync(Func<Task> scenario) =>
        (await Assert.ThrowsAsync<UnexpectedFailureException>(() => Expected.FailureAsync(Gap, Signature, scenario)))!;
}
