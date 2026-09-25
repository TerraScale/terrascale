set -eu
printf 'tool\tcommand_path\tresolved_path\tversion_first_line\tsha256\n'
for tool in gpgv apt-get apt apt-cache dpkg dpkg-deb dpkg-query xz; do
  tool_path="$(command -v "$tool" 2>/dev/null || true)"
  if [ -z "$tool_path" ]; then
    printf '%s\tABSENT\tABSENT\tABSENT\tABSENT\n' "$tool"
  else
    resolved_path="$(readlink -f "$tool_path" 2>/dev/null || printf '%s' "$tool_path")"
    version_line="$("$tool_path" --version 2>&1 | sed -n '1p' || true)"
    hash_line="$(sha256sum "$resolved_path")"
    tool_hash="${hash_line%% *}"
    printf '%s\t%s\t%s\t%s\t%s\n' "$tool" "$tool_path" "$resolved_path" "$version_line" "$tool_hash"
  fi
done
printf '\nKEYRING\tpath\tsha256\n'
keyring=/usr/share/keyrings/ubuntu-archive-keyring.gpg
if [ -r "$keyring" ]; then
  keyring_hash="$(sha256sum "$keyring")"
  printf '%s\t%s\n' "$keyring" "${keyring_hash%% *}"
else
  printf '%s\tABSENT\n' "$keyring"
fi
printf '\nPACKAGE_INVENTORY_BEGIN\n'
dpkg-query -W -f='${db:Status-Abbrev}\t${binary:Package}\t${Version}\t${Architecture}\n' | LC_ALL=C sort
printf 'PACKAGE_INVENTORY_END\n'
