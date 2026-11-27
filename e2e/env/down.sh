set -uo pipefail
pids=${E2E_STATE:?E2E_STATE is the run state directory}/pids
for file in "$pids"/*.pid; do
  [ -f "$file" ] && kill "$(cat "$file")" 2> /dev/null
done
for file in "$pids"/*.pid; do
  [ -f "$file" ] || continue
  pid=$(cat "$file")
  while kill -0 "$pid" 2> /dev/null; do sleep 0.1; done
  rm -f "$file"
done
