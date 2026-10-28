#!/usr/bin/env bash
set -euo pipefail
client_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
cp "$client_root/prepare-sdk.sh" "$client_root/with-sdk.sh" "$client_root/sdk.rev" "$client_root/Makefile" "$fixture/"
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
git -C .sdk remote add origin https://github.com/TerraScale/ts-dotnet.git
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
  shift 3
  shift # Drop the official remote name; preserve the production refspec.
  "$SDK_TEST_GIT" -C .sdk fetch "$SDK_TEST_ORIGIN" "$@"
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
# A custom mapping must not leave origin/main stale after preparation.
git -C .sdk config remote.origin.fetch '+refs/heads/main:refs/remotes/origin/custom'
bash prepare-sdk.sh >diagnostic 2>&1
[[ $(git -C .sdk rev-parse HEAD) == "$(<sdk.rev)" ]]
[[ $(git -C .sdk rev-parse refs/remotes/origin/main) == "$(<sdk.rev)" ]]
! git -C .sdk symbolic-ref -q HEAD
[[ -z $(git -C .sdk status --porcelain) ]]
bash prepare-sdk.sh --verify

bash with-sdk.sh bash -c '
  set -euo pipefail
  [[ $TS_BENCH_SDK_LOCKED == 1 ]]
  if flock --exclusive --nonblock .sdk.lock true; then
    echo "SDK mutation was allowed during a build command." >&2
    exit 1
  fi
  bash prepare-sdk.sh --verify
'
flock --exclusive --nonblock .sdk.lock true
if bash with-sdk.sh bash -c 'exit 17'; then
  echo 'Build wrapper failed to preserve command failure.' >&2
  exit 1
else
  [[ $? == 17 ]]
fi
flock --exclusive --nonblock .sdk.lock true

previous_head=$(git -C .sdk rev-parse HEAD)
previous_refs=$(git -C .sdk show-ref)
git -C "$SDK_TEST_ORIGIN" -c user.name=fixture -c user.email=fixture@example.test -c commit.gpgsign=false commit -qm next --allow-empty
git -C "$SDK_TEST_ORIGIN" rev-parse HEAD >sdk.rev
export SDK_TEST_FAIL_FETCH=1
expect_failure 'SDK fixture fetch failed.'
[[ $(git -C .sdk rev-parse HEAD) == "$previous_head" ]]
[[ $(git -C .sdk show-ref) == "$previous_refs" ]]
[[ -z $(git -C .sdk status --porcelain) ]]
echo 'SDK preparation: 14 checks passed (no network requests).'
