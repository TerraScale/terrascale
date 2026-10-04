#!/usr/bin/env bash
# Start the built service with throwaway Development settings on a free loopback
# port, save the OpenAPI document it serves, and stop it. The document is the
# exact contract Scalar loads at /scalar.
set -euo pipefail

test "$#" -eq 2 || { echo "usage: $0 <ts-auth.dll> <output.json>" >&2; exit 2; }
dll=$1
output=$2

log=$(mktemp)
# Only the throwaway settings below apply: inherited TS_AUTH_*, OTEL_* and
# SENTRY_* values could fail validation or send this run's telemetry somewhere,
# inherited Logging__* filters could hide the startup line read below, and
# inherited Kestrel__*, ASPNETCORE_*, DOTNET_*__* (host configuration with its
# prefix stripped), URLS or HTTP(S)_PORTS settings could add listeners beyond
# the loopback one. .NET configuration keys are case-insensitive, so the match is too.
# Names are matched case-insensitively and may contain any character (.NET also
# accepts hyphenated endpoint names and colon-separated keys), so python reads them.
# Fail closed: if the filter cannot run, the export does not start at all.
names=$(python3 -c '
import os, re
hosting = re.compile(r"^(ts_auth_|otel_|sentry_|aspnetcore_|kestrel|logging|allowedhosts)|^dotnet_.*(__|:)|^dotnet_(urls|https?_ports|environment)$|^(urls|https?_ports)$")
for name in os.environ:
    if hosting.search(name.lower()):
        print("-u"); print(name)
') || { echo "could not compute the inherited settings to remove" >&2; exit 1; }
inherited=()
if [[ -n $names ]]; then mapfile -t inherited <<<"$names"; fi
# Port 0: the kernel picks a free port at bind time, and the address is read
# back from this process's own startup log, so no other listener can answer.
env "${inherited[@]}" \
  TS_AUTH_LISTEN="http://127.0.0.1:0" \
  TS_AUTH_PUBLIC_ORIGIN="https://auth.example.invalid" \
  TS_AUTH_TERRASCALE_API_URL="http://127.0.0.1:9" \
  TS_AUTH_TERRASCALE_API_KEY="openapi-export" \
  TS_AUTH_DATABASE="openapi-export" \
  TS_AUTH_MASTER_KEY="$(head -c 32 /dev/urandom | base64)" \
  TS_AUTH_OPERATOR_KEY="openapi-export" \
  TS_AUTH_ENVIRONMENT="Development" \
  "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information" \
  "Logging__Console__LogLevel__Microsoft.Hosting.Lifetime=Information" "Logging__Console__FormatterName=simple" \
  dotnet "$dll" >"$log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; rm -f "$log"' EXIT

address=
for _ in $(seq 1 150); do
  kill -0 "$pid" 2>/dev/null || { cat "$log" >&2; echo "ts-auth exited before serving OpenAPI" >&2; exit 1; }
  address=$(sed -nE 's#.*Now listening on: (http://127\.0\.0\.1:[0-9]+).*#\1#p' "$log" | head -n 1)
  [[ -n $address ]] && break
  sleep 0.2
done
[[ -n $address ]] || { cat "$log" >&2; echo "Timed out waiting for ts-auth to listen" >&2; exit 1; }

curl -fsS --noproxy '*' --connect-timeout 2 --max-time 10 "$address/openapi/v1.json" -o "$output"
echo "Exported OpenAPI to $output"
