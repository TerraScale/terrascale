#!/usr/bin/env bash
# Self-check for scripts/terrabase/storage-format-check.sh against a fake gh; CI runs it in the `test` job.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
# Answers from $FILES (one filename per line), $PATCH (StorageFormat.cs's patch) and $LABELS (one per line).
cat >"$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
if [[ $1 == pr ]]; then
  if [[ $* == *'--json body'* ]]; then printf '%s' "$BODY"; else printf '%s' "$LABELS"; fi
  exit 0
fi
if [[ $* == *select* ]]; then printf '%s' "$PATCH"; else printf '%s' "$FILES"; fi
FAKE
chmod +x "$tmp/bin/gh"
export PATH="$tmp/bin:$PATH"

bump='@@ -1 +1 @@
-    public const int Version = 2;
+    public const int Version = 3;'
engine=src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs
version=src/TerraBase/Engine/Sqlite/StorageFormat.cs
# check <expect pass|fail> <files> <patch> <labels> [body]
check() {
  local outcome=pass
  FILES=$2 PATCH=$3 LABELS=$4 BODY=${5-} "$here/storage-format-check.sh" o/r 1 >/dev/null 2>&1 || outcome=fail
  [[ $outcome == "$1" ]] || { echo "storage-format-check-test: expected $1 for files [$2] labels [$4]" >&2; exit 1; }
}

check pass "docs/terrabase/ci.md" "" ""
check fail "$engine" "" ""
check pass "$engine" "" "storage-format-unchanged"
check pass "$engine
$version" "$bump" "storage-format"
check fail "$engine
$version" "$bump" ""
check fail "$engine" "" "storage-format"
check pass "$engine" "" "storage-format" "Needs format bump"
check fail "$engine" "" "" "Needs format bump"
check fail "$engine" "" "storage-format
storage-format-unchanged" "Needs format bump"
check fail "src/TerraBase/Engine/Lattices/DocumentStateEncoding.cs" "" ""
check fail "$version" "$bump" "storage-format
storage-format-unchanged"
# A comment edit in StorageFormat.cs is no bump.
check pass "$version" "+    /// <summary>reworded</summary>" ""
echo "storage-format-check: all checks passed"
