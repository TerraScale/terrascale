#!/usr/bin/env bash
set -euo pipefail
client_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
cp "$client_root/prepare-sdk.sh" "$client_root/sdk.rev" "$client_root/Makefile" "$fixture/"
cd -- "$fixture"
git init -q

expect_failure() {
  local expected=$1
  shift
  if bash prepare-sdk.sh "$@" >diagnostic 2>&1; then
    echo "Expected SDK preparation/verification failure: $expected" >&2
    exit 1
  fi
  if [[ $(<diagnostic) != *"$expected"* ]]; then
    echo "Expected SDK diagnostic: $expected" >&2
    exit 1
  fi
}

expect_failure 'before building' --verify
make --no-print-directory -n build test check >commands
[[ $(head -n 1 commands) == 'bash prepare-sdk.sh' ]]
mkdir .sdk
# An empty directory must not resolve to the parent benchmark repository.
expect_failure 'not a usable SDK checkout' --verify
rmdir .sdk
git init -q .sdk
git -C .sdk -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm initial --allow-empty
git -C .sdk rev-parse HEAD >sdk.rev
expect_failure 'official ts-dotnet origin' --verify
git -C .sdk remote add origin ssh://git@git.terrascale.tech/TerraScale/ts-dotnet.git
git -C .sdk update-ref refs/remotes/origin/main HEAD
bash prepare-sdk.sh --verify
touch .sdk/untracked
expect_failure 'local changes' --verify
rm .sdk/untracked
git -C .sdk -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm feature --allow-empty
expect_failure 'does not match sdk.rev' --verify
git -C .sdk rev-parse HEAD >sdk.rev
expect_failure 'not an ancestor of official origin/main' --verify

# Translate only network operations to a local origin; retain all real Git gates.
export SDK_TEST_GIT=$(command -v git)
export SDK_TEST_ORIGIN="$fixture/origin"
git init -qb main "$SDK_TEST_ORIGIN"
git -C "$SDK_TEST_ORIGIN" -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm initial --allow-empty
git -C "$SDK_TEST_ORIGIN" rev-parse HEAD >sdk.rev
mv .sdk rejected-sdk
mkdir tools
cat >tools/git <<'GIT'
#!/usr/bin/env bash
set -euo pipefail
if [[ $1 == clone ]]; then
  "$SDK_TEST_GIT" clone "$SDK_TEST_ORIGIN" "$3"
  "$SDK_TEST_GIT" -C "$3" remote set-url origin "$2"
elif [[ $1 == -C && $3 == fetch ]]; then
  if [[ ${SDK_TEST_FAIL_FETCH:-0} == 1 ]]; then
    echo 'SDK fixture fetch failed.' >&2
    exit 1
  fi
  "$SDK_TEST_GIT" -C "$2" fetch "$SDK_TEST_ORIGIN" refs/heads/main:refs/remotes/origin/main
else
  exec "$SDK_TEST_GIT" "$@"
fi
GIT
chmod +x tools/git
export PATH="$fixture/tools:$PATH"

bash prepare-sdk.sh >diagnostic 2>&1
[[ $(git -C .sdk rev-parse HEAD) == "$(<sdk.rev)" ]]
! git -C .sdk symbolic-ref -q HEAD
[[ -z $(git -C .sdk status --porcelain) ]]
bash prepare-sdk.sh --verify

git -C "$SDK_TEST_ORIGIN" -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm update --allow-empty
git -C "$SDK_TEST_ORIGIN" rev-parse HEAD >sdk.rev
bash prepare-sdk.sh >diagnostic 2>&1
[[ $(git -C .sdk rev-parse HEAD) == "$(<sdk.rev)" ]]
! git -C .sdk symbolic-ref -q HEAD
[[ -z $(git -C .sdk status --porcelain) ]]
bash prepare-sdk.sh --verify

previous_head=$(git -C .sdk rev-parse HEAD)
previous_refs=$(git -C .sdk show-ref)
git -C "$SDK_TEST_ORIGIN" -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm next --allow-empty
git -C "$SDK_TEST_ORIGIN" rev-parse HEAD >sdk.rev
export SDK_TEST_FAIL_FETCH=1
expect_failure 'SDK fixture fetch failed.'
[[ $(git -C .sdk rev-parse HEAD) == "$previous_head" ]]
[[ $(git -C .sdk show-ref) == "$previous_refs" ]]
[[ -z $(git -C .sdk status --porcelain) ]]
echo 'SDK preparation: 11 checks passed (no network requests).'
