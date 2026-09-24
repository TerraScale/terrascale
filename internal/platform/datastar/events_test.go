package datastar

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestOfficialSSEWireEvents(t *testing.T) {
	events := Events{}
	request := httptest.NewRequest(http.MethodPost, "http://dashboard.test/events", nil)
	elements := httptest.NewRecorder()
	if err := events.PatchElements(elements, request, `<div id="message">Ready</div>`); err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(elements.Header().Get("Content-Type"), "text/event-stream") ||
		!strings.Contains(elements.Body.String(), "event: datastar-patch-elements") ||
		!strings.Contains(elements.Body.String(), `<div id="message">Ready</div>`) {
		t.Fatalf("unexpected elements SSE: %q", elements.Body.String())
	}
	signals := httptest.NewRecorder()
	if err := events.PatchSignals(signals, request, map[string]any{"ready": true}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(signals.Body.String(), "event: datastar-patch-signals") ||
		!strings.Contains(signals.Body.String(), `"ready":true`) {
		t.Fatalf("unexpected signals SSE: %q", signals.Body.String())
	}
}
