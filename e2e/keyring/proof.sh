export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH";
set -euo pipefail
[ -z "${E2E_TARGET+set}" ] || { printf '%s\n' 'keyring proof requires the local target' >&2; exit 2; }
: "${E2E_STATE:?throwaway profile required}" "${E2E_BUILD:?candidate build required}" "${E2E_KEYRING_DRIVER:?keyring driver required}"
umask 077
mkdir -p "$E2E_STATE"
exec unshare --user --map-root-user --net --pid --fork --mount-proc --kill-child bash "$(dirname "$0")/namespace.sh"
