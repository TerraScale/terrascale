#!/usr/bin/env bash
# Proves the RS0030 banned-API rules still reject authored dashboard code. A temporary probe source file under
# src/TerraScale.Dashboard uses BadHttpRequestException and Newtonsoft.Json; the Release build must fail with RS0030
# at both probe lines. Run on a dev host through ts-heavy-any from a clean, committed checkout.
set -euo pipefail

case "$(uname -n)" in
  mariogk-desktop | mariogk-desktop.*)
    echo "refusing to run on mariogk-desktop; use a dev host through ts-heavy-any" >&2
    exit 2
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

project="src/TerraScale.Dashboard/TerraScale.Dashboard.csproj"
probe_dir="src/TerraScale.Dashboard"
probe_path=""
log_file=""

cleanup() {
  if [[ -n "$probe_path" ]]; then
    rm -f -- "$probe_path"
  fi
  if [[ -n "$log_file" ]]; then
    rm -f -- "$log_file"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
  echo "checkout must be clean and committed before the banned-API probe runs" >&2
  exit 2
fi

dotnet restore "$project" --locked-mode --verbosity quiet

probe_path="$(mktemp --suffix=.cs "$probe_dir/BannedApiProbe_XXXXXXXXXXXX")"
probe_class="$(basename "$probe_path" .cs)"
cat >"$probe_path" <<EOF
namespace TerraScale.Dashboard.BannedApiProbes;

internal static class ${probe_class}
{
    internal static string Use()
    {
        var exception = new global::Microsoft.AspNetCore.Http.BadHttpRequestException("probe");
        var json = global::Newtonsoft.Json.JsonConvert.SerializeObject(1);
        return exception.Message + json;
    }
}
EOF

exception_line="$(grep -nF 'BadHttpRequestException("probe")' "$probe_path" | cut -d: -f1)"
json_line="$(grep -nF 'SerializeObject(1)' "$probe_path" | cut -d: -f1)"

log_file="$(mktemp)"
set +e
dotnet build "$project" --configuration Release --no-restore -p:UseSharedCompilation=false --nologo --verbosity quiet \
  >"$log_file" 2>&1
build_status=$?
set -e

if [[ "$build_status" -eq 0 ]]; then
  echo "FAILED: probe build succeeded, so banned authored APIs were not rejected" >&2
  exit 1
fi

# Matches one diagnostic at the probe file and line; the log is never printed.
rs0030_at() {
  grep -F -- "${probe_path}($1," "$log_file" | grep -F -- ': error RS0030:' >/dev/null
}

report_probe_codes() {
  echo "probe diagnostic codes (no messages):" >&2
  grep -F -- "${probe_path}(" "$log_file" | grep -oE 'error [A-Z]+[0-9]+' | sort | uniq -c >&2 || true
}

if ! rs0030_at "$exception_line"; then
  report_probe_codes
  echo "FAILED: no RS0030 for BadHttpRequestException at ${probe_path}:${exception_line}" >&2
  exit 1
fi

if ! rs0030_at "$json_line"; then
  report_probe_codes
  echo "FAILED: no RS0030 for Newtonsoft.Json at ${probe_path}:${json_line}" >&2
  exit 1
fi

echo "PASS: build rejected authored probe with RS0030 for BadHttpRequestException (${probe_path}:${exception_line}) and Newtonsoft.Json (${probe_path}:${json_line})"
