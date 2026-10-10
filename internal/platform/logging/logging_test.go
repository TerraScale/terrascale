package logging

import (
	"bytes"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/platform/tracing"
)

func decode(t *testing.T, line string) map[string]any {
	t.Helper()
	var record map[string]any
	if err := json.Unmarshal([]byte(line), &record); err != nil {
		t.Fatalf("record is not JSON: %v\n%s", err, line)
	}
	return record
}

func TestHandlerWritesRecordV1Keys(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	logger := slog.New(Handler(&out)).With(Fields("gru", "machine-1")...)
	logger.Warn("resolve session", "error", errors.New("store unavailable"), "path", "/databases")

	record := decode(t, strings.TrimSpace(out.String()))
	want := map[string]string{
		"level": "WARN", "msg": "resolve session", "service": "ts-dashboard",
		"region": "gru", "node": "machine-1", "error": "store unavailable", "path": "/databases",
	}
	for key, value := range want {
		if record[key] != value {
			t.Fatalf("%s = %v, want %q in %v", key, record[key], value, record)
		}
	}
	if time, _ := record["time"].(string); !strings.HasSuffix(time, "Z") {
		t.Fatalf("time = %q, want RFC 3339 UTC", time)
	}
	if _, ok := record["trace_id"]; ok {
		t.Fatalf("record without a request carries trace_id: %v", record)
	}
}

func TestHandlerOmitsEmptyFlyFields(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	slog.New(Handler(&out)).With(Fields("", "")...).Info("dashboard listening")

	record := decode(t, strings.TrimSpace(out.String()))
	if _, ok := record["region"]; ok {
		t.Fatalf("region set without FLY_REGION: %v", record)
	}
	if _, ok := record["node"]; ok {
		t.Fatalf("node set without FLY_MACHINE_ID: %v", record)
	}
}

func TestRecordLoggedWithRequestContextCarriesTraceID(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	logger := slog.New(Handler(&out))
	handler := tracing.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		logger.WarnContext(r.Context(), "resolve session", "error", errors.New("store unavailable"))
		logger.Warn("background pass")
	}))
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	request.Header.Set("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")
	handler.ServeHTTP(httptest.NewRecorder(), request)

	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 2 {
		t.Fatalf("got %d records, want 2:\n%s", len(lines), out.String())
	}
	if traced := decode(t, lines[0]); traced["trace_id"] != "4bf92f3577b34da6a3ce929d0e0e4736" {
		t.Fatalf("trace_id = %v, want the request's trace: %v", traced["trace_id"], traced)
	}
	if plain := decode(t, lines[1]); plain["trace_id"] != nil {
		t.Fatalf("record logged without the request context carries trace_id: %v", plain)
	}
}
