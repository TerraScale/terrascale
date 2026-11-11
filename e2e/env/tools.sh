set -euo pipefail
tools=${E2E_TOOLS:?E2E_TOOLS is the directory that caches the pinned tools}
mkdir -p "$tools"
fetch() {
  local name=$1 url=$2 sum=$3 member=${4:-$1}
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
