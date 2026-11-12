set -euo pipefail
tools=${E2E_TOOLS:?E2E_TOOLS is the directory that caches the pinned tools}
wanted=("$@")
mkdir -p "$tools"
want() {
  [ ${#wanted[@]} -eq 0 ] || [[ " ${wanted[*]} " == *" $1 "* ]]
}
fetch() {
  local name=$1 url=$2 sum=$3 member=${4:-$1}
  want "$name" || return 0
  local archive=$tools/${name}-${url##*/}
  if [ ! -f "$archive" ] || ! printf '%s  %s\n' "$sum" "$archive" | sha256sum --check --status; then
    curl -fsSL -o "$archive.part" "$url"
    printf '%s  %s\n' "$sum" "$archive.part" | sha256sum --check --quiet
    mv "$archive.part" "$archive"
  fi
  tar -xzf "$archive" -C "$tools" --transform='s|.*/||' "$member"
}
fetch caddy https://github.com/caddyserver/caddy/releases/download/v2.11.9/caddy_2.11.9_linux_amd64.tar.gz 5b8cb7faf8726a2028ad96a66e54e6e81e8f361bc6175e5d22bbd997cc5177fb
fetch mailpit https://github.com/axllent/mailpit/releases/download/v1.33.1/mailpit-linux-amd64.tar.gz 84d7fe375ebef25df26758984dc0d5d5e17d8c0734cb2e77be7873b14ab51716
fetch uv https://github.com/astral-sh/uv/releases/download/0.13.7/uv-x86_64-unknown-linux-gnu.tar.gz c5221ff41b7892bfc223543883600125f6a47fe845093e0f5bdff4f359196d83 uv-x86_64-unknown-linux-gnu/uv
branch_action=97cb98375e0c9c4881be520432799b6ffbae6884
for script in install-ts.sh branch.sh; do
  fetch branch-action "https://github.com/TerraScale/branch-action/archive/$branch_action.tar.gz" \
    277518ac3558626462f11736626be72e8d607afb6ece7560b6f00342b593dcf5 "branch-action-$branch_action/$script"
done
if want branch-action; then
  cli_version=0.0.58
  cli_platform=linux_amd64
  cli_sum=8cb62b2f6bf609fa458d8fa9a640c50cca84a11119bdb4da08ce838ea9eb8cdf
  cli_archive=$tools/ts-$cli_version-$cli_platform.tar.gz
  cli_home=$tools/ts
  if [ ! -f "$cli_archive" ] || ! printf '%s  %s\n' "$cli_sum" "$cli_archive" | sha256sum --check --status; then
    echo "downloading ts $cli_version ($cli_platform)" >&2
    curl --fail --silent --show-error --location --retry 2 \
      "https://github.com/TerraScale/terrascale/releases/download/sdk/go/v$cli_version/ts_$cli_platform.tar.gz" \
      --output "$cli_archive.part"
    printf '%s  %s\n' "$cli_sum" "$cli_archive.part" | sha256sum --check --quiet
    mv "$cli_archive.part" "$cli_archive"
  else
    echo "reusing ts $cli_version ($cli_platform): checksum matches, no download" >&2
  fi
  mkdir -p "$cli_home/.local/bin"
  tar -xzf "$cli_archive" -C "$cli_home/.local/bin" ts
  installed=$("$cli_home/.local/bin/ts" --version)
  [ "$installed" = "ts $cli_version" ] || { echo "ts_$cli_platform.tar.gz holds $installed, not ts $cli_version" >&2; exit 1; }
fi
