#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 2 ]] || {
  echo "usage: $0 <x86_64-unknown-linux-gnu|aarch64-unknown-linux-gnu> <tool-directory>" >&2
  exit 2
}
target=$1
tools_root=$2
[[ "$tools_root" == /* && ! -e "$tools_root" && ! -L "$tools_root" ]]
mkdir -p "$tools_root"

readonly nixpkgs_revision=c5c4a43b0e8056328ec4529f735cabdb8f1942bb
readonly nixpkgs_nar_hash='sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei/uiU9vrMqOiwWP1Y='
readonly native_flake='github:NixOS/nixpkgs/c5c4a43b0e8056328ec4529f735cabdb8f1942bb?narHash=sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei%2FuiU9vrMqOiwWP1Y%3D'
case "$target" in
  x86_64-unknown-linux-gnu)
    nix_system=x86_64-linux
    expected_uname=x86_64
    expected_gcc=/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0
    expected_cmake=/nix/store/vrsali0sa6hwri27azgn4q0y0kmrglyi-cmake-4.1.6
    expected_ninja=/nix/store/60z9n8f7cczph0klb7ny1mikhd8m1vkl-ninja-1.13.2
    expected_perl=/nix/store/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5.42.0
    expected_go=/nix/store/3yh8g6m3balbhzxsx77jlyyx443bxqii-go-1.26.6
    expected_buildah=/nix/store/vhnmplgnlcs61j0dny36hmp4q7r2jlb4-buildah-wrapper-1.43.1
    expected_skopeo=/nix/store/jp9k9xpd8rjzfg9m8ny6h35npl50v8zl-skopeo-1.22.2
    expected_jq=/nix/store/vkjzswzi7r1ayxa0xr920bjrzmi04x7v-jq-1.8.2-bin
    expected_binutils=/nix/store/25771zf6h2dryp8d3i65gyg17mmphy9v-binutils-wrapper-2.46
    expected_gzip=/nix/store/2722ncq58c1amdv6g6nv5d1wvab30p0r-gzip-1.14
    expected_gnutar=/nix/store/l4qnlbca31jvyh0sdg5qqbqkh48z1q19-gnutar-1.35
    expected_coreutils=/nix/store/gph17gb73p6qbrqqjnypfjj5npf0ldyx-coreutils-9.11
    expected_file=/nix/store/5j5g0rkjd7vbxjpzm6ipgiw0lj0vpapk-file-5.47
    ;;
  aarch64-unknown-linux-gnu)
    nix_system=aarch64-linux
    expected_uname=aarch64
    expected_gcc=/nix/store/2w3qlfrhf8q326jsax1sxra3wx0983c9-gcc-wrapper-15.2.0
    expected_cmake=/nix/store/42sj4bmjgv5f8f0hycfvx8yvdl7ybfwb-cmake-4.1.6
    expected_ninja=/nix/store/3k3fml4xvys85pndnw1lvshiky4i58v0-ninja-1.13.2
    expected_perl=/nix/store/9kv98czb2gz54hcgjkdj25mg3yq13kvq-perl-5.42.0
    expected_go=/nix/store/863k45gpmsq0jnjf4kzr7kg6kjwyn7zl-go-1.26.6
    expected_buildah=/nix/store/88ixxl6awcvc6zxxkwha7ldaw74yhzzv-buildah-wrapper-1.43.1
    expected_skopeo=/nix/store/3b8idg9pgd5ilvhmvmgqg7mm7xmmc3nc-skopeo-1.22.2
    expected_jq=/nix/store/m176nh79cghiwkmfh6wdcpxm7fal2c6m-jq-1.8.2-bin
    expected_binutils=/nix/store/sbvrmvvx6w8p53wgqx17ymc4lprjcbns-binutils-wrapper-2.46
    expected_gzip=/nix/store/kyqyikq8i5h0sggyx0lkskq3wly7d4aw-gzip-1.14
    expected_gnutar=/nix/store/fi3ai6q21gyqhqcd47cx2il0c9b89rwm-gnutar-1.35
    expected_coreutils=/nix/store/hlc4nqfx0l06f6p7z7lmd77qcwszi7wc-coreutils-9.11
    expected_file=/nix/store/9d3cwnrqhnb9ksfzh735av05cdqhnwf2-file-5.47
    ;;
  *) exit 2 ;;
esac
[[ "$(uname -m)" == "$expected_uname" ]]

tool_specs=(
  'gcc:gcc:out'
  'cmake:cmake:out'
  'ninja:ninja:out'
  'perl:perl:out'
  'go:go:out'
  'buildah:buildah:out'
  'skopeo:skopeo:out'
  'jq:jq:bin'
  'binutils:binutils:out'
  'gzip:gzip:out'
  'gnutar:gnutar:out'
  'coreutils:coreutils:out'
  'file:file:out'
)
packages=()
for tool_spec in "${tool_specs[@]}"; do
  IFS=: read -r package attribute output <<<"$tool_spec"
  packages+=("$package")
  nix build --out-link "$tools_root/$package" \
    "$native_flake#legacyPackages.$nix_system.$attribute^$output"
  resolved=$(readlink -f "$tools_root/$package")
  [[ "$resolved" == /nix/store/* && -d "$resolved" ]]
done

gcc_root=$(readlink -f "$tools_root/gcc")
cmake_root=$(readlink -f "$tools_root/cmake")
ninja_root=$(readlink -f "$tools_root/ninja")
perl_root=$(readlink -f "$tools_root/perl")
go_root=$(readlink -f "$tools_root/go")
binutils_root=$(readlink -f "$tools_root/binutils")
[[ "$gcc_root" == "$expected_gcc" ]]
[[ "$cmake_root" == "$expected_cmake" ]]
[[ "$ninja_root" == "$expected_ninja" ]]
[[ "$perl_root" == "$expected_perl" ]]
[[ "$go_root" == "$expected_go" ]]
for package in buildah skopeo jq binutils gzip gnutar coreutils file; do
  expected_variable=expected_$package
  [[ "$(readlink -f "$tools_root/$package")" == "${!expected_variable}" ]]
done

path_entries=()
for package in "${packages[@]}"; do
  package_root=$(readlink -f "$tools_root/$package")
  [[ -d "$package_root/bin" ]] && path_entries+=("$package_root/bin")
done
[[ -n ${GITHUB_ENV-} && -n ${GITHUB_PATH-} ]] || {
  echo 'Forgejo environment files are required for native release provisioning' >&2
  exit 1
}
{
  printf 'TERRABASE_NATIVE_TOOLCHAIN_REVISION=%s\n' "$nixpkgs_revision"
  printf 'TERRABASE_NATIVE_TOOLCHAIN_NAR_HASH=%s\n' "$nixpkgs_nar_hash"
  printf 'TERRABASE_NATIVE_TOOLCHAIN_SYSTEM=%s\n' "$nix_system"
  printf 'TERRABASE_NATIVE_TOOLCHAIN_TARGET=%s\n' "$target"
  printf 'CC=%s/bin/gcc\n' "$gcc_root"
  printf 'CXX=%s/bin/g++\n' "$gcc_root"
  printf 'CMAKE=%s/bin/cmake\n' "$cmake_root"
  printf 'CMAKE_GENERATOR=Ninja\n'
  printf 'CMAKE_MAKE_PROGRAM=%s/bin/ninja\n' "$ninja_root"
  printf 'PERL=%s/bin/perl\n' "$perl_root"
  printf 'GO=%s/bin/go\n' "$go_root"
  printf 'GOROOT=%s/share/go\n' "$go_root"
  printf 'GOENV=off\n'
  printf 'GOTOOLCHAIN=local\n'
  printf 'GOPATH=%s/go-path\n' "$tools_root"
  printf 'GOCACHE=%s/go-cache\n' "$tools_root"
  printf 'GOBIN=%s/go-bin\n' "$tools_root"
  printf 'READELF=%s/bin/readelf\n' "$binutils_root"
  printf 'CARGO_TARGET_%s_LINKER=%s/bin/gcc\n' \
    "$(printf '%s' "$target" | tr '[:lower:]-' '[:upper:]_')" "$gcc_root"
} >>"$GITHUB_ENV"
printf '%s\n' "${path_entries[@]}" >>"$GITHUB_PATH"

echo "provisioned pinned native release tools from nixpkgs $nixpkgs_revision"
