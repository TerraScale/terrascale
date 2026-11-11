set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
pki=${E2E_STATE:?E2E_STATE is the run state directory}/pki
umask 077
rm -rf "$pki"
mkdir -p "$pki/browser-home/.pki/nssdb"
sans=$(sed 's/^/DNS:/' "$here/hostnames" | paste -sd, -)
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -noenc -days 2 -subj "/CN=TerraScale journey CA" \
  -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,cRLSign \
  -keyout "$pki/ca.key" -out "$pki/ca.pem" 2>/dev/null
openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -noenc -subj "/CN=dash.terrascale.tech" \
  -keyout "$pki/edge.key" -out "$pki/edge.csr" 2>/dev/null
printf 'subjectAltName=%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\n' "$sans" > "$pki/edge.ext"
openssl x509 -req -in "$pki/edge.csr" -CA "$pki/ca.pem" -CAkey "$pki/ca.key" -set_serial "0x$(openssl rand -hex 16)" -days 2 \
  -extfile "$pki/edge.ext" -out "$pki/edge.pem" 2>/dev/null
rm "$pki/ca.key" "$pki/edge.csr" "$pki/edge.ext"
certutil -N -d "sql:$pki/browser-home/.pki/nssdb" --empty-password
certutil -A -d "sql:$pki/browser-home/.pki/nssdb" -n "TerraScale journey CA" -t C,, -i "$pki/ca.pem"
