export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH";
set -euo pipefail
[ -z "${E2E_TARGET+set}" ] || { printf '%s\n' 'keyring proof requires the local target' >&2; exit 2; }
: "${E2E_STATE:?throwaway profile required}" "${E2E_KEYRING_BASELINE:?origin/main binary required}" "${E2E_BUILD:?candidate build required}" "${E2E_KEYRING_DRIVER:?keyring lifecycle driver required}"
umask 077
mkdir -p "$E2E_STATE"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -noenc -days 1 -subj /CN=keyring-proof \
  -addext basicConstraints=critical,CA:TRUE \
  -addext subjectAltName=DNS:journey.backups.terrascale.tech,DNS:6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com \
  -keyout "$E2E_STATE/tls.key" -out "$E2E_STATE/tls.pem" 2>/dev/null
exec unshare --user --map-root-user --net --mount --pid --fork --mount-proc --kill-child bash "$(dirname "$0")/namespace.sh"
