#!/usr/bin/env bash
# Every 60 s: per machine CPU by mode (share of one vCPU) and burst balance, from Fly's Prometheus (agent's own Fly token).
P() { curl -sS -G -H "Authorization: FlyV1 $(flyctl auth token 2>/dev/null)" https://api.fly.io/prometheus/terrascale-previews-515/api/v1/query --data-urlencode "query=$1"; }
while true; do
  t=$(date -u +%T)
  P 'sum by (region,mode) (rate(fly_instance_cpu{app="ts-terrabase-e2e",mode!="idle"}[1m]))' | jq -r --arg t $t '.data.result[] | "\($t) \(.metric.region) \(.metric.mode) \(.value[1]|tonumber|.*10|round/10)"'
  P 'fly_instance_memory_mem_available{app="ts-terrabase-e2e"}' | jq -r --arg t $t '.data.result[] | "\($t) \(.metric.region) mem_available_mb \(.value[1]|tonumber/1048576|round)"'
  sleep 60
done
