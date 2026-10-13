#!/usr/bin/env bash
# F7 and F3 live proofs on ts-terrabase-e2e (run under with-key.sh: needs tenant.env, machines.env, ips.env, proxies 18181-3).
#  F7a: an anchored revocation (API key DELETE) on gru, then gru frozen (SIGSTOP) and stopped, so the VM exits abruptly
#       after the kill timeout (the F7 path); after the start: gate opens with no operator, nothing behind the anchor, and
#       the revoked key is refused in every region.
#  F7b: the same with SIGKILL (flyctl machine kill).
#  F3:  gru restarted while sjc and fra drop its 6PN traffic (its first gate pass stalls past the 5 s start budget): the
#       host must start (no "Hosting failed to start"), stay closed, and open once the traffic is restored.
set -uo pipefail; set +x
here=$(cd "$(dirname "$0")" && pwd); cd "$here"; source tenant.env; source machines.env; source ips.env
A=ts-terrabase-e2e
declare -A ip; for pair in $IPS; do ip[${pair%%=*}]=${pair#*=}; done
step() { echo "=== $(date -u +%T) $*"; }
op() { curl -sS -m 120 --http2-prior-knowledge -H "X-TerraBase-User-Id: $owner" -H 'content-type: application/json' "$@"; }
gates() { for p in 18181 18182 18183; do curl -sS -m 8 --http2-prior-knowledge http://127.0.0.1:$p/catalog-gate | jq -c '{open,trusted,pending,d:.digest[0:12]}' 2>/dev/null || echo unreachable; done; }
ready() { t0=$(date +%s); for i in $(seq 1 100); do c=$(curl -sS -m 5 -o /dev/null -w "%{http_code}" -H "fly-force-instance-id: $1" https://$A.fly.dev/readyz 2>/dev/null); [[ $c == 200 ]] && { echo "readyz 200 after $(( $(date +%s) - t0 ))s"; return 0; }; sleep 3; done; echo "readyz $c after 300s"; return 1; }
logs() { flyctl logs -a $A -i $1 --no-tail 2>/dev/null | sed 's/\x1b\[[0-9;]*m//g' | grep -E "behind its anchor|Hosting failed|SIGABRT|exited abruptly|Sending signal|Main child exited|verified against|stays closed until|operator" | grep -E "T$2" | cut -c1-260; }
keys=http://127.0.0.1:18181/v1/management/organizations/$org/projects/$project/api-keys
probe_key() {   # $1 secret: the status each region answers for a document read with it (404 = authenticated, 401 = refused)
  for r in gru sjc fra; do m=$G; [[ $r == sjc ]] && m=$S; [[ $r == fra ]] && m=$F
    printf '%s=%s ' $r "$(curl -sS -m 10 -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $1" -H "fly-force-instance-id: $m" "https://$A.fly.dev/v1/projects/$project/databases/e2e/collections/f7-probe/documents/by-key?id=none")"; done; echo; }
f7() {   # $1 label, $2 how (freeze|kill)
  step "F7$1: mint a throwaway key, check it works, revoke it on gru (anchored), then $2 gru at once"
  issued=$(curl -sS --fail --http2-prior-knowledge -H 'content-type: application/json' -d "$(jq -cn --arg o "$org" --arg p "$project" \
    '{name: "f7-throwaway", scopes: ["documents:read"], organizationId: $o, projectId: $p, expiresAt: (now + 3600 | todate)}')" http://127.0.0.1:18181/admin/api-keys)
  kid=$(jq -r .id <<< "$issued"); secret=$(jq -r .secret <<< "$issued"); unset issued; echo "throwaway key id $kid (1 h expiry)"
  for i in $(seq 1 20); do s=$(probe_key "$secret"); [[ $s != *401* && $s != *000* ]] && break; sleep 2; done; echo "before revoke: $s"
  t=$(date -u +%H:%M:%S); code=$(op -o /dev/null -w '%{http_code}' -X DELETE "$keys/$kid"); echo "revoke on gru at $t: HTTP $code"
  if [[ $2 == freeze ]]; then
    flyctl ssh console -a $A --machine $G -C "sh -c 'kill -STOP \$(pidof terrabase)'" > /dev/null 2>&1; echo "$(date -u +%T) gru process frozen (SIGSTOP)"
    flyctl machine stop $G -a $A 2>&1 | tail -1
  else
    flyctl machine kill $G -a $A 2>&1 | tail -1; sleep 5
  fi
  flyctl machine wait $G -a $A --state stopped --wait-timeout 60s > /dev/null 2>&1; flyctl machine start $G -a $A 2>&1 | tail -1
  ready $G; sleep 10; gates
  echo "gru log since the revoke:"; logs $G "$(date -u +%H:%M | cut -c1-4)"
  echo "revoked key, per region (401 expected everywhere): $(probe_key "$secret")"; unset secret
}
f7 a freeze
f7 b kill
step "F3: sjc and fra drop gru's 6PN traffic; restart gru"
for r in sjc fra; do m=$S; [[ $r == fra ]] && m=$F; flyctl ssh console -a $A --machine $m -C "sh -c 'ip6tables -A INPUT -s ${ip[gru]} -j DROP && ip6tables -A OUTPUT -d ${ip[gru]} -j DROP'" > /dev/null; done
flyctl machine restart $G -a $A --skip-health-checks 2>&1 | tail -1
sleep 60; echo "healthz gru: $(curl -sS -m 10 -H "fly-force-instance-id: $G" https://$A.fly.dev/healthz | jq -c '{status,machine}')"
echo "readyz gru: $(curl -sS -m 10 -o /dev/null -w '%{http_code}' -H "fly-force-instance-id: $G" https://$A.fly.dev/readyz)"; gates
echo "gru log after the restart:"; logs $G "$(date -u +%H:%M | cut -c1-4)"
step "F3: restore gru's traffic"
for r in sjc fra; do m=$S; [[ $r == fra ]] && m=$F; flyctl ssh console -a $A --machine $m -C "sh -c 'ip6tables -D INPUT -s ${ip[gru]} -j DROP && ip6tables -D OUTPUT -d ${ip[gru]} -j DROP'" > /dev/null; done
ready $G; sleep 5; gates
step "done"
