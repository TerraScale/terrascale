export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH";
set -euo pipefail
ip link set lo up
exec node "$(dirname "$0")/proof.mjs"
