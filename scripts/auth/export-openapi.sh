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
# and inherited Logging__* filters could hide the startup line read below.
inherited=$(env | sed -nE 's/^((TS_AUTH|OTEL|SENTRY)_[A-Za-z0-9_]*|Logging__[A-Za-z0-9_.]*)=.*/-u \1/p' | tr '\n' ' ')
# Port 0: the kernel picks a free port at bind time, and the address is read
# back from this process's own startup log, so no other listener can answer.
# shellcheck disable=SC2086 # $inherited is a list of separate -u options
env $inherited \
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
