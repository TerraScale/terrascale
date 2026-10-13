#!/usr/bin/env bash
# Run under with-key.sh. Per machine: SIGKILL (flyctl machine kill; restart policy on-failure brings it back) and a clean restart, each followed by
# pinned /healthz + readyz, catalog-gate open/trusted/equal digest on all three, and the bulk collection's counts+digests.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); cd "$here"; source machines.env; A=ts-terrabase-e2e
declare -A port=([$G]=18181 [$S]=18182 [$F]=18183) region=([$G]=gru [$S]=sjc [$F]=fra)
gates() { for i in $(seq 1 24); do
    l=""; for m in $G $S $F; do l="$l $(curl -sS -m 5 --http2-prior-knowledge http://127.0.0.1:${port[$m]}/catalog-gate | jq -r '"\(.open)/\(.trusted)/\(.digest[0:12])"' 2>/dev/null)"; done
    set -- $l; [[ $# == 3 && $1 == true/true/* && $1 == "$2" && $2 == "$3" ]] && { echo "gates equal after ~$((i*5))s:$l"; return 0; }; sleep 5; done; echo "gates NOT equal in 120s:$l"; return 1; }
ready() { t0=$(date +%s); for i in $(seq 1 90); do c=$(curl -sS -m 5 -o /dev/null -w "%{http_code}" -H "fly-force-instance-id: $1" https://$A.fly.dev/readyz 2>/dev/null); [[ $c == 200 ]] && { echo "readyz 200 after $(( $(date +%s) - t0 ))s; $(curl -sS -H "fly-force-instance-id: $1" https://$A.fly.dev/healthz | jq -c '{region,machine,revision}')"; return 0; }; sleep 2; done; echo "readyz $c after 180s"; return 1; }
for id in ${ONLY:-$G $S $F}; do
  for how in ${HOWS:-kill restart}; do
    echo "== $how ${region[$id]} $id $(date -u +%T)"
    if [[ $how == kill ]]; then flyctl machine kill $id -a $A 2>&1 | tail -1; sleep 3; else flyctl machine restart $id -a $A --skip-health-checks 2>&1 | tail -1; fi
    ready $id; gates
    python3 -I counts.py verify "$1" "$2" 2>&1 | grep -v revoked
    flyctl logs -a $A -i $id --no-tail 2>/dev/null | sed 's/\x1b\[[0-9;]*m//g' | grep -c "Hosting failed to start" | sed 's/^/hosting-failed lines in log buffer: /'
  done
done
