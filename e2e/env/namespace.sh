set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
state=${E2E_STATE:?E2E_STATE is the run state directory}
ns=$state/namespace
rm -rf "$ns"
mkdir -p "$ns/empty"
ip link set lo up
{ printf '127.0.0.1 localhost\n::1 localhost\n'; sed 's/^/127.0.0.1 /' "$here/hostnames"; } > "$ns/hosts"
printf 'passwd: files\ngroup: files\nshadow: files\nhosts: files\n' > "$ns/nsswitch.conf"
cat "${SSL_CERT_FILE:-/etc/ssl/certs/ca-certificates.crt}" "$state/pki/ca.pem" > "$ns/ca-certificates.crt"
mount --bind "$ns/hosts" /etc/hosts
mount --bind "$ns/nsswitch.conf" /etc/nsswitch.conf
if [ -d /run/nscd ]; then mount --bind "$ns/empty" /run/nscd; fi
mount --bind "$ns/ca-certificates.crt" /etc/ssl/certs/ca-certificates.crt
export SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
boot() { bash "$here/up.sh" || { status=$?; bash "$here/down.sh"; exit "$status"; }; }
case ${1:?run or hold} in
  run)
    shift
    boot
    status=0
    "$@" || status=$?
    bash "$here/down.sh"
    exit "$status"
    ;;
  hold)
    boot
    mkfifo "$ns/stop"
    touch "$ns/ready"
    read -r _ < "$ns/stop"
    bash "$here/down.sh"
    ;;
esac
