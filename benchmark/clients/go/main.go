// Command api-bench-go is the Go client of the TS-832 API benchmark. Its
// protocol is in docs/api-bench.md.
package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/signal"
	"runtime"
	"runtime/debug"
	"strings"
	"sync"
	"syscall"
	"time"

	"terrascale.tech/sdk/tsauth"
	"terrascale.tech/sdk/v1"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

// started is initialised first in this package, so init_ms covers this
// package's setup rather than the Go runtime's own start.
var started = time.Now()

const placeholderJWT = "header.payload.signature"

func main() {
	args := os.Args[1:]
	var err error
	switch {
	case len(args) == 1 && args[0] == "startup":
		err = startup(os.Stdout)
	case len(args) > 0 && args[0] == "run":
		err = runBench(args[1:])
	default:
		err = errors.New("usage: api-bench-go startup | run --spec PATH --credentials PATH")
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "api-bench-go:", err)
		os.Exit(1)
	}
}

// startup builds the SDK client without network access and reports the time
// since the package started.
func startup(w io.Writer) error {
	if _, err := sdkClient("https://localhost", placeholderJWT, &http.Client{Transport: sdkTransport()}); err != nil {
		return err
	}
	return json.NewEncoder(w).Encode(apibench.Startup{Type: "startup", InitMS: time.Since(started).Seconds() * 1000})
}

// sdkTransport copies ts-go v1's default transport (v1/client.go newClient).
// The SDK does not expose it and the meter must wrap it, so the copy keeps the
// measurement on the SDK as shipped.
func sdkTransport() http.RoundTripper {
	return &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSHandshakeTimeout:   10 * time.Second,
		ResponseHeaderTimeout: 30 * time.Second,
		TLSClientConfig:       &tls.Config{MinVersion: tls.VersionTLS13},
		ForceAttemptHTTP2:     true,
		IdleConnTimeout:       90 * time.Second,
	}
}

// sdkClient retains the shipped SDK retry policy.
func sdkClient(endpoint, token string, hc *http.Client) (*v1.Client, error) {
	return v1.New(v1.Config{BaseURL: endpoint, Token: token, HTTPClient: hc})
}

// bench is one client run: its database, the current token and SDK handle,
// and the seeded ids.
type bench struct {
	spec    apibench.Spec
	project string
	key     string
	dbName  string
	auth    tsauth.APIKeyConfig
	ts      *tsauth.TokenSource
	token   string
	db      *v1.Database
	hc      *http.Client
	h2, h1  *http.Client
	meter   *apibench.Meter
	ids     map[string][]string
	bodies  map[int][]json.RawMessage
}

// runBench does the untimed setup, answers step lines until stdin closes, then
// deletes the database. On SIGINT or SIGTERM the running step is cut short and
// answers; the runner closes stdin when it stops, which ends the loop the same way.
func runBench(args []string) error {
	fs := flag.NewFlagSet("run", flag.ContinueOnError)
	specPath := fs.String("spec", "", "benchmark spec")
	credsPath := fs.String("credentials", "", "credentials file, mode 0600")
	if err := fs.Parse(args); err != nil {
		return err
	}
	spec, err := apibench.LoadSpec(*specPath)
	if err != nil {
		return err
	}
	key, project, err := loadCredentials(*credsPath)
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	m := &apibench.Meter{}
	b := &bench{
		spec:    spec,
		project: project,
		key:     key,
		dbName:  "bench-" + strings.ToLower(v1.NewID()), // SDK database names are lowercase
		meter:   m,
		hc:      &http.Client{Transport: m.Wrap(sdkTransport())},
		h2:      rawClient(m, true),
		h1:      rawClient(m, false),
		ids:     map[string][]string{},
		bodies:  map[int][]json.RawMessage{},
	}
	b.auth = tsauth.APIKeyConfig{Issuer: spec.AuthIssuer, Key: key, Project: project, HTTPClient: b.hc}
	if b.ts, err = tsauth.NewAPIKeySource(b.auth); err != nil {
		return err
	}

	t0 := time.Now()
	if err := b.refresh(ctx); err != nil {
		return err
	}
	authMS := time.Since(t0).Seconds() * 1000

	t1 := time.Now()
	if err := b.setup(ctx); err != nil {
		return errors.Join(err, b.finish())
	}
	ready := apibench.Ready{
		Client:     "go",
		SDK:        "ts-go",
		SDKVersion: sdkVersion(),
		Runtime:    runtime.Version(),
		Database:   b.dbName,
		SetupMS:    time.Since(t1).Seconds() * 1000,
		AuthMS:     authMS,
	}
	err = apibench.Serve(os.Stdin, os.Stdout, ready, func(s apibench.Step) apibench.Result { return b.step(ctx, s) })
	return errors.Join(err, b.finish())
}

// loadCredentials reads {api_key, project}. It refuses a file that group or
// others can read and never includes the file's contents in an error.
func loadCredentials(path string) (key, project string, err error) {
	fi, err := os.Stat(path)
	if err != nil {
		return "", "", err
	}
	if fi.Mode().Perm()&0o077 != 0 {
		return "", "", fmt.Errorf("%s must not be readable by group or others (chmod 0600)", path)
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", "", err
	}
	var c struct {
		APIKey  string `json:"api_key"`
		Project string `json:"project"`
	}
	if err := json.Unmarshal(b, &c); err != nil {
		return "", "", fmt.Errorf("%s: invalid JSON", path)
	}
	return c.APIKey, c.Project, nil
}

// sdkVersion is the terrascale.tech/sdk version, or the replacement's when the
// module is replaced.
func sdkVersion() string {
	bi, ok := debug.ReadBuildInfo()
	if !ok {
		return "unknown"
	}
	for _, d := range bi.Deps {
		if d.Path == "terrascale.tech/sdk" {
			if d.Replace != nil {
				return d.Replace.Version
			}
			return d.Version
		}
	}
	return "unknown"
}

// refresh takes the source's current token. The SDK client is rebuilt only when
// the token changed, so the pooled connections stay in use.
func (b *bench) refresh(ctx context.Context) error {
	tok, err := b.ts.Token(ctx)
	if err != nil || tok == b.token {
		return err
	}
	c, err := sdkClient(b.spec.Endpoint, tok, b.hc)
	if err != nil {
		return err
	}
	b.token, b.db = tok, c.Database(b.project, b.dbName)
	return nil
}

// step refreshes the token, seeds the step's payload size if needed, then runs
// the step. A failed refresh keeps the cached token, so the step's errors show
// up as status keys. A failed seed answers the step as stopped, before measuring.
func (b *bench) step(ctx context.Context, s apibench.Step) apibench.Result {
	if err := b.refresh(ctx); err != nil {
		fmt.Fprintln(os.Stderr, "token refresh failed, keeping the cached token:", err)
	}
	if err := b.ensure(ctx, seedSize(s)); err != nil {
		fmt.Fprintln(os.Stderr, "seeding failed:", b.redact(err.Error()))
		return apibench.Result{
			Type:         "result",
			Scenario:     s.Scenario,
			Transport:    s.Transport,
			PayloadBytes: s.PayloadBytes,
			Mode:         s.Mode,
			Concurrency:  s.Concurrency,
			Rate:         s.Rate,
			Status:       map[string]int64{"setup": 1},
			Stopped:      "setup",
		}
	}
	return apibench.RunStep(ctx, s, b.op(s), b.meter)
}

// seedSize is the payload size whose seed_ documents a step reads. query.small
// always reads seed_256.
func seedSize(s apibench.Step) int {
	if s.Scenario == "query.small" {
		return 256
	}
	return s.PayloadBytes
}

// setup creates the database. Collections and seeds come later, in ensure.
func (b *bench) setup(ctx context.Context) error {
	_, err := b.db.Create(ctx)
	return err
}

// ensure creates the collections of payload size p and seeds seed_p and upd_p,
// once per size, before the first step that uses it. Size 0 needs nothing.
func (b *bench) ensure(ctx context.Context, p int) error {
	if _, ok := b.bodies[p]; ok || p == 0 {
		return nil
	}
	t0 := time.Now()
	bodies, err := payloads(p)
	if err != nil {
		return err
	}
	for _, pre := range []string{"seed", "upd", "ins", "txn"} {
		if _, err := b.db.CreateCollection(ctx, fmt.Sprintf("%s_%d", pre, p)); err != nil {
			return err
		}
	}
	var docs []seedDoc
	for _, pre := range []string{"seed", "upd"} {
		col := fmt.Sprintf("%s_%d", pre, p)
		ids := make([]string, b.spec.SeedDocuments)
		for i := range ids {
			ids[i] = v1.NewID()
			docs = append(docs, seedDoc{col, ids[i], bodies[i%16]})
		}
		b.ids[col] = ids
	}
	if err := b.seed(ctx, docs); err != nil {
		return err
	}
	b.bodies[p] = bodies
	fmt.Fprintf(os.Stderr, "seeded %d in %.0f ms\n", p, time.Since(t0).Seconds()*1000)
	return nil
}

type seedDoc struct {
	col, id string
	body    json.RawMessage
}

// seed writes the documents with 16 workers and returns the first error.
func (b *bench) seed(ctx context.Context, docs []seedDoc) error {
	work := make(chan seedDoc)
	var (
		wg    sync.WaitGroup
		once  sync.Once
		first error
	)
	for range 16 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for d := range work {
				if _, err := b.db.Collection(d.col).Put(ctx, d.id, d.body); err != nil {
					once.Do(func() { first = err })
				}
			}
		}()
	}
	for _, d := range docs {
		work <- d
	}
	close(work)
	wg.Wait()
	return first
}

// finish deletes the database and prints the done line.
func (b *bench) finish() error {
	return json.NewEncoder(os.Stdout).Encode(b.cleanup())
}

// cleanup deletes the database on a fresh 30 s context.
func (b *bench) cleanup() apibench.Done {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	err := b.refresh(ctx)
	if err == nil {
		err = b.db.Delete(ctx)
	}
	d := apibench.Done{Type: "done", Cleanup: err == nil}
	if err != nil {
		d.CleanupError = b.redact(err.Error())
	}
	return d
}

// redact keeps the API key and token out of cleanup_error.
func (b *bench) redact(msg string) string {
	for _, secret := range []string{b.key, b.token} {
		if secret != "" {
			msg = strings.ReplaceAll(msg, secret, "<redacted>")
		}
	}
	return msg
}
