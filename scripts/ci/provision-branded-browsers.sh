#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
browser_paths="$(mktemp "${RUNNER_TEMP:-/tmp}/terrascale-branded-browsers.XXXXXX")"
# Use actual vendor browsers and their Nix runtime closures. The revision and
# NAR digest bind the definitions and upstream vendor archive hashes together.
# fetchTarball is fixed-output, so with a cache the source substitutes by its
# NAR digest instead of being downloaded and unpacked again.
nixpkgs='builtins.fetchTarball {
  url = "https://github.com/NixOS/nixpkgs/archive/b4fd65b198c599cbe814fcb9f42d25d021595ec9.tar.gz";
  sha256 = "sha256-ilerN1WLSvF+HMjziC/Wv99J5y02maDH+6hZPwsORKg=";
}'
# TS_NIX_CACHE names a binary cache that persists between jobs (CI: the
# runner's shared /cache volume). The pinned closures substitute from it
# instead of being downloaded from upstream and rebuilt in every job.
substituters=()
if [ -n "${TS_NIX_CACHE:-}" ]; then
  substituters=(--extra-substituters "${TS_NIX_CACHE}?trusted=1&priority=10")
fi
nix build "${substituters[@]}" --max-jobs 1 --cores 2 --no-link --print-out-paths --expr "
  let
    pkgs = import (${nixpkgs}) {
      system = \"x86_64-linux\";
      config.allowUnfree = true;
    };
  in [ pkgs.google-chrome pkgs.microsoft-edge ]
" > "${browser_paths}"
if [ -n "${TS_NIX_CACHE:-}" ]; then
  # Keep the evaluated nixpkgs source and both runtime closures in the cache;
  # paths it already holds are skipped.
  # shellcheck disable=SC2046 # one store path per word
  nix copy --to "${TS_NIX_CACHE}?compression=zstd&parallel-compression=true" \
    "$(nix eval --raw --expr "${nixpkgs}")" \
    $(nix-store --query --requisites $(cat "${browser_paths}"))
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
