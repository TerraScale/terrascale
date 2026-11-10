set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
pki=${E2E_STATE:?E2E_STATE is the run state directory}/pki
sed 's/^/127.0.0.1 /' "$here/hostnames" | sudo tee -a /etc/hosts > /dev/null
sudo install -m 0644 "$pki/ca.pem" /usr/local/share/ca-certificates/terrascale-journey.crt
sudo update-ca-certificates > /dev/null
sudo sysctl -q -w net.ipv4.ip_unprivileged_port_start=443
