package querysubscriptions

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func renderContent(t *testing.T, d pageData) string {
	t.Helper()
	d.Databases = []databaseChoice{{ID: "db_1", Name: "primary"}}
	d.Configs = []core.SharedResultConfig{{ID: "cfg_1", DatabaseID: "db_1", Name: "orders_view", Query: "SELECT * FROM orders", Transport: "http", Status: "paused"}}
	var out bytes.Buffer
	if err := subscriptionsContent(core.Page{CSRFToken: "token"}, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	return out.String()
}

func TestReadersSeeConfigurationsWithoutControls(t *testing.T) {
	reader := renderContent(t, pageData{})
	for _, control := range []string{"/api/v1/dashboard/subscriptions/attach", "Resume orders_view", "Detach orders_view"} {
		if strings.Contains(reader, control) {
			t.Errorf("a reader is offered %q, which the server refuses", control)
		}
	}
	if !strings.Contains(reader, "orders_view") {
		t.Error("a reader still sees the saved configurations")
	}

	writer := renderContent(t, pageData{CanWrite: true})
	for _, control := range []string{"/api/v1/dashboard/subscriptions/attach", "Resume orders_view", `commandfor="detach-cfg_1"`, `<dialog id="detach-cfg_1"`} {
		if !strings.Contains(writer, control) {
			t.Errorf("a writer is not offered %q", control)
		}
	}
	if strings.Contains(writer, "Pause orders_view") {
		t.Error("a paused configuration offers Resume, not Pause")
	}
}

func TestRefusedConfigurationKeepsTheForm(t *testing.T) {
	page := renderContent(t, pageData{CanWrite: true, FormError: "Choose HTTP + CDN or WebSocket delivery.", Form: configForm{DatabaseID: "db_1", Name: "draft", Query: "SELECT * FROM orders LIMIT 5", Transport: "websocket"}})
	for _, want := range []string{"Choose HTTP + CDN or WebSocket delivery.", `value="draft"`, "SELECT * FROM orders LIMIT 5", `<option value="websocket" selected`, `<option value="db_1" selected`} {
		if !strings.Contains(page, want) {
			t.Errorf("the refused form lacks %q", want)
		}
	}
}
