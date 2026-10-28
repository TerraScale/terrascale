#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
// Offline control for api-auth-temporary-allocation-20261028.sh, run once against the frozen arms before any timed arm.
// 1. Each arm's own ValidatedTokens.KeyOf (reflection on its terrabase.dll) must return exactly what the baseline expression
//    returns (or throw the same exception type) for every input below: empty, ASCII, Unicode, unpaired surrogates, UTF-8
//    lengths around the candidate's 1,024-byte stack buffer, oversized inputs and the minted control tokens.
// 2. The keyed lane's token shape. VsTursoBenchmark gets its token from TestIssuer.Mint (ES256, SetDefaultTimesOnTokenCreation
//    off) with a synthetic API key's claims. This replicates that source with the arm's own IdentityModel and EntityIds; it does
//    not load the harness, so it is a source-based derivation, not a measurement of the timed token. It prints lengths and claim
//    counts only, never a token.
// Usage (outside any repository, so no Directory.Build.* applies): dotnet run api-auth-token-key-control.cs -- <before> <after>
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: api-auth-token-key-control.cs <before arm dir> <after arm dir>");
    return 2;
}

var arms = args.Select(dir => new Arm(Path.GetFullPath(dir))).ToArray();
var tokens = Enumerable.Range(0, 32).Select(_ => Mint(arms[1])).ToArray();
string A(int count) => new('a', count);
var cases = new List<(string Name, string? Input)>
{
    ("null", null), ("empty", ""), ("ascii", "abc.DEF-123_xyz~"), ("unicode bmp+astral", "çãé 日本語 😀  �"),
    ("lone high surrogate", "a\uD800b"), ("lone low surrogate", "a\uDC00b"), ("trailing high surrogate", "abc\uD83D"),
    ("reversed pair", "\uDE00\uD83D"), ("ascii 1023 B", A(1023)), ("ascii 1024 B", A(1024)), ("ascii 1025 B", A(1025)),
    ("1022 + 2-byte = 1024 B", A(1022) + "é"), ("1023 + 2-byte = 1025 B", A(1023) + "é"), ("1021 + 3-byte = 1024 B", A(1021) + "日"),
    ("1022 + 3-byte = 1025 B", A(1022) + "日"), ("1020 + 4-byte = 1024 B", A(1020) + "😀"), ("1021 + 4-byte = 1025 B", A(1021) + "😀"),
    ("1021 + lone surrogate = 1024 B", A(1021) + "\uD800"), ("1022 + lone surrogate = 1025 B", A(1022) + "\uDC00"),
    ("341 x 3-byte = 1023 B", new string('日', 341)), ("342 x 3-byte = 1026 B", new string('日', 342)),
    ("oversized ascii 4096 B", A(4096)), ("oversized ascii 65536 B", A(65536)), ("oversized 3-byte 300000 B", new string('日', 100_000)),
};
cases.AddRange(tokens.Select((token, i) => ($"minted control token {i + 1}", (string?)token)));

var mismatches = 0;
Console.WriteLine("| Case | UTF-16 chars | UTF-8 bytes | baseline expression | before | after |");
Console.WriteLine("| --- | ---: | ---: | --- | --- | --- |");
foreach (var (name, input) in cases)
{
    var expected = Outcome(() => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input!))));
    var results = arms.Select(arm => Outcome(() => arm.KeyOf(input))).ToArray();
    var same = results.Select(result => result == expected).ToArray();
    mismatches += same.Count(ok => !ok);
    var shape = input is null ? "n/a | n/a" : $"{input.Length} | {Encoding.UTF8.GetByteCount(input)}";
    Console.WriteLine($"| {name} | {shape} | {(expected.StartsWith("throws", StringComparison.Ordinal) ? expected : "key")} | " +
        $"{(same[0] ? "same" : "DIFFERENT")} | {(same[1] ? "same" : "DIFFERENT")} |");
}

Console.WriteLine();
Console.WriteLine($"Key equivalence: {(mismatches == 0 ? "PASS" : $"FAIL ({mismatches} differing outcomes)")}");
Console.WriteLine();
Console.WriteLine($"Control token shape over {tokens.Length} mints (source-replicated, UNVERIFIED against the timed token):");
Console.WriteLine($"- UTF-8 bytes: {string.Join(", ", tokens.Select(t => Encoding.UTF8.GetByteCount(t)).Distinct().Order())}");
Console.WriteLine($"- UTF-16 chars: {string.Join(", ", tokens.Select(t => t.Length).Distinct().Order())}");
Console.WriteLine($"- segments: {string.Join(", ", tokens.Select(t => t.Split('.').Length).Distinct())}; header members: " +
    $"{string.Join(", ", tokens.Select(t => Members(t, 0)).Distinct())}; payload claims: {string.Join(", ", tokens.Select(t => Members(t, 1)).Distinct())}");
return mismatches == 0 ? 0 : 1;

static string Outcome(Func<string> key)
{
    try
    {
        return key();
    }
    catch (Exception error)
    {
        return "throws " + ((error as TargetInvocationException)?.InnerException ?? error).GetType().FullName;
    }
}

static int Members(string token, int segment)
{
    var part = token.Split('.')[segment].Replace('-', '+').Replace('_', '/');
    using var json = JsonDocument.Parse(Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=')));
    return json.RootElement.EnumerateObject().Count();
}

// TestIssuer.Mint and AddKey(ES256) with VsTursoBenchmark's descriptor edit, from the arm's own assemblies.
static string Mint(Arm arm)
{
    var keyId = arm.NewId();
    var key = keyId + "_" + RandomNumberGenerator.GetString("0123456789abcdefghjkmnpqrstvwxyz", 52);
    var now = DateTime.UtcNow;
    dynamic signingKey = Activator.CreateInstance(arm.Find("Microsoft.IdentityModel.Tokens", "Microsoft.IdentityModel.Tokens.ECDsaSecurityKey"),
        ECDsa.Create(ECCurve.NamedCurves.nistP256))!;
    signingKey.KeyId = arm.NewId();
    var claims = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["sub"] = keyId,
        ["project_id"] = arm.NewId(),
        ["scope"] = "databases:read databases:write documents:read documents:write",
        ["key_id"] = keyId,
        ["key_gen"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16],
    };
    claims.TryAdd("org_id", arm.NewId());
    dynamic descriptor = Activator.CreateInstance(arm.Find("Microsoft.IdentityModel.Tokens", "Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor"))!;
    descriptor.TokenType = "at+jwt";
    descriptor.Issuer = "https://auth.test/terrascale";
    descriptor.Audience = "https://terrabase.test";
    descriptor.IssuedAt = now;
    descriptor.NotBefore = now.AddMinutes(-1);
    descriptor.Expires = DateTime.UtcNow.AddHours(12);
    descriptor.Claims = claims;
    descriptor.SigningCredentials = (dynamic?)Activator.CreateInstance(
        arm.Find("Microsoft.IdentityModel.Tokens", "Microsoft.IdentityModel.Tokens.SigningCredentials"), (object)signingKey, "ES256");
    dynamic handler = Activator.CreateInstance(arm.Find("Microsoft.IdentityModel.JsonWebTokens", "Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler"))!;
    handler.SetDefaultTimesOnTokenCreation = false;
    return (string)handler.CreateToken(descriptor);
}

/// <summary>One arm directory in its own load context: its own terrabase.dll and the package assemblies beside it.</summary>
sealed class Arm : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly MethodInfo _keyOf;
    private readonly MethodInfo _newId;

    public Arm(string directory) : base(directory)
    {
        _directory = directory;
        const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        _keyOf = Find("terrabase", "TerraBase.Features.UserAuth.ValidatedTokens").GetMethod("KeyOf", Static, [typeof(string)])!;
        _newId = Find("terrabase", "TerraBase.Primitives.EntityIds").GetMethod("New", Static)!;
    }

    public string KeyOf(string? token) => (string)_keyOf.Invoke(null, [token])!;

    public string NewId() => _newId.Invoke(null, [null])!.ToString()!;

    public Type Find(string assembly, string name) => LoadFromAssemblyName(new AssemblyName(assembly)).GetType(name, throwOnError: true)!;

    protected override Assembly? Load(AssemblyName name) =>
        File.Exists(Path.Combine(_directory, name.Name + ".dll")) ? LoadFromAssemblyPath(Path.Combine(_directory, name.Name + ".dll")) : null;
}
