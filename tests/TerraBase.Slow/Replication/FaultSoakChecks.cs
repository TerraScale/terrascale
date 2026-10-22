using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.Slow.Replication;

internal static class FaultSoakChecks
{
    public static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> check, string reason, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!await check())
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException(reason);
            }

            await Task.Delay(20, token);
        }
    }

    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(member => member.Key, StringComparer.Ordinal)
            .Select(member => JsonSerializer.Serialize(member.Key) + ":" + Canonical(member.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null",
    };

    public static string Digest(IReadOnlyDictionary<string, string> documents) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', documents.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "\t" + item.Value)))));

    public static async Task<JsonNode?> ReadAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var answer = await client.GetAsync(uri, token);
        if (answer.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            await RefusedAsync(answer, token);
            return null;
        }

        if (answer.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await SuccessAsync(answer, token);
        return JsonNode.Parse(await answer.Content.ReadAsStringAsync(token))!["document"];
    }

    public static async Task<Dictionary<string, string>?> ListAsync(HttpClient client, string database, string collection, CancellationToken token)
    {
        using var answer = await client.GetAsync(new Uri($"{database}/collections/{collection}/documents?limit=1000", UriKind.Relative), token);
        if (answer.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            await RefusedAsync(answer, token);
            return null;
        }

        await SuccessAsync(answer, token);
        var body = JsonNode.Parse(await answer.Content.ReadAsStringAsync(token))!;
        Check(body["next_cursor"] is null, "soak collection exceeded its bounded single page");
        return body["items"]!.AsArray().ToDictionary(item => item!["id"]!.GetValue<string>(), item => Canonical(item!["document"]), StringComparer.Ordinal);
    }

    public static async Task RefusedAsync(HttpResponseMessage answer, CancellationToken token)
    {
        var body = await answer.Content.ReadAsStringAsync(token);
        var code = JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>();
        Check(answer.StatusCode == HttpStatusCode.ServiceUnavailable && code is
            "remote_region_unavailable" or "confirmation_busy" or "write_outcome_unknown" or "catalog_unverified" or "catalog_anchor_unavailable",
            $"unexpected refusal HTTP {(int)answer.StatusCode} code={code}");
    }

    public static async Task SuccessAsync(HttpResponseMessage answer, CancellationToken token)
    {
        Check(answer.IsSuccessStatusCode, $"HTTP {(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync(token)}");
    }

    public static async Task SetupUniqueAsync(FaultSoakCluster cluster, CancellationToken token)
    {
        using (var created = await cluster["gru"].PostAsJsonAsync(new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative), new { name = "claims" }, token))
        {
            await SuccessAsync(created, token);
        }

        using (var declared = await cluster.Management("gru").PutAsJsonAsync(new Uri($"{cluster.DatabasePath}/schema/claims", UriKind.Relative),
            new { fields = new[] { new { name = "email", unique = true } } }, token))
        {
            await SuccessAsync(declared, token);
        }

        await UntilAsync(async () => (await Task.WhenAll(cluster.Regions.Select(async region =>
        {
            using var answer = await cluster.Management(region).GetAsync(new Uri($"{cluster.DatabasePath}/schema/claims", UriKind.Relative), token);
            return answer.IsSuccessStatusCode && JsonNode.Parse(await answer.Content.ReadAsStringAsync(token))?["uniqueConstraints"]?.AsArray().Count == 1;
        }))).All(value => value), "unique schema replicated", token);
    }

    public static async Task<IReadOnlySet<string>> ClaimAsync(FaultSoakCluster cluster, int seed, ConcurrentQueue<string> history, CancellationToken token)
    {
        var accepted = new ConcurrentBag<string>();
        await Task.WhenAll(cluster.Regions.Select(async (region, index) =>
        {
            var id = Id(seed, 100 + index);
            using var answer = await cluster[region].PutAsJsonAsync(cluster.Document(id, "claims"), new { email = "same@example.test", from = region }, token);
            history.Enqueue($"claim {region} {id} {(int)answer.StatusCode}");
            if (answer.IsSuccessStatusCode)
            {
                accepted.Add(id);
            }
            else if (answer.StatusCode == HttpStatusCode.Conflict)
            {
                var body = await answer.Content.ReadAsStringAsync(token);
                Check(JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() == "unique_violation", "unexpected unique claim refusal: " + body);
            }
            else
            {
                await RefusedAsync(answer, token);
            }
        }));
        Check(!accepted.IsEmpty, "no unique claim was acknowledged under faults");
        return accepted.ToHashSet(StringComparer.Ordinal);
    }

    public static Task VerifyUniqueAsync(FaultSoakCluster cluster, int seed, IReadOnlySet<string> acknowledged, CancellationToken token) => UntilAsync(async () =>
    {
        var copies = await Task.WhenAll(cluster.Regions.Select(region => ListAsync(cluster[region], cluster.DatabasePath, "claims", token)));
        if (!copies.All(copy => copy?.Count == 1) || copies.Select(copy => Digest(copy!)).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            return false;
        }

        var winner = copies[0]!.Keys.Single();
        var winnerIndex = Array.IndexOf<string>([.. Enumerable.Range(0, cluster.Regions.Length).Select(index => Id(seed, 100 + index))], winner);
        if (winnerIndex < 0 || copies[0]![winner] != Canonical(new JsonObject { ["email"] = "same@example.test", ["from"] = cluster.Regions[winnerIndex] }))
        {
            return false;
        }

        return (await Task.WhenAll(cluster.Regions.Select(async region =>
        {
            foreach (var id in Enumerable.Range(0, 3).Select(index => Id(seed, 100 + index)))
            {
                using var answer = await cluster[region].GetAsync(cluster.Document(id, "claims"), token);
                if (id == winner)
                {
                    if (answer.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }
                }
                else if (answer.StatusCode == HttpStatusCode.Conflict)
                {
                    var body = JsonNode.Parse(await answer.Content.ReadAsStringAsync(token));
                    if (body?["error"]?["code"]?.GetValue<string>() != "unique_violation"
                        || StringComparer.Ordinal.Compare(id, winner) < 0)
                    {
                        return false;
                    }
                }
                else if (answer.StatusCode != HttpStatusCode.NotFound || acknowledged.Contains(id))
                {
                    return false;
                }
            }

            return true;
        }))).All(value => value);
    }, "exactly one identical unique claim winner per replica", token);

    public static string Id(int seed, int index)
    {
        var bytes = new byte[10];
        new Random(seed ^ 0x38c2).NextBytes(bytes);
        return Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddMilliseconds(index + 1), bytes).ToString();
    }
}
