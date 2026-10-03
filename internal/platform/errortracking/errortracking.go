// Package errortracking reports panics, server errors and error logs to a
// Sentry-compatible service (GlitchTip) through the official Sentry SDK.
package errortracking

import (
	"context"
	"fmt"
	"log/slog"
	"net/http"
	"time"

	"github.com/getsentry/sentry-go"
	sentryhttp "github.com/getsentry/sentry-go/http"
)

// Options configures error tracking; an empty DSN disables it.
type Options struct {
	DSN         string
	Release     string
	Environment string
	// Transport overrides how events are delivered; tests capture them.
	Transport sentry.Transport
}

// flushTimeout bounds how long shutdown waits for queued events.
const flushTimeout = 2 * time.Second

// Init starts the Sentry client and returns a function that delivers queued
// events, which callers run before exiting.
func Init(opts Options) (func(), error) {
	if opts.DSN == "" {
		return func() {}, nil
	}
	err := sentry.Init(sentry.ClientOptions{
		Dsn:              opts.DSN,
		Release:          opts.Release,
		Environment:      opts.Environment,
		AttachStacktrace: true,
		Transport:        opts.Transport,
		// Never collect bodies (login forms carry passwords), cookies
		// (sessions), the signed-in user or query strings: the CLI approval
		// URL carries its user_code there, also nested inside returnTo. For
		// the same reason Referer is not on the header allowlist; only
		// headers that describe the client and request shape keep values.
		DataCollection: &sentry.DataCollection{
			UserInfo:   sentry.Set(false),
			Cookies:    &sentry.KeyValueCollectionBehavior{Mode: sentry.CollectionOff},
			HTTPBodies: []sentry.BodyType{},
			HTTPHeaders: &sentry.HeaderCollectionConfig{
				Request: &sentry.KeyValueCollectionBehavior{Mode: sentry.CollectionAllowList, Terms: []string{
					"accept", "content-type", "content-length", "datastar-request", "user-agent",
				}},
				Response: &sentry.KeyValueCollectionBehavior{Mode: sentry.CollectionAllowList, Terms: []string{"content-type"}},
			},
			QueryParams: &sentry.KeyValueCollectionBehavior{Mode: sentry.CollectionOff},
		},
		BeforeSend: func(event *sentry.Event, hint *sentry.EventHint) *sentry.Event {
			// net/http's sentinel for deliberately aborted responses is not
			// a failure.
			if hint != nil && hint.RecoveredException == http.ErrAbortHandler {
				return nil
			}
			return event
		},
	})
	if err != nil {
		return func() {}, fmt.Errorf("error tracking: %w", err)
	}
	return func() { sentry.Flush(flushTimeout) }, nil
}

// Middleware gives every request its own hub carrying the request, reports
// panics before re-raising them to net/http, and reports 5xx responses.
func Middleware(next http.Handler) http.Handler {
	return sentryhttp.New(sentryhttp.Options{Repanic: true}).Handle(reportServerErrors(next))
}

func reportServerErrors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		recorder := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(recorder, r)
		if recorder.status < http.StatusInternalServerError {
			return
		}
		hub := sentry.GetHubFromContext(r.Context())
		if hub == nil {
			return
		}
		// r.Pattern is the matched route, so one failing route groups into
		// a single issue whatever IDs its path carries.
		route := r.Pattern
		if route == "" {
			route = r.Method + " " + r.URL.Path
		}
		hub.WithScope(func(scope *sentry.Scope) {
			scope.SetLevel(sentry.LevelError)
			scope.SetTag("http.status_code", fmt.Sprint(recorder.status))
			scope.SetFingerprint([]string{"http-server-error", route, fmt.Sprint(recorder.status)})
			hub.CaptureMessage(fmt.Sprintf("%d %s %s", recorder.status, http.StatusText(recorder.status), route))
		})
	})
}

// statusRecorder notes the response status. Flush and Unwrap keep Datastar
// and MCP streams flushing through it.
type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (r *statusRecorder) WriteHeader(status int) {
	if r.status == 0 {
		r.status = status
	}
	r.ResponseWriter.WriteHeader(status)
}

func (r *statusRecorder) Write(b []byte) (int, error) {
	if r.status == 0 {
		r.status = http.StatusOK
	}
	return r.ResponseWriter.Write(b)
}

func (r *statusRecorder) Flush() {
	_ = http.NewResponseController(r.ResponseWriter).Flush()
}

func (r *statusRecorder) Unwrap() http.ResponseWriter { return r.ResponseWriter }

// LogHandler passes every record to next and also reports records at error
// level or above as events, with the first error attribute as the exception.
func LogHandler(next slog.Handler) slog.Handler {
	return &logHandler{next: next}
}

type logHandler struct {
	next   slog.Handler
	attrs  []slog.Attr
	prefix string
}

func (h *logHandler) Enabled(ctx context.Context, level slog.Level) bool {
	return level >= slog.LevelError || h.next.Enabled(ctx, level)
}

func (h *logHandler) Handle(ctx context.Context, record slog.Record) error {
	var err error
	if h.next.Enabled(ctx, record.Level) {
		err = h.next.Handle(ctx, record)
	}
	if record.Level < slog.LevelError {
		return err
	}
	hub := sentry.GetHubFromContext(ctx)
	if hub == nil {
		hub = sentry.CurrentHub()
	}
	if hub.Client() == nil {
		return err
	}
	event := sentry.NewEvent()
	event.Level = sentry.LevelError
	event.Message = record.Message
	event.Logger = "slog"
	extra := map[string]any{}
	var cause error
	collect := func(key string, value slog.Value) {
		if e, ok := value.Any().(error); ok && cause == nil {
			cause = e
		}
		extra[key] = value.String()
	}
	for _, attr := range h.attrs {
		flatten("", attr, collect)
	}
	record.Attrs(func(attr slog.Attr) bool {
		flatten(h.prefix, attr, collect)
		return true
	})
	event.Contexts["log"] = extra
	if cause != nil {
		event.SetException(cause, -1)
		// Plain errors carry no stack; point at the code that logged them.
		last := &event.Exception[len(event.Exception)-1]
		if last.Stacktrace == nil {
			last.Stacktrace = sentry.NewStacktrace()
		}
		event.Fingerprint = []string{"slog", record.Message}
	}
	hub.CaptureEvent(event)
	return err
}

func (h *logHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	scoped := make([]slog.Attr, 0, len(h.attrs)+len(attrs))
	scoped = append(scoped, h.attrs...)
	for _, attr := range attrs {
		if h.prefix != "" {
			attr.Key = h.prefix + attr.Key
		}
		scoped = append(scoped, attr)
	}
	return &logHandler{next: h.next.WithAttrs(attrs), attrs: scoped, prefix: h.prefix}
}

func (h *logHandler) WithGroup(name string) slog.Handler {
	if name == "" {
		return h
	}
	return &logHandler{next: h.next.WithGroup(name), attrs: h.attrs, prefix: h.prefix + name + "."}
}

// flatten reports attr's leaves under dotted keys, as slog's text handler
// prints groups.
func flatten(prefix string, attr slog.Attr, visit func(string, slog.Value)) {
	value := attr.Value.Resolve()
	if value.Kind() == slog.KindGroup {
		if attr.Key != "" {
			prefix += attr.Key + "."
		}
		for _, child := range value.Group() {
			flatten(prefix, child, visit)
		}
		return
	}
	if attr.Key == "" {
		return
	}
	visit(prefix+attr.Key, value)
}
