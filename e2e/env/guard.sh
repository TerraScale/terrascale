set -euo pipefail
pinned=$(curl -sS --max-time 5 -o /dev/null -w '%{http_code} from %{remote_ip}' https://status.terrascale.tech/)
echo "pinned https://status.terrascale.tech/ answered $pinned"
test "$pinned" = "404 from 127.0.0.1"
if [ "${E2E_REGIONS:-1}" = 2 ]; then
  pinned=$(curl -sS --max-time 5 -o /dev/null -w '%{http_code} from %{remote_ip}' https://fra.api.terrascale.tech/)
  echo "pinned https://fra.api.terrascale.tech/ answered $pinned"
  test "${pinned##* from }" = "127.0.0.1"
fi
for target in https://sjc.api.terrascale.tech/ https://1.1.1.1/; do
  if curl -sS --max-time 5 -o /dev/null "$target"; then
    echo "unpinned $target answered from inside the namespace" >&2
    exit 1
  fi
  echo "unpinned $target did not connect"
done
