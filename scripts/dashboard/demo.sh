#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# The E2E project's synthetic host (tests/TerraScale.Dashboard.E2E/Demo). Its port is the
# worktree development port + 2000; all interfaces admit the Tailscale network.
# .NET 11 file-app watch cannot reload its transient project after new files
# appear. Materialize a launcher for the demo host outside the repo for a stable watch
# graph; the solution still contains exactly one committed application project.
# This machine's tailnet name, so the demo is reachable over Tailscale.
dashboard_demo_host=$(tailscale status --json | python3 -c 'import json, sys; name = json.load(sys.stdin)["Self"]["DNSName"].rstrip("."); assert name, "tailscale is not logged in"; print(name)')
dashboard_demo_dir=$(mktemp -d)
python3 - "$PWD" "$dashboard_demo_dir" <<'PY'
from pathlib import Path
from xml.sax.saxutils import quoteattr
import sys
root, target = map(Path, sys.argv[1:])
(target / 'Program.cs').write_text('''using System.Security.Cryptography;
using TerraScale.Dashboard.E2E.Demo;
using System.Text;
var root = args[0];
var host = args[1];
var port = 7500 + (int)(Convert.ToUInt32(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..8], 16) % 1000);
var environment = new DemoEnvironment();
await environment.InitializeAsync();
await using var app = environment.CreateHost(Path.Combine(root, "src", "TerraScale.Dashboard"), port, $"http://{host}:{port}");
Console.WriteLine($"Test-only demo: http://{host}:{port}; signs in as user@example.com");
await app.RunAsync();
''')
(target / 'DemoHost.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PublishAot>false</PublishAot></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include={quoteattr(str(root / 'tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj'))} />
  </ItemGroup>
</Project>
''')
(target / 'global.json').write_bytes((root / 'global.json').read_bytes())
config = (root / 'NuGet.config').read_text()
(target / 'NuGet.Config').write_text(config)
PY
node src/TerraScale.Dashboard/web/build/build-css.mjs --watch --minify &
dashboard_demo_css_pid=$!
node src/TerraScale.Dashboard/web/build/build-js.mjs --watch --minify &
dashboard_demo_js_pid=$!
setsid dotnet watch --project "$dashboard_demo_dir/DemoHost.csproj" --non-interactive --no-launch-profile -- "$PWD" "$dashboard_demo_host" &
dashboard_demo_host_pid=$!
cleanup() {
  trap - EXIT INT TERM
  kill -TERM -- "-$dashboard_demo_host_pid" "$dashboard_demo_css_pid" "$dashboard_demo_js_pid" 2>/dev/null || true
  sleep 0.5
  kill -KILL -- "-$dashboard_demo_host_pid" 2>/dev/null || true
  wait "$dashboard_demo_host_pid" "$dashboard_demo_css_pid" "$dashboard_demo_js_pid" 2>/dev/null || true
  rm -rf -- "$dashboard_demo_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
wait "$dashboard_demo_host_pid"
