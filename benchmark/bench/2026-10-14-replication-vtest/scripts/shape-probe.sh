#!/usr/bin/env bash
# Run under vtest.sh with-key: creates a collection with one document in gru, then asks each machine for the shape
# bootstrap (offset=-1, redirects not followed) and its target, printing status, the shape headers and the error body.
set -uo pipefail
db="$TERRABASE_E2E_FLY_URL"v1/projects/$TERRABASE_E2E_FLY_PROJECT/databases/e2e; c=shape-probe-$(date +%s)
auth=(-H "Authorization: Bearer $TERRABASE_E2E_FLY_API_KEY")
pin() { echo "fly-force-instance-id: $(tr ',' '\n' <<< "$TERRABASE_E2E_FLY_MACHINES" | grep "^$1=" | cut -d= -f2)"; }
curl -sS -o /dev/null -w "create collection %{http_code}\n" "${auth[@]}" -H "$(pin gru)" -H 'content-type: application/json' -d "{\"name\":\"$c\"}" "$db/collections"
curl -sS -w " put %{http_code}\n" "${auth[@]}" -H "$(pin gru)" -H 'content-type: application/json' -X PUT -d '{"v":1}' "$db/collections/$c/documents/by-key?id=01M4XQ0000000000000000PR0B"
sleep "${PROBE_WAIT:-5}"
for r in gru sjc fra; do
  echo "== $r $(date -u +%T)"
  loc=$(curl -sS -o /tmp/shape-probe.body -D - "${auth[@]}" -H "$(pin $r)" "$db/collections/$c/shape?offset=-1" | tr -d '\r' | tee /dev/stderr | grep -i '^location:' | cut -d' ' -f2)
  head -c 300 /tmp/shape-probe.body; echo
  [[ -n $loc ]] && { curl -sS -D - "${auth[@]}" -H "$(pin $r)" "${TERRABASE_E2E_FLY_URL%/}$loc" | tr -d '\r' | grep -iE '^(HTTP|tb-shape|retry-after|x-terrabase)|^\{' | head -c 900; echo; }
done 2>&1 | grep -iE '^(==|HTTP|location|tb-shape|retry-after|x-terrabase-region)|^\{|error' | cut -c1-400
curl -sS -o /dev/null -w "drop collection %{http_code}\n" "${auth[@]}" -H "$(pin gru)" -X DELETE "$db/collections/$c"
