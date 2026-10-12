using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

public enum AuthBudgetKind { SignIn, Mail, SignUpMail }
public interface IAuthAttemptBudget
{
    Task ReserveAsync(HttpContext http, string account, AuthBudgetKind kind, CancellationToken cancellationToken);
    Task ForgiveAsync(HttpContext http, string account, CancellationToken cancellationToken);
}

// A bounded CAS document makes IP/account reservations atomic on every replica.
// Budgets use the original Go GCRA limits; spent reservations expire naturally.
public sealed class AuthAttemptBudget(IDashboardState state, TimeProvider time) : IAuthAttemptBudget
{
    private const string Collection = "login_throttle";
    private const string Document = "budgets";
    private const int MaxEntries = 8192;
    private const int MaxDocumentBytes = 1024 * 1024;

    public async Task ReserveAsync(HttpContext http, string account, AuthBudgetKind kind, CancellationToken cancellationToken)
    {
        var prefix = kind == AuthBudgetKind.SignUpMail ? "signup-mail " : "";
        var keys = new List<(string Key, long Interval, int Burst)> { (ClientKey(http, prefix), 12000, 10) };
        if (account.Length != 0)
            keys.Add((DeviceGrants.Hash(prefix + (kind == AuthBudgetKind.Mail ? "mail " : "") + account.Trim().ToLowerInvariant()), kind == AuthBudgetKind.SignUpMail ? 60000 : 30000, kind == AuthBudgetKind.SignUpMail ? 1 : 10));
        try
        {
            await UpdateAsync(due =>
            {
                var now = time.GetUtcNow().ToUnixTimeMilliseconds();
                var wait = keys.Max(bucket => Math.Max(0, due.GetValueOrDefault(bucket.Key) - (bucket.Burst - 1) * bucket.Interval - now));
                if (wait > 0) throw Rejected((int)Math.Ceiling(wait / 1000d));
                if (due.Count + keys.Count(bucket => !due.ContainsKey(bucket.Key)) > MaxEntries) throw Rejected(12);
                foreach (var bucket in keys) due[bucket.Key] = Math.Max(now, due.GetValueOrDefault(bucket.Key)) + bucket.Interval;
            }, cancellationToken);
        }
        catch (Exception error) when (error is not TerraBaseNotFoundException { ErrorCode: "unknown_collection" or "unknown_database" } &&
                                     error is HttpRequestException or IOException or JsonException or TimeoutException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthAttemptBudget").LogWarning("Sign-in budget unavailable ({ErrorType})", error.GetType().Name);
            throw Rejected(12);
        }
    }

    public async Task ForgiveAsync(HttpContext http, string account, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateAsync(due =>
            {
                var client = ClientKey(http, "");
                if (due.TryGetValue(client, out var value)) due[client] = Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), value - 12000);
                if (account.Length != 0) due.Remove(DeviceGrants.Hash(account.Trim().ToLowerInvariant()));
            }, cancellationToken);
        }
        catch (Exception error) when (error is not TerraBaseNotFoundException { ErrorCode: "unknown_collection" or "unknown_database" } &&
                                     error is HttpRequestException or IOException or JsonException or TimeoutException)
        { http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthAttemptBudget").LogWarning("Sign-in budget forgiveness unavailable ({ErrorType})", error.GetType().Name); }
    }

    private async Task UpdateAsync(Action<Dictionary<string, long>> update, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(Collection, Document, cancellationToken);
            if (document is not null && document.State != "value") throw new IOException("Sign-in budget has a causal conflict.");
            if (document is not null && System.Text.Encoding.UTF8.GetByteCount(document.Document.GetRawText()) > MaxDocumentBytes)
                throw new IOException("Sign-in budget is too large.");
            var due = document is null ? new Dictionary<string, long>(StringComparer.Ordinal) : document.Document.Deserialize<Dictionary<string, long>>() ?? throw new IOException("Sign-in budget is invalid.");
            if (due.Count > MaxEntries || due.Any(item => item.Key.Length != 64 || !item.Key.All(char.IsAsciiHexDigitLower) || item.Value < 0 || item.Value > time.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds()))
                throw new IOException("Sign-in budget is invalid.");
            foreach (var expired in due.Where(item => item.Value <= time.GetUtcNow().ToUnixTimeMilliseconds()).Select(item => item.Key).ToArray()) due.Remove(expired);
            update(due);
            var value = JsonSerializer.SerializeToElement(due);
            if (System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxDocumentBytes) throw new IOException("Sign-in budget is full.");
            try
            {
                await state.PutAsync(Collection, Document, value, document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (HttpRequestException error) when (attempt < 7 && error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) { }
        }
    }

    private static AuthRequestRejectedException Rejected(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 300);
        return new($"Too many sign-in attempts. Try again in {seconds} {(seconds == 1 ? "second" : "seconds")}.", statusCode: 429, retryAfter: seconds);
    }

    private static string ClientKey(HttpContext http, string prefix)
    {
        var client = EnrollmentEndpoints.ClientIp(http);
        if (IPAddress.TryParse(client, out var address))
        {
            address = address.MapToIPv6().IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            { var bytes = address.GetAddressBytes(); Array.Clear(bytes, 8, 8); client = new IPAddress(bytes).ToString() + "/64"; }
            else client = address.ToString();
        }
        return DeviceGrants.Hash(prefix + "client " + client);
    }
}
