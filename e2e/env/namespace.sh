set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
state=${E2E_STATE:?E2E_STATE is the run state directory}
ns=$state/namespace
rm -rf "$ns"
mkdir -p "$ns/empty"
ip link set lo up
ip address add 93.184.216.34/32 dev lo
if [ "${E2E_REGIONS:-1}" = 2 ]; then
  ip address add 127.0.0.2/32 dev lo
  ip route replace local 127.0.0.1/32 dev lo src 127.0.0.2 table local
  ip route replace local 127.0.0.2/32 dev lo src 127.0.0.1 table local
fi
{ printf '127.0.0.1 localhost\n::1 localhost\n'; sed '/^hooks.journey.terrascale.tech$/d;s/^/127.0.0.1 /' "$here/hostnames"; printf '93.184.216.34 hooks.journey.terrascale.tech\n'; } > "$ns/hosts"
printf 'passwd: files\ngroup: files\nshadow: files\nhosts: files\n' > "$ns/nsswitch.conf"
cat "${SSL_CERT_FILE:-/etc/ssl/certs/ca-certificates.crt}" "$state/pki/ca.pem" > "$ns/ca-certificates.crt"
mount --bind "$ns/hosts" /etc/hosts
mount --bind "$ns/nsswitch.conf" /etc/nsswitch.conf
if [ -d /run/nscd ]; then mount --bind "$ns/empty" /run/nscd; fi
mount --bind "$ns/ca-certificates.crt" /etc/ssl/certs/ca-certificates.crt
export SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
bash "$here/edge.sh" || { status=$?; bash "$here/down.sh"; exit "$status"; }
trap 'bash "$here/down.sh"; exit 0' TERM
touch "$ns/ready"
sleep infinity &
wait
