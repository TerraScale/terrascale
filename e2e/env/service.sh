state=${E2E_STATE:?E2E_STATE is the run state directory}
logs=$state/logs
pids=$state/pids
boot_seconds=90

start() {
  local name=$1
  shift
  "$@" > "$logs/$name.log" 2>&1 &
  echo $! > "$pids/$name.pid"
}

ready() {
  local name=$1
  shift
  local pid
  pid=$(cat "$pids/$name.pid")
  until "$@" > /dev/null 2>&1; do
    if ! kill -0 "$pid" 2> /dev/null; then
      echo "$name exited before it was ready" >&2
      tail -n 40 "$logs/$name.log" >&2
      return 1
    fi
    if [ "$SECONDS" -ge "$boot_seconds" ]; then
      echo "$name is not ready $boot_seconds s after the boot started" >&2
      tail -n 40 "$logs/$name.log" >&2
      return 1
    fi
    sleep 0.1
  done
}
