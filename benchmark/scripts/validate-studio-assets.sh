#!/usr/bin/env bash
set -euo pipefail

repo_root=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
asset_root=$repo_root/crates/terrabase-studio/static

# The Studio binary embeds these files. Keep the browser surface reproducible
# and fail closed if a vendored file is changed without updating its recorded
# provenance in static/VENDORED_ASSETS.md.
declare -A expected=(
  [basecoat.css]=7159a2e4e182e373cf093a82debb6cb3fa5f1c97dcdffbd01df3804a5b4a2de2
  [datastar-1.0.3.js]=5d6b7794a50a83d82da962aec5e382f5ae83ac7afbc751f903f7a9c6bd433c65
  [studio-monaco.js]=298fa053283ed077fff5fd545f12cf4678d2138b25eef6b7a0a9acde6c2b5d58
  [LICENSE.basecoat.md]=0fc06a4298f05b38473b01c86c978861c7ab01c2ceaebeeb9c3cc67b7a2b77ef
  [LICENSE.datastar.md]=d3744837a0204e16cce7dfbcc77a1b72f302a74d10089a4dd552147582151cb0
  [monaco/vs/loader.js]=28f3584fd04b182dfce15a9a1ce35b25bea22b31464aee500372bed18b7fee1a
  [monaco/vs/editor/editor.main.js]=90b588bc0b624e24052a576e1bcab2eaffec7bc666895188862eebd9c9745782
  [monaco/vs/editor/editor.main.css]=857bc0beafefbcc83ab5cf3510e929391c0314add43ee1a4cb8ecaf4726abbac
  [monaco/vs/base/worker/workerMain.js]=dbe4aa4f2874f5768e8d98e94281667c0bc493e02dd9760adca930699ff3279b
  [monaco/vs/language/json/jsonMode.js]=9200ffce4ac390675f6cb4c64ffaf4b4d8e650d3888caf631709cb136615e379
  [monaco/vs/language/json/jsonWorker.js]=4b197c4165e356a04857e9317ec3d536fdbb92b91077bc483590c4f51056e3df
  [monaco/vs/base/browser/ui/codicons/codicon/codicon.ttf]=0f1d5219934e96e83b8db162d60b4d8c09b5de1e7d38031cbafe4a3c0f2889c9
  [monaco/LICENSE]=33e4ff1a06ef62ba21788ea162564ee8165269a24a9ce6ef301837447eab0ac6
  [monaco/ThirdPartyNotices.txt]=790537262fc78a764e121e6b92b959bcd3f5c310b47d9d9b9e92e17fe0af5336
)

for relative_path in "${!expected[@]}"; do
  path=$asset_root/$relative_path
  [[ -f "$path" && ! -L "$path" ]] || {
    echo "missing or unsafe Studio asset: $relative_path" >&2
    exit 1
  }
  observed=$(sha256sum "$path" | awk '{print $1}')
  [[ "$observed" == "${expected[$relative_path]}" ]] || {
    echo "Studio asset checksum mismatch: $relative_path" >&2
    exit 1
  }
done

grep -Fq 'Vendored browser assets' "$asset_root/VENDORED_ASSETS.md"
echo "validated pinned Studio browser assets"
