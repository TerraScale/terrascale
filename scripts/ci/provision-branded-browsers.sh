#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
browser_paths="$(mktemp "${RUNNER_TEMP:-/tmp}/terrascale-branded-browsers.XXXXXX")"
# Use actual vendor browsers and their Nix runtime closures. The revision and
# NAR digest bind the definitions and upstream vendor archive hashes together.
nix build --max-jobs 1 --cores 2 --no-link --print-out-paths --expr '
  let
    source = builtins.fetchTree {
      type = "github";
      owner = "NixOS";
      repo = "nixpkgs";
      rev = "b4fd65b198c599cbe814fcb9f42d25d021595ec9";
      narHash = "sha256-ilerN1WLSvF+HMjziC/Wv99J5y02maDH+6hZPwsORKg=";
    };
    pkgs = import source {
      system = "x86_64-linux";
      config.allowUnfree = true;
    };
  in [ pkgs.google-chrome pkgs.microsoft-edge ]
' > "${browser_paths}"

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
test "$("${chrome}" --version)" = "Google Chrome 154.0.8037.57"
test "$("${edge}" --version)" = "Microsoft Edge 154.0.4258.37"
printf 'PLAYWRIGHT_CHROME_EXECUTABLE_PATH=%s\nPLAYWRIGHT_EDGE_EXECUTABLE_PATH=%s\n' \
  "${chrome}" "${edge}" >> "${GITHUB_ENV}"
"${chrome}" --version
"${edge}" --version
