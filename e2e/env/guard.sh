set -euo pipefail
pinned=$(curl -sS --max-time 5 -o /dev/null -w '%{http_code} from %{remote_ip}' https://status.terrascale.tech/)
echo "pinned https://status.terrascale.tech/ answered $pinned"
test "$pinned" = "404 from 127.0.0.1"
for target in https://fra.api.terrascale.tech/ https://1.1.1.1/; do
  if curl -sS --max-time 5 -o /dev/null "$target"; then
    echo "unpinned $target answered from inside the namespace" >&2
    exit 1
  fi
  echo "unpinned $target did not connect"
done
