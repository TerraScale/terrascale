#!/usr/bin/env bash
# Inspect the actual linked library and the compiler's macro audit, not the host CPU's capabilities.
set -Eeuo pipefail
trap 'printf "crypto proof failed at line %s: %s\n" "$LINENO" "$BASH_COMMAND" >&2' ERR
library=${1:?usage: check-turso-native-crypto.sh <library> <rid> <receipt-directory>}
rid=${2:?usage: check-turso-native-crypto.sh <library> <rid> <receipt-directory>}
output=${3:?usage: check-turso-native-crypto.sh <library> <rid> <receipt-directory>}
case "$rid" in
  linux-x64) implementation=aegis256_aesni_implementation; instruction='v?aesenc' ;;
  linux-arm64) implementation=aegis256_neon_aes_implementation; instruction='aese|aesmc' ;;
  *) exit 1 ;;
esac
mkdir -p "$output"
grep -h '^cargo:warning=terrascale-aegis ' target/lib-release/build/aegis-*/output > "$output/$rid-aegis-compiler.txt"
cat "$output/$rid-aegis-compiler.txt"
grep -q 'terrascale-aegis compiler=' "$output/$rid-aegis-compiler.txt"
grep -q 'HAS_HW_AES=undefined; runtime dispatch and portable fallback retained' "$output/$rid-aegis-compiler.txt"
nm --defined-only "$library" | awk '$3 ~ /^aegis(256_|_runtime|_init)/' > "$output/$rid-aegis-symbols.txt"
grep -qw aegis256_soft_implementation "$output/$rid-aegis-symbols.txt"
grep -qw aegis256_pick_best_implementation "$output/$rid-aegis-symbols.txt"
grep -qw "$implementation" "$output/$rid-aegis-symbols.txt"
objdump -d "$library" | awk -v instruction="$instruction" '$0 ~ "[[:space:]](" instruction ")[[:space:]]" { if (count++ < 32) print }' > "$output/$rid-aegis-hardware-disassembly.txt"
test -s "$output/$rid-aegis-hardware-disassembly.txt"
printf 'crypto proof: %s hardware instructions, runtime dispatch and portable fallback present; HAS_HW_AES undefined\n' "$rid" | tee "$output/$rid-aegis-proof.txt"
