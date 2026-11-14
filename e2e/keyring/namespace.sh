export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH";
set -euo pipefail
ip link set lo up
ip address add 8.8.8.8/32 dev lo
printf '127.0.0.1 localhost\n8.8.8.8 journey.backups.terrascale.tech 6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com\n' > "$E2E_STATE/hosts"
printf 'hosts: files\n' > "$E2E_STATE/nsswitch.conf"
mount --bind "$E2E_STATE/hosts" /etc/hosts
mount --bind "$E2E_STATE/nsswitch.conf" /etc/nsswitch.conf
mkdir -p "$E2E_STATE/empty"
if [ -d /run/nscd ]; then mount --bind "$E2E_STATE/empty" /run/nscd; fi
cat /etc/ssl/certs/ca-certificates.crt "$E2E_STATE/tls.pem" > "$E2E_STATE/ca.pem"
mount --bind "$E2E_STATE/ca.pem" /etc/ssl/certs/ca-certificates.crt
export SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt NODE_EXTRA_CA_CERTS="$E2E_STATE/tls.pem"
exec node "$(dirname "$0")/proof.mjs"
