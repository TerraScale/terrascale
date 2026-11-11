set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
e2e=$(dirname "$here")
state=$e2e/.state/production
report=$e2e/production-report
ca=/etc/ssl/certs/ca-certificates.crt
projects=(pages-desktop pages-mobile hosted interactions invitation)

refuse() {
  echo "production.sh refuses to run: $1"
  return 2
}

isolated() {
  local preset
  preset=$(env | sed -n -E 's/^(E2E_[A-Z_]*|TERRASCALE_[A-Z_]*|NODE_EXTRA_CA_CERTS|[Hh][Tt][Tt][Pp][Ss]?_[Pp][Rr][Oo][Xx][Yy]|[Aa][Ll][Ll]_[Pp][Rr][Oo][Xx][Yy])=.*/\1/p' | sort | paste -sd ' ' -)
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
  domains=$(curl -fsS --max-time 15 -H 'accept: application/json' https://api.mail.tm/domains) || {
    echo "inbox provider mail.tm: GET /domains failed; production was not contacted"
    return 3
  }
  jq -e 'any(.[]; .isActive and (.isPrivate | not))' <<< "$domains" > /dev/null || {
    echo "inbox provider mail.tm: GET /domains lists no active public domain; production was not contacted"
    return 3
  }
  echo "inbox provider mail.tm: $(jq -r '[.[] | select(.isActive and (.isPrivate | not)) | .domain] | join(" ")' <<< "$domains")"
}

served() {
  local service body
  for service in dash auth; do
    body=$(curl -fsS --max-time 15 "https://$service.terrascale.tech/readyz") || {
      echo "production: https://$service.terrascale.tech/readyz is not ready"
      return 4
    }
    echo "$service.terrascale.tech/readyz $body" | tee -a "$report/readyz.txt"
  done
}

journey() {
  isolated
  inbox
  served
  cd "$e2e"
  E2E_TARGET=production E2E_STATE="$state" E2E_SDK_BUILD="$e2e/.state/sdk" E2E_CA_FILE="$ca" \
    npx playwright test "${projects[@]/#/--project=}"
}

export SSL_CERT_FILE=$ca
unset SSL_CERT_DIR
[ $# -eq 0 ] || refuse "it takes no arguments, so nothing can point it elsewhere"
rm -rf "$state" "$report"
mkdir -p "$state/browser-home" "$report"
chmod 700 "$state"
journey 2>&1 | tee "$report/list.txt"
