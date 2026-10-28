#!/usr/bin/env bash
# Offline configuration control; invoke through ts-heavy-any nice -n 19.
set -euo pipefail
cd -- "$(dirname -- "$0")/../clients/dotnet"
make check
probe_dir=$(mktemp -d /tmp/perf-api-raw-version-probe-XXXXXXXX)
trap 'rm -rf -- "$probe_dir"' EXIT
cat > "$probe_dir/ProtocolProbe.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
XML
cat > "$probe_dir/Program.cs" <<'CS'
using System.Net;

foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
{
    foreach (var copy in new[] { false, true })
    {
        var handler = new Observer();
        using var http = new HttpClient(handler)
        {
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.terrascale.tech/");
        if (copy)
        {
            request.Version = http.DefaultRequestVersion;
            request.VersionPolicy = http.DefaultVersionPolicy;
        }
        using var response = await http.SendAsync(request);
        var expected = copy ? version : HttpVersion.Version11;
        var expectedPolicy = copy ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower;
        if (handler.Version != expected || handler.Policy != expectedPolicy)
            throw new InvalidOperationException("Unexpected request version or policy");
        Console.WriteLine($"desired={version} copy={copy} observed={handler.Version} policy={handler.Policy}");
    }
}
Console.WriteLine("Offline request configuration probe: 4/4 passed; no sockets or credentials used.");

sealed class Observer : HttpMessageHandler
{
    internal Version? Version { get; private set; }
    internal HttpVersionPolicy Policy { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Version = request.Version;
        Policy = request.VersionPolicy;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
CS
dotnet run --project "$probe_dir/ProtocolProbe.csproj" --configuration Release

