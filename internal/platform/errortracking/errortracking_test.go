package errortracking

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/getsentry/sentry-go"
)

type capture struct {
	mu     sync.Mutex
	events []*sentry.Event
}

func (c *capture) Configure(sentry.ClientOptions)        {}
func (c *capture) Flush(time.Duration) bool              { return true }
func (c *capture) FlushWithContext(context.Context) bool { return true }
func (c *capture) Close()                                {}
func (c *capture) SendEvent(event *sentry.Event) {
	c.mu.Lock()
	c.events = append(c.events, event)
	c.mu.Unlock()
}
func (c *capture) all() []*sentry.Event {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]*sentry.Event(nil), c.events...)
}

// start points the global Sentry hub at a capturing transport. Tests sharing
// the global hub must not run in parallel.
func start(t *testing.T) *capture {
	t.Helper()
	transport := &capture{}
	flush, err := Init(Options{DSN: "https://key@glitchtip.example/1", Release: "test", Environment: "test", Transport: transport})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		flush()
		sentry.CurrentHub().BindClient(nil)
	})
	return transport
}

func TestInitWithoutDSNIsDisabled(t *testing.T) {
	flush, err := Init(Options{})
	if err != nil || flush == nil {
		t.Fatalf("flush missing=%v error=%v", flush == nil, err)
	}
	flush()
}

func TestMiddlewareReportsServerErrorsWithoutSecrets(t *testing.T) {
	transport := start(t)
	mux := http.NewServeMux()
	mux.HandleFunc("POST /projects/{id}", func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "could not save project", http.StatusInternalServerError)
	})
	mux.HandleFunc("GET /ok", func(w http.ResponseWriter, r *http.Request) { w.Write([]byte("ok")) })
	handler := Middleware(mux)

	req := httptest.NewRequest(http.MethodPost, "/projects/6T3ANS9YMBPBSRGZE2158QZ7VM?user_code=ABCD-EFGH&returnTo=%2Fcli%2Fapprove%3Fuser_code%3DWXYZ-1234", strings.NewReader("password=hunter2"))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Cookie", "session=secret-session")
	req.Header.Set("Authorization", "Bearer secret-token")
	req.Header.Set("Fly-Client-IP", "203.0.113.9")
	req.Header.Set("User-Agent", "test-agent")
	req.Header.Set("Referer", "https://dashboard.example/cli/approve?user_code=REFR-CODE")
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	handler.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/ok", nil))

	events := transport.all()
	if rec.Code != http.StatusInternalServerError || len(events) != 1 {
		t.Fatalf("status=%d events=%d", rec.Code, len(events))
	}
	event := events[0]
	if event.Message != "500 Internal Server Error POST /projects/{id}" || event.Release != "test" || event.Environment != "test" {
		t.Fatalf("event message=%q release=%q environment=%q", event.Message, event.Release, event.Environment)
	}
	if event.Request == nil || event.Request.Headers["User-Agent"] != "test-agent" {
		t.Fatalf("request context missing: %+v", event.Request)
	}
	exported := event.Request.URL + event.Request.QueryString + event.Request.Data + event.Request.Cookies + strings.Join(values(event.Request.Headers), " ")
	for _, leaked := range []string{"hunter2", "secret-session", "secret-token", "203.0.113.9", "ABCD-EFGH", "WXYZ-1234", "REFR-CODE", "user_code"} {
		if strings.Contains(exported, leaked) {
			t.Fatalf("event leaked %q: %+v", leaked, event.Request)
		}
	}
}

func values(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for _, v := range m {
		out = append(out, v)
	}
	return out
}

func TestMiddlewareReportsPanicsAndRepanics(t *testing.T) {
	transport := start(t)
	handler := Middleware(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { panic("boom") }))
	func() {
		defer func() {
			if recover() == nil {
				t.Fatal("panic was swallowed; net/http must still abort the response")
			}
		}()
		handler.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/", nil))
	}()
	if events := transport.all(); len(events) != 1 || events[0].Message != "boom" || events[0].Level != sentry.LevelFatal {
		t.Fatalf("events=%+v", events)
	}
}

func TestMiddlewareIgnoresAbortedHandlers(t *testing.T) {
	transport := start(t)
	handler := Middleware(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { panic(http.ErrAbortHandler) }))
	func() {
		defer func() { recover() }()
		handler.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/", nil))
	}()
	if events := transport.all(); len(events) != 0 {
		t.Fatalf("aborted handler reported: %+v", events)
	}
}

func TestMiddlewareKeepsStreamsFlushing(t *testing.T) {
	start(t)
	var flushed bool
	handler := Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("event: x\n\n"))
		flushed = http.NewResponseController(w).Flush() == nil
	}))
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/", nil))
	if !flushed || !rec.Flushed {
		t.Fatalf("flushed=%v recorder=%v", flushed, rec.Flushed)
	}
}

func TestLogHandlerReportsErrors(t *testing.T) {
	transport := start(t)
	var out bytes.Buffer
	logger := slog.New(LogHandler(slog.NewTextHandler(&out, nil))).With("component", "stripe").WithGroup("req")
	logger.Info("listening", "addr", ":8080")
	logger.Error("bind Stripe customer", "organization_id", "1MZ7GT1142NZMAQXJE73TYTFZW", "error", errors.New("stripe unavailable"))

	if !strings.Contains(out.String(), "listening") || !strings.Contains(out.String(), "stripe unavailable") {
		t.Fatalf("records did not reach the wrapped handler: %s", out.String())
	}
	events := transport.all()
	if len(events) != 1 {
		t.Fatalf("events=%d", len(events))
	}
	event := events[0]
	if event.Message != "bind Stripe customer" || len(event.Exception) == 0 || event.Exception[len(event.Exception)-1].Value != "stripe unavailable" {
		t.Fatalf("event message=%q exception=%+v", event.Message, event.Exception)
	}
	log := event.Contexts["log"]
	if log["component"] != "stripe" || log["req.organization_id"] != "1MZ7GT1142NZMAQXJE73TYTFZW" {
		t.Fatalf("log context=%v", log)
	}
}
