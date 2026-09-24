#!/bin/sh
set -eu

# These archive digests come from the official Go download metadata and Node
# release checksum manifest respectively.
GO_VERSION='1.27.1'
GO_SHA256='63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445'
NODE_VERSION='24.21.0'
NODE_SHA256='fd8e59d5a511510f6a298afb548f18c7d2b1be404d8b4a27d94fbe49f56cb2d6'

GO_ARCHIVE="go${GO_VERSION}.linux-amd64.tar.gz"
NODE_ARCHIVE="node-v${NODE_VERSION}-linux-x64.tar.xz"
GO_URL="https://go.dev/dl/${GO_ARCHIVE}"
NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/${NODE_ARCHIVE}"

fail() {
	printf '%s\n' "setup-ci-toolchains: $*" >&2
	exit 1
}

for command in curl sha256sum tar mktemp uname; do
	command -v "$command" >/dev/null 2>&1 || fail "required command not found: $command"
done

case "$(uname -s)/$(uname -m)" in
	Linux/x86_64|Linux/amd64) ;;
	*) fail "unsupported runner platform: $(uname -s)/$(uname -m); expected Linux x86_64" ;;
esac

: "${RUNNER_TEMP:?RUNNER_TEMP must point to runner temporary storage}"
: "${GITHUB_PATH:?GITHUB_PATH must point to the runner path command file}"
case "$RUNNER_TEMP" in
	/*) ;;
	*) fail "RUNNER_TEMP must be an absolute path" ;;
esac

mkdir -p "$RUNNER_TEMP"
install_root=$(mktemp -d "${RUNNER_TEMP%/}/ts-dashboard-toolchains.XXXXXX")
go_archive_path="$install_root/$GO_ARCHIVE"
node_archive_path="$install_root/$NODE_ARCHIVE"

printf 'Downloading Go %s from %s\n' "$GO_VERSION" "$GO_URL"
curl --fail --location --silent --show-error --retry 3 --retry-connrefused \
	--output "$go_archive_path" "$GO_URL"
printf 'Downloading Node.js %s from %s\n' "$NODE_VERSION" "$NODE_URL"
curl --fail --location --silent --show-error --retry 3 --retry-connrefused \
	--output "$node_archive_path" "$NODE_URL"

(
	cd "$install_root"
	printf '%s  %s\n' "$GO_SHA256" "$GO_ARCHIVE" | sha256sum --check
	printf '%s  %s\n' "$NODE_SHA256" "$NODE_ARCHIVE" | sha256sum --check
)

tar -xzf "$go_archive_path" -C "$install_root"
tar -xJf "$node_archive_path" -C "$install_root"

go_bin="$install_root/go/bin/go"
node_bin="$install_root/node-v${NODE_VERSION}-linux-x64/bin/node"
node_bin_dir="$install_root/node-v${NODE_VERSION}-linux-x64/bin"

[ -x "$go_bin" ] || fail 'Go archive did not contain go/bin/go'
[ -x "$node_bin" ] || fail 'Node archive did not contain the expected node binary'

actual_go_version=$("$go_bin" version)
[ "$actual_go_version" = "go version go${GO_VERSION} linux/amd64" ] || \
	fail "expected Go ${GO_VERSION}, got: $actual_go_version"
actual_node_version=$("$node_bin" --version)
[ "$actual_node_version" = "v${NODE_VERSION}" ] || \
	fail "expected Node.js v${NODE_VERSION}, got: $actual_node_version"
[ -x "$node_bin_dir/npm" ] || fail 'Node archive did not contain npm'

rm -f "$go_archive_path" "$node_archive_path"
printf '%s\n%s\n' "$install_root/go/bin" "$node_bin_dir" >> "$GITHUB_PATH"

printf 'Installed and verified %s and Node.js v%s under %s\n' \
	"$actual_go_version" "$NODE_VERSION" "$install_root"
