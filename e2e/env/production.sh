set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
e2e=$(dirname "$here")
state=$e2e/.state/production
report=$e2e/production-report
ca=/etc/ssl/certs/ca-certificates.crt
projects=(pages-desktop pages-mobile hosted interactions invitation)
domain=

refuse() {
  echo "production.sh refuses to run: $1"
  return 2
}

isolated() {
  local preset
  preset=$(env | sed -n -E 's/^(E2E_[A-Z_]*|TERRASCALE_[A-Z_]*|PW_[A-Z_]*|PLAYWRIGHT_[A-Z_]*|NODE_EXTRA_CA_CERTS|NODE_OPTIONS|NODE_TLS_REJECT_UNAUTHORIZED|CURL_CA_BUNDLE|REQUESTS_CA_BUNDLE|[Hh][Tt][Tt][Pp][Ss]?_[Pp][Rr][Oo][Xx][Yy]|[Aa][Ll][Ll]_[Pp][Rr][Oo][Xx][Yy])=.*/\1/p' | sort | paste -sd ' ' -)
  [ -z "$preset" ] || refuse "$preset set; the production target chooses its own state, hosts and trust"
  local host address addresses status
  while read -r host; do
    addresses=$(timeout --kill-after=2 15 getent ahosts "$host") || {
      status=$?
      if [ "$status" -ne 2 ]; then
        echo "[production-envelope] host lookup did not complete"
        return "$status"
      fi
      addresses=
    }
    while read -r address; do
      case $address in
        127.* | ::1 | ::ffff:127.* | 0.0.0.0) refuse "$host resolves to $address, the journey's network namespace or a pinned name, not production" ;;
      esac
    done < <(awk '{ print $1 }' <<< "$addresses" | sort -u)
  done < <(grep '\.terrascale\.tech$' "$here/hostnames")
}

inbox() {
  local domains status
  domains=$(cd "$e2e" && timeout --kill-after=5 185 node --input-type=module - "$state" <<'JS'
import { InboxProvider } from './support/inbox-provider.ts';
const provider = new InboxProvider(process.argv[2], undefined, () => ({ source: 'preflight' }));
try {
  process.stdout.write(JSON.stringify(await provider.request('GET', '/domains')));
} catch (error) {
  console.error(`${error.message}; production was not contacted`);
  process.exitCode = 3;
}
JS
  ) || {
    status=$?
    if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then
      echo '[production-envelope] inbox preflight exceeded its deadline; production was not contacted'
    fi
    return 3
  }
  domain=$(jq -r 'first(.[] | select(.isActive and (.isPrivate | not) and (.domain | test("^[a-z0-9.-]+$"))) | .domain)' <<< "$domains") || {
    echo "inbox provider mail.tm: GET /domains answered no domain list; production was not contacted"
    return 3
  }
  [ -n "$domain" ] || {
    echo "inbox provider mail.tm: GET /domains lists no active public domain; production was not contacted"
    return 3
  }
  echo "inbox provider mail.tm: $domain"
}

served() {
  local service body
  for service in dash auth; do
    body=$(curl -fsS --max-time 15 "https://$service.terrascale.tech/readyz") || {
      echo "production: https://$service.terrascale.tech/readyz is not ready $1 the suite"
      return 4
    }
    echo "$1 $service.terrascale.tech/readyz $body" | tee -a "$report/readyz.txt"
  done
}

begin() {
  local status=0
  (cd "$e2e" && timeout --kill-after=2 10 node --input-type=module -e "import { beginEnvelope } from './support/production-envelope.mjs'; console.log(beginEnvelope(process.argv[1], process.argv[2]));" "$state" "$1") || status=$?
  if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then echo '[production-envelope] deadline initialization timed out' >&2; fi
  return "$status"
}

play() {
  local phase=$1 budget status=0
  shift
  budget=$(begin "$phase") || return
  printf 'Production %s budget: %s ms\n' "$phase" "$budget"
  timeout --kill-after=5 "$(( (budget + 999) / 1000 + 10 ))s" env E2E_TARGET=production E2E_STATE="$state" \
    E2E_SDK_BUILD="$e2e/.state/sdk" E2E_CA_FILE="$ca" E2E_INBOX_DOMAIN="$domain" E2E_PRODUCTION_PHASE="$phase" \
    npx playwright test "$@" || status=$?
  if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then echo "[production-envelope] $phase process exceeded its deadline"; fi
  return "$status"
}

journey() {
  local suite=0 cleanup=0 readiness=0
  isolated
  begin preflight > /dev/null
  inbox
  served before
  cd "$e2e"
  play suite "${projects[@]/#/--project=}" || suite=$?
  printf 'Suite exit: %s\n' "$suite" | tee "$report/phases.txt"
  play cleanup --project=finish --project=desktop-close || cleanup=$?
  printf 'Cleanup exit: %s\n' "$cleanup" | tee -a "$report/phases.txt"
  served after || readiness=$?
  if [ "$suite" -ne 0 ]; then return "$suite"; fi
  if [ "$cleanup" -ne 0 ]; then return "$cleanup"; fi
  return "$readiness"
}

export SSL_CERT_FILE=$ca
unset SSL_CERT_DIR
[ $# -eq 0 ] || refuse "it takes no arguments, so nothing can point it elsewhere"
umask 077
mkdir -p "$e2e/.state"
exec 9> "$e2e/.state/production.lock"
if ! flock -n 9; then refuse "another process owns the production journey state"; fi
mkdir -p "$state"
find "$state" -mindepth 1 -maxdepth 1 ! -name inbox-requests ! -name inbox-cooldowns -exec rm -rf -- {} +
rm -rf -- "$report"
mkdir -p "$state/browser-home" "$report"
chmod 700 "$state"
journey 2>&1 | tee "$report/list.txt"
