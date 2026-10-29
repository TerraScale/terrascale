#!/usr/bin/env bash
set -euo pipefail

docker run -d --name dashboard-smoke -e DEMO=true -p 127.0.0.1:8080:8080 "ts-dashboard:$GITHUB_SHA"
trap 'docker rm -f dashboard-smoke >/dev/null' EXIT
for _ in {1..30}; do
  if curl -fsS http://127.0.0.1:8080/health/ready -o "$RUNNER_TEMP/ready.json"; then
    # Readiness bodies no longer carry the build revision (#126); the image tag already pins the SHA.
    jq -e '.status == "ok"' "$RUNNER_TEMP/ready.json"
    exit
  fi
  sleep 1
done
docker logs dashboard-smoke 2>&1 | tail -n 50
exit 1
