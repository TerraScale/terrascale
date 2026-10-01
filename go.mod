module terrascale.tech/dashboard

go 1.27.0

require (
	github.com/CAFxX/httpcompression v0.0.9
	github.com/a-h/templ v0.3.1020
	github.com/google/jsonschema-go v0.4.3
	github.com/modelcontextprotocol/go-sdk v1.8.0
	github.com/mxschmitt/playwright-go v0.6201.1
	github.com/starfederation/datastar-go v1.2.2
	github.com/stripe/stripe-go/v86 v86.4.2
	golang.org/x/net v0.51.0
	terrascale.tech/sdk v0.0.0-20260930023159-430cb6624fa8
)

require (
	github.com/a-h/parse v0.0.0-20250122154542-74294addb73e // indirect
	github.com/andybalholm/brotli v1.2.0 // indirect
	github.com/cenkalti/backoff/v4 v4.3.0 // indirect
	github.com/cli/browser v1.3.0 // indirect
	github.com/coder/websocket v1.8.15 // indirect
	github.com/deckarep/golang-set/v2 v2.8.0 // indirect
	github.com/fatih/color v1.16.0 // indirect
	github.com/fsnotify/fsnotify v1.7.0 // indirect
	github.com/go-stack/stack v1.8.1 // indirect
	github.com/klauspost/compress v1.18.0 // indirect
	github.com/klauspost/cpuid/v2 v2.0.12 // indirect
	github.com/mattn/go-colorable v0.1.13 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	github.com/natefinch/atomic v1.0.1 // indirect
	github.com/segmentio/asm v1.1.3 // indirect
	github.com/segmentio/encoding v0.5.4 // indirect
	github.com/valyala/bytebufferpool v1.0.0 // indirect
	github.com/yosida95/uritemplate/v3 v3.0.2 // indirect
	github.com/zeebo/blake3 v0.2.4 // indirect
	golang.org/x/mod v0.33.0 // indirect
	golang.org/x/oauth2 v0.35.0 // indirect
	golang.org/x/sync v0.20.0 // indirect
	golang.org/x/sys v0.41.0 // indirect
	golang.org/x/time v0.15.0 // indirect
	golang.org/x/tools v0.42.0 // indirect
)

tool github.com/a-h/templ/cmd/templ

// The SDK declares this vanity module path, which does not yet serve go-import
// metadata. Resolve the pinned source through its public Forgejo repository.
replace terrascale.tech/sdk => git.terrascale.tech/TerraScale/ts-go v0.0.0-20260930023159-430cb6624fa8
