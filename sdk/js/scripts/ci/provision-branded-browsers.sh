#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
browser_paths="$(mktemp "${RUNNER_TEMP:-/tmp}/terrascale-branded-browsers.XXXXXX")"
trap 'rm -f -- "$browser_paths"' EXIT
script_dir=$(cd "$(dirname "$0")" && pwd)
# Use actual vendor browsers and their Nix runtime closures. The revision and
# NAR digest bind the definitions and upstream vendor archive hashes together.
# fetchTarball is fixed-output, so with a cache the source substitutes by its
# NAR digest instead of being downloaded and unpacked again.
# TS_NIX_CACHE persists fixed-output source inputs between jobs. Require the
# runner's configured signatures for input-addressed paths. Browser closures
# remain in the persistent /nix store; this job has no cache signing key, so
# it must not publish unsigned closures for future signature-enforcing jobs.
substituters=()
if [ -n "${TS_NIX_CACHE:-}" ]; then
  substituters=(--extra-substituters "${TS_NIX_CACHE}?priority=10")
fi
nix build "${substituters[@]}" --option require-sigs true --max-jobs 1 --cores 2 \
  --no-link --print-out-paths --file "$script_dir/branded-browsers.nix" > "$browser_paths"
if [ -n "${TS_NIX_CACHE:-}" ]; then
  # The nixpkgs source is fixed-output and needs no private signing key.
  if ! nix copy --to "${TS_NIX_CACHE}?compression=zstd&parallel-compression=true" \
    "$(nix eval --raw --file "$script_dir/nixpkgs-pin.nix")"; then
    echo "warning: could not populate the optional browser cache" >&2
  fi
fi

chrome=""
edge=""
while IFS= read -r browser_root; do
  if [ -x "${browser_root}/bin/google-chrome-stable" ]; then
    chrome="${browser_root}/bin/google-chrome-stable"
  elif [ -x "${browser_root}/bin/microsoft-edge" ]; then
    edge="${browser_root}/bin/microsoft-edge"
  else
    echo "Pinned browser output lacks an expected vendor executable." >&2
    exit 1
  fi
done < "${browser_paths}"
test -n "${chrome}" && test -n "${edge}"

# Image tools require /lib, while vendor closures need their own libraries.
# Keep this adjustment on vendor children for both probes and Playwright.
browser_launchers="$(mktemp -d "${RUNNER_TEMP:-/tmp}/terrascale-branded-browser-launchers.XXXXXX")"
create_vendor_launcher() {
  local executable="$1"
  local launcher="$2"
  printf '#!%s\nunset LD_LIBRARY_PATH LD_PRELOAD\nexec %q "$@"\n' \
    "${BASH}" "${executable}" > "${launcher}"
  chmod 0700 "${launcher}"
}
create_vendor_launcher "${chrome}" "${browser_launchers}/google-chrome-stable"
create_vendor_launcher "${edge}" "${browser_launchers}/microsoft-edge"
chrome="${browser_launchers}/google-chrome-stable"
edge="${browser_launchers}/microsoft-edge"

require_version() {
  local browser="$1"
  local observed="$2"
  local expected="$3"
  if [[ ! "${observed}" =~ ^"${expected}"[[:space:]]*$ ]]; then
    printf 'Unexpected %s version: expected "%s", observed "%s"\n' \
      "${browser}" "${expected}" "${observed}" >&2
    return 1
  fi
}
chrome_version="$("${chrome}" --version)"
edge_version="$("${edge}" --version)"
require_version Chrome "${chrome_version}" "Google Chrome 154.0.8037.57"
require_version Edge "${edge_version}" "Microsoft Edge 154.0.4258.37"
printf 'PLAYWRIGHT_CHROME_EXECUTABLE_PATH=%s\nPLAYWRIGHT_EDGE_EXECUTABLE_PATH=%s\n' \
  "${chrome}" "${edge}" >> "${GITHUB_ENV}"
printf '%s\n%s\n' "${chrome_version}" "${edge_version}"
