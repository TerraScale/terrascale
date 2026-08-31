#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: $0 <x86_64-unknown-linux-gnu|aarch64-unknown-linux-gnu>" >&2
  exit 2
}
target=$1
case "$target" in
  x86_64-unknown-linux-gnu)
    system=x86_64-linux
    gcc=/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0
    cmake=/nix/store/vrsali0sa6hwri27azgn4q0y0kmrglyi-cmake-4.1.6
    ninja=/nix/store/60z9n8f7cczph0klb7ny1mikhd8m1vkl-ninja-1.13.2
    perl=/nix/store/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5.42.0
    go=/nix/store/3yh8g6m3balbhzxsx77jlyyx443bxqii-go-1.26.6
    buildah=/nix/store/vhnmplgnlcs61j0dny36hmp4q7r2jlb4-buildah-wrapper-1.43.1
    skopeo=/nix/store/jp9k9xpd8rjzfg9m8ny6h35npl50v8zl-skopeo-1.22.2
    jq_root=/nix/store/vkjzswzi7r1ayxa0xr920bjrzmi04x7v-jq-1.8.2-bin
    binutils=/nix/store/25771zf6h2dryp8d3i65gyg17mmphy9v-binutils-wrapper-2.46
    gzip=/nix/store/2722ncq58c1amdv6g6nv5d1wvab30p0r-gzip-1.14
    tar=/nix/store/l4qnlbca31jvyh0sdg5qqbqkh48z1q19-gnutar-1.35
    coreutils=/nix/store/gph17gb73p6qbrqqjnypfjj5npf0ldyx-coreutils-9.11
    file=/nix/store/5j5g0rkjd7vbxjpzm6ipgiw0lj0vpapk-file-5.47
    ;;
  aarch64-unknown-linux-gnu)
    system=aarch64-linux
    gcc=/nix/store/2w3qlfrhf8q326jsax1sxra3wx0983c9-gcc-wrapper-15.2.0
    cmake=/nix/store/42sj4bmjgv5f8f0hycfvx8yvdl7ybfwb-cmake-4.1.6
    ninja=/nix/store/3k3fml4xvys85pndnw1lvshiky4i58v0-ninja-1.13.2
    perl=/nix/store/9kv98czb2gz54hcgjkdj25mg3yq13kvq-perl-5.42.0
    go=/nix/store/863k45gpmsq0jnjf4kzr7kg6kjwyn7zl-go-1.26.6
    buildah=/nix/store/88ixxl6awcvc6zxxkwha7ldaw74yhzzv-buildah-wrapper-1.43.1
    skopeo=/nix/store/3b8idg9pgd5ilvhmvmgqg7mm7xmmc3nc-skopeo-1.22.2
    jq_root=/nix/store/m176nh79cghiwkmfh6wdcpxm7fal2c6m-jq-1.8.2-bin
    binutils=/nix/store/sbvrmvvx6w8p53wgqx17ymc4lprjcbns-binutils-wrapper-2.46
    gzip=/nix/store/kyqyikq8i5h0sggyx0lkskq3wly7d4aw-gzip-1.14
    tar=/nix/store/fi3ai6q21gyqhqcd47cx2il0c9b89rwm-gnutar-1.35
    coreutils=/nix/store/hlc4nqfx0l06f6p7z7lmd77qcwszi7wc-coreutils-9.11
    file=/nix/store/9d3cwnrqhnb9ksfzh735av05cdqhnwf2-file-5.47
    ;;
  *) exit 2 ;;
esac

jq -n -S \
  --arg target "$target" --arg system "$system" \
  --arg gcc "$gcc" --arg cmake "$cmake" --arg ninja "$ninja" \
  --arg perl "$perl" --arg go "$go" --arg buildah "$buildah" \
  --arg skopeo "$skopeo" --arg jq_root "$jq_root" --arg binutils "$binutils" \
  --arg gzip "$gzip" --arg tar "$tar" --arg coreutils "$coreutils" --arg file "$file" '
  {
    nixpkgs: {
      revision: "c5c4a43b0e8056328ec4529f735cabdb8f1942bb",
      narHash: "sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei/uiU9vrMqOiwWP1Y=",
      system: $system
    },
    target: $target,
    rustToolchain: {
      installerAction: "dtolnay/rust-toolchain@4360b52568e2003a75bf9bc1d59f33a8e3fc893c",
      channel: "1.97.1",
      rustcVersion: "rustc 1.97.1 (8bab26f4f 2026-07-14)",
      cargoVersion: "cargo 1.97.1 (c980f4866 2026-06-30)",
      linker: ($gcc + "/bin/gcc")
    },
    compiler: {
      cc: {attribute: "gcc", output: "out", version: "15.2.0", storePath: $gcc, path: ($gcc + "/bin/gcc")},
      cxx: {attribute: "gcc", output: "out", version: "15.2.0", storePath: $gcc, path: ($gcc + "/bin/g++")}
    },
    cmake: {attribute: "cmake", output: "out", version: "4.1.6", storePath: $cmake, path: ($cmake + "/bin/cmake")},
    ninja: {attribute: "ninja", output: "out", version: "1.13.2", storePath: $ninja, path: ($ninja + "/bin/ninja")},
    perl: {attribute: "perl", output: "out", version: "5.42.0", storePath: $perl, path: ($perl + "/bin/perl")},
    go: {attribute: "go", output: "out", version: "1.26.6", storePath: $go, path: ($go + "/bin/go")},
    artifactShaping: {
      buildah: {attribute: "buildah", output: "out", version: "1.43.1", storePath: $buildah, path: ($buildah + "/bin/buildah")},
      skopeo: {attribute: "skopeo", output: "out", version: "1.22.2", storePath: $skopeo, path: ($skopeo + "/bin/skopeo")},
      jq: {attribute: "jq", output: "bin", version: "1.8.2", storePath: $jq_root, path: ($jq_root + "/bin/jq")},
      readelf: {attribute: "binutils", output: "out", version: "2.46", storePath: $binutils, path: ($binutils + "/bin/readelf")},
      gzip: {attribute: "gzip", output: "out", version: "1.14", storePath: $gzip, path: ($gzip + "/bin/gzip")},
      tar: {attribute: "gnutar", output: "out", version: "1.35", storePath: $tar, path: ($tar + "/bin/tar")},
      sha256sum: {attribute: "coreutils", output: "out", version: "9.11", storePath: $coreutils, path: ($coreutils + "/bin/sha256sum")},
      file: {attribute: "file", output: "out", version: "5.47", storePath: $file, path: ($file + "/bin/file")}
    },
    sbomGenerator: {
      package: "terrabase-release-sbom",
      manifest: "deploy/release/sbom/Cargo.toml",
      lockSha256: "da4bca6bd9cd16bfd7bcfa2cc28dd1d0e10c631462291090a6bc1da43ec6f116"
    }
  }'
