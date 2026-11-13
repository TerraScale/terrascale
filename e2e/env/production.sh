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
  local host address
  while read -r host; do
    for address in $(getent ahosts "$host" | awk '{ print $1 }' | sort -u); do
      case $address in
        127.* | ::1 | ::ffff:127.* | 0.0.0.0) refuse "$host resolves to $address, the journey's network namespace or a pinned name, not production" ;;
      esac
    done
  done < <(grep '\.terrascale\.tech$' "$here/hostnames")
}

inbox() {
  local domains
  domains=$(cd "$e2e" && node --input-type=module - "$state" <<'JS'
import { InboxProvider } from './support/inbox-provider.ts';
const provider = new InboxProvider(process.argv[2], undefined, () => ({ source: 'preflight' }));
try {
  process.stdout.write(JSON.stringify(await provider.request('GET', '/domains')));
} catch (error) {
  console.error(`${error.message}; production was not contacted`);
  process.exitCode = 3;
}
JS
  ) || return 3
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

journey() {
  local status=0
  isolated
  inbox
  served before
  cd "$e2e"
  E2E_TARGET=production E2E_STATE="$state" E2E_SDK_BUILD="$e2e/.state/sdk" E2E_CA_FILE="$ca" E2E_INBOX_DOMAIN="$domain" \
    npx playwright test "${projects[@]/#/--project=}" || status=$?
  served after
  return "$status"
}

export SSL_CERT_FILE=$ca
unset SSL_CERT_DIR
[ $# -eq 0 ] || refuse "it takes no arguments, so nothing can point it elsewhere"
rm -rf "$state" "$report"
mkdir -p "$state/browser-home" "$report"
chmod 700 "$state"
journey 2>&1 | tee "$report/list.txt"
