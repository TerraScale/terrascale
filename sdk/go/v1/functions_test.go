package v1_test

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestFunctionContracts(t *testing.T) {
	const fn = `{"name":"hello","source":"x","trigger":{"collection":"items","events":["upsert"]},"cron":"* * * * *",` +
		`"createdAt":"2026-10-24T12:00:00Z","updatedAt":"2026-10-24T12:00:01Z"}`
	const listed = `[{"name":"hello","source":null,"trigger":null,"cron":null,"createdAt":"2026-10-24T12:00:00Z","updatedAt":"2026-10-24T12:00:00Z"}]`
	const logs = `[{"id":"` + parityID + `","at":"2026-10-24T12:00:00Z","trigger":"cron","status":"error","durationMs":12,"lines":["hi"],"error":"boom"}]`
	const deliveries = `[{"id":"` + parityID + `","createdAt":"2026-10-24T12:00:00Z","status":"delivered","attempts":1,` +
		`"lastAttemptAt":null,"nextAttemptAt":null,"httpStatus":200,"errorCode":null}]`
	for _, c := range []struct{ name, method, path, query, body, reply string }{
		{"deploy", http.MethodPut, "/hello", "", `{"source":"x","trigger":{"collection":"items","events":["upsert"]},"cron":"* * * * *"}`, fn},
		{"deploy plain", http.MethodPut, "/hello", "", `{"source":"x","trigger":null,"cron":null}`, fn},
		{"list", http.MethodGet, "", "", "", listed},
		{"get", http.MethodGet, "/hello", "", "", fn},
		{"delete", http.MethodDelete, "/hello", "", "", ""},
		{"invoke", http.MethodPost, "/hello/invoke", "", `{"n":1}`, `{"ok":true}`},
		{"invoke error", http.MethodPost, "/hello/invoke", "", "", `{"reason":"no"}`},
		{"logs", http.MethodGet, "/hello/logs", "limit=5", "", logs},
		{"deliveries", http.MethodGet, "/hello/deliveries", "", "", deliveries},
	} {
		t.Run(c.name, func(t *testing.T) {
			status := map[string]int{"delete": http.StatusNoContent, "invoke error": http.StatusTeapot}[c.name]
			d := parityClient(t, c.method, parityDatabase+"/functions"+c.path, c.query, c.body, c.reply, max(status, http.StatusOK)).Database(parityProject, "main")
			ok := false
			var err error
			switch c.name {
			case "deploy":
				var f *tb.Function
				f, err = d.DeployFunction(t.Context(), "hello", tb.FunctionRequest{Source: "x",
					Trigger: &tb.FunctionTrigger{Collection: "items", Events: []string{"upsert"}}, Cron: "* * * * *"})
				ok = err == nil && f.Trigger.Collection == "items" && *f.Cron == "* * * * *" && !f.UpdatedAt.IsZero()
			case "deploy plain":
				_, err = d.DeployFunction(t.Context(), "hello", tb.FunctionRequest{Source: "x"})
				ok = err == nil
			case "list":
				var out []tb.Function
				out, err = d.Functions(t.Context())
				ok = err == nil && len(out) == 1 && out[0].Source == "" && out[0].Trigger == nil && out[0].Cron == nil
			case "get":
				var f *tb.Function
				f, err = d.Function(t.Context(), "hello")
				ok = err == nil && f.Source == "x"
			case "delete":
				err = d.DeleteFunction(t.Context(), "hello")
				ok = err == nil
			case "invoke":
				var out []byte
				out, err = d.InvokeFunction(t.Context(), "hello", []byte(`{"n":1}`))
				ok = err == nil && string(out) == `{"ok":true}`
			case "invoke error":
				_, err = d.InvokeFunction(t.Context(), "hello", nil)
				var api *tb.APIError
				ok = errors.As(err, &api) && api.Status == http.StatusTeapot && string(api.Body) == c.reply
			case "logs":
				var out []tb.FunctionLog
				out, err = d.FunctionLogs(t.Context(), "hello", 5)
				ok = err == nil && len(out) == 1 && out[0].DurationMs == 12 && out[0].Lines[0] == "hi" && *out[0].Error == "boom"
			case "deliveries":
				var out []tb.WebhookDelivery
				out, err = d.FunctionDeliveries(t.Context(), "hello")
				ok = err == nil && len(out) == 1 && *out[0].HTTPStatus == 200
			}
			if !ok {
				t.Fatalf("%s did not preserve the contract: %v", c.name, err)
			}
		})
	}
}

func TestFunctionValidationBeforeHTTP(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { t.Error("unexpected HTTP request") }))
	defer server.Close()
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	d := c.Database(parityProject, "main")
	for _, name := range []string{"", "Hello", "../x", "a/b"} {
		if _, err := d.InvokeFunction(t.Context(), name, nil); err == nil || d.DeleteFunction(t.Context(), name) == nil {
			t.Errorf("accepted function name %q", name)
		}
	}
	trigger := &tb.FunctionTrigger{Collection: "../x"}
	if _, err := d.DeployFunction(t.Context(), "hello", tb.FunctionRequest{Source: "x", Trigger: trigger}); err == nil {
		t.Error("deploy accepted an invalid trigger collection")
	}
	if _, err := d.FunctionLogs(t.Context(), "hello", 101); err == nil {
		t.Error("logs accepted limit 101")
	}
}
