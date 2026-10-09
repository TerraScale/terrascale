module terrascale.tech/dashboard

go 1.27.0

require (
	github.com/CAFxX/httpcompression v0.0.9
	github.com/a-h/templ v0.3.1020
	github.com/coreos/go-oidc/v3 v3.21.0
	github.com/getsentry/sentry-go v0.49.0
	github.com/google/jsonschema-go v0.4.3
	github.com/modelcontextprotocol/go-sdk v1.8.0
	github.com/mxschmitt/playwright-go v0.6201.1
	github.com/oklog/ulid/v2 v2.1.2
	github.com/starfederation/datastar-go v1.2.2
	github.com/stripe/stripe-go/v86 v86.4.2
	golang.org/x/net v0.57.0
	golang.org/x/oauth2 v0.36.0
	golang.org/x/sync v0.22.0
	terrascale.tech/sdk v0.0.0-20261005130518-b911817c6e63
)

require (
	github.com/a-h/parse v0.0.0-20250122154542-74294addb73e // indirect
	github.com/andybalholm/brotli v1.2.0 // indirect
	github.com/cenkalti/backoff/v4 v4.3.0 // indirect
	github.com/cli/browser v1.3.0 // indirect
	github.com/deckarep/golang-set/v2 v2.8.0 // indirect
	github.com/fatih/color v1.16.0 // indirect
	github.com/fsnotify/fsnotify v1.7.0 // indirect
	github.com/go-jose/go-jose/v4 v4.1.4 // indirect
	github.com/go-stack/stack v1.8.1 // indirect
	github.com/jackc/pgpassfile v1.0.0 // indirect
	github.com/jackc/pgservicefile v0.0.0-20240606120523-5a60cdf6a761 // indirect
	github.com/jackc/pgx/v5 v5.9.2 // indirect
	github.com/klauspost/compress v1.18.0 // indirect
	github.com/mattn/go-colorable v0.1.13 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	github.com/natefinch/atomic v1.0.1 // indirect
	github.com/segmentio/asm v1.1.3 // indirect
	github.com/segmentio/encoding v0.5.4 // indirect
	github.com/valyala/bytebufferpool v1.0.0 // indirect
	github.com/yosida95/uritemplate/v3 v3.0.2 // indirect
	golang.org/x/mod v0.38.0 // indirect
	golang.org/x/sys v0.47.0 // indirect
	golang.org/x/text v0.41.0 // indirect
	golang.org/x/time v0.15.0 // indirect
	golang.org/x/tools v0.48.0 // indirect
)

tool github.com/a-h/templ/cmd/templ

// The SDK declares this vanity module path, which does not yet serve go-import
// metadata. Resolve the pinned source through its public Forgejo repository.
replace terrascale.tech/sdk => git.terrascale.tech/TerraScale/ts-go v0.0.0-20261009024544-81eb9eeec5bd
